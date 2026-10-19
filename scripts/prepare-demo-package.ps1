param(
    [string]$AppPath = '',
    [string]$OutputDirectory = '',
    [switch]$Build
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
if ($Build -and $AppPath) { throw 'Use either -Build or -AppPath, not both.' }
if (!$Build -and !$AppPath) { throw 'Use -Build or supply a published -AppPath.' }
if (!$OutputDirectory) {
    $OutputDirectory = Join-Path $repository ('local-data/shared-hosting-demo-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) {
    if (!(Test-Path -LiteralPath $output -PathType Container) -or @(Get-ChildItem -LiteralPath $output -Force).Count -ne 0) {
        throw "Demo output must be a new or empty folder: $output"
    }
}

Push-Location $repository
try {
    if ($Build) {
        Push-Location (Join-Path $repository 'ui')
        try {
            npm ci --ignore-scripts --no-audit --no-fund
            if ($LASTEXITCODE -ne 0) { throw 'Locked UI dependency installation failed.' }
            npm run build --ignore-scripts
            if ($LASTEXITCODE -ne 0) { throw 'Demo UI build failed.' }
        }
        finally { Pop-Location }
        dotnet restore src/TogetherServer/TogetherServer.csproj --locked-mode
        if ($LASTEXITCODE -ne 0) { throw 'Locked application restore failed.' }
        $publish = Join-Path $repository ('local-data/demo-publish-' + [Guid]::NewGuid().ToString('N'))
        dotnet publish src/TogetherServer/TogetherServer.csproj -c Release --no-restore -o $publish
        if ($LASTEXITCODE -ne 0) { throw 'Demo application publish failed.' }
        $AppPath = Join-Path $publish 'TogetherServer.exe'
    }
    $source = (Resolve-Path -LiteralPath $AppPath).Path
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $executable = Join-Path $output 'TogetherServer DEVELOPMENT DEMO.exe'
    Copy-Item -LiteralPath $source -Destination $executable

    # Static verification reads the single-file bundle without running or loading the app.
    $assembly = Join-Path $repository 'src/TogetherServer/bin/Release/net10.0-windows/win-x64/TogetherServer.dll'
    python (Join-Path $PSScriptRoot 'verify-demo-package.py') --app $executable --assembly $assembly --ui (Join-Path $repository 'ui/dist') --output (Join-Path $output 'package-evidence.json')
    if ($LASTEXITCODE -ne 0) { throw 'Static demo bundle verification failed.' }
    $hash = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash
    $signature = Get-AuthenticodeSignature -LiteralPath $executable
    $commit = (git rev-parse HEAD).Trim()
    @"
TogetherServer DEVELOPMENT DEMO

Double-click TogetherServer DEVELOPMENT DEMO.exe. Keep this exact filename.
This is a fake-data UI demo, with no real game servers, downloads, save transfers,
pairing, network checks or external links. Changes to fake servers last for this
session. Use the permanent demo bar to pick a scenario or reset the fake data.

The Servers list groups Your servers, Hosting for friends and Friend servers.
Choose your server > Backups > Sharing & hosting to change hosting permissions.
Choose Morgan's Meadow under Friend servers to try Host on this PC beside Start.
Prepare this PC simulates the checks; Start server on this PC simulates hosting.
The resulting local server is labeled Hosted for Morgan. The bar also offers
missing-server, offline-Host, checking-players and repeated-player-issue scenes.

The demo uses loopback port 5129 and its own %LOCALAPPDATA%\TogetherServer-Demo
folder. Production and ordinary development settings/worlds are not loaded.
Companion networking, updates, startup registration and game services are absent.
Close the demo with its native window Close button or tray Quit.

This package was built without launching an app or changing the running production
instance. Synthetic tests and static bundle verification do not establish native
painting, real game operation, save integrity or real network reachability.

Source commit: $commit
SHA-256: $hash
Authenticode: $($signature.Status)
"@ | Set-Content -LiteralPath (Join-Path $output 'README-DEMO.txt') -Encoding UTF8
    Write-Host "Demo package: $output"
    Write-Host "Double-click: $executable"
    Write-Host "SHA-256: $hash"
    Write-Host "Authenticode: $($signature.Status)"
    Write-Host 'The application was not launched. No production settings, credentials or worlds were included.'
}
finally { Pop-Location }
