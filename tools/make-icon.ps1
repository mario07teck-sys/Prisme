# Génère l'identité visuelle de Prisme : icône multi-taille, logo, images de l'installeur.
# Dessin vectoriel WPF rendu séparément à chaque taille (net à 16 px comme à 256 px).
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$root = Split-Path $PSScriptRoot
$assets = Join-Path $root 'src\Assets'
$inst = Join-Path $root 'installer'
New-Item -ItemType Directory -Force $assets, $inst | Out-Null

function C($hex, $a = 255) { $c = [Windows.Media.ColorConverter]::ConvertFromString($hex); $c.A = $a; $c }
function B($hex, $a = 255) { $b = New-Object Windows.Media.SolidColorBrush (C $hex $a); $b.Freeze(); $b }
function P([double]$x, [double]$y) { New-Object Windows.Point $x, $y }
function Poly($pts) {
  $g = New-Object Windows.Media.StreamGeometry
  $ctx = $g.Open(); $ctx.BeginFigure($pts[0], $true, $true)
  $ctx.PolyLineTo([Windows.Point[]]$pts[1..($pts.Count - 1)], $true, $true); $ctx.Close(); $g
}
$Spectrum = '#FF3D6E', '#FF8A1F', '#FFD93D', '#3DDC97', '#3DA5FF', '#8B5CF6'

# Dessine le prisme dans un carré (ox, oy, s) ; $bg = fond arrondi
function Draw-Prism($dc, [double]$ox, [double]$oy, [double]$s, [bool]$bg) {
  $small = $s -lt 40
  if ($bg) {
    $grad = New-Object Windows.Media.LinearGradientBrush (C '#231C4A'), (C '#07070E'), (P 0 0), (P 1 1)
    $rect = New-Object Windows.Rect $ox, $oy, $s, $s
    $dc.DrawRoundedRectangle($grad, $null, $rect, $s * 0.23, $s * 0.23)
    $dc.PushClip((New-Object Windows.Media.RectangleGeometry $rect, ($s * 0.23), ($s * 0.23)))
    # halo coloré derrière l'arc-en-ciel
    $halo = New-Object Windows.Media.RadialGradientBrush (C '#8B5CF6' 90), (C '#8B5CF6' 0)
    $dc.DrawEllipse($halo, $null, (P ($ox + $s * 0.78) ($oy + $s * 0.62)), $s * 0.5, $s * 0.42)
  }
  $X = { param($v) $ox + $v * $s }; $Y = { param($v) $oy + $v * $s }
  # prisme taillé : 3 facettes en dégradé du spectre qui se rejoignent au cœur
  $T = P (& $X 0.5) (& $Y 0.16); $L = P (& $X 0.15) (& $Y 0.79); $R = P (& $X 0.85) (& $Y 0.79); $K = P (& $X 0.5) (& $Y 0.575)
  function G($c1, $c2, $p1, $p2) { $g = New-Object Windows.Media.LinearGradientBrush (C $c1), (C $c2), 0; $g.MappingMode = 'Absolute'; $g.StartPoint = $p1; $g.EndPoint = $p2; $g }
  $dc.DrawGeometry((G '#FF3D6E' '#FF9A3D' $T $L), $null, (Poly @($T, $L, $K)))
  $dc.DrawGeometry((G '#B565FF' '#3DA5FF' $T $R), $null, (Poly @($T, $K, $R)))
  $dc.DrawGeometry((G '#2EE6C5' '#3D7BFF' $L $R), $null, (Poly @($L, $R, $K)))
  # arêtes lumineuses
  $w = if ($small) { $s * 0.05 } else { $s * 0.018 }
  $edge = New-Object Windows.Media.Pen (B '#FFFFFF' $(if ($small) { 150 } else { 110 })), $w; $edge.LineJoin = 'Round'; $edge.StartLineCap = 'Round'; $edge.EndLineCap = 'Round'
  $dc.DrawGeometry($null, $edge, (Poly @($T, $L, $R)))
  if (-not $small) {
    foreach ($v in $T, $L, $R) { $dc.DrawLine($edge, $K, $v) }
    # reflet sur la facette gauche + éclat au cœur
    $shine = New-Object Windows.Media.Pen (B '#FFFFFF' 170), ($s * 0.022); $shine.StartLineCap = 'Round'; $shine.EndLineCap = 'Round'
    $dc.DrawLine($shine, (P (& $X 0.455) (& $Y 0.28)), (P (& $X 0.31) (& $Y 0.56)))
    $spark = New-Object Windows.Media.RadialGradientBrush (C '#FFFFFF' 230), (C '#FFFFFF' 0)
    $dc.DrawEllipse($spark, $null, $K, $s * 0.09, $s * 0.09)
  }
  if ($bg) { $dc.Pop() }
}

