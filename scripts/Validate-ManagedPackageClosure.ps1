[CmdletBinding()]
param(
    [string]$PackageDirectory,
    [string]$ProjectAssetsFile,
    [string]$PublishOutput,
    [string]$StagedBinaryDirectory,
    [switch]$NativeAot
)

$ErrorActionPreference = 'Stop'

$nativePackagePattern = '(?i)Ahtola\.(Raw|Data\.(Native|Sync)|Data\.Sqlite\.(Native[^"]*|Sync))'
$nativeConfigurationPattern = "(?i)$nativePackagePattern|cargo|rustc|cargo-ndk|turso_sdk_kit|DirectPInvoke|NativeLibrary|DllImport|LibraryImport|TursoUseStaticNativeLibrary"
$nativeAssetPattern = '(?i)(^|[\\/])(Ahtola\.Raw|Ahtola\.Data\.Native|Ahtola\.Data\.Sync)\.dll$|(^|[\\/])(lib)?Ahtola(_sync)?_sdk_kit(\.dll|\.so|\.dylib|\.a|\.lib)?$'
$ridOrNativePathPattern = '(?i)(^|[\\/])(runtimes|native|(?:win(?:10)?|linux|osx|unix|browser|android|ios|iossimulator|maccatalyst|tvos|tvossimulator|freebsd)-[^\\/]+)([\\/]|$)'
$nativeBinaryExtensionPattern = '(?i)\.(wasm|a|lib|o|obj|so|dylib|exe)$'
$expectedFrameworks = @('net8.0', 'net9.0', 'net10.0')
$expectedPackageIds = @(
    'Devolutions.Ahtola.Core',
    'Devolutions.Ahtola.Data.Sqlite',
    'Devolutions.Ahtola.Data.Sqlite.Browser',
    'Devolutions.Ahtola.EntityFrameworkCore.Sqlite'
)

function Fail([string]$Message) {
    throw "Managed release closure validation failed: $Message"
}

function Get-ChildElement([System.Xml.XmlNode]$Parent, [string]$Name) {
    return $Parent.SelectSingleNode("*[local-name()='$Name']")
}

function Test-ManagedAssemblyStream(
    [System.IO.Stream]$Stream,
    [string]$DisplayName
) {
    $seekableStream = $Stream
    $memoryStream = $null
    $peReader = $null
    try {
        if (-not $Stream.CanSeek) {
            $memoryStream = [System.IO.MemoryStream]::new()
            $Stream.CopyTo($memoryStream)
            $memoryStream.Position = 0
            $seekableStream = $memoryStream
        }

        $peReader = [System.Reflection.PortableExecutable.PEReader]::new($seekableStream)
        if (-not $peReader.HasMetadata) {
            Fail "'$DisplayName' is a native PE image, not a managed assembly."
        }
    }
    catch [System.BadImageFormatException] {
        Fail "'$DisplayName' is not a valid managed assembly."
    }
    finally {
        if ($null -ne $peReader) {
            $peReader.Dispose()
        }
        if ($null -ne $memoryStream) {
            $memoryStream.Dispose()
        }
    }
}

function Test-PackageMetadata(
    [System.Xml.XmlNode]$Metadata,
    [string]$PackageName,
    [string]$PackageId
) {
    $requiredText = [ordered]@{
        'version'     = $null
        'title'       = $null
        'authors'     = 'Devolutions'
        'description' = $null
        'copyright'   = $null
        'tags'        = $null
        'readme'      = 'README.md'
        'projectUrl'  = 'https://github.com/Devolutions/ahtola'
    }
    foreach ($name in $requiredText.Keys) {
        $node = Get-ChildElement $Metadata $name
        if ($null -eq $node -or [string]::IsNullOrWhiteSpace($node.InnerText)) {
            Fail "package '$PackageName' metadata '$name' must not be empty."
        }
        $expected = $requiredText[$name]
        if ($null -ne $expected -and $node.InnerText -ne $expected) {
            Fail "package '$PackageName' metadata '$name' must be '$expected'."
        }
    }

    $license = Get-ChildElement $Metadata 'license'
    if ($null -eq $license -or $license.type -ne 'expression' -or $license.InnerText -ne 'MIT') {
        Fail "package '$PackageName' must declare the MIT license as a package license expression."
    }

    $repository = Get-ChildElement $Metadata 'repository'
    if ($null -eq $repository -or
        $repository.type -ne 'git' -or
        $repository.url -ne 'https://github.com/Devolutions/ahtola' -or
        [string]::IsNullOrWhiteSpace($repository.commit)) {
        Fail "package '$PackageName' must declare its git repository URL and source commit."
    }

    $expectedFileName = "$PackageId.$((Get-ChildElement $Metadata 'version').InnerText).nupkg"
    if ($PackageName -ne $expectedFileName) {
        Fail "package '$PackageName' must be named '$expectedFileName' from its id and version metadata."
    }
}

