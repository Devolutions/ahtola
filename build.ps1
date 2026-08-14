#Requires -Version 7.0
<#
.SYNOPSIS
    Build, pack, test, and validate the managed-only Ahtola packages.

.DESCRIPTION
    PowerShell entrypoint that replaces the former Makefile. There is no native
    companion, no Rust tooling, and no P/Invoke coupling in this tree.

.EXAMPLE
    ./build.ps1 build
    ./build.ps1 test
    ./build.ps1 pack -PackageVersion 0.1.0-preview.1
    ./build.ps1 validate-package
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet(
        'all',
        'restore',
        'build',
        'test',
        'pack',
                'pack-powershell',
                'test-powershell',
                'validate-package',
                'validate-runtime',
                'cloud-smoke',
                'validate-project-closure',
                'validate-packed-closure',
                'format-check'
            )]
            [string]$Task = 'build',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [string]$Framework = 'net10.0',

    [string]$PackageVersion,

    [string]$PackageOutput = './artifacts/managed-packages',

    [string]$PackageConsumerOutput = './artifacts/managed-package-consumer',

    [int]$MinimumExecutedTests = 2500,

        # Floor for Pester module tests (test-powershell). Keep in sync with
        # tests/PowerShell/Devolutions.Ahtola.Sqlite/Module.Tests.ps1.
        [int]$PowerShellMinimumExecutedTests = 34
    )

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$RepoRoot = $PSScriptRoot
Set-Location -LiteralPath $RepoRoot

$DataSqliteProject = './src/Ahtola.Data.Sqlite/Ahtola.Data.Sqlite.csproj'
$EfCoreProject = './src/Ahtola.EntityFrameworkCore.Sqlite/Ahtola.EntityFrameworkCore.Sqlite.csproj'
$PowerShellProject = './src/Devolutions.Ahtola.PowerShell/Devolutions.Ahtola.PowerShell.csproj'
$CoreProject = './src/Ahtola.Core/Ahtola.Core.csproj'
$TestsProject = './src/Ahtola.Tests/Ahtola.Tests.csproj'
$Solution = './Ahtola.slnx'
$PowerShellModuleName = 'Devolutions.Ahtola.Sqlite'
$PowerShellModuleOutput = "./artifacts/powershell-modules/$PowerShellModuleName"
$PowerShellAssemblyName = 'Devolutions.Ahtola.PowerShell'
$PowerShellHelpMarkdown = Join-Path $RepoRoot "docs/powershell-help/$PowerShellModuleName"
$PowerShellHelpBuilder = Join-Path $RepoRoot 'scripts/Build-PowerShellHelp.ps1'
$PowerShellTestRunner = Join-Path $RepoRoot 'scripts/Invoke-PowerShellModuleTests.ps1'
$ConsumerProject = './samples/ManagedPackageConsumer/ManagedPackageConsumer.csproj'
$ConsumerNugetConfig = './samples/ManagedPackageConsumer/obj/managed-package-consumer.nuget.config'
$ClosureValidator = Join-Path $RepoRoot 'scripts/Validate-ManagedPackageClosure.ps1'
$TestRunner = Join-Path $RepoRoot 'scripts/Invoke-ManagedTestSuite.ps1'
$ConsumerFrameworks = @('net8.0', 'net9.0', 'net10.0')

$NativeLeakPattern = '(?i)(Ahtola\.(Raw|Data\.(Native|Sync)|Data\.Sqlite\.(Native[^"]*|Sync))|cargo|rustc|cargo-ndk|turso_sdk_kit|DirectPInvoke|NativeLibrary|DllImport|LibraryImport|TursoUseStaticNativeLibrary)'

function Write-Step([string]$Message) {
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Invoke-DotNet {
    param([Parameter(Mandatory)][string[]]$Arguments)
    Write-Host "dotnet $($Arguments -join ' ')" -ForegroundColor DarkGray
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

function Invoke-PwshScript {
    param(
        [Parameter(Mandatory)][string]$Path,
        [string[]]$Arguments = @()
    )
    Write-Host "pwsh -File $Path $($Arguments -join ' ')" -ForegroundColor DarkGray
    & pwsh -NoLogo -NoProfile -File $Path @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Path failed with exit code $LASTEXITCODE"
    }
}

function Get-AbsolutePath([string]$Path) {
    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }
    return [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $Path))
}

