<#
.SYNOPSIS
	Generates or installs Windows hosts entries from Azure Private DNS zones.

.DESCRIPTION
	Reads A records from Azure Private DNS zones, translates private-link zone
	names to the hostnames used by clients, and renders a managed hosts-file
	block. Install replaces only the block owned by this script. Remove deletes
	that block and preserves every unrelated hosts-file entry.

	By default, the script uses the selected azd environment to find the Azure
	subscription and platform resource group. Generate writes a preview under
	.azure/<environment> without requiring administrator privileges. Install and
	Remove must run from an elevated PowerShell session.

.PARAMETER Action
	Generate, Install, Remove, or Status. The default is Generate.

.PARAMETER EnvironmentName
	The azd environment to use. Defaults to the currently selected environment.

.PARAMETER SubscriptionId
	Optional Azure subscription override. Defaults to AZURE_SUBSCRIPTION_ID in
	the selected azd environment.

.PARAMETER ResourceGroupName
	Optional resource group containing the private DNS zones. Defaults to
	AZURE_PLATFORM_RESOURCE_GROUP in the selected azd environment.

.PARAMETER ZoneName
	Optional private DNS zone names to include. The default includes every
	private DNS zone in ResourceGroupName.

.PARAMETER OutputPath
	Generated hosts block path. Defaults to
	.azure/<environment>/private-dns-hosts.txt.

.PARAMETER HostsPath
	Hosts file to inspect or modify. Defaults to the Windows system hosts file.

.EXAMPLE
	./scripts/eng/sync-private-dns-hosts.ps1

.EXAMPLE
	./scripts/eng/sync-private-dns-hosts.ps1 -Action Install -WhatIf

.EXAMPLE
	./scripts/eng/sync-private-dns-hosts.ps1 -Action Install

.EXAMPLE
	./scripts/eng/sync-private-dns-hosts.ps1 -Action Remove

.EXAMPLE
	./scripts/eng/sync-private-dns-hosts.ps1 -Action Status
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
param(
	[ValidateSet('Generate', 'Install', 'Remove', 'Status')]
	[string] $Action = 'Generate',

	[string] $EnvironmentName,
	[string] $SubscriptionId,
	[string] $ResourceGroupName,
	[string[]] $ZoneName,
	[string] $OutputPath,
	[string] $HostsPath = (Join-Path $env:SystemRoot 'System32\drivers\etc\hosts')
)

$ErrorActionPreference = 'Stop'
$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$BlockStart = '# BEGIN agentic-ai-contact-center private DNS hosts'
$BlockEnd = '# END agentic-ai-contact-center private DNS hosts'

function Assert-Command {
	param([Parameter(Mandatory)] [string] $Name)

	if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
		throw "Required command '$Name' was not found on PATH."
	}
}

function Assert-Administrator {
	$systemHostsPath = Join-Path $env:SystemRoot 'System32\drivers\etc\hosts'
	if ([IO.Path]::GetFullPath($HostsPath) -ne [IO.Path]::GetFullPath($systemHostsPath)) {
		return
	}

	$currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
	$principal = [Security.Principal.WindowsPrincipal]::new($currentIdentity)
	if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
		throw "Action '$Action' requires an elevated PowerShell session. Run PowerShell as Administrator and try again."
	}
}

