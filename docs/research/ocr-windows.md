# OCR su Windows per "Punta e Ascolta": Windows.Media.Ocr da .NET 10 (e valutazione di TextRecognizer / Windows AI APIs)

Data della ricerca: 21 settembre 2026. Macchina di riferimento: ASUS Zenbook, Snapdragon X (ARM64), Windows 11 build 10.0.26220, lingua di sistema it-IT.

Metodo: documentazione ufficiale Microsoft (Learn, NuGet, GitHub) + codice sorgente di progetti che usano lo stesso motore (PowerToys Text Extractor, Text-Grab, NVDA) + **misure reali eseguite oggi sulla macchina di sviluppo** chiamando `Windows.Media.Ocr` da Windows PowerShell 5.1 (processo ARM64, nessuna installazione, nessuna GUI, immagini sintetiche generate in memoria). Gli script sono salvati in `docs/research/probe/ocr-winrt-bench-region.ps1` e `docs/research/probe/ocr-winrt-bench-menu.ps1` e sono ripetibili. Dove utile cito anche i file `affinity-ocr-variants.txt` e `affinity-ocr-geometry.txt` presenti nella stessa cartella `probe` (prodotti oggi da un altro task di ricerca su screenshot veri di Affinity): li uso come dati, non li ho generati io.

Legenda affidabilita: **[DOC]** = scritto nella documentazione ufficiale; **[MISURA]** = misurato oggi su questa macchina; **[SORGENTE]** = letto nel codice di un progetto open source; **[INFERENZA]** = deduzione mia, da verificare.

---

## Raccomandazione

1. **Usare `Windows.Media.Ocr.OcrEngine` come motore OCR principale e sempre presente.** Funziona da app non impacchettata, portabile, self-contained, su x64 e ARM64, senza pacchetti NuGet aggiuntivi: basta il TFM `net10.0-windows10.0.19041.0` (o successivo). Nessuna identita di pacchetto, nessun modello da distribuire, nessuna rete.
2. **Creare il motore in modo esplicito con `OcrEngine.TryCreateFromLanguage(new Language("it-IT"))`**, con ripiego su `TryCreateFromUserProfileLanguages()` e poi sulla prima lingua di `AvailableRecognizerLanguages`. Sulla macchina di sviluppo e installato solo `it-IT` e **il riconoscitore italiano legge correttamente anche le parole inglesi dell'interfaccia** (100% su 49 parole inglesi di UI a scala 2x, vedi misure): **non serve far installare il pacchetto OCR inglese**. Lasciare comunque la lingua OCR configurabile nelle impostazioni.
3. **Preprocessing obbligatorio prima dell'OCR**, in un solo passaggio GDI+:
   - **upscaling adattivo con `InterpolationMode.HighQualityBicubic`**, fattore `clamp(2.5 * 96 / dpiMonitor, 1.0, 4.0)` (quindi 2.5x al 100%, circa 1.7x al 150%, 1.25x al 200%); mai `NearestNeighbor` (peggiora il risultato);
   - **padding** di almeno 16-32 px (dopo lo scaling) riempito con il colore di sfondo, e dimensione minima dell'immagine di circa 150x100 px: i ritagli piccoli (es. 110x22) restituiscono **testo vuoto**;
   - **scala di grigi + stiramento del contrasto (auto-levels)**: non cambia nulla sul testo normale, ma porta dal 90% al 100% il testo a basso contrasto (voci disabilitate grigio su grigio scuro);
   - **l'inversione dei temi scuri NON serve**: risultati identici, carattere per carattere, con e senza inversione, sia sul sintetico sia sugli screenshot reali di Affinity. Tenerla solo come opzione diagnostica, spenta.
