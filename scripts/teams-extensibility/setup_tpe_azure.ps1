<#
.SYNOPSIS
    Azure-tenant provisioning for Teams Phone Extensibility (TPE).

.DESCRIPTION
    Runs in the AZURE TENANT context. All phases are idempotent and gracefully
    handle pre-existing resources, including the very common case where the ACS
    resource already exists.

      Phase 1 — Azure Bot Service
                * Skipped if the bot already exists.
                * Registers Microsoft.BotService provider on first run.
      Phase 2 — ACS TPE Authorization (PUT assignment, idempotent)
                * RBAC pre-flight: warns if the caller lacks rights.
                * Auto-discovers acsName from acsGlobalId if needed.
      Phase 3 — Event Grid IncomingCall subscription
                * Creates a system topic on the ACS resource if missing.
                * Subscribes a webhook filtered to the RA Object ID, so this
                  ACS resource only delivers events for our resource account.
                * Skipped when eventGrid.enabled = false in config.

    Required CLIs: Azure CLI (az), authenticated to the Azure tenant.
#>

[CmdletBinding(DefaultParameterSetName = 'ConfigFile')]
param(
    [Parameter(ParameterSetName = 'ConfigFile')] [string] $ConfigFile,
    [string] $TeamsOutputFile,

    [Parameter(ParameterSetName = 'Params')] [string] $AzureTenantId,
    [Parameter(ParameterSetName = 'Params')] [string] $AzureSubscriptionId,
    [Parameter(ParameterSetName = 'Params')] [string] $AzureResourceGroupName,
    [Parameter(ParameterSetName = 'Params')] [string] $AcsCommunicationServicesName,
    [Parameter(ParameterSetName = 'Params')] [string] $AcsCommunicationServiceGlobalId,
    [Parameter(ParameterSetName = 'Params')] [string] $AzureBotServiceName,
    [Parameter(ParameterSetName = 'Params')] [string] $BotMessagingEndpoint = 'https://example.invalid/api/messages',

    [string] $EntraAppClientId,
    [string] $TeamsTenantId,
    [string] $TeamsResourceAccountObjectId,

    [bool]   $EventGridEnabled = $true,
    [string] $EventGridSubscriptionName = 'tpe-incoming-call',
    [string] $EventGridEndpointUrl,
    [bool]   $EventGridFilterToResourceAccount = $true,
    [bool]   $AssignAcsContributorToCurrentUser = $false,

    [ValidateSet('All', 'Phase1', 'Phase2', 'Phase3')]
    [string[]] $Phases = @('All'),

    [switch] $SkipBotCreation,
    [switch] $WhatIf
)

$ErrorActionPreference = 'Stop'
$TpeApiVersion = "2025-06-30"

#region Load config + Teams output
if ($ConfigFile) {
    $config = Get-Content $ConfigFile -Raw | ConvertFrom-Json

    if (-not $AzureTenantId)                    { $AzureTenantId = $config.azure.tenantId }
    if (-not $AzureSubscriptionId)              { $AzureSubscriptionId = $config.azure.subscriptionId }
    if (-not $AzureResourceGroupName)           { $AzureResourceGroupName = $config.azure.resourceGroupName }
    if (-not $AcsCommunicationServicesName)     { $AcsCommunicationServicesName = $config.azure.acsName }
    if (-not $AcsCommunicationServiceGlobalId)  { $AcsCommunicationServiceGlobalId = $config.azure.acsGlobalId }
    if (-not $AzureBotServiceName)              { $AzureBotServiceName = $config.azure.botServiceName }
    if ($config.azure.botMessagingEndpoint)     { $BotMessagingEndpoint = $config.azure.botMessagingEndpoint }

    if ($config.azure.eventGrid) {
        if ($null -ne $config.azure.eventGrid.enabled)                     { $EventGridEnabled = [bool]$config.azure.eventGrid.enabled }
        if ($config.azure.eventGrid.subscriptionName)                      { $EventGridSubscriptionName = $config.azure.eventGrid.subscriptionName }
        if ($config.azure.eventGrid.endpointUrl)                           { $EventGridEndpointUrl = $config.azure.eventGrid.endpointUrl }
        if ($null -ne $config.azure.eventGrid.filterToResourceAccountOnly) { $EventGridFilterToResourceAccount = [bool]$config.azure.eventGrid.filterToResourceAccountOnly }
    }
}

