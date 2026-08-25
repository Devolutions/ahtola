#Requires -Version 7.0
<#
.SYNOPSIS
    Deterministic trim/AOT analysis gate for the packed Ahtola consumers.

.DESCRIPTION
    Publishes each consumer profile with trim analysis fully unsuppressed
    (-p:SuppressTrimAnalysisWarnings=false -p:TrimmerSingleWarn=false), captures the publish
    output, and fails on any warning attributable to Ahtola.

    Profiles:

      Ado             samples/BrowserAdoTrimConsumer — browser, ADO only
                      (Devolutions.Ahtola.Data.Sqlite.Browser -> …Data.Sqlite -> …Core).
                      Requires ZERO IL2xxx/IL3xxx warnings in the whole closure. This is the
                      proof that the browser + core ADO package path is completely trim-clean.

      Ef              samples/BrowserEfTrimConsumer — browser, ADO + EF Core analysis profile.
      DesktopTrimmed  samples/ManagedPackageConsumer published with PublishTrimmed (ILLink).
      DesktopAot      samples/ManagedPackageConsumer published with PublishAot (ILC).
                      These three require zero warnings *originating in Ahtola source or
                      Devolutions.Ahtola assemblies*, and no grouped IL2104/IL3053 naming an
                      Ahtola assembly. Warnings originating solely in upstream Microsoft
                      EF Core / ASP.NET Core / runtime assemblies are recorded for the record but
                      do not fail the gate: EF Core is annotated RequiresUnreferencedCode /
                      RequiresDynamicCode upstream and is not trim-clean. An EF profile is only
                      "trim-clean" once its upstream dependency chain is warning-free.

      AdoDesktopTrimmed / AdoDesktopAot
                      samples/AdoTrimConsumer published with PublishTrimmed and with PublishAot.
                      ADO only, so these also require ZERO IL2xxx/IL3xxx warnings in the whole
                      closure — and the published binary is executed, so a schema table, an
                      annotated GetFieldType or a tuple accumulator that analyses cleanly but
                      breaks after trimming/AOT still fails the gate.

      Browser         Ado + Ef.
      AdoDesktop      AdoDesktopTrimmed + AdoDesktopAot.
      Desktop         AdoDesktop + DesktopTrimmed + DesktopAot.
      All             everything.

.PARAMETER PackageDirectory
    Directory containing the packed Devolutions.Ahtola.* nupkgs.

.PARAMETER PackageVersion
    Package version to consume from -PackageDirectory.

.PARAMETER Profile
    Which consumer profile(s) to gate.

.PARAMETER ClassifyOnly
    Re-run only the Ahtola attribution over an existing publish log.
