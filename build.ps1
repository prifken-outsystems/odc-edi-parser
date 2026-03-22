#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Local build script for EdiParserLibrary ODC External Logic project.

.DESCRIPTION
    1. Restores NuGet packages
    2. Builds in Release configuration
    3. Publishes for linux-x64 runtime (ODC Lambda requirement)
    4. Creates deployment ZIP

.PARAMETER Clean
    Clean build outputs before building.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Clean
#>

param(
    [switch]$Clean
)

$ErrorActionPreference = "Stop"

$projectFile = "EdiParserLibrary.csproj"
$configuration = "Release"
$runtime = "linux-x64"
$publishDir = "bin\publish"
$zipFile = "EdiParserLibrary.zip"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "EdiParserLibrary — Local Build Script" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

if ($Clean) {
    Write-Host "[1/4] Cleaning previous build outputs..." -ForegroundColor Yellow
    if (Test-Path "bin") { Remove-Item -Path "bin" -Recurse -Force }
    if (Test-Path "obj") { Remove-Item -Path "obj" -Recurse -Force }
    if (Test-Path $zipFile) { Remove-Item -Path $zipFile -Force }
    Write-Host "      Clean completed" -ForegroundColor Green
} else {
    Write-Host "[1/4] Skipping clean (use -Clean to clean first)" -ForegroundColor Gray
}

Write-Host ""
Write-Host "[2/4] Restoring NuGet packages..." -ForegroundColor Yellow
dotnet restore $projectFile
if ($LASTEXITCODE -ne 0) { Write-Host "ERROR: Restore failed" -ForegroundColor Red; exit 1 }
Write-Host "      Restore completed" -ForegroundColor Green

Write-Host ""
Write-Host "[3/4] Building in Release configuration..." -ForegroundColor Yellow
dotnet build $projectFile --configuration $configuration --no-restore
if ($LASTEXITCODE -ne 0) { Write-Host "ERROR: Build failed" -ForegroundColor Red; exit 1 }
Write-Host "      Build completed" -ForegroundColor Green

Write-Host ""
Write-Host "[4/4] Publishing for linux-x64 (ODC Lambda)..." -ForegroundColor Yellow
dotnet publish $projectFile `
    --configuration $configuration `
    --runtime $runtime `
    --output $publishDir `
    --no-build `
    --self-contained true

if ($LASTEXITCODE -ne 0) { Write-Host "ERROR: Publish failed" -ForegroundColor Red; exit 1 }
Write-Host "      Publish completed" -ForegroundColor Green

Write-Host ""
Write-Host "[5/5] Creating deployment ZIP..." -ForegroundColor Yellow
if (Test-Path $zipFile) { Remove-Item -Path $zipFile -Force }
Compress-Archive -Path "$publishDir\*" -DestinationPath $zipFile -CompressionLevel Optimal

if (Test-Path $zipFile) {
    $zipSize = (Get-Item $zipFile).Length / 1MB
    Write-Host "      ZIP created: $zipFile ($("{0:N2}" -f $zipSize) MB)" -ForegroundColor Green
} else {
    Write-Host "ERROR: Failed to create ZIP" -ForegroundColor Red; exit 1
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "BUILD SUCCESSFUL" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Deployment package: $zipFile"
Write-Host "Push to main to trigger automated deployment to ODC."
Write-Host ""
