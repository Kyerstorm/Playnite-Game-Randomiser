<#
.SYNOPSIS
    Validates the extension's release metadata and builds the Playnite package (.pext).

.DESCRIPTION
    A .pext is a zip of the extension folder. This script checks that the version and id agree
    everywhere they are written down, builds the plugin, packs exactly the files Playnite needs
    into artifacts/, and then re-opens the package to verify what is inside it.

    Works in Windows PowerShell 5.1 and PowerShell 7. Needs only the .NET SDK.

.EXAMPLE
    ./build/pack.ps1                        # validate, build, pack, verify
    ./build/pack.ps1 -NoBuild               # pack what is already built
    ./build/pack.ps1 -ValidateOnly          # metadata checks only
    ./build/pack.ps1 -ExpectedVersion v1.1.0   # also require this release tag to match
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$OutputDir,
    [string]$ExpectedVersion,
    [switch]$NoBuild,
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

$root = Split-Path -Parent $PSScriptRoot
if (-not $OutputDir) { $OutputDir = Join-Path $root 'artifacts' }
$pluginDir = Join-Path $root 'src/GameRandomiser'
$binDir = Join-Path $pluginDir "bin/$Configuration"

# The complete contents of a release package. Anything else in the package is an error.
$required = @('extension.yaml', 'icon.png', 'GameRandomiser.dll', 'GameRandomiser.Core.dll')
$optional = @('GameRandomiser.pdb', 'GameRandomiser.Core.pdb')

$problems = New-Object System.Collections.Generic.List[string]
function Fail([string]$message) { $script:problems.Add($message) }
function Stop-IfProblems([string]$stage) {
    if ($script:problems.Count -gt 0) {
        Write-Host "$stage failed:" -ForegroundColor Red
        $script:problems | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
        exit 1
    }
    Write-Host "$stage OK" -ForegroundColor Green
}

