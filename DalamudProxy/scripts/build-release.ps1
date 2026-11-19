param(
    [string]$Configuration = 'Release',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$repositoryRoot = Split-Path -Parent $root
Set-Location -LiteralPath $root

if ([string]::IsNullOrWhiteSpace($env:DALAMUD_HOME)) {
    $cnDev = Join-Path $env:APPDATA 'XIVLauncherCN\addon\Hooks\dev'
    $globalDev = Join-Path $env:APPDATA 'XIVLauncher\addon\Hooks\dev'
    if (Test-Path -LiteralPath $cnDev) {
        $env:DALAMUD_HOME = $cnDev
    }
    elseif (Test-Path -LiteralPath $globalDev) {
        $env:DALAMUD_HOME = $globalDev
    }
}

Write-Host "DALAMUD_HOME=$env:DALAMUD_HOME"
dotnet restore .\DalamudProxy.sln
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed" }

dotnet clean .\DalamudProxy.sln -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "dotnet clean failed" }

if (-not $SkipTests) {
    dotnet test .\DalamudProxy.sln -c $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw "dotnet test failed" }
}

dotnet build .\DalamudProxy.sln -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }


$zipCandidates = @(
    (Join-Path $root "src\DalamudProxy\bin\x64\$Configuration\DalamudProxy\latest.zip"),
    (Join-Path $root "src\DalamudProxy\bin\$Configuration\DalamudProxy\latest.zip")
)

$sourceZip = $zipCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $sourceZip) {
    throw "DalamudPackager did not produce latest.zip. Checked: $($zipCandidates -join ', ')"
}

$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
$targetZip = Join-Path $dist 'DalamudProxy.zip'
Copy-Item -LiteralPath $sourceZip -Destination $targetZip -Force

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($targetZip)
try {
    $manifestEntry = $archive.GetEntry('DalamudProxy.json')
    if (-not $manifestEntry) { throw 'Release zip is missing DalamudProxy.json' }
    $reader = [System.IO.StreamReader]::new($manifestEntry.Open())
    try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    $repoRaw = Get-Content -LiteralPath (Join-Path $repositoryRoot 'repo.json') -Raw
    $repoTrimmed = $repoRaw.Trim()
    if (-not $repoTrimmed.StartsWith('[') -or -not $repoTrimmed.EndsWith(']')) {
        throw "repo.json top-level value must be a JSON array"
    }

    $repoEntries = @($repoRaw | ConvertFrom-Json)
    $repo = $repoEntries | Where-Object InternalName -eq $manifest.InternalName | Select-Object -First 1
    if (-not $repo) { throw "repo.json does not contain $($manifest.InternalName)" }
    if ($manifest.InternalName -ne $repo.InternalName -or $manifest.AssemblyVersion -ne $repo.AssemblyVersion -or $manifest.DalamudApiLevel -ne $repo.DalamudApiLevel) {
        throw "Manifest and repo.json metadata mismatch"
    }

    $names = $archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') }
    foreach ($required in @('DalamudProxy.dll', 'DalamudProxy.json', 'DalamudProxy.Core.dll', 'System.Security.Cryptography.ProtectedData.dll')) {
        if ($names -notcontains $required) {
            throw "Release zip is missing required entry: $required"
        }
    }

    if ($names -contains '0Harmony.dll' -or $names -contains 'libs/0Harmony.dll') {
        throw "Release zip must not bundle 0Harmony.dll; Dalamud provides it."
    }

}
finally {
    $archive.Dispose()
}

$hash = (Get-FileHash -LiteralPath $targetZip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath (Join-Path $dist 'DalamudProxy.zip.sha256') -Value "$hash  DalamudProxy.zip" -Encoding ascii

Write-Host "Created $targetZip"
Write-Host "SHA256 $hash"
