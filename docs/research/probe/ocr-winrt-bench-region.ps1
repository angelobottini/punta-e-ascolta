# Read-only in-memory benchmark of Windows.Media.Ocr on synthetic UI-like text.
# No GUI, no files written, nothing installed.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Runtime.WindowsRuntime
[void][Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime]
[void][Windows.Graphics.Imaging.SoftwareBitmap, Windows.Graphics, ContentType = WindowsRuntime]
[void][Windows.Security.Cryptography.CryptographicBuffer, Windows.Security, ContentType = WindowsRuntime]
[void][Windows.Globalization.Language, Windows.Globalization, ContentType = WindowsRuntime]

$asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
function Await($op, $resultType) {
    $t = $asTaskGeneric.MakeGenericMethod($resultType).Invoke($null, @($op))
    $t.Wait(-1) | Out-Null
    $t.Result
}

$engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage([Windows.Globalization.Language]::new('it-IT'))
"Engine language: " + $engine.RecognizerLanguage.LanguageTag

$lines = @(
  'File Modifica Testo Documento Livello Seleziona Disponi Filtri Visualizza Finestra Guida',
  'Nuovo documento Apri Salva con nome Esporta Stampa Chiudi Annulla Ripeti',
  'Layer Effects Blend Mode Opacity Brush Tool Gradient Crop Export Persona',
  'Settings Preferences Undo Redo Copy Paste Duplicate Delete Rename Group',
  'Regolazione Luminosita Contrasto Saturazione Tonalita Curve Livelli Maschera',
  'Pixel Layer Adjustment Live Filter Snapshot History Navigator Channels',
  'Dimensioni documento Ridimensiona tela Ruota orario Capovolgi orizzontale',
  'Zoom Fit Width Height Resolution Anchor Resample Bilinear Bicubic Lanczos',
  'Inserisci Formule Dati Revisione Layout di pagina Sviluppo Componenti',
  'Font Size Bold Italic Underline Align Left Center Right Justify Spacing'
)
$truth = ($lines -join ' ') -split ' '

function New-TextBitmap([int]$w, [int]$h, [float]$px, [bool]$dark, [string]$hint, [string[]]$text, [int]$lineStep) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    if ($dark) { $bg = [System.Drawing.Color]::FromArgb(255, 43, 43, 43); $fg = [System.Drawing.Color]::FromArgb(255, 214, 214, 214) }
    else       { $bg = [System.Drawing.Color]::FromArgb(255, 243, 243, 243); $fg = [System.Drawing.Color]::FromArgb(255, 26, 26, 26) }
    $g.Clear($bg)
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::$hint
    $font = New-Object System.Drawing.Font('Segoe UI', $px, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $brush = New-Object System.Drawing.SolidBrush($fg)
    $y = 10
    foreach ($l in $text) { $g.DrawString($l, $font, $brush, 12, $y); $y += $lineStep }
    $g.Dispose(); $font.Dispose(); $brush.Dispose()
    $bmp
}

