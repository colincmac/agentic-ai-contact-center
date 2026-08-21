<#
.SYNOPSIS
    Teams-tenant provisioning for Teams Phone Extensibility (TPE).

.DESCRIPTION
    Runs in the TEAMS TENANT context. All phases are idempotent: existing
    resources are detected and reused. Specifically supports the common
    enterprise scenario where the Teams Resource Account, phone number, and
    license already exist and only the ACS binding is missing.

      Phase 1 — Entra App Registration
                * Reuses existing app by display name or supplied client ID.
                * Adds TeamsExtension.ManageCalls permission if missing.
                * Ensures a Service Principal exists.
                * Optionally creates a client secret and stores it in Key Vault.
                * Optionally grants admin consent for the delegated permission.

      Phase 2 — Teams Resource Account
                * Reuses existing RA when the UPN already resolves.
                * Re-binds to the ACS resource (Set-CsOnlineApplicationInstance).
                * Re-syncs the application instance.

      Phase 3 — Licensing & Phone Number
                * Detects existing license/phone assignments and skips them.
                * Validates the phone number exists in tenant inventory.
                * Optionally assigns extra license SKUs (e.g. Calling Plan).

    Outputs a JSON fragment with the IDs required by setup_tpe_azure.ps1.

    Required modules:
      - Microsoft.Entra              >= 1.2.0
      - MicrosoftTeams               >= 7.5.0
      - Microsoft.Graph.Users.Actions
      - Az.KeyVault                  (only when secret + keyVaultName configured)

    Required Graph scopes:
      Application.ReadWrite.All, AppRoleAssignment.ReadWrite.All,
      DelegatedPermissionGrant.ReadWrite.All,
      User.ReadWrite.All, Organization.Read.All

    Required Teams roles:
      Global Admin OR (Cloud Application Administrator + Teams Communications
      Administrator + User Administrator)
#>
[CmdletBinding(DefaultParameterSetName = 'ConfigFile')]
param(
    [Parameter(ParameterSetName = 'ConfigFile', Mandatory)]
    [string] $ConfigFile,

    [Parameter(ParameterSetName = 'Params', Mandatory)] [string] $TeamsTenantId,
    [Parameter(ParameterSetName = 'Params', Mandatory)] [string] $EntraAppRegistrationName,
    [Parameter(ParameterSetName = 'Params', Mandatory)] [string] $TeamsResourceAccountUpn,
    [Parameter(ParameterSetName = 'Params', Mandatory)] [string] $TeamsResourceAccountDisplayName,
    [Parameter(ParameterSetName = 'Params', Mandatory)] [string] $AcsCommunicationServiceGlobalId,
    [Parameter(ParameterSetName = 'Params', Mandatory)] [string] $TeamsPhoneNumber,
    [Parameter(ParameterSetName = 'Params')]
    [ValidateSet('CallingPlan', 'DirectRouting', 'OperatorConnect')]
    [string] $PhoneNumberType = 'CallingPlan',
    [Parameter(ParameterSetName = 'Params')] [string] $TeamsUsageLocation = 'US',

    [string] $ExistingEntraAppClientId,
    [bool]   $CreateClientSecret = $false,
    [int]    $ClientSecretLifetimeMonths = 12,
    [bool]   $GrantAdminConsent = $false,
    [string] $KeyVaultName,
    [string] $ClientSecretName = 'tpe-entra-app-secret',
    [string[]] $AdditionalLicenseSkuIds = @(),

    [ValidateSet('All', 'Phase1', 'Phase2', 'Phase3')]
    [string[]] $Phases = @('All'),

    [string] $OutputFile = (Join-Path $PWD "tpe-teams-output.json"),

    [switch] $WhatIf
)

$ErrorActionPreference = 'Stop'

# Well-known constants
$AcsFirstPartyAppId        = "1fd5118e-2576-4263-8130-9503064c837a"   # https://auth.msft.communication.azure.com
$TeamsExtManageCallsPermId = "9ed60762-c537-4e50-8984-4b1db3d922ce"   # TeamsExtension.ManageCalls (delegated Scope)
$TeamsExtManageCallsScope  = "TeamsExtension.ManageCalls"
$TeamsPhoneRASkuId         = "440eaaa8-b3e0-484b-a8be-62870b9ba70a"   # Microsoft Teams Phone Resource Account

