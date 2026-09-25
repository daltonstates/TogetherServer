param(
    [string]$AppPath = '',
    [string]$OutputDirectory = '',
    [switch]$Build
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
if ($Build -and $AppPath) { throw 'Use either -Build or -AppPath, not both.' }
if ($Build) {
    & (Join-Path $PSScriptRoot 'build.ps1') -ReleaseName release-candidate
    if ($LASTEXITCODE -ne 0) { throw 'Release-candidate build failed.' }
}
if (!$AppPath) { $AppPath = Join-Path $repository 'local-data/release-candidate/TogetherServer.exe' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repository 'local-data/staging-package' }
$appPath = (Resolve-Path -LiteralPath $AppPath).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null

$developmentExecutable = 'TogetherServer DEVELOPMENT.exe'
$legacyNames = @(
    'TogetherServer.exe',
    'Start TogetherServer STAGING Host.cmd',
    'Start TogetherServer STAGING Friend.cmd',
    'README-STAGING.txt'
)
$allowed = @(
    $developmentExecutable,
    'Start TogetherServer DEVELOPMENT Host.cmd',
    'Start TogetherServer DEVELOPMENT Friend.cmd',
    'README-DEVELOPMENT.txt'
)
$recognized = @($allowed) + @($legacyNames)
$unexpected = @(Get-ChildItem -LiteralPath $output -Force | Where-Object Name -NotIn $recognized)
if ($unexpected.Count -gt 0) {
    throw "Refusing to mix the staging package with other files: $($unexpected[0].FullName)"
}
foreach ($name in $legacyNames) {
    $legacyPath = Join-Path $output $name
    if (Test-Path -LiteralPath $legacyPath -PathType Leaf) { Remove-Item -LiteralPath $legacyPath -Force }
}

Copy-Item -LiteralPath $appPath -Destination (Join-Path $output $developmentExecutable) -Force
foreach ($name in $allowed | Where-Object { $_ -ne $developmentExecutable }) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "staging/$name") -Destination (Join-Path $output $name) -Force
}
$developmentPath = Join-Path $output $developmentExecutable
$hash = (Get-FileHash -LiteralPath $developmentPath -Algorithm SHA256).Hash
$signature = Get-AuthenticodeSignature -LiteralPath $developmentPath
Write-Host "Staging package: $output"
Write-Host "Double-click: $developmentPath"
Write-Host "SHA-256: $hash"
Write-Host "Authenticode: $($signature.Status)"
Write-Host 'No settings, credentials, runs, or world saves were included.'
