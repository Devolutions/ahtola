#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$PackageDirectory = './artifacts/managed-packages',
    [string]$PackageVersion = '0.0.0-browser-local',
    [string]$Output = './artifacts/browser-wasm-consumer',
    [int]$Port = 8124,
    [string]$BrowserExecutable,
    [switch]$RunAot
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'samples/BrowserWasmConsumer/BrowserWasmConsumer.csproj'
$serverScript = Join-Path $repoRoot 'samples/BrowserWasmConsumer/serve.mjs'
$probeScript = Join-Path $repoRoot 'scripts/Run-BrowserProbe.mjs'
$validator = Join-Path $repoRoot 'scripts/Validate-ManagedPackageClosure.ps1'
$packageDirectory = if ([System.IO.Path]::IsPathRooted($PackageDirectory)) {
    [System.IO.Path]::GetFullPath($PackageDirectory)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $repoRoot $PackageDirectory))
}
$output = if ([System.IO.Path]::IsPathRooted($Output)) {
    [System.IO.Path]::GetFullPath($Output)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $repoRoot $Output))
}
$obj = Join-Path (Split-Path -Parent $project) 'obj'
$config = Join-Path $obj 'browser-consumer.nuget.config'
$globalPackages = Join-Path $packageDirectory '.browser-consumer-packages'
$serverOutput = Join-Path $repoRoot 'artifacts/test-results/browser-consumer-server.log'
$serverError = Join-Path $repoRoot 'artifacts/test-results/browser-consumer-server.err.log'

if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
    throw 'Browser package validation requires Node.js.'
}
if (-not (Test-Path -LiteralPath $packageDirectory -PathType Container)) {
    throw "Package directory '$packageDirectory' does not exist."
}

New-Item -ItemType Directory -Path $obj -Force | Out-Null
New-Item -ItemType Directory -Path (Split-Path -Parent $serverOutput) -Force | Out-Null
if (Test-Path -LiteralPath $output) {
    Remove-Item -LiteralPath $output -Recurse -Force
}

$configXml = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="managed-package" value="$packageDirectory" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
"@
Set-Content -LiteralPath $config -Value $configXml -Encoding utf8

& dotnet restore $project `
    --configfile $config `
    --packages $globalPackages `
    "-p:AhtolaPackageVersion=$PackageVersion"
if ($LASTEXITCODE -ne 0) {
    throw "Browser consumer restore failed with exit code $LASTEXITCODE."
}

$publishArguments = @(
    'publish',
    $project,
    '--configuration', 'Release',
    '--no-restore',
    '--output', $output,
    "-p:AhtolaPackageVersion=$PackageVersion"
)
if ($RunAot) {
    $publishArguments += '-p:RunAOTCompilation=true'
}
& dotnet @publishArguments
if ($LASTEXITCODE -ne 0) {
    throw "Browser consumer publish failed with exit code $LASTEXITCODE."
}

& pwsh -NoLogo -NoProfile -File $validator -PublishOutput $output
if ($LASTEXITCODE -ne 0) {
    throw "Browser consumer closure validation failed with exit code $LASTEXITCODE."
}

$wwwroot = Join-Path $output 'wwwroot'
$server = Start-Process `
    -FilePath (Get-Command node).Source `
    -ArgumentList @($serverScript, $wwwroot, "$Port") `
    -RedirectStandardOutput $serverOutput `
    -RedirectStandardError $serverError `
    -NoNewWindow `
    -PassThru
try {
    $uri = "http://127.0.0.1:$Port/"
    $ready = $false
    for ($attempt = 0; $attempt -lt 120; $attempt++) {
        if ($server.HasExited) {
            throw "Browser consumer server exited with code $($server.ExitCode): $(Get-Content -LiteralPath $serverError -Raw)"
        }
        try {
            $response = Invoke-WebRequest -Uri $uri -UseBasicParsing
            if ($response.StatusCode -eq 200 -and
                $response.Headers['Cross-Origin-Opener-Policy'] -eq 'same-origin' -and
                $response.Headers['Cross-Origin-Embedder-Policy'] -eq 'require-corp') {
                $ready = $true
                break
            }
        } catch {
        }
        Start-Sleep -Milliseconds 250
    }
    if (-not $ready) {
        throw 'Browser consumer server did not become cross-origin isolated before timeout.'
    }

    $previousBrowser = $env:AHTOLA_BROWSER_EXECUTABLE
    try {
        if (-not [string]::IsNullOrWhiteSpace($BrowserExecutable)) {
            $env:AHTOLA_BROWSER_EXECUTABLE = $BrowserExecutable
        }
        & node $probeScript $uri 'PASS:capabilities=True;storage=True;crypto=True;ado=42;ef=84;persistent-ado=126;persistent-ef=168;persistent-core=210;persistent-features=546'
        if ($LASTEXITCODE -ne 0) {
            throw "Browser consumer probe failed with exit code $LASTEXITCODE."
        }
    } finally {
        $env:AHTOLA_BROWSER_EXECUTABLE = $previousBrowser
    }
} finally {
    if (-not $server.HasExited) {
        Stop-Process -Id $server.Id
        $server.WaitForExit()
    }
}

Write-Host 'Packed browser consumer passed.' -ForegroundColor Green
