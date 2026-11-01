param([ValidatePattern('^\d+\.\d+\.\d+\.\d+$')][string]$ReleaseVersion)
$ErrorActionPreference = 'Stop'
$releaseRoot = Split-Path -Parent $PSScriptRoot
$releaseProject = Join-Path $releaseRoot 'LeitstellenPilot/LeitstellenPilot.csproj'
$releaseOutput = Join-Path $releaseRoot 'publish/standalone-win-x64'
$previousResolver = [Environment]::GetEnvironmentVariable('MSBuildEnableWorkloadResolver', 'Process')
try {
    [Environment]::SetEnvironmentVariable('MSBuildEnableWorkloadResolver', 'false', 'Process')
    $versionArguments = @(); if ($ReleaseVersion) { $versionArguments += ('-p:ReleaseVersion=' + $ReleaseVersion) }
    & dotnet publish $releaseProject -c Release -r win-x64 --self-contained true '-p:PublishProfile=Standalone-WinX64' '-p:UseSharedCompilation=false' @versionArguments -m:1 -v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'Windows-Publish fehlgeschlagen.' }
} finally { [Environment]::SetEnvironmentVariable('MSBuildEnableWorkloadResolver', $previousResolver, 'Process') }
$releaseExe = Join-Path $releaseOutput 'LeitstellenPilot.exe'
$legacyManifest = $releaseExe + '.update.json'
if (Test-Path -LiteralPath $legacyManifest) { Remove-Item -LiteralPath $legacyManifest }
foreach ($notice in @('README.md','THIRD_PARTY_NOTICES.md','THIRD_PARTY_LICENSES.txt','LICENSE')) {
    $noticePath = Join-Path $releaseRoot $notice
    if (Test-Path -LiteralPath $noticePath) { Copy-Item -LiteralPath $noticePath -Destination (Join-Path $releaseOutput $notice) -Force }
}
$releaseZip = Join-Path $releaseRoot 'publish/LeitstellenPilot-windows-x64.zip'
Compress-Archive -LiteralPath $releaseExe,(Join-Path $releaseOutput 'README.md'),(Join-Path $releaseOutput 'THIRD_PARTY_NOTICES.md'),(Join-Path $releaseOutput 'THIRD_PARTY_LICENSES.txt'),(Join-Path $releaseOutput 'LICENSE') -DestinationPath $releaseZip -Force
Write-Output ('Standalone Windows: ' + $releaseExe)
Write-Output ('Paket: ' + $releaseZip)
Write-Output ('SHA256: ' + (Get-FileHash -LiteralPath $releaseExe -Algorithm SHA256).Hash)
