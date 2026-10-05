# Builds the release zip: a self-contained Clayo, so the PC needs no .NET, in a Clayo\ folder.
# Unzipped into %LOCALAPPDATA%\Programs it lands at %LOCALAPPDATA%\Programs\Clayo.
# From the repo root:  .\scripts\publish.ps1
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\Clayo\CcxShell.csproj'
$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> in $project" }

$out = Join-Path $root 'publish'
$folder = Join-Path $out 'Clayo'
$zip = Join-Path $out "clayo-$version-win-x64.zip"
if (Test-Path $folder) { Remove-Item $folder -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }
if (Test-Path "$zip.sha256") { Remove-Item "$zip.sha256" -Force }

# English only: Clayo's own text is, so WPF's other languages are dead weight.
dotnet publish $project -c Release -r win-x64 --self-contained true -p:SatelliteResourceLanguages=en -o $folder
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

# The terminal pane loads Assets\ from next to the exe (TerminalPane), so both must be there.
foreach ($need in 'clayo.exe', 'Assets\terminal.html', 'Assets\xterm\xterm.js') {
    if (-not (Test-Path (Join-Path $folder $need))) { throw "Missing from the build: $need" }
}

# Entry by entry, with forward slashes: Windows PowerShell 5.1's Compress-Archive and ZipFile
# both write backslashes, which some unzip tools turn into files named "Clayo\clayo.exe".
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem $folder -Recurse -File) {
        $name = 'Clayo/' + $file.FullName.Substring($folder.Length + 1).Replace('\', '/')
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $name, [IO.Compression.CompressionLevel]::Optimal)
    }
}
finally { $archive.Dispose() }

# Uploaded beside the zip: install.ps1 refuses a zip whose hash is not this one.
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content "$zip.sha256" "$hash  $(Split-Path $zip -Leaf)" -Encoding ascii -NoNewline
$folderMb = (Get-ChildItem $folder -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
$zipMb = (Get-Item $zip).Length / 1MB
'Clayo {0}: {1:N0} MB in {2}, zipped to {3} ({4:N0} MB), SHA-256 {5}' -f $version, $folderMb, $folder, $zip, $zipMb, $hash
