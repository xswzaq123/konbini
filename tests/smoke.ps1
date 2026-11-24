# End-to-end smoke test: drives the real UI in-process and checks outputs.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File tests\smoke.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$root = Split-Path $PSScriptRoot
$work = Join-Path $env:TEMP ("konbini-test-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $work | Out-Null
$script:fails = 0
function Check($name, $cond, $detail = '') {
    if ($cond) { Write-Host "PASS $name" } else { Write-Host "FAIL $name $detail" -ForegroundColor Red; $script:fails++ }
}
function Pump($ms) { $sw = [Diagnostics.Stopwatch]::StartNew(); while ($sw.ElapsedMilliseconds -lt $ms) { [Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 15 } }
function WaitFor($sb, $timeoutMs = 60000) { $sw = [Diagnostics.Stopwatch]::StartNew(); while (-not (& $sb)) { if ($sw.ElapsedMilliseconds -gt $timeoutMs) { return $false }; Pump 50 }; return $true }
function Find-Ctl($c, $pred) { foreach ($x in $c.Controls) { if (& $pred $x) { return $x }; $r = Find-Ctl $x $pred; if ($r) { return $r } } }

# --- Test images: photo-like gradient + noise, several formats, one corrupt file
function New-TestImage($path, $w, $h, $fmt) {
    $b = New-Object Drawing.Bitmap $w, $h
    $g = [Drawing.Graphics]::FromImage($b)
    $br = New-Object Drawing.Drawing2D.LinearGradientBrush((New-Object Drawing.Rectangle 0, 0, $w, $h), [Drawing.Color]::SteelBlue, [Drawing.Color]::Orange, 45)
    $g.FillRectangle($br, 0, 0, $w, $h)
    $rnd = New-Object Random 7
    for ($i = 0; $i -lt 400; $i++) { $g.FillEllipse((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(120, $rnd.Next(256), $rnd.Next(256), $rnd.Next(256)))), $rnd.Next($w), $rnd.Next($h), $rnd.Next(10, 200), $rnd.Next(10, 200)) }
    $g.Dispose(); $b.Save($path, $fmt); $b.Dispose()
}
New-TestImage "$work\photo.jpg" 4000 3000 ([Drawing.Imaging.ImageFormat]::Jpeg)
New-TestImage "$work\wide.png" 1600 400 ([Drawing.Imaging.ImageFormat]::Png)
New-TestImage "$work\small.bmp" 300 200 ([Drawing.Imaging.ImageFormat]::Bmp)
New-TestImage "$work\photo.png" 800 600 ([Drawing.Imaging.ImageFormat]::Png)   # same base name as photo.jpg
[IO.File]::WriteAllBytes("$work\broken.jpg", [byte[]](1..200))
$inputs = @("$work\photo.jpg", "$work\wide.png", "$work\small.bmp", "$work\photo.png", "$work\broken.jpg")

$asm = [Reflection.Assembly]::LoadFile("$root\dist\Konbini.exe")
$nonPub = [Reflection.BindingFlags]'NonPublic,Instance'

# --- Pure logic
$rt = $asm.GetType('Konbini.ImageResizerTool')
$sz = New-Object Drawing.Size 4000, 3000
Check 'fit within 1920x1080' (($rt::ComputeSize($sz, 0, 1920, 1080, 1, $true, $true)) -eq (New-Object Drawing.Size 1440, 1080))
Check 'exact no aspect' (($rt::ComputeSize($sz, 1, 500, 500, 1, $false, $true)) -eq (New-Object Drawing.Size 500, 500))
Check 'exact keep aspect' (($rt::ComputeSize($sz, 1, 500, 500, 1, $true, $true)) -eq (New-Object Drawing.Size 500, 375))
Check 'percent 25' (($rt::ComputeSize($sz, 2, 0, 0, 0.25, $true, $true)) -eq (New-Object Drawing.Size 1000, 750))
Check 'no upscale' (($rt::ComputeSize((New-Object Drawing.Size 300, 200), 0, 1920, 1080, 1, $true, $true)) -eq (New-Object Drawing.Size 300, 200))
Check 'upscale allowed' (($rt::ComputeSize((New-Object Drawing.Size 300, 200), 2, 0, 0, 2, $true, $false)) -eq (New-Object Drawing.Size 600, 400))

$form = $asm.CreateInstance('Konbini.MainForm')
$form.Show(); Pump 300
$nav = Find-Ctl $form { param($x) $x -is [Windows.Forms.ListBox] }

function Get-Panel { Find-Ctl $form { param($x) $x.GetType().Name -eq 'BatchImagePanel' -and $x.Visible } }
function Open-Tool($name) { for ($i = 0; $i -lt $nav.Items.Count; $i++) { if ("$($nav.Items[$i].Name)" -eq $name) { $nav.SelectedIndex = $i } }; Pump 200; Get-Panel }
function Run-Panel($p) {
    $btn = Find-Ctl $p { param($x) $x -is [Windows.Forms.Button] -and $x.Text -like '*images' -and $x.BackColor.B -gt 200 -and $x.ForeColor -eq [Drawing.Color]::White }
    $btn.PerformClick()
    WaitFor { $btn.Enabled -and ($p.List.Items | Where-Object { $_.SubItems[3].Text -in '', 'Waiting…' }).Count -eq 0 } | Out-Null
}
function Results($p) { $h = @{}; foreach ($it in $p.List.Items) { $h[$it.Text + '|' + $it.Tag] = $it.SubItems[3].Text }; $h }

# --- Thumbnail regression: add while in List view, then switch to Thumbnails
$p = Open-Tool 'Image Compressor'
$add = $p.GetType().GetMethod('AddPaths', $nonPub)
$add.Invoke($p, @(, [string[]]$inputs))
Check 'items added (incl. broken)' ($p.List.Items.Count -eq 5) "count=$($p.List.Items.Count)"
Check 'duplicates ignored' $(& { $add.Invoke($p, @(, [string[]]$inputs)); $p.List.Items.Count -eq 5 })
$ok = WaitFor { ($p.List.Items | Where-Object { $_.SubItems[1].Text -eq '…' }).Count -eq 0 } 20000
Check 'dimensions filled in background' $ok
Check 'dimensions correct' (($p.List.Items | Where-Object { $_.Text -eq 'photo.jpg' }).SubItems[1].Text -eq '4000 × 3000')
Check 'broken file flagged' (($p.List.Items | Where-Object { $_.Text -eq 'broken.jpg' }).SubItems[1].Text -eq 'unreadable')
$thumbBtn = Find-Ctl $p { param($x) $x.Text -like '*Thumbnails' }
$thumbBtn.PerformClick(); Pump 300
Check 'switched to thumbnails' ($p.List.View -eq [Windows.Forms.View]::LargeIcon)
# Ask the native control which image each item shows (LVM_GETITEMW, LVIF_IMAGE)
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class LV {
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] public struct LVITEM { public uint mask; public int iItem, iSubItem; public uint state, stateMask; public IntPtr pszText; public int cchTextMax, iImage; public IntPtr lParam; public int iIndent, iGroupId; public uint cColumns; public IntPtr puColumns, piColFmt; public int iGroup; }
  [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, ref LVITEM l);
  public static int Image(IntPtr h, int i) { var it = new LVITEM { mask = 2, iItem = i }; SendMessage(h, 0x104B, IntPtr.Zero, ref it); return it.iImage; }
}
'@
$good = 0
for ($i = 0; $i -lt $p.List.Items.Count; $i++) { $it = $p.List.Items[$i]; if ($it.Text -ne 'broken.jpg' -and [LV]::Image($p.List.Handle, $i) -ge 0) { $good++ } }
Check 'every readable item shows its thumbnail on first switch' ($good -eq 4) "good=$good"
$bmp = New-Object Drawing.Bitmap $form.Width, $form.Height; $form.DrawToBitmap($bmp, (New-Object Drawing.Rectangle 0, 0, $form.Width, $form.Height)); $bmp.Save("$work\thumbs.png")

