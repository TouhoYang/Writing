# Writing - unified icon generator (ASCII only so it runs without a BOM in Windows PowerShell 5.1)
# Artwork: blue rounded square + white paper with text lines + a pen writing on the paper (with ink trail)
# Outputs: Windows app.ico / Android mipmap / HarmonyOS media / web favicon (base64)
# Drawing style: absolute coordinates + thick round-cap strokes (no Graphics transforms, no nested functions)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $MyInvocation.MyCommand.Path

function New-Logo([int]$size) {
  $W = $size * 4                                   # supersampled master, downscaled at the end
  $bmp = New-Object System.Drawing.Bitmap $W, $W
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $g.Clear([System.Drawing.Color]::Transparent)

  # --- rounded blue background ---
  $r = [int]($W * 0.22)
  $bg = New-Object System.Drawing.Drawing2D.GraphicsPath
  $bg.AddArc(0, 0, $r, $r, 180, 90)
  $bg.AddArc($W - $r, 0, $r, $r, 270, 90)
  $bg.AddArc($W - $r, $W - $r, $r, $r, 0, 90)
  $bg.AddArc(0, $W - $r, $r, $r, 90, 90)
  $bg.CloseFigure()
  $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.Point 0, 0), (New-Object System.Drawing.Point 0, $W),
    [System.Drawing.Color]::FromArgb(70, 132, 246), [System.Drawing.Color]::FromArgb(38, 94, 214))
  $g.FillPath($grad, $bg)

  # --- paper sheet ---
  $px = [int]($W * 0.16); $py = [int]($W * 0.18)
  $pw = [int]($W * 0.68); $ph = [int]($W * 0.64)
  $pr = [int]($W * 0.06)
  $paper = New-Object System.Drawing.Drawing2D.GraphicsPath
  $paper.AddArc($px, $py, $pr, $pr, 180, 90)
  $paper.AddArc($px + $pw - $pr, $py, $pr, $pr, 270, 90)
  $paper.AddArc($px + $pw - $pr, $py + $ph - $pr, $pr, $pr, 0, 90)
  $paper.AddArc($px, $py + $ph - $pr, $pr, $pr, 90, 90)
  $paper.CloseFigure()
  $paperBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
  $g.FillPath($paperBrush, $paper)

  # --- text lines on paper: 3 round-cap strokes, last one short (room for the pen) ---
  $linePen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(180, 199, 232)), ([float]($W * 0.040))
  $linePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
  $linePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
  $g.DrawLine($linePen, [float]($W * 0.245), [float]($W * 0.295), [float]($W * 0.760), [float]($W * 0.295))
  $g.DrawLine($linePen, [float]($W * 0.245), [float]($W * 0.415), [float]($W * 0.690), [float]($W * 0.415))
  $g.DrawLine($linePen, [float]($W * 0.245), [float]($W * 0.535), [float]($W * 0.560), [float]($W * 0.535))

  # --- pen: nib lands on the lower part of the paper, body goes up-right ---
  $tx = 0.430; $ty = 0.735                     # nib tip
  $bx = $tx + 0.0707; $by = $ty - 0.0707       # centre of nib base
  $ex = 0.815; $ey = 0.350                     # pen tail
  $nibW = 0.046                                # nib half width
  # ink trail left of the nib
  $inkPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(31, 58, 110)), ([float]($W * 0.019))
  $inkPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
  $inkPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
  $g.DrawLine($inkPen, [float]($W * 0.262), [float]($W * 0.802), [float]($W * 0.412), [float]($W * 0.745))
  # pen shadow
  $shadowPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(45, 10, 30, 70)), ([float]($W * 0.090))
  $shadowPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
  $shadowPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
  $g.DrawLine($shadowPen, [float]($W * ($bx + 0.014)), [float]($W * ($by + 0.014)), [float]($W * ($ex + 0.014)), [float]($W * ($ey + 0.014)))
  # nib triangle (ink colour)
  $nibPts = @(
    [System.Drawing.PointF]::new([float]($W * $tx), [float]($W * $ty)),
    [System.Drawing.PointF]::new([float]($W * ($bx + $nibW * 0.7071)), [float]($W * ($by + $nibW * 0.7071))),
    [System.Drawing.PointF]::new([float]($W * ($bx - $nibW * 0.7071)), [float]($W * ($by - $nibW * 0.7071)))
  )
  $nibBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(31, 58, 110))
  $g.FillPolygon($nibBrush, [System.Drawing.PointF[]]$nibPts)
  # body outline (keeps the white pen readable on white paper) + white body
  $edgePen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(160, 150, 180, 220)), ([float]($W * 0.118))
  $edgePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
  $edgePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
  $g.DrawLine($edgePen, [float]($W * $bx), [float]($W * $by), [float]($W * $ex), [float]($W * $ey))
  $bodyPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([float]($W * 0.098))
  $bodyPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
  $bodyPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
  $g.DrawLine($bodyPen, [float]($W * $bx), [float]($W * $by), [float]($W * $ex), [float]($W * $ey))
  $g.Dispose()

  # --- downscale ---
  $out = New-Object System.Drawing.Bitmap $size, $size
  $g2 = [System.Drawing.Graphics]::FromImage($out)
  $g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $g2.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
  $g2.DrawImage($bmp, 0, 0, $size, $size)
  $g2.Dispose()
  $bmp.Dispose()
  return $out
}