#region Config file loading
if ($ConfigFile) {
    $config = Get-Content $ConfigFile -Raw | ConvertFrom-Json

    $TeamsTenantId                   = $config.teams.tenantId
    $EntraAppRegistrationName        = $config.teams.entraAppName
    $TeamsResourceAccountUpn         = $config.teams.resourceAccountUpn
    $TeamsResourceAccountDisplayName = $config.teams.resourceAccountDisplayName
    $AcsCommunicationServiceGlobalId = $config.azure.acsGlobalId
    $TeamsPhoneNumber                = $config.teams.phoneNumber
    if ($config.teams.phoneNumberType)         { $PhoneNumberType         = $config.teams.phoneNumberType }
    if ($config.teams.usageLocation)           { $TeamsUsageLocation      = $config.teams.usageLocation }
    if ($config.teams.existingEntraAppClientId){ $ExistingEntraAppClientId = $config.teams.existingEntraAppClientId }
    if ($null -ne $config.teams.createClientSecret)        { $CreateClientSecret = [bool]$config.teams.createClientSecret }
    if ($null -ne $config.teams.grantAdminConsent)         { $GrantAdminConsent  = [bool]$config.teams.grantAdminConsent }
    if ($config.teams.clientSecretLifetimeMonths)          { $ClientSecretLifetimeMonths = [int]$config.teams.clientSecretLifetimeMonths }
    if ($config.teams.additionalLicenseSkuIds)             { $AdditionalLicenseSkuIds = @($config.teams.additionalLicenseSkuIds) }
    if ($config.azure.keyVaultName)                        { $KeyVaultName = $config.azure.keyVaultName }
    if ($config.azure.clientSecretName)                    { $ClientSecretName = $config.azure.clientSecretName }
}
#endregion

#region Helpers
function ShouldRunPhase([string]$phase) {
    return ($Phases -contains 'All') -or ($Phases -contains $phase)
}

function Write-Phase([string]$phase, [string]$description) {
    Write-Host ""
    Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Green
    Write-Host "  $phase — $description" -ForegroundColor Green
    Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Green
    Write-Host ""
}

function Assert-Module([string]$Name, [string]$MinVersion) {
    $mod = Get-Module -ListAvailable -Name $Name |
        Where-Object { -not $MinVersion -or [version]$_.Version -ge [version]$MinVersion } |
        Select-Object -First 1
    if (-not $mod) {
        $verSuffix = if ($MinVersion) { " >= $MinVersion" } else { "" }
        throw "Required PowerShell module not installed: $Name$verSuffix. Install with: Install-Module $Name -Scope CurrentUser"
    }
}
#endregion

#region Module pre-flight
Assert-Module 'Microsoft.Entra' '1.2.0'
Assert-Module 'MicrosoftTeams'  '7.5.0'
Assert-Module 'Microsoft.Graph.Users.Actions'
if ($CreateClientSecret -and $KeyVaultName) {
    Assert-Module 'Az.KeyVault'
}
#endregion

# Track outputs
$entraAppClientId             = $ExistingEntraAppClientId
$entraAppObjectId             = $null
$entraServicePrincipalId      = $null
$teamsResourceAccountObjectId = $null
$clientSecretInfo             = $null
$keyVaultSecretUri            = $null

