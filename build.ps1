# Builds the client release package, then deploys it to each available SPT install.
# Existing install config files are preserved; unavailable or locked paths are skipped with a warning.

$ErrorActionPreference = 'Stop'

$repoRoot    = Split-Path -Parent $MyInvocation.MyCommand.Path
$packageRoot = Join-Path $repoRoot 'Build\KMABodyDrag'
$bepinDir    = Join-Path $packageRoot 'BepInEx'
$deployRoots = @('C:\SPT', 'D:\SPT', 'Y:\SPT', 'Y:\NsmServers\Tarkov2.0Test\Host1')

Set-Location $repoRoot

Write-Host "Deleting BepInEx folder" -ForegroundColor Cyan
Remove-Item -Recurse -Force $bepinDir -ErrorAction SilentlyContinue

Write-Host "Recreating output folders" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path (Join-Path $bepinDir 'plugins') | Out-Null

Write-Host "Building KMABodyDrag-Core (Release)" -ForegroundColor Cyan
dotnet build 'KMABodyDrag-Core/KMABodyDrag.csproj' --configuration Release --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw "KMABodyDrag-Core build failed" }

Write-Host "Build complete: $packageRoot" -ForegroundColor Green

# Package folder -> install folder, relative to the package / install root.
$deployDirs = @(
    'BepInEx\plugins'
)

$skipped = @()
foreach ($root in $deployRoots) {
    if (-not (Test-Path $root)) {
        Write-Warning "Skipping $root (not found)"
        $skipped += $root
        continue
    }

    Write-Host "Deploying to $root" -ForegroundColor Cyan
    Remove-Item -Force (Join-Path $root 'BepInEx\plugins\KMABodyDrag.dll') -ErrorAction SilentlyContinue
    Remove-Item -Force (Join-Path $root 'BepInEx\plugins\KMABodyDrag\KMABodyDrag.dll'), (Join-Path $root 'BepInEx\plugins\KMABodyDrag\Localizations\en.json') -ErrorAction SilentlyContinue
    foreach ($dir in $deployDirs) {
        $target = Join-Path $root $dir
        try {
            New-Item -ItemType Directory -Force -Path $target | Out-Null
            Copy-Item -Path (Join-Path (Join-Path $packageRoot $dir) '*') -Destination $target -Recurse -Force
        }
        catch {
            Write-Warning "Deploy to $target failed (is the game running?): $($_.Exception.Message)"
            $skipped += $target
        }
    }
}

if ($skipped.Count -gt 0) {
    Write-Host "Not deployed to: $($skipped -join ', ')" -ForegroundColor Yellow
}
else {
    Write-Host "Deployed to: $($deployRoots -join ', ')" -ForegroundColor Green
}