if ($TeamsOutputFile) {
    if (-not (Test-Path $TeamsOutputFile)) {
        throw "Teams output file not found: $TeamsOutputFile. Run setup_tpe_teams.ps1 first."
    }
    $teamsOutput = Get-Content $TeamsOutputFile -Raw | ConvertFrom-Json
    if (-not $EntraAppClientId)             { $EntraAppClientId = $teamsOutput.entraAppClientId }
    if (-not $TeamsTenantId)                { $TeamsTenantId = $teamsOutput.teamsTenantId }
    if (-not $TeamsResourceAccountObjectId) { $TeamsResourceAccountObjectId = $teamsOutput.teamsResourceAccountObjectId }
    if (-not $AcsCommunicationServiceGlobalId -and $teamsOutput.acsGlobalId) {
        $AcsCommunicationServiceGlobalId = $teamsOutput.acsGlobalId
    }

    Write-Host "Loaded Teams outputs from: $TeamsOutputFile" -ForegroundColor Cyan
    Write-Host "  Entra App Client ID: $EntraAppClientId"
    Write-Host "  Teams Tenant ID:     $TeamsTenantId"
    Write-Host "  RA Object ID:        $TeamsResourceAccountObjectId"
    Write-Host ""
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

function Invoke-Az {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string[]] $Args, [switch] $AllowFailure)
    $output = & az @Args 2>&1
    $code = $LASTEXITCODE
    if ($code -ne 0 -and -not $AllowFailure) {
        throw "az $($Args -join ' ') failed (exit $code):`n$output"
    }
    return [pscustomobject]@{ ExitCode = $code; Output = $output }
}

function Test-AzCliInstalled {
    if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
        throw "Azure CLI ('az') not found on PATH. Install from https://aka.ms/installazurecli."
    }
}

function Ensure-AzLogin {
    param([string] $TenantId, [string] $SubscriptionId)
    Test-AzCliInstalled
    $acct = Invoke-Az -Args @('account','show','--only-show-errors') -AllowFailure
    if ($acct.ExitCode -ne 0) {
        Write-Host "Azure CLI not signed in — running 'az login --tenant $TenantId'..." -ForegroundColor Cyan
        if (-not $WhatIf) { Invoke-Az -Args @('login','--tenant', $TenantId) | Out-Null }
    }
    if ($SubscriptionId) {
        Invoke-Az -Args @('account','set','--subscription', $SubscriptionId) | Out-Null
    }
}

function Get-AcsResourceId {
    param(
        [string] $SubscriptionId,
        [string] $ResourceGroup,
        [string] $AcsName
    )
    $r = Invoke-Az -Args @(
        'resource','show',
        '--resource-type','Microsoft.Communication/communicationServices',
        '--name', $AcsName,
        '--resource-group', $ResourceGroup,
        '--subscription', $SubscriptionId,
        '--query','id','-o','tsv',
        '--only-show-errors'
    ) -AllowFailure
    if ($r.ExitCode -eq 0) { return ($r.Output -join '').Trim() }
    return $null
}

