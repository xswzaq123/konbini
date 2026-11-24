# Generates installer\konbini.ico (16-256 px, PNG-compressed entries): a white diamond on a rounded blue tile.
Add-Type -AssemblyName System.Drawing
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
    $b = New-Object Drawing.Bitmap $s, $s; $g = [Drawing.Graphics]::FromImage($b)
    $g.SmoothingMode = 'AntiAlias'; $g.Clear([Drawing.Color]::Transparent)
    $r = [single]($s * 0.22); $p = New-Object Drawing.Drawing2D.GraphicsPath
    $w = [single]($s - 1)
    $p.AddArc(0, 0, $r, $r, 180, 90); $p.AddArc($w - $r, 0, $r, $r, 270, 90); $p.AddArc($w - $r, $w - $r, $r, $r, 0, 90); $p.AddArc(0, $w - $r, $r, $r, 90, 90); $p.CloseFigure()
    $br = New-Object Drawing.Drawing2D.LinearGradientBrush((New-Object Drawing.Point 0, 0), (New-Object Drawing.Point $s, $s), [Drawing.Color]::FromArgb(64, 140, 255), [Drawing.Color]::FromArgb(30, 90, 220))
    $g.FillPath($br, $p)
    $c = $s / 2.0; $d = $s * 0.28
    $pts = [Drawing.PointF[]]@((New-Object Drawing.PointF $c, ($c - $d)), (New-Object Drawing.PointF ($c + $d), $c), (New-Object Drawing.PointF $c, ($c + $d)), (New-Object Drawing.PointF ($c - $d), $c))
    $g.FillPolygon([Drawing.Brushes]::White, $pts)
    $g.Dispose(); $ms = New-Object IO.MemoryStream; $b.Save($ms, [Drawing.Imaging.ImageFormat]::Png); $b.Dispose(); , $ms.ToArray()
}
$out = New-Object IO.MemoryStream; $bw = New-Object IO.BinaryWriter($out)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $bw.Write([byte]($s % 256)); $bw.Write([byte]($s % 256)); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset); $offset += $pngs[$i].Length
}
foreach ($png in $pngs) { $bw.Write($png) }
[IO.File]::WriteAllBytes("$PSScriptRoot\..\installer\konbini.ico", $out.ToArray())
Write-Host "Wrote installer\konbini.ico"
