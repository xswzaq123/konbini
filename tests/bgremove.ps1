# Background Remover tests. Usage: powershell -MTA -NoProfile -ExecutionPolicy Bypass -File tests\bgremove.ps1 [-cpu]
#   -cpu forces Windows ML onto the CPU (no-GPU path). -models lite,high,max picks extra models to test (default: lite;
#   high/max need 12-16 GB RAM and are slow). Needs dist\Konbini.exe built first.
param([switch]$cpu, [string[]]$models = @('lite'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing @'
using System; using System.Drawing; using System.Drawing.Imaging;
public static class B {
  public static byte[] Px(Bitmap b) {
    var d = b.LockBits(new Rectangle(0,0,b.Width,b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    var px = new byte[b.Width*b.Height*4];
    for (int y = 0; y < b.Height; y++) System.Runtime.InteropServices.Marshal.Copy(d.Scan0 + y*d.Stride, px, y*b.Width*4, b.Width*4);
    b.UnlockBits(d); return px;
  }
  // Truth mask (from a white-on-black drawing) vs predicted alpha: IoU, background clear %, core solid %, soft-edge count.
  // Pixels inside `skip` (the hair region) are excluded; "background"/"core" exclude a band of `band` px around the edge.
  public static string Score(Bitmap truth, float[] alpha, Rectangle skip, int band) {
    int w = truth.Width, h = truth.Height; var t = Px(truth);
    Func<int,int,bool> fg = (x,y) => t[(y*w+x)*4+1] > 127;
    long inter=0, uni=0, bgN=0, bgOk=0, coreN=0, coreOk=0, soft=0;
    for (int y = band; y < h-band; y++) for (int x = band; x < w-band; x++) {
      if (skip.Contains(x,y)) continue;
      bool f = fg(x,y); float a = alpha[y*w+x]; bool p = a > 0.5f;
      if (f && p) inter++; if (f || p) uni++;
      bool near = false;
      for (int k = -band; k <= band && !near; k += band) for (int j = -band; j <= band; j += band) if (fg(x+j,y+k) != f) { near = true; break; }
      if (near) { if (a > 0.05f && a < 0.95f) soft++; continue; }
      if (f) { coreN++; if (a > 0.9f) coreOk++; } else { bgN++; if (a < 0.1f) bgOk++; }
    }
    return string.Format("{0:F4};{1:F4};{2:F4};{3}", (double)inter/uni, (double)bgOk/bgN, (double)coreOk/coreN, soft);
  }
  public static double MeanDiff(float[] a, float[] b) { double s = 0; for (int i = 0; i < a.Length; i++) s += Math.Abs(a[i]-b[i]); return s / a.Length; }
  // Mean green in semi-transparent pixels after compositing on black: ours vs naive (original colour * alpha).
  public static string Fringe(Bitmap src, Bitmap onBlack, float[] alpha) {
    var s = Px(src); var o = Px(onBlack); double ours = 0, naive = 0; long n = 0;
    for (int i = 0; i < alpha.Length; i++) { float a = alpha[i]; if (a < 0.2f || a > 0.8f) continue; ours += o[i*4+1]; naive += s[i*4+1]*a; n++; }
    return string.Format("{0:F1};{1:F1};{2}", ours/n, naive/n, n);
  }
}
'@
$root = Split-Path $PSScriptRoot -Parent
$a = [Reflection.Assembly]::LoadFile("$root\dist\Konbini.exe")
if ($cpu) { $a.GetType('Konbini.Upscale.WinMl').GetField('ForceCpu').SetValue($null, $true) }
# The full-precision model is a downloadable dependency: install it from models\ if present, else download it.
$DMt = $a.GetType('Konbini.Deps.DepManager'); $bgFull = $a.GetType('Konbini.Deps.Registry').GetField('BgFull').GetValue($null)
if (Test-Path "$root\models\isnet-general-use.onnx") { $DMt.GetMethod('InstallFromFile').Invoke($null, @($bgFull, [string]"$root\models\isnet-general-use.onnx")) }
else { $DMt.GetMethod('EnsureInstalled').Invoke($null, [object[]]@($bgFull, $null, $null)) }
# The Standard fp16 model is a download from the models-v1 release; use a local copy if there is one.
$bgFast = $a.GetType('Konbini.Deps.Registry').GetField('BgFast').GetValue($null)
$fp16 = @("$root\models\isnet-general-use-fp16.onnx", "$root\hosting\isnet-general-use-fp16.onnx") | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($fp16) { $DMt.GetMethod('InstallFromFile').Invoke($null, @($bgFast, [string]$fp16)) } else { $DMt.GetMethod('EnsureInstalled').Invoke($null, [object[]]@($bgFast, $null, $null)) }
$br = $a.GetType('Konbini.BgRemove.BackgroundRemover'); $PT = $a.GetType('Konbini.BgRemove.BgPrecision'); $MT = $a.GetType('Konbini.BgRemove.BgMode')
$fail = 0; function Check($name, $cond, $info) { if ($cond) { "PASS $name  $info" } else { "FAIL $name :: $info"; $script:fail++ } }
function Alpha($bmp, [string]$prec, [bool]$refine = $true, $cancel = $null) { ($br.GetMethods() | Where-Object { $_.Name -eq 'Alpha' -and $_.GetParameters().Count -eq 5 } | Select-Object -First 1).Invoke($null, @([Drawing.Bitmap]$bmp, [Enum]::Parse($PT, $prec), $refine, $cancel, $null)) }
function Compose($bmp, $al, [string]$mode, $color = [Drawing.Color]::White, $bgImg = $null) { $br.GetMethod('Compose').Invoke($null, @([Drawing.Bitmap]$bmp, [float[]]$al, [Enum]::Parse($MT, $mode), [Drawing.Color]$color, 40, [Drawing.Bitmap]$bgImg)) }

# Scene 1: a person-like subject (head, shoulders, hair strands) on a busy striped background, with its exact truth mask.
function Scene($w, $h) {
    $img = New-Object Drawing.Bitmap $w, $h; $mask = New-Object Drawing.Bitmap $w, $h
    $g = [Drawing.Graphics]::FromImage($img); $m = [Drawing.Graphics]::FromImage($mask)
    $g.SmoothingMode = 'AntiAlias'; $m.SmoothingMode = 'AntiAlias'; $m.Clear([Drawing.Color]::Black)
    $sx = $w / 800.0; $sy = $h / 600.0
    for ($i = 0; $i -lt 40; $i++) { $g.FillRectangle((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(60 + ($i * 37) % 150, 120 + ($i * 53) % 120, 90 + ($i * 29) % 160))), [float]($i * 20 * $sx), 0, [float](20 * $sx + 1), $h) }
    $skin = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(230, 140, 60)); $shirt = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(40, 60, 160))
    foreach ($gg in @(@($g, $skin, $shirt), @($m, [Drawing.Brushes]::White, [Drawing.Brushes]::White))) {
        $gg[0].FillEllipse($gg[1], [float](300 * $sx), [float](120 * $sy), [float](200 * $sx), [float](220 * $sy))
        $gg[0].FillRectangle($gg[2], [float](240 * $sx), [float](320 * $sy), [float](320 * $sx), [float](280 * $sy))
    }
    $pen = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(70, 40, 20)), ([float](1.5 * $sx)); $rnd = New-Object Random 3
    for ($i = 0; $i -lt 120; $i++) { $x = 310 + $rnd.Next(180); $g.DrawBezier($pen, [float]($x * $sx), [float](140 * $sy), [float](($x - 30 + $rnd.Next(60)) * $sx), [float](90 * $sy), [float](($x - 40 + $rnd.Next(80)) * $sx), [float](70 * $sy), [float](($x - 50 + $rnd.Next(100)) * $sx), [float]((50 + $rnd.Next(40)) * $sy)) }
    $g.Dispose(); $m.Dispose()
    return @($img, $mask, (New-Object Drawing.Rectangle ([int](240 * $sx)), 0, ([int](330 * $sx)), ([int](180 * $sy))))
}
$s = Scene 800 600; $img = $s[0]; $truth = $s[1]; $hair = $s[2]