# --- Compressor: quality mode
Run-Panel $p
$r = Results $p
Check 'compress jpg' (($r.GetEnumerator() | Where-Object { $_.Key -like 'photo.jpg|*' }).Value -match '→') ($r | Out-String)
Check 'compress broken reports error' (($r.GetEnumerator() | Where-Object { $_.Key -like 'broken.jpg|*' }).Value -like 'Error*')
Check 'jpg output exists' (Test-Path "$work\photo_compressed-q75.jpg")
Check 'png output exists' (Test-Path "$work\photo_compressed-q75.png")

# --- Compressor: target size 150 KB
$bySize = Find-Ctl $p { param($x) $x -is [Windows.Forms.RadioButton] -and $x.Text -like 'Target*' }; $bySize.Checked = $true
$num = Find-Ctl $p { param($x) $x -is ($asm.GetType("Konbini.NumBox")) -and $x.Enabled -and $x.Value -eq 200 }; $num.Value = 150
$sfx = Find-Ctl $p { param($x) $x -is [Windows.Forms.TextBox] -and $x.Text -like '_compressed*' }; $sfx.Text = '_150k'
Run-Panel $p
foreach ($f in 'photo_150k.jpg', 'wide_150k.png', 'photo_150k.png') {
    if (Test-Path "$work\$f") { $len = (Get-Item "$work\$f").Length; Check "target size $f <= 150 KB" ($len -le 150KB) "$len bytes" }
    else { Check "target size $f written" $false }
}

