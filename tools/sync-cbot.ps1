# Copy the cBot source from this repo into cTrader's cBot folder,
# and create Documents\BacktestLab\config.json on first run.
# Usage (from the repo folder):  powershell -ExecutionPolicy Bypass -File tools\sync-cbot.ps1

$ErrorActionPreference = "Stop"
$docs = [Environment]::GetFolderPath("MyDocuments")
$src  = Join-Path $PSScriptRoot "..\cbot\BacktestLab"
$dst  = Join-Path $docs "cAlgo\Sources\Robots\BacktestLab\BacktestLab"

if (-not (Test-Path $dst)) {
    Write-Host "Not found: $dst" -ForegroundColor Yellow
    Write-Host "Open cTrader > Algo > cBots > New, name it exactly 'BacktestLab', save, then run this script again."
    exit 1
}

Copy-Item -Path (Join-Path $src "*") -Destination $dst -Recurse -Force
Write-Host "Copied cBot source to $dst" -ForegroundColor Green

$cfgDir  = Join-Path $docs "BacktestLab"
$cfgFile = Join-Path $cfgDir "config.json"
if (-not (Test-Path $cfgFile)) {
    New-Item -ItemType Directory -Force -Path $cfgDir | Out-Null
    $api = Read-Host "Lab API URL (https://<project-ref>.supabase.co/functions/v1/lab)"
    $key = Read-Host "INGEST_KEY"
    @{ apiUrl = $api.Trim(); ingestKey = $key.Trim() } | ConvertTo-Json | Set-Content -Encoding UTF8 $cfgFile
    Write-Host "Created $cfgFile" -ForegroundColor Green
}

try {
    $cfg = Get-Content $cfgFile -Raw | ConvertFrom-Json
    $h = Invoke-RestMethod -Uri ($cfg.apiUrl.TrimEnd('/') + "/health")
    if ($h.missing.Count -gt 0) { Write-Host ("Lab is up, missing secrets: " + ($h.missing -join ", ")) -ForegroundColor Yellow }
    else { Write-Host "Lab is up and fully configured." -ForegroundColor Green }
} catch {
    Write-Host "Could not reach the lab API: $($_.Exception.Message)" -ForegroundColor Yellow
}

Write-Host "Now open cTrader, select the BacktestLab cBot and press Build."
