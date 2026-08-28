<#
.SYNOPSIS
	Temporarily allows this computer's public IP address to access Microsoft Foundry.

.DESCRIPTION
	Discovers the Foundry AIServices account for an azd environment, adds the
	computer's current public IPv4 address as a /32 firewall rule, and enables
	the account's public endpoint with deny-by-default network ACLs.

	The first Enable operation records the account's original network settings
	under .azure/<environment>/ so Disable can restore them. Disable removes only
	IP rules added by this script. Status performs no writes.

	This script intentionally changes only the Foundry account needed for
	project-level calls from https://ai.azure.com. It does not expose private
	Storage, Cosmos DB, Search, Key Vault, or other service dependencies.

.PARAMETER Action
	Enable, Disable, or Status. The default is Enable.

.PARAMETER EnvironmentName
	The azd environment to use. Defaults to the currently selected environment.

.PARAMETER SubscriptionId
	Optional Azure subscription override. Defaults to AZURE_SUBSCRIPTION_ID in
	the selected azd environment.

.PARAMETER ResourceGroupName
	Optional Foundry account resource group. Specify with AccountName to bypass
	tag-based discovery.

.PARAMETER AccountName
	Optional Foundry AIServices account name. Specify with ResourceGroupName to
	bypass tag-based discovery.

.PARAMETER IpAddress
	Optional public IPv4 address or CIDR range override. By default, two
	independent services are queried and must report the same address. Specify
	an approved CIDR range when the Dev Box uses rotating public egress.

.EXAMPLE
	./scripts/eng/whitelist-local-env-in-azure.ps1

.EXAMPLE
	./scripts/eng/whitelist-local-env-in-azure.ps1 -Action Status

.EXAMPLE
	./scripts/eng/whitelist-local-env-in-azure.ps1 -Action Disable

.EXAMPLE
	./scripts/eng/whitelist-local-env-in-azure.ps1 -EnvironmentName centralus -WhatIf
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
	[ValidateSet('Enable', 'Disable', 'Status')]
	[string] $Action = 'Enable',

	[string] $EnvironmentName,
	[string] $SubscriptionId,
	[string] $ResourceGroupName,
	[string] $AccountName,

	[ValidateScript({
		$parts = $_ -split '/', 2
		$parsedAddress = $null
		$validAddress = [System.Net.IPAddress]::TryParse($parts[0], [ref] $parsedAddress) -and
			$parsedAddress.AddressFamily -eq [System.Net.Sockets.AddressFamily]::InterNetwork
		$validPrefix = $parts.Count -eq 1 -or
			($parts[1] -match '^\d{1,2}$' -and [int] $parts[1] -ge 0 -and [int] $parts[1] -le 32)
		$validAddress -and $validPrefix
	})]
	[string] $IpAddress
)

$ErrorActionPreference = 'Stop'
$FoundryApiVersion = '2026-05-01'
$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

function Assert-Command {
	param([Parameter(Mandatory)] [string] $Name)

	if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
		throw "Required command '$Name' was not found on PATH."
	}
}

function Invoke-ExternalCommand {
	param(
		[Parameter(Mandatory)] [string] $Command,
		[Parameter(Mandatory)] [string[]] $Arguments
	)

	$output = & $Command @Arguments 2>&1
	if ($LASTEXITCODE -ne 0) {
		throw "Command failed: $Command $($Arguments -join ' ')`n$($output -join [Environment]::NewLine)"
	}

	return $output
}

function Get-AzdValue {
	param(
		[Parameter(Mandatory)] [string] $Name,
		[string] $Environment
	)

	$arguments = @('env', 'get-value', $Name)
	if ($Environment) {
		$arguments += @('--environment', $Environment)
	}

	$value = Invoke-ExternalCommand -Command 'azd' -Arguments $arguments
	return ([string] ($value -join "`n")).Trim().Trim('"')
}

function Get-OptionalAzdValue {
	param(
		[Parameter(Mandatory)] [string] $Name,
		[string] $Environment
	)

	try {
		return Get-AzdValue -Name $Name -Environment $Environment
	}
	catch {
		return $null
	}
}

function Get-PublicIpv4Address {
	$addresses = foreach ($uri in @('https://api.ipify.org', 'https://ifconfig.me/ip')) {
		try {
			([string] (Invoke-RestMethod -Uri $uri -TimeoutSec 10)).Trim()
		}
		catch {
			throw "Unable to determine the public IP address from '$uri': $($_.Exception.Message)"
		}
	}

	if ($addresses[0] -ne $addresses[1]) {
		throw "Public IP checks disagreed: '$($addresses[0])' and '$($addresses[1])'. Use -IpAddress to select the intended address."
	}

	$parsedAddress = $null
	if (-not [System.Net.IPAddress]::TryParse($addresses[0], [ref] $parsedAddress) -or
		$parsedAddress.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) {
		throw "The detected address '$($addresses[0])' is not a valid public IPv4 address."
	}

	return $addresses[0]
}