function Test-AcsRbac {
    param([string] $AcsResourceId)
    # Best-effort: check the signed-in principal has any role on the ACS resource.
    $signedInId = (Invoke-Az -Args @('ad','signed-in-user','show','--query','id','-o','tsv','--only-show-errors') -AllowFailure).Output
    if (-not $signedInId) { return $true }   # service principal context — skip
    $signedInId = ($signedInId -join '').Trim()
    $roles = Invoke-Az -Args @(
        'role','assignment','list',
        '--assignee', $signedInId,
        '--scope', $AcsResourceId,
        '--query','[].roleDefinitionName','-o','tsv','--only-show-errors'
    ) -AllowFailure
    if ($roles.ExitCode -ne 0) { return $true }
    $roleList = @($roles.Output | Where-Object { $_ })
    if ($roleList.Count -eq 0) {
        Write-Warning @"
Signed-in user has no RBAC role on ACS resource $AcsResourceId.
The TPE assignment API requires at least 'Contributor' (or a custom role granting
Microsoft.Communication/communicationServices/teamsExtension/*) on the ACS resource.
"@
        return $false
    }
    Write-Host "  RBAC OK — signed-in user roles on ACS: $($roleList -join ', ')" -ForegroundColor DarkGray
    return $true
}
#endregion

Ensure-AzLogin -TenantId $AzureTenantId -SubscriptionId $AzureSubscriptionId

# Resolve ACS resource — required for Phases 2 and 3.
$acsResourceId = $null
if ($AcsCommunicationServicesName -and $AzureResourceGroupName) {
    $acsResourceId = Get-AcsResourceId -SubscriptionId $AzureSubscriptionId -ResourceGroup $AzureResourceGroupName -AcsName $AcsCommunicationServicesName
    if ($acsResourceId) {
        Write-Host "ACS resource located: $acsResourceId" -ForegroundColor DarkGray
    }
    else {
        Write-Warning "Could not locate ACS resource '$AcsCommunicationServicesName' in RG '$AzureResourceGroupName'. Phase 3 will be skipped if not found."
    }
}

#region Phase 1 — Bot Service (idempotent)
if (ShouldRunPhase 'Phase1') {
    Write-Phase "Phase 1/3" "Azure Bot Service"

    if (-not $EntraAppClientId) { throw "Phase 1 requires -EntraAppClientId or -TeamsOutputFile." }
    if (-not $TeamsTenantId)    { throw "Phase 1 requires -TeamsTenantId." }
    if (-not $AzureBotServiceName -or -not $AzureResourceGroupName) {
        throw "Phase 1 requires AzureBotServiceName and AzureResourceGroupName."
    }

    if ($SkipBotCreation) {
        Write-Host "Skipping Bot Service phase (-SkipBotCreation)." -ForegroundColor Yellow
    }
    else {
        # Ensure provider is registered.
        $providerState = (Invoke-Az -Args @(
            'provider','show','--namespace','Microsoft.BotService',
            '--query','registrationState','-o','tsv','--only-show-errors'
        ) -AllowFailure).Output
        if (($providerState -join '').Trim() -ne 'Registered') {
            Write-Host "Registering Microsoft.BotService provider..." -ForegroundColor Cyan
            if (-not $WhatIf) {
                Invoke-Az -Args @('provider','register','--namespace','Microsoft.BotService','--wait') | Out-Null
            }
        }

        # Idempotent existence check.
        $existing = Invoke-Az -Args @(
            'bot','show',
            '--name', $AzureBotServiceName,
            '--resource-group', $AzureResourceGroupName,
            '--subscription', $AzureSubscriptionId,
            '--only-show-errors'
        ) -AllowFailure

        if ($existing.ExitCode -eq 0) {
            Write-Host "Bot Service '$AzureBotServiceName' already exists — skipping create." -ForegroundColor Yellow
        }
        else {
            Write-Host "Creating Bot Service '$AzureBotServiceName'..." -ForegroundColor Cyan
            Write-Host "  AppId:     $EntraAppClientId" -ForegroundColor DarkCyan
            Write-Host "  TenantId:  $TeamsTenantId (Teams tenant)" -ForegroundColor DarkCyan
            Write-Host "  Endpoint:  $BotMessagingEndpoint" -ForegroundColor DarkCyan

            $botArgs = @(
                'bot','create',
                '--resource-group', $AzureResourceGroupName,
                '--name', $AzureBotServiceName,
                '--app-type','SingleTenant',
                '--appid', $EntraAppClientId,
                '--tenant-id', $TeamsTenantId,
                '--sku','S1',
                '--location','global',
                '--endpoint', $BotMessagingEndpoint,
                '--subscription', $AzureSubscriptionId,
                '--only-show-errors'
            )
            if ($WhatIf) {
                Write-Host "[WhatIf] az $($botArgs -join ' ')" -ForegroundColor DarkGray
            }
            else {
                Invoke-Az -Args $botArgs | Out-Null
                Write-Host "Bot Service created." -ForegroundColor Green

                $msTeamsChannelArgs = @(
                    'bot','msteams', 'create',
                    '--resource-group', $AzureResourceGroupName,
                    '--name', $AzureBotServiceName,
                    '--enable-calling','--calling-web-hook', $BotMessagingEndpoint,
                    '--subscription', $AzureSubscriptionId,
                    '--only-show-errors'
                )
                if ($WhatIf) {
                    Write-Host "[WhatIf] az $($msTeamsChannelArgs -join ' ')" -ForegroundColor DarkGray
                }
                else {
                    Invoke-Az -Args $msTeamsChannelArgs | Out-Null
                    Write-Host "Bot Service Teams channel registration created." -ForegroundColor Green
                }
            }
        }
    }
}
#endregion

#region Phase 2 — ACS TPE Authorization (idempotent)
if (ShouldRunPhase 'Phase2') {
    Write-Phase "Phase 2/3" "ACS TPE Assignment"

    if (-not $TeamsResourceAccountObjectId) { throw "Phase 2 requires -TeamsResourceAccountObjectId." }
    if (-not $TeamsTenantId)                { throw "Phase 2 requires -TeamsTenantId." }
    if (-not $AcsCommunicationServicesName) { throw "Phase 2 requires -AcsCommunicationServicesName." }

    if ($acsResourceId) { Test-AcsRbac -AcsResourceId $acsResourceId | Out-Null }

    $scriptPath = Join-Path $PSScriptRoot "azure_acs_tpe_auth.ps1"
    if (-not (Test-Path $scriptPath)) {
        throw "Required helper not found: $scriptPath"
    }

    Write-Host "Creating/refreshing TPE assignment..." -ForegroundColor Cyan
    Write-Host "  ACS Resource:  $AcsCommunicationServicesName" -ForegroundColor DarkCyan
    Write-Host "  Teams Tenant:  $TeamsTenantId" -ForegroundColor DarkCyan
    Write-Host "  RA Object ID:  $TeamsResourceAccountObjectId" -ForegroundColor DarkCyan

    $tpeArgs = @{
        AzureCommunicationServicesName = $AcsCommunicationServicesName
        TeamsTenantId                  = $TeamsTenantId
        TeamsResourceAccountObjectId   = $TeamsResourceAccountObjectId
        PrincipalType                  = 'teamsResourceAccount'
        Verbose                        = $true
    }
    if ($WhatIf) { $tpeArgs['WhatIf'] = $true }

    & $scriptPath @tpeArgs

    if (-not $WhatIf) {
        Write-Host "Verifying TPE assignment..." -ForegroundColor Cyan
        $verifyUrl = "https://$AcsCommunicationServicesName.communication.azure.com/access/teamsExtension/tenants/$TeamsTenantId/assignments/${TeamsResourceAccountObjectId}?api-version=$TpeApiVersion"
        $verify = Invoke-Az -Args @(
            'rest','--method','GET','--url', $verifyUrl,
            '--resource','https://communication.azure.com',
            '--only-show-errors'
        ) -AllowFailure
        if ($verify.ExitCode -eq 0) {
            Write-Host "Verification OK:" -ForegroundColor Green
            Write-Host ($verify.Output -join "`n")
        }
        else {
            Write-Warning "Verification GET failed (the assignment may still be propagating). Output:`n$($verify.Output)"
        }
    }
}
#endregion

#region Phase 3 — Event Grid IncomingCall subscription (idempotent)
if (ShouldRunPhase 'Phase3') {
    Write-Phase "Phase 3/3" "Event Grid IncomingCall Subscription"

    if (-not $EventGridEnabled) {
        Write-Host "Event Grid step disabled in configuration — skipping." -ForegroundColor Yellow
    }
    elseif (-not $EventGridEndpointUrl) {
        Write-Warning "eventGrid.endpointUrl not configured. Skipping subscription creation. Set the URL after your callback is hosted (e.g. dev tunnel) and re-run with -Phases Phase3."
    }
    elseif (-not $acsResourceId) {
        Write-Warning "ACS resource not located — cannot create Event Grid subscription. Skipping."
    }
    else {
        # Idempotent existence check.
        $existing = Invoke-Az -Args @(
            'eventgrid','event-subscription','show',
            '--name', $EventGridSubscriptionName,
            '--source-resource-id', $acsResourceId,
            '--only-show-errors'
        ) -AllowFailure

        $existsAndCorrect = $false
        if ($existing.ExitCode -eq 0) {
            $existingObj = $existing.Output -join "`n" | ConvertFrom-Json -ErrorAction SilentlyContinue
            $existingEndpoint = $existingObj.destination.endpointUrl
            if ($existingEndpoint -and $existingEndpoint -like "$EventGridEndpointUrl*") {
                Write-Host "Event Grid subscription '$EventGridSubscriptionName' already exists with matching endpoint." -ForegroundColor Yellow
                $existsAndCorrect = $true
            }
            else {
                Write-Host "Event Grid subscription '$EventGridSubscriptionName' exists but endpoint differs — updating." -ForegroundColor Cyan
            }
        }

        if (-not $existsAndCorrect) {
            $verb = if ($existing.ExitCode -eq 0) { 'update' } else { 'create' }
            Write-Host "Running 'az eventgrid event-subscription $verb' for IncomingCall..." -ForegroundColor Cyan
            $egArgs = @(
                'eventgrid','event-subscription', $verb,
                '--name', $EventGridSubscriptionName,
                '--source-resource-id', $acsResourceId,
                '--endpoint-type','webhook',
                '--endpoint', $EventGridEndpointUrl,
                '--included-event-types','Microsoft.Communication.IncomingCall',
                '--only-show-errors'
            )
            if ($EventGridFilterToResourceAccount -and $TeamsResourceAccountObjectId) {
                $egArgs += @(
                    '--advanced-filter','data.to.rawId','StringContains', $TeamsResourceAccountObjectId
                )
            }
            if ($WhatIf) {
                Write-Host "[WhatIf] az $($egArgs -join ' ')" -ForegroundColor DarkGray
            }
            else {
                Invoke-Az -Args $egArgs | Out-Null
                Write-Host "Event Grid subscription configured." -ForegroundColor Green
                Write-Host "  NOTE: Microsoft.Communication.IncomingCall delivery requires that the webhook responds to the validation handshake on first delivery." -ForegroundColor DarkYellow
            }
        }
    }
}
#endregion

#region Summary
Write-Host ""
Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Green
Write-Host "  Azure Tenant Setup Complete" -ForegroundColor Green
Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Green
Write-Host ""
Write-Host "  Bot Service:           $AzureBotServiceName"
Write-Host "  ACS Resource:          $AcsCommunicationServicesName ($AcsCommunicationServiceGlobalId)"
Write-Host "  RA Object ID:          $TeamsResourceAccountObjectId"
Write-Host "  Teams Tenant:          $TeamsTenantId"
Write-Host "  Event Grid Endpoint:   $(if ($EventGridEndpointUrl) { $EventGridEndpointUrl } else { '(not configured — set eventGrid.endpointUrl and re-run -Phases Phase3)' })"
Write-Host ""
Write-Host "Verify end-to-end with a test call to the assigned phone number." -ForegroundColor Green
Write-Host ""
#endregion
