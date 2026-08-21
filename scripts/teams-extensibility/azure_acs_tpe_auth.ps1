<#
.SYNOPSIS
    Create or update an ACS TPE assignment that authorizes a Teams resource
    account to route calls into an Azure Communication Services resource.

.DESCRIPTION
    Calls the Microsoft Teams Extension access assignments API
    (PUT /access/teamsExtension/tenants/{tid}/assignments/{oid}) using the
    Entra-auth variant of the API. The body is wrapped in a "request" envelope
    and written to a temp file to avoid Windows shell escaping issues.

    The script is idempotent: PUT acts as an upsert. Existing assignments are
    detected via a pre-flight GET; the PUT is still issued so configuration
    drift in clientIds is reconciled.

    Failure modes are surfaced explicitly: a non-zero exit code from `az rest`
    causes this script to throw with the captured stderr.

.PARAMETER AzureCommunicationServicesName
    DNS name of the ACS resource (e.g. "woodgrove-ai").

.PARAMETER TeamsTenantId
    Tenant ID of the Teams (M365) tenant that owns the resource account.

.PARAMETER TeamsResourceAccountObjectId
    Object ID of the Teams resource account (from New-CsOnlineApplicationInstance).

.PARAMETER PrincipalType
    Either teamsResourceAccount (default) or user.

.PARAMETER ClientIds
    Optional list of Entra App client IDs scoped to this assignment. Empty
    array is allowed and is the typical case.

.PARAMETER ApiVersion
    TPE assignment API version. Default: 2025-06-30.

.PARAMETER WhatIf
    Dry-run mode. Prints the planned URL and body but does not call az rest.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $AzureCommunicationServicesName,
    [Parameter(Mandatory)] [string] $TeamsTenantId,
    [Parameter(Mandatory)] [string] $TeamsResourceAccountObjectId,
    [ValidateSet('teamsResourceAccount','user')]
    [string] $PrincipalType = 'teamsResourceAccount',
    [string[]] $ClientIds,
    [string] $ApiVersion = '2025-06-30',
    [switch] $WhatIf
)

$ErrorActionPreference = 'Stop'

# Resource endpoint (matches AcsConnectionString.Endpoint in AzureCommunicationService.cs)
$endpoint = "https://$AzureCommunicationServicesName.communication.azure.com"
$url      = "$endpoint/access/teamsExtension/tenants/$TeamsTenantId/assignments/${TeamsResourceAccountObjectId}?api-version=$ApiVersion"

# Pre-flight: ensure az is signed in.
$null = az account show 2>$null
if ($LASTEXITCODE -ne 0) {
    throw "Azure CLI is not signed in. Run 'az login --tenant <azureTenantId>' before invoking this script."
}

# Pre-flight GET — surfaces 404 (assignment doesn't exist yet) vs 200 (already exists)
# vs 401/403 (auth/RBAC failure, surfaced before the more destructive PUT).
Write-Verbose "GET $url"
$existing = az rest --method GET --url $url --resource "https://communication.azure.com" --only-show-errors 2>&1
$existingExit = $LASTEXITCODE
if ($existingExit -eq 0) {
    Write-Host "  TPE assignment already exists for $TeamsResourceAccountObjectId. Re-applying to reconcile drift." -ForegroundColor Yellow
}
elseif ($existing -match '404|NotFound') {
    Write-Verbose "No existing TPE assignment found (404). Creating new assignment."
}
elseif ($existing -match '401|403|Forbidden|Unauthorized') {
    throw @"
TPE assignment pre-flight GET failed with auth error:
$existing

Common causes:
  * Caller lacks the required RBAC role on the ACS resource '$AzureCommunicationServicesName'.
    Assign 'Contributor' (or a custom role granting Microsoft.Communication/communicationServices/teamsExtension/*)
    to the signed-in principal on the ACS resource.
  * Signed-in tenant does not match the ACS resource's tenant.
    Sign in with: az login --tenant <azureTenantId>
"@
}
else {
    Write-Verbose "Pre-flight GET returned non-success (exit $existingExit). Continuing to PUT to surface real error: $existing"
}

# Entra-auth variant of the API expects the DTO wrapped in a "request" envelope.
# Inner shape mirrors TeamsExtensionAssignmentCreateOrUpdateRequest
# (principalType + always-present clientIds array).
$payload = [ordered]@{
    request = [ordered]@{
        principalType = $PrincipalType
        clientIds     = @($ClientIds)   # serialize as [] when empty
    }
}
$bodyJson = $payload | ConvertTo-Json -Depth 5 -Compress

if ($WhatIf) {
    Write-Host "[WhatIf] PUT $url" -ForegroundColor DarkGray
    Write-Host "[WhatIf] Body: $bodyJson" -ForegroundColor DarkGray
    return
}

# Use a body file so az.cmd doesn't strip the embedded double quotes on Windows
$bodyFile = New-TemporaryFile
try {
    Set-Content -Path $bodyFile -Value $bodyJson -Encoding utf8 -NoNewline

    Write-Verbose "PUT $url"
    Write-Verbose "Body: $bodyJson"

    $putOutput = az rest `
        --method PUT `
        --url $url `
        --resource "https://communication.azure.com" `
        --headers "Content-Type=application/json" `
        --body "@$($bodyFile.FullName)" `
        --only-show-errors 2>&1
    $putExit = $LASTEXITCODE

    if ($putExit -ne 0) {
        throw @"
TPE assignment PUT failed (exit code $putExit):
$putOutput

URL:  $url
Body: $bodyJson
"@
    }

    if ($putOutput) { Write-Host $putOutput }
    Write-Verbose "TPE assignment upsert succeeded."
}
finally {
    Remove-Item $bodyFile -ErrorAction SilentlyContinue
}

