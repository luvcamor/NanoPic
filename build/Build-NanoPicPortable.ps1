[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$OutputDirectory = 'artifacts/portable/win-x64'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$portableRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts/portable'))
$outputPath = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
}
else {
    [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
}
$portablePrefix = $portableRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $outputPath.StartsWith($portablePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Portable output must be a child of $portableRoot"
}

$appProject = Join-Path $repositoryRoot 'src/NanoPic.App/NanoPic.App.csproj'
$nugetConfig = Join-Path $repositoryRoot 'NuGet.config'
$sourceDirectory = Join-Path $repositoryRoot "src/NanoPic.App/bin/$Configuration/net48"
$sourceExecutable = Join-Path $sourceDirectory 'NanoPic.exe'

Push-Location $repositoryRoot
try {
    dotnet restore $appProject --locked-mode --configfile $nugetConfig
    if ($LASTEXITCODE -ne 0) {
        throw "Locked restore failed with exit code $LASTEXITCODE."
    }

    dotnet build $appProject -c $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "Portable build failed with exit code $LASTEXITCODE."
    }

    if (-not (Test-Path -LiteralPath $sourceExecutable -PathType Leaf)) {
        throw "Build did not produce NanoPic.exe: $sourceExecutable"
    }

    if (Test-Path -LiteralPath $outputPath) {
        Remove-Item -LiteralPath $outputPath -Recurse -Force
    }
    New-Item -ItemType Directory -Path $outputPath -Force | Out-Null

    $portableExecutable = Join-Path $outputPath 'NanoPic.exe'
    Copy-Item -LiteralPath $sourceExecutable -Destination $portableExecutable

    $runtimeFiles = @(Get-ChildItem -LiteralPath $outputPath -Recurse -File)
    if ($runtimeFiles.Count -ne 1 -or $runtimeFiles[0].Name -ne 'NanoPic.exe') {
        throw "Portable layout must contain exactly one runtime file: NanoPic.exe."
    }

    if ($runtimeFiles | Where-Object { $_.Name -match 'Magick|ImageMagick' }) {
        throw 'Portable layout contains a forbidden Magick runtime file.'
    }

    $size = $runtimeFiles[0].Length
    if ($size -gt 40000000) {
        throw "FAILED-SIZE-GATE: NanoPic.exe is $size bytes."
    }

    $hash = (Get-FileHash -LiteralPath $portableExecutable -Algorithm SHA256).Hash.ToUpperInvariant()
    Write-Host "Portable executable: $portableExecutable"
    Write-Host "Runtime files: $($runtimeFiles.Count)"
    Write-Host "Size: $size bytes"
    Write-Host "SHA-256: $hash"
}
finally {
    Pop-Location
}