#>
[CmdletBinding()]
param(
    [string]$PackageDirectory = './artifacts/managed-packages',
    [string]$PackageVersion = '0.0.0-managed-local',
    [ValidateSet('Ado', 'Ef', 'AdoDesktopTrimmed', 'AdoDesktopAot', 'DesktopTrimmed', 'DesktopAot', 'Browser', 'AdoDesktop', 'Desktop', 'All')]
    [string]$Profile = 'All',
    [string]$LogDirectory = './artifacts/trim-analysis',

    # Classify an existing publish log instead of publishing. Used to verify the attribution rules
    # against a captured log without paying for a publish.
    [string]$ClassifyOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$repoRoot = Split-Path -Parent $PSScriptRoot

$HostRuntimeIdentifier = if ($IsWindows) { 'win-x64' } elseif ($IsLinux) { 'linux-x64' } elseif ($IsMacOS) { 'osx-x64' } else {
    throw 'The NativeAOT trim profile is not configured for this host OS.'
}

function Resolve-RepoPath([string]$Path) {
    if ([System.IO.Path]::IsPathRooted($Path)) { return [System.IO.Path]::GetFullPath($Path) }
    return [System.IO.Path]::GetFullPath((Join-Path $repoRoot $Path))
}

# Attribution rules. A warning is "Ahtola-attributed" when EITHER
#   * its source file lives under src/Ahtola.<something>/ (our shipped source), or
#   * the member/assembly it names is in a Devolutions.Ahtola assembly or an Ahtola.* namespace.
# Both are checked for every line: a warning raised at a *consumer* source site (for example
# IL2091 on the consumer's own call) still belongs to us when its payload names an Ahtola generic
# parameter or member, so the file prefix must never short-circuit the payload check.
# Repository path segments are deliberately NOT used as evidence: a checkout directory that
# happens to contain "ahtola" must not change how a warning is classified.
$warningPattern = '\b(IL[23]\d{3})\b'
$groupedWarningCode = 'IL2104|IL3053'
$ahtolaSourcePattern = '(?i)[\\/]src[\\/]Ahtola\.[^\\/]+[\\/]'
$ahtolaMemberPattern = '(?<![A-Za-z0-9_.])(Devolutions\.Ahtola|Ahtola)\.[A-Za-z]'

function Test-AhtolaWarning([string]$Line) {
    # Analyzer-style: "<file>(line,col): warning IL2026: <message>"
    # ILLink-style:   "ILLink : Trim analysis warning IL2111: <member>: <message>"
    $payload = $Line
    $fileMatch = [regex]::Match($Line, '^(?<file>[^(]+)\(\d+,\d+\):')
    if ($fileMatch.Success) {
        if ([regex]::IsMatch($fileMatch.Groups['file'].Value, $ahtolaSourcePattern)) {
            return $true
        }

        # Not our source file. The warning can still be ours by payload, so strip the file prefix
        # (which may legitimately contain no Ahtola evidence) and keep classifying.
        $payload = $Line.Substring($fileMatch.Length)
    }

    $payload = [regex]::Replace($payload, '\s*\[[^\]]*\]\s*$', '')
    $codeMatch = [regex]::Match($payload, '\bIL[23]\d{3}\b:\s*(?<rest>.*)$')
    if (-not $codeMatch.Success) { return $false }

    return [bool]([regex]::IsMatch($codeMatch.Groups['rest'].Value, $ahtolaMemberPattern))
}

if (-not [string]::IsNullOrWhiteSpace($ClassifyOnly)) {
    $classifyPath = Resolve-RepoPath $ClassifyOnly
    if (-not (Test-Path -LiteralPath $classifyPath -PathType Leaf)) {
        throw "Log '$classifyPath' does not exist."
    }

    $classifyLines = @(
        Get-Content -LiteralPath $classifyPath |
            Where-Object { $_ -match $warningPattern } |
            ForEach-Object { $_.Trim() } |
            Sort-Object -Unique
    )
    $classifyAhtola = @($classifyLines | Where-Object { Test-AhtolaWarning $_ })
    Write-Host "total=$($classifyLines.Count) ahtola=$($classifyAhtola.Count) upstream=$($classifyLines.Count - $classifyAhtola.Count)"
    $classifyAhtola | ForEach-Object { Write-Host "  [Ahtola] $_" }
    return
}

$packageDirectory = Resolve-RepoPath $PackageDirectory
$logDirectory = Resolve-RepoPath $LogDirectory

if (-not (Test-Path -LiteralPath $packageDirectory -PathType Container)) {
    throw "Package directory '$packageDirectory' does not exist. Run ./build.ps1 pack first."
}

New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null

$consumers = @(
    [pscustomobject]@{
        Name        = 'Ado'
        Project     = 'samples/BrowserAdoTrimConsumer/BrowserAdoTrimConsumer.csproj'
        Description = 'ADO-only browser consumer (no Entity Framework Core)'
        RequireZeroTotalWarnings = $true
        RunExecutable = $null
        ExpectedOutput = $null
        ExtraRestoreArguments = @('-p:PublishTrimmed=true')
        ExtraPublishArguments = @('-p:PublishTrimmed=true')
    }
    [pscustomobject]@{
        Name        = 'Ef'
        Project     = 'samples/BrowserEfTrimConsumer/BrowserEfTrimConsumer.csproj'
        Description = 'Browser consumer with Devolutions.Ahtola.EntityFrameworkCore.Sqlite'
        RequireZeroTotalWarnings = $false
        RunExecutable = $null
        ExpectedOutput = $null
        ExtraRestoreArguments = @('-p:PublishTrimmed=true')
        ExtraPublishArguments = @('-p:PublishTrimmed=true')
    }
    [pscustomobject]@{
        Name        = 'AdoDesktopTrimmed'
        Project     = 'samples/AdoTrimConsumer/AdoTrimConsumer.csproj'
        Description = 'ADO-only desktop consumer, PublishTrimmed (net10.0)'
        RequireZeroTotalWarnings = $true
        ExtraRestoreArguments = @('-p:PublishTrimmed=true')
        ExtraPublishArguments = @('-p:PublishTrimmed=true', '--runtime', $HostRuntimeIdentifier, '--self-contained', 'true')
        RunExecutable = 'AdoTrimConsumer'
        ExpectedOutput = 'PASS: ado-trim-consumer'
    }
    [pscustomobject]@{
        Name        = 'AdoDesktopAot'
        Project     = 'samples/AdoTrimConsumer/AdoTrimConsumer.csproj'
        Description = 'ADO-only desktop consumer, PublishAot (net10.0)'
        RequireZeroTotalWarnings = $true
        ExtraRestoreArguments = @('--runtime', $HostRuntimeIdentifier, '-p:PublishAot=true')
        ExtraPublishArguments = @('--runtime', $HostRuntimeIdentifier, '-p:PublishAot=true')
        RunExecutable = 'AdoTrimConsumer'
        ExpectedOutput = 'PASS: ado-trim-consumer'
    }
    [pscustomobject]@{
        Name        = 'DesktopTrimmed'
        Project     = 'samples/ManagedPackageConsumer/ManagedPackageConsumer.csproj'
        Description = 'Packed desktop consumer, PublishTrimmed (net10.0)'
        RequireZeroTotalWarnings = $false
        RunExecutable = $null
        ExpectedOutput = $null
        ExtraRestoreArguments = @(
            '-p:AhtolaConsumerTargetFramework=net10.0'
            '-p:PublishTrimmed=true'
        )
        ExtraPublishArguments = @(
            '--framework', 'net10.0'
            '-p:AhtolaConsumerTargetFramework=net10.0'
            '-p:PublishTrimmed=true'
        )
    }
    [pscustomobject]@{
        Name        = 'DesktopAot'
        Project     = 'samples/ManagedPackageConsumer/ManagedPackageConsumer.csproj'
        Description = 'Packed desktop consumer, PublishAot (net10.0)'
        RequireZeroTotalWarnings = $false
        RunExecutable = $null
        ExpectedOutput = $null
        ExtraRestoreArguments = @(
            '--runtime', $HostRuntimeIdentifier
            '-p:AhtolaConsumerTargetFramework=net10.0'
            '-p:PublishAot=true'
        )
        ExtraPublishArguments = @(
            '--framework', 'net10.0'
            '--runtime', $HostRuntimeIdentifier
            '-p:AhtolaConsumerTargetFramework=net10.0'
            '-p:PublishAot=true'
            '-p:SuppressAotAnalysisWarnings=false'
        )
    }
)

if ($Profile -ne 'All') {
    $selected = switch ($Profile) {
        'Browser' { @('Ado', 'Ef') }
        'AdoDesktop' { @('AdoDesktopTrimmed', 'AdoDesktopAot') }
        'Desktop' { @('AdoDesktopTrimmed', 'AdoDesktopAot', 'DesktopTrimmed', 'DesktopAot') }
        default { @($Profile) }
    }
    $consumers = @($consumers | Where-Object { $selected -contains $_.Name })
}

$nugetConfigXml = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="managed-package" value="$packageDirectory" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
"@

$failures = [System.Collections.Generic.List[string]]::new()
$summaries = [System.Collections.Generic.List[object]]::new()

foreach ($consumer in $consumers) {
    $project = Resolve-RepoPath $consumer.Project
    if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
        throw "Consumer project '$project' does not exist."
    }

    $projectDirectory = Split-Path -Parent $project
    $obj = Join-Path $projectDirectory 'obj'
    New-Item -ItemType Directory -Path $obj -Force | Out-Null
    $nugetConfig = Join-Path $obj 'trim-analysis.nuget.config'
    Set-Content -LiteralPath $nugetConfig -Value $nugetConfigXml -Encoding utf8

    $globalPackages = Join-Path $packageDirectory ".trim-analysis-packages"
    $publishOutput = Join-Path $logDirectory "$($consumer.Name)-publish"
    $log = Join-Path $logDirectory "$($consumer.Name)-publish.log"
    if (Test-Path -LiteralPath $publishOutput) {
        Remove-Item -LiteralPath $publishOutput -Recurse -Force
    }

    Write-Host "==> Restoring $($consumer.Description)" -ForegroundColor Cyan
    & dotnet restore $project `
        --configfile $nugetConfig `
        --packages $globalPackages `
        "-p:AhtolaPackageVersion=$PackageVersion" `
        @($consumer.ExtraRestoreArguments) | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Restore failed for $($consumer.Project) with exit code $LASTEXITCODE."
    }

    Write-Host "==> Publishing $($consumer.Description) with trim analysis unsuppressed" -ForegroundColor Cyan
    # --no-incremental / a cleaned obj is not enough: ILLink only re-reports warnings when it
    # actually runs, so the publish output directory is removed above and the linker is forced to
    # re-run by publishing into a fresh directory.
    $publishOutputLines = & dotnet publish $project `
        --configuration Release `
        --no-restore `
        --output $publishOutput `
        "-p:AhtolaPackageVersion=$PackageVersion" `
        -p:SuppressTrimAnalysisWarnings=false `
        -p:TrimmerSingleWarn=false `
        @($consumer.ExtraPublishArguments) 2>&1
    $publishExitCode = $LASTEXITCODE
    $publishOutputLines | Set-Content -LiteralPath $log -Encoding utf8

    if ($publishExitCode -ne 0) {
        $failures.Add("$($consumer.Project): publish failed with exit code $publishExitCode (see $log).")
        continue
    }

    $warningLines = @(
        $publishOutputLines |
            ForEach-Object { [string]$_ } |
            Where-Object { $_ -match $warningPattern } |
            ForEach-Object { $_.Trim() } |
            Sort-Object -Unique
    )
    $ahtolaWarnings = @($warningLines | Where-Object { Test-AhtolaWarning $_ })
    $groupedAhtola = @($ahtolaWarnings | Where-Object { $_ -match $groupedWarningCode })
    $upstreamWarnings = @($warningLines | Where-Object { -not (Test-AhtolaWarning $_) })

    $summaries.Add([pscustomobject]@{
        Consumer = $consumer.Name
        Total    = $warningLines.Count
        Ahtola   = $ahtolaWarnings.Count
        Grouped  = $groupedAhtola.Count
        Upstream = $upstreamWarnings.Count
        Log      = $log
    })

    if ($ahtolaWarnings.Count -gt 0) {
        $failures.Add("$($consumer.Project): $($ahtolaWarnings.Count) Ahtola-attributed trim/AOT warning(s).")
        foreach ($warning in $ahtolaWarnings) {
            Write-Host "  [Ahtola] $warning" -ForegroundColor Red
        }
    }

    if ($groupedAhtola.Count -gt 0) {
        $failures.Add("$($consumer.Project): grouped IL2104/IL3053 warning(s) name an Ahtola assembly.")
    }

    if ($consumer.RequireZeroTotalWarnings -and $warningLines.Count -gt 0) {
        $failures.Add("$($consumer.Project): expected zero IL2xxx/IL3xxx warnings in the whole closure, found $($warningLines.Count).")
        foreach ($warning in $warningLines | Select-Object -First 40) {
            Write-Host "  [Total] $warning" -ForegroundColor Red
        }
    }

    if (-not $consumer.RequireZeroTotalWarnings -and $upstreamWarnings.Count -gt 0) {
        $upstreamLog = Join-Path $logDirectory "$($consumer.Name)-upstream-warnings.txt"
        $upstreamWarnings | Set-Content -LiteralPath $upstreamLog -Encoding utf8
        Write-Host "  $($upstreamWarnings.Count) upstream (non-Ahtola) warning(s) recorded in $upstreamLog" -ForegroundColor Yellow
    }

    if (-not [string]::IsNullOrWhiteSpace($consumer.RunExecutable)) {
        # Publishing cleanly is not the same as still working. Running the published binary is what
        # proves the annotated members and the tuple accumulator constructors actually survived.
        $executable = Join-Path $publishOutput ($consumer.RunExecutable + $(if ($IsWindows) { '.exe' } else { '' }))
        if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
            $failures.Add("$($consumer.Project): published executable '$executable' was not produced.")
        } else {
            Write-Host "==> Running $($consumer.Description)" -ForegroundColor Cyan
            $runOutput = & $executable 2>&1
            $runExitCode = $LASTEXITCODE
            $runLog = Join-Path $logDirectory "$($consumer.Name)-run.log"
            $runOutput | Set-Content -LiteralPath $runLog -Encoding utf8
            if ($runExitCode -ne 0) {
                $failures.Add("$($consumer.Project): published executable exited with $runExitCode (see $runLog).")
            } elseif (-not ($runOutput | Where-Object { "$_" -like "*$($consumer.ExpectedOutput)*" })) {
                $failures.Add("$($consumer.Project): published executable did not report '$($consumer.ExpectedOutput)' (see $runLog).")
            } else {
                Write-Host "  $($consumer.ExpectedOutput)" -ForegroundColor Green
            }
        }
    }
}

Write-Host ''
$summaries | Format-Table Consumer, Total, Ahtola, Grouped, Upstream -AutoSize | Out-String | Write-Host

if ($failures.Count -gt 0) {
    foreach ($failure in $failures) {
        Write-Host "FAIL: $failure" -ForegroundColor Red
    }
    throw "Trim analysis gate failed with $($failures.Count) problem(s)."
}

Write-Host 'Trim analysis gate passed.' -ForegroundColor Green