function Remove-PathIfExists([string]$Path) {
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}

function Assert-ManagedProjectClosure {
    Write-Step 'Validating managed project closure (no native/Rust refs)'

    $scanFiles = [System.Collections.Generic.List[string]]::new()
    foreach ($relative in @('Directory.Build.props', 'Directory.Build.targets', 'Ahtola.slnx')) {
        $full = Join-Path $RepoRoot $relative
        if (Test-Path -LiteralPath $full -PathType Leaf) {
            $scanFiles.Add($full)
        }
    }

    $projectRoots = @(
        (Join-Path $RepoRoot 'src/Ahtola.Core'),
        (Join-Path $RepoRoot 'src/Ahtola.Data'),
        (Join-Path $RepoRoot 'src/Ahtola.Data.Sqlite'),
        (Join-Path $RepoRoot 'src/Ahtola.EntityFrameworkCore.Sqlite'),
                (Join-Path $RepoRoot 'src/Devolutions.Ahtola.PowerShell'),
                (Join-Path $RepoRoot 'samples/ManagedPackageConsumer')
            )
    foreach ($root in $projectRoots) {
        if (-not (Test-Path -LiteralPath $root -PathType Container)) {
            continue
        }
        Get-ChildItem -LiteralPath $root -Recurse -File -Include *.csproj, *.props, *.targets |
            ForEach-Object { $scanFiles.Add($_.FullName) }
    }

    $hits = @(Select-String -Path $scanFiles.ToArray() -Pattern $NativeLeakPattern -ErrorAction SilentlyContinue)
    if ($hits.Count -gt 0) {
        foreach ($hit in $hits) {
            Write-Host "$($hit.Path):$($hit.LineNumber): $($hit.Line.Trim())" -ForegroundColor Red
        }
        throw 'Managed package, solution, and build configurations must not reference native Ahtola packages, P/Invoke assets, or Rust tooling'
    }
}

function Invoke-Restore {
    Write-Step 'Restoring managed packages'
    Invoke-DotNet @('restore', $DataSqliteProject)
    Invoke-DotNet @('restore', $EfCoreProject)
        Invoke-DotNet @('restore', $PowerShellProject)
}

