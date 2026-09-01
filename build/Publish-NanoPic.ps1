[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$OutputDirectory = 'artifacts/publish/win-x64'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$publishRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts/publish'))
$publishDirectory = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
}
else {
    [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
}
$publishPrefix = $publishRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $publishDirectory.StartsWith($publishPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Publish output must be a child of $publishRoot"
}
if (-not [string]::Equals($RuntimeIdentifier, 'win-x64', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'NanoPic currently supports only the win-x64 release target.'
}

$solution = Join-Path $repositoryRoot 'NanoPic.sln'
$nugetConfig = Join-Path $repositoryRoot 'NuGet.config'
$portableScript = Join-Path $PSScriptRoot 'Build-NanoPicPortable.ps1'
$portableDirectory = Join-Path $repositoryRoot "artifacts/portable/$RuntimeIdentifier"
$portableExecutable = Join-Path $portableDirectory 'NanoPic.exe'
$licenseSource = Join-Path $PSScriptRoot 'release-assets/licenses'
$projectLicenseSource = Join-Path $repositoryRoot 'LICENSE'
$noticeSource = Join-Path $repositoryRoot 'src/NanoPic.App/THIRD-PARTY-NOTICES.txt'
$sbomTemplate = Join-Path $PSScriptRoot 'release-assets/SBOM.spdx.template.json'
$utf8WithoutBom = [Text.UTF8Encoding]::new($false)
$samplePngBase64 = 'iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAYAAADED76LAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAACFSURBVChTFcpBEcBACACxc4ITnOAECesAJzhBzXaad957GA/zYT3sh/NwH97D9wIjMAMrsAMncAMv/pAYiZlYiZ04iZt4+YfCKMzCKuzCKdzCqz80RmM2VmM3TuM2Xv9hMAZzsAZ7cAZ38OYPi7GYi7XYi7O4i7d/OIzDPKzDPpzDPbzDD8zwmQFcLHd6AAAAAElFTkSuQmCC'

foreach ($requiredSource in @(
    $solution,
    $nugetConfig,
    $portableScript,
    $licenseSource,
    $projectLicenseSource,
    $noticeSource,
    $sbomTemplate
)) {
    if (-not (Test-Path -LiteralPath $requiredSource)) {
        throw "Required release source was not found: $requiredSource"
    }
}

Push-Location $repositoryRoot
try {
    dotnet restore $solution --locked-mode --configfile $nugetConfig
    if ($LASTEXITCODE -ne 0) {
        throw "Locked restore failed with exit code $LASTEXITCODE."
    }

    dotnet clean $solution -c $Configuration --verbosity minimal
    if ($LASTEXITCODE -ne 0) {
        throw "Release clean failed with exit code $LASTEXITCODE."
    }

    dotnet build $solution -c $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "Release build failed with exit code $LASTEXITCODE."
    }

    dotnet test $solution -c $Configuration --no-build --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "Release regression suite failed with exit code $LASTEXITCODE."
    }

    & $portableScript -Configuration $Configuration -OutputDirectory $portableDirectory
    if (-not (Test-Path -LiteralPath $portableExecutable -PathType Leaf)) {
        throw "Portable build did not produce NanoPic.exe: $portableExecutable"
    }

    if (Test-Path -LiteralPath $publishDirectory) {
        Remove-Item -LiteralPath $publishDirectory -Recurse -Force
    }
    New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null

    $executable = Join-Path $publishDirectory 'NanoPic.exe'
    Copy-Item -LiteralPath $portableExecutable -Destination $executable
    Copy-Item -LiteralPath $projectLicenseSource -Destination (Join-Path $publishDirectory 'LICENSE')
    Copy-Item -LiteralPath $noticeSource -Destination (Join-Path $publishDirectory 'THIRD-PARTY-NOTICES.txt')

    $publishLicenseDirectory = Join-Path $publishDirectory 'licenses'
    New-Item -ItemType Directory -Path $publishLicenseDirectory -Force | Out-Null
    Get-ChildItem -LiteralPath $licenseSource -File | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $publishLicenseDirectory $_.Name)
    }

    $hash = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToUpperInvariant()
    $size = (Get-Item -LiteralPath $executable).Length
    $productVersion = (Get-Item -LiteralPath $executable).VersionInfo.ProductVersion
    if ($productVersion -like '*+*') {
        $productVersion = $productVersion.Split('+')[0]
    }
    if ([string]::IsNullOrWhiteSpace($productVersion)) {
        throw 'FAILED-VERSION: the portable executable does not report a product version.'
    }
    $assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName($executable).Version.ToString()
    if ($size -ge 2000000) {
        throw "FAILED-SIZE-GATE: NanoPic.exe is $size bytes; expected < 2,000,000 B."
    }

    # Release gate: the packaged executable must pass the smoke test and must resolve
    # startup modes strictly (unknown switches never open the main window, and the COM
    # embedding mode never shows an empty window).
    $gateDirectory = Join-Path ([IO.Path]::GetTempPath()) ('nanopic-release-gate-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $gateDirectory -Force | Out-Null
    try {
        # 8x8 PNG kept inline so the gate has no imaging dependency (Windows PowerShell and
        # pwsh on CI resolve System.Drawing differently).
        $gateInput = Join-Path $gateDirectory 'gate-input.png'
        $gateOutput = Join-Path $gateDirectory 'gate-output.png'
        [IO.File]::WriteAllBytes($gateInput, [Convert]::FromBase64String($samplePngBase64))
        # Start-Process joins ArgumentList into one native command line. Preserve path
        # boundaries explicitly so a TEMP directory containing spaces remains valid.
        $gateInputArgument = '"' + $gateInput + '"'
        $gateOutputArgument = '"' + $gateOutput + '"'

        $smoke = Start-Process -FilePath $executable -ArgumentList @('--smoke-test', $gateInputArgument, $gateOutputArgument) -Wait -PassThru -WindowStyle Hidden
        if ($smoke.ExitCode -ne 0) {
            throw "FAILED-SMOKE-TEST: the packaged executable exited with $($smoke.ExitCode)."
        }

        if (-not (Test-Path -LiteralPath $gateOutput -PathType Leaf)) {
            throw 'FAILED-SMOKE-TEST: the packaged executable produced no output file.'
        }

        $usageCases = @(
            @('--smoke-test'),
            @('--smoke-test', $gateInputArgument),
            @('--smoke-test', $gateInputArgument, $gateOutputArgument, 'extra'),
            @('--shell-add'),
            @('-unknown-switch')
        )
        foreach ($usageCase in $usageCases) {
            $usage = Start-Process -FilePath $executable -ArgumentList $usageCase -Wait -PassThru -WindowStyle Hidden
            if ($usage.ExitCode -ne 64) {
                throw "FAILED-STARTUP-MODE-GATE: '$($usageCase -join ' ')' exited with $($usage.ExitCode); expected 64."
            }
        }

        $embedding = Start-Process -FilePath $executable -ArgumentList '-Embedding' -PassThru -WindowStyle Hidden
        try {
            Start-Sleep -Seconds 4
            $embedding.Refresh()
            if ($embedding.HasExited) {
                throw "FAILED-STARTUP-MODE-GATE: -Embedding exited immediately with $($embedding.ExitCode)."
            }

            if ($embedding.MainWindowHandle -ne 0) {
                throw 'FAILED-STARTUP-MODE-GATE: -Embedding opened a window without a shell request.'
            }
        }
        finally {
            $embedding.Refresh()
            if (-not $embedding.HasExited) {
                $embedding.Kill()
            }
        }

        Write-Host 'Release gate: smoke test and startup modes verified on the packaged executable.'
    }
    finally {
        Remove-Item -LiteralPath $gateDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }

    [IO.File]::WriteAllText(
        (Join-Path $publishDirectory 'NanoPic.exe.sha256'),
        "$hash *NanoPic.exe`n",
        $utf8WithoutBom)

    $manifest = [ordered]@{
        schemaVersion = '1.0'
        product = [ordered]@{
            name = 'NanoPic'
            version = $productVersion
            assemblyVersion = $assemblyVersion
            targetFramework = 'net48'
            runtimeIdentifier = $RuntimeIdentifier
            platform = 'x64'
            selfContained = $false
            runtimeRequirement = '.NET Framework 4.8'
            singleExecutable = $true
            singleExecutableMechanism = 'Costura.Fody'
            debugSymbolsEmbedded = $false
            codecBackend = 'Windows WIC + libwebp 1.6.0'
            embeddedLibwebpTools = $true
        }
        primaryArtifact = [ordered]@{
            path = 'NanoPic.exe'
            sha256 = $hash
            sizeBytes = $size
            sizeMegabytesDecimal = [Math]::Round($size / 1000000d, 3)
            sizeMebibytes = [Math]::Round($size / 1MB, 3)
        }
        runtimeFiles = @('NanoPic.exe')
        releaseFiles = @(
            'NanoPic.exe',
            'NanoPic.exe.sha256',
            'LICENSE',
            'THIRD-PARTY-NOTICES.txt',
            'licenses/BSD-3-CLAUSE-LIBWEBP.txt',
            'licenses/MIT-MANAGED-DEPENDENCIES.txt',
            'licenses/README.md',
            'SBOM.spdx.json',
            'manifest.json'
        )
        validation = [ordered]@{
            lockedRestoreCommand = 'dotnet restore NanoPic.sln --locked-mode --configfile NuGet.config'
            releaseCleanCommand = 'dotnet clean NanoPic.sln -c Release --verbosity minimal'
            releaseBuildCommand = 'dotnet build NanoPic.sln -c Release --no-restore'
            releaseTestCommand = 'dotnet test NanoPic.sln -c Release --no-build --no-restore'
            portableBuildCommand = './build/Build-NanoPicPortable.ps1 -Configuration Release'
            sizeGateRule = 'NanoPic.exe < 2,000,000 B'
            packagedSmokeTestRule = 'NanoPic.exe --smoke-test <input> <output> exits 0 and writes the output file'
            startupModeGateRule = 'unknown switches and malformed --smoke-test exit 64; -Embedding stays windowless'
        }
    }
    [IO.File]::WriteAllText(
        (Join-Path $publishDirectory 'manifest.json'),
        ($manifest | ConvertTo-Json -Depth 8),
        $utf8WithoutBom)

    $sbom = Get-Content -LiteralPath $sbomTemplate -Raw | ConvertFrom-Json
    $sbom.name = "NanoPic-$productVersion-win-x64"
    $sbom.packages[0].versionInfo = $productVersion
    $sbom.documentNamespace = "https://github.com/luvcamor/NanoPic/sbom/$productVersion/win-x64/$($hash.Substring(0, 16).ToLowerInvariant())"
    $sbom.creationInfo.created = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    $sbom.packages[0] | Add-Member -NotePropertyName checksums -NotePropertyValue @(
        [pscustomobject]@{ algorithm = 'SHA256'; checksumValue = $hash }
    ) -Force
    [IO.File]::WriteAllText(
        (Join-Path $publishDirectory 'SBOM.spdx.json'),
        ($sbom | ConvertTo-Json -Depth 10),
        $utf8WithoutBom)

    $requiredReleaseFiles = @(
        'NanoPic.exe',
        'NanoPic.exe.sha256',
        'LICENSE',
        'THIRD-PARTY-NOTICES.txt',
        'licenses/BSD-3-CLAUSE-LIBWEBP.txt',
        'licenses/MIT-MANAGED-DEPENDENCIES.txt',
        'licenses/README.md',
        'SBOM.spdx.json',
        'manifest.json'
    )
    $missing = $requiredReleaseFiles | Where-Object {
        -not (Test-Path -LiteralPath (Join-Path $publishDirectory $_) -PathType Leaf)
    }
    if ($missing) {
        throw "Release is missing required files: $($missing -join ', ')"
    }

    $forbiddenRuntimeFiles = @(Get-ChildItem -LiteralPath $publishDirectory -File | Where-Object {
        $_.Extension -in @('.dll', '.pdb', '.config') -or $_.Name -match 'Magick|ImageMagick'
    })
    if ($forbiddenRuntimeFiles.Count -ne 0) {
        throw "Release contains forbidden runtime files: $($forbiddenRuntimeFiles.Name -join ', ')"
    }

    $actualHash = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToUpperInvariant()
    if (-not [string]::Equals($actualHash, $hash, [StringComparison]::Ordinal)) {
        throw 'NanoPic.exe changed while the release bundle was being generated.'
    }

    Get-Content -LiteralPath (Join-Path $publishDirectory 'manifest.json') -Raw | ConvertFrom-Json | Out-Null
    Get-Content -LiteralPath (Join-Path $publishDirectory 'SBOM.spdx.json') -Raw | ConvertFrom-Json | Out-Null

    $archivePath = Join-Path $publishRoot "NanoPic-v$productVersion-$RuntimeIdentifier.zip"
    if (Test-Path -LiteralPath $archivePath) {
        Remove-Item -LiteralPath $archivePath -Force
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory(
        $publishDirectory,
        $archivePath,
        [IO.Compression.CompressionLevel]::Optimal,
        $false)

    $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        $expectedEntries = @($requiredReleaseFiles | ForEach-Object { $_.Replace('\', '/') } | Sort-Object)
        $actualEntries = @($archive.Entries |
            Where-Object { -not [string]::IsNullOrEmpty($_.Name) } |
            ForEach-Object { $_.FullName.Replace('\', '/') } |
            Sort-Object)
        $entryDifferences = @(Compare-Object -ReferenceObject $expectedEntries -DifferenceObject $actualEntries)
        if ($entryDifferences.Count -ne 0 -or $actualEntries.Count -ne @($actualEntries | Select-Object -Unique).Count) {
            throw "Release archive entries do not match the validated release files: $($entryDifferences -join ', ')"
        }

        $archivedExecutable = @($archive.Entries | Where-Object {
            [string]::Equals($_.FullName.Replace('\', '/'), 'NanoPic.exe', [StringComparison]::Ordinal)
        })
        if ($archivedExecutable.Count -ne 1) {
            throw 'Release archive must contain exactly one NanoPic.exe entry.'
        }

        $archiveExecutableStream = $archivedExecutable[0].Open()
        $sha256 = [Security.Cryptography.SHA256]::Create()
        try {
            $archivedExecutableHash = [BitConverter]::ToString($sha256.ComputeHash($archiveExecutableStream)).Replace('-', '')
        }
        finally {
            $sha256.Dispose()
            $archiveExecutableStream.Dispose()
        }
        if (-not [string]::Equals($archivedExecutableHash, $hash, [StringComparison]::Ordinal)) {
            throw 'NanoPic.exe inside the release archive does not match the published executable.'
        }
    }
    finally {
        $archive.Dispose()
    }

    $archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToUpperInvariant()

    Write-Host "Published executable: $executable"
    Write-Host "Runtime files: 1"
    Write-Host "Size: $size bytes"
    Write-Host "SHA-256: $hash"
    Write-Host "Release archive: $archivePath"
    Write-Host "Archive SHA-256: $archiveHash"
}
finally {
    Pop-Location
}