function Get-ExpectedDependencies(
    [string]$PackageId,
    [string]$Framework,
    [string]$PackageVersion
) {
    $dependencies = [ordered]@{}
    switch ($PackageId) {
        'Devolutions.Ahtola.Data.Sqlite' {
            $dependencies['Devolutions.Ahtola.Core'] = $PackageVersion
        }
        'Devolutions.Ahtola.Data.Sqlite.Browser' {
            $dependencies['Devolutions.Ahtola.Data.Sqlite'] = $PackageVersion
        }
        'Devolutions.Ahtola.EntityFrameworkCore.Sqlite' {
            $dependencies['Devolutions.Ahtola.Data.Sqlite'] = $PackageVersion
            $dependencies['Microsoft.EntityFrameworkCore.Sqlite.Core'] = if ($Framework -eq 'net10.0') {
                '[10.0.0,11.0.0)'
            } else {
                '[9.0.9,10.0.0)'
            }
        }
    }
    return $dependencies
}

function Test-PackageDependencies(
    [System.Xml.XmlNode]$Metadata,
    [string]$PackageName,
    [string]$PackageId,
    [string]$PackageVersion
) {
    $dependenciesNode = Get-ChildElement $Metadata 'dependencies'
    if ($null -eq $dependenciesNode) {
        Fail "package '$PackageName' does not declare dependency groups."
    }

    $ungroupedDependencies = @($dependenciesNode.SelectNodes("*[local-name()='dependency']"))
    if ($ungroupedDependencies.Count -ne 0) {
        Fail "package '$PackageName' contains ungrouped dependencies."
    }

    $groups = @($dependenciesNode.SelectNodes("*[local-name()='group']"))
    if ($groups.Count -ne $expectedFrameworks.Count) {
        Fail "package '$PackageName' must declare exactly one dependency group for each supported framework."
    }

    foreach ($framework in $expectedFrameworks) {
        $frameworkGroups = @($groups | Where-Object { $_.targetFramework -eq $framework })
        if ($frameworkGroups.Count -ne 1) {
            Fail "package '$PackageName' must declare exactly one dependency group for '$framework'."
        }

        $expectedDependencies = Get-ExpectedDependencies $PackageId $framework $PackageVersion
        $actualDependencies = @($frameworkGroups[0].SelectNodes("*[local-name()='dependency']"))
        if ($actualDependencies.Count -ne $expectedDependencies.Count) {
            Fail "package '$PackageName' has an unexpected dependency count for '$framework'."
        }

        $duplicateDependencies = @($actualDependencies |
                Group-Object -Property id |
                Where-Object { $_.Count -gt 1 })
        if ($duplicateDependencies.Count -ne 0) {
            Fail "package '$PackageName' has duplicate dependency '$($duplicateDependencies[0].Name)' for '$framework'."
        }

        foreach ($dependency in $actualDependencies) {
            if (-not $expectedDependencies.Contains($dependency.id)) {
                Fail "package '$PackageName' has unexpected dependency '$($dependency.id)' for '$framework'."
            }
        }
        foreach ($expectedDependencyId in $expectedDependencies.Keys) {
            $matches = @($actualDependencies | Where-Object { $_.id -eq $expectedDependencyId })
            if ($matches.Count -ne 1) {
                Fail "package '$PackageName' must declare exactly one '$expectedDependencyId' dependency for '$framework'."
            }
            $expectedVersion = $expectedDependencies[$expectedDependencyId]
            if (($matches[0].version -replace '\s', '') -ne $expectedVersion) {
                Fail "package '$PackageName' must constrain '$expectedDependencyId' to '$expectedVersion' for '$framework'."
            }
        }
    }
}

