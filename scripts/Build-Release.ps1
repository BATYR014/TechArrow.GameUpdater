param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '0.3.2',
    [string]$UpdateSource = 'https://github.com/BATYR014/TechArrow.GameUpdater'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$publishPath = Join-Path $projectRoot "artifacts\publish\$Version"
$releasePath = Join-Path $projectRoot 'artifacts\Releases'
Push-Location $projectRoot
try {
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'Could not restore release tooling.' }
    dotnet publish src/TechArrow.GameUpdater/TechArrow.GameUpdater.csproj -c Release -r win-x64 --self-contained true -p:Version=$Version -o $publishPath
    if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }
    $config = @{ Source = $UpdateSource } | ConvertTo-Json
    [System.IO.File]::WriteAllText((Join-Path $publishPath 'update-source.json'), $config, [System.Text.UTF8Encoding]::new($false))
    dotnet tool run vpk -- pack --packId TechArrow.GameUpdater --packVersion $Version --packDir $publishPath --mainExe TechArrow.GameUpdater.exe --packTitle 'TechArrow Game Updater' --outputDir $releasePath --channel win --runtime win-x64
    if ($LASTEXITCODE -ne 0) { throw 'Installer packaging failed.' }
    Write-Output "Release files: $releasePath"
    if ([string]::IsNullOrWhiteSpace($UpdateSource)) { Write-Output 'Update source is not configured yet. Configure the same source on every PC or rebuild with -UpdateSource.' }
}
finally { Pop-Location }