function Get-FoundryAccount {
	param(
		[Parameter(Mandatory)] [string] $Subscription,
		[Parameter(Mandatory)] [string] $Environment,
		[string] $ResourceGroup,
		[string] $Name
	)

	if (($ResourceGroup -and -not $Name) -or ($Name -and -not $ResourceGroup)) {
		throw 'Specify AccountName and ResourceGroupName together.'
	}

	if ($ResourceGroup -and $Name) {
		$json = Invoke-ExternalCommand -Command 'az' -Arguments @(
			'cognitiveservices', 'account', 'show',
			'--subscription', $Subscription,
			'--resource-group', $ResourceGroup,
			'--name', $Name,
			'--output', 'json',
			'--only-show-errors'
		)
		$account = ($json -join "`n") | ConvertFrom-Json
		if ($account.kind -ne 'AIServices') {
			throw "Account '$Name' has kind '$($account.kind)', not 'AIServices'."
		}
		return $account
	}

	$json = Invoke-ExternalCommand -Command 'az' -Arguments @(
		'resource', 'list',
		'--subscription', $Subscription,
		'--resource-type', 'Microsoft.CognitiveServices/accounts',
		'--output', 'json',
		'--only-show-errors'
	)
	$accounts = @(($json -join "`n") | ConvertFrom-Json | Where-Object {
		$_.kind -eq 'AIServices' -and $_.tags.environment -eq $Environment
	})

	if ($accounts.Count -eq 0) {
		throw "No AIServices account with tag environment='$Environment' was found. Use -AccountName and -ResourceGroupName to select it explicitly."
	}
	if ($accounts.Count -gt 1) {
		$choices = ($accounts | ForEach-Object { "$($_.resourceGroup)/$($_.name)" }) -join ', '
		throw "Multiple AIServices accounts matched environment '$Environment': $choices. Use -AccountName and -ResourceGroupName."
	}

	return Get-FoundryAccount `
		-Subscription $Subscription `
		-Environment $Environment `
		-ResourceGroup $accounts[0].resourceGroup `
		-Name $accounts[0].name
}

function Get-NetworkState {
	param([Parameter(Mandatory)] $Account)

	$ipRules = @($Account.properties.networkAcls.ipRules | ForEach-Object { $_.value })
	return [ordered]@{
		resourceId = $Account.id
		subscriptionId = $Account.id.Split('/')[2]
		resourceGroupName = $Account.resourceGroup
		accountName = $Account.name
		originalPublicNetworkAccess = $Account.properties.publicNetworkAccess
		originalDefaultAction = $Account.properties.networkAcls.defaultAction
		originalIpRules = $ipRules
		addedIpRules = @()
		createdAtUtc = [DateTime]::UtcNow.ToString('o')
	}
}

function Set-FoundryNetworkProperties {
	param(
		[Parameter(Mandatory)] [string] $ResourceId,
		[Parameter(Mandatory)] [string] $PublicNetworkAccess,
		[Parameter(Mandatory)] [string] $DefaultAction
	)

	Invoke-ExternalCommand -Command 'az' -Arguments @(
		'resource', 'update',
		'--ids', $ResourceId,
		'--api-version', $FoundryApiVersion,
		'--set',
		"properties.publicNetworkAccess=$PublicNetworkAccess",
		"properties.networkAcls.defaultAction=$DefaultAction",
		'--output', 'none',
		'--only-show-errors'
	) | Out-Null
}

Assert-Command -Name 'az'
Assert-Command -Name 'azd'

if (-not $EnvironmentName) {
	$EnvironmentName = Get-AzdValue -Name 'AZURE_ENV_NAME'
}
if (-not $SubscriptionId) {
	$SubscriptionId = Get-AzdValue -Name 'AZURE_SUBSCRIPTION_ID' -Environment $EnvironmentName
}
if (-not $EnvironmentName) {
	throw 'Unable to resolve the azd environment name. Use -EnvironmentName.'
}
if (-not $SubscriptionId) {
	throw 'Unable to resolve the Azure subscription ID. Use -SubscriptionId.'
}

if (-not $AccountName -and -not $ResourceGroupName) {
	$AccountName = Get-OptionalAzdValue -Name 'AZURE_AI_ACCOUNT_NAME' -Environment $EnvironmentName
	$ResourceGroupName = Get-OptionalAzdValue -Name 'AZURE_PLATFORM_RESOURCE_GROUP' -Environment $EnvironmentName
	if (-not $AccountName -or -not $ResourceGroupName) {
		$AccountName = $null
		$ResourceGroupName = $null
	}
}

Invoke-ExternalCommand -Command 'az' -Arguments @(
	'account', 'show', '--subscription', $SubscriptionId, '--output', 'none', '--only-show-errors'
) | Out-Null

$stateDirectory = Join-Path $RepositoryRoot ".azure\$EnvironmentName"
$statePath = Join-Path $stateDirectory 'whitelist-local-env-state.json'
$state = if (Test-Path $statePath) {
	Get-Content $statePath -Raw | ConvertFrom-Json
}
else {
	$null
}

