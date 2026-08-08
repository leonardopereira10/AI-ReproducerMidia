# download-ffmpeg.ps1 (ST-05)
#
# Downloads the BtbN win64-gpl-shared FFmpeg build, extracts the shared DLLs the
# player needs (avcodec/avformat/avutil/swscale/swresample), the ffmpeg/ffprobe
# CLI executables (audio mux + thumbnails + probe) plus the license files, and
# places them in lib/ffmpeg/ next to the solution.
#
# Idempotent: if the required DLLs are already present the script exits early.
#
# Usage:
#   pwsh ./scripts/download-ffmpeg.ps1
#
# NOTE: This script is intended to be run manually by a developer. It is NOT invoked
# by the build or the test suite, and no binaries are committed (see .gitignore).

[CmdletBinding()]
param(
    # BtbN 'latest' = master/8.x which matches FFmpeg.AutoGen 8.1.0 (avcodec-63 etc.).
    [string]$ReleaseUrl = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl-shared.zip'
)

$ErrorActionPreference = 'Stop'

# Resolve repo root as the parent of this script's directory.
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$repoRoot = Split-Path -Parent $scriptDir
$targetDir = Join-Path $repoRoot (Join-Path 'lib' 'ffmpeg')

# Shared libraries: the five FFmpeg.AutoGen binds to, plus avfilter/avdevice
# which ffmpeg.exe/ffprobe.exe (audio mux / probe / thumbnails) link against.
$requiredDllPrefixes = @('avcodec', 'avformat', 'avutil', 'swscale', 'swresample', 'avfilter', 'avdevice')

function Test-AlreadyPresent {
    if (-not (Test-Path $targetDir)) { return $false }
    foreach ($prefix in $requiredDllPrefixes) {
        if (-not (Get-ChildItem -Path $targetDir -Filter "$prefix-*.dll" -ErrorAction SilentlyContinue)) {
            return $false
        }
    }
    # CLI tools must also be present (audio mux / probe / thumbnails).
    foreach ($tool in @('ffmpeg.exe', 'ffprobe.exe')) {
        if (-not (Test-Path (Join-Path $targetDir $tool))) { return $false }
    }
    return $true
}

if (Test-AlreadyPresent) {
    Write-Host "FFmpeg shared libraries already present in $targetDir - nothing to do."
    return
}

Write-Host "Creating target directory: $targetDir"
New-Item -ItemType Directory -Force -Path $targetDir | Out-Null

$workDir = Join-Path ([System.IO.Path]::GetTempPath()) ("catra-ffmpeg-" + [Guid]::NewGuid().ToString('N'))
$zipPath = Join-Path $workDir 'ffmpeg.zip'
New-Item -ItemType Directory -Force -Path $workDir | Out-Null

try {
    Write-Host "Downloading $ReleaseUrl ..."
    # TLS 1.2 required for GitHub on older runtimes.
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $ReleaseUrl -OutFile $zipPath -UseBasicParsing

    Write-Host "Extracting archive ..."
    Expand-Archive -Path $zipPath -DestinationPath $workDir -Force

    # The archive contains a single top-level folder (ffmpeg-master-...-shared).
    $extractedRoot = Get-ChildItem -Path $workDir -Directory |
        Where-Object { $_.Name -like 'ffmpeg-*' } |
        Select-Object -First 1
    if (-not $extractedRoot) {
        throw 'Could not locate the extracted ffmpeg-* folder inside the archive.'
    }

    $binDir = Join-Path $extractedRoot.FullName 'bin'
    $docDir = Join-Path $extractedRoot.FullName 'doc'

    Write-Host "Copying shared DLLs from $binDir ..."
    foreach ($prefix in $requiredDllPrefixes) {
        $dlls = Get-ChildItem -Path $binDir -Filter "$prefix-*.dll"
        if (-not $dlls) {
            throw "Required library '$prefix-*.dll' was not found in the archive bin folder."
        }
        $dlls | Copy-Item -Destination $targetDir -Force
    }

    # CLI tools (ST-17 audio mux, ST-09 thumbnails, library probe): shared
    # builds ship ffmpeg.exe/ffprobe.exe in bin/ next to the DLLs.
    Write-Host "Copying CLI tools (ffmpeg.exe, ffprobe.exe) ..."
    foreach ($tool in @('ffmpeg.exe', 'ffprobe.exe')) {
        $exe = Join-Path $binDir $tool
        if (Test-Path $exe) {
            Copy-Item -Path $exe -Destination $targetDir -Force
        }
        else {
            Write-Warning "$tool not found in the archive bin folder — CLI-dependent features will fall back to PATH."
        }
    }

    # License / attribution files (GPL build) - keep for legal compliance.
    if (Test-Path $docDir) {
        Write-Host 'Copying license files ...'
        Get-ChildItem -Path $docDir -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '(?i)license|copying|gpl' } |
            Copy-Item -Destination $targetDir -Force
    }

    Write-Host "FFmpeg shared libraries installed to $targetDir :"
    Get-ChildItem -Path $targetDir -Filter '*.dll' | ForEach-Object { Write-Host "  $($_.Name)" }
}
finally {
    if (Test-Path $workDir) {
        Remove-Item -Recurse -Force $workDir -ErrorAction SilentlyContinue
    }
}
