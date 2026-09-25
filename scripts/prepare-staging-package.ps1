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

$allowed = @(
    'TogetherServer.exe',
    'Start TogetherServer STAGING Host.cmd',
    'Start TogetherServer STAGING Friend.cmd',
    'README-STAGING.txt'
)
$unexpected = @(Get-ChildItem -LiteralPath $output -Force | Where-Object Name -NotIn $allowed)
if ($unexpected.Count -gt 0) {
    throw "Refusing to mix the staging package with other files: $($unexpected[0].FullName)"
}

Copy-Item -LiteralPath $appPath -Destination (Join-Path $output 'TogetherServer.exe') -Force
foreach ($name in $allowed | Where-Object { $_ -ne 'TogetherServer.exe' }) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "staging/$name") -Destination (Join-Path $output $name) -Force
}
$hash = (Get-FileHash -LiteralPath (Join-Path $output 'TogetherServer.exe') -Algorithm SHA256).Hash
$signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $output 'TogetherServer.exe')
Write-Host "Staging package: $output"
Write-Host "SHA-256: $hash"
Write-Host "Authenticode: $($signature.Status)"
Write-Host 'No settings, credentials, runs, or world saves were included.'
