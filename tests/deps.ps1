# Dependency manager tests. Usage: powershell -NoProfile -ExecutionPolicy Bypass -File tests\deps.ps1 [-offline]
# Uses a temporary install root (never touches your real %LOCALAPPDATA%\Konbini\deps).
# Without -offline it downloads ffmpeg (~114 MB) over HTTPS once to test download, cancel and resume.
param([switch]$offline)
$ErrorActionPreference = 'Stop'
$asm = [Reflection.Assembly]::LoadFile((Resolve-Path "$PSScriptRoot\..\dist\Konbini.exe"))
$DM = $asm.GetType('Konbini.Deps.DepManager'); $Reg = $asm.GetType('Konbini.Deps.Registry'); $DepT = $asm.GetType('Konbini.Deps.Dependency')
$fail = 0
function Check($name, $ok, $detail) { if ($ok) { Write-Host "PASS $name $detail" } else { Write-Host "FAIL $name $detail" -ForegroundColor Red; $script:fail++ } }
function Dep($n) { $Reg.GetField($n).GetValue($null) }
function Status($d) { "$($DM.GetMethod('Status').Invoke($null, @($d)))" }
function Inner($e) { $x = $e.Exception; while ($x.InnerException) { $x = $x.InnerException }; $x }
function Ensure($d, $cancel) {
    $cb = if ($cancel) { [Func[bool]]$cancel } else { $null }
    $DM.GetMethod('EnsureInstalled').Invoke($null, [object[]]@($d, $null, $cb))
}
$models = Resolve-Path "$PSScriptRoot\..\models"
$root = [string](Join-Path $env:TEMP ("konbini-deps-" + [guid]::NewGuid().ToString('N').Substring(0,8)))
$DM.GetField('RootOverride').SetValue($null, $root)
try {
    $full = Dep 'BgFull'; $ff = Dep 'Ffmpeg'
    Check 'embedded deps present' ((Status (Dep 'Upscaler')) -eq 'Installed' -and (Status (Dep 'BgLite')) -eq 'Installed')
    Check 'downloadable starts missing' ((Status $full) -eq 'Missing' -and (Status $ff) -eq 'Missing')
    Check 'large deps not in exe' (-not $asm.GetManifestResourceInfo('Konbini.bg32.onnx') -and -not $asm.GetManifestResourceInfo('Konbini.ffmpeg.gz'))

    # every registry entry is well formed: https on an allowed host, 64-hex hashes, sizes set
    $allowed = $Reg.GetField('AllowedHosts').GetValue($null)
    $bad = @()
    foreach ($d in $Reg.GetField('All').GetValue($null)) {
        if ("$($d.Source)" -eq 'Embedded') { if (-not $asm.GetManifestResourceInfo($d.Resource)) { $bad += "$($d.Id): resource missing" }; continue }
        $u = [Uri]$d.Url
        if ($u.Scheme -ne 'https' -or $allowed -notcontains $u.Host) { $bad += "$($d.Id): url" }
        if ($d.Sha256 -notmatch '^[0-9a-f]{64}$' -or $d.DownloadSha256 -notmatch '^[0-9a-f]{64}$') { $bad += "$($d.Id): hash" }
        if ($d.Size -le 0 -or $d.DownloadSize -le 0) { $bad += "$($d.Id): size" }
    }
    Check 'registry entries well formed' (-not $bad) ($bad -join '; ')

    # wrong file rejected
    $junk = Join-Path $env:TEMP 'konbini-junk.onnx'; [IO.File]::WriteAllBytes($junk, [byte[]](1..100))
    $msg = ''; try { $DM.GetMethod('InstallFromFile').Invoke($null, @($full, [string]$junk)) } catch { $msg = (Inner $_).Message }
    Remove-Item $junk
    Check 'wrong file rejected' ($msg -match "checksum doesn't match" -and (Status $full) -eq 'Missing') $msg

    # install from local file; a second install never rewrites a verified copy
    if (Test-Path "$models\isnet-general-use.onnx") {
        $DM.GetMethod('InstallFromFile').Invoke($null, @($full, "$models\isnet-general-use.onnx"))
        $p = $DM.GetMethod('InstallPath').Invoke($null, @($full)); $t1 = (Get-Item $p).LastWriteTimeUtc
        Check 'install from file' ((Status $full) -eq 'Installed')
        Start-Sleep -Milliseconds 50
        Ensure $full $null
        $DM.GetMethod('InstallFromFile').Invoke($null, @($full, "$models\isnet-general-use.onnx"))
        Check 'verified copy not overwritten' ((Get-Item $p).LastWriteTimeUtc -eq $t1)

        # locked while in use: can't be swapped or deleted
        $h = $DM.GetMethod('Acquire').Invoke($null, @($full))
        $del = $true; try { Remove-Item $p -ErrorAction Stop } catch { $del = $false }
        $wr = $true; try { [IO.File]::OpenWrite($p).Dispose() } catch { $wr = $false }
        $h.Dispose()
        Check 'acquired file locked against replace/delete' (-not $del -and -not $wr)

        # damaged copy detected, refused, and repaired
        $fs = [IO.File]::OpenWrite($p); $fs.Seek(1000, 'Begin') | Out-Null; $fs.WriteByte(0x55); $fs.WriteByte(0xAA); $fs.Dispose()
        (Get-Item $p).LastWriteTimeUtc = [DateTime]::UtcNow.AddMinutes(1)
        Check 'damage detected' ((Status $full) -eq 'Damaged')
        $msg = ''; try { $DM.GetMethod('Acquire').Invoke($null, @($full)) } catch { $e = Inner $_; $msg = $e.Message; $needs = $e.NeedsInstall }
        Check 'damaged copy never used' ($msg -match 'damaged' -and $needs) $msg
        $DM.GetMethod('InstallFromFile').Invoke($null, @($full, "$models\isnet-general-use.onnx"))
        Check 'damaged copy repaired' ((Status $full) -eq 'Installed')

        # concurrent installs: one wins, the other sees it installed; no temp files left
        $DM.GetMethod('Uninstall').Invoke($null, @($full))
        Check 'uninstall' ((Status $full) -eq 'Missing')
        $ps = 1..3 | ForEach-Object {
            $rs = [PowerShell]::Create()
            [void]$rs.AddScript({ param($asmPath, $root, $file)
                $a = [Reflection.Assembly]::LoadFile($asmPath); $dm = $a.GetType('Konbini.Deps.DepManager')
                $dm.GetField('RootOverride').SetValue($null, $root)
                $d = $a.GetType('Konbini.Deps.Registry').GetField('BgFull').GetValue($null)
                $dm.GetMethod('InstallFromFile').Invoke($null, @($d, $file)); 'ok'
            }).AddArgument($asm.Location).AddArgument($root).AddArgument("$models\isnet-general-use.onnx")
            @{ R = $rs; H = $rs.BeginInvoke() }
        }
        $res = $ps | ForEach-Object { try { $_.R.EndInvoke($_.H) } catch { "err: $($_.Exception.Message)" }; $_.R.Dispose() }
        $left = Get-ChildItem (Split-Path $p) -Force | Where-Object { $_.Name -ne 'isnet-general-use.onnx' }
        Check 'concurrent installs safe' (($res | Where-Object { $_ -eq 'ok' }).Count -eq 3 -and (Status $full) -eq 'Installed' -and -not $left) ("results=" + ($res -join ',') + " leftovers=" + ($left.Name -join ','))
    }

    # ZIP path traversal
    $bad = @('../evil.exe', '..\..\evil.exe', 'C:\Windows\evil.exe', '/abs.exe', 'a/../../evil.exe')
    $rejected = 0
    foreach ($b in $bad) { try { $DM.GetMethod('SafeEntryPath').Invoke($null, @($ff, [string]$root, [string]$b)) | Out-Null } catch { $rejected++ } }
    $okPath = $DM.GetMethod('SafeEntryPath').Invoke($null, @($ff, [string]$root, "dir/bin/ffmpeg.exe"))
    Check 'zip traversal rejected' ($rejected -eq $bad.Count -and $okPath.StartsWith($root)) "$rejected/$($bad.Count)"

    # only https on allow-listed hosts
    foreach ($u in 'http://github.com/x.bin', 'https://example.com/x.bin') {
        $d = [Activator]::CreateInstance($DepT)
        $d.Id = 'test-url'; $d.DisplayName = 'Test'; $d.Source = 'Download'; $d.FileName = 'x.bin'; $d.Url = $u
        $d.Sha256 = ('0' * 64); $d.DownloadSha256 = ('0' * 64); $d.DownloadSize = 10; $d.Size = 10
        $msg = ''; try { Ensure $d $null } catch { $msg = (Inner $_).Message }
        Check "refuses $u" ($msg -match 'approved secure source' -and -not (Test-Path (Join-Path $root 'test-url\x.bin'))) $msg
    }

    # tampered download: a full-size .partial with wrong bytes is rejected, deleted, never extracted
    $d = [Activator]::CreateInstance($DepT)
    $d.Id = 'test-tamper'; $d.DisplayName = 'Tamper test'; $d.Source = 'Download'; $d.FileName = 'x.exe'; $d.Executable = $true
    $d.Url = 'https://github.com/x/y/releases/download/1/x.zip'; $d.Sha256 = ('1' * 64); $d.DownloadSha256 = ('2' * 64); $d.DownloadSize = 4096; $d.Size = 10
    $tdir = Join-Path $root 'test-tamper'; New-Item -ItemType Directory $tdir -Force | Out-Null
    [IO.File]::WriteAllBytes("$tdir\download.partial", (New-Object byte[] 4096)); Set-Content "$tdir\download.partial.url" $d.Url -NoNewline
    $msg = ''; try { Ensure $d $null } catch { $msg = (Inner $_).Message }
    Check 'tampered download rejected' ($msg -match 'corrupted' -and -not (Test-Path "$tdir\download.partial") -and -not (Test-Path "$tdir\x.exe")) $msg

    # stale temp files from a crash are cleaned on the next attempt
    Set-Content "$tdir\abc.tmp" 'x'
    try { Ensure $d $null } catch { }
    Check 'stale temp cleaned' (-not (Test-Path "$tdir\abc.tmp"))

    # real HTTPS download: cancel part-way (partial kept, nothing installed), then resume to a verified install
    if (-not $offline) {
        $partial = Join-Path $root 'ffmpeg\download.partial'
        $cancelled = $false
        try { Ensure $ff { (Test-Path $partial) -and (Get-Item $partial).Length -gt 15MB } } catch { $cancelled = (Inner $_) -is [OperationCanceledException] }
        $plen = if (Test-Path $partial) { (Get-Item $partial).Length } else { 0 }
        Check 'download cancel keeps partial' ($cancelled -and $plen -gt 15MB -and (Status $ff) -eq 'Missing') ("{0:N1} MB" -f ($plen/1MB))
        $sw = [Diagnostics.Stopwatch]::StartNew()
        Ensure $ff $null
        $exe = $DM.GetMethod('InstallPath').Invoke($null, @($ff))
        $leftovers = Get-ChildItem (Split-Path $exe) -Force | Where-Object { $_.Name -ne 'ffmpeg.exe' }
        Check 'download resumes and verifies' ((Status $ff) -eq 'Installed' -and -not $leftovers) ("{0:N0}s, leftovers={1}" -f $sw.Elapsed.TotalSeconds, ($leftovers.Name -join ','))
        $v = & $exe -version 2>$null | Select-Object -First 1
        Check 'installed ffmpeg runs' ($v -match 'ffmpeg version 9.1') $v
        $hq = Dep 'BgHq'
        $sw.Restart(); Ensure $hq $null
        Check 'High-quality model downloads and verifies' ((Status $hq) -eq 'Installed') ("{0:N0}s" -f $sw.Elapsed.TotalSeconds)
        $sw.Restart(); Ensure $ff $null
        Check 'no re-download when installed' ($sw.ElapsedMilliseconds -lt 3000) "$($sw.ElapsedMilliseconds) ms"
    }
}
finally {
    $DM.GetField('RootOverride').SetValue($null, $null)
    Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
}
if ($fail) { Write-Host "$fail FAILED" -ForegroundColor Red; exit 1 } else { Write-Host 'ALL PASSED' }