function Test-PackageContent(
    [string]$PackageName,
    [string]$PackageId,
    [string[]]$EntryNames
) {
    $assemblyName = "$PackageId.dll"
    $expectedAssemblies = @($assemblyName)
    if ($PackageId -eq 'Devolutions.Ahtola.Data.Sqlite') {
        $expectedAssemblies += 'Devolutions.Ahtola.Data.dll'
    }

    $requiredEntries = [System.Collections.Generic.List[string]]::new()
    $requiredEntries.Add('README.md')
    foreach ($framework in $expectedFrameworks) {
        foreach ($expectedAssembly in $expectedAssemblies) {
            $requiredEntries.Add("lib/$framework/$expectedAssembly")
        }
    }
    if ($PackageId -eq 'Devolutions.Ahtola.Data.Sqlite.Browser') {
        foreach ($entry in @(
                'staticwebassets/ahtola-opfs.mjs',
                'staticwebassets/ahtola-opfs-capability-probe-worker.mjs',
                'staticwebassets/ahtola-opfs-worker.mjs',
                'staticwebassets/ahtola-crypto.mjs'
            )) {
            $requiredEntries.Add($entry)
        }
    }
    foreach ($requiredEntry in $requiredEntries) {
        if ($EntryNames -notcontains $requiredEntry) {
            Fail "package '$PackageName' is missing required entry '$requiredEntry'."
        }
    }

    $unsupportedLibEntries = @($EntryNames | Where-Object {
            $_ -match '^lib/([^/]+)/' -and $Matches[1] -notin $expectedFrameworks
        })
    if ($unsupportedLibEntries.Count -ne 0) {
        Fail "package '$PackageName' contains unsupported framework content '$($unsupportedLibEntries[0])'."
    }

    foreach ($framework in $expectedFrameworks) {
        $actualAssemblies = @($EntryNames | Where-Object {
                $_ -match "^lib/$framework/(Devolutions\.Ahtola\..+\.dll)$"
            } | ForEach-Object { [System.IO.Path]::GetFileName($_) })
        $unexpectedAssemblies = @($actualAssemblies | Where-Object { $_ -notin $expectedAssemblies })
        if ($unexpectedAssemblies.Count -ne 0) {
            Fail "package '$PackageName' contains unexpected Ahtola assembly '$($unexpectedAssemblies[0])' for '$framework'."
        }
    }
}

