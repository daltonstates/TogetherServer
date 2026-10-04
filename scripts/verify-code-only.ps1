$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot

function Invoke-Checked([string]$Name, [scriptblock]$Command) {
    Write-Host "`n== $Name =="
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE." }
}

Push-Location $repository
try {
    Write-Host 'Code-only verification: no packaged app or game process is started.'
    Write-Host 'SKIP packaged app, installer, update handoff, and staging journeys.'
    Write-Host 'SKIP process, fixture, console, browser, and listener journeys.'
    Write-Host 'SKIP real-game, Friend-PC, and public-network acceptance.'

    $requiredSdk = [string]((Get-Content -LiteralPath 'global.json' -Raw | ConvertFrom-Json).sdk.version)
    $actualSdk = (dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $actualSdk -ne $requiredSdk) {
        throw "Expected .NET SDK $requiredSdk from global.json; found $actualSdk."
    }
    $requiredNode = (Get-Content -LiteralPath '.node-version' -Raw).Trim().TrimStart('v')
    $actualNode = (node --version).Trim().TrimStart('v')
    if ($LASTEXITCODE -ne 0 -or $actualNode -ne $requiredNode) {
        throw "Expected Node $requiredNode from .node-version; found $actualNode."
    }
    Write-Host "Pinned tools: .NET $actualSdk, Node $actualNode"
    $project = [xml](Get-Content -LiteralPath 'src/TogetherServer/TogetherServer.csproj' -Raw)
    $uiManifest = Get-Content -LiteralPath 'ui/package.json' -Raw | ConvertFrom-Json
    if ([string]$uiManifest.version -ne [string]$project.Project.PropertyGroup.Version) {
        throw 'UI package version does not match application version.'
    }
    Write-Host 'PASS application and UI version alignment.'

    Push-Location ui
    try {
        Invoke-Checked 'Locked UI dependency install (install scripts disabled)' { npm ci --ignore-scripts --no-audit --no-fund }
        Invoke-Checked 'UI TypeScript typecheck' { & (Join-Path (Get-Location) 'node_modules/.bin/tsc.cmd') --noEmit }
        Invoke-Checked 'UI lint' { npm run lint --ignore-scripts }
    }
    finally { Pop-Location }

    Invoke-Checked 'Locked .NET solution restore' { dotnet restore TogetherServer.slnx --locked-mode }
    Invoke-Checked '.NET solution compilation (no UI package or app launch)' {
        dotnet build TogetherServer.slnx -c Release --no-restore -p:CodeOnlyVerification=true
    }
    Invoke-Checked 'Git whitespace check' { git diff HEAD --check }

    Invoke-Checked 'Desktop diagnostic isolation' {
        $diagnosticOutput = [IO.Path]::GetFullPath((Join-Path $repository 'src/TogetherServer/DiagnosticOutput.cs'))
        $offenders = @(Get-ChildItem -LiteralPath (Join-Path $repository 'src/TogetherServer') -Filter '*.cs' -File -Recurse |
            Where-Object { [IO.Path]::GetFullPath($_.FullName) -ne $diagnosticOutput } |
            Select-String -Pattern '\bConsole\.' -CaseSensitive)
        if ($offenders.Count -gt 0) {
            $locations = $offenders | ForEach-Object { "$($_.Path):$($_.LineNumber)" }
            throw "Desktop code bypasses DiagnosticOutput: $($locations -join ', ')"
        }
    }
    Write-Host "`nPASS code-only verification; no app or fixture was launched by this script."
}
finally { Pop-Location }
