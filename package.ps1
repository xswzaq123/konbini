# Builds the shareable release:
#   dist\release\Konbini-Setup-<version>.exe     per-user installer (no admin), Start menu + Apps & features entry
#   dist\release\Konbini-<version>-portable.zip  extract-and-run; keeps its data next to the exe
# Uses only the in-box C# compiler, like build.ps1.
$ErrorActionPreference = 'Stop'
& "$PSScriptRoot\build.ps1"
$version = ([regex]::Match((Get-Content "$PSScriptRoot\src\AppInfo.cs" -Raw), 'Version = "([\d.]+)"')).Groups[1].Value
$setupVersion = ([regex]::Match((Get-Content "$PSScriptRoot\installer\Setup.cs" -Raw), 'Version = "([\d.]+)"')).Groups[1].Value
if ($version -ne $setupVersion) { throw "Version mismatch: src\AppInfo.cs says $version, installer\Setup.cs says $setupVersion" }

$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path "$fw\csc.exe")) { $fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319' }
$out = "$PSScriptRoot\dist\release"; $work = "$PSScriptRoot\dist\pkg"
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $out, $work | Out-Null
$refs = 'System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll' | ForEach-Object { "/r:$_" }
$icon = "/win32icon:$PSScriptRoot\installer\konbini.ico"
if (-not (Test-Path "$PSScriptRoot\installer\konbini.ico")) { $icon = $null }

function Gzip($src, $dst) {
    $in = [IO.File]::OpenRead($src); $o = [IO.File]::Create($dst)
    $gz = New-Object IO.Compression.GZipStream($o, [IO.Compression.CompressionLevel]::Optimal)
    $in.CopyTo($gz); $gz.Dispose(); $o.Dispose(); $in.Dispose()
}

# 1. Uninstaller
& "$fw\csc.exe" /nologo /target:winexe /optimize+ /platform:anycpu /define:UNINSTALLER $icon "/out:$work\Uninstall.exe" $refs "$PSScriptRoot\installer\Setup.cs"
if ($LASTEXITCODE -ne 0) { throw "Uninstaller build failed" }

# 2. Installer with the payload embedded (gzipped)
Gzip "$PSScriptRoot\dist\Konbini.exe" "$work\Konbini.exe.gz"
Gzip "$work\Uninstall.exe" "$work\Uninstall.exe.gz"
Gzip "$PSScriptRoot\installer\THIRD-PARTY-NOTICES.txt" "$work\THIRD-PARTY-NOTICES.txt.gz"
$setup = "$out\Konbini-Setup-$version.exe"
& "$fw\csc.exe" /nologo /target:winexe /optimize+ /platform:anycpu $icon "/out:$setup" $refs `
    "/resource:$work\Konbini.exe.gz,payload.Konbini.exe" "/resource:$work\Uninstall.exe.gz,payload.Uninstall.exe" `
    "/resource:$work\THIRD-PARTY-NOTICES.txt.gz,payload.THIRD-PARTY-NOTICES.txt" "$PSScriptRoot\installer\Setup.cs"
if ($LASTEXITCODE -ne 0) { throw "Installer build failed" }

# 3. Portable zip
$zip = "$out\Konbini-$version-portable.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$z = [IO.Compression.ZipFile]::Open($zip, 'Create')
try {
    $root = "Konbini-$version"
    [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($z, "$PSScriptRoot\dist\Konbini.exe", "$root/Konbini.exe", 'Optimal')
    [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($z, "$PSScriptRoot\installer\README-portable.txt", "$root/README.txt", 'Optimal')
    [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($z, "$PSScriptRoot\installer\THIRD-PARTY-NOTICES.txt", "$root/THIRD-PARTY-NOTICES.txt", 'Optimal')
    $e = $z.CreateEntry("$root/Konbini.portable"); $w = New-Object IO.StreamWriter($e.Open())
    $w.Write("This file makes Konbini keep its settings and downloads in the 'Konbini Data' folder next to Konbini.exe. Delete it to use your user profile instead."); $w.Dispose()
}
finally { $z.Dispose() }

Remove-Item $work -Recurse -Force
foreach ($f in $setup, $zip) {
    $h = (Get-FileHash $f -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host ("{0}  {1:N1} MB  sha256 {2}" -f (Split-Path $f -Leaf), ((Get-Item $f).Length / 1MB), $h)
}