#region Phase 1 — Entra App Registration
if (ShouldRunPhase 'Phase1') {
    Write-Phase "Phase 1/3" "Entra App Registration (idempotent)"

    Write-Host "Connecting to Teams tenant Entra ID ($TeamsTenantId)..." -ForegroundColor Cyan
    if (-not $WhatIf) {
        Connect-Entra -Scopes @(
            "Application.ReadWrite.All",
            "AppRoleAssignment.ReadWrite.All",
            "DelegatedPermissionGrant.ReadWrite.All"
        ) -TenantId $TeamsTenantId | Out-Null
    }

    $entraApp = $null

    # 1a. Locate existing app — prefer explicit client ID, fall back to display-name lookup.
    if ($ExistingEntraAppClientId) {
        Write-Host "Looking up Entra app by Client ID: $ExistingEntraAppClientId" -ForegroundColor Cyan
        if (-not $WhatIf) {
            $entraApp = Get-EntraApplication -Filter "appId eq '$ExistingEntraAppClientId'" -ErrorAction SilentlyContinue | Select-Object -First 1
            if (-not $entraApp) {
                throw "ExistingEntraAppClientId '$ExistingEntraAppClientId' not found in tenant $TeamsTenantId."
            }
        }
    }
    else {
        Write-Host "Searching for existing Entra app by display name: '$EntraAppRegistrationName'" -ForegroundColor Cyan
        if (-not $WhatIf) {
            $entraApp = Get-EntraApplication -Filter "displayName eq '$EntraAppRegistrationName'" -ErrorAction SilentlyContinue | Select-Object -First 1
        }
    }

    # 1b. Create the app if not found.
    if (-not $entraApp -and -not $WhatIf) {
        Write-Host "Creating new Entra App Registration '$EntraAppRegistrationName'..." -ForegroundColor Cyan
        $requiredResourceAccess = @(
            @{
                resourceAppId  = $AcsFirstPartyAppId
                resourceAccess = @(
                    @{ id = $TeamsExtManageCallsPermId; type = "Scope" }
                )
            }
        )
        $entraApp = New-EntraApplication -DisplayName $EntraAppRegistrationName -RequiredResourceAccess $requiredResourceAccess
        Write-Host "Entra App created. ClientId=$($entraApp.AppId)" -ForegroundColor Green
    }
    elseif ($entraApp) {
        Write-Host "Reusing existing Entra App. ClientId=$($entraApp.AppId)" -ForegroundColor Yellow

        # 1c. Ensure required permission is on the manifest (additive, idempotent).
        $hasPerm = $false
        if ($entraApp.RequiredResourceAccess) {
            foreach ($rra in $entraApp.RequiredResourceAccess) {
                if ($rra.ResourceAppId -eq $AcsFirstPartyAppId) {
                    foreach ($ra in $rra.ResourceAccess) {
                        if ($ra.Id -eq $TeamsExtManageCallsPermId) { $hasPerm = $true; break }
                    }
                }
            }
        }
        if (-not $hasPerm) {
            Write-Host "Adding TeamsExtension.ManageCalls permission to existing app..." -ForegroundColor Cyan
            $newRra = @($entraApp.RequiredResourceAccess) + @(@{
                resourceAppId  = $AcsFirstPartyAppId
                resourceAccess = @(@{ id = $TeamsExtManageCallsPermId; type = "Scope" })
            })
            Set-EntraApplication -ApplicationId $entraApp.Id -RequiredResourceAccess $newRra
        }
        else {
            Write-Host "  TeamsExtension.ManageCalls permission already present." -ForegroundColor DarkGray
        }
    }

    if ($entraApp) {
        $entraAppClientId = $entraApp.AppId
        $entraAppObjectId = $entraApp.Id
    }

    # 1d. Ensure Service Principal exists for the app.
    if (-not $WhatIf -and $entraAppClientId) {
        $sp = Get-EntraServicePrincipal -Filter "appId eq '$entraAppClientId'" -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $sp) {
            Write-Host "Creating Service Principal for app $entraAppClientId..." -ForegroundColor Cyan
            $sp = New-EntraServicePrincipal -AppId $entraAppClientId
        }
        else {
            Write-Host "Service Principal already exists. Id=$($sp.Id)" -ForegroundColor DarkGray
        }
        $entraServicePrincipalId = $sp.Id
    }

    # 1e. Optionally create a client secret and store it in Key Vault.
    if ($CreateClientSecret -and -not $WhatIf -and $entraAppObjectId) {
        # Reuse a non-expired secret with our marker name if present.
        $secretMarker = "tpe-onboarding-$(Get-Date -Format 'yyyyMMdd')"
        $existingCreds = Get-EntraApplicationPasswordCredential -ApplicationId $entraAppObjectId -ErrorAction SilentlyContinue
        $alreadyHasUsable = $existingCreds | Where-Object {
            $_.DisplayName -like 'tpe-onboarding-*' -and $_.EndDateTime -gt (Get-Date).AddDays(30)
        } | Select-Object -First 1

        if ($alreadyHasUsable) {
            Write-Host "Reusing existing client secret '$($alreadyHasUsable.DisplayName)' (expires $($alreadyHasUsable.EndDateTime))." -ForegroundColor Yellow
            Write-Host "  NOTE: secret value is only available at creation time. If you need it again, delete and re-create." -ForegroundColor DarkYellow
        }
        else {
            Write-Host "Creating client secret '$secretMarker' (lifetime: $ClientSecretLifetimeMonths months)..." -ForegroundColor Cyan
            $passwordCredential = @{
                DisplayName   = $secretMarker
                EndDateTime   = (Get-Date).AddMonths($ClientSecretLifetimeMonths)
            }
            $clientSecretInfo = New-EntraApplicationPasswordCredential `
                -ApplicationId $entraAppObjectId `
                -PasswordCredential $passwordCredential

            if ($KeyVaultName) {
                Write-Host "Storing secret in Key Vault '$KeyVaultName' as '$ClientSecretName'..." -ForegroundColor Cyan
                $secureSecret = ConvertTo-SecureString $clientSecretInfo.SecretText -AsPlainText -Force
                $kvSecret = Set-AzKeyVaultSecret `
                    -VaultName $KeyVaultName `
                    -Name $ClientSecretName `
                    -SecretValue $secureSecret `
                    -Tag @{
                        purpose       = 'tpe-entra-app'
                        appClientId   = $entraAppClientId
                        rotationDate  = (Get-Date).ToString('s')
                    }
                $keyVaultSecretUri = $kvSecret.Id
                Write-Host "  Stored at: $keyVaultSecretUri" -ForegroundColor Green
            }
            else {
                Write-Host ""
                Write-Host "─────────────────────────────────────────────────────────────" -ForegroundColor Yellow
                Write-Host "  CLIENT SECRET (record this NOW — it cannot be retrieved later):" -ForegroundColor Yellow
                Write-Host "  $($clientSecretInfo.SecretText)" -ForegroundColor White
                Write-Host "─────────────────────────────────────────────────────────────" -ForegroundColor Yellow
                Write-Host ""
                Write-Warning "No keyVaultName supplied. Store this secret immediately."
            }
        }
    }

    # 1f. Grant tenant-wide admin consent for TeamsExtension.ManageCalls (delegated).
    if ($GrantAdminConsent -and -not $WhatIf -and $entraServicePrincipalId) {
        Write-Host "Granting tenant-wide admin consent for TeamsExtension.ManageCalls..." -ForegroundColor Cyan
        $acsSp = Get-EntraServicePrincipal -Filter "appId eq '$AcsFirstPartyAppId'" -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $acsSp) {
            Write-Host "  ACS first-party SP not present in tenant — provisioning it..." -ForegroundColor DarkCyan
            $acsSp = New-EntraServicePrincipal -AppId $AcsFirstPartyAppId
        }

        $existingGrants = Get-EntraOauth2PermissionGrant -All -ErrorAction SilentlyContinue |
            Where-Object { $_.ClientId -eq $entraServicePrincipalId -and $_.ResourceId -eq $acsSp.Id -and $_.ConsentType -eq 'AllPrincipals' }

        if ($existingGrants -and ($existingGrants | Where-Object { $_.Scope -match $TeamsExtManageCallsScope })) {
            Write-Host "  Admin consent already granted." -ForegroundColor DarkGray
        }
        else {
            try {
                if ($existingGrants) {
                    # Append the scope to the existing grant entry.
                    foreach ($g in $existingGrants) {
                        $newScope = ($g.Scope.Trim() + " " + $TeamsExtManageCallsScope).Trim()
                        Set-EntraOauth2PermissionGrant -OAuth2PermissionGrantId $g.Id -Scope $newScope
                    }
                }
                else {
                    New-EntraOauth2PermissionGrant `
                        -ClientId $entraServicePrincipalId `
                        -ConsentType "AllPrincipals" `
                        -ResourceId $acsSp.Id `
                        -Scope $TeamsExtManageCallsScope | Out-Null
                }
                Write-Host "  Admin consent granted." -ForegroundColor Green
            }
            catch {
                Write-Warning @"
Failed to programmatically grant admin consent: $($_.Exception.Message)
Manual fallback:
  1. Open https://portal.azure.com → Entra ID → App registrations → '$EntraAppRegistrationName'
  2. API permissions → click 'Grant admin consent for <tenant>'
"@
            }
        }
    }
}
#endregion