function Convert-Bitmap([System.Drawing.Bitmap]$src, [double]$scale, [string]$interp, [bool]$invert, [int]$pad) {
    $nw = [int]($src.Width * $scale) + 2 * $pad; $nh = [int]($src.Height * $scale) + 2 * $pad
    $dst = New-Object System.Drawing.Bitmap($nw, $nh, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($dst)
    $g.Clear($src.GetPixel(0, 0))
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::$interp
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $ia = New-Object System.Drawing.Imaging.ImageAttributes
    $ia.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
    $rect = New-Object System.Drawing.Rectangle($pad, $pad, ($nw - 2 * $pad), ($nh - 2 * $pad))
    $g.DrawImage($src, $rect, 0, 0, $src.Width, $src.Height, [System.Drawing.GraphicsUnit]::Pixel, $ia)
    $g.Dispose()
    if ($invert) {
        $inv = New-Object System.Drawing.Bitmap($nw, $nh, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g2 = [System.Drawing.Graphics]::FromImage($inv)
        $cm = New-Object System.Drawing.Imaging.ColorMatrix
        $cm.Matrix00 = -1; $cm.Matrix11 = -1; $cm.Matrix22 = -1; $cm.Matrix33 = 1; $cm.Matrix44 = 1
        $cm.Matrix40 = 1; $cm.Matrix41 = 1; $cm.Matrix42 = 1
        $ia2 = New-Object System.Drawing.Imaging.ImageAttributes
        $ia2.SetColorMatrix($cm)
        $g2.DrawImage($dst, (New-Object System.Drawing.Rectangle(0, 0, $nw, $nh)), 0, 0, $nw, $nh, [System.Drawing.GraphicsUnit]::Pixel, $ia2)
        $g2.Dispose(); $dst.Dispose(); $dst = $inv
    }
    $dst
}

function Invoke-Ocr([System.Drawing.Bitmap]$bmp) {
    $rect = New-Object System.Drawing.Rectangle(0, 0, $bmp.Width, $bmp.Height)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bytes = New-Object byte[] ($data.Stride * $bmp.Height)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
    $bmp.UnlockBits($data)
    $buf = [System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions]::AsBuffer($bytes)
    $sb = [Windows.Graphics.Imaging.SoftwareBitmap]::CreateCopyFromBuffer($buf, [Windows.Graphics.Imaging.BitmapPixelFormat]::Bgra8, $bmp.Width, $bmp.Height, [Windows.Graphics.Imaging.BitmapAlphaMode]::Premultiplied)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $res = Await ($engine.RecognizeAsync($sb)) ([Windows.Media.Ocr.OcrResult])
    $sw.Stop()
    $sb.Dispose()
    [pscustomobject]@{ Result = $res; Ms = $sw.Elapsed.TotalMilliseconds }
}

function Get-Accuracy($res, [string[]]$truthWords) {
    $found = New-Object System.Collections.Generic.List[string]
    foreach ($l in $res.Lines) { foreach ($w in $l.Words) { $found.Add($w.Text) } }
    $hit = 0
    foreach ($t in $truthWords) { $i = $found.IndexOf($t); if ($i -ge 0) { $hit++; $found.RemoveAt($i) } }
    [math]::Round(100.0 * $hit / $truthWords.Count, 1)
}

# warm-up
$warm = New-TextBitmap 400 100 20 $false 'ClearTypeGridFit' @('warm up') 30
$null = Invoke-Ocr $warm; $warm.Dispose()

"=== A) Full region 1000x400, 10 lines, Segoe UI; word accuracy % / OCR ms (median of 3) ==="
"{0,-5} {1,-6} {2,-18} {3,-6} {4,-20} {5,-6} {6,8} {7,8}" -f 'px', 'theme', 'hint', 'scale', 'interp', 'invert', 'acc%', 'ms'
foreach ($px in 11, 12, 13) {
  foreach ($dark in $true, $false) {
    foreach ($hint in 'ClearTypeGridFit', 'AntiAliasGridFit') {
      $src = New-TextBitmap 1000 400 $px $dark $hint $lines 36
      $configs = @(
        @{ s = 1.0; i = 'NearestNeighbor'; inv = $false },
        @{ s = 2.0; i = 'HighQualityBicubic'; inv = $false },
        @{ s = 3.0; i = 'HighQualityBicubic'; inv = $false }
      )
      if ($dark) {
        $configs += @{ s = 1.0; i = 'NearestNeighbor'; inv = $true }
        $configs += @{ s = 2.0; i = 'HighQualityBicubic'; inv = $true }
        $configs += @{ s = 2.0; i = 'HighQualityBilinear'; inv = $true }
        $configs += @{ s = 2.0; i = 'NearestNeighbor'; inv = $true }
        $configs += @{ s = 2.5; i = 'HighQualityBicubic'; inv = $true }
        $configs += @{ s = 3.0; i = 'HighQualityBicubic'; inv = $true }
      } else {
        $configs += @{ s = 2.0; i = 'HighQualityBilinear'; inv = $false }
        $configs += @{ s = 2.0; i = 'NearestNeighbor'; inv = $false }
      }
      foreach ($c in $configs) {
        $img = Convert-Bitmap $src $c.s $c.i $c.inv 0
        $times = @(); $acc = 0
        for ($k = 0; $k -lt 3; $k++) { $r = Invoke-Ocr $img; $times += $r.Ms; $acc = Get-Accuracy $r.Result $truth }
        $med = ($times | Sort-Object)[1]
        $themeName = 'light'; if ($dark) { $themeName = 'dark' }
        "{0,-5} {1,-6} {2,-18} {3,-6} {4,-20} {5,-6} {6,8} {7,8:N0}" -f $px, $themeName, $hint, $c.s, $c.i, $c.inv, $acc, $med
        $img.Dispose()
      }
      $src.Dispose()
    }
  }
}

"=== B) Single short label (tight crop), 12px, dark theme, grayscale AA ==="
foreach ($word in 'Esporta', 'Opacity', 'OK', 'Livello', 'Blend Mode', 'Annulla') {
  $src = New-TextBitmap 110 22 12 $true 'AntiAliasGridFit' @($word) 30
  # re-render tight: text drawn at y=10 would be clipped in 22px, so draw at y=2
  $src.Dispose()
  $src = New-Object System.Drawing.Bitmap(110, 22, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($src); $g.Clear([System.Drawing.Color]::FromArgb(255, 43, 43, 43))
  $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
  $f = New-Object System.Drawing.Font('Segoe UI', 12, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
  $b = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 214, 214, 214))
  $g.DrawString($word, $f, $b, 2, 2); $g.Dispose(); $f.Dispose(); $b.Dispose()
  foreach ($c in @(
      @{ s = 1.0; inv = $false; pad = 0 }, @{ s = 1.0; inv = $true; pad = 0 }, @{ s = 1.0; inv = $true; pad = 32 },
      @{ s = 3.0; inv = $false; pad = 0 }, @{ s = 3.0; inv = $true; pad = 0 }, @{ s = 3.0; inv = $true; pad = 32 },
      @{ s = 4.0; inv = $true; pad = 32 })) {
    $interp = 'HighQualityBicubic'; if ($c.s -eq 1.0) { $interp = 'NearestNeighbor' }
    $img = Convert-Bitmap $src $c.s $interp $c.inv $c.pad
    $r = Invoke-Ocr $img
    "{0,-12} scale={1,-4} invert={2,-6} pad={3,-3} size={4}x{5} -> '{6}'  ({7:N0} ms)" -f $word, $c.s, $c.inv, $c.pad, $img.Width, $img.Height, $r.Result.Text, $r.Ms
    $img.Dispose()
  }
  $src.Dispose()
}

"=== C) Sample raw output, 12px dark grayscale AA, scale 2.5 inverted ==="
$src = New-TextBitmap 1000 400 12 $true 'AntiAliasGridFit' $lines 36
$img = Convert-Bitmap $src 2.5 'HighQualityBicubic' $true 0
$r = Invoke-Ocr $img
"TextAngle=" + $r.Result.TextAngle + "  lines=" + $r.Result.Lines.Count + "  ms=" + [int]$r.Ms
foreach ($l in $r.Result.Lines) { "  " + $l.Text }
$w0 = $r.Result.Lines[0].Words[0]
"First word rect: " + $w0.Text + " -> X=" + $w0.BoundingRect.X + " Y=" + $w0.BoundingRect.Y + " W=" + $w0.BoundingRect.Width + " H=" + $w0.BoundingRect.Height
$img.Dispose(); $src.Dispose()

"=== D) Engine reuse / creation cost ==="
$sw = [System.Diagnostics.Stopwatch]::StartNew()
for ($i = 0; $i -lt 20; $i++) { $e2 = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage([Windows.Globalization.Language]::new('it-IT')) }
$sw.Stop(); "TryCreateFromLanguage x20: {0:N1} ms total" -f $sw.Elapsed.TotalMilliseconds
$en = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage([Windows.Globalization.Language]::new('en-US'))
"TryCreateFromLanguage('en-US') is null: " + ($null -eq $en)
"IsLanguageSupported en-US: " + [Windows.Media.Ocr.OcrEngine]::IsLanguageSupported([Windows.Globalization.Language]::new('en-US'))
"IsLanguageSupported it: " + [Windows.Media.Ocr.OcrEngine]::IsLanguageSupported([Windows.Globalization.Language]::new('it'))
