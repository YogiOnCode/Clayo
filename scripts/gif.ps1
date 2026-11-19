# Records a short demo GIF of Clayo into media\<Name>.gif.
#   .\scripts\gif.ps1 pages            # the Clayo window, 12 s
#   .\scripts\gif.ps1 island -Island   # the top-centre strip of the screen, where the island drops
#   .\scripts\gif.ps1 usage -Seconds 8
# Recording starts after a 3 s countdown. Needs ffmpeg on PATH.
param(
    [Parameter(Mandatory)][string]$Name,
    [int]$Seconds = 12,
    [switch]$Island,
    [int]$Width = 960
)
$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot "..\media\$Name.gif"
$raw = Join-Path $env:TEMP "clayo-gif-$Name.mp4"

if ($Island) { $src = @('-i', 'desktop'); $crop = 'crop=1100:300:(iw-1100)/2:0,' }
else         { $src = @('-i', 'title=C L A Y O'); $crop = '' }

3..1 | ForEach-Object { Write-Host "Recording in $_..."; Start-Sleep 1 }
Write-Host "Recording $Seconds s. Do the flow now."
ffmpeg -loglevel error -y -f gdigrab -framerate 30 -draw_mouse 1 @src -t $Seconds -c:v libx264 -crf 12 -pix_fmt yuv444p $raw

$vf = "${crop}fps=12,scale=${Width}:-1:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle"
ffmpeg -loglevel error -y -i $raw -vf $vf $out
Remove-Item $raw
"{0}  {1:N1} MB" -f (Resolve-Path $out), ((Get-Item $out).Length / 1MB)