function Invoke-Build {
    param([string]$BuildConfiguration = $Configuration)

    Assert-ManagedProjectClosure
    Invoke-Restore
    Write-Step "Building managed packages ($BuildConfiguration)"
    Invoke-DotNet @('build', '--no-restore', '-c', $BuildConfiguration, $DataSqliteProject)
    Invoke-DotNet @('build', '--no-restore', '-c', $BuildConfiguration, $EfCoreProject)
        Invoke-DotNet @('build', '--no-restore', '-c', $BuildConfiguration, $PowerShellProject)
}

    function Invoke-PackPowerShell {
    param(
        [string]$BuildConfiguration = $Configuration,
        [string]$Version = $PackageVersion
    )

    Assert-ManagedProjectClosure
        Write-Step "Building and staging $PowerShellModuleName PowerShell module ($BuildConfiguration)"
        Invoke-DotNet @('build', '-c', $BuildConfiguration, '-f', 'net8.0', $PowerShellProject)

        $moduleAbsolute = Get-AbsolutePath $PowerShellModuleOutput
        $manifestPath = Join-Path $moduleAbsolute "$PowerShellModuleName.psd1"
        $assemblyPath = Join-Path $moduleAbsolute "bin\$PowerShellAssemblyName.dll"
        if (-not (Test-Path -LiteralPath $manifestPath)) {
            throw "Expected staged module manifest at $manifestPath"
        }
        if (-not (Test-Path -LiteralPath $assemblyPath)) {
            throw "Expected staged module assembly at $assemblyPath"
        }
        if (-not (Test-Path -LiteralPath $PowerShellHelpBuilder -PathType Leaf)) {
            throw "PowerShell help builder not found: $PowerShellHelpBuilder"
        }

        Invoke-PwshScript -Path $PowerShellHelpBuilder -Arguments @(
            '-ModulePath', $moduleAbsolute,
            '-MarkdownPath', $PowerShellHelpMarkdown
        )

        if (-not [string]::IsNullOrWhiteSpace($Version)) {
            if ($Version -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$') {
                throw "PowerShell module versions must contain only numeric components. Received '$Version'."
            }

            $manifest = Get-Content -LiteralPath $manifestPath -Raw
            if ($manifest -notmatch '(?m)^\s*ModuleVersion\s*=\s*''[^'']+''') {
                throw "Could not locate ModuleVersion in staged manifest $manifestPath"
            }

            $updatedManifest = $manifest -replace '(?m)^(\s*ModuleVersion\s*=\s*)''[^'']+''', "`$1'$Version'"
            Set-Content -LiteralPath $manifestPath -Value $updatedManifest -Encoding utf8NoBOM
        }

        Write-Host "PowerShell module staged at $moduleAbsolute" -ForegroundColor Green
    }

    function Invoke-TestPowerShell {
            param(
                [string]$BuildConfiguration = $Configuration,
                [int]$MinimumPesterTests = $PowerShellMinimumExecutedTests
            )

            Invoke-PackPowerShell -BuildConfiguration $BuildConfiguration
            Write-Step "Running Pester 6 tests for $PowerShellModuleName (min $MinimumPesterTests)"
            if (-not (Test-Path -LiteralPath $PowerShellTestRunner -PathType Leaf)) {
                throw "PowerShell module test runner not found: $PowerShellTestRunner"
            }

            & $PowerShellTestRunner `
                -ModulePath (Get-AbsolutePath $PowerShellModuleOutput) `
                -Configuration $BuildConfiguration `
                -MinimumExecutedTests $MinimumPesterTests
            if ($LASTEXITCODE -ne 0) {
                throw "PowerShell module tests failed with exit code $LASTEXITCODE"
            }
        }

function Invoke-Pack {
    param(
        [string]$Version = $PackageVersion,
        [string]$Output = $PackageOutput
    )

    Assert-ManagedProjectClosure
    Invoke-Restore

    $outputAbsolute = Get-AbsolutePath $Output
    Write-Step "Packing managed packages -> $outputAbsolute"
    Remove-PathIfExists $outputAbsolute
    New-Item -ItemType Directory -Path $outputAbsolute -Force | Out-Null

    $packArgs = @('pack', '--no-restore', '-c', 'Release', '--output', $outputAbsolute)
    if (-not [string]::IsNullOrWhiteSpace($Version)) {
        $packArgs += "-p:PackageVersion=$Version"
    }

    Invoke-DotNet ($packArgs + @($CoreProject))
    Invoke-DotNet ($packArgs + @($DataSqliteProject))
    Invoke-DotNet ($packArgs + @($EfCoreProject))

    Invoke-ValidatePackedClosure -Output $outputAbsolute
}

function Invoke-ValidatePackedClosure {
    param([string]$Output = $PackageOutput)

    $outputAbsolute = Get-AbsolutePath $Output
    Write-Step "Validating packed nupkg closure in $outputAbsolute"
    Invoke-PwshScript -Path $ClosureValidator -Arguments @('-PackageDirectory', $outputAbsolute)
}

function Write-ConsumerNugetConfig {
    param(
        [Parameter(Mandatory)][string]$ConfigPath,
        [Parameter(Mandatory)][string]$PackageSource
    )

    $directory = Split-Path -Parent $ConfigPath
    if (-not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    $xml = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="managed-package" value="$PackageSource" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
"@
    Set-Content -LiteralPath $ConfigPath -Value $xml -Encoding utf8
}

function Invoke-ValidatePackage {
    Assert-ManagedProjectClosure

    $localVersion = '0.0.0-managed-local'
    $packageOutputAbsolute = Get-AbsolutePath $PackageOutput
    $consumerOutputRoot = Get-AbsolutePath $PackageConsumerOutput
    $consumerNugetConfigAbsolute = Get-AbsolutePath $ConsumerNugetConfig
    $consumerProjectAbsolute = Get-AbsolutePath $ConsumerProject
    $consumerObj = Join-Path (Split-Path -Parent $consumerProjectAbsolute) 'obj'
    $consumerBin = Join-Path (Split-Path -Parent $consumerProjectAbsolute) 'bin'
    $globalPackages = Join-Path $packageOutputAbsolute '.nuget-packages'

    Invoke-Pack -Version $localVersion -Output $packageOutputAbsolute
    Invoke-ValidatePackedClosure -Output $packageOutputAbsolute

    Write-Step 'Validating packed consumer restore/build/run/publish'
    foreach ($targetFramework in $ConsumerFrameworks) {
        $consumerOutput = Join-Path $consumerOutputRoot $targetFramework
        Write-Host "--- consumer $targetFramework ---" -ForegroundColor Yellow

        Remove-PathIfExists $consumerBin
        Remove-PathIfExists $consumerObj
        Remove-PathIfExists $consumerOutput
        New-Item -ItemType Directory -Path $consumerObj -Force | Out-Null
        New-Item -ItemType Directory -Path $consumerOutput -Force | Out-Null

        Write-ConsumerNugetConfig -ConfigPath $consumerNugetConfigAbsolute -PackageSource $packageOutputAbsolute

        Invoke-DotNet @(
            'restore', $ConsumerProject,
            '--configfile', $consumerNugetConfigAbsolute,
            '--packages', $globalPackages,
            "-p:AhtolaPackageVersion=$localVersion",
            "-p:AhtolaConsumerTargetFramework=$targetFramework"
        )
        Invoke-DotNet @(
            'build', '--no-restore',
            '--framework', $targetFramework,
            $ConsumerProject,
            "-p:AhtolaPackageVersion=$localVersion",
            "-p:AhtolaConsumerTargetFramework=$targetFramework"
        )
        Invoke-DotNet @(
            'run', '--no-build', '--no-restore',
            '--framework', $targetFramework,
            '--project', $ConsumerProject,
            "-p:AhtolaPackageVersion=$localVersion",
            "-p:AhtolaConsumerTargetFramework=$targetFramework"
        )
        Invoke-DotNet @(
            'publish',
            '--configuration', 'Debug',
            '--no-build', '--no-restore',
            '--framework', $targetFramework,
            '--output', $consumerOutput,
            $ConsumerProject,
            "-p:AhtolaPackageVersion=$localVersion",
            "-p:AhtolaConsumerTargetFramework=$targetFramework"
        )

        $assetsFile = Join-Path $consumerObj 'project.assets.json'
        Invoke-PwshScript -Path $ClosureValidator -Arguments @(
            '-ProjectAssetsFile', $assetsFile,
            '-PublishOutput', $consumerOutput
        )
    }
}

function Invoke-ValidateRuntime {
    Invoke-ValidatePackage

    if ($Framework -ne 'net10.0') {
        Write-Step "Skipping NativeAOT consumer publish because $Framework is not net10.0"
        return
    }

    $localVersion = '0.0.0-managed-local'
    $packageOutputAbsolute = Get-AbsolutePath $PackageOutput
    $consumerNugetConfigAbsolute = Get-AbsolutePath $ConsumerNugetConfig
    $consumerProjectAbsolute = Get-AbsolutePath $ConsumerProject
    $globalPackages = Join-Path $packageOutputAbsolute '.nuget-packages'
    $runtimeOutputRoot = Get-AbsolutePath './artifacts/managed-package-runtime'
    $trimmedOutput = Join-Path $runtimeOutputRoot 'trimmed'
    $nativeAotOutput = Join-Path $runtimeOutputRoot 'nativeaot'
    $runtimeIdentifier = if ($IsWindows) { 'win-x64' } elseif ($IsLinux) { 'linux-x64' } elseif ($IsMacOS) { 'osx-x64' } else {
        throw "NativeAOT consumer publish is not configured for this host OS."
    }

    Remove-PathIfExists $trimmedOutput
    Remove-PathIfExists $nativeAotOutput

    Write-Step "Publishing trimmed packed consumer ($Framework)"
    Invoke-DotNet @(
        'restore', $ConsumerProject,
        '--configfile', $consumerNugetConfigAbsolute,
        '--packages', $globalPackages,
        "-p:AhtolaPackageVersion=$localVersion",
        "-p:AhtolaConsumerTargetFramework=$Framework",
        '-p:PublishTrimmed=true'
    )
    Invoke-DotNet @(
        'publish',
        '--configuration', 'Release',
        '--no-restore',
        '--framework', $Framework,
        '--output', $trimmedOutput,
        $ConsumerProject,
        "-p:AhtolaPackageVersion=$localVersion",
        "-p:AhtolaConsumerTargetFramework=$Framework",
        '-p:PublishTrimmed=true'
    )
    Invoke-PwshScript -Path $ClosureValidator -Arguments @(
        '-PublishOutput', $trimmedOutput
    )

    Write-Step "Publishing NativeAOT packed consumer ($Framework/$runtimeIdentifier)"
    Invoke-DotNet @(
        'restore', $ConsumerProject,
        '--runtime', $runtimeIdentifier,
        '--configfile', $consumerNugetConfigAbsolute,
        '--packages', $globalPackages,
        "-p:AhtolaPackageVersion=$localVersion",
        "-p:AhtolaConsumerTargetFramework=$Framework",
        '-p:PublishAot=true'
    )
    Invoke-DotNet @(
        'publish',
        '--configuration', 'Release',
        '--no-restore',
        '--framework', $Framework,
        '--runtime', $runtimeIdentifier,
        '--output', $nativeAotOutput,
        $ConsumerProject,
        "-p:AhtolaPackageVersion=$localVersion",
        "-p:AhtolaConsumerTargetFramework=$Framework",
        '-p:PublishAot=true'
    )
    Invoke-PwshScript -Path $ClosureValidator -Arguments @(
        '-PublishOutput', $nativeAotOutput,
        '-NativeAot'
    )
}

function Invoke-CloudSmoke {
    if ($env:AHTOLA_RUN_CLOUD_SMOKE -ne '1') {
        throw 'Cloud smoke requires AHTOLA_RUN_CLOUD_SMOKE=1.'
    }
    foreach ($name in 'TURSO_REMOTE_URL', 'TURSO_AUTH_TOKEN') {
        if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) {
            throw "Cloud smoke requires $name."
        }
    }

    Invoke-ValidatePackage
}

function Invoke-Test {
    Invoke-ValidatePackage
    Write-Step "Running managed test suite ($Framework)"
    Invoke-PwshScript -Path $TestRunner -Arguments @(
        '-Framework', $Framework,
        '-MinimumExecutedTests', "$MinimumExecutedTests"
    )
}

function Invoke-FormatCheck {
    Write-Step 'Checking formatting'
    Invoke-DotNet @('format', $Solution, '--verify-no-changes')
    Invoke-DotNet @('format', $TestsProject, '--verify-no-changes')
}

switch ($Task) {
    'all' { Invoke-Build }
    'restore' { Invoke-Restore }
    'build' { Invoke-Build }
    'pack' { Invoke-Pack }
        'pack-powershell' { Invoke-PackPowerShell }
        'test-powershell' { Invoke-TestPowerShell }
        'validate-project-closure' { Assert-ManagedProjectClosure }
        'validate-packed-closure' { Invoke-ValidatePackedClosure }
        'validate-package' { Invoke-ValidatePackage }
        'validate-runtime' { Invoke-ValidateRuntime }
        'cloud-smoke' { Invoke-CloudSmoke }
        'test' { Invoke-Test }
        'format-check' { Invoke-FormatCheck }
        default { throw "Unknown task '$Task'" }
    }

Write-Host "Task '$Task' completed." -ForegroundColor Green
