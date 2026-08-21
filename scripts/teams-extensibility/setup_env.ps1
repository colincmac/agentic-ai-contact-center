[CmdletBinding(DefaultParameterSetName = 'ConfigFile')]
param(
    [Parameter(ParameterSetName = 'ConfigFile', Mandatory)]
    [string] $ConfigFile
)
$ErrorActionPreference = 'Stop'

$RequiredModules = @(
    @{ Name = "Microsoft.Entra"; MinimumVersion = "1.2.0" },
    @{ Name = "MicrosoftTeams"; MinimumVersion = "7.5.0" },
    @{ Name = "Microsoft.Graph.Users"; MinimumVersion = "2.36.1" }
)

foreach ($module in $RequiredModules) {
    $params = @{
        Name = $module.Name
        Scope = "CurrentUser"
        Force = $true
        AllowClobber = $true
    }

    if ($module.ContainsKey("RequiredVersion")) {
        $params["RequiredVersion"] = $module.RequiredVersion
    }
    elseif ($module.ContainsKey("MinimumVersion")) {
        $params["MinimumVersion"] = $module.MinimumVersion
    }

    # Install if not present or version is outdated
    $installed = Get-Module -ListAvailable -Name $module.Name |
                 Sort-Object Version -Descending |
                 Select-Object -First 1

    if (-not $installed -or
        ($module.ContainsKey("RequiredVersion") -and $installed.Version -ne [version]$module.RequiredVersion) -or
        ($module.ContainsKey("MinimumVersion") -and $installed.Version -lt [version]$module.MinimumVersion)) {

        Write-Host "Installing $($module.Name)..." -ForegroundColor Yellow
        Install-Module @params
    }
    else {
        Write-Host "$($module.Name) is up to date." -ForegroundColor Green
    }
}

if (-not (Test-Path $ConfigFile)) { throw "Config file not found: $ConfigFile" }

$config = Get-Content $ConfigFile -Raw | ConvertFrom-Json `
$azureTenantId        = $config.azure.tenantId `
$azureSubscriptionId  = $config.azure.subscriptionId `
$resourceGroup        = $config.azure.resourceGroupName `
$acsName              = $config.azure.acsName `
$acsGlobalId          = $config.azure.acsGlobalId `
$entraAppName         = $config.teams.entraAppName `
$teamsTenantId        = $config.teams.tenantId `
$teamsResourceAccountObjectId = $config.teams.resourceAccountObjectId `
$teamsResourceAccountDisplayName = $config.teams.resourceAccountDisplayName `
$teamsResourceAccountUpn = $config.teams.resourceAccountUpn `
$phoneNumber          = $config.teams.phoneNumber `
$phoneNumberType      = $config.teams.phoneNumberType `
$usageLocation        = $config.teams.usageLocation
