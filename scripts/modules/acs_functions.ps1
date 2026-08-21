<#
.SYNOPSIS
    Reusable PowerShell functions for ACS Teams Phone Extensibility (TPE).

.DESCRIPTION
    Provides helper functions for managing TPE assignments on Azure
    Communication Services resources via the Teams Extension API.

    Import with: . (Join-Path $PSScriptRoot 'modules/acs_functions.ps1')
#>

$script:DefaultTpeApiVersion = '2025-06-30'

function Get-AcsTpeEndpoint {
    <#
    .SYNOPSIS
        Returns the base ACS endpoint URL for a given resource name.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $AcsName
    )
    return "https://$AcsName.communication.azure.com"
}

function Get-AcsTpeAssignmentUrl {
    <#
    .SYNOPSIS
        Builds the full TPE assignment API URL for a specific resource account.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $AcsName,
        [Parameter(Mandatory)] [string] $TeamsTenantId,
        [Parameter(Mandatory)] [string] $ResourceAccountObjectId,
        [string] $ApiVersion = $script:DefaultTpeApiVersion
    )
    $endpoint = Get-AcsTpeEndpoint -AcsName $AcsName
    return "$endpoint/access/teamsExtension/tenants/$TeamsTenantId/assignments/${ResourceAccountObjectId}?api-version=$ApiVersion"
}

function Test-AcsTpeAssignment {
    <#
    .SYNOPSIS
        Checks whether a TPE assignment exists for the given resource account.

    .DESCRIPTION
        Issues a GET against the TPE assignments API. Returns $true if the
        assignment exists (HTTP 200), $false on 404, and throws on auth errors.

    .OUTPUTS
        [bool] $true if the assignment exists, $false otherwise.

    .EXAMPLE
        if (Test-AcsTpeAssignment -AcsName 'woodgrove-ai' -TeamsTenantId $tid -ResourceAccountObjectId $raOid) {
            Write-Host "Assignment exists"
        }
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)] [string] $AcsName,
        [Parameter(Mandatory)] [string] $TeamsTenantId,
        [Parameter(Mandatory)] [string] $ResourceAccountObjectId,
        [string] $ApiVersion = $script:DefaultTpeApiVersion
    )

    $url = Get-AcsTpeAssignmentUrl -AcsName $AcsName -TeamsTenantId $TeamsTenantId `
        -ResourceAccountObjectId $ResourceAccountObjectId -ApiVersion $ApiVersion

    Write-Verbose "GET $url"
    $output = az rest --method GET --url $url --resource "https://communication.azure.com" --only-show-errors 2>&1
    $code = $LASTEXITCODE

    if ($code -eq 0) { return $true }

    $outputStr = $output -join "`n"
    if ($outputStr -match '404|NotFound') { return $false }

    if ($outputStr -match '401|403|Forbidden|Unauthorized') {
        throw "TPE assignment check failed with auth error on ACS '$AcsName': $outputStr"
    }

    Write-Warning "Unexpected response (exit $code) checking TPE assignment: $outputStr"
    return $false
}

function Get-AcsTpeAssignment {
    <#
    .SYNOPSIS
        Retrieves the TPE assignment details for a resource account.

    .DESCRIPTION
        Returns the parsed JSON object from the TPE assignments API, or $null
        if the assignment does not exist (404).

    .OUTPUTS
        [PSCustomObject] The assignment details, or $null.

    .EXAMPLE
        $assignment = Get-AcsTpeAssignment -AcsName 'woodgrove-ai' -TeamsTenantId $tid -ResourceAccountObjectId $raOid
        if ($assignment) { $assignment | ConvertTo-Json }
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $AcsName,
        [Parameter(Mandatory)] [string] $TeamsTenantId,
        [Parameter(Mandatory)] [string] $ResourceAccountObjectId,
        [string] $ApiVersion = $script:DefaultTpeApiVersion
    )

    $url = Get-AcsTpeAssignmentUrl -AcsName $AcsName -TeamsTenantId $TeamsTenantId `
        -ResourceAccountObjectId $ResourceAccountObjectId -ApiVersion $ApiVersion

    Write-Verbose "GET $url"
    $output = az rest --method GET --url $url --resource "https://communication.azure.com" --only-show-errors 2>&1
    $code = $LASTEXITCODE

    if ($code -eq 0) {
        return ($output -join "`n" | ConvertFrom-Json)
    }

    $outputStr = $output -join "`n"
    if ($outputStr -match '404|NotFound') { return $null }

    if ($outputStr -match '401|403|Forbidden|Unauthorized') {
        throw "Failed to retrieve TPE assignment on ACS '$AcsName': $outputStr"
    }

    throw "Unexpected error (exit $code) retrieving TPE assignment: $outputStr"
}