if ($Action -eq 'Status') {
	$account = if ($state) {
		Get-FoundryAccount `
			-Subscription $state.subscriptionId `
			-Environment $EnvironmentName `
			-ResourceGroup $state.resourceGroupName `
			-Name $state.accountName
	}
	else {
		Get-FoundryAccount `
			-Subscription $SubscriptionId `
			-Environment $EnvironmentName `
			-ResourceGroup $ResourceGroupName `
			-Name $AccountName
	}

	[pscustomobject]@{
		Environment = $EnvironmentName
		Account = "$($account.resourceGroup)/$($account.name)"
		PublicNetworkAccess = $account.properties.publicNetworkAccess
		DefaultAction = $account.properties.networkAcls.defaultAction
		IpRules = @($account.properties.networkAcls.ipRules | ForEach-Object { $_.value }) -join ', '
		ManagedStateExists = [bool] $state
		ManagedIpRules = if ($state) { @($state.addedIpRules) -join ', ' } else { '' }
	}
	return
}

if ($Action -eq 'Enable') {
	$account = if ($state) {
		Get-FoundryAccount `
			-Subscription $state.subscriptionId `
			-Environment $EnvironmentName `
			-ResourceGroup $state.resourceGroupName `
			-Name $state.accountName
	}
	else {
		Get-FoundryAccount `
			-Subscription $SubscriptionId `
			-Environment $EnvironmentName `
			-ResourceGroup $ResourceGroupName `
			-Name $AccountName
	}

	if ($state -and $state.resourceId -ne $account.id) {
		throw "State file '$statePath' belongs to '$($state.resourceId)', but '$($account.id)' was selected. Run Disable first or remove the stale state file after reviewing it."
	}

	if (-not $state) {
		$state = [pscustomobject] (Get-NetworkState -Account $account)
	}

	$resolvedIpAddress = if ($IpAddress) { $IpAddress } else { Get-PublicIpv4Address }
	$cidr = if ($resolvedIpAddress.Contains('/')) { $resolvedIpAddress } else { "$resolvedIpAddress/32" }
	$existingRules = @($account.properties.networkAcls.ipRules | ForEach-Object { $_.value })
	$ruleNeedsAdding = $cidr -notin $existingRules -and $resolvedIpAddress -notin $existingRules

	if (-not $PSCmdlet.ShouldProcess("$($account.resourceGroup)/$($account.name)", "Allow $cidr and enable selected-network public access")) {
		return
	}

	if ($ruleNeedsAdding) {
		$state.addedIpRules = @($state.addedIpRules) + $cidr | Select-Object -Unique
	}
	New-Item -ItemType Directory -Path $stateDirectory -Force | Out-Null
	$state | ConvertTo-Json -Depth 10 | Set-Content -Path $statePath -Encoding utf8

	if ($ruleNeedsAdding) {
		Invoke-ExternalCommand -Command 'az' -Arguments @(
			'cognitiveservices', 'account', 'network-rule', 'add',
			'--subscription', $SubscriptionId,
			'--resource-group', $account.resourceGroup,
			'--name', $account.name,
			'--ip-address', $cidr,
			'--output', 'none',
			'--only-show-errors'
		) | Out-Null
	}

	Set-FoundryNetworkProperties `
		-ResourceId $account.id `
		-PublicNetworkAccess 'Enabled' `
		-DefaultAction 'Deny'

	Write-Host "Allowed $cidr on Foundry account '$($account.name)'." -ForegroundColor Green
	Write-Host "Original network state is saved at '$statePath'."
	Write-Warning 'This egress IP might change. Run this script again to add a new address, and run with -Action Disable after the demo.'
	return
}

if (-not $state) {
	throw "No managed state was found at '$statePath'. Nothing can be restored safely."
}

if (-not $PSCmdlet.ShouldProcess("$($state.resourceGroupName)/$($state.accountName)", 'Remove managed IP rules and restore the original network configuration')) {
	return
}

# Close the public endpoint before removing allowlist entries.
Set-FoundryNetworkProperties `
	-ResourceId $state.resourceId `
	-PublicNetworkAccess 'Disabled' `
	-DefaultAction $state.originalDefaultAction

foreach ($managedRule in @($state.addedIpRules)) {
	Invoke-ExternalCommand -Command 'az' -Arguments @(
		'cognitiveservices', 'account', 'network-rule', 'remove',
		'--subscription', $state.subscriptionId,
		'--resource-group', $state.resourceGroupName,
		'--name', $state.accountName,
		'--ip-address', $managedRule,
		'--output', 'none',
		'--only-show-errors'
	) | Out-Null
}

Set-FoundryNetworkProperties `
	-ResourceId $state.resourceId `
	-PublicNetworkAccess $state.originalPublicNetworkAccess `
	-DefaultAction $state.originalDefaultAction

Remove-Item $statePath -Force
Write-Host "Restored the original network settings for Foundry account '$($state.accountName)'." -ForegroundColor Green