#region Phase 2 — Resource Account
if (ShouldRunPhase 'Phase2') {
    Write-Phase "Phase 2/3" "Teams Resource Account (idempotent)"

    if (-not $entraAppClientId) {
        throw "Phase 2 requires an Entra App Client ID. Run Phase 1 first or supply -ExistingEntraAppClientId."
    }

    Write-Host "Connecting to Microsoft Teams ($TeamsTenantId)..." -ForegroundColor Cyan
    if (-not $WhatIf) {
        Connect-MicrosoftTeams -TenantId $TeamsTenantId | Out-Null
        Connect-Graph -Scopes User.ReadWrite.All, Organization.Read.All -NoWelcome | Out-Null
    }

    $teamsResourceAccount = $null

    # 2a. Look up existing RA.
    if (-not $WhatIf) {
        try {
            $teamsResourceAccount = Get-CsOnlineApplicationInstance -Identity $TeamsResourceAccountUpn -ErrorAction Stop
            Write-Host "Reusing existing Resource Account '$TeamsResourceAccountUpn'." -ForegroundColor Yellow
            Write-Host "  ObjectId:      $($teamsResourceAccount.ObjectId)" -ForegroundColor DarkGray
            Write-Host "  ApplicationId: $($teamsResourceAccount.ApplicationId)" -ForegroundColor DarkGray
            Write-Host "  AcsResourceId: $($teamsResourceAccount.AcsResourceId)" -ForegroundColor DarkGray
            Write-Host "  PhoneNumber:   $($teamsResourceAccount.PhoneNumber)" -ForegroundColor DarkGray

            if ($teamsResourceAccount.ApplicationId -and $teamsResourceAccount.ApplicationId -ne $entraAppClientId) {
                Write-Warning "Existing RA is bound to ApplicationId $($teamsResourceAccount.ApplicationId) which differs from configured $entraAppClientId. Will rebind."
            }
        }
        catch {
            Write-Host "Resource Account not found. Will create '$TeamsResourceAccountUpn'." -ForegroundColor Cyan
        }
    }

    # 2b. Create RA if missing.
    if (-not $teamsResourceAccount -and -not $WhatIf) {
        Write-Host "Creating Resource Account..." -ForegroundColor Cyan
        $teamsResourceAccount = New-CsOnlineApplicationInstance `
            -UserPrincipalName $TeamsResourceAccountUpn `
            -ApplicationId $entraAppClientId `
            -DisplayName $TeamsResourceAccountDisplayName
        Write-Host "Resource Account created. ObjectId=$($teamsResourceAccount.ObjectId)" -ForegroundColor Green
    }

    if ($teamsResourceAccount) {
        $teamsResourceAccountObjectId = $teamsResourceAccount.ObjectId
    }

    # 2c. Bind RA to the ACS resource (idempotent — Set-* is no-op when already correct).
    if (-not $WhatIf) {
        $needsBind = $true
        if ($teamsResourceAccount.AcsResourceId -eq $AcsCommunicationServiceGlobalId -and
            $teamsResourceAccount.ApplicationId -eq $entraAppClientId) {
            Write-Host "  RA already bound to correct ACS resource and ApplicationId — skipping Set-CsOnlineApplicationInstance." -ForegroundColor DarkGray
            $needsBind = $false
        }

        if ($needsBind) {
            Write-Host "Binding RA → ACS resource $AcsCommunicationServiceGlobalId..." -ForegroundColor Cyan
            Set-CsOnlineApplicationInstance `
                -Identity $TeamsResourceAccountUpn `
                -ApplicationId $entraAppClientId `
                -AcsResourceId $AcsCommunicationServiceGlobalId
        }

        # 2d. Sync — always safe to repeat. Required for newly created bindings;
        # has been observed to also need re-running after later license changes.
        Write-Host "Syncing application instance to Agent Provisioning Service..." -ForegroundColor Cyan
        Sync-CsOnlineApplicationInstance `
            -ObjectId $teamsResourceAccount.ObjectId `
            -ApplicationId $entraAppClientId `
            -AcsResourceId $AcsCommunicationServiceGlobalId
        Write-Host "Sync complete." -ForegroundColor Green
    }
}
#endregion

#region Phase 3 — Licensing & Phone Number
if (ShouldRunPhase 'Phase3') {
    Write-Phase "Phase 3/3" "Licensing & Phone Number (idempotent)"

    $raUpn = $TeamsResourceAccountUpn

    if (-not $WhatIf) {
        # Ensure Teams + Graph are connected (Phase 3 may run independently).
        try { Get-CsOnlineUser -Identity $raUpn -ErrorAction Stop | Out-Null }
        catch {
            Connect-MicrosoftTeams -TenantId $TeamsTenantId | Out-Null
            Connect-Graph -Scopes User.ReadWrite.All, Organization.Read.All -NoWelcome | Out-Null
        }

        # 3a. Wait for the resource account user object to land in Entra ID.
        Write-Host "Waiting for resource account to appear in Entra ID..." -ForegroundColor Cyan
        $resourceAccountObject = $null
        for ($retry = 1; $retry -le 20; $retry++) {
            try {
                $resourceAccountObject = Get-MgUser -UserId $raUpn -ErrorAction Stop
                if ($resourceAccountObject -and $resourceAccountObject.UserPrincipalName -eq $raUpn) { break }
            } catch { }
            Write-Host "  Attempt $retry/20 — not yet visible. Waiting 15s..." -ForegroundColor DarkCyan
            Start-Sleep 15
        }
        if (-not $resourceAccountObject) {
            throw "Resource account $raUpn did not appear in Entra ID after 20 attempts."
        }

        # 3b. Usage location — only set if missing or different.
        if ($resourceAccountObject.UsageLocation -ne $TeamsUsageLocation) {
            Write-Host "Setting usage location: $TeamsUsageLocation" -ForegroundColor Cyan
            Update-MgUser -UserId $raUpn -UsageLocation $TeamsUsageLocation
            Start-Sleep 10
        }
        else {
            Write-Host "Usage location already set to $TeamsUsageLocation." -ForegroundColor DarkGray
        }

        # 3c. License assignment — detect existing assignments.
        $userLicenses = Get-MgUserLicenseDetail -UserId $raUpn -ErrorAction SilentlyContinue
        $assignedSkuIds = @($userLicenses | Select-Object -ExpandProperty SkuId)

        $licensesToAssign = @()
        if ($assignedSkuIds -notcontains $TeamsPhoneRASkuId) {
            $licensesToAssign += $TeamsPhoneRASkuId
        }
        else {
            Write-Host "Teams Phone Resource Account license already assigned." -ForegroundColor DarkGray
        }
        foreach ($sku in $AdditionalLicenseSkuIds) {
            if ($assignedSkuIds -notcontains $sku) {
                $licensesToAssign += $sku
            }
        }

        foreach ($sku in $licensesToAssign) {
            Write-Host "Assigning license SKU $sku..." -ForegroundColor Cyan
            $success = $false
            for ($retry = 1; $retry -le 10 -and -not $success; $retry++) {
                try {
                    Set-MgUserLicense -UserId $raUpn `
                        -AddLicenses @(@{SkuId = $sku}) `
                        -RemoveLicenses @() | Out-Null
                    $success = $true
                }
                catch {
                    Write-Host "  Attempt $retry/10 failed: $($_.Exception.Message)" -ForegroundColor Yellow
                    Start-Sleep 15
                }
            }
            if (-not $success) { throw "License assignment failed for SKU $sku." }
        }

        # 3d. Phone number — pre-flight tenant inventory and skip if already assigned.
        $existingNumber = $null
        try {
            $existingNumber = Get-CsPhoneNumberAssignment -TelephoneNumber $TeamsPhoneNumber -ErrorAction Stop | Select-Object -First 1
        } catch { }

        if ($existingNumber -and $existingNumber.AssignedPstnTargetId -and
            ($existingNumber.AssignedPstnTargetId -eq $resourceAccountObject.Id -or
             $existingNumber.AssignedPstnTargetId -eq $raUpn)) {
            Write-Host "Phone number $TeamsPhoneNumber is already assigned to $raUpn." -ForegroundColor DarkGray
        }
        elseif ($existingNumber -and $existingNumber.AssignedPstnTargetId) {
            throw "Phone number $TeamsPhoneNumber is already assigned to $($existingNumber.AssignedPstnTargetId). Free it first or pick a different number."
        }
        elseif (-not $existingNumber) {
            throw @"
Phone number $TeamsPhoneNumber is not in this tenant's inventory.
Acquire it via Teams Admin Center → Voice → Phone numbers, then re-run Phase 3.
"@
        }
        else {
            Write-Host "Assigning phone number $TeamsPhoneNumber ($PhoneNumberType)..." -ForegroundColor Cyan
            $licenseAssigned = $false
            for ($retry = 1; $retry -le 10 -and -not $licenseAssigned; $retry++) {
                try {
                    $licenseAssigned = $TeamsPhoneRASkuId -in (Get-MgUserLicenseDetail -UserId $raUpn | Select-Object -ExpandProperty SkuId)
                }
                catch {
                    Write-Host "  Attempt $retry/10 — license not yet visible. Waiting 15s..." -ForegroundColor Yellow
                    Start-Sleep 15
                }
            }
            if (-not $licenseAssigned)
            {
                throw "The Teams Phone Resource Account license does not appear to be assigned to $raUpn after multiple retries. Please verify that the license has been applied in Microsoft 365 Admin Center and is visible via Get-MgUserLicenseDetail before re-running this script."
            }

            Set-CsPhoneNumberAssignment `
                -Identity $raUpn `
                -PhoneNumber $TeamsPhoneNumber `
                -PhoneNumberType $PhoneNumberType
            Write-Host "Phone number assigned." -ForegroundColor Green
        }

        if ($PhoneNumberType -ne 'CallingPlan') {
            Write-Warning @"
PhoneNumberType=$PhoneNumberType requires additional manual configuration:
  * DirectRouting   — verified SBC + Voice Routing Policy assigned to the RA.
  * OperatorConnect — number provisioned by an approved OC carrier supporting voice apps.
Verify before testing.
"@
        }

        # Capture ObjectId if not yet known.
        if (-not $teamsResourceAccountObjectId) {
            $raInstance = Get-CsOnlineApplicationInstance -Identity $raUpn
            $teamsResourceAccountObjectId = $raInstance.ObjectId
        }
    }
    else {
        Write-Host "[WhatIf] Would license SKU $TeamsPhoneRASkuId (+$($AdditionalLicenseSkuIds.Count) extra) and assign $TeamsPhoneNumber to $raUpn" -ForegroundColor DarkGray
    }
}
#endregion

