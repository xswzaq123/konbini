# Home page: drop analysis, suggestions, opening a tool with the files, and the busy guard.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File tests\home.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$root = Split-Path $PSScriptRoot
$work = Join-Path $env:TEMP ("konbini home " + [guid]::NewGuid().ToString('N').Substring(0, 6))
New-Item -ItemType Directory "$work\sub" | Out-Null
$script:fails = 0
function Check($name, $cond, $detail = '') { if ($cond) { Write-Host "PASS $name $detail" } else { Write-Host "FAIL $name $detail" -ForegroundColor Red; $script:fails++ } }
function Pump($ms) { $sw = [Diagnostics.Stopwatch]::StartNew(); while ($sw.ElapsedMilliseconds -lt $ms) { [Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 15 } }
function WaitFor($sb, $ms = 20000) { $sw = [Diagnostics.Stopwatch]::StartNew(); while (-not (& $sb)) { if ($sw.ElapsedMilliseconds -gt $ms) { return $false }; Pump 50 }; $true }
function Find-Ctl($c, $pred) { foreach ($x in $c.Controls) { if (& $pred $x) { return $x }; $r = Find-Ctl $x $pred; if ($r) { return $r } } }

# Test files: 3 images (one in a subfolder), a PDF, a CSV, an unknown file
foreach ($n in "$work\a.jpg", "$work\b.png", "$work\sub\c.png") { $b = New-Object Drawing.Bitmap 64, 48; $b.Save($n); $b.Dispose() }
[IO.File]::WriteAllText("$work\doc.pdf", "%PDF-1.4`n1 0 obj<<>>endobj`ntrailer<<>>`n%%EOF")
[IO.File]::WriteAllText("$work\data.csv", "name,value`na,1`nb,2`n")
[IO.File]::WriteAllBytes("$work\mystery.xyz", [byte[]](0..255 | ForEach-Object { [byte](($_ * 37) % 256) }))

$asm = [Reflection.Assembly]::LoadFile("$root\dist\Konbini.exe")
$form = $asm.CreateInstance('Konbini.MainForm'); $form.Show(); Pump 400
$nav = Find-Ctl $form { param($x) $x -is [Windows.Forms.ListBox] }
$homeView = Find-Ctl $form { param($x) $x.GetType().Name -eq 'HomeView' }
Check 'starts on Home' ($homeView -ne $null -and $homeView.Visible) "selected=$($nav.SelectedItem.Name)"
Check 'Home is first in the sidebar' ("$($nav.Items[0].Name)" -eq 'Home')

# Drop: the folder (expanded recursively) plus loose files
$homeView.Analyse([string[]]@("$work\sub", "$work\a.jpg", "$work\b.png", "$work\doc.pdf", "$work\data.csv", "$work\mystery.xyz"))
Check 'analysis finishes' (WaitFor { $homeView.Last -ne $null })
$a = $homeView.Last
Check 'counts' ($a.Images -eq 3 -and $a.Others -eq 3) $a.Summary
$names = @($a.Suggestions | ForEach-Object { $_.Tool.Name })
Check 'suggestions ordered' ($names[0] -eq 'File Converter' -and $names -contains 'Background Remover' -and $names.IndexOf('Image Compressor') -lt $names.IndexOf('Background Remover')) ($names -join ', ')
$bg = $a.Suggestions | Where-Object { $_.Tool.Name -eq 'Background Remover' }
Check 'bg remover takes the 3 images' ($bg.Files.Count -eq 3)
$cards = @(); foreach ($c in (Find-Ctl $homeView { param($x) $x -is [Windows.Forms.FlowLayoutPanel] -and $x.Controls.Count -gt 0 -and $x.Controls[0].GetType().Name -eq 'SuggestionCard' }).Controls) { $cards += $c }
Check 'one card per suggestion' ($cards.Count -eq $a.Suggestions.Count) "$($cards.Count) cards"

# Simple vs. settings-heavy tools: one-click run button + "More options" link, or "Open with N files"
$comp = $cards | Where-Object { $_.Heading -eq 'Image Compressor' }
$resz = $cards | Where-Object { $_.Heading -eq 'Image Resizer' }
Check 'compressor has a run button and a tool link' ($comp.MainButton.Text -eq 'Compress' -and $comp.Count -eq '3 images' -and $comp.MoreLink -ne $null) $comp.MainButton.Text
Check 'resizer opens its tool instead' ($resz.MainButton.Text -like 'Open tool*' -and $resz.Count -eq '3 images' -and $resz.MoreLink -eq $null) $resz.MainButton.Text

# One click: compress right on Home, saved next to the originals, never leaving Home
$comp.MainButton.PerformClick()
Check 'quick run finishes on Home' (WaitFor { $homeView.RunStatus -like 'Done*' } 60000) $homeView.RunStatus
Check 'still on Home' ("$($nav.SelectedItem.Name)" -eq 'Home')
$outs = @(Get-ChildItem $work -Recurse -File | Where-Object { $_.Name -like '*_compressed*' -or $_.Name -like '*_min*' -or ($_.BaseName -notin 'a','b','c','doc','data','mystery') })
Check 'outputs next to the originals' ($outs.Count -eq 3) (($outs | ForEach-Object { $_.FullName.Substring($work.Length) }) -join ', ')
Check 'originals untouched' ((Test-Path "$work\a.jpg") -and (Test-Path "$work\sub\c.png"))
$outs | Remove-Item

# "More options" on Background Remover: the tool opens with exactly those files
$card = $cards | Where-Object { $_.Heading -eq 'Background Remover' }
$link = $card.MoreLink
$link.GetType().GetMethod('OnLinkClicked', [Reflection.BindingFlags]'NonPublic,Instance').Invoke($link, @([Windows.Forms.LinkLabelLinkClickedEventArgs](New-Object Windows.Forms.LinkLabelLinkClickedEventArgs $link.Links[0]))); Pump 600
$panel = Find-Ctl $form { param($x) $x.GetType().Name -eq 'BatchImagePanel' -and $x.Visible }
Check 'tool opened' ("$($nav.SelectedItem.Name)" -eq 'Background Remover')
Check 'files in its batch' ($panel -ne $null -and $panel.Files.Length -eq 3) "files=$($panel.Files.Length)"

# Busy: going Home is refused (message answered by a timer)
$script:boxes = 0
$t = New-Object Windows.Forms.Timer; $t.Interval = 250
$t.add_Tick({ if ([Windows.Forms.Form]::ActiveForm -eq $null) { $script:boxes++; [Windows.Forms.SendKeys]::SendWait('{ENTER}') } }); $t.Start()
$panel.GetType().GetProperty('Busy').SetValue($panel, $true)
$nav.SelectedIndex = 0; Pump 800
Check 'Home blocked while busy' ("$($nav.SelectedItem.Name)" -eq 'Background Remover' -and $panel.Files.Length -eq 3 -and $script:boxes -gt 0)
$panel.GetType().GetProperty('Busy').SetValue($panel, $false)
$nav.SelectedIndex = 0; Pump 500
Check 'Home after the run, batch cleared' ("$($nav.SelectedItem.Name)" -eq 'Home' -and $panel.Files.Length -eq 0)
$t.Stop()

# An unknown file type is still offered to the File Converter
$homeView.Analyse([string[]]@("$work\mystery.xyz")); WaitFor { $homeView.Last -ne $null -and $homeView.Last.Others -eq 1 } | Out-Null
Check 'unknown file: only File Converter (it can zip anything)' ($homeView.Last.Suggestions.Count -eq 1 -and $homeView.Last.Suggestions[0].Tool.Name -eq 'File Converter') $homeView.Last.Summary

$form.Close(); Remove-Item $work -Recurse -Force
if ($script:fails) { Write-Host "$script:fails FAILED" -ForegroundColor Red; exit 1 } else { Write-Host 'ALL PASSED' }
