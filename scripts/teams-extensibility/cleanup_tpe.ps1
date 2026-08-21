<#
.SYNOPSIS
    Teardown for Teams Phone Extensibility (TPE) provisioning.

.DESCRIPTION
    Removes the resources provisioned by setup_tpe.ps1, in reverse order.
    Every operation is gated by -Confirm semantics (this script uses
    SupportsShouldProcess) and is safe to run against partial deployments.

    By default this script DOES NOT remove:
      * The Azure Communication Services resource itself.
      * The phone number (returned to inventory only).
      * The Entra App Registration (use -RemoveEntraApp to opt in).

    Removed (when present):
      * Event Grid IncomingCall subscription
      * ACS TPE assignment for the resource account
      * Azure Bot Service
      * Phone number assignment (number is unassigned, not released)
      * Resource Account license
      * Teams Resource Account (use -RemoveResourceAccount to opt in)

.PARAMETER ConfigFile
    Path to the same JSON config used by setup_tpe.ps1.

.PARAMETER TeamsOutputFile
    Path to the tpe-teams-output.json from setup_tpe_teams.ps1
    (provides the RA Object ID needed for some teardown steps).

.PARAMETER RemoveResourceAccount
    Also delete the Teams Resource Account user object.

.PARAMETER RemoveEntraApp
    Also delete the Entra App Registration (and its Service Principal).
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)] [string] $ConfigFile,
    [string] $TeamsOutputFile,

    [switch] $RemoveResourceAccount,
    [switch] $RemoveEntraApp
)

$ErrorActionPreference = 'Stop'
$TpeApiVersion = "2025-06-30"

if (-not (Test-Path $ConfigFile)) { throw "Config file not found: $ConfigFile" }
$config = Get-Content $ConfigFile -Raw | ConvertFrom-Json

if (-not $TeamsOutputFile) {
    $TeamsOutputFile = Join-Path $PSScriptRoot "tpe-teams-output.json"
}
$teamsOutput = $null
if (Test-Path $TeamsOutputFile) {
    $teamsOutput = Get-Content $TeamsOutputFile -Raw | ConvertFrom-Json
}

$azureTenantId        = $config.azure.tenantId
$azureSubscriptionId  = $config.azure.subscriptionId
$resourceGroup        = $config.azure.resourceGroupName
$acsName              = $config.azure.acsName
$botName              = $config.azure.botServiceName
$eventGridSubName     = if ($config.azure.eventGrid.subscriptionName) { $config.azure.eventGrid.subscriptionName } else { 'tpe-incoming-call' }

$teamsTenantId        = $config.teams.tenantId
$raUpn                = $config.teams.resourceAccountUpn
$raObjectId           = if ($teamsOutput) { $teamsOutput.teamsResourceAccountObjectId } else { $null }
$entraAppClientId     = if ($teamsOutput) { $teamsOutput.entraAppClientId } else { $config.teams.existingEntraAppClientId }
$phoneNumber          = $config.teams.phoneNumber

function Confirm-And-Run {
    param([string] $Description, [scriptblock] $Action)
    if ($PSCmdlet.ShouldProcess($Description, "Remove")) {
        try { & $Action }
        catch { Write-Warning "Failed to remove '$Description': $($_.Exception.Message)" }
    }
}

#region Azure side
Write-Host ""
Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Magenta
Write-Host "  Azure Teardown" -ForegroundColor Magenta
Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Magenta

$null = az account show 2>$null
if ($LASTEXITCODE -ne 0) { az login --tenant $azureTenantId | Out-Null }
az account set --subscription $azureSubscriptionId | Out-Null

$acsResourceId = (az resource show `
    --resource-type 'Microsoft.Communication/communicationServices' `
    --name $acsName --resource-group $resourceGroup --subscription $azureSubscriptionId `
    --query id -o tsv --only-show-errors 2>$null)

# 1. Event Grid subscription
if ($acsResourceId) {
    Confirm-And-Run "Event Grid subscription '$eventGridSubName' on $acsName" {
        az eventgrid event-subscription delete `
            --name $eventGridSubName `
            --source-resource-id $acsResourceId `
            --only-show-errors | Out-Null
    }
}

