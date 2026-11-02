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
$releaseConfig = Join-Path $releaseOutput 'config.example.json'
$exampleConfig = Get-Content -LiteralPath (Join-Path $releaseRoot 'deploy/linux/config.example.json') -Raw | ConvertFrom-Json
$exampleConfig.Headless = $false
$exampleConfig.BotSettings.AutoStart = $false
[IO.File]::WriteAllText($releaseConfig, ($exampleConfig | ConvertTo-Json -Depth 10) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
$releaseZip = Join-Path $releaseRoot 'publish/LeitstellenPilot-windows-x64.zip'
Compress-Archive -LiteralPath $releaseExe,$releaseConfig -DestinationPath $releaseZip -Force
Write-Output ('Standalone Windows: ' + $releaseExe)
Write-Output ('Paket: ' + $releaseZip)
Write-Output ('SHA256: ' + (Get-FileHash -LiteralPath $releaseExe -Algorithm SHA256).Hash)
