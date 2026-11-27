# Runs the spike. Must be an Administrator PowerShell.
# Examples:
#   .\run.ps1                                   (msedge, 500 KB/s, until Ctrl+C)
#   .\run.ps1 --limit-kbps 1000 --seconds 60
#   .\run.ps1 --crash-after 30                  (crash test)
Set-Location $PSScriptRoot
& .\out\FloodgateSpike.exe @args
Write-Host "Exit code: $LASTEXITCODE"