# 2. ACS TPE assignment
if ($raObjectId -and $acsName) {
    Confirm-And-Run "ACS TPE assignment for RA $raObjectId" {
        $url = "https://$acsName.communication.azure.com/access/teamsExtension/assignments/${raObjectId}?api-version=$TpeApiVersion"
        az rest --method DELETE --url $url --resource "https://communication.azure.com" --only-show-errors | Out-Null
    }
}

# 3. Bot Service
$botExists = (az bot show --name $botName --resource-group $resourceGroup --subscription $azureSubscriptionId --only-show-errors 2>$null)
if ($LASTEXITCODE -eq 0 -and $botExists) {
    Confirm-And-Run "Azure Bot Service '$botName'" {
        az bot delete --name $botName --resource-group $resourceGroup --subscription $azureSubscriptionId --only-show-errors | Out-Null
    }
}
#endregion

#region Teams side
Write-Host ""
Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Magenta
Write-Host "  Teams Teardown" -ForegroundColor Magenta
Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Magenta

Connect-MicrosoftTeams -TenantId $teamsTenantId | Out-Null
Connect-Graph -Scopes User.ReadWrite.All -NoWelcome | Out-Null

# 4. Phone number unassignment (number stays in tenant inventory)
$existingNumber = $null
try { $existingNumber = Get-CsPhoneNumberAssignment -TelephoneNumber $phoneNumber -ErrorAction Stop | Select-Object -First 1 } catch { }
if ($existingNumber -and $existingNumber.AssignedPstnTargetId) {
    Confirm-And-Run "Phone number $phoneNumber from $raUpn (number returned to inventory)" {
        Remove-CsPhoneNumberAssignment -Identity $raUpn -PhoneNumber $phoneNumber -PhoneNumberType $existingNumber.NumberType
    }
}

# 5. License removal
$ra = $null
try { $ra = Get-MgUser -UserId $raUpn -ErrorAction Stop } catch { }
if ($ra) {
    $licDetails = Get-MgUserLicenseDetail -UserId $raUpn -ErrorAction SilentlyContinue
    foreach ($lic in $licDetails) {
        Confirm-And-Run "License $($lic.SkuPartNumber) from $raUpn" {
            Set-MgUserLicense -UserId $raUpn -AddLicenses @() -RemoveLicenses @($lic.SkuId) | Out-Null
        }
    }
}

# 6. Resource Account
if ($RemoveResourceAccount) {
    Confirm-And-Run "Teams Resource Account $raUpn" {
        Remove-CsOnlineApplicationInstance -Identity $raUpn -ErrorAction SilentlyContinue
        if ($ra) { Remove-MgUser -UserId $raUpn }
    }
}
else {
    Write-Host "Resource Account NOT removed (use -RemoveResourceAccount to delete it)." -ForegroundColor Yellow
}

# 7. Entra App
if ($RemoveEntraApp -and $entraAppClientId) {
    Connect-Entra -Scopes "Application.ReadWrite.All" -TenantId $teamsTenantId | Out-Null
    $app = Get-EntraApplication -Filter "appId eq '$entraAppClientId'" -ErrorAction SilentlyContinue | Select-Object -First 1
    $sp  = Get-EntraServicePrincipal -Filter "appId eq '$entraAppClientId'" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($sp) {
        Confirm-And-Run "Service Principal $($sp.Id) for app $entraAppClientId" {
            Remove-EntraServicePrincipal -ObjectId $sp.Id
        }
    }
    if ($app) {
        Confirm-And-Run "Entra App Registration $entraAppClientId" {
            Remove-EntraApplication -ApplicationId $app.Id
        }
    }
}
elseif ($entraAppClientId) {
    Write-Host "Entra App $entraAppClientId NOT removed (use -RemoveEntraApp to delete it)." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Teardown complete." -ForegroundColor Green
Write-Host ""