4. **Un solo `OcrEngine` riusato per tutta la vita dell'app, protetto da `SemaphoreSlim(1,1)`**: la classe e agile e chiamabile da qualunque thread, ma **due `RecognizeAsync` sovrapposte sulla stessa istanza falliscono** con "Another RecognizeAsync operation is already running!" **[MISURA]**. Creare il motore costa circa 0,5 ms, quindi in alternativa si puo creare un'istanza per richiesta.
5. **Leggere sempre `OcrEngine.MaxImageDimension` a runtime** e limitare lo scaling di conseguenza: su questa build vale **10000** **[MISURA]**, le fonti storiche riportano 2600; superarlo genera un'eccezione.
6. **Non esistono punteggi di confidenza** in `Windows.Media.Ocr`. Compensare con euristiche (filtrare token fatti solo di simboli, doppio passaggio a scala diversa quando il risultato e vuoto o sospetto, dizionario delle etichette gia lette in cache).
7. **Limite pratico: caratteri sotto circa 11 px di corpo (a 100% DPI) restano inaffidabili anche con upscaling** (9 px: massimo 50% delle righe corrette; 10 px: massimo 75%). Sugli schermi HiDPI reali il problema e molto attenuato (nel menu vero di Affinity sullo Zenbook: 26/26 etichette corrette gia a 1x). Se in campo emergono UI con testo piu piccolo, il secondo motore deve essere l'OCR ONNX gia previsto nello stack, non TextRecognizer.
8. **`Microsoft.Windows.AI.Imaging.TextRecognizer` (Windows AI APIs, Windows App SDK): NON adottarlo nella v1.** Gira solo su Copilot+ PC con NPU, **richiede identita di pacchetto** (per un'app portabile: pacchetto sparse/"external location" firmato, certificato fidato, registrazione legata al percorso assoluto della cartella), la documentazione avverte che le app self-contained non funzionano da cartelle sotto `C:\Users`, ed esiste una segnalazione aperta nel 2026 di fallimento su x64 con external location. Prevedere pero fin da subito l'astrazione `IOcrEngine` nel core, cosi da poterlo aggiungere come percorso opzionale (utile soprattutto per il testo dentro le foto: cartelli, magliette) dopo uno spike a tempo limitato sullo Zenbook, che ha gia il modello NPU installato (`WindowsWorkload.TextRecognition.Qnn.1`).

---

## Dettagli tecnici

### 1. Progetto, TargetFramework e CsWinRT

- Da .NET 6 in poi le API WinRT si usano specificando un TFM con versione del sistema operativo; questo aggiunge in automatico il riferimento al targeting pack `Microsoft.Windows.SDK.NET.Ref` (proiezione generata da C#/WinRT). Non serve alcun `PackageReference`. La pagina Learn aggiornata il 3 luglio 2026 usa proprio .NET 10 negli esempi: `net10.0-windows10.0.19041.0`, `...22000.0`, `...22621.0`, `...26100.0`. **[DOC]**
- `Windows.Media.Ocr` esiste dalla 10.0.10240 (UniversalApiContract v1), quindi **qualsiasi** TFM Windows elencato basta. Consiglio `net10.0-windows10.0.19041.0` per il solo OCR; se si vuole tenere aperta la porta a Windows App SDK / Windows AI APIs, usare `net10.0-windows10.0.22621.0` (e il valore usato dagli esempi Microsoft per le AI API). La versione nel TFM seleziona solo le API visibili a compile time, non il sistema minimo a runtime (`SupportedOSPlatformVersion`). **[DOC]**
- Versioni correnti: `Microsoft.Windows.SDK.NET.Ref` **10.0.26100.87** (23 luglio 2026) su NuGet; la nota di rilascio di CsWinRT 2.3.1 indica i pacchetti `10.0.xxxxx.86` per .NET 8 e `10.0.xxxxx.87` per .NET 9/10. **CsWinRT 3.0 e ancora in preview** (pacchetti `10.0.xxxxx.85-preview`, richiede .NET 10.0.5+, cambia la forma di alcune API: array come `ReadOnlySpan<T>`, ecc.): non usarlo ora. Se serve fissare la proiezione: proprieta MSBuild `WindowsSdkPackageVersion`. **[DOC]**
- Il layer Windows e l'unico progetto con TFM `-windows`; il core resta `net10.0` puro (vedi sezione 10). In self-contained la proiezione aggiunge `Microsoft.Windows.SDK.NET.dll` e `WinRT.Runtime.dll` (ordine di grandezza: 25 MB, da verificare alla prima `dotnet publish`). **[INFERENZA]**
- ARM64: le chiamate WinRT funzionano da processo ARM64 nativo (il mio test gira in PowerShell ARM64). Pubblicare due cartelle: `win-arm64` e `win-x64`.

```xml
<!-- src/PuntaEAscolta.Windows/PuntaEAscolta.Windows.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
    <SupportedOSPlatformVersion>10.0.22000.0</SupportedOSPlatformVersion> <!-- Windows 11 only -->
    <RuntimeIdentifiers>win-x64;win-arm64</RuntimeIdentifiers>
    <UseWPF>true</UseWPF>
    <Nullable>enable</Nullable>
    <!-- Optional: pin the Windows SDK projection (CsWinRT 2.3.x line for .NET 9/10 ends with .87) -->
    <!-- <WindowsSdkPackageVersion>10.0.19041.87</WindowsSdkPackageVersion> -->
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\PuntaEAscolta.Core\PuntaEAscolta.Core.csproj" />
  </ItemGroup>
</Project>
```

Attenzione DPI: il processo deve essere **PerMonitorV2 DPI-aware** (app.manifest), altrimenti la cattura dello schermo arriva gia riscalata e sfocata da Windows e l'OCR peggiora sensibilmente.

### 2. Superficie API e struttura del risultato

`OcrEngine` **[DOC]**: attributi `MarshalingBehavior(Agile)` e `Threading(Both)`; membri:

| Membro | Note |
| --- | --- |
| `static IReadOnlyList<Language> AvailableRecognizerLanguages` | lingue OCR installate sul dispositivo |
| `static bool IsLanguageSupported(Language)` | vero se la lingua si risolve in una lingua OCR installata (risoluzione BCP-47: `it`, `it-CH` risultano supportate con il solo `it-IT` installato **[MISURA]**) |
| `static uint MaxImageDimension` | lato massimo in pixel; la documentazione non indica il valore |
| `static OcrEngine? TryCreateFromLanguage(Language)` | `null` se la lingua non e risolvibile |
| `static OcrEngine? TryCreateFromUserProfileLanguages()` | prima lingua di `GlobalizationPreferences.Languages` che ha un OCR installato, altrimenti `null` |
| `Language RecognizerLanguage` | lingua effettiva del motore |
| `IAsyncOperation<OcrResult> RecognizeAsync(SoftwareBitmap)` | unica operazione di riconoscimento |

`OcrResult`: `Lines` (lista di `OcrLine`), `Text` (tutto il testo), `TextAngle` (`double?`, rotazione oraria in gradi del testo attorno al centro dell'immagine; su UI a schermo vale 0 **[MISURA]**). `OcrLine`: `Text`, `Words`. `OcrWord`: `Text`, `BoundingRect` (`Windows.Foundation.Rect`, in pixel dell'immagine passata, quindi **da dividere per il fattore di scala e da correggere del padding**). **Non esiste alcuna proprieta di confidenza**, ne per parola ne per riga. La riga non ha un proprio rettangolo: si ottiene come unione dei rettangoli delle parole (cosi fa PowerToys **[SORGENTE]**).

Comportamento osservato sulle righe **[MISURA]**: parole separate da spazi normali sulla stessa linea di base finiscono nella **stessa `OcrLine`** (una barra dei menu "File Modifica Testo ..." e UNA riga sola); elementi separati da un vuoto ampio (etichetta del menu e scorciatoia a destra) vengono restituiti come **righe distinte** (nel probe su Affinity: 0 casi di etichetta+scorciatoia fuse su 26).

Con testo ruotato (foto) i rettangoli sono espressi nel sistema di coordinate dell'immagine raddrizzata di `TextAngle` (l'esempio ufficiale UWP applica una `RotateTransform` all'overlay): per l'uso "parla cio che c'e sotto il puntatore" conviene, se `TextAngle` e diverso da 0, ruotare il punto del puntatore dello stesso angolo prima dell'hit-test. **[INFERENZA, da verificare]**

### 3. Lingue: rilevamento, creazione del motore, cosa installare

Stato della macchina di sviluppo **[MISURA]**:

```
OcrEngine.AvailableRecognizerLanguages  -> it-IT (Italiano (Italia))
GlobalizationPreferences.Languages      -> it-IT
TryCreateFromUserProfileLanguages()     -> it-IT
TryCreateFromLanguage("en-US")          -> null        IsLanguageSupported("en-US") -> False
C:\Windows\OCR\it-it\MsOcrRes.orp       -> 228.848 byte
```

Il file di risorse per lingua pesa solo 229 KB: il riconoscitore dei caratteri latini sta nel componente di sistema ed e condiviso, il pacchetto lingua porta essenzialmente alfabeto e modello linguistico **[INFERENZA]**. Coerente con le misure: il motore it-IT ha letto senza errori "Layer Effects", "Blend Mode", "Opacity", "Brush Tool", "Undo", "Rename", "Bicubic", "Lanczos", "Justify", ecc. L'effetto del modello linguistico si vede invece qui: avendo io disegnato per errore "perchè", il motore ha restituito **"perché"** (ha corretto verso l'italiano giusto). Rischio speculare: una parola inglese molto simile a una italiana puo essere "italianizzata"; nei test non e successo.

`TryCreateFromLanguage` contro `TryCreateFromUserProfileLanguages`: il secondo dipende dall'ordine delle lingue del profilo utente e quindi non e deterministico fra PC diversi; per un ausilio e meglio il primo con lingua esplicita e catena di ripiego.

```csharp
using Windows.Globalization;
using Windows.Media.Ocr;

public static class OcrLanguages
{
    public static IReadOnlyList<string> Installed() =>
        OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToList();

    public static OcrEngine? CreateEngine(string? preferredTag = "it-IT")
    {
        OcrEngine? e = null;
        if (!string.IsNullOrWhiteSpace(preferredTag) && Language.IsWellFormed(preferredTag))
            e = OcrEngine.TryCreateFromLanguage(new Language(preferredTag));   // null if not installed
        e ??= OcrEngine.TryCreateFromUserProfileLanguages();
        if (e is null && OcrEngine.AvailableRecognizerLanguages.FirstOrDefault() is { } any)
            e = OcrEngine.TryCreateFromLanguage(any);
        return e;   // null => no OCR language at all: tell the helper (spoken message + settings window)
    }
}
```

Se non c'e nessuna lingua OCR (raro: il componente OCR arriva con la lingua di visualizzazione) l'aiutante deve installarla. Due strade **[DOC, pagina PowerToys Text Extractor]**:

- Impostazioni > Data/ora e lingua > Lingua e area geografica > Aggiungi una lingua (aggiunge pero anche tastiera e preferenze);
- PowerShell **come amministratore** (solo la funzionalita OCR, nessuna tastiera):

```powershell
Get-WindowsCapability -Online | Where-Object { $_.Name -like 'Language.OCR*' }      # elenco e stato
Add-WindowsCapability -Online -Name 'Language.OCR~~~it-IT~0.0.1.0'                 # oppure en-US, ecc.
```

L'app non deve tentare l'installazione da sola (richiede elevazione e modifica il sistema): deve solo rilevare e spiegare. Nota PowerToys: se il disco di sistema non e `C:`, la cartella `X:\Windows\OCR` puo non essere trovata.

### 4. Da cattura BGRA (byte[] / System.Drawing) a SoftwareBitmap

Evitare il giro "salva BMP in `MemoryStream` -> `BitmapDecoder` -> `GetSoftwareBitmapAsync`" usato da PowerToys e Text-Grab **[SORGENTE]**: e asincrono, alloca e ricodifica. Con i byte BGRA gia in mano basta **una copia**:

```csharp
using System.Runtime.InteropServices.WindowsRuntime;   // byte[].AsBuffer(...)
using Windows.Graphics.Imaging;

// pixels: BGRA32 top-down, tightly packed (stride == width * 4), length >= width*height*4
static SoftwareBitmap ToSoftwareBitmap(byte[] pixels, int width, int height) =>
    SoftwareBitmap.CreateCopyFromBuffer(
        pixels.AsBuffer(0, width * height * 4),      // no copy here: IBuffer view over the array
        BitmapPixelFormat.Bgra8, width, height,
        BitmapAlphaMode.Ignore);                     // BitBlt captures often have alpha = 0
```

Da `System.Drawing.Bitmap` (dopo il preprocessing GDI+):

```csharp
static unsafe byte[] CopyBgra(Bitmap bmp, ArrayPool<byte> pool, out int length)
{
    var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
    BitmapData d = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb); // memory order = B,G,R,A
    try
    {
        length = bmp.Width * bmp.Height * 4;          // 32bpp => stride == width*4, no row padding
        byte[] buf = pool.Rent(length);
        Marshal.Copy(d.Scan0, buf, 0, length);
        return buf;
    }
    finally { bmp.UnlockBits(d); }
}
```

Note:
- `Format32bppArgb` di GDI+ in memoria e B,G,R,A, cioe esattamente `BitmapPixelFormat.Bgra8`. Nessuna conversione di canali.
- Formati accettati da `RecognizeAsync` **[MISURA]**: `Bgra8`, `Rgba8` e `Gray8` funzionano tutti e danno lo stesso risultato. Passare `Gray8` non ha reso l'OCR piu veloce (73 ms contro 70 ms), quindi non vale la pena convertire apposta.
- Per 1000x400 la copia e di 1,6 MB: trascurabile (ben sotto 1 ms). L'accesso zero-copy via `IMemoryBufferByteAccess` con CsWinRT e scomodo e poco documentato (issue CsWinRT #646, #1214): non serve.
- Per ridurre le allocazioni si puo tenere una `SoftwareBitmap` riusabile e chiamare `CopyFromBuffer(IBuffer)` quando le dimensioni non cambiano; visto il costo minimo, farlo solo se il profiler lo chiede.
- `SoftwareBitmap` e `IDisposable`: usare `using`.

### 5. Preprocessing: scala, padding, contrasto, inversione (con misure)

#### 5.1 Cosa fanno gli altri **[SORGENTE]**

| Progetto | Regola |
| --- | --- |
| PowerToys Text Extractor (`PowerOCR.Core/Imaging/BitmapPreprocessor.cs`, 2026) | `MinimumDimension = 64`, `Padding = 8`, `HighQualityBicubic`, sfondo riempito con il colore del pixel d'angolo; fattore 1.5 se non si supera `MaxImageDimension` |
| Text-Grab (`OcrUtilities.GetIdealScaleFactorForOcrResult`) | primo passaggio OCR, poi riscalatura in modo che l'altezza di riga sia **40 px** ("Ideal Line Height is 40px"), limitata da `MaxImageDimension` |
| NVDA (`contentRecog/uwpOcr.py`) | "UWP OCR performs poorly with small images": se larghezza o altezza < 100 px ingrandisce 4x |
| Microsoft Q&A 685995 | con 89x27 px nessun risultato; "limitation of OCR" (staff Microsoft); rimedio: ingrandire |

#### 5.2 Misure sintetiche di oggi (motore it-IT, Segoe UI, rendering GDI+)

Test A - regione 1000x400, 10 righe fitte, 98 parole (49 italiane + 49 inglesi, senza accenti), percentuale di parole esatte:

| Corpo, tema, antialias | 1x | 2x NearestNeighbor | 2x HQ Bilinear | 2x HQ Bicubic | 3x HQ Bicubic |
| --- | --- | --- | --- | --- | --- |
| 11 px, scuro, ClearType | 94,9 | 93,9 | 92,9 | **100** | 100 |
| 11 px, scuro, grigi | 89,8 | 79,6 | 98 | **98** | 95,9 |
| 11 px, chiaro, ClearType | 87,8 | 96,9 | 100 | **100** | 100 |
| 11 px, chiaro, grigi | 84,7 | 79,6 | 95,9 | **98** | 93,9 |
| 12 px, scuro, ClearType | 94,9 | 93,9 | 100 | **100** | 100 |
| 12 px, scuro, grigi | 94,9 | 84,7 | 99 | **100** | 100 |
| 12 px, chiaro, ClearType | 88,8 | 87,8 | 100 | **100** | 100 |
| 13 px, chiaro, ClearType | 82,7 | 70,4 | 99 | **100** | 99 |

In tutti i casi scuri, **immagine invertita e non invertita hanno dato la stessa identica percentuale** alla stessa scala.

Test K - colonna di menu larga 300 px, 20 voci (accenti, "...", "&", ":", "%"), riga n. 5 evidenziata in blu come sotto il mouse; percentuale di righe esatte dopo normalizzazione (solo lettere e cifre, senza maiuscole):

| Corpo | 1x | 1.5x | 2x | 2.5x | 3x | 4x |
| --- | --- | --- | --- | --- | --- | --- |
| 9 px | 0 | 10-20 | 10-25 | 15-35 | 30-50 | 25-40 |
| 10 px | 0-5 | 30-70 | 35-75 | 55-70 | 60-75 | 55-70 |
| 11 px | 75-90 | 65-100 | 90-100 | 95-100 | **100** | 100 |
| 12 px | 65-80 | 90-95 | 95-100 | 95-100 | 95-100 | 95-100 |
| 14 px | 75-90 | 95-100 | 95-100 | 90-100 | 95-100 | 100 |

(intervalli = minimo e massimo fra tema scuro/chiaro e ClearType/grigi; a 12 px il residuo non al 100% e quasi sempre la riga con "perchè" che il motore corregge in "perché", cioe un falso errore del mio test.)

Test L - voci disabilitate, grigio #787878 su #2B2B2B, 12 px, scala 2.5x: nessun trattamento 90%; solo grigi 90%; solo inversione 90%; **grigi + stiramento del contrasto x3: 100%** (con o senza inversione).

Test B - etichetta singola in ritaglio stretto 110x22 px (corpo 12 px, tema scuro): a 1x **sempre testo vuoto** (6 su 6); 1x + padding 32 px: 3 su 6 (e "Opacity" diventa "O paci ty"); 3x senza padding: 5 su 6 (manca "OK"); **3x + padding 32 px: 6 su 6**; 4x + padding: 5 su 6 ("Opacity" diventa "Opaciw": scalare troppo puo peggiorare).

#### 5.3 Dati reali su Affinity (file del probe, tema scuro, Zenbook HiDPI)

Menu File intero, 486x938 px, altezza delle maiuscole 11-14 px: **26 etichette su 26 corrette gia a 1x**, comprese le 11 disabilitate. L'errore tipico a 1x e `Ctrl` letto `CtrI` (l minuscola / I maiuscola): scorciatoie corrette 3 su 10 a 1x, 8 su 10 da 1.5x a 3x, **9 su 10 a 2x in scala di grigi**, 7 su 10 a 4x. "Ctrl+P" viene sempre letto "Ctrl+p". Inversione: nessuna differenza. Riga evidenziata "Apri Recenti" a 1x: letto "ri Recenti" (persa la parte sinistra), corretto da 1.5x in su.

#### 5.4 Regola proposta

- Obiettivo: portare il corpo del testo a circa 28-32 px nell'immagine data al motore (coerente con i 40 px di altezza di riga di Text-Grab).
- Fattore iniziale: `scale = clamp(2.5 * 96.0 / monitorDpi, 1.0, 4.0)`, poi ridotto se `max(w, h) * scale + 2 * pad > OcrEngine.MaxImageDimension`.
- Secondo passaggio solo se serve: risultato vuoto, oppure altezza mediana delle parole (riportata a 1x) sotto 9 px -> ripetere con `scale * 1.6`. Su ritagli piccoli un passaggio costa 5-40 ms, quindi due passaggi restano sotto la soglia percepibile.
- Interpolazione: `HighQualityBicubic` + `PixelOffsetMode.HighQuality` + `WrapMode.TileFlipXY` (evita l'alone scuro ai bordi). Mai `NearestNeighbor`.
- Padding 16-32 px del colore di sfondo (pixel d'angolo o moda dei bordi); dimensione minima finale circa 150x100 px.
- Grigi + auto-levels nello stesso `DrawImage` tramite `ColorMatrix` (costo zero aggiuntivo). Inversione: opzione, spenta.

```csharp
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

public readonly record struct PrepInfo(double Scale, int Pad);   // to map OCR rects back to capture pixels

public static class OcrPreprocessor
{
    public static Bitmap Prepare(Bitmap src, double scale, int pad, bool autoLevels, bool invert, out PrepInfo info)
    {
        int w = (int)Math.Round(src.Width * scale), h = (int)Math.Round(src.Height * scale);
        var dst = new Bitmap(w + 2 * pad, h + 2 * pad, PixelFormat.Format32bppArgb);

        (float gain, float offset) = autoLevels ? AutoLevels(src) : (1f, 0f);
        float k = invert ? -gain : gain, off = invert ? 1f - offset : offset;

        using var ia = new ImageAttributes();
        ia.SetWrapMode(WrapMode.TileFlipXY);
        ia.SetColorMatrix(new ColorMatrix(new[]
        {   //            -> R'       G'        B'       A'  w
            new[] { 0.299f * k, 0.299f * k, 0.299f * k, 0f, 0f },   // from R
            new[] { 0.587f * k, 0.587f * k, 0.587f * k, 0f, 0f },   // from G
            new[] { 0.114f * k, 0.114f * k, 0.114f * k, 0f, 0f },   // from B
            new[] { 0f, 0f, 0f, 1f, 0f },
            new[] { off, off, off, 0f, 1f },
        }));

        using (var g = Graphics.FromImage(dst))
        {
            Color bg = src.GetPixel(0, 0);                       // better: mode of the border pixels
            float l = Math.Clamp((0.299f * bg.R + 0.587f * bg.G + 0.114f * bg.B) / 255f * k + off, 0f, 1f);
            int v = (int)(l * 255);
            g.Clear(Color.FromArgb(255, v, v, v));               // padding in the (transformed) background tone
            g.InterpolationMode = scale == 1.0 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(src, new Rectangle(pad, pad, w, h), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
        }
        info = new PrepInfo(scale, pad);
        return dst;
    }

    // 1st/99th percentile stretch on luminance; returns (gain, offset) in 0..1 units for the ColorMatrix.
    static unsafe (float gain, float offset) AutoLevels(Bitmap src)
    {
        var d = src.LockBits(new Rectangle(0, 0, src.Width, src.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        Span<int> hist = stackalloc int[256];
        try
        {
            var px = new ReadOnlySpan<byte>((void*)d.Scan0, src.Width * src.Height * 4);
            for (int i = 0; i < px.Length; i += 4) hist[(px[i] * 29 + px[i + 1] * 150 + px[i + 2] * 77) >> 8]++;
        }
        finally { src.UnlockBits(d); }
        int total = src.Width * src.Height, lo = 0, hi = 255, acc = 0;
        while (lo < 255 && (acc += hist[lo]) < total / 100) lo++;
        acc = 0;
        while (hi > 0 && (acc += hist[hi]) < total / 100) hi--;
        if (hi - lo < 24) return (1f, 0f);                        // flat area: probably no text at all
        float gain = Math.Min(255f / (hi - lo), 4f);
        return (gain, -(lo / 255f) * gain);
    }
}
```

Riportare i rettangoli al sistema della cattura: `x0 = (rect.X - pad) / scale`, idem per Y, larghezza e altezza `/ scale`.

### 6. Prestazioni **[MISURA]** (Snapdragon X, processo ARM64, solo il tempo di `RecognizeAsync`)

| Immagine data al motore | Tempo |
| --- | --- |
| etichetta singola 330x66 ... 500x150 | 2-9 ms |
| colonna menu 300x490 (20 righe) a 1x | 10-45 ms |
| stessa a 2x / 2.5x / 3x / 4x | 22-50 / 30-66 / 37-100 / 56-97 ms |
| **regione 1000x400 fitta (98 parole) a 1x** | **37-130 ms** |
| stessa a 2x (2000x800) / 3x (3000x1200) | 58-180 / 100-480 ms |
| Affinity reale, regione 900x300: 1x / 1.5x / 2x / 3x | 16-29 / 24-67 / 36-68 / 71-129 ms |
| Affinity reale, menu intero 486x938: 1x / 1.5x / 2x | 32 / 56 / 122-146 ms |

I valori alti delle forchette sono comparsi a fine batteria (decine di chiamate consecutive: frequenza della CPU e core di efficienza); la chiamata isolata tipica sta nella parte bassa. Il tempo cresce con i pixel e con la quantita di testo. Attenzione: il **preprocessing GDI+ `HighQualityBicubic` puo costare quanto l'OCR** sulle immagini grandi (nel probe Affinity: 27-150 ms per 486x938 a 1.5x-2x). Per questo conviene catturare una regione contenuta attorno al puntatore (es. 900x300 px fisici, o meno) e non l'intera finestra. Budget realistico per "clic -> testo pronto": cattura 5-15 ms + preprocessing 10-30 ms + OCR 25-70 ms = **circa 50-120 ms**, inferiore alla latenza della sintesi vocale. Creazione del motore: 20 creazioni in 10,5 ms (circa 0,5 ms l'una). La primissima chiamata del processo e piu lenta (caricamento del componente): fare un riscaldamento all'avvio con un'immagine finta.

### 7. Thread-safety, riuso, cancellazione

- `OcrEngine` e `OcrResult` sono `Agile` con `ThreadingModel.Both` **[DOC]**: si possono creare e usare da thread STA (UI WPF) o MTA (thread pool) indifferentemente; il lavoro pesante avviene comunque fuori dal thread chiamante.
- **Non rientrante per istanza** **[MISURA]**: lanciando 4 `RecognizeAsync` sulla stessa istanza senza attendere, le successive falliscono con `System.Exception: "Another RecognizeAsync operation is already running!"`. Quindi: un'istanza + `SemaphoreSlim`, oppure un'istanza per richiesta (costa 0,5 ms).
- Mai fare OCR dentro la callback dell'hook del mouse di basso livello (Windows rimuove in silenzio gli hook lenti): la callback deve solo accodare la richiesta (es. `Channel<T>`), un worker esegue cattura -> preprocessing -> OCR -> voce. Politica "vince l'ultimo clic": un `CancellationTokenSource` per richiesta, controllato fra una fase e l'altra; l'OCR in corso (decine di ms) si lascia finire e si scarta il risultato.

```csharp
public sealed class WindowsMediaOcrEngine : IOcrEngine, IDisposable
{
    private readonly OcrEngine _engine;
    private readonly SemaphoreSlim _gate = new(1, 1);          // RecognizeAsync is not re-entrant per instance
    public WindowsMediaOcrEngine(OcrEngine engine) => _engine = engine;
    public string Name => $"Windows.Media.Ocr ({_engine.RecognizerLanguage.LanguageTag})";

    public async Task<OcrPage> RecognizeAsync(PixelFrame f, CancellationToken ct)   // f: BGRA32, tightly packed
    {
        uint max = OcrEngine.MaxImageDimension;                // 10000 on build 26220; read it, never hard-code
        if (f.Width > max || f.Height > max) throw new ArgumentOutOfRangeException(nameof(f), "image too large for OcrEngine");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using SoftwareBitmap sb = SoftwareBitmap.CreateCopyFromBuffer(
                f.Pixels.AsBuffer(0, f.Width * f.Height * 4), BitmapPixelFormat.Bgra8, f.Width, f.Height, BitmapAlphaMode.Ignore);
            OcrResult r = await _engine.RecognizeAsync(sb);    // IAsyncOperation<T> is directly awaitable (CsWinRT)

            var lines = new List<OcrLineDto>(r.Lines.Count);
            foreach (OcrLine line in r.Lines)
            {
                var words = line.Words.Select(w => new OcrWordDto(
                    w.Text,
                    new RectF((float)w.BoundingRect.X, (float)w.BoundingRect.Y, (float)w.BoundingRect.Width, (float)w.BoundingRect.Height),
                    Confidence: null)).ToList();               // legacy engine has no confidence
                lines.Add(new OcrLineDto(line.Text, words));
            }
            return new OcrPage(lines, r.TextAngle);
        }
        finally { _gate.Release(); }
    }
    public void Dispose() => _gate.Dispose();
}
```

### 8. Dal risultato OCR all'elemento sotto il puntatore (nota di progetto)

L'OCR restituisce tutto il testo della regione; serve isolare "solo quella voce":

1. convertire il punto del puntatore nelle coordinate dell'immagine preprocessata (`p * scale + pad`);
2. candidate = righe la cui fascia verticale (unione dei rettangoli delle parole, con tolleranza del 40% dell'altezza) contiene la Y del puntatore;
3. dentro la riga, spezzare in **segmenti** dove il vuoto fra due parole supera circa 1 volta l'altezza della riga (nel probe Affinity lo spazio normale fra parole vale 4-6 px su righe alte 11-15 px; il padding fra voci di una barra dei menu e molto maggiore): serve per le barre dei menu, che arrivano come una riga unica;
4. scegliere il segmento che contiene la X del puntatore; se nessuno la contiene (puntatore nello spazio vuoto di una voce di menu, caso "fra Nuovo e Ctrl+N" del probe) prendere il segmento **a sinistra** sulla stessa fascia, entro una distanza massima; se il puntatore e sopra la scorciatoia, leggere l'etichetta a sinistra e non la scorciatoia;
5. ripulire: togliere token fatti solo di simboli (icone lette come `>`, `•`, `□`, `x`), normalizzare `CtrI` -> `Ctrl`, togliere "..." finali (a corpi piccoli vengono letti come "—"); la regola "mai emoji" e rispettata per costruzione (il motore non ne produce), ma un filtro sulle categorie Unicode `So`/`Sk` resta opportuno.

### 9. Windows AI APIs: `Microsoft.Windows.AI.Imaging.TextRecognizer`

**Cos'e** **[DOC]**: OCR basato su modello neurale eseguito su NPU, esposto dal Windows App SDK (namespace `Microsoft.Windows.AI.Imaging`, stabile dalla 1.7.1 del 2025; stabile corrente **2.5.1 del 16 settembre 2026**; la 1.8 e uscita dal supporto il 9 settembre 2026). Microsoft lo dichiara "faster and more accurate than the legacy Windows.Media.Ocr.OcrEngine". Fornisce righe, parole, **poligoni a quattro punti** (`RecognizedTextBoundingBox`: TopLeft, TopRight, BottomRight, BottomLeft), **`RecognizedWord.MatchConfidence`** e lo stile della riga (`RecognizedLineStyle`). Avvertenza ufficiale: "Characters that are illegible or small in size can generate inaccurate results".

**Requisiti** **[DOC]**:
- hardware: **solo Copilot+ PC con NPU**. Nella tabella dell'hardware supportato (pagina aggiornata a luglio-agosto 2026) la riga "Text Recognition (OCR)" e NPU: disponibile; GPU: non supportato; CPU: non supportato. Su un PC Intel/AMD non Copilot+ `GetReadyState()` restituisce `NotSupportedOnCurrentSystem`;
- sistema: Windows 11 25H2 build 10.0.26200.7309 o successiva (pagina troubleshooting);
- **identita di pacchetto obbligatoria**: "Apps that use Windows AI APIs need to be granted package identity at runtime"; README degli esempi ufficiali: "Unpackaged app configuration is no longer supported. Every app using Windows AI APIs must have a package identity" (ottenibile anche con "packaging with external location"). Nel manifest del pacchetto serve la capability `systemAIModels`. Senza identita: `UnauthorizedAccessException` o errori di accesso;
- **Limited Access Feature: NON richiesto per l'OCR.** Il token LAF riguarda Phi Silica (note di rilascio 2.0.1); la pagina indice lo indica solo per Phi Silica;
- self-contained del Windows App SDK supportato (`WindowsAppSDKSelfContained=true`), ma la pagina troubleshooting avverte: **"Self-contained apps cannot run from the Downloads folder (or from anywhere under the C:\Users folder)"**. Per una cartella portabile (oggi il progetto sta sul Desktop) e un vincolo pesante;
- modello: su Copilot+ e preinstallato o scaricato via Windows Update con `EnsureReadyAsync()`. Sullo Zenbook risultano gia installati `WindowsWorkload.TextRecognition.Qnn.1` 1.2508.888.0 e Windows App Runtime 2.5.1 (arm64, x64, x86) **[MISURA]**.

**Cosa comporta l'identita per un'app portabile** **[DOC]**: creare un manifest di identita (`AllowExternalContent=true`, `runFullTrust`, piu `systemAIModels`), impacchettarlo con `MakeAppx /nv`, **firmarlo** con un certificato fidato sul PC di destinazione (autofirmato: importare il `.cer` in `Cert:\CurrentUser\TrustedPeople`; senza: errore `0x800B0109`), incorporare nell'eseguibile un manifest con l'elemento `<msix publisher=... packageName=... applicationId=.../>` e **registrare** il pacchetto con `Add-AppxPackage -Path ... -ExternalLocation <cartella>` oppure `PackageManager.AddPackageByUriAsync` con `AddPackageOptions.ExternalLocationUri`. La registrazione e per utente ed e **legata al percorso assoluto della cartella**: spostando la cartella l'identita sparisce senza errori e va rifatta la registrazione. `winapp create-debug-identity` (WinApp CLI, giugno 2026) fa la stessa cosa senza firma ma richiede la Modalita sviluppatore: va bene solo per lo spike. In pratica non e piu "copia la cartella e avvia".

**Problemi noti 2026**: issue microsoft/WindowsAppSDK #6308 (Windows App SDK 1.8.5, build 26220.8062): app **x64** con external location -> `TextRecognizer.GetReadyState()` lancia `ERROR_BAD_EXE_FORMAT`, mentre ARM64 funziona e il packaging MSIX completo funziona su entrambe; esito della issue non chiaro dalla pagina. Issue #6375 (aprile 2026): `ResourceManager` MRT non trova il `.pri` nelle app con pacchetto sparse.

**Verdetto**: non vale un percorso di codice nella v1. Vantaggi reali (confidenza, poligoni, testo in foto e scene naturali) ma: copre solo una parte dei PC target (per Intel serve comunque un'altra soluzione), rompe la distribuzione a cartella portabile, aggiunge firma, certificato e Windows App SDK (decine di MB se self-contained). Il secondo motore multipiattaforma deve essere l'OCR ONNX. Tenere TextRecognizer come esperimento di fase 2, dietro `IOcrEngine`, attivabile solo se `IsAvailable()` e vero.

Schizzo del percorso opzionale (progetto separato `PuntaEAscolta.Windows.Npu`, riferimento `Microsoft.WindowsAppSDK` 2.x, TFM `net10.0-windows10.0.22621.0`, `WindowsPackageType=None`):

```csharp
using Microsoft.Graphics.Imaging;          // ImageBuffer
using Microsoft.Windows.AI;                // AIFeatureReadyState, AIFeatureReadyResultState
using Microsoft.Windows.AI.Imaging;        // TextRecognizer, RecognizedText, RecognizedLine, RecognizedWord
using Windows.Graphics.Imaging;

public sealed class NpuTextRecognizerEngine : IOcrEngine, IDisposable
{
    private TextRecognizer? _rec;
    public string Name => "Windows AI TextRecognizer (NPU)";

    // Never throws: no package identity, runtime missing, non-Copilot+ PC, x64 sparse bug... all mean "not available".
    public static bool IsAvailable()
    {
        try
        {
            return TextRecognizer.GetReadyState() is AIFeatureReadyState.Ready or AIFeatureReadyState.NotReady;
        }
        catch { return false; }
    }

    public async Task InitializeAsync()
    {
        if (TextRecognizer.GetReadyState() == AIFeatureReadyState.NotReady)
        {
            var r = await TextRecognizer.EnsureReadyAsync();      // model download through Windows Update
            if (r.Status != AIFeatureReadyResultState.Success) throw new InvalidOperationException(r.ExtendedError?.Message);
        }
        _rec = await TextRecognizer.CreateAsync();
    }

    public Task<OcrPage> RecognizeAsync(PixelFrame f, CancellationToken ct) => Task.Run(() =>
    {
        using SoftwareBitmap sb = SoftwareBitmap.CreateCopyFromBuffer(
            f.Pixels.AsBuffer(0, f.Width * f.Height * 4), BitmapPixelFormat.Bgra8, f.Width, f.Height, BitmapAlphaMode.Premultiplied);
        using ImageBuffer img = ImageBuffer.CreateForSoftwareBitmap(sb);   // or ImageBuffer.CreateForBuffer(IBuffer, format, w, h, stride)
        RecognizedText text = _rec!.RecognizeTextFromImage(img);           // RecognizeTextFromImageAsync also exists

        var lines = text.Lines.Select(l => new OcrLineDto(l.Text, l.Words.Select(w =>
        {
            var b = w.BoundingBox;                                          // 4-point polygon
            float x0 = (float)Math.Min(b.TopLeft.X, b.BottomLeft.X), y0 = (float)Math.Min(b.TopLeft.Y, b.TopRight.Y);
            float x1 = (float)Math.Max(b.TopRight.X, b.BottomRight.X), y1 = (float)Math.Max(b.BottomLeft.Y, b.BottomRight.Y);
            return new OcrWordDto(w.Text, new RectF(x0, y0, x1 - x0, y1 - y0), Confidence: w.MatchConfidence);
        }).ToList())).ToList();
        return new OcrPage(lines, TextAngle: null);
    }, ct);

    public void Dispose() => _rec?.Dispose();
}
```

`TextRecognizerOptions` (`DetectorOutputGeometryMode`, `EnableWordLevelConfidence`) e le overload che lo accettano sono marcate `[Experimental]` e compaiono solo nel moniker 2.0-experimental: non usarle.

### 10. Separazione core / piattaforma

Nel core (`net10.0`, nessun riferimento a Windows) solo il contratto e i DTO; il layer Windows implementa `WindowsMediaOcrEngine` (sempre), `OnnxOcrEngine` (opzionale) e, in futuro, `NpuTextRecognizerEngine`. Su macOS lo stesso contratto verra implementato con Vision (`VNRecognizeTextRequest`).

```csharp
namespace PuntaEAscolta.Core.Ocr;

public readonly record struct RectF(float X, float Y, float Width, float Height);
public sealed record PixelFrame(byte[] Pixels, int Width, int Height);   // BGRA32, top-down, stride = Width*4 (array may be pooled/longer)
public sealed record OcrWordDto(string Text, RectF Bounds, float? Confidence);
public sealed record OcrLineDto(string Text, IReadOnlyList<OcrWordDto> Words);
public sealed record OcrPage(IReadOnlyList<OcrLineDto> Lines, double? TextAngle);

public interface IOcrEngine
{
    string Name { get; }
    Task<OcrPage> RecognizeAsync(PixelFrame frame, CancellationToken ct);
}
```

La logica di scelta della scala, il secondo passaggio, l'hit-test della sezione 8 e la pulizia del testo stanno nel core e sono testabili senza Windows; il ridimensionamento GDI+ sta nel layer Windows dietro un'interfaccia `IImagePreprocessor`.

---

## Insidie note

1. **Ritagli piccoli = risultato vuoto, senza errore.** 110x22 px: niente. Servono scala e padding; minimo pratico circa 150x100 px finali (PowerToys usa minimo 64 + padding 8, NVDA 4x sotto i 100 px, Microsoft conferma il limite).
2. **Parole brevi isolate** ("OK", "Si", "No", "X") sono le piu fragili: "OK" e stato letto solo con 3x + padding 32. Catturare sempre un po' di contesto attorno al puntatore invece del solo controllo.
3. **Scalare troppo peggiora** (4x: "Opacity" -> "Opaciw"; su Affinity scorciatoie 7/10 a 4x contro 9/10 a 2x in grigi) e costa tempo. Puntare a corpo 28-32 px, non "piu grande possibile".
4. **`NearestNeighbor` e dannoso** (fino a -20 punti rispetto al bicubico alla stessa scala).
5. **Confusioni tipiche**: `l`/`I` ("CtrI"), `P`/`p` ("Ctrl+p"), "..." -> "—" sotto gli 11 px, `z` -> `:` e `y:` -> `f.` a 10 px, lettere sottili (`i`, `l`) perse a 10 px ("Sava con nome", "Tonalta"). Normalizzare le piu frequenti prima della sintesi vocale.
6. **Il modello linguistico corregge**: "perchè" -> "perché". Utile di solito; attenzione a sigle e nomi di file (nel probe: "STRADE_vettoriale.svg" letto a volte "STRADE_vettorialesvg", "profile" -> "profiIe").
7. **Riga evidenziata sotto il mouse**: l'hover cambia lo sfondo proprio della riga che interessa; a 1x su Affinity "Apri Recenti" e diventato "ri Recenti". Con scala >= 1.5 il problema sparisce; nei test sintetici l'evidenziazione blu non ha ridotto l'accuratezza.
8. **Icone lette come lettere o simboli** all'inizio o alla fine della riga (segni di spunta, frecce di sottomenu, icone degli strumenti): filtrare i token di 1-2 caratteri senza lettere o cifre e quelli separati dal testo da un vuoto ampio.
9. **Testo vicino ai bordi del ritaglio**: parole tagliate producono spazzatura; il padding aiuta il motore ma non ricostruisce le lettere mancanti: allargare la regione di cattura in orizzontale (almeno 300-450 px per lato) e scartare i segmenti che toccano il bordo.
10. **Nessuna confidenza**: un risultato sbagliato e indistinguibile da uno giusto. Mitigazioni: doppio passaggio a scale diverse e confronto, filtro su rapporto lettere/simboli, preferire sempre UI Automation quando restituisce un nome.
11. **`RecognizeAsync` non rientrante per istanza**: serializzare (sezione 7).
12. **`MaxImageDimension` varia con la versione di Windows** (2600 nelle fonti storiche, 10000 sulla 26220): non cablarlo; oltre il limite parte un'eccezione ("Image dimensions are too large!").
13. **Alfa**: le catture `BitBlt` hanno spesso alfa 0; usare `BitmapAlphaMode.Ignore` (nei miei test l'alfa era 255: il caso alfa 0 va verificato, vedi domande aperte).
14. **DPI**: senza PerMonitorV2 la cattura e riscalata da Windows e l'OCR degrada. Il fattore di scala va calcolato sul DPI del monitor sotto il puntatore.
15. **Costo del preprocessing GDI+**: `HighQualityBicubic` su immagini grandi costa quanto l'OCR. Tenere piccola la regione.
16. **Non usare `oneocr.dll`** (il motore nuovo di Strumento di cattura e Foto): non e un'API pubblica ne ridistribuibile.
17. **Script PowerShell 5.1 con caratteri accentati salvati in UTF-8 senza BOM** vengono letti come ANSI: nel mio primo test le accentate sono state disegnate sbagliate ("TonaltÃ"). Negli script di prova usare solo ASCII e `[char]0xE0`.
18. **TextRecognizer**: identita legata al percorso assoluto; self-contained non eseguibile da sotto `C:\Users`; bug x64 con external location; la 1.8 del Windows App SDK e fuori supporto dal 9 settembre 2026 (usare 2.x); API `TextRecognizerOptions` sperimentali.

---

## Domande aperte da verificare sulla macchina

1. **Ripetere le misure dal vero stack** (.NET 10 + CsWinRT invece di PowerShell 5.1) appena installato l'SDK: stessi risultati attesi (il motore e il componente di sistema), ma vanno confermati `AsBuffer`, `await` diretto su `IAsyncOperation`, dimensione del publish self-contained (`Microsoft.Windows.SDK.NET.dll`), tempo della prima chiamata a freddo.
2. **Alfa = 0**: una cattura `BitBlt` reale con alfa 0 e `BitmapAlphaMode.Ignore` (e `Premultiplied`) viene letta correttamente?
3. **Screenshot reali** oltre al menu File di Affinity: pannelli di Affinity (Livelli, Regolazioni: testo piu piccolo dei menu), barra contestuale, Photoshop, tooltip, finestre di dialogo; a 100%, 150% e 200% di scala. Verificare la formula `2.5 * 96 / dpi` e la soglia del secondo passaggio.
4. **PC di destinazione di Matteo**: risoluzione, scala, e se e un Copilot+ PC. Se e un portatile a 100% di scala con UI a 11-12 px, l'upscaling 2.5-3x e indispensabile; se il testo scende sotto gli 11 px serve il motore ONNX.
5. **Soglia di segmentazione orizzontale** (sezione 8) su barre dei menu reali di Word, Excel, Affinity: il rapporto vuoto/altezza riga fra voci adiacenti e davvero > 1?
6. **Testo nelle foto** (cartello, maglietta): qualita di `Windows.Media.Ocr` su testo ruotato, in prospettiva, su sfondo non uniforme; comportamento di `TextAngle` e del sistema di coordinate dei rettangoli con immagine ruotata di 10-30 gradi. E qui che ONNX o TextRecognizer possono fare la differenza.
7. **Parole inglesi "italianizzate"**: cercare casi reali in cui il modello linguistico it-IT altera un termine inglese dell'interfaccia (es. "Persona", "Live", "Export", nomi di filtri). Se capitano, valutare l'installazione di `Language.OCR~~~en-US~0.0.1.0` e un doppio passaggio it/en solo su richiesta.
8. **Spike TextRecognizer a tempo limitato (mezza giornata) sullo Zenbook**: WPF .NET 10, Windows App SDK 2.5.1, `winapp create-debug-identity`, capability `systemAIModels`; verificare `GetReadyState()`, latenza su ritagli 900x300, qualita sul testo da 9-10 px e sulle foto, e se la cartella sotto `C:\Users\...\Desktop` blocca davvero l'esecuzione self-contained. Su un PC x64 Copilot+ (se disponibile) verificare la issue #6308.
9. **Deriva termica e core di efficienza**: nelle batterie lunghe i tempi sono triplicati. Misurare la latenza sulla chiamata singola dopo inattivita (scenario reale: un clic ogni tanto) e a batteria con risparmio energetico attivo.
10. **`MaxImageDimension` su un Windows 11 stabile 24H2/25H2 x64**: vale ancora 10000 o 2600?

---

## Fonti

Documentazione Microsoft
- Call Windows Runtime APIs in desktop apps (TFM .NET 10, aggiornata 2026-07-03): https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/desktop-to-uwp-enhance
- OcrEngine: https://learn.microsoft.com/en-us/uwp/api/windows.media.ocr.ocrengine
- OcrEngine.RecognizeAsync: https://learn.microsoft.com/en-us/uwp/api/windows.media.ocr.ocrengine.recognizeasync
- OcrEngine.TryCreateFromLanguage: https://learn.microsoft.com/en-us/uwp/api/windows.media.ocr.ocrengine.trycreatefromlanguage
- OcrEngine.TryCreateFromUserProfileLanguages: https://learn.microsoft.com/en-us/uwp/api/windows.media.ocr.ocrengine.trycreatefromuserprofilelanguages
- OcrEngine.MaxImageDimension: https://learn.microsoft.com/en-us/uwp/api/windows.media.ocr.ocrengine.maximagedimension
- OcrResult (Lines, Text, TextAngle): https://learn.microsoft.com/en-us/uwp/api/windows.media.ocr.ocrresult
- PowerToys Text Extractor (elenco e installazione dei pacchetti `Language.OCR`): https://learn.microsoft.com/en-us/windows/powertoys/text-extractor
- Microsoft Q&A, "Windows.Media.Ocr won't scan anything if width and/or height are too small": https://learn.microsoft.com/en-us/answers/questions/685995/windows-media-ocr-wont-scan-anything-if-width-and
- Blog Windows Developer 2016, OCR for Windows 10: https://blogs.windows.com/windowsdeveloper/2016/02/08/optical-character-recognition-ocr-for-windows-10/
- Windows AI APIs, panoramica e tabella hardware (2026-07-15): https://learn.microsoft.com/en-us/windows/ai/apis/
- Text Recognition (TextRecognizer), guida (2026-07-16): https://learn.microsoft.com/en-us/windows/ai/apis/text-recognition
- Text recognizer walkthrough: https://learn.microsoft.com/en-us/windows/ai/apis/text-recognition-tutorial
- Get started with Windows AI APIs (manifest, `systemAIModels`, WPF): https://learn.microsoft.com/en-us/windows/ai/apis/get-started
- Windows AI API troubleshooting (identita di pacchetto, LAF, self-contained sotto C:\Users, build minima): https://learn.microsoft.com/en-us/windows/ai/apis/troubleshooting
- API reference TextRecognizer: https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.windows.ai.imaging.textrecognizer
- Namespace Microsoft.Windows.AI.Imaging: https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.windows.ai.imaging
- RecognizedWord (MatchConfidence): https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.windows.ai.imaging.recognizedword
- TextRecognizerOptions (experimental): https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.windows.ai.imaging.textrecognizeroptions
- ImageBuffer: https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.graphics.imaging.imagebuffer
- Windows App SDK, canali di rilascio e ciclo di vita (2.5.1 del 2026-09-16): https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/stable-channel
- Windows App SDK 2.0, note di rilascio: https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-notes/windows-app-sdk-2-0
- Grant package identity by packaging with external location (2026-04-08): https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/grant-identity-to-nonpackaged-apps
- .NET Blog, Packaging and Package Identity for .NET apps with WinApp CLI (2026-06-29): https://devblogs.microsoft.com/dotnet/packaging-dotnet-apps-winapp/

NuGet e repository
- Microsoft.Windows.SDK.NET.Ref (10.0.26100.87, 2026-07-23): https://www.nuget.org/packages/Microsoft.Windows.SDK.NET.Ref
- CsWinRT releases (2.3.1; 3.0 preview): https://github.com/microsoft/cswinrt/releases
- CsWinRT issue #646 e #1214 (IMemoryBufferByteAccess): https://github.com/microsoft/CsWinRT/issues/646 , https://github.com/microsoft/CsWinRT/issues/1214
- PowerToys, BitmapPreprocessor.cs: https://github.com/microsoft/PowerToys/tree/main/src/modules/PowerOCR/PowerOCR.Core/Imaging
- PowerToys, WindowsOcrRecognizer.cs: https://github.com/microsoft/PowerToys/tree/main/src/modules/PowerOCR/PowerOCR.Core/Ocr
- PowerToys PR #44906 (PadImage, minimo 64x64): https://github.com/microsoft/PowerToys/pull/44906
- Text-Grab, OcrUtilities.cs (altezza di riga ideale 40 px): https://github.com/TheJoeFin/Text-Grab/blob/main/Text-Grab/Utilities/OcrUtilities.cs
- NVDA, uwpOcr.py (4x sotto i 100 px): https://github.com/nvaccess/nvda/blob/master/source/contentRecog/uwpOcr.py
- WindowsAppSDK-Samples, Windows AI (README: "Unpackaged app configuration is no longer supported"): https://github.com/microsoft/WindowsAppSDK-Samples/tree/main/Samples/WindowsAIFoundry/cs-winui
- WindowsAppSDK issue #6308 (x64 + external location, ERROR_BAD_EXE_FORMAT): https://github.com/microsoft/WindowsAppSDK/issues/6308
- WindowsAppSDK issue #6375 (MRT con pacchetto sparse): https://github.com/microsoft/WindowsAppSDK/issues/6375
- Thomas Claudius Huber, Use Windows AI in WPF (2025-05-03, epoca experimental non impacchettata): https://www.thomasclaudiushuber.com/2025/05/03/use-windows-ai-in-wpf/

Misure locali (2026-09-21, questa macchina)
- `docs/research/probe/ocr-winrt-bench-region.ps1` (test A e B)
- `docs/research/probe/ocr-winrt-bench-menu.ps1` (test K, L, concorrenza)
- `docs/research/probe/affinity-ocr-variants.txt`, `docs/research/probe/affinity-ocr-geometry.txt` (dati di un altro task di ricerca su screenshot reali di Affinity)
