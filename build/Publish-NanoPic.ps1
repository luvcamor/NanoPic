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
    throw 'NanoPic 3.0 currently supports only the win-x64 release target.'
}

$solution = Join-Path $repositoryRoot 'NanoPic.sln'
$nugetConfig = Join-Path $repositoryRoot 'NuGet.config'
$portableScript = Join-Path $PSScriptRoot 'Build-NanoPicPortable.ps1'
$portableDirectory = Join-Path $repositoryRoot "artifacts/portable/$RuntimeIdentifier"
$portableExecutable = Join-Path $portableDirectory 'NanoPic.exe'
$licenseSource = Join-Path $PSScriptRoot 'release-assets/licenses'
$noticeSource = Join-Path $repositoryRoot 'src/NanoPic.App/THIRD-PARTY-NOTICES.txt'
$sbomTemplate = Join-Path $PSScriptRoot 'release-assets/SBOM.spdx.template.json'
$utf8WithoutBom = [Text.UTF8Encoding]::new($false)

foreach ($requiredSource in @(
    $solution,
    $nugetConfig,
    $portableScript,
    $licenseSource,
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
    if ($size -ge 2000000) {
        throw "FAILED-SIZE-GATE: NanoPic.exe is $size bytes; expected < 2,000,000 B."
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
            assemblyVersion = '3.2.2.0'
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
            'THIRD-PARTY-NOTICES.txt',
            'licenses/BSD-3-CLAUSE-LIBWEBP.txt',
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
        'THIRD-PARTY-NOTICES.txt',
        'licenses/BSD-3-CLAUSE-LIBWEBP.txt',
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

    Write-Host "Published executable: $executable"
    Write-Host "Runtime files: 1"
    Write-Host "Size: $size bytes"
    Write-Host "SHA-256: $hash"
}
finally {
    Pop-Location
}