# --- Separate output folder + same-basename collision
$same = Find-Ctl $p { param($x) $x -is [Windows.Forms.CheckBox] -and $x.Text -eq 'Save next to originals' }; $same.Checked = $false
$dir = Find-Ctl $p { param($x) $x -is [Windows.Forms.TextBox] -and $x.Text -like '*Konbini*' }; $dir.Text = "$work\out"
$toJpeg = Find-Ctl $p { param($x) $x -is [Windows.Forms.CheckBox] -and $x.Text -like 'Convert*' }; $toJpeg.Checked = $true
$sfx.Text = ''
Run-Panel $p
Check 'output folder created' (Test-Path "$work\out")
Check 'collision kept both (photo.jpg + photo.jpg (2))' ((Test-Path "$work\out\photo.jpg") -and (Test-Path "$work\out\photo (2).jpg"))

# --- Resizer
$p = Open-Tool 'Image Resizer'
$p.GetType().GetMethod('AddPaths', $nonPub).Invoke($p, @(, [string[]]@("$work\photo.jpg", "$work\wide.png", "$work\small.bmp")))
Run-Panel $p
function Dim($f) { $i = [Drawing.Image]::FromFile($f); $s = "$($i.Width)x$($i.Height)"; $i.Dispose(); $s }
Check 'resize fit photo' ((Dim "$work\photo_resized-fit-1920x1080.jpg") -eq '1440x1080')
Check 'resize fit wide' ((Dim "$work\wide_resized-fit-1920x1080.png") -eq '1600x400')
Check 'resize small not enlarged' ((Dim "$work\small_resized-fit-1920x1080.bmp") -eq '300x200')
$mode = Find-Ctl $p { param($x) $x -is [Windows.Forms.ComboBox] -and $x.Items.Contains('Percentage') }; $mode.SelectedIndex = 2
$fmt = Find-Ctl $p { param($x) $x -is [Windows.Forms.ComboBox] -and $x.Items.Contains('Same as original') }; $fmt.SelectedItem = 'PNG'
$rs = Find-Ctl $p { param($x) $x -is [Windows.Forms.TextBox] -and $x.Text -like '_resized*' }; $rs.Text = '_half'
Run-Panel $p
Check 'resize 50% to png' ((Dim "$work\photo_half.png") -eq '2000x1500')

# --- Linked width/height
$mode.SelectedIndex = 1; Pump 50
$nums = @(); $sr = Find-Ctl $p { param($x) $x -is [Windows.Forms.FlowLayoutPanel] -and ($x.Controls | Where-Object { $_.Text -eq 'W' }) }
foreach ($c in $sr.Controls) { if ($c -is ($asm.GetType("Konbini.NumBox"))) { $nums += $c } }
$wN = $nums[0]; $hN = $nums[1]
$lnk = $sr.Controls | Where-Object { $_ -is [Windows.Forms.CheckBox] }
foreach ($it in $p.List.Items) { $it.Selected = $false }
$p.List.Items[0].Selected = $true            # photo.jpg is 4000x3000 (4:3)
$wN.Value = 1600; $lnk.Checked = $true; Pump 50
Check 'lock uses selected image ratio' ($hN.Value -eq 1200) "h=$($hN.Value)"
$wN.Value = 800; Check 'linked: width drives height' ($hN.Value -eq 600) "h=$($hN.Value)"
$hN.Value = 300; Check 'linked: height drives width' ($wN.Value -eq 400) "w=$($wN.Value)"
$lnk.Checked = $false; $wN.Value = 999; Check 'unlocked: independent' ($hN.Value -eq 300) "h=$($hN.Value)"
foreach ($it in $p.List.Items) { $it.Selected = $false }

# --- Remove / clear
$p.List.Items[0].Selected = $true
(Find-Ctl $p { param($x) $x.Text -eq 'Remove selected' }).PerformClick(); Pump 50
Check 'remove selected' ($p.List.Items.Count -eq 2)
(Find-Ctl $p { param($x) $x.Text -eq 'Clear' }).PerformClick(); Pump 50
Check 'clear' ($p.List.Items.Count -eq 0)
$p.GetType().GetMethod('AddPaths', $nonPub).Invoke($p, @(, [string[]]@("$work\photo.jpg")))
Check 'can re-add after clear' ($p.List.Items.Count -eq 1)

