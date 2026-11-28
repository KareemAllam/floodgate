# Downloads the latest WinDivert release and builds the spike into .\out
# Run in a normal or admin PowerShell, from this folder. Needs internet and the .NET SDK.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

Write-Host "1/3 Getting WinDivert..."
$dl = Join-Path $PSScriptRoot 'windivert-dl'
New-Item -ItemType Directory -Force $dl | Out-Null
$zip = Get-ChildItem $dl -Filter '*.zip' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($zip) {
    Write-Host "    Using the zip you placed here: $($zip.Name)"
} else {
    try {
        $rel = Invoke-RestMethod 'https://api.github.com/repos/basil00/WinDivert/releases/latest' -Headers @{ 'User-Agent' = 'floodgate-spike' }
        $asset = $rel.assets | Where-Object { $_.name -match '\.zip$' } | Sort-Object { $_.name -notmatch '-A\.zip$' } | Select-Object -First 1
        if (-not $asset) { throw "No .zip asset found on release $($rel.tag_name)." }
        Write-Host "    Downloading $($asset.name) from $($rel.tag_name)"
        Invoke-WebRequest $asset.browser_download_url -OutFile (Join-Path $dl $asset.name)
    } catch {
        Write-Host ""
        Write-Host "Could not download WinDivert automatically: $($_.Exception.Message)" -ForegroundColor Yellow
        Write-Host "Manual step: in Edge open https://github.com/basil00/WinDivert/releases/latest ,"
        Write-Host "download the WinDivert-*-A.zip file, put it in this folder:"
        Write-Host "    $dl"
        Write-Host "then run setup.ps1 again."
        exit 1
    }
    $zip = Get-ChildItem $dl -Filter '*.zip' | Select-Object -First 1
}
Expand-Archive $zip.FullName -DestinationPath (Join-Path $dl 'unzipped') -Force

Write-Host "2/3 Building the spike..."
dotnet build -c Release -o out
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed. Copy the error text and send it to Claude." }

Write-Host "3/3 Copying the 64-bit driver files next to the program..."
$dll = Get-ChildItem (Join-Path $dl 'unzipped') -Recurse -Filter 'WinDivert.dll' | Where-Object { $_.FullName -match 'x64' } | Select-Object -First 1
$sys = Get-ChildItem (Join-Path $dl 'unzipped') -Recurse -Filter 'WinDivert64.sys' | Select-Object -First 1
if (-not $dll -or -not $sys) { throw "Could not find x64 WinDivert.dll / WinDivert64.sys in the download." }
Copy-Item $dll.FullName, $sys.FullName -Destination (Join-Path $PSScriptRoot 'out')
Write-Host "Done. Files in .\out:"
Get-ChildItem out | Where-Object { $_.Name -match 'WinDivert|FloodgateSpike' } | Select-Object Name, Length | Format-Table
Write-Host "Next: open PowerShell AS ADMINISTRATOR in this folder and run  .\run.ps1"