function Save-Png([System.Drawing.Bitmap]$bmp, [string]$path) {
  $dir = Split-Path -Parent $path
  if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
}

function Get-PngBytes([System.Drawing.Bitmap]$bmp) {
  $ms = New-Object System.IO.MemoryStream
  $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  $bytes = $ms.ToArray()
  $ms.Dispose()
  return $bytes
}

function Get-BmpBytes([System.Drawing.Bitmap]$bmp) {
  # ICO 内的 BMP 条目:40 字节 BITMAPINFOHEADER + BGRA 像素(自下而上) + AND 掩码
  $w = $bmp.Width; $h = $bmp.Height
  $maskRow = [int]([Math]::Floor(($w + 31) / 32) * 4)
  $maskSize = $maskRow * $h
  $ms = New-Object System.IO.MemoryStream
  $bw = New-Object System.IO.BinaryWriter($ms)
  $bw.Write([uint32]40); $bw.Write([int32]$w); $bw.Write([int32]($h * 2))
  $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]0)
  $bw.Write([uint32]($w * $h * 4)); $bw.Write([int32]0); $bw.Write([int32]0)
  $bw.Write([uint32]0); $bw.Write([uint32]0)
  for ($y = $h - 1; $y -ge 0; $y--) {
    for ($x = 0; $x -lt $w; $x++) {
      $c = $bmp.GetPixel($x, $y)
      $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
    }
  }
  $bw.Write([byte[]](New-Object byte[] $maskSize))
  $bw.Flush()
  $bytes = $ms.ToArray()
  $bw.Close(); $ms.Dispose()
  return $bytes
}

function Write-Ico([int[]]$sizes, [string]$path) {
  # 小尺寸用 BMP 条目(Windows/csc 的图标写入器要求),128 以上用 PNG 条目
  $images = @()
  foreach ($s in $sizes) {
    $bmp = New-Logo $s
    if ($s -le 64) { $images += ,@($s, (Get-BmpBytes $bmp), $false) }
    else { $images += ,@($s, (Get-PngBytes $bmp), $true) }
    $bmp.Dispose()
  }
  $ms = New-Object System.IO.MemoryStream
  $bw = New-Object System.IO.BinaryWriter($ms)
  $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$images.Count)
  $offset = 6 + 16 * $images.Count
  foreach ($img in $images) {
    $s = $img[0]; $data = $img[1]
    $b = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$b); $bw.Write([byte]$b)
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$data.Length); $bw.Write([uint32]$offset)
    $offset += $data.Length
  }
  foreach ($img in $images) { $bw.Write([byte[]]$img[1]) }
  $bw.Flush()
  [System.IO.File]::WriteAllBytes($path, $ms.ToArray())
  $bw.Close(); $ms.Dispose()
}

# 1) Windows multi-size ICO
Write-Ico @(16, 24, 32, 48, 64, 128, 256) (Join-Path $root 'Writing\app.ico')
Write-Host '[ok] Windows icon: Writing\app.ico (16/24/32/48/64/128/256)'

# 2) Android mipmaps
$androidIcons = @{ 'mdpi' = 48; 'hdpi' = 72; 'xhdpi' = 96; 'xxhdpi' = 144; 'xxxhdpi' = 192 }
foreach ($k in $androidIcons.Keys) {
  $bmp = New-Logo $androidIcons[$k]
  Save-Png $bmp (Join-Path $root "mobile\android\res\mipmap-$k\ic_launcher.png")
  $bmp.Dispose()
}
Write-Host '[ok] Android icons: 5 densities'

# 3) HarmonyOS
foreach ($p in 'AppScope\resources\base\media\app_icon.png',
               'entry\src\main\resources\base\media\icon.png',
               'entry\src\main\resources\base\media\startIcon.png') {
  $bmp = New-Logo 192
  Save-Png $bmp (Join-Path $root "mobile\harmony\$p")
  $bmp.Dispose()
}
Write-Host '[ok] HarmonyOS icons: app_icon / icon / startIcon'

# 4) preview + web favicon
$big = New-Logo 256
Save-Png $big (Join-Path $root 'mobile\icon-preview.png')
$big.Dispose()
$fav = New-Logo 48
[System.IO.File]::WriteAllText((Join-Path $root 'mobile\favicon.base64.txt'),
  [Convert]::ToBase64String((Get-PngBytes $fav)), (New-Object System.Text.UTF8Encoding $false))
$fav.Dispose()
Write-Host '[ok] preview + favicon base64'
Write-Host 'All icons generated'