# --- W x H auto-fill when every image has the same size
WaitFor { $wN.Value -eq 4000 } 10000 | Out-Null
Check 'single image fills W x H' ($wN.Value -eq 4000 -and $hN.Value -eq 3000) "$($wN.Value)x$($hN.Value)"
Copy-Item "$work\photo.jpg" "$work\photo_copy.jpg"
$wN.Value = 1234
$p.GetType().GetMethod('AddPaths', $nonPub).Invoke($p, @(, [string[]]@("$work\photo_copy.jpg", "$work\broken.jpg")))
Pump 1500
Check 'same-size images keep W x H (user edit not overwritten, broken ignored)' ($wN.Value -eq 1234) "w=$($wN.Value)"
(Find-Ctl $p { param($x) $x.Text -eq 'Clear' }).PerformClick(); Pump 50
$p.GetType().GetMethod('AddPaths', $nonPub).Invoke($p, @(, [string[]]@("$work\photo.jpg", "$work\photo_copy.jpg")))
WaitFor { $wN.Value -eq 4000 } 10000 | Out-Null
Check 'two same-size images fill W x H' ($wN.Value -eq 4000 -and $hN.Value -eq 3000) "$($wN.Value)x$($hN.Value)"
$wN.Value = 50; $hN.Value = 50
$p.GetType().GetMethod('AddPaths', $nonPub).Invoke($p, @(, [string[]]@("$work\wide.png")))
Pump 1500
Check 'mixed sizes leave W x H alone' ($wN.Value -eq 50 -and $hN.Value -eq 50) "$($wN.Value)x$($hN.Value)"

# --- Settings (back up the user's real settings file and restore it afterwards)
$sType = $asm.GetType('Konbini.Settings'); $tType = $asm.GetType('Konbini.Theme')
$sFile = $sType.GetField('FilePath').GetValue($null)
$backup = if (Test-Path $sFile) { [IO.File]::ReadAllBytes($sFile) } else { $null }
try {
    (Find-Ctl $form { param($x) $x -is [Windows.Forms.Button] -and $x.Text -like '*Settings' }).PerformClick(); Pump 200
    $radio = { param($n) Find-Ctl $form { param($x) $x -is [Windows.Forms.RadioButton] -and $x.Name -eq $n } }
    (& $radio 'Dark').Checked = $true; Pump 200
    Check 'dark theme active' ($tType.GetField('IsDark').GetValue($null))
    Check 'settings saved' ((Get-Content $sFile) -contains 'theme=Dark')
    $content = Find-Ctl $form { param($x) $x -is [Windows.Forms.Panel] -and $x.Dock -eq 'Fill' -and $x.Padding.Left -eq 24 }
    Check 'content recoloured dark' ($content.BackColor.R -lt 60)
    (& $radio 'Thumbnails').Checked = $true; Pump 200
    Check 'default view saved' ((Get-Content $sFile) -contains 'defaultView=Thumbnails')
    Check 'open batch switched to thumbnails' ($p.List.View -eq [Windows.Forms.View]::LargeIcon)
    Check 'hidden batch recoloured' ($p.List.BackColor.R -lt 60)
    $p = Open-Tool 'Image Resizer'
    $b = New-Object Drawing.Bitmap $form.Width, $form.Height; $form.DrawToBitmap($b, (New-Object Drawing.Rectangle 0, 0, $form.Width, $form.Height)); $b.Save("$work\dark.png")
    # Reload from disk
    $sType.GetField('ThemeMode').SetValue($null, 'Light'); $sType.GetField('DefaultView').SetValue($null, 'List')
    $sType.GetMethod('Load').Invoke($null, @())
    Check 'settings reload from file' ($sType.GetField('ThemeMode').GetValue($null) -eq 'Dark' -and $sType.GetField('DefaultView').GetValue($null) -eq 'Thumbnails')
    Set-Content $sFile "theme=Purple`r`ngarbage"; $sType.GetField('ThemeMode').SetValue($null, 'Light')
    $sType.GetMethod('Load').Invoke($null, @())
    Check 'bad settings ignored' ($sType.GetField('ThemeMode').GetValue($null) -eq 'Light')
    (Find-Ctl $form { param($x) $x -is [Windows.Forms.Button] -and $x.Text -like '*Settings' }).PerformClick(); Pump 100
    (& $radio 'Light').Checked = $true; Pump 200
    Check 'back to light' (-not $tType.GetField('IsDark').GetValue($null))
} finally {
    if ($backup) { [IO.File]::WriteAllBytes($sFile, $backup) } elseif (Test-Path $sFile) { Remove-Item $sFile }
}

$form.Close()
Write-Host "Dark screenshot: $work\dark.png"
Write-Host "`nScreenshot: $work\thumbs.png"
if ($script:fails -gt 0) { Write-Host "$script:fails test(s) FAILED" -ForegroundColor Red; exit 1 } else { Write-Host 'ALL PASSED' -ForegroundColor Green }
