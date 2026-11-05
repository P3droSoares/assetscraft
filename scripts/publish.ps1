# Gera executáveis self-contained em dist\<rid>\
# Uso: .\scripts\publish.ps1 [-Rid win-x64]
param([string[]]$Rid = @("win-x64", "linux-x64"))
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")
foreach ($r in $Rid) {
  Write-Host ">> $r"
  if (-not (Test-Path "vendor/ffmpeg/$r/bin")) { Write-Warning "FFmpeg embutido ausente: rode scripts/fetch-ffmpeg.sh $r (Git Bash/WSL) antes" }
  dotnet publish src/AssetsCraft.Desktop -c Release -r $r --self-contained -p:DebugType=none `
    -o "dist/$r"
}
