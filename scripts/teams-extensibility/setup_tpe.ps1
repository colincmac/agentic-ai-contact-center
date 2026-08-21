<#
.SYNOPSIS
    Orchestrator for end-to-end Teams Phone Extensibility (TPE) provisioning.

.DESCRIPTION
    Runs setup_tpe_teams.ps1 (Teams tenant) then setup_tpe_azure.ps1 (Azure tenant)
    in the correct dependency order. The Teams script outputs a JSON file that the
    Azure script consumes.

    Both scripts are fully idempotent — re-running this orchestrator against an
    environment that already has the Entra App, Resource Account, phone number,
    Bot Service, ACS resource, or Event Grid subscription will reuse them.

    For environments where different admins manage each tenant, run the scripts
    individually instead — see docs/tpe-onboarding-guide.md.

.PARAMETER ConfigFile
    Path to a JSON configuration file (same schema as tpe-config.sample.json).

.PARAMETER ExistingEntraAppClientId
    If the Entra App already exists, supply its Client ID (overrides the value
    in the config file).

.PARAMETER SkipBotCreation
    Skip Azure Bot Service creation (use when the bot is managed elsewhere).

.PARAMETER WhatIf
    Dry-run mode — prints what would happen without making changes.

.EXAMPLE
    .\setup_tpe.ps1 -ConfigFile .\tpe-config.json

.EXAMPLE
    # Re-run only the Azure side after the Teams admin has provisioned everything
    .\setup_tpe.ps1 -ConfigFile .\tpe-config.json -SkipBotCreation
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ConfigFile,

    [string] $ExistingEntraAppClientId,
    [switch] $SkipBotCreation,
    [switch] $WhatIf
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $ConfigFile)) { throw "Config file not found: $ConfigFile" }

$teamsOutputFile = Join-Path $PSScriptRoot "tpe-teams-output.json"

#region Step 1 — Teams Tenant
Write-Host ""
Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Magenta
Write-Host "  Step 1 of 2 — Teams Tenant Provisioning (idempotent)" -ForegroundColor Magenta
Write-Host "  Entra App → Resource Account → License + Phone Number" -ForegroundColor Magenta
Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Magenta
Write-Host ""

$teamsArgs = @(
    "-ConfigFile", $ConfigFile,
    "-OutputFile", $teamsOutputFile
)
if ($ExistingEntraAppClientId) { $teamsArgs += "-ExistingEntraAppClientId", $ExistingEntraAppClientId }
if ($WhatIf) { $teamsArgs += "-WhatIf" }

$teamsScript = Join-Path $PSScriptRoot "setup_tpe_teams.ps1"
& $teamsScript @teamsArgs

if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "Teams tenant setup failed." }
#endregion

#region Step 2 — Azure Tenant
Write-Host ""
Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Magenta
Write-Host "  Step 2 of 2 — Azure Tenant Provisioning (idempotent)" -ForegroundColor Magenta
Write-Host "  Bot Service → ACS TPE Authorization → Event Grid Subscription" -ForegroundColor Magenta
Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Magenta
Write-Host ""

$azureArgs = @(
    "-ConfigFile", $ConfigFile,
    "-TeamsOutputFile", $teamsOutputFile
)
if ($SkipBotCreation) { $azureArgs += "-SkipBotCreation" }
if ($WhatIf) { $azureArgs += "-WhatIf" }

$azureScript = Join-Path $PSScriptRoot "setup_tpe_azure.ps1"
& $azureScript @azureArgs

if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "Azure tenant setup failed." }
#endregion

Write-Host ""
Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Green
Write-Host "  End-to-End TPE Setup Complete" -ForegroundColor Green
Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Green
Write-Host ""
Write-Host "Teams output saved to: $teamsOutputFile" -ForegroundColor Cyan
Write-Host ""
Write-Host "Final verification — place a test call to the assigned phone number." -ForegroundColor Yellow
Write-Host "If nothing arrives at your callback, see docs/tpe-onboarding-guide.md → Verification Checklist." -ForegroundColor Yellow
Write-Host ""
