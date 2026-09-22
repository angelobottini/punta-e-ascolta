# Third read-only in-memory benchmark (ASCII-only source; accents built from char codes).
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Runtime.WindowsRuntime
[void][Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime]
[void][Windows.Graphics.Imaging.SoftwareBitmap, Windows.Graphics, ContentType = WindowsRuntime]
[void][Windows.Globalization.Language, Windows.Globalization, ContentType = WindowsRuntime]

$asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
function ToTask($op, $resultType) { $asTaskGeneric.MakeGenericMethod($resultType).Invoke($null, @($op)) }
function Await($op, $resultType) { $t = ToTask $op $resultType; $t.Wait(-1) | Out-Null; $t.Result }

$engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage([Windows.Globalization.Language]::new('it-IT'))
$a = [string][char]0xE0; $e = [string][char]0xE8; $u = [string][char]0xF9; $i2 = [string][char]0xEC; $o = [string][char]0xF2

function To-SoftwareBitmap([System.Drawing.Bitmap]$bmp) {
    $rect = New-Object System.Drawing.Rectangle(0, 0, $bmp.Width, $bmp.Height)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bytes = New-Object byte[] ($data.Stride * $bmp.Height)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
    $bmp.UnlockBits($data)
    $buf = [System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions]::AsBuffer($bytes)
    [Windows.Graphics.Imaging.SoftwareBitmap]::CreateCopyFromBuffer($buf, [Windows.Graphics.Imaging.BitmapPixelFormat]::Bgra8, $bmp.Width, $bmp.Height, [Windows.Graphics.Imaging.BitmapAlphaMode]::Premultiplied)
}
function Scale-Bitmap([System.Drawing.Bitmap]$src, [double]$scale, [string]$interp, $cm) {
    $nw = [int]($src.Width * $scale); $nh = [int]($src.Height * $scale)
    $dst = New-Object System.Drawing.Bitmap($nw, $nh, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($dst)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::$interp
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $ia = New-Object System.Drawing.Imaging.ImageAttributes
    $ia.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
    if ($null -ne $cm) { $ia.SetColorMatrix($cm) }
    $g.DrawImage($src, (New-Object System.Drawing.Rectangle(0, 0, $nw, $nh)), 0, 0, $src.Width, $src.Height, [System.Drawing.GraphicsUnit]::Pixel, $ia)
    $g.Dispose(); $dst
}
function Ocr-Text([System.Drawing.Bitmap]$bmp) {
    $sb = To-SoftwareBitmap $bmp
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $res = Await ($engine.RecognizeAsync($sb)) ([Windows.Media.Ocr.OcrResult])
    $sw.Stop(); $sb.Dispose()
    [pscustomobject]@{ Res = $res; Ms = $sw.Elapsed.TotalMilliseconds }
}
function Norm([string]$s) { ($s.ToLowerInvariant() -replace '[^\p{L}\p{Nd}]', '') }
function LineAccuracy($res, [string[]]$truthLines) {
    $got = @(); foreach ($l in $res.Lines) { $got += (Norm $l.Text) }
    $hit = 0; $bad = @()
    foreach ($t in $truthLines) { if ($got -contains (Norm $t)) { $hit++ } else { $bad += $t } }
    [pscustomobject]@{ Pct = [math]::Round(100.0 * $hit / $truthLines.Count, 0); Bad = $bad }
}

$items = @('Nuovo...', 'Apri...', 'Salva con nome...', 'Esporta...', "Luminosit$a e contrasto", "Tonalit$a/Saturazione", "Pi$u opzioni", 'Layer Effects...', 'Blend Mode', 'Opacity: 100 %', 'Undo Brush Stroke', 'Preferenze', "Citt$a perch$e gi$a gi$u", 'Rasterize & Trim', 'Zoom', 'OK', 'Annulla', 'Pixel', 'Stile livello', 'Maschera di ritaglio')

function New-Menu([float]$px, [string]$hint, $bg, $fg, [int]$hlIndex, $hlBg, $hlFg, [string]$fontName) {
    $rowH = [int]($px * 2.0); $w = 300; $h = $rowH * $items.Count + 8
    $bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.Clear($bg)
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::$hint
    $font = New-Object System.Drawing.Font($fontName, $px, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    for ($i = 0; $i -lt $items.Count; $i++) {
        $y = 4 + $i * $rowH
        $c = $fg
        if ($i -eq $hlIndex) { $g.FillRectangle((New-Object System.Drawing.SolidBrush($hlBg)), 2, $y, $w - 4, $rowH); $c = $hlFg }
        $g.DrawString($items[$i], $font, (New-Object System.Drawing.SolidBrush($c)), 28, $y + [int]($px * 0.3))
    }
    $g.Dispose(); $font.Dispose(); $bmp
}
function C($r, $gg, $b) { [System.Drawing.Color]::FromArgb(255, $r, $gg, $b) }
function ContrastMatrix([double]$gain, [double]$offset, [bool]$invert) {
    $cm = New-Object System.Drawing.Imaging.ColorMatrix
    $k = $gain; $off = $offset
    if ($invert) { $k = -$gain; $off = 1.0 - $offset }
    # grayscale + gain: each output channel = k*(0.299R+0.587G+0.114B) + off
    $cm.Matrix00 = 0.299 * $k; $cm.Matrix01 = 0.299 * $k; $cm.Matrix02 = 0.299 * $k
    $cm.Matrix10 = 0.587 * $k; $cm.Matrix11 = 0.587 * $k; $cm.Matrix12 = 0.587 * $k
    $cm.Matrix20 = 0.114 * $k; $cm.Matrix21 = 0.114 * $k; $cm.Matrix22 = 0.114 * $k
    $cm.Matrix33 = 1; $cm.Matrix44 = 1
    $cm.Matrix40 = $off; $cm.Matrix41 = $off; $cm.Matrix42 = $off
    $cm
}

$warm = New-Menu 16 'ClearTypeGridFit' (C 255 255 255) (C 0 0 0) -1 (C 0 0 0) (C 0 0 0) 'Segoe UI'
$null = Ocr-Text $warm

"=== K) Menu column, 20 items, line-level accuracy after normalization (letters+digits only, case-insensitive) ==="
"{0,-4} {1,-17} {2,-22} {3,-6} {4,6} {5,6}  {6}" -f 'px', 'hint', 'theme', 'scale', 'line%', 'ms', 'wrong lines'
$themes = @(
  @{ n = 'dark+blue hover';     bg = (C 43 43 43);    fg = (C 214 214 214); hb = (C 10 100 216); hf = (C 255 255 255) },
  @{ n = 'light+blue hover';    bg = (C 249 249 249); fg = (C 26 26 26);    hb = (C 0 95 184);   hf = (C 255 255 255) }
)
foreach ($px in 9, 10, 11, 12, 14) {
  foreach ($hint in 'ClearTypeGridFit', 'AntiAliasGridFit') {
    foreach ($t in $themes) {
      $src = New-Menu $px $hint $t.bg $t.fg 4 $t.hb $t.hf 'Segoe UI'
      foreach ($s in 1.0, 1.5, 2.0, 2.5, 3.0, 4.0) {
        $interp = 'HighQualityBicubic'; if ($s -eq 1.0) { $interp = 'NearestNeighbor' }
        $img = Scale-Bitmap $src $s $interp $null
        $r = Ocr-Text $img; $la = LineAccuracy $r.Res $items
        "{0,-4} {1,-17} {2,-22} {3,-6} {4,6} {5,6:N0}  {6}" -f $px, $hint, $t.n, $s, $la.Pct, $r.Ms, (($la.Bad | Select-Object -First 4) -join ' | ')
        $img.Dispose()
      }
      $src.Dispose()
    }
  }
}

"=== L) Low-contrast (disabled items: #787878 on #2B2B2B), 12px grayscale AA: effect of grayscale+contrast stretch and inversion, scale 2.5 ==="
$src = New-Menu 12 'AntiAliasGridFit' (C 43 43 43) (C 120 120 120) -1 (C 0 0 0) (C 0 0 0) 'Segoe UI'
$variants = @(
  @{ n = 'none';                          cm = $null },
  @{ n = 'gray only';                     cm = (ContrastMatrix 1.0 0.0 $false) },
  @{ n = 'gray+stretch x3';               cm = (ContrastMatrix 3.0 -0.5 $false) },
  @{ n = 'gray+stretch x3 + invert';      cm = (ContrastMatrix 3.0 -0.5 $true) },
  @{ n = 'invert only';                   cm = (ContrastMatrix 1.0 0.0 $true) }
)
foreach ($v in $variants) {
  $img = Scale-Bitmap $src 2.5 'HighQualityBicubic' $v.cm
  $r = Ocr-Text $img; $la = LineAccuracy $r.Res $items
  "{0,-28} line%={1,4}  ms={2,5:N0}  wrong: {3}" -f $v.n, $la.Pct, $r.Ms, (($la.Bad | Select-Object -First 5) -join ' | ')
  $img.Dispose()
}
$src.Dispose()

"=== M) Raw output 12px dark grayscale AA scale 2.5 (check punctuation/accents) ==="
$src = New-Menu 12 'AntiAliasGridFit' (C 43 43 43) (C 214 214 214) 4 (C 10 100 216) (C 255 255 255) 'Segoe UI'
$img = Scale-Bitmap $src 2.5 'HighQualityBicubic' $null
$r = Ocr-Text $img
foreach ($l in $r.Res.Lines) { "  [" + $l.Text + "]" }

"=== N) Concurrency on the same engine (4 calls in flight) ==="
try {
  $sbs = @(); 1..4 | ForEach-Object { $sbs += (To-SoftwareBitmap $img) }
  $sw = [System.Diagnostics.Stopwatch]::StartNew()
  $ops = @(); foreach ($s in $sbs) { $ops += $engine.RecognizeAsync($s) }
  $tasks = @(); foreach ($op in $ops) { $tasks += (ToTask $op ([Windows.Media.Ocr.OcrResult])) }
  foreach ($t in $tasks) { $t.Wait(-1) | Out-Null }
  $sw.Stop()
  $n = 0; foreach ($t in $tasks) { $la = LineAccuracy $t.Result $items; $n++; "  call $n line%=" + $la.Pct }
  "  4 overlapping calls completed in " + [int]$sw.Elapsed.TotalMilliseconds + " ms (single call was " + [int]$r.Ms + " ms)"
} catch {
  $ex = $_.Exception; while ($ex.InnerException) { $ex = $ex.InnerException }
  "  FAILED: " + $ex.GetType().FullName + ": " + $ex.Message
}
