param([ValidatePattern('^\d+\.\d+\.\d+\.\d+$')][string]$ReleaseVersion)
$ErrorActionPreference = 'Stop'
$releaseRoot = Split-Path -Parent $PSScriptRoot
$releaseOutput = Join-Path $releaseRoot 'publish/standalone-linux-x64'
$previousResolver = [Environment]::GetEnvironmentVariable('MSBuildEnableWorkloadResolver', 'Process')
try {
    [Environment]::SetEnvironmentVariable('MSBuildEnableWorkloadResolver', 'false', 'Process')
    $versionArguments = @(); if ($ReleaseVersion) { $versionArguments += ('-p:ReleaseVersion=' + $ReleaseVersion) }
    & dotnet publish (Join-Path $releaseRoot 'LeitstellenPilot/LeitstellenPilot.csproj') -c Release -r linux-x64 --self-contained true '-p:PublishProfile=Standalone-LinuxX64' '-p:UseSharedCompilation=false' @versionArguments -m:1 -v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'Linux-Publish fehlgeschlagen.' }
} finally { [Environment]::SetEnvironmentVariable('MSBuildEnableWorkloadResolver', $previousResolver, 'Process') }
$releaseBinary = Join-Path $releaseOutput 'LeitstellenPilot'
$legacyManifest = $releaseBinary + '.update.json'
if (Test-Path -LiteralPath $legacyManifest) { Remove-Item -LiteralPath $legacyManifest }
$releaseArchive = Join-Path $releaseRoot 'publish/LeitstellenPilot-linux-x64.tar.gz'
Add-Type -AssemblyName System.Formats.Tar
$archive = [IO.File]::Create($releaseArchive)
try {
    $gzip = [IO.Compression.GZipStream]::new($archive, [IO.Compression.CompressionLevel]::Optimal, $true)
    try {
        $tar = [System.Formats.Tar.TarWriter]::new($gzip, $true)
        try {
            $files = @(
                @{ Path=$releaseBinary; Name='LeitstellenPilot'; Mode=493 },
                @{ Path=(Join-Path $releaseRoot 'deploy/linux/config.example.json'); Name='config.example.json'; Mode=384 },
                @{ Path=(Join-Path $releaseRoot 'deploy/linux/leitstellenpilot.service'); Name='leitstellenpilot.service'; Mode=420 },
                @{ Path=(Join-Path $releaseRoot 'docs/Standalone.md'); Name='README-Linux.md'; Mode=420 },
                @{ Path=(Join-Path $releaseRoot 'THIRD_PARTY_NOTICES.md'); Name='THIRD_PARTY_NOTICES.md'; Mode=420 },
                @{ Path=(Join-Path $releaseRoot 'THIRD_PARTY_LICENSES.txt'); Name='THIRD_PARTY_LICENSES.txt'; Mode=420 },
                @{ Path=(Join-Path $releaseRoot 'LICENSE'); Name='LICENSE'; Mode=420 }
            )
            foreach ($file in $files) {
                $entry = [System.Formats.Tar.PaxTarEntry]::new([System.Formats.Tar.TarEntryType]::RegularFile, ('LeitstellenPilot-linux-x64/' + $file.Name))
                $entry.Mode = [IO.UnixFileMode]$file.Mode
                $stream = [IO.File]::OpenRead($file.Path)
                try { $entry.DataStream = $stream; $tar.WriteEntry($entry) } finally { $stream.Dispose() }
            }
        } finally { $tar.Dispose() }
    } finally { $gzip.Dispose() }
} finally { $archive.Dispose() }
Write-Output ('Standalone Linux: ' + $releaseArchive)
Write-Output ('SHA256: ' + (Get-FileHash -LiteralPath $releaseBinary -Algorithm SHA256).Hash)