function Read-YamlValue([string]$text, [string]$key) {
    $match = [regex]::Match($text, "(?m)^$key\s*:\s*`"?([^`"\r\n]+?)`"?\s*$")
    if ($match.Success) { return $match.Groups[1].Value.Trim() }
    return $null
}

# ---- 1. Metadata ----------------------------------------------------------------------------

$manifestPath = Join-Path $pluginDir 'extension.yaml'
$manifest = Get-Content $manifestPath -Raw
$id = Read-YamlValue $manifest 'Id'
$manifestVersion = Read-YamlValue $manifest 'Version'
$module = Read-YamlValue $manifest 'Module'
$icon = Read-YamlValue $manifest 'Icon'

if (-not $id) { Fail 'extension.yaml has no Id.' }
if (-not $module) { Fail 'extension.yaml has no Module.' }
if (-not $manifestVersion -or $manifestVersion -notmatch '^\d+\.\d+(\.\d+)?$') {
    Fail "extension.yaml Version '$manifestVersion' is not a version number (expected e.g. 1.1)."
}

[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props') -Raw
$assemblyVersion = ($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
if (-not $assemblyVersion) { Fail 'Directory.Build.props has no <Version>.' }

if ($problems.Count -eq 0) {
    # extension.yaml carries "1.1"; assemblies carry "1.1.0". They must describe the same release.
    $normalised = [version]$manifestVersion
    $patch = [Math]::Max(0, $normalised.Build)
    $manifestFull = "$($normalised.Major).$($normalised.Minor).$patch"
    if ($manifestFull -ne $assemblyVersion) {
        Fail "Version mismatch: extension.yaml says $manifestVersion ($manifestFull) but Directory.Build.props says $assemblyVersion."
    }

    if ($ExpectedVersion) {
        $tag = $ExpectedVersion -replace '^refs/tags/', '' -replace '^v', ''
        if ($tag -ne $assemblyVersion) {
            Fail "Release tag '$ExpectedVersion' does not match the project version $assemblyVersion."
        }
    }

    # The plugin's Guid, the manifest id and the addon database entry must all name the same extension.
    $pluginSource = Get-Content (Join-Path $pluginDir 'GameRandomiserPlugin.cs') -Raw
    $guid = [regex]::Match($pluginSource, 'Guid\.Parse\("([0-9a-fA-F-]{36})"\)')
    if (-not $guid.Success) { Fail 'Could not find the plugin Guid in GameRandomiserPlugin.cs.' }
    elseif ($id -notlike "*$($guid.Groups[1].Value)") { Fail "extension.yaml Id '$id' does not end with the plugin Guid $($guid.Groups[1].Value)." }

    $installerPath = Join-Path $root 'installer.yaml'
    if (Test-Path $installerPath) {
        $addonId = Read-YamlValue (Get-Content $installerPath -Raw) 'AddonId'
        if ($addonId -ne $id) { Fail "installer.yaml AddonId '$addonId' does not match extension.yaml Id '$id'." }
    }

    if ($module -ne 'GameRandomiser.dll') { Fail "extension.yaml Module is '$module'; expected GameRandomiser.dll." }
    if ($icon -and -not (Test-Path (Join-Path $pluginDir $icon))) { Fail "extension.yaml Icon '$icon' does not exist." }
}

Stop-IfProblems 'Metadata validation'
Write-Host "  $id  version $manifestVersion (assemblies $assemblyVersion)"
if ($ValidateOnly) { exit 0 }

# ---- 2. Build -------------------------------------------------------------------------------

if (-not $NoBuild) {
    & dotnet build (Join-Path $pluginDir 'GameRandomiser.csproj') -c $Configuration -nologo -v minimal
    if ($LASTEXITCODE -ne 0) { Write-Host 'Build failed.' -ForegroundColor Red; exit 1 }
}

foreach ($file in $required) {
    if (-not (Test-Path (Join-Path $binDir $file))) { Fail "Build output is missing $file (looked in $binDir)." }
}
Stop-IfProblems 'Build output check'

# ---- 3. Pack --------------------------------------------------------------------------------

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

New-Item -ItemType Directory -Force $OutputDir | Out-Null
$packageName = '{0}_{1}.pext' -f $id, ($manifestVersion -replace '\.', '_')
$packagePath = Join-Path $OutputDir $packageName
if (Test-Path $packagePath) { Remove-Item $packagePath -Force }

# Stage only the allow-listed files, so nothing else in bin/ can leak into a release.
$stage = Join-Path ([System.IO.Path]::GetTempPath()) ("GameRandomiserPack_" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $stage | Out-Null
try {
    foreach ($file in ($required + $optional)) {
        $source = Join-Path $binDir $file
        if (Test-Path $source) { Copy-Item $source (Join-Path $stage $file) }
    }
    [System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $packagePath, [System.IO.Compression.CompressionLevel]::Optimal, $false)
}
finally {
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
}

# ---- 4. Verify the package ------------------------------------------------------------------

$verify = Join-Path ([System.IO.Path]::GetTempPath()) ("GameRandomiserVerify_" + [guid]::NewGuid().ToString('N'))
try {
    [System.IO.Compression.ZipFile]::ExtractToDirectory($packagePath, $verify)
    $verifyRoot = (Get-Item $verify).FullName
    $entries = @(Get-ChildItem $verifyRoot -Recurse -File | ForEach-Object { $_.FullName.Substring($verifyRoot.Length + 1) })

    foreach ($file in $required) {
        if ($entries -notcontains $file) { Fail "Package is missing $file." }
    }
    foreach ($entry in $entries) {
        if (($required + $optional) -notcontains $entry) { Fail "Package contains an unexpected file: $entry." }
        if ($entry -match '(?i)(Tests?|Harness|Moq|xunit|Playnite\.SDK|Newtonsoft)') { Fail "Package contains a development or host-provided file: $entry." }
    }

    if ($problems.Count -eq 0) {
        $packedManifest = Get-Content (Join-Path $verifyRoot 'extension.yaml') -Raw
        if ((Read-YamlValue $packedManifest 'Version') -ne $manifestVersion) { Fail 'Packaged extension.yaml has a different version from the source manifest (stale build output?).' }
        if ((Read-YamlValue $packedManifest 'Id') -ne $id) { Fail 'Packaged extension.yaml has a different Id from the source manifest.' }

        foreach ($dll in @('GameRandomiser.dll', 'GameRandomiser.Core.dll')) {
            $built = [System.Reflection.AssemblyName]::GetAssemblyName((Join-Path $verifyRoot $dll)).Version
            $builtText = "$($built.Major).$($built.Minor).$($built.Build)"
            if ($builtText -ne $assemblyVersion) { Fail "$dll is version $builtText; expected $assemblyVersion (stale build output?)." }
        }
    }
}
finally {
    Remove-Item $verify -Recurse -Force -ErrorAction SilentlyContinue
}

Stop-IfProblems 'Package validation'
$size = [Math]::Round((Get-Item $packagePath).Length / 1KB, 1)
Write-Host "  $packagePath ($size KB, $($entries.Count) files)"
