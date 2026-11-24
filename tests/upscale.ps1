# Image Upscaler tests. Usage: powershell -MTA -NoProfile -ExecutionPolicy Bypass -File tests\upscale.ps1 [-cpu]
#   -cpu forces Windows ML onto the CPU (tests the no-GPU path). Needs dist\Konbini.exe built first.
param([switch]$cpu)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing @'
using System; using System.Drawing; using System.Drawing.Imaging;
public static class M {
  static float[] Luma(Bitmap b) {
    var l = new float[b.Width * b.Height];
    var d = b.LockBits(new Rectangle(0,0,b.Width,b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    var px = new byte[d.Stride * b.Height]; System.Runtime.InteropServices.Marshal.Copy(d.Scan0, px, 0, px.Length); b.UnlockBits(d);
    for (int y = 0; y < b.Height; y++) for (int x = 0; x < b.Width; x++) { int i = y * d.Stride + x * 4; l[y * b.Width + x] = 0.114f*px[i] + 0.587f*px[i+1] + 0.299f*px[i+2]; }
    return l;
  }
  // Mean absolute Laplacian: higher = more fine detail / sharper edges.
  public static double Sharpness(Bitmap b) {
    var l = Luma(b); int w = b.Width, h = b.Height; double s = 0;
    for (int y = 1; y < h-1; y++) for (int x = 1; x < w-1; x++) { int i = y*w+x; s += Math.Abs(4*l[i]-l[i-1]-l[i+1]-l[i-w]-l[i+w]); }
    return s / ((w-2.0)*(h-2.0));
  }
  // PSNR against a reference of the same size.
  public static double Psnr(Bitmap a, Bitmap b) {
    var la = Luma(a); var lb = Luma(b); double e = 0;
    for (int i = 0; i < la.Length; i++) { double d = la[i]-lb[i]; e += d*d; }
    return 10 * Math.Log10(255.0*255.0 / (e / la.Length));
  }
  // Ratio of the mean horizontal step across tile boundaries (every `period` columns) to the mean step elsewhere.
  public static double SeamRatio(Bitmap b, int period) {
    var l = Luma(b); int w = b.Width, h = b.Height; double at = 0, other = 0; long na = 0, no = 0;
    for (int y = 0; y < h; y++) for (int x = 1; x < w; x++) { double d = Math.Abs(l[y*w+x]-l[y*w+x-1]); if (x % period == 0) { at += d; na++; } else { other += d; no++; } }
    return (at/na) / (other/no);
  }
  // Mean absolute luma difference between a (shifted by dx) and b over b's columns [x0,x1).
  public static double ShiftDiff(Bitmap a, Bitmap b, int dx, int x0, int x1) {
    var la = Luma(a); var lb = Luma(b); double e = 0; long n = 0;
    for (int y = 0; y < b.Height; y++) for (int x = x0; x < x1; x++) { e += Math.Abs(la[y*a.Width + x + dx] - lb[y*b.Width + x]); n++; }
    return e / n;
  }
  public static int AlphaAt(Bitmap b, int x, int y) { return b.GetPixel(x, y).A; }
}
'@
$root = Split-Path $PSScriptRoot -Parent
$a = [Reflection.Assembly]::LoadFile("$root\dist\Konbini.exe")
$ai = $a.GetType('Konbini.Upscale.AiUpscaler'); $lz = $a.GetType('Konbini.Upscale.Lanczos')
if ($cpu) { $ai.GetField('ForceCpu').SetValue($null, $true) }
$fail = 0; function Check($name, $cond, $info) { if ($cond) { "PASS $name  $info" } else { "FAIL $name :: $info"; $script:fail++ } }
function AI($bmp, $s) { $ai.GetMethod('Upscale').Invoke($null, @([Drawing.Bitmap]$bmp, [int]$s, $null, $null)) }
function Fast($bmp, $s) { $lz.GetMethod('Resize').Invoke($null, @([Drawing.Bitmap]$bmp, [int]($bmp.Width * $s), [int]($bmp.Height * $s), $true)) }

Check "AI available" ($ai.GetProperty('Available').GetValue($null)) ("device=" + $ai.GetProperty('DeviceName').GetValue($null) + " " + $ai.GetProperty('Error').GetValue($null))

# Test image: a detailed synthetic photo-like scene (gradients, text, thin lines, circles) at 600x400 ground truth.
$gt = New-Object Drawing.Bitmap 600, 400
$g = [Drawing.Graphics]::FromImage($gt); $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAlias'
$grad = New-Object Drawing.Drawing2D.LinearGradientBrush (New-Object Drawing.Point 0, 0), (New-Object Drawing.Point 600, 400), ([Drawing.Color]::SteelBlue), ([Drawing.Color]::Wheat)
$g.FillRectangle($grad, 0, 0, 600, 400)
$rnd = New-Object Random 7
for ($i = 0; $i -lt 60; $i++) { $g.DrawEllipse((New-Object Drawing.Pen ([Drawing.Color]::FromArgb($rnd.Next(256), $rnd.Next(256), $rnd.Next(256))), 2), $rnd.Next(600), $rnd.Next(400), 20 + $rnd.Next(80), 20 + $rnd.Next(80)) }
for ($i = 0; $i -lt 40; $i++) { $g.DrawLine([Drawing.Pens]::Black, $rnd.Next(600), $rnd.Next(400), $rnd.Next(600), $rnd.Next(400)) }
$g.DrawString("Konbini upscaler 123", (New-Object Drawing.Font 'Segoe UI', 28.0, ([Drawing.FontStyle]::Bold)), [Drawing.Brushes]::White, 40, 160)
$g.Dispose()
$small = New-Object Drawing.Bitmap 150, 100
$g = [Drawing.Graphics]::FromImage($small); $g.InterpolationMode = 'HighQualityBicubic'; $g.DrawImage($gt, 0, 0, 150, 100); $g.Dispose()

# 1. Exact output sizes (also covers images narrower than one tile and non-multiples of 256).
foreach ($s in 2, 3, 4) {
    $r = AI $small $s; Check "AI x$s size" ($r.Width -eq 150 * $s -and $r.Height -eq 100 * $s) "$($r.Width)x$($r.Height)"; $r.Dispose()
    $r = Fast $small $s; Check "Fast x$s size" ($r.Width -eq 150 * $s -and $r.Height -eq 100 * $s) "$($r.Width)x$($r.Height)"; $r.Dispose()
}
# 2. Quality: AI at 4x should be sharper than Fast and at least as close to the ground truth.
$rA = AI $small 4; $rF = Fast $small 4
$sA = [M]::Sharpness($rA); $sF = [M]::Sharpness($rF); $pA = [M]::Psnr($rA, $gt); $pF = [M]::Psnr($rF, $gt)
Check "AI sharper than Fast" ($sA -gt $sF * 1.1) ("sharpness AI={0:N2} Fast={1:N2}" -f $sA, $sF)
# Faithful, not invented: shrinking the AI result back down should reproduce the input closely.
$back = New-Object Drawing.Bitmap 150, 100; $g = [Drawing.Graphics]::FromImage($back); $g.InterpolationMode = 'HighQualityBicubic'; $g.DrawImage($rA, 0, 0, 150, 100); $g.Dispose()
$pB = [M]::Psnr($back, $small)
Check "AI consistent with input (no shift or colour error)" ($pB -gt 20) ("PSNR(downscaled AI, input)={0:N2}dB" -f $pB); $back.Dispose()
$rA.Save("$env:TEMP\konbini-upscale-ai.png"); $rF.Save("$env:TEMP\konbini-upscale-fast.png"); $rA.Dispose(); $rF.Dispose()
# 3. No seams: a pixel's result must not depend on where tile boundaries fall. Upscale the image and the same image
#    with 100 columns cropped off (moving every boundary), then compare the overlap away from the image edges.
$mid = New-Object Drawing.Bitmap $gt, 1000, 400
$crop = $mid.Clone((New-Object Drawing.Rectangle 100, 0, 900, 400), $mid.PixelFormat)
foreach ($s in 2, 4) {
    $r1 = AI $mid $s; $r2 = AI $crop $s
    $d = [M]::ShiftDiff($r1, $r2, 100 * $s, 60 * $s, 840 * $s)
    Check "no seams x$s" ($d -lt 1.0) ("mean difference when tiles move={0:N3} levels" -f $d); $r1.Dispose(); $r2.Dispose()
}
# 4. Transparency is kept.
$t = New-Object Drawing.Bitmap 120, 80, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [Drawing.Graphics]::FromImage($t); $g.Clear([Drawing.Color]::Transparent); $g.FillEllipse([Drawing.Brushes]::Red, 30, 10, 60, 60); $g.Dispose()
$r = AI $t 2
Check "alpha kept" ([M]::AlphaAt($r, 5, 5) -eq 0 -and [M]::AlphaAt($r, 120, 80) -eq 255) ("corner=" + [M]::AlphaAt($r, 5, 5) + " centre=" + [M]::AlphaAt($r, 120, 80)); $r.Dispose()
# 5. Big image: 3000x3000 at 2x stays within a reasonable memory budget.
$big = New-Object Drawing.Bitmap $gt, 3000, 3000
[GC]::Collect(); $before = [Diagnostics.Process]::GetCurrentProcess().PrivateMemorySize64
$sw = [Diagnostics.Stopwatch]::StartNew(); $peak = 0
$r = AI $big 2; $sw.Stop()
$after = [Diagnostics.Process]::GetCurrentProcess().PeakWorkingSet64
$growMB = ([Diagnostics.Process]::GetCurrentProcess().PrivateMemorySize64 - $before) / 1MB
Check "3000x3000 x2" ($r.Width -eq 6000 -and $growMB -lt 600) ("{0:N1}s, +{1:N0} MB (result bitmap is {2:N0} MB)" -f $sw.Elapsed.TotalSeconds, $growMB, (6000 * 6000 * 4 / 1MB)); $r.Dispose(); $big.Dispose()
# 6. Too-large requests are refused with a clear message.
try { $ai.GetMethod('CheckSize').Invoke($null, @(20000, 20000)); Check "size limit" $false "no error" }
catch { Check "size limit" ($_.Exception.InnerException.Message -match 'too large') $_.Exception.InnerException.Message }
# 7. Cancellation stops between tiles.
$cancel = [Func[bool]] { $true }
try { $ai.GetMethod('Upscale').Invoke($null, @([Drawing.Bitmap]$mid, 4, $null, $cancel)); Check "cancel" $false "ran to the end" }
catch { Check "cancel" ($_.Exception.InnerException -is [OperationCanceledException]) $_.Exception.InnerException.GetType().Name }

if ($fail) { "FAILED $fail" } else { "ALL PASSED" }
