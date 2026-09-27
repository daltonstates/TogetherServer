param(
    [string]$AppPath = '',
    [string]$OutputDirectory = '',
    [string]$CompilerPath = '',
    [string]$SigningCertificateThumbprint = $env:TOGETHERSERVER_SIGNING_THUMBPRINT,
    [string]$SignToolPath = '',
    [uri]$TimestampUrl = 'https://timestamp.digicert.com'
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
if (!$AppPath) { $AppPath = Join-Path $repository 'local-data/release-candidate/TogetherServer.exe' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repository 'local-data/installer' }
$app = (Resolve-Path -LiteralPath $AppPath).Path
$script = (Resolve-Path -LiteralPath (Join-Path $repository 'installer/TogetherServer.iss')).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
$project = [xml](Get-Content -LiteralPath (Join-Path $repository 'src/TogetherServer/TogetherServer.csproj') -Raw)
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'TogetherServer.csproj needs a stable MAJOR.MINOR.PATCH version.' }
$appInfo = (Get-Item -LiteralPath $app).VersionInfo
if ($appInfo.FileVersion -ne "$version.0") {
    throw "App file version $($appInfo.FileVersion) does not match project version $version."
}

if ($CompilerPath) { $compiler = (Resolve-Path -LiteralPath $CompilerPath).Path }
else
{
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { $compiler = $command.Source }
    else
    {
        $candidates = @(
            (Join-Path $env:LOCALAPPDATA 'Programs/Inno Setup 7/ISCC.exe'),
            (Join-Path $env:LOCALAPPDATA 'Programs/Inno Setup 6/ISCC.exe'),
            (Join-Path $env:ProgramFiles 'Inno Setup 7/ISCC.exe'),
            (Join-Path $env:ProgramFiles 'Inno Setup 6/ISCC.exe'),
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 7/ISCC.exe'),
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe')
        ) | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) }
        $compiler = $candidates | Select-Object -First 1
    }
}
if (!$compiler) {
    throw 'Inno Setup compiler was not found. Install it with: winget install --id JRSoftware.InnoSetup -e -s winget'
}

New-Item -ItemType Directory -Path $output -Force | Out-Null
& $compiler /Qp "/DAppSource=$app" "/DAppVersion=$version" "/DOutputDirectory=$output" $script
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }

$installer = Join-Path $output 'TogetherServer-Setup-win-x64.exe'
if (!(Test-Path -LiteralPath $installer -PathType Leaf)) { throw "Installer was not created: $installer" }
$installerInfo = (Get-Item -LiteralPath $installer).VersionInfo
if ([Version]$installerInfo.FileVersion -ne [Version]$version) {
    throw "Installer file version $($installerInfo.FileVersion) does not match $version."
}

$thumbprint = ($SigningCertificateThumbprint -replace '\s', '').ToUpperInvariant()
if ($thumbprint)
{
    if ($thumbprint -notmatch '^[0-9A-F]{40}([0-9A-F]{24})?$') {
        throw 'The supplied code-signing certificate thumbprint is invalid.'
    }
    if ($SignToolPath) { $signTool = (Resolve-Path -LiteralPath $SignToolPath).Path }
    else
    {
        $signCommand = Get-Command signtool.exe -ErrorAction SilentlyContinue
        if ($signCommand) { $signTool = $signCommand.Source }
        else
        {
            $sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
            $signTool = Get-ChildItem -LiteralPath $sdkRoot -Filter signtool.exe -File -Recurse -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -match '[\\/]x64[\\/]signtool\.exe$' } |
                Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
        }
    }
    if (!$signTool) { throw 'signtool.exe was not found. Install the Windows SDK or pass -SignToolPath.' }
    & $signTool sign /sha1 $thumbprint /s My /fd SHA256 /td SHA256 /tr $TimestampUrl.AbsoluteUri $installer
    if ($LASTEXITCODE -ne 0) { throw 'Installer Authenticode signing failed.' }
}

$signature = Get-AuthenticodeSignature -LiteralPath $installer
if ($thumbprint)
{
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $thumbprint) {
        throw "Installer signing did not produce the requested valid signature; status was $($signature.Status)."
    }
}
elseif ($signature.Status -ne 'NotSigned') {
    throw "Unsigned installer has an unexpected Authenticode state: $($signature.Status)."
}

$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
$checksum = "$installer.sha256"
Set-Content -LiteralPath $checksum -Value "$hash  $([IO.Path]::GetFileName($installer))" -Encoding ascii
Write-Host "Built $(if ($thumbprint) { 'signed' } else { 'unsigned' }) installer: $installer"
Write-Host "SHA-256: $hash"
Write-Host "Authenticode: $($signature.Status)"
