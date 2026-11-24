# Builds Konbini.exe using the C# compiler that ships with Windows (.NET Framework 4.x).
# Nothing to install - for you or for end users. Windows Runtime APIs (media, PDF, imaging) come
# from the metadata files Windows itself ships in System32\WinMetadata.
$ErrorActionPreference = 'Stop'
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path "$fw\csc.exe")) { $fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319' }
$wm = Join-Path $env:WINDIR 'System32\WinMetadata'
New-Item -ItemType Directory -Force "$PSScriptRoot\dist" | Out-Null
# Only small dependencies are embedded (upscaler model, Lite background model). Large ones (ffmpeg, the
# full-precision background model) are downloaded at runtime by src\Deps\DepManager.cs from the hash-pinned
# URLs in src\Deps\Dependency.cs - they are NOT part of the build.
$src = Get-ChildItem "$PSScriptRoot\src" -Recurse -Filter *.cs | ForEach-Object { $_.FullName }
$refs = @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Xml.dll', 'System.Xml.Linq.dll',
          'System.IO.Compression.dll', 'System.IO.Compression.FileSystem.dll', 'System.Web.Extensions.dll', "$fw\System.Runtime.dll", "$fw\System.Runtime.WindowsRuntime.dll") |
        ForEach-Object { "/r:$_" }
$winmd = 'Foundation', 'Storage', 'Media', 'Data', 'Graphics', 'UI', 'AI' | ForEach-Object { "/r:$wm\Windows.$_.winmd" }
& "$fw\csc.exe" /nologo /target:winexe /optimize+ /unsafe /platform:anycpu "/win32icon:$PSScriptRoot\installer\konbini.ico" "/resource:$PSScriptRoot\models\realesr-general-x4v3.onnx,Konbini.upscale.onnx" "/resource:$PSScriptRoot\models\u2netp.onnx,Konbini.bglite.onnx" "/out:$PSScriptRoot\dist\Konbini.exe" $refs $winmd $src
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Write-Host "Built dist\Konbini.exe"
