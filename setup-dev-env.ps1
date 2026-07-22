# Setup development environment for CampaignVault
# Initializes git hooks, git lfs, and fetches the embedding model

Write-Host "Setting up CampaignVault development environment..." -ForegroundColor Cyan

# 1. Verify git lfs is installed
Write-Host "`n1. Checking git lfs installation..." -ForegroundColor Yellow
$lfsVersion = git lfs version 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Git LFS is not installed. Visit: https://git-lfs.com" -ForegroundColor Red
    exit 1
}
Write-Host "OK - Git LFS is installed: $lfsVersion" -ForegroundColor Green

# 2. Initialize git lfs in this repo (idempotent)
Write-Host "`n2. Initializing git lfs in repository..." -ForegroundColor Yellow
git lfs install --local
Write-Host "OK - Git LFS initialized locally" -ForegroundColor Green

# 3. Fetch embedding model from lfs
Write-Host "`n3. Fetching embedding model from LFS..." -ForegroundColor Yellow
$modelPath = "models/embedding/model.onnx"
if (!(Test-Path $modelPath)) {
    Write-Host "  Model file not found locally, fetching..." -ForegroundColor Cyan
    git lfs fetch --include="$modelPath" --all
}
Write-Host "OK - Embedding model ready at: $modelPath" -ForegroundColor Green

Write-Host "`nDone! Development environment ready." -ForegroundColor Green
Write-Host "`nYou can now:"
Write-Host "  - Run 'dotnet build' to build the project"
Write-Host "  - Run 'dotnet test' to run tests"
