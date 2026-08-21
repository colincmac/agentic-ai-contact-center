<#!
.SYNOPSIS
Assemble composed Grafana dashboards from the shared panel library + manifest.

.DESCRIPTION
Reads manifest.json (dashboards -> rows -> panel refs), loads each referenced panel from the
panels/ library exactly once per dashboard, injects the shared template variables + annotations,
lays panels out (computes gridPos.y and row markers), assigns panel ids, and writes one dashboard
JSON per entry under the manifest's outputDirectory (generated/).

This is the inverse of the reference split-dashboard.ps1: instead of exploding ONE dashboard into
panels, it composes MANY dashboards from ONE reusable panel library. The same panel file can be
referenced by any number of dashboards.

Panels are matched to library files by their "ref" (path under panels/, without .json). A panel
file's "__meta" documentation key is stripped on assembly. gridPos in the manifest carries only
h/w/x; y is derived by a simple flow layout where a panel with x == 0 (after the first) starts a
new visual line.

.PARAMETER ManifestPath
Path to manifest.json. Defaults to the manifest next to this script's parent (Dashboards/manifest.json).

.PARAMETER OutputDirectory
Override the manifest's outputDirectory.

.EXAMPLE
./assemble-dashboards.ps1

.EXAMPLE
./assemble-dashboards.ps1 -ManifestPath ./manifest.json
#>
[CmdletBinding()] param(
    [string]$ManifestPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'manifest.json'),
    [string]$OutputDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Info($m) { Write-Host "[INFO] $m" }
function Fail($m) { throw $m }

if (-not (Test-Path -LiteralPath $ManifestPath)) { Fail "Manifest not found: $ManifestPath" }
$manifestDir = Split-Path -Parent (Resolve-Path -LiteralPath $ManifestPath)
Info "Manifest: $ManifestPath"

$manifest = (Get-Content -LiteralPath $ManifestPath -Raw) | ConvertFrom-Json -Depth 100

$panelLibraryDir = Join-Path $manifestDir $manifest.panelLibrary
if (-not (Test-Path -LiteralPath $panelLibraryDir)) { Fail "Panel library not found: $panelLibraryDir" }

$sharedVarsPath = Join-Path $manifestDir $manifest.sharedTemplating
if (-not (Test-Path -LiteralPath $sharedVarsPath)) { Fail "Shared variables not found: $sharedVarsPath" }
$sharedVars = (Get-Content -LiteralPath $sharedVarsPath -Raw) | ConvertFrom-Json -Depth 100

$outDir = if ($OutputDirectory) { $OutputDirectory } else { Join-Path $manifestDir $manifest.outputDirectory }
if (-not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }

function Import-Panel([string]$ref) {
    $panelPath = Join-Path $panelLibraryDir ($ref + '.json')
    if (-not (Test-Path -LiteralPath $panelPath)) { Fail "Panel ref '$ref' not found: $panelPath" }
    $panel = (Get-Content -LiteralPath $panelPath -Raw) | ConvertFrom-Json -Depth 100
    if ($panel.PSObject.Properties.Name -contains '__meta') { $panel.PSObject.Properties.Remove('__meta') }
    return $panel
}

$dashboardCount = 0
foreach ($dash in $manifest.dashboards) {
    $panelId = 0
    $cursorY = 0
    $panels = New-Object System.Collections.Generic.List[object]

    foreach ($row in $dash.rows) {
        $panelId++
        $rowPanel = [ordered]@{
            id        = $panelId
            type      = 'row'
            title     = $row.title
            collapsed = $false
            gridPos   = [ordered]@{ h = 1; w = 24; x = 0; y = $cursorY }
            panels    = @()
        }
        $panels.Add([PSCustomObject]$rowPanel) | Out-Null
        $cursorY += 1

        $lineMaxH = 0
        $first = $true
        foreach ($p in $row.panels) {
            $h = [int]$p.gridPos.h
            $w = [int]$p.gridPos.w
            $x = [int]$p.gridPos.x
            if (-not $first -and $x -eq 0) {
                $cursorY += $lineMaxH
                $lineMaxH = 0
            }
            $first = $false

            $panelObj = Import-Panel $p.ref
            $panelId++
            $panelObj | Add-Member -MemberType NoteProperty -Name id -Value $panelId -Force
            $panelObj | Add-Member -MemberType NoteProperty -Name gridPos -Value ([ordered]@{ h = $h; w = $w; x = $x; y = $cursorY }) -Force
            $panels.Add($panelObj) | Out-Null

            if ($h -gt $lineMaxH) { $lineMaxH = $h }
        }
        $cursorY += $lineMaxH
    }

    $templating = [ordered]@{ list = $sharedVars.list }
    if ($dash.PSObject.Properties.Name -contains 'extraVars' -and $dash.extraVars) {
        $templating.list = @($sharedVars.list) + @($dash.extraVars)
    }

    $refresh = if ($dash.PSObject.Properties.Name -contains 'refresh') { $dash.refresh } else { '' }

    $root = [ordered]@{
        annotations   = $manifest.annotations
        editable      = $true
        graphTooltip  = 1
        links         = @()
        panels        = $panels
        refresh       = $refresh
        schemaVersion = 39
        tags          = $dash.tags
        templating    = $templating
        time          = $dash.time
        timezone      = 'browser'
        title         = $dash.title
        uid           = $dash.uid
        version       = 1
    }

    $outPath = Join-Path $outDir ($dash.uid + '.json')
    ($root | ConvertTo-Json -Depth 100) | Out-File -FilePath $outPath -Encoding UTF8
    Info "Wrote $outPath ($($panels.Count) panels)"
    $dashboardCount++
}

Info "Assembled $dashboardCount dashboard(s) into $outDir"
