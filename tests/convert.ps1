# End-to-end converter test: generates sample files, converts across formats and checks each
# output's real type by content. Usage: powershell -MTA -NoProfile -ExecutionPolicy Bypass -File tests\convert.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot
$work = Join-Path $env:TEMP ("konbini-conv-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory "$work\out" -Force | Out-Null
$asm = [Reflection.Assembly]::LoadFile("$root\dist\Konbini.exe")
$Det = $asm.GetType('Konbini.Conversion.FileDetector'); $Conv = $asm.GetType('Konbini.Conversion.Converters')
$opts = $asm.CreateInstance('Konbini.Conversion.ConvertOptions')
$reserve = [Func[string, string]] { param($p) $p }
$script:fails = 0; $script:passes = 0
function Check($name, $cond, $detail = '') {
    if ($cond) { $script:passes++; Write-Host "PASS $name" } else { $script:fails++; Write-Host "FAIL $name $detail" -ForegroundColor Red }
}
function Kind($path) { $Det::Detect($path) }

# Converts $src to $target; checks the output exists and is detected as $expect (defaults to $target).
function Conv($src, $target, $expect = $target, [switch]$Quiet) {
    $k = Kind $src
    $name = [IO.Path]::GetFileNameWithoutExtension($src) -replace '\.tar$', ''
    $base = Join-Path "$work\out" ("{0}_{1}_to_{2}" -f $name, $k.Id, $target)
    try {
        $res = $Conv::Convert($src, $k, $target, $base, $opts, $reserve)
        $out = Get-ChildItem "$work\out" -File | Where-Object { $_.FullName.StartsWith($base) } | Sort-Object Name | Select-Object -First 1
        if (-not $out) { Check "$($k.Id) -> $target" $false 'no output'; return $null }
        $got = (Kind $out.FullName).Id
        Check "$($k.Id) -> $target ($([math]::Round($out.Length/1KB,1)) KB)" ($got -eq $expect -and $out.Length -gt 0) "detected=$got  $res"
        return $out.FullName
    } catch {
        $msg = $_.Exception.InnerException.Message; if (-not $msg) { $msg = $_.Exception.Message }
        Check "$($k.Id) -> $target" $false "ERROR: $msg"
        return $null
    }
}

Write-Host "Work folder: $work`n"

# ---------- sample inputs ----------
# WAV: 3 s, two tones
$sr = 44100; $n = $sr * 3; $ms = New-Object IO.MemoryStream; $w = New-Object IO.BinaryWriter $ms
$w.Write([Text.Encoding]::ASCII.GetBytes('RIFF')); $w.Write([int](36 + $n * 2)); $w.Write([Text.Encoding]::ASCII.GetBytes('WAVEfmt '))
$w.Write([int]16); $w.Write([int16]1); $w.Write([int16]1); $w.Write([int]$sr); $w.Write([int]($sr * 2)); $w.Write([int16]2); $w.Write([int16]16)
$w.Write([Text.Encoding]::ASCII.GetBytes('data')); $w.Write([int]($n * 2))
for ($i = 0; $i -lt $n; $i++) { $w.Write([int16](6000 * [Math]::Sin(2 * [Math]::PI * (440 + 220 * [int]($i -gt $n / 2)) * $i / $sr))) }
[IO.File]::WriteAllBytes("$work\tone.wav", $ms.ToArray())

$b = New-Object Drawing.Bitmap 640, 400; $g = [Drawing.Graphics]::FromImage($b)
$g.Clear([Drawing.Color]::White); $g.FillEllipse([Drawing.Brushes]::OrangeRed, 100, 60, 280, 280); $g.FillRectangle([Drawing.Brushes]::SteelBlue, 360, 120, 220, 160)
$g.Dispose(); $b.Save("$work\art.png", [Drawing.Imaging.ImageFormat]::Png); $b.Dispose()

$md = @"
# Quarterly Report

Intro paragraph with **bold** text and a [link](https://example.com).

## Highlights

- Revenue up 12%
- New office opened
  - in Berlin

| Region | Sales | Growth |
| --- | --- | --- |
| North | 1200 | 5% |
| South | 950 | 9% |

``````
code block line 1
``````

Closing paragraph.
"@
Set-Content "$work\report.md" $md -Encoding UTF8
Set-Content "$work\page.html" "<html><head><title>Page</title><style>p{color:red}</style></head><body><h1>Hello</h1><p>First &amp; second.</p><ul><li>One</li><li>Two</li></ul><table><tr><th>A</th><th>B</th></tr><tr><td>1</td><td>2</td></tr></table></body></html>" -Encoding UTF8
Set-Content "$work\notes.txt" (("Line of plain text number {0} that is long enough to wrap across the page width in a PDF document. " * 3 + "`r`n`r`n") -f 1 * 60) -Encoding UTF8
Set-Content "$work\memo.rtf" '{\rtf1\ansi{\fonttbl{\f0 Arial;}}\f0 Hello \b world\b0 .\par Second line with caf\''e9.\par}' -Encoding ASCII
Set-Content "$work\people.csv" "name,age,city`r`nAda,36,London`r`n""Lovelace, Jr."",12,""New """"York""""""`r`nAlan,41,Wilmslow" -Encoding UTF8
Set-Content "$work\data.json" '[{"id":1,"name":"Pen","tags":["a","b"],"price":{"usd":1.5}},{"id":2,"name":"Ink","price":{"usd":3}}]' -Encoding UTF8
Set-Content "$work\feed.xml" '<?xml version="1.0"?><catalog><book id="1"><title>Dune</title><year>1965</year></book><book id="2"><title>Emma</title><year>1815</year></book></catalog>' -Encoding UTF8
Set-Content "$work\subs.srt" "1`r`n00:00:01,000 --> 00:00:02,500`r`nHello there`r`n`r`n2`r`n00:00:03,000 --> 00:00:04,000`r`nSecond line" -Encoding UTF8
[IO.File]::WriteAllBytes("$work\mystery.bin", [byte[]](0..255))

# ---------- detection ----------
Check 'detect wav' ((Kind "$work\tone.wav").Id -eq 'wav')
Check 'detect png' ((Kind "$work\art.png").Id -eq 'png')
Check 'detect md' ((Kind "$work\report.md").Id -eq 'md')
Check 'detect html' ((Kind "$work\page.html").Id -eq 'html')
Check 'detect csv' ((Kind "$work\people.csv").Id -eq 'csv')
Check 'detect json' ((Kind "$work\data.json").Id -eq 'json')
Check 'detect rtf' ((Kind "$work\memo.rtf").Id -eq 'rtf')
Copy-Item "$work\art.png" "$work\renamed.dat"
Check 'detect by content, not extension (png named .dat)' ((Kind "$work\renamed.dat").Id -eq 'png')
Check 'unknown binary has no conversions except compress' (@($Conv::TargetsFor((Kind "$work\mystery.bin")) | ForEach-Object Id) -join ',' -eq 'zip,gz')

# ---------- images ----------
foreach ($t in 'jpg', 'gif', 'bmp', 'tif', 'ico', 'pdf', 'jxr') { $null = Conv "$work\art.png" $t }
$jxr = Get-ChildItem "$work\out\art_png_to_jxr*" | Select-Object -First 1
if ($jxr) { $null = Conv $jxr.FullName 'png' }   # decode via Windows imaging
$ico = Get-ChildItem "$work\out\art_png_to_ico*" | Select-Object -First 1
if ($ico) { $null = Conv $ico.FullName 'png' }
$null = Conv "$work\renamed.dat" 'jpg'

# ---------- PDF ----------
$imgPdf = Get-ChildItem "$work\out\art_png_to_pdf*" | Select-Object -First 1
if ($imgPdf) { $null = Conv $imgPdf.FullName 'png'; $null = Conv $imgPdf.FullName 'jpg' }
$txtPdf = Conv "$work\notes.txt" 'pdf'
if ($txtPdf) {
    $null = Conv $txtPdf 'png'
    $pages = @(Get-ChildItem "$work\out" -Filter "*_pdf_to_png_page*")
    Check 'multi-page PDF -> one PNG per page' ($pages.Count -ge 2) "pages=$($pages.Count)"
}

# ---------- audio ----------
$mp3 = Conv "$work\tone.wav" 'mp3'
foreach ($t in 'm4a', 'wma', 'flac') { $null = Conv "$work\tone.wav" $t }
if ($mp3) { $null = Conv $mp3 'wav'; $null = Conv $mp3 'flac' }
$m4a = Get-ChildItem "$work\out\tone_wav_to_m4a*" | Select-Object -First 1
if ($m4a) { $null = Conv $m4a.FullName 'mp3' }

# ---------- video ----------
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'; $wm = Join-Path $env:WINDIR 'System32\WinMetadata'
& "$fw\csc.exe" /nologo "/out:$work\make-video.exe" "/r:$fw\System.Runtime.dll" "/r:$fw\System.Runtime.WindowsRuntime.dll" `
    "/r:$wm\Windows.Foundation.winmd" "/r:$wm\Windows.Storage.winmd" "/r:$wm\Windows.Media.winmd" "/r:$wm\Windows.UI.winmd" "$PSScriptRoot\make-video.cs" | Out-Null
& "$work\make-video.exe" "$work\tone.wav" "$work\clip.mp4" | Out-Null
if (Test-Path "$work\clip.mp4") {
    Check 'detect mp4' ((Kind "$work\clip.mp4").Id -eq 'mp4')
    $wmv = Conv "$work\clip.mp4" 'wmv'
    $null = Conv "$work\clip.mp4" 'mp3'
    $null = Conv "$work\clip.mp4" 'm4a'
    $null = Conv "$work\clip.mp4" 'avi'
    if ($wmv) { $null = Conv $wmv 'mp4' }
} else { Check 'generate sample video' $false }

# ---------- documents ----------
$docx = Conv "$work\report.md" 'docx'
foreach ($t in 'html', 'pdf', 'txt', 'rtf') { $null = Conv "$work\report.md" $t }
if ($docx) {
    $back = Conv $docx 'md'
    if ($back) {
        $t = Get-Content $back -Raw
        Check 'docx round-trip keeps heading, list and table' ($t -match '# Quarterly Report' -and $t -match '- Revenue up 12%' -and $t -match '\| North \| 1200 \| 5% \|') $t
    }
    $null = Conv $docx 'pdf'; $null = Conv $docx 'html'
}
$h2m = Conv "$work\page.html" 'md'
if ($h2m) { $t = Get-Content $h2m -Raw; Check 'html -> md content' ($t -match '# Hello' -and $t -match 'First & second' -and $t -notmatch 'color:red') $t }
$null = Conv "$work\page.html" 'docx'
$r2t = Conv "$work\memo.rtf" 'txt'
if ($r2t) { $t = Get-Content $r2t -Raw -Encoding UTF8; Check 'rtf -> txt text' ($t -match 'Hello world' -and $t -match 'caf' + [char]0xE9) $t }
$null = Conv "$work\notes.txt" 'docx'

# ---------- data ----------
$xlsx = Conv "$work\people.csv" 'xlsx'
foreach ($t in 'json', 'xml', 'tsv', 'html', 'md', 'pdf') { $null = Conv "$work\people.csv" $t }
if ($xlsx) {
    $csv2 = Conv $xlsx 'csv'
    if ($csv2) {
        $a = (Get-Content "$work\people.csv" -Raw).Trim(); $bb = (Get-Content $csv2 -Raw -Encoding UTF8).Trim().TrimStart([char]0xFEFF)
        Check 'csv -> xlsx -> csv round-trip identical (quotes, commas)' ($a -eq $bb) "`n$a`n---`n$bb"
    }
    $null = Conv $xlsx 'json'
}
$j2c = Conv "$work\data.json" 'csv'
if ($j2c) { $t = Get-Content $j2c -Raw; Check 'json -> csv flattens nested fields' ($t -match 'price\.usd' -and $t -match 'a; b') $t }
foreach ($t in 'xml', 'yaml', 'xlsx') { $null = Conv "$work\data.json" $t }
$x2j = Conv "$work\feed.xml" 'json'
if ($x2j) { $t = Get-Content $x2j -Raw; Check 'xml -> json structure' ($t -match '"@id": "1"' -and $t -match '"title": "Dune"') $t }
$x2c = Conv "$work\feed.xml" 'csv'
if ($x2c) { $t = Get-Content $x2c -Raw; Check 'xml -> csv rows' ($t -match 'Dune' -and $t -match 'Emma') $t }

# ---------- subtitles ----------
$vtt = Conv "$work\subs.srt" 'vtt'
if ($vtt) {
    Check 'srt -> vtt header + timing' ((Get-Content $vtt -Raw) -match 'WEBVTT' -and (Get-Content $vtt -Raw) -match '00:00:01.000 --> 00:00:02.500')
    $srt2 = Conv $vtt 'srt'
    if ($srt2) { Check 'vtt -> srt timing' ((Get-Content $srt2 -Raw) -match '00:00:01,000 --> 00:00:02,500') }
}

# ---------- archives ----------
$zip = Conv "$work\report.md" 'zip'
if ($zip) {
    $tgz = Conv $zip 'tgz'; $tar = Conv $zip 'tar'
    if ($tgz) { $z2 = Conv $tgz 'zip' }
    if ($tar) { $null = Conv $tar 'zip' }
    if ($z2) {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $za = [IO.Compression.ZipFile]::OpenRead($z2); $e = $za.Entries | Select-Object -First 1
        $r = New-Object IO.StreamReader($e.Open()); $content = $r.ReadToEnd(); $r.Close(); $za.Dispose()
        Check 'zip -> tar.gz -> zip keeps file content' ($e.Name -eq 'report.md' -and $content -eq (Get-Content "$work\report.md" -Raw)) $e.Name
    }
}
$gz = Conv "$work\notes.txt" 'gz'
if ($gz) {
    $k = Kind $gz
    $res = $Conv::Convert($gz, $k, 'ungz', (Join-Path "$work\out" 'x'), $opts, $reserve)
    $un = Join-Path "$work\out" ([IO.Path]::GetFileName($gz) -replace '\.gz$', '')
    Check 'gz -> extract restores identical file' ((Test-Path $un) -and ((Get-FileHash $un).Hash -eq (Get-FileHash "$work\notes.txt").Hash)) $res
}

Write-Host "`n$script:passes passed, $script:fails failed. Outputs: $work\out"
if ($script:fails -gt 0) { exit 1 }