function Set-AcsTpeAssignment {
    <#
    .SYNOPSIS
        Creates or updates a TPE assignment for a resource account.

    .DESCRIPTION
        Issues a PUT (upsert) against the TPE assignments API. The request body
        is written to a temp file to avoid Windows shell escaping issues with
        az rest. Returns the parsed response on success.

    .OUTPUTS
        [PSCustomObject] The API response, or $null in WhatIf mode.

    .EXAMPLE
        Set-AcsTpeAssignment -AcsName 'woodgrove-ai' -TeamsTenantId $tid `
            -ResourceAccountObjectId $raOid -PrincipalType 'teamsResourceAccount'
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)] [string] $AcsName,
        [Parameter(Mandatory)] [string] $TeamsTenantId,
        [Parameter(Mandatory)] [string] $ResourceAccountObjectId,
        [ValidateSet('teamsResourceAccount', 'user')]
        [string] $PrincipalType = 'teamsResourceAccount',
        [string[]] $ClientIds,
        [string] $ApiVersion = $script:DefaultTpeApiVersion
    )

    $url = Get-AcsTpeAssignmentUrl -AcsName $AcsName -TeamsTenantId $TeamsTenantId `
        -ResourceAccountObjectId $ResourceAccountObjectId -ApiVersion $ApiVersion

    $payload = [ordered]@{
        request = [ordered]@{
            principalType = $PrincipalType
            clientIds     = @($ClientIds)
        }
    }
    $bodyJson = $payload | ConvertTo-Json -Depth 5 -Compress

    if (-not $PSCmdlet.ShouldProcess("TPE assignment for $ResourceAccountObjectId on $AcsName", "PUT")) {
        return $null
    }

    $bodyFile = New-TemporaryFile
    try {
        Set-Content -Path $bodyFile -Value $bodyJson -Encoding utf8 -NoNewline

        Write-Verbose "PUT $url"
        Write-Verbose "Body: $bodyJson"

        $output = az rest `
            --method PUT `
            --url $url `
            --resource "https://communication.azure.com" `
            --headers "Content-Type=application/json" `
            --body "@$($bodyFile.FullName)" `
            --only-show-errors 2>&1
        $code = $LASTEXITCODE

        if ($code -ne 0) {
            throw "TPE assignment PUT failed (exit $code) on ACS '$AcsName':`n$($output -join "`n")`n`nURL: $url`nBody: $bodyJson"
        }

        $outputStr = ($output -join "`n").Trim()
        if ($outputStr) {
            return ($outputStr | ConvertFrom-Json)
        }
        return $null
    }
    finally {
        Remove-Item $bodyFile -ErrorAction SilentlyContinue
    }
}

function Remove-AcsTpeAssignment {
    <#
    .SYNOPSIS
        Deletes a TPE assignment for a resource account.

    .DESCRIPTION
        Issues a DELETE against the TPE assignments API. Returns $true on success,
        $false if the assignment was not found (404).

    .OUTPUTS
        [bool] $true if deleted, $false if not found.

    .EXAMPLE
        Remove-AcsTpeAssignment -AcsName 'woodgrove-ai' -TeamsTenantId $tid -ResourceAccountObjectId $raOid
    #>
    [CmdletBinding(SupportsShouldProcess)]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)] [string] $AcsName,
        [Parameter(Mandatory)] [string] $TeamsTenantId,
        [Parameter(Mandatory)] [string] $ResourceAccountObjectId,
        [string] $ApiVersion = $script:DefaultTpeApiVersion
    )

    $url = Get-AcsTpeAssignmentUrl -AcsName $AcsName -TeamsTenantId $TeamsTenantId `
        -ResourceAccountObjectId $ResourceAccountObjectId -ApiVersion $ApiVersion

    if (-not $PSCmdlet.ShouldProcess("TPE assignment for $ResourceAccountObjectId on $AcsName", "DELETE")) {
        return $false
    }

    Write-Verbose "DELETE $url"
    $output = az rest --method DELETE --url $url --resource "https://communication.azure.com" --only-show-errors 2>&1
    $code = $LASTEXITCODE

    if ($code -eq 0) { return $true }

    $outputStr = $output -join "`n"
    if ($outputStr -match '404|NotFound') {
        Write-Verbose "TPE assignment not found (already removed)."
        return $false
    }

    throw "TPE assignment DELETE failed (exit $code) on ACS '$AcsName': $outputStr"
}

function Get-AcsResourceId {
    <#
    .SYNOPSIS
        Resolves the ARM resource ID for an ACS resource by name and resource group.

    .OUTPUTS
        [string] The ARM resource ID, or $null if not found.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $AcsName,
        [Parameter(Mandatory)] [string] $ResourceGroupName,
        [Parameter(Mandatory)] [string] $SubscriptionId
    )

    $output = az resource show `
        --resource-type 'Microsoft.Communication/communicationServices' `
        --name $AcsName `
        --resource-group $ResourceGroupName `
        --subscription $SubscriptionId `
        --query 'id' -o tsv `
        --only-show-errors 2>&1
    $code = $LASTEXITCODE

    if ($code -eq 0) { return ($output -join '').Trim() }
    return $null
}

function Test-AcsTpeRbac {
    <#
    .SYNOPSIS
        Best-effort check that the signed-in Azure CLI principal has a role on the ACS resource.

    .DESCRIPTION
        Queries role assignments for the current az CLI user on the specified ACS
        resource ID. Returns $true if at least one role is found or if the check
        cannot be performed (e.g. service principal context). Emits a warning if
        no roles are found.

    .OUTPUTS
        [bool] $true if RBAC looks OK or cannot be determined, $false if no roles found.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)] [string] $AcsResourceId
    )

    $signedIn = az ad signed-in-user show --query 'id' -o tsv --only-show-errors 2>&1
    if ($LASTEXITCODE -ne 0) { return $true }   # service principal — skip

    $signedInId = ($signedIn -join '').Trim()
    $roles = az role assignment list `
        --assignee $signedInId `
        --scope $AcsResourceId `
        --query '[].roleDefinitionName' -o tsv `
        --only-show-errors 2>&1

    if ($LASTEXITCODE -ne 0) { return $true }

    $roleList = @($roles | Where-Object { $_ })
    if ($roleList.Count -eq 0) {
        Write-Warning @"
Signed-in user has no RBAC role on ACS resource $AcsResourceId.
The TPE assignment API requires at least 'Contributor' (or a custom role granting
Microsoft.Communication/communicationServices/teamsExtension/*) on the ACS resource.
"@
        return $false
    }

    Write-Verbose "RBAC OK — signed-in user roles on ACS: $($roleList -join ', ')"
    return $true
}