function Test-EntryPath([string]$Path, [string]$ContainerName) {
    if ($Path -match $ridOrNativePathPattern) {
        Fail "'$ContainerName' contains RID-specific or native path '$Path'."
    }
    if ($Path -match $nativeAssetPattern -or $Path -match $nativeBinaryExtensionPattern) {
        Fail "'$ContainerName' contains native asset '$Path'."
    }
    if ($Path -match '(?i)(^|[\\/])turso-src([\\/]|$)') {
        Fail "'$ContainerName' contains vendored Turso source '$Path'."
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
    if ($packages.Count -ne $expectedPackageIds.Count) {
        Fail "package directory '$Path' must contain exactly $($expectedPackageIds.Count) managed packages."
    }

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    Add-Type -AssemblyName System.Reflection.Metadata

    $seenPackageIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($package in $packages) {
        $archive = [System.IO.Compression.ZipFile]::OpenRead($package.FullName)
        try {
            $entryNames = @($archive.Entries | ForEach-Object { $_.FullName })
            $nuspecEntries = @($archive.Entries | Where-Object { $_.FullName -match '(?i)\.nuspec$' })
            if ($nuspecEntries.Count -ne 1) {
                Fail "package '$($package.Name)' must contain exactly one nuspec."
            }

            $nuspecReader = [System.IO.StreamReader]::new($nuspecEntries[0].Open())
            try {
                [xml]$nuspec = $nuspecReader.ReadToEnd()
            }
            finally {
                $nuspecReader.Dispose()
            }
            $metadata = $nuspec.SelectSingleNode("//*[local-name()='metadata']")
            $packageIdNode = if ($null -eq $metadata) { $null } else { Get-ChildElement $metadata 'id' }
            if ($null -eq $packageIdNode -or $packageIdNode.InnerText -notin $expectedPackageIds) {
                Fail "package '$($package.Name)' has an unexpected or missing package id."
            }
            $packageId = $packageIdNode.InnerText
            if (-not $seenPackageIds.Add($packageId)) {
                Fail "package directory '$Path' contains duplicate package id '$packageId'."
            }

            foreach ($entry in $archive.Entries) {
                Test-EntryPath $entry.FullName $package.Name
                if ($entry.FullName -match '(?i)\.dll$') {
                    $assemblyStream = $entry.Open()
                    try {
                        Test-ManagedAssemblyStream $assemblyStream "$($package.Name):$($entry.FullName)"
                    }
                    finally {
                        $assemblyStream.Dispose()
                    }
                }

                if ($entry.FullName -notmatch '(?i)\.(nuspec|props|targets)$') {
                    continue
                }

                $reader = [System.IO.StreamReader]::new($entry.Open())
                try {
                    $content = $reader.ReadToEnd()
                }
                finally {
                    $reader.Dispose()
                }

                if ($content -match $nativeConfigurationPattern) {
                    Fail "package '$($package.Name)' configuration entry '$($entry.FullName)' contains a native, P/Invoke, or Rust edge."
                }
            }

            $packageVersion = (Get-ChildElement $metadata 'version').InnerText
            Test-PackageMetadata $metadata $package.Name $packageId
            Test-PackageDependencies $metadata $package.Name $packageId $packageVersion
            Test-PackageContent $package.Name $packageId $entryNames
        }
        finally {
            $archive.Dispose()
        }
    }

    foreach ($expectedPackageId in $expectedPackageIds) {
        if (-not $seenPackageIds.Contains($expectedPackageId)) {
            Fail "package directory '$Path' is missing expected package '$expectedPackageId'."
        }
    }
}

function Test-StagedBinaryDirectory([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        Fail "staged binary directory '$Path' does not exist."
    }

    Add-Type -AssemblyName System.Reflection.Metadata
    $files = @(Get-ChildItem -LiteralPath $Path -File -Recurse)
    if ($files.Count -eq 0) {
        Fail "staged binary directory '$Path' is empty."
    }

    $managedAssemblies = 0
    foreach ($file in $files) {
        $relativePath = [System.IO.Path]::GetRelativePath($Path, $file.FullName)
        Test-EntryPath $relativePath $Path

        if ($file.Extension -eq '.dll') {
            $assemblyStream = $file.OpenRead()
            try {
                Test-ManagedAssemblyStream $assemblyStream $file.FullName
                $managedAssemblies++
            }
            finally {
                $assemblyStream.Dispose()
            }
        }

        if ($file.Extension -notin '.json', '.ps1', '.psd1', '.psm1') {
            continue
        }

        $content = Get-Content -LiteralPath $file.FullName -Raw
        if ($content -match $nativeConfigurationPattern) {
            Fail "staged file '$relativePath' contains a native, P/Invoke, or Rust edge."
        }
        if ($file.Name -match '(?i)\.deps\.json$') {
            $deps = $content | ConvertFrom-Json
            if ($null -ne $deps.runtimeTarget -and
                $deps.runtimeTarget.name -match '/') {
                Fail "staged dependency manifest '$relativePath' targets RID '$($deps.runtimeTarget.name)'."
            }

            foreach ($target in @($deps.targets.PSObject.Properties.Name)) {
                if ($target -match '/') {
                    Fail "staged dependency manifest '$relativePath' contains RID-specific target '$target'."
                }
            }

            foreach ($targetProperty in @($deps.targets.PSObject.Properties)) {
                foreach ($libraryProperty in @($targetProperty.Value.PSObject.Properties)) {
                    $runtimeTargets = $libraryProperty.Value.runtimeTargets
                    if ($null -eq $runtimeTargets) {
                        continue
                    }

                    foreach ($runtimeTarget in @($runtimeTargets.PSObject.Properties)) {
                        $assetPath = $runtimeTarget.Name
                        Test-EntryPath $assetPath "$relativePath runtimeTargets"
                        $asset = $runtimeTarget.Value
                        if ($asset.assetType -eq 'native' -or
                            -not [string]::IsNullOrWhiteSpace([string]$asset.rid)) {
                            Fail "staged dependency manifest '$relativePath' contains RID/native runtime target '$assetPath'."
                        }
                    }
                }
            }

            foreach ($library in @($deps.libraries.PSObject.Properties.Name)) {
                if ($library -match "(?i)$nativePackagePattern/") {
                    Fail "staged dependency manifest '$relativePath' contains native Ahtola package '$library'."
                }
            }
        }
    }

    if ($managedAssemblies -eq 0) {
        Fail "staged binary directory '$Path' contains no managed assemblies."
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
    [string]::IsNullOrWhiteSpace($PublishOutput) -and
    [string]::IsNullOrWhiteSpace($StagedBinaryDirectory)) {
    Fail 'supply PackageDirectory, ProjectAssetsFile, PublishOutput, or StagedBinaryDirectory.'
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

if (-not [string]::IsNullOrWhiteSpace($StagedBinaryDirectory)) {
    Test-StagedBinaryDirectory $StagedBinaryDirectory
}
