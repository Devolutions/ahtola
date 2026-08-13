[CmdletBinding()]
param(
    [string]$PackageDirectory,
    [string]$ProjectAssetsFile,
    [string]$PublishOutput,
    [switch]$NativeAot
)

$ErrorActionPreference = 'Stop'

$nativePackagePattern = '(?i)Ahtola\.(Raw|Data\.(Native|Sync)|Data\.Sqlite\.(Native[^"]*|Sync))'
$nativeConfigurationPattern = "(?i)$nativePackagePattern|cargo|rustc|cargo-ndk|turso_sdk_kit|DirectPInvoke|NativeLibrary|DllImport|LibraryImport|TursoUseStaticNativeLibrary"
$nativeArchiveEntryPattern = '(?i)(^|[\\/])(runtimes|native)[\\/]|(^|[\\/])(Ahtola\.Raw|Ahtola\.Data\.Native|Ahtola\.Data\.Sync)\.dll$|(^|[\\/])(lib)?Ahtola(_sync)?_sdk_kit(\.dll|\.so|\.dylib|\.a|\.lib)?$'

function Fail([string]$Message) {
    throw "Managed release closure validation failed: $Message"
}

function Test-EfCorePackageContract([xml]$Nuspec, [string]$PackageName) {
    $packageId = $Nuspec.SelectSingleNode("//*[local-name()='metadata']/*[local-name()='id']")
    if ($null -eq $packageId -or $packageId.InnerText -ne 'Devolutions.Ahtola.EntityFrameworkCore.Sqlite') {
        return
    }

    $expectedFrameworkRanges = [ordered]@{
            'net8.0'  = '[9.0.9,10.0.0)'
            'net9.0'  = '[9.0.9,10.0.0)'
            'net10.0' = '[10.0.0,11.0.0)'
        }
        $dependencyGroups = @($Nuspec.SelectNodes("//*[local-name()='dependencies']/*[local-name()='group']"))
        foreach ($expectedFramework in $expectedFrameworkRanges.Keys) {
            $expectedRange = $expectedFrameworkRanges[$expectedFramework]
            $groups = @($dependencyGroups | Where-Object { $_.targetFramework -eq $expectedFramework })
            if ($groups.Count -ne 1) {
                    Fail "package '$PackageName' must declare exactly one EF Core dependency group for '$expectedFramework'."
        }

            $dependencies = @($groups[0].SelectNodes("*[local-name()='dependency' and @id='Microsoft.EntityFrameworkCore.Sqlite.Core']"))
            if ($dependencies.Count -ne 1) {
                    Fail "package '$PackageName' must declare exactly one Microsoft.EntityFrameworkCore.Sqlite.Core dependency for '$expectedFramework'."
            }

            if (($dependencies[0].version -replace '\s', '') -ne $expectedRange) {
                    Fail "package '$PackageName' must constrain Microsoft.EntityFrameworkCore.Sqlite.Core to '$expectedRange' for '$expectedFramework'."
            }
        }
}

function Test-PackageDirectory([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        Fail "package directory '$Path' does not exist."
    }

    $packages = @(Get-ChildItem -LiteralPath $Path -Filter '*.nupkg' -File)
    if ($packages.Count -eq 0) {
        Fail "package directory '$Path' contains no .nupkg files."
    }

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    foreach ($package in $packages) {
        $archive = [System.IO.Compression.ZipFile]::OpenRead($package.FullName)
        try {
            $nuspecContent = $null
            foreach ($entry in $archive.Entries) {
                if ($entry.FullName -match $nativeArchiveEntryPattern) {
                    Fail "package '$($package.Name)' contains native entry '$($entry.FullName)'."
                }

                if ($entry.FullName -notmatch '(?i)\.(nuspec|props|targets)$') {
                    continue
                }

                $reader = [System.IO.StreamReader]::new($entry.Open())
                try {
                    $content = $reader.ReadToEnd()
                    if ($entry.FullName -match '(?i)\.nuspec$') {
                        $nuspecContent = $content
                    }
                }
                finally {
                    $reader.Dispose()
                }

                if ($content -match $nativeConfigurationPattern) {
                    Fail "package '$($package.Name)' configuration entry '$($entry.FullName)' contains a native, P/Invoke, or Rust edge."
                }
            }

            if ($null -eq $nuspecContent) {
                Fail "package '$($package.Name)' does not contain a nuspec."
            }

            Test-EfCorePackageContract ([xml]$nuspecContent) $package.Name
        }
        finally {
            $archive.Dispose()
        }
    }
}

function Test-ProjectAssets([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Fail "assets file '$Path' does not exist."
    }

    $assets = Get-Content -LiteralPath $Path -Raw
    if ($assets -notmatch '(?i)"Devolutions\.Ahtola\.Data\.Sqlite/') {
        Fail "assets file '$Path' does not restore Devolutions.Ahtola.Data.Sqlite."
    }

    if ($assets -match "(?i)`"$nativePackagePattern/") {
        Fail "assets file '$Path' restores a native Ahtola companion package."
    }
}

function Test-PublishOutput([string]$Path, [bool]$IsNativeAot) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        Fail "publish output '$Path' does not exist."
    }

    $files = @(Get-ChildItem -LiteralPath $Path -File -Recurse)
    if ($files.Count -eq 0) {
        Fail "publish output '$Path' is empty."
    }

    foreach ($file in $files) {
        if ($file.Name -match '(?i)^(Ahtola\.Raw|Ahtola\.Data\.Native|Ahtola\.Data\.Sync)\.dll$|^(lib)?Ahtola(_sync)?_sdk_kit(\.dll|\.so|\.dylib|\.a|\.lib)?$') {
            Fail "publish output '$Path' contains native companion asset '$($file.Name)'."
        }
    }

    if (-not $IsNativeAot) {
        return
    }

    $executables = @($files | Where-Object { $_.Extension -eq '' -or $_.Extension -eq '.exe' })
    if ($executables.Count -ne 1) {
        Fail "NativeAOT publish output '$Path' must contain exactly one executable."
    }

    $unexpected = @($files | Where-Object {
            $_.Extension -ne '' -and $_.Extension -notin '.exe', '.pdb', '.dbg', '.xml'
        })
    if ($unexpected.Count -ne 0) {
        Fail "NativeAOT publish output '$Path' contains unexpected file '$($unexpected[0].Name)'."
    }
}

if ([string]::IsNullOrWhiteSpace($PackageDirectory) -and
    [string]::IsNullOrWhiteSpace($ProjectAssetsFile) -and
    [string]::IsNullOrWhiteSpace($PublishOutput)) {
    Fail 'supply PackageDirectory, ProjectAssetsFile, or PublishOutput.'
}

if (-not [string]::IsNullOrWhiteSpace($PackageDirectory)) {
    Test-PackageDirectory $PackageDirectory
}

if (-not [string]::IsNullOrWhiteSpace($ProjectAssetsFile)) {
    Test-ProjectAssets $ProjectAssetsFile
}

if (-not [string]::IsNullOrWhiteSpace($PublishOutput)) {
    Test-PublishOutput $PublishOutput $NativeAot
}