# 1-4. Accuracy (both precisions), soft edges, precision agreement.
$alF = Alpha $img 'Full'; "device: " + $br.GetProperty('LastDevice').GetValue($null)
$alH = Alpha $img 'Fast'; "device: " + $br.GetProperty('LastDevice').GetValue($null)
foreach ($pair in @(@('Full', $alF), @('Fast', $alH))) {
    $r = [B]::Score($truth, [float[]]$pair[1], $hair, 6).Split(';')
    Check "$($pair[0]): IoU > 0.95" ([double]$r[0] -gt 0.95) ("IoU={0}" -f $r[0])
    Check "$($pair[0]): background >= 98% clear" ([double]$r[1] -ge 0.98) ("clear={0}" -f $r[1])
    Check "$($pair[0]): subject core >= 98% solid" ([double]$r[2] -ge 0.98) ("solid={0}" -f $r[2])
    Check "$($pair[0]): soft anti-aliased edges" ([int]$r[3] -gt 200) ("soft edge pixels={0}" -f $r[3])
}
$d = [B]::MeanDiff([float[]]$alF, [float[]]$alH)
Check "Fast and Full agree" ($d -lt 0.02) ("mean alpha difference={0:N4}" -f $d)

# Other models. BiRefNet (high/max) inference only runs when asked for explicitly with -models, because it is too
# heavy for some test machines (the user asked not to run it on theirs).
$BM = $a.GetType('Konbini.BgRemove.BgModels')
$minIoU = @{ lite = 0.85; high = 0.95; max = 0.95 }
foreach ($id in $models) {
    $m = $BM.GetMethod('ById').Invoke($null, @([string]$id))
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $al = $br.GetMethods() | Where-Object { $_.Name -eq 'Alpha' -and $_.GetParameters().Count -eq 6 } | Select-Object -First 1
    $alM = $al.Invoke($null, [object[]]@([Drawing.Bitmap]$img, $m, [Enum]::Parse($PT, 'Automatic'), $true, $null, $null))
    $r = [B]::Score($truth, [float[]]$alM, $hair, 6).Split(';')
    Check "${id}: IoU > $($minIoU[$id])" ([double]$r[0] -gt $minIoU[$id]) ("IoU={0} clear={1} solid={2} on {3} in {4:N1}s" -f $r[0], $r[1], $r[2], $br.GetProperty('LastDevice').GetValue($null), $sw.Elapsed.TotalSeconds)
}
$alN = Alpha $img 'Full' $false
$rN = [B]::Score($truth, [float[]]$alN, $hair, 6).Split(';'); $rR = [B]::Score($truth, [float[]]$alF, $hair, 6).Split(';')
Check "refinement doesn't hurt accuracy" ([double]$rR[0] -ge [double]$rN[0] - 0.005) ("IoU refined={0} unrefined={1}" -f $rR[0], $rN[0])

