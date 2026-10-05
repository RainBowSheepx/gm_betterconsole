<#
.SYNOPSIS
  Draws the BetterConsole icon (src/BetterConsole.App/Assets/app.ico + logo.png). Run it again after
  changing the colours; the generated files are committed.
#>
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root "src\BetterConsole.App\Assets"
New-Item -ItemType Directory -Force $assets | Out-Null

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear([System.Drawing.Color]::Transparent)

    $pad = [Math]::Max(1, [int]($size * 0.04))
    $rect = New-Object System.Drawing.RectangleF $pad, $pad, ($size - 2 * $pad), ($size - 2 * $pad)
    $radius = $rect.Width * 0.22
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $c1 = [System.Drawing.Color]::FromArgb(255, 37, 99, 235)    # blue
    $c2 = [System.Drawing.Color]::FromArgb(255, 124, 58, 237)   # violet
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, $c1, $c2, 45.0
    $g.FillPath($brush, $path)

    # Window chrome hint: a lighter strip at the top
    if ($size -ge 32) {
        $strip = New-Object System.Drawing.Drawing2D.GraphicsPath
        $sh = $rect.Height * 0.16
        $strip.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
        $strip.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
        $strip.AddLine($rect.Right, $rect.Y + $sh, $rect.X, $rect.Y + $sh)
        $strip.CloseFigure()
        $g.SetClip($path)
        $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(45, 255, 255, 255))), $rect.X, $rect.Y, $rect.Width, $sh)
        $g.ResetClip()
        $dot = $sh * 0.42
        $dy = $rect.Y + ($sh - $dot) / 2
        for ($i = 0; $i -lt 3; $i++) {
            $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(190, 255, 255, 255))), ($rect.X + $sh * 0.6 + $i * $dot * 1.7), $dy, $dot, $dot)
        }
    }

    # ">_" prompt
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $penW = [Math]::Max(1.5, $size * 0.085)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), $penW
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $cx = $rect.X + $rect.Width * 0.26
    $cy = $rect.Y + $rect.Height * 0.58
    $arm = $rect.Width * 0.17
    $g.DrawLines($pen, [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF $cx, ($cy - $arm)),
        (New-Object System.Drawing.PointF ($cx + $arm), $cy),
        (New-Object System.Drawing.PointF $cx, ($cy + $arm))))
    $ux = $rect.X + $rect.Width * 0.52
    $g.DrawLine($pen, $ux, ($cy + $arm), ($ux + $rect.Width * 0.24), ($cy + $arm))

    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = @()
foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , $ms.ToArray()
    if ($s -eq 256) { $bmp.Save((Join-Path $assets "logo.png"), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
}

# ICO with PNG frames
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $b = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$b); $w.Write([byte]$b); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$pngs[$i].Length); $w.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $assets "app.ico"), $out.ToArray())
Write-Host "Wrote $assets\app.ico and logo.png"
