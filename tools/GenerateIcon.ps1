$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase

$repo = Split-Path -Parent $PSScriptRoot
$android = Join-Path $repo 'src/android/app/src/main/res/drawable/ic_launcher.xml'
$windows = Join-Path $repo 'src/windows/MagicCompanion.Agent/Assets/SupperScreen.ico'
[xml]$vector = Get-Content -Raw -LiteralPath $android
$ns = 'http://schemas.android.com/apk/res/android'
$width = [double]$vector.vector.GetAttribute('viewportWidth', $ns)
$height = [double]$vector.vector.GetAttribute('viewportHeight', $ns)

$frames = foreach ($size in 256,128,64,48,32,24,16) {
    $visual = [Windows.Media.DrawingVisual]::new()
    $drawing = $visual.RenderOpen()
    $drawing.PushTransform([Windows.Media.ScaleTransform]::new($size / $width, $size / $height))
    foreach ($path in $vector.vector.path) {
        $geometry = [Windows.Media.Geometry]::Parse($path.GetAttribute('pathData', $ns))
        $fillColor = $path.GetAttribute('fillColor', $ns)
        $fill = if ($fillColor.StartsWith('#')) {
            [Windows.Media.SolidColorBrush]::new([Windows.Media.ColorConverter]::ConvertFromString($fillColor))
        }
        $strokeColor = $path.GetAttribute('strokeColor', $ns)
        $pen = if ($strokeColor.StartsWith('#')) {
            $stroke = [Windows.Media.SolidColorBrush]::new([Windows.Media.ColorConverter]::ConvertFromString($strokeColor))
            $line = [Windows.Media.Pen]::new($stroke, [double]$path.GetAttribute('strokeWidth', $ns))
            $line.StartLineCap = $line.EndLineCap = $path.GetAttribute('strokeLineCap', $ns)
            $line.LineJoin = $path.GetAttribute('strokeLineJoin', $ns)
            $line
        }
        $drawing.DrawGeometry($fill, $pen, $geometry)
    }
    $drawing.Pop()
    $drawing.Close()
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.MemoryStream]::new()
    try {
        $encoder.Save($stream)
        @{ Size = $size; Png = $stream.ToArray() }
    }
    finally { $stream.Dispose() }
}

[IO.Directory]::CreateDirectory((Split-Path -Parent $windows)) | Out-Null
$writer = [IO.BinaryWriter]::new([IO.File]::Create($windows))
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = [byte]($frame.Size % 256)
        $writer.Write($dimension)
        $writer.Write($dimension)
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Png.Length)
        $writer.Write([uint32]$offset)
        $offset += $frame.Png.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Png) }
}
finally { $writer.Dispose() }