#region Output
$output = [ordered]@{
    teamsTenantId                = $TeamsTenantId
    entraAppClientId             = $entraAppClientId
    entraAppObjectId             = $entraAppObjectId
    entraServicePrincipalId      = $entraServicePrincipalId
    keyVaultSecretUri            = $keyVaultSecretUri
    teamsResourceAccountUpn      = $TeamsResourceAccountUpn
    teamsResourceAccountObjectId = $teamsResourceAccountObjectId
    phoneNumber                  = $TeamsPhoneNumber
    phoneNumberType              = $PhoneNumberType
    acsGlobalId                  = $AcsCommunicationServiceGlobalId
}

$outputJson = $output | ConvertTo-Json -Depth 3
Write-Host ""
Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Green
Write-Host "  Teams Tenant Setup Complete" -ForegroundColor Green
Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Green
Write-Host ""
Write-Host $outputJson
Write-Host ""

if (-not $WhatIf) {
    $outputJson | Set-Content -Path $OutputFile -Encoding utf8
    Write-Host "Output written to: $OutputFile" -ForegroundColor Cyan
    if (-not $KeyVaultName -and $clientSecretInfo) {
        Write-Warning "Output file does NOT contain the client secret. Record the secret printed above before continuing."
    }
    Write-Host ""
    Write-Host "Next: Run setup_tpe_azure.ps1 -ConfigFile <config> -TeamsOutputFile '$OutputFile'" -ForegroundColor Yellow
}
#endregion