function Render([int]$w, [int]$h, [scriptblock]$draw) {
  $v = New-Object Windows.Media.DrawingVisual
  $dc = $v.RenderOpen(); & $draw $dc; $dc.Close()
  $bmp = New-Object Windows.Media.Imaging.RenderTargetBitmap $w, $h, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
  $bmp.Render($v); $bmp
}
function Save($bmp, $path, $enc = 'Png') {
  $e = New-Object "Windows.Media.Imaging.${enc}BitmapEncoder"
  $e.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bmp))
  $ms = New-Object IO.MemoryStream; $e.Save($ms)
  if ($path) { [IO.File]::WriteAllBytes($path, $ms.ToArray()) }
  , $ms.ToArray()
}

# --- icône .ico (entrées PNG) ---
$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
# ≤ 48 px : entrées DIB classiques (lues par tous les composants Windows) ; au-delà : PNG
function Dib($bmp) {
  $s = $bmp.PixelWidth; $cv = New-Object Windows.Media.Imaging.FormatConvertedBitmap $bmp, ([Windows.Media.PixelFormats]::Bgra32), $null, 0
  $px = New-Object byte[] ($s * $s * 4); $cv.CopyPixels($px, $s * 4, 0)
  $ms = New-Object IO.MemoryStream; $w = New-Object IO.BinaryWriter $ms
  $w.Write([uint32]40); $w.Write([int32]$s); $w.Write([int32]($s * 2)); $w.Write([uint16]1); $w.Write([uint16]32)
  $w.Write([uint32]0); $w.Write([uint32]($s * $s * 4)); $w.Write([int32]0); $w.Write([int32]0); $w.Write([uint32]0); $w.Write([uint32]0)
  for ($y = $s - 1; $y -ge 0; $y--) { $w.Write($px, $y * $s * 4, $s * 4) }
  $mask = [Math]::Ceiling($s / 32) * 4; $w.Write((New-Object byte[] ($mask * $s)))
  $w.Flush(); , $ms.ToArray()
}
$pngs = foreach ($sz in $sizes) {
  $r = Render $sz $sz { param($dc) Draw-Prism $dc 0 0 $sz $true }
  if ($sz -le 48) { , (Dib $r) } else { , (Save $r $null) }
}
$ms = New-Object IO.MemoryStream; $bw = New-Object IO.BinaryWriter $ms
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$off = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $d = $sizes[$i] % 256
  $bw.Write([byte]$d); $bw.Write([byte]$d); $bw.Write([byte]0); $bw.Write([byte]0)
  $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$off)
  $off += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write([byte[]]$p) }
$bw.Flush(); [IO.File]::WriteAllBytes((Join-Path $assets 'prisme.ico'), $ms.ToArray())

# --- logo 512 (fond transparent, pour l'interface) et aperçu 1024 ---
Save (Render 512 512 { param($dc) Draw-Prism $dc 0 0 512 $false }) (Join-Path $assets 'logo.png') | Out-Null
Save (Render 1024 1024 { param($dc) Draw-Prism $dc 0 0 1024 $true }) (Join-Path $root 'prisme-1024.png') | Out-Null

# --- images de l'installeur (BMP, 100 % et 200 %) ---
foreach ($k in 1, 2) {
  $w = 164 * $k; $h = 314 * $k
  $bmp = Render $w $h { param($dc)
    $g = New-Object Windows.Media.LinearGradientBrush (C '#1E1840'), (C '#06060C'), (P 0 0), (P 0.4 1)
    $dc.DrawRectangle($g, $null, (New-Object Windows.Rect 0, 0, $w, $h))
    Draw-Prism $dc ($w * 0.14) ($h * 0.22) ($w * 0.72) $false
    $tf = New-Object Windows.Media.Typeface 'Segoe UI Variable Display Semibold'
    $ft = New-Object Windows.Media.FormattedText 'Prisme', ([Globalization.CultureInfo]::InvariantCulture), 'LeftToRight', $tf, (26 * $k), (B '#FFFFFF'), 1.0
    $dc.DrawText($ft, (P (($w - $ft.Width) / 2) ($h * 0.66)))
    $ft2 = New-Object Windows.Media.FormattedText 'Convertir. Télécharger.', ([Globalization.CultureInfo]::InvariantCulture), 'LeftToRight', $tf, (10.5 * $k), (B '#B8B4D8'), 1.0
    $dc.DrawText($ft2, (P (($w - $ft2.Width) / 2) ($h * 0.66 + 36 * $k)))
  }
  Save $bmp (Join-Path $inst "wizard-$k.bmp") 'Bmp' | Out-Null
  $sz = 55 * $k
  Save (Render $sz $sz { param($dc) $dc.DrawRectangle((B '#FFFFFF'), $null, (New-Object Windows.Rect 0, 0, $sz, $sz)); Draw-Prism $dc 0 0 $sz $true }) (Join-Path $inst "small-$k.bmp") 'Bmp' | Out-Null
}
'Identité générée.'
