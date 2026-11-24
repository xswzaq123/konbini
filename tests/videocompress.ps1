# Video Compressor tests. Usage: powershell -NoProfile -ExecutionPolicy Bypass -File tests\videocompress.ps1
# Needs dist\Konbini.exe built first. Generates its own test clips with the embedded ffmpeg.
$ErrorActionPreference = 'Stop'
$asm = [Reflection.Assembly]::LoadFile((Resolve-Path "$PSScriptRoot\..\dist\Konbini.exe"))
$FF = $asm.GetType('Konbini.Video.Ffmpeg'); $VC = $asm.GetType('Konbini.Video.VideoCompress')
$OptT = $asm.GetType('Konbini.Video.CompressOptions')
$fail = 0
function Check($name, $ok, $detail) { if ($ok) { Write-Host "PASS $name $detail" } else { Write-Host "FAIL $name $detail" -ForegroundColor Red; $script:fail++ } }
function Probe($p) { $FF.GetMethod('Probe').Invoke($null, @([string]$p)) }
function Ff([string[]]$a, [double]$dur) { $FF.GetMethod('Run').Invoke($null, @([Collections.Generic.IList[string]]$a, $dur, $null, $null)) }
function Compress($src, $dst, $opt, $cancel) {
    $w = $null
    $cb = if ($cancel) { [Func[bool]]$cancel } else { [Func[bool]]{ $false } }
    $args2 = [object[]]@([string]$src, [string]$dst, $opt, $null, $cb, $w)
    $r = $VC.GetMethod("Compress").Invoke($null, $args2)
    return @{ Outcome = "$r"; Warning = $args2[5] }
}
function Tpl($name) { ($VC.GetField('Templates').GetValue($null) | Where-Object { $_.Name -eq $name }).Options.Clone() }

