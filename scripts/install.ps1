# Installs Clayo, or updates it, into %LOCALAPPDATA%\Programs\Clayo, then starts it.
#   irm https://raw.githubusercontent.com/YogiOnCode/Clayo/main/scripts/install.ps1 | iex
# Clayo's Update button runs this too, as it quits. Settings live in %LOCALAPPDATA%\Clayo and are kept.
$ErrorActionPreference = 'Stop'
# Windows PowerShell's progress bar slows a download to a crawl.
$ProgressPreference = 'SilentlyContinue'

# The Update button names the folder the running Clayo is in, which may be elsewhere.
$dir = if ($env:CLAYO_DIR) { $env:CLAYO_DIR.TrimEnd('\') } else { Join-Path $env:LOCALAPPDATA 'Programs\Clayo' }
$programs = Split-Path $dir -Parent
$exe = Join-Path $dir 'clayo.exe'
# Unpacked next to the install, so the swap is a rename on one drive.
$staging = Join-Path $programs ('Clayo-update-' + [guid]::NewGuid().ToString('N'))
$zip = "$staging.zip"
$old = "$staging-old"

function Get-Clayo { @(Get-Process clayo -ErrorAction SilentlyContinue | Where-Object Path -eq $exe) }

# Run by hand, a running Clayo is left alone: the script cannot tell whether a session in it
# is mid-task. The Update button waits for those, then quits Clayo and runs this.
if (-not $env:CLAYO_DIR -and (Get-Clayo)) {
    Write-Host 'Clayo is running. Quit it from the gear menu, then run this again.' -ForegroundColor Yellow
    return
}

try {
    $release = Invoke-RestMethod 'https://api.github.com/repos/YogiOnCode/Clayo/releases/latest'
    $asset = $release.assets | Where-Object name -like '*-win-x64.zip' | Select-Object -First 1
    if (-not $asset) { throw "Clayo $($release.tag_name) has no win-x64 zip." }

    Write-Host "Downloading Clayo $($release.tag_name)..."
    New-Item -ItemType Directory -Force $programs | Out-Null
    Invoke-WebRequest $asset.browser_download_url -OutFile $zip -UseBasicParsing
    Expand-Archive $zip $staging -Force

    # The Update button started this as Clayo quit; it holds its files open until it is gone.
    # Nothing in it was busy, so one that hangs on the way out is closed.
    $running = Get-Clayo
    if ($running) {
        Write-Host 'Waiting for Clayo to close...'
        $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
        $running | Where-Object { -not $_.HasExited } | Stop-Process -Force
        $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
    }

    # Renaming the old folder away fails as a whole if anything in it is still in use, so a
    # failed update leaves the old Clayo as it was. A swap rather than an unzip over the top,
    # so files a new version dropped do not linger.
    if (Test-Path $dir) { Rename-Item $dir (Split-Path $old -Leaf) }
    Move-Item (Join-Path $staging 'Clayo') $dir
    Remove-Item $old -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "Clayo $($release.tag_name) is installed in $dir."
}
catch {
    # Put the old Clayo back if it was moved away and the new one never landed.
    if ((Test-Path $old) -and -not (Test-Path $dir)) { Rename-Item $old (Split-Path $dir -Leaf) }
    throw
}
finally {
    Remove-Item $zip, $staging -Recurse -Force -ErrorAction SilentlyContinue
    # The new Clayo, or the old one again if the update failed.
    if (Test-Path $exe) { Start-Process $exe }
}
