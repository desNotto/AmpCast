# Renders the vector logo (src/AmpCast/Themes/Logo.xaml) to AmpCast.ico and PNG previews.
# Run with Windows PowerShell in STA mode:
#   powershell -STA -ExecutionPolicy Bypass -File tools\make-icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$root     = Split-Path -Parent $PSScriptRoot
$xamlPath = Join-Path $root 'src\AmpCast\Themes\Logo.xaml'
$icoPath  = Join-Path $root 'src\AmpCast\Assets\AmpCast.ico'
$assets   = Join-Path $root 'assets'
New-Item -ItemType Directory -Force (Split-Path $icoPath) | Out-Null
New-Item -ItemType Directory -Force $assets | Out-Null

$dict = [Windows.Markup.XamlReader]::Parse([IO.File]::ReadAllText($xamlPath))

function Render-Png([System.Windows.Media.ImageSource] $image, [int] $size) {
    $visual = New-Object System.Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    $dc.DrawImage($image, (New-Object System.Windows.Rect 0, 0, $size, $size))
    $dc.Close()
    $bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($visual)
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = New-Object IO.MemoryStream
    $enc.Save($ms)
    return ,$ms.ToArray()
}

$icon  = $dict['LogoIconImage']
$mark  = $dict['LogoMarkImage']
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs  = foreach ($s in $sizes) { ,(Render-Png $icon $s) }

# ICO container with PNG-compressed frames (supported since Windows Vista).
$out = New-Object IO.MemoryStream
$w = New-Object IO.BinaryWriter $out
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$len); $w.Write([UInt32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Flush()
[IO.File]::WriteAllBytes($icoPath, $out.ToArray())

[IO.File]::WriteAllBytes((Join-Path $assets 'ampcast-icon-512.png'), (Render-Png $icon 512))
[IO.File]::WriteAllBytes((Join-Path $assets 'ampcast-mark-512.png'), (Render-Png $mark 512))
"Written: $icoPath"