$T = Join-Path $env:TEMP ("konbini-vc-" + [guid]::NewGuid().ToString('N').Substring(0,8)); New-Item -ItemType Directory $T | Out-Null
try {
    # ---- engine: installed through the dependency manager (from models\ffmpeg.exe if present, else downloaded) ----
    $DM = $asm.GetType('Konbini.Deps.DepManager'); $dep = $FF.GetProperty('Dep').GetValue($null)
    if (Test-Path "$PSScriptRoot\..\models\ffmpeg.exe") { $DM.GetMethod('InstallFromFile').Invoke($null, @($dep, [string](Resolve-Path "$PSScriptRoot\..\models\ffmpeg.exe"))) }
    else { $DM.GetMethod('EnsureInstalled').Invoke($null, [object[]]@($dep, $null, $null)) }
    $exe2 = $FF.GetProperty('ExePath').GetValue($null)
    Check 'engine installed' ($FF.GetProperty('Available').GetValue($null)) $exe2

    # ---- test clips: noisy 1080p with audio (hard to compress), and a tiny already-compressed one ----
    $big = Join-Path $T 'big clip ünï.mp4'
    Ff @('-f','lavfi','-i','testsrc2=size=1920x1080:rate=30:duration=8','-f','lavfi','-i','sine=frequency=440:duration=8',
         '-vf','noise=alls=20:allf=t','-c:v','libx264','-preset','ultrafast','-crf','12','-c:a','pcm_s16le','-shortest','-y',$big) 8
    $small = Join-Path $T 'small.mp4'
    Ff @('-f','lavfi','-i','color=c=gray:size=320x240:rate=15:duration=3','-c:v','libx264','-crf','40','-preset','veryslow','-an','-y',$small) 3
    $portrait = Join-Path $T 'portrait.mp4'
    Ff @('-f','lavfi','-i','testsrc2=size=1080x1920:rate=30:duration=3','-c:v','libx264','-preset','ultrafast','-crf','14','-an','-y',$portrait) 3
    $srcLen = (Get-Item $big).Length
    $pi = Probe $big
    Check 'probe' ($pi.Width -eq 1920 -and $pi.Height -eq 1080 -and [math]::Abs($pi.Duration - 8) -lt 0.2 -and $pi.HasAudio) "$($pi.Width)x$($pi.Height) $($pi.Duration)s fps=$($pi.Fps)"

    # ---- every template ----
    foreach ($tp in $VC.GetField('Templates').GetValue($null)) {
        $out = Join-Path $T ("t-" + ($tp.Name -replace '[^A-Za-z0-9]','') + ".mp4")
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $r = Compress $big $out $tp.Options.Clone() $null
        $sw.Stop()
        if (-not (Test-Path $out)) { Check "template $($tp.Name)" $false $r.Outcome; continue }
        $o = Probe $out; $len = (Get-Item $out).Length
        $ok = $len -lt $srcLen -and [math]::Abs($o.Duration - 8) -lt 0.3 -and $o.HasVideo
        $ok = $ok -and (($tp.Options.AudioKbps -eq 0) -ne $o.HasAudio)
        $exp = if ($tp.Options.MaxWidth -eq 1280) { 720 } elseif ($tp.Options.MaxHeight -gt 0 -and $tp.Options.MaxHeight -lt 1080) { $tp.Options.MaxHeight } else { 1080 }
        if ($tp.Options.TargetMB -gt 0) {
            $ok = $ok -and $len -le $tp.Options.TargetMB * 1MB * 1.05
            if ($tp.Options.TargetMB -lt 20) { $exp = 720 }
        }
        $ok = $ok -and $o.Height -eq $exp -and $o.Width -eq [int]($exp * 16 / 9)
        $codecOk = if ($tp.Options.Codec.ToString() -eq 'H265') { $o.VideoCodec -eq 'hevc' } else { $o.VideoCodec -eq 'h264' }
        Check "template $($tp.Name)" ($ok -and $codecOk) ("{0:N1} MB -> {1:N2} MB, {2}x{3} {4} {5:N0}s" -f ($srcLen/1MB), ($len/1MB), $o.Width, $o.Height, $o.VideoCodec, $sw.Elapsed.TotalSeconds)
    }

    # ---- custom: target size accuracy, fps cap, audio removal ----
    $o = Tpl 'Balanced (recommended)'; $o.TargetMB = 3; $o.MaxFps = 24; $o.AudioKbps = 0
    $out = Join-Path $T 'target3.mp4'; $r = Compress $big $out $o $null
    $len = (Get-Item $out).Length; $p = Probe $out
    Check 'target 3 MB within 10%' ([math]::Abs($len - 3MB) / 3MB -lt 0.10) ("{0:N2} MB" -f ($len/1MB))
    Check 'fps capped at 24' ([math]::Abs($p.Fps - 24) -lt 0.1) "fps=$($p.Fps)"
    Check 'audio removed' (-not $p.HasAudio)

    $o = Tpl 'Balanced (recommended)'; $o.Codec = $o.Codec.GetType()::H265; $o.TargetMB = 2
    $out = Join-Path $T 'target265.mp4'; $r = Compress $big $out $o $null
    $len = (Get-Item $out).Length
    Check 'H.265 two-pass target' ((Probe $out).VideoCodec -eq 'hevc' -and [math]::Abs($len - 2MB) / 2MB -lt 0.15) ("{0:N2} MB" -f ($len/1MB))

    # ---- portrait stays portrait; 720p limits the short side ----
    $o = Tpl 'Messaging (plays everywhere)'; $out = Join-Path $T 'portrait-out.mp4'; $r = Compress $portrait $out $o $null
    $p = Probe $out
    Check 'portrait 720 short side' ($p.Width -eq 720 -and $p.Height -eq 1280) "$($p.Width)x$($p.Height)"

    # ---- never upscale ----
    $o = Tpl 'Reduced Resolution'; $o.Crf = 18; $out = Join-Path $T 'noup.mp4'
    $r = Compress $small $out $o $null
    Check 'already-small file kept' ($r.Outcome -eq 'KeptOriginal' -and -not (Test-Path $out)) $r.Outcome

    # ---- warning for impossible targets ----
    $o = Tpl 'Fit under 10 MB'; $o.TargetMB = 0.05; $out = Join-Path $T 'tiny.mp4'
    $r = Compress $big $out $o $null
    Check 'too-small target warns' ($r.Warning -ne $null) "$($r.Warning)"

    # ---- cancel mid-run: no partial file, no ffmpeg left ----
    $o = Tpl 'Archive (near-lossless)'; $out = Join-Path $T 'cancel.mp4'
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $cancelled = $false
    try { Compress $big $out $o { $sw.ElapsedMilliseconds -gt 1500 } | Out-Null } catch { $cancelled = $_.Exception.InnerException -is [OperationCanceledException] }
    Start-Sleep -Milliseconds 300
    Check 'cancel' ($cancelled -and -not (Test-Path $out) -and $FF.GetProperty('RunningCount').GetValue($null) -eq 0 -and -not (Get-Process | Where-Object { $_.Path -eq $exe2 })) "partial exists=$(Test-Path $out)"

    # ---- bad input ----
    $bad = Join-Path $T 'broken.mp4'; [IO.File]::WriteAllBytes($bad, [byte[]](0..255 + 0..255))
    $msg = ''; try { Compress $bad (Join-Path $T 'b.mp4') (Tpl 'Balanced (recommended)') $null | Out-Null } catch { $msg = $_.Exception.InnerException.Message }
    Check 'corrupt input friendly error' ($msg -match "isn't a readable video|no video") $msg

    # ---- read-only output folder ----
    $ro = Join-Path $T 'ro'; New-Item -ItemType Directory $ro | Out-Null
    icacls $ro /deny "${env:USERNAME}:(W,AD,WD)" | Out-Null
    $msg = ''; try { Compress $small (Join-Path $ro 'x.mp4') (Tpl 'Online Sharing') $null | Out-Null } catch { $msg = $_.Exception.InnerException.Message }
    icacls $ro /remove:d "${env:USERNAME}" | Out-Null
    Check 'read-only folder friendly error' ($msg -match "isn't allowed|couldn't") $msg

    # ---- UI tool: accepts videos, rejects images, describes ----
    Add-Type -AssemblyName System.Windows.Forms
    $tool = [Activator]::CreateInstance($asm.GetType('Konbini.VideoCompressorTool'))
    $view = $tool.CreateView()
    $acc = $view.GetType().GetField('Accept').GetValue($view)
    $png = Join-Path $T 'x.png'; $bmp = New-Object Drawing.Bitmap 4,4; $bmp.Save($png); $bmp.Dispose()
    Check 'accepts video, rejects image' ($acc.Invoke($big) -and -not $acc.Invoke($png))
    $desc = $view.GetType().GetField('Describe').GetValue($view).Invoke($big)
    Check 'describe' ($desc -match '1920 × 1080 · 0:08') $desc
    $th = $view.GetType().GetField('Thumbnail').GetValue($view).Invoke($big, 128)
    Check 'video thumbnail' ($th -ne $null -and $th.Width -eq 128 -and $th.Height -eq 72) "$($th.Width)x$($th.Height)"
    $view.Dispose()
}
finally {
    Remove-Item $T -Recurse -Force -ErrorAction SilentlyContinue
    $left = Get-ChildItem $env:TEMP -Filter 'konbini-2pass-*' -ErrorAction SilentlyContinue
    Check 'temp files cleaned' (-not $left) ""
}
if ($fail) { Write-Host "$fail FAILED" -ForegroundColor Red; exit 1 } else { Write-Host 'ALL PASSED' }
