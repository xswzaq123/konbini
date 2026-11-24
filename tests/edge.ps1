param([string]$demo)
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type -ReferencedAssemblies System.Windows.Forms @'
using System; using System.IO; using System.Text; using System.Runtime.InteropServices;
public static class Dlg {
  delegate bool EP(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(EP f, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr p, EP f, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
  [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  public static string Last = "";
  // Finds our process's message box, records its text, clicks the named button.
  public static bool Answer(string button) {
    uint me = (uint)System.Diagnostics.Process.GetCurrentProcess().Id; bool done = false;
    EnumWindows((h, l) => {
      uint pid; GetWindowThreadProcessId(h, out pid); var c = new StringBuilder(64); GetClassName(h, c, 64);
      if (pid != me || c.ToString() != "#32770" || !IsWindowVisible(h)) return true;
      var text = new StringBuilder(); IntPtr btn = IntPtr.Zero;
      EnumChildWindows(h, (ch, l2) => { var cc = new StringBuilder(64); GetClassName(ch, cc, 64); var t = new StringBuilder(512); GetWindowText(ch, t, 512);
        if (cc.ToString() == "Static") text.Append(t).Append(' ');
        if (cc.ToString() == "Button" && t.ToString().Replace("&","") == button) btn = ch; return true; }, IntPtr.Zero);
      Last = text.ToString().Trim(); if (btn != IntPtr.Zero) SendMessage(btn, 0xF5, IntPtr.Zero, IntPtr.Zero); done = true; return false; }, IntPtr.Zero);
    return done;
  }
  public static Func<string,string,string> Partial(Func<string,string> reserve) {
    return (src, b) => { var p = reserve(b + ".bin"); File.WriteAllBytes(p, new byte[1000]); throw new IOException("boom"); };
  }
  public static Exception DiskFull() { return new IOException("x", unchecked((int)0x80070070)); }
  public static Exception Locked() { return new IOException("x", unchecked((int)0x80070020)); }
}
'@
$a=[Reflection.Assembly]::LoadFile((Join-Path (Split-Path $PSScriptRoot) 'dist\Konbini.exe'))
$BF=[Reflection.BindingFlags]'NonPublic,Instance'
$f=$a.CreateInstance('Konbini.MainForm');$f.Show()
function Find-C($c,$p){foreach($x in $c.Controls){if(& $p $x){return $x};$r=Find-C $x $p;if($r){return $r}}}
function Pump($n){for($i=0;$i -lt $n;$i++){[Windows.Forms.Application]::DoEvents();Start-Sleep -m 20}}
function Tool($name){$nav=Find-C $f {param($x)$x -is [Windows.Forms.ListBox]};for($i=0;$i -lt $nav.Items.Count;$i++){if("$($nav.Items[$i].Name)" -eq $name){$nav.SelectedIndex=$i}};Pump 5
  Find-C $f {param($x)$x.GetType().Name -eq 'BatchImagePanel' -and $x.Visible}}
function Load($p,$paths){ $p.GetType().GetMethod('Clear',$BF) | Out-Null; $p.List.Items.Clear()
  $p.GetType().GetField('files',$BF).GetValue($p).Clear(); $p.GetType().GetField('itemsByPath',$BF).GetValue($p).Clear()
  $p.GetType().GetMethod('AddPaths',$BF).Invoke($p,@(,[string[]]$paths)); Pump 30 }
function SetOut($p,$dir){ $p.GetType().GetField('sameFolder',$BF).GetValue($p).Checked = -not $dir; if($dir){$p.GetType().GetField('outDir',$BF).GetValue($p).Text=$dir} }
$script:answer='OK'; $script:dialogs=New-Object Collections.ArrayList
$tm=New-Object Windows.Forms.Timer; $tm.Interval=150; $tm.add_Tick({ if([Dlg]::Answer($script:answer)){ [void]$script:dialogs.Add([Dlg]::Last) } }); $tm.Start()
function RunIt($p){ $script:dialogs.Clear(); $p.GetType().GetMethod('Run',$BF).Invoke($p,@()); for($i=0;$i -lt 500 -and $p.Busy;$i++){Pump 1}; Pump 10 }
function Results($p){ ($p.List.Items | % { $_.SubItems[3].Text }) -join ' | ' }
$fail=0; function Check($name,$cond,$info){ if($cond){"PASS $name"}else{"FAIL $name :: $info"; $script:fail++} }

$root=Join-Path $env:TEMP ("konbini edge " + [guid]::NewGuid().ToString('N').Substring(0,6))
$uni=Join-Path $root "folder with spaces ü 写真"; New-Item -ItemType Directory $uni | Out-Null
$png=(Get-ChildItem $demo -File | ? Extension -eq '.png' | select -First 1).FullName; if(-not $png){$png=(Get-ChildItem $demo -File|select -First 1).FullName}
$ext=[IO.Path]::GetExtension($png)
$u1=Join-Path $uni ("フォト #1 & co (final)" + $ext); Copy-Item -LiteralPath $png $u1
$me=[Security.Principal.WindowsIdentity]::GetCurrent().Name

$p=Tool 'Image Resizer'; $p.Suffix='_r'
# A: Unicode, spaces, special characters
SetOut $p $null; Load $p @($u1); RunIt $p
$out1=Get-ChildItem -LiteralPath $uni | ? Name -like '*_r*' | select -First 1
Check "unicode/space name" ($out1 -and (Results $p) -notmatch 'Error') (Results $p)
# B: re-run never overwrites the earlier output
$t1=$out1.LastWriteTimeUtc; Start-Sleep -m 50; RunIt $p
$outs=@(Get-ChildItem -LiteralPath $uni | ? Name -like '*_r*')
Check "rerun keeps earlier output" ((Get-Item -LiteralPath $out1.FullName).LastWriteTimeUtc -eq $t1 -and $outs.Count -eq 2) (($outs|% Name) -join ',')
# C: source locked by another program
$lockedSrc=Join-Path $root ("locked" + $ext); Copy-Item -LiteralPath $png $lockedSrc; Load $p @($lockedSrc)
$fs=[IO.File]::Open($lockedSrc,'Open','ReadWrite','None'); RunIt $p; $fs.Close()
Check "locked file message" ((Results $p) -match 'in use by another program') (Results $p)
# D: output folder not writable -> clear message, nothing starts
$ro=Join-Path $root "readonly out"; New-Item -ItemType Directory $ro | Out-Null
icacls $ro /deny "${me}:(W,AD,WD)" | Out-Null
Load $p @($u1); SetOut $p $ro; RunIt $p
Check "unwritable output folder" (($script:dialogs -join ' ') -match "(?s)Can't save to the output folder.*Access denied") ($script:dialogs -join ' ')
# E: originals in a read-only folder, saving next to them
$rosrc=Join-Path $root "readonly src"; New-Item -ItemType Directory $rosrc | Out-Null; $r1=Join-Path $rosrc ("a" + $ext); Copy-Item -LiteralPath $png $r1
icacls $rosrc /deny "${me}:(W,AD,WD)" | Out-Null
SetOut $p $null; Load $p @($r1); RunIt $p
Check "read-only source folder" ((Results $p) -match 'Access denied') (Results $p)
# F: a failure halfway through writing leaves no partial file
$pf=Join-Path $root "partial"; New-Item -ItemType Directory $pf | Out-Null; $s1=Join-Path $pf ("s" + $ext); Copy-Item -LiteralPath $png $s1
$old=$p.Process; $p.Process=[Dlg]::Partial([Func[string,string]]([Delegate]::CreateDelegate([Func[string,string]],$p,'ReservePath')))
Load $p @($s1); RunIt $p; $p.Process=$old
Check "partial output removed" (@(Get-ChildItem $pf).Count -eq 1 -and (Results $p) -match 'boom') ((Get-ChildItem $pf|% Name) -join ',')
# G: error wording for disk full / sharing violation
$fe=$a.GetType('Konbini.BatchImagePanel').GetMethod('FriendlyError')
Check "disk full message" ($fe.Invoke($null,@([Dlg]::DiskFull())) -match 'disk space') ''
Check "sharing violation message" ($fe.Invoke($null,@([Dlg]::Locked())) -match 'in use') ''
# H: missing file (deleted after adding)
$gone=Join-Path $root ("gone" + $ext); Copy-Item -LiteralPath $png $gone; Load $p @($gone); Remove-Item -LiteralPath $gone; RunIt $p
Check "deleted source message" ((Results $p) -match 'no longer exists|Error') (Results $p)
# I: closing during processing asks, stops, leaves no half-written files
$many=Join-Path $root "many"; New-Item -ItemType Directory $many | Out-Null
$big=New-Object Drawing.Bitmap 3000,3000; $g=[Drawing.Graphics]::FromImage($big); $rnd=New-Object Random 1
for($k=0;$k -lt 400;$k++){ $g.FillEllipse((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb($rnd.Next(255),$rnd.Next(255),$rnd.Next(255)))),$rnd.Next(3000),$rnd.Next(3000),300,300) }
$bigp=Join-Path $many "m0.png"; $big.Save($bigp); $g.Dispose(); $big.Dispose()
1..30 | % { Copy-Item -LiteralPath $bigp (Join-Path $many "m$_.png") }
$c=Tool 'Image Compressor'; $c.Suffix='_c'; SetOut $c $null; Load $c @(Get-ChildItem $many | % FullName)
$script:answer='Yes'; $script:dialogs.Clear(); $c.GetType().GetMethod('Run',$BF).Invoke($c,@()); Pump 1
$f.Close(); for($i=0;$i -lt 500 -and $f.Visible;$i++){Pump 1}
$done=@(Get-ChildItem $many | ? Name -like '*_c*').Count
Check "close mid-run asks first" (($script:dialogs -join ' ') -match 'still being processed') ($script:dialogs -join ' ')
Check "close mid-run stops early and closes" ((-not $f.Visible) -and $done -lt 31 -and -not $c.Busy) "visible=$($f.Visible) outputs=$done"
$bad=@(Get-ChildItem $many | ? Name -like '*_c*' | ? { try { [Drawing.Image]::FromFile($_.FullName).Dispose(); $false } catch { $true } }).Count
Check "no corrupt outputs after stop" ($bad -eq 0) "$bad corrupt"
$tm.Stop()
icacls $ro /remove:d $me | Out-Null; icacls $rosrc /remove:d $me | Out-Null; Remove-Item -LiteralPath $root -Recurse -Force
if($fail){"FAILED $fail"}else{"ALL PASSED"}