function Invoke-ExternalCommand {
	param(
		[Parameter(Mandatory)] [string] $Command,
		[Parameter(Mandatory)] [string[]] $Arguments
	)

	$errorPath = [IO.Path]::GetTempFileName()
	$previousWhatIfPreference = $WhatIfPreference
	try {
		$WhatIfPreference = $false
		$output = & $Command @Arguments 2> $errorPath
		$exitCode = $LASTEXITCODE
		$errorOutput = [IO.File]::ReadAllText($errorPath).Trim()
		if ($exitCode -ne 0) {
			throw "Command failed: $Command $($Arguments -join ' ')`n$errorOutput"
		}
	}
	finally {
		$WhatIfPreference = $previousWhatIfPreference
		[IO.File]::Delete($errorPath)
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

function Get-ClientZoneName {
	param([Parameter(Mandatory)] [string] $PrivateZoneName)

	$clientZoneOverrides = @{
		'privatelink.vaultcore.azure.net' = 'vault.azure.net'
	}
	if ($clientZoneOverrides.ContainsKey($PrivateZoneName)) {
		return $clientZoneOverrides[$PrivateZoneName]
	}

	# A private AKS API server uses its privatelink FQDN directly. Other zones
	# in this solution map back to the public service suffix used by clients.
	if ($PrivateZoneName -match '^privatelink\.[^.]+\.azmk8s\.io$') {
		return $PrivateZoneName
	}
	if ($PrivateZoneName.StartsWith('privatelink.', [StringComparison]::OrdinalIgnoreCase)) {
		return $PrivateZoneName.Substring('privatelink.'.Length)
	}

	return $PrivateZoneName
}

function Get-PrivateDnsHostsEntries {
	param(
		[Parameter(Mandatory)] [string] $Subscription,
		[Parameter(Mandatory)] [string] $ResourceGroup,
		[string[]] $RequestedZoneNames
	)

	$zoneJson = Invoke-ExternalCommand -Command 'az' -Arguments @(
		'network', 'private-dns', 'zone', 'list',
		'--subscription', $Subscription,
		'--resource-group', $ResourceGroup,
		'--output', 'json',
		'--only-show-errors'
	)
	$zones = @(($zoneJson -join "`n") | ConvertFrom-Json)

	if ($RequestedZoneNames) {
		$missingZones = @($RequestedZoneNames | Where-Object { $_ -notin $zones.name })
		if ($missingZones.Count -gt 0) {
			throw "Private DNS zone(s) not found in resource group '$ResourceGroup': $($missingZones -join ', ')"
		}
		$zones = @($zones | Where-Object { $_.name -in $RequestedZoneNames })
	}

	if ($zones.Count -eq 0) {
		throw "No Azure Private DNS zones were found in resource group '$ResourceGroup'."
	}

	$entries = foreach ($zone in ($zones | Sort-Object name)) {
		$recordJson = Invoke-ExternalCommand -Command 'az' -Arguments @(
			'network', 'private-dns', 'record-set', 'a', 'list',
			'--subscription', $Subscription,
			'--resource-group', $ResourceGroup,
			'--zone-name', $zone.name,
			'--output', 'json',
			'--only-show-errors'
		)
		$recordSets = @(($recordJson -join "`n") | ConvertFrom-Json)
		$clientZoneName = Get-ClientZoneName -PrivateZoneName $zone.name

		foreach ($recordSet in $recordSets) {
			$hostName = if ($recordSet.name -eq '@') {
				$clientZoneName
			}
			else {
				"$($recordSet.name).$clientZoneName"
			}

			foreach ($aRecord in @($recordSet.aRecords)) {
				if ($aRecord.ipv4Address) {
					[pscustomobject]@{
						IpAddress = $aRecord.ipv4Address
						HostName = $hostName.TrimEnd('.')
						PrivateZone = $zone.name
					}
				}
			}
		}
	}

	return @($entries | Sort-Object PrivateZone, HostName, IpAddress -Unique)
}

function New-HostsBlock {
	param(
		[Parameter(Mandatory)] [object[]] $Entries,
		[Parameter(Mandatory)] [string] $Environment
	)

	$lines = @(
		$BlockStart
		"# Environment: $Environment"
		"# Generated: $([DateTime]::UtcNow.ToString('o'))"
	)

	$currentZone = $null
	foreach ($entry in $Entries) {
		if ($entry.PrivateZone -ne $currentZone) {
			$lines += "# Zone: $($entry.PrivateZone)"
			$currentZone = $entry.PrivateZone
		}
		$lines += '{0,-15} {1}' -f $entry.IpAddress, $entry.HostName
	}

	$lines += $BlockEnd
	return $lines -join [Environment]::NewLine
}

function Remove-ManagedBlock {
	param([Parameter(Mandatory)] [string] $Content)

	$escapedStart = [regex]::Escape($BlockStart)
	$escapedEnd = [regex]::Escape($BlockEnd)
	$pattern = "(?ms)^$escapedStart\r?\n.*?^$escapedEnd(?:\r?\n)?"
	$matches = [regex]::Matches($Content, $pattern)
	if ($matches.Count -gt 1) {
		throw "The hosts file contains $($matches.Count) managed blocks. Remove the duplicates manually before continuing."
	}

	return [pscustomobject]@{
		Content = ([regex]::Replace($Content, $pattern, '')).TrimEnd("`r", "`n")
		Found = $matches.Count -eq 1
	}
}

function Write-Utf8File {
	param(
		[Parameter(Mandatory)] [string] $Path,
		[Parameter(Mandatory)] [AllowEmptyString()] [string] $Content
	)

	$utf8WithoutBom = [Text.UTF8Encoding]::new($false)
	[IO.File]::WriteAllText($Path, $Content, $utf8WithoutBom)
}

if ($Action -in @('Install', 'Remove') -and -not $IsWindows) {
	throw "Action '$Action' is supported only on Windows. Use Generate to create entries for another platform."
}

if ($Action -eq 'Remove') {
	if (-not $WhatIfPreference) {
		Assert-Administrator
	}
	if (-not (Test-Path $HostsPath)) {
		throw "Hosts file not found: $HostsPath"
	}

	$hostsContent = [IO.File]::ReadAllText($HostsPath)
	$withoutManagedBlock = Remove-ManagedBlock -Content $hostsContent
	if (-not $withoutManagedBlock.Found) {
		Write-Host 'No managed private DNS hosts block is installed.'
		return
	}

	if ($PSCmdlet.ShouldProcess($HostsPath, 'Remove the managed private DNS hosts block')) {
		$restoredContent = if ($withoutManagedBlock.Content) {
			$withoutManagedBlock.Content + [Environment]::NewLine
		}
		else {
			''
		}
		Write-Utf8File -Path $HostsPath -Content $restoredContent
		Clear-DnsClientCache
		Write-Host "Removed the managed private DNS hosts block from '$HostsPath'." -ForegroundColor Green
	}
	return
}

if ($Action -eq 'Status') {
	if (-not (Test-Path $HostsPath)) {
		throw "Hosts file not found: $HostsPath"
	}

	$hostsContent = [IO.File]::ReadAllText($HostsPath)
	$managedBlock = [regex]::Match(
		$hostsContent,
		"(?ms)^$([regex]::Escape($BlockStart))\r?\n.*?^$([regex]::Escape($BlockEnd))$"
	)
	[pscustomobject]@{
		HostsPath = $HostsPath
		Installed = $managedBlock.Success
		EntryCount = if ($managedBlock.Success) {
			@($managedBlock.Value -split '\r?\n' | Where-Object { $_ -match '^\s*\d{1,3}(?:\.\d{1,3}){3}\s+\S+' }).Count
		}
		else {
			0
		}
	}
	return
}

Assert-Command -Name 'az'
Assert-Command -Name 'azd'

if (-not $EnvironmentName) {
	$EnvironmentName = Get-AzdValue -Name 'AZURE_ENV_NAME'
}
if (-not $SubscriptionId) {
	$SubscriptionId = Get-AzdValue -Name 'AZURE_SUBSCRIPTION_ID' -Environment $EnvironmentName
}
if (-not $ResourceGroupName) {
	$ResourceGroupName = Get-AzdValue -Name 'AZURE_PLATFORM_RESOURCE_GROUP' -Environment $EnvironmentName
}
if (-not $OutputPath) {
	$OutputPath = Join-Path $RepositoryRoot ".azure\$EnvironmentName\private-dns-hosts.txt"
}

Invoke-ExternalCommand -Command 'az' -Arguments @(
	'account', 'show',
	'--subscription', $SubscriptionId,
	'--output', 'none',
	'--only-show-errors'
) | Out-Null

$entries = Get-PrivateDnsHostsEntries `
	-Subscription $SubscriptionId `
	-ResourceGroup $ResourceGroupName `
	-RequestedZoneNames $ZoneName

if ($entries.Count -eq 0) {
	throw "The selected private DNS zones contain no IPv4 A records."
}

$hostsBlock = New-HostsBlock -Entries $entries -Environment $EnvironmentName

if ($Action -eq 'Generate') {
	if ($PSCmdlet.ShouldProcess($OutputPath, "Generate $($entries.Count) private DNS hosts entries")) {
		$outputDirectory = Split-Path $OutputPath -Parent
		if ($outputDirectory) {
			New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
		}
		Write-Utf8File -Path $OutputPath -Content ($hostsBlock + [Environment]::NewLine)
		Write-Host "Generated $($entries.Count) hosts entries at '$OutputPath'." -ForegroundColor Green
	}
	return
}

if (-not $WhatIfPreference) {
	Assert-Administrator
}
if (-not (Test-Path $HostsPath)) {
	throw "Hosts file not found: $HostsPath"
}

$hostsContent = [IO.File]::ReadAllText($HostsPath)
$withoutManagedBlock = Remove-ManagedBlock -Content $hostsContent
$newContent = if ($withoutManagedBlock.Content) {
	$withoutManagedBlock.Content + [Environment]::NewLine + [Environment]::NewLine + $hostsBlock + [Environment]::NewLine
}
else {
	$hostsBlock + [Environment]::NewLine
}

if ($PSCmdlet.ShouldProcess($HostsPath, "Install $($entries.Count) private DNS hosts entries")) {
	Write-Utf8File -Path $HostsPath -Content $newContent
	Clear-DnsClientCache
	Write-Host "Installed $($entries.Count) private DNS hosts entries in '$HostsPath'." -ForegroundColor Green
}