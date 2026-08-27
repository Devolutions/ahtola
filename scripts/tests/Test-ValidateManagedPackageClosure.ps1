#Requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$validator = Join-Path $repoRoot 'scripts/Validate-ManagedPackageClosure.ps1'
$versionStager = Join-Path $repoRoot 'scripts/Set-PowerShellModuleVersion.ps1'
$scratchRoot = Join-Path $repoRoot "artifacts/script-tests/package-closure-$([Guid]::NewGuid().ToString('N'))"
$managedAssembly = Join-Path $PSHOME 'System.Management.Automation.dll'
$version = '1.2.3-test.4'
$frameworks = @('net8.0', 'net9.0', 'net10.0')
$packageIds = @(
    'Devolutions.Ahtola.Core',
    'Devolutions.Ahtola.Data.Sqlite',
    'Devolutions.Ahtola.Data.Sqlite.Browser',
    'Devolutions.Ahtola.EntityFrameworkCore.Sqlite'
)

function Fail([string]$Message) {
    throw "Test-ValidateManagedPackageClosure failed: $Message"
}

function Get-Dependencies(
    [string]$PackageId,
    [string]$Framework,
    [switch]$ExtraCoreDependency,
    [switch]$DuplicateEfProviderDependency
) {
    $dependencies = switch ($PackageId) {
        'Devolutions.Ahtola.Data.Sqlite' {
            @("        <dependency id=`"Devolutions.Ahtola.Core`" version=`"$version`" />")
        }
        'Devolutions.Ahtola.Data.Sqlite.Browser' {
            @("        <dependency id=`"Devolutions.Ahtola.Data.Sqlite`" version=`"$version`" />")
        }
        'Devolutions.Ahtola.EntityFrameworkCore.Sqlite' {
            $efVersion = if ($Framework -eq 'net10.0') { '[10.0.0,11.0.0)' } else { '[9.0.9,10.0.0)' }
            if ($DuplicateEfProviderDependency) {
                @(
                    "        <dependency id=`"Devolutions.Ahtola.Data.Sqlite`" version=`"$version`" />"
                    "        <dependency id=`"Devolutions.Ahtola.Data.Sqlite`" version=`"$version`" />"
                )
            } else {
                @(
                    "        <dependency id=`"Devolutions.Ahtola.Data.Sqlite`" version=`"$version`" />"
                    "        <dependency id=`"Microsoft.EntityFrameworkCore.Sqlite.Core`" version=`"$efVersion`" />"
                )
            }
        }
        default {
            if ($ExtraCoreDependency) {
                @("        <dependency id=`"Unexpected.Package`" version=`"1.0.0`" />")
            } else {
                @()
            }
        }
    }
    return @($dependencies)
}

function New-TestPackage(
    [string]$PackageDirectory,
    [string]$PackageId,
    [switch]$ExtraCoreDependency,
    [switch]$DuplicateEfProviderDependency,
    [switch]$BadMetadata,
    [switch]$RidLeak,
    [switch]$NativeLeak
) {
    $source = Join-Path $scratchRoot "source-$($PackageId.Replace('.', '-'))"
    New-Item -ItemType Directory -Path $source -Force | Out-Null

    $groups = foreach ($framework in $frameworks) {
        $dependencies = @(Get-Dependencies $PackageId $framework `
                -ExtraCoreDependency:$ExtraCoreDependency `
                -DuplicateEfProviderDependency:$DuplicateEfProviderDependency)
        if ($dependencies.Count -eq 0) {
            "      <group targetFramework=`"$framework`" />"
        } else {
            @"
      <group targetFramework="$framework">
$($dependencies -join [Environment]::NewLine)
      </group>
"@
        }

        $lib = Join-Path $source "lib/$framework"
        New-Item -ItemType Directory -Path $lib -Force | Out-Null
        Copy-Item -LiteralPath $managedAssembly -Destination (Join-Path $lib "$PackageId.dll")
        if ($PackageId -eq 'Devolutions.Ahtola.Data.Sqlite') {
            Copy-Item -LiteralPath $managedAssembly -Destination (Join-Path $lib 'Devolutions.Ahtola.Data.dll')
        }
    }

    if ($PackageId -eq 'Devolutions.Ahtola.Data.Sqlite.Browser') {
        $assets = Join-Path $source 'staticwebassets'
        New-Item -ItemType Directory -Path $assets -Force | Out-Null
        foreach ($asset in @(
                'ahtola-opfs.mjs',
                'ahtola-opfs-capability-probe-worker.mjs',
                'ahtola-opfs-worker.mjs',
                'ahtola-crypto.mjs'
            )) {
            Set-Content -LiteralPath (Join-Path $assets $asset) -Value 'export {};'
        }
    }

    if ($RidLeak -and $PackageId -eq 'Devolutions.Ahtola.Core') {
        $ridDirectory = Join-Path $source 'runtimes/win-x64/lib/net8.0'
        New-Item -ItemType Directory -Path $ridDirectory -Force | Out-Null
        Copy-Item -LiteralPath $managedAssembly -Destination (Join-Path $ridDirectory 'RidLeak.dll')
    }
    if ($NativeLeak -and $PackageId -eq 'Devolutions.Ahtola.Core') {
        Set-Content -LiteralPath (Join-Path $source 'payload.so') -Value 'native'
    }

    Set-Content -LiteralPath (Join-Path $source 'README.md') -Value '# Test'
    $authors = if ($BadMetadata -and $PackageId -eq 'Devolutions.Ahtola.Core') { '' } else { 'Devolutions' }
    $nuspec = @"
<?xml version="1.0" encoding="utf-8"?>
<package>
  <metadata>
    <id>$PackageId</id>
    <version>$version</version>
    <title>Package closure test</title>
    <authors>$authors</authors>
    <license type="expression">MIT</license>
    <readme>README.md</readme>
    <projectUrl>https://github.com/Devolutions/ahtola</projectUrl>
    <description>Package closure validation fixture.</description>
    <copyright>Copyright (c) 2026 Devolutions</copyright>
    <tags>ahtola test</tags>
    <repository type="git" url="https://github.com/Devolutions/ahtola" commit="0123456789012345678901234567890123456789" />
    <dependencies>
$($groups -join [Environment]::NewLine)
    </dependencies>
  </metadata>
</package>
"@
    Set-Content -LiteralPath (Join-Path $source "$PackageId.nuspec") -Value $nuspec
    $packagePath = Join-Path $PackageDirectory "$PackageId.$version.nupkg"
    [System.IO.Compression.ZipFile]::CreateFromDirectory($source, $packagePath)
    Remove-Item -LiteralPath $source -Recurse -Force
}

function New-TestPackageSet(
    [string]$Name,
    [switch]$ExtraCoreDependency,
    [switch]$DuplicateEfProviderDependency,
    [switch]$BadMetadata,
    [switch]$RidLeak,
    [switch]$NativeLeak
) {
    $directory = Join-Path $scratchRoot $Name
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    foreach ($packageId in $packageIds) {
        New-TestPackage $directory $packageId `
            -ExtraCoreDependency:$ExtraCoreDependency `
            -DuplicateEfProviderDependency:$DuplicateEfProviderDependency `
            -BadMetadata:$BadMetadata `
            -RidLeak:$RidLeak `
            -NativeLeak:$NativeLeak
    }
    return $directory
}

function Invoke-Validator([string[]]$Arguments, [bool]$ShouldSucceed, [string]$ExpectedFailure = '') {
    $output = @(& pwsh -NoLogo -NoProfile -File $validator @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    $renderedOutput = ($output -join [Environment]::NewLine) `
        -replace '\x1B\[[0-?]*[ -/]*[@-~]', '' `
        -replace '\s*\|\s*', ' ' `
        -replace '\s+', ' '
    if ($ShouldSucceed -and $exitCode -ne 0) {
        Fail "validator unexpectedly failed: $renderedOutput"
    }
    if (-not $ShouldSucceed -and $exitCode -eq 0) {
        Fail "validator unexpectedly accepted: $($Arguments -join ' ')"
    }
    if (-not $ShouldSucceed -and
        -not [string]::IsNullOrWhiteSpace($ExpectedFailure) -and
        $renderedOutput -notmatch [regex]::Escape($ExpectedFailure)) {
        Fail "validator failure did not contain '$ExpectedFailure': $renderedOutput"
    }
}

function New-TestModuleManifest([string]$Path) {
    @(
        '@{'
        "    ModuleVersion = '0.1.0'"
        "    GUID = '11111111-1111-1111-1111-111111111111'"
        '    PrivateData = @{'
        '        PSData = @{'
        '        }'
        '    }'
        '}'
    ) | Set-Content -LiteralPath $Path -Encoding utf8NoBOM
}

function Invoke-VersionStager([string]$ManifestPath, [string]$RequestedVersion) {
    $output = @(
        & pwsh -NoLogo -NoProfile -File $versionStager `
            -ManifestPath $ManifestPath `
            -RequestedVersion $RequestedVersion 2>&1
    )
    if ($LASTEXITCODE -ne 0) {
        Fail "version stager failed for '$RequestedVersion': $($output -join [Environment]::NewLine)"
    }
    return Import-PowerShellDataFile -LiteralPath $ManifestPath
}

try {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    New-Item -ItemType Directory -Path $scratchRoot -Force | Out-Null

    $stableManifestPath = Join-Path $scratchRoot 'stable.psd1'
    New-TestModuleManifest $stableManifestPath
    $stableManifest = Invoke-VersionStager $stableManifestPath '1.2.3'
    if ($stableManifest.ModuleVersion.ToString() -ne '1.2.3' -or
        $stableManifest.PrivateData.PSData.Contains('Prerelease') -or
        $stableManifest.PrivateData.AhtolaPackageVersion -ne '1.2.3') {
        Fail 'stable PowerShell version mapping was not preserved safely.'
    }

    $fourPartManifestPath = Join-Path $scratchRoot 'four-part-stable.psd1'
    New-TestModuleManifest $fourPartManifestPath
    $fourPartManifest = Invoke-VersionStager $fourPartManifestPath '1.2.3.4'
    if ($fourPartManifest.ModuleVersion.ToString() -ne '1.2.3.4' -or
        $fourPartManifest.PrivateData.PSData.Contains('Prerelease') -or
        $fourPartManifest.PrivateData.AhtolaPackageVersion -ne '1.2.3.4') {
        Fail 'four-part stable PowerShell version was not preserved.'
    }

    $ciManifestPath = Join-Path $scratchRoot 'ci.psd1'
    New-TestModuleManifest $ciManifestPath
    $ciManifest = Invoke-VersionStager $ciManifestPath '0.0.0-ci.42'
    if ($ciManifest.ModuleVersion.ToString() -ne '0.0.0' -or
        $ciManifest.PrivateData.PSData.Prerelease -ne 'ci-42' -or
        $ciManifest.PrivateData.AhtolaPackageVersion -ne '0.0.0-ci.42') {
        Fail 'CI PowerShell version mapping is not publishable or lost the requested package version.'
    }

    $validPackages = New-TestPackageSet 'valid'
    Invoke-Validator @('-PackageDirectory', $validPackages) $true

    $standaloneRoot = Join-Path $scratchRoot 'standalone-build'
    $standaloneScripts = Join-Path $standaloneRoot 'scripts'
    New-Item -ItemType Directory -Path $standaloneScripts -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot 'build.ps1') -Destination $standaloneRoot
    Copy-Item -LiteralPath $validator -Destination $standaloneScripts
    $standaloneOutput = @(
        & pwsh -NoLogo -NoProfile -File (Join-Path $standaloneRoot 'build.ps1') `
            validate-packed-closure -PackageOutput $validPackages 2>&1
    )
    if ($LASTEXITCODE -ne 0) {
        Fail "standalone validate-packed-closure unexpectedly required staged PowerShell output: $($standaloneOutput -join [Environment]::NewLine)"
    }

    $badDependencies = New-TestPackageSet 'bad-dependency' -ExtraCoreDependency
    Invoke-Validator @('-PackageDirectory', $badDependencies) $false 'unexpected dependency count'

    $duplicateEfDependency = New-TestPackageSet 'duplicate-ef-dependency' -DuplicateEfProviderDependency
    Invoke-Validator @('-PackageDirectory', $duplicateEfDependency) $false 'duplicate dependency'

    $badMetadata = New-TestPackageSet 'bad-metadata' -BadMetadata
    Invoke-Validator @('-PackageDirectory', $badMetadata) $false "'authors'"

    $ridPackages = New-TestPackageSet 'rid-leak' -RidLeak
    Invoke-Validator @('-PackageDirectory', $ridPackages) $false 'RID-specific'

    $nativePackages = New-TestPackageSet 'native-leak' -NativeLeak
    Invoke-Validator @('-PackageDirectory', $nativePackages) $false 'native asset'

    $staged = Join-Path $scratchRoot 'staged'
    $stagedBin = Join-Path $staged 'bin'
    New-Item -ItemType Directory -Path $stagedBin -Force | Out-Null
    Copy-Item -LiteralPath $managedAssembly -Destination (Join-Path $stagedBin 'Devolutions.Ahtola.PowerShell.dll')
    $depsPath = Join-Path $stagedBin 'Devolutions.Ahtola.PowerShell.deps.json'
    $validDepsJson = @'
{
  "runtimeTarget": { "name": ".NETCoreApp,Version=v8.0" },
  "targets": { ".NETCoreApp,Version=v8.0": {} },
  "libraries": {}
}
'@
    Set-Content -LiteralPath $depsPath -Value $validDepsJson
    Invoke-Validator @('-StagedBinaryDirectory', $staged) $true

    Set-Content -LiteralPath $depsPath -Value @'
{
  "runtimeTarget": { "name": ".NETCoreApp,Version=v8.0" },
  "targets": {
    ".NETCoreApp,Version=v8.0": {
      "Native.Dependency/1.0.0": {
        "runtimeTargets": {
          "runtimes/win-x64/native/dependency.dll": {
            "rid": "win-x64",
            "assetType": "native"
          }
        }
      }
    }
  },
  "libraries": {}
}
'@
    Invoke-Validator @('-StagedBinaryDirectory', $staged) $false 'RID-specific'

    Set-Content -LiteralPath $depsPath -Value @'
{
  "runtimeTarget": { "name": ".NETCoreApp,Version=v8.0" },
  "targets": {
    ".NETCoreApp,Version=v8.0": {
      "Native.Dependency/1.0.0": {
        "runtimeTargets": {
          "lib/net8.0/dependency.dll": {
            "assetType": "native"
          }
        }
      }
    }
  },
  "libraries": {}
}
'@
    Invoke-Validator @('-StagedBinaryDirectory', $staged) $false 'RID/native runtime target'
    Set-Content -LiteralPath $depsPath -Value $validDepsJson

    $ridStage = Join-Path $staged 'bin/win-x64'
    New-Item -ItemType Directory -Path $ridStage -Force | Out-Null
    Copy-Item -LiteralPath $managedAssembly -Destination (Join-Path $ridStage 'RidLeak.dll')
    Invoke-Validator @('-StagedBinaryDirectory', $staged) $false 'RID-specific'

    $nativeStage = Join-Path $scratchRoot 'native-staged/bin'
    New-Item -ItemType Directory -Path $nativeStage -Force | Out-Null
    Copy-Item -LiteralPath $managedAssembly -Destination (Join-Path $nativeStage 'Managed.dll')
    Set-Content -LiteralPath (Join-Path $nativeStage 'Native.dll') -Value 'not a managed PE image'
    Invoke-Validator @('-StagedBinaryDirectory', (Split-Path -Parent $nativeStage)) $false 'native PE image'

    Write-Host 'Test-ValidateManagedPackageClosure passed (15 checks).' -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $scratchRoot) {
        Remove-Item -LiteralPath $scratchRoot -Recurse -Force
    }
}