# 5. Colour fringes: red subject on a green background, composited on black; edges must carry far less green than naive.
$f = New-Object Drawing.Bitmap 600, 400; $g = [Drawing.Graphics]::FromImage($f); $g.SmoothingMode = 'AntiAlias'
$g.Clear([Drawing.Color]::FromArgb(40, 200, 40)); $g.FillEllipse((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(220, 40, 40))), 150, 60, 300, 280); $g.Dispose()
$alR = Alpha $f 'Full'; $blk = Compose $f $alR 'Color' ([Drawing.Color]::Black)
$fr = [B]::Fringe($f, $blk, [float[]]$alR).Split(';')
Check "no green fringe" ([double]$fr[0] -lt [double]$fr[1] * 0.6 -or [int]$fr[2] -lt 50) ("edge green ours={0} naive={1} (n={2})" -f $fr[0], $fr[1], $fr[2]); $blk.Dispose()

# 6. Every output mode: size and pixel rules.
$bgPic = New-Object Drawing.Bitmap 300, 900; $g = [Drawing.Graphics]::FromImage($bgPic); $g.Clear([Drawing.Color]::Gold); $g.Dispose()
foreach ($mode in 'Transparent', 'Color', 'Blur', 'Image', 'Mask') {
    $o = Compose $img $alF $mode ([Drawing.Color]::White) $bgPic
    $c1 = $o.GetPixel(20, 20); $c2 = $o.GetPixel(400, 450)
    $ok = $o.Width -eq 800 -and $o.Height -eq 600
    switch ($mode) {
        'Transparent' { $ok = $ok -and $c1.A -lt 20 -and $c2.A -gt 235 }
        'Color' { $ok = $ok -and $c1.A -eq 255 -and $c1.R -gt 240 -and $c1.G -gt 240 }
        'Blur' { $ok = $ok -and $c1.A -eq 255 }
        'Image' { $ok = $ok -and $c1.R -gt 230 -and $c1.G -gt 190 -and $c1.B -lt 40 }
        'Mask' { $ok = $ok -and $c1.R -lt 20 -and $c2.R -gt 235 -and $c2.R -eq $c2.G }
    }
    Check "output mode $mode" $ok ("{0}x{1} corner={2} subject={3}" -f $o.Width, $o.Height, $c1, $c2); $o.Dispose()
}
# 7. Cancellation.
try { Alpha $img 'Full' $true ([Func[bool]] { $true }) | Out-Null; Check "cancel" $false "ran to the end" }
catch { $e = $_.Exception; while ($e.InnerException) { $e = $e.InnerException }; Check "cancel" ($e -is [OperationCanceledException]) $e.GetType().Name }
# 8. Large photo: 4000x3000 stays within a sensible memory budget.
$big = Scene 4000 3000
[GC]::Collect(); $p0 = [Diagnostics.Process]::GetCurrentProcess(); $p0.Refresh(); $before = $p0.PrivateMemorySize64
$sw = [Diagnostics.Stopwatch]::StartNew(); $alB = Alpha $big[0] 'Automatic'; $o = Compose $big[0] $alB 'Transparent'; $sw.Stop()
$p0.Refresh(); $grow = ($p0.PeakPagedMemorySize64 - $before) / 1MB
$rB = [B]::Score($big[1], [float[]]$alB, $big[2], 20).Split(';')
Check "4000x3000" ($o.Width -eq 4000 -and [double]$rB[0] -gt 0.95 -and $grow -lt 1500) ("{0:N1}s, peak +{1:N0} MB, IoU={2}" -f $sw.Elapsed.TotalSeconds, $grow, $rB[0])
$o.Save("$env:TEMP\konbini-bg-large.png"); $o.Dispose()

if ($fail) { "FAILED $fail" } else { "ALL PASSED" }
