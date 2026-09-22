# OCR ONNX (PaddleOCR / RapidOCR) in C# su win-x64 e win-arm64

Ricerca del 21 settembre 2026 per "Punta e Ascolta". Solo ricerca documentale: nessun codice C# compilato
(manca ancora il .NET SDK). Le verifiche "dal vivo" fatte sono: richieste HTTP HEAD/Range ai file dei modelli
(per dimensioni e SHA256), lettura dei file di testo (dizionari, YAML, sorgenti) e interrogazione dell'indice PyPI.
Nessun modello o binario e' stato scaricato.

---

## Raccomandazione

**In breve: usare la libreria NuGet `RapidOcrNet` 4.2.0 (BobLd, Apache-2.0) sopra `Microsoft.ML.OnnxRuntime` 1.30.0
con il solo provider CPU, modelli PP-OCRv5 "latin" gia' inclusi nel pacchetto come default, e PP-OCRv6 "small"
come opzione "alta qualita'" scaricata a build-time con verifica SHA256. Tenere pronto (ma non scrivere subito)
un piano B di implementazione propria in C# puro, descritto sotto.**

Motivi:

1. **E' l'unico port C# "pulito" per ARM64.** `RapidOcrNet` ha eliminato OpenCV e System.Drawing: dipende solo da
   `Microsoft.ML.OnnxRuntime` (nativo win-x64 + win-arm64 nello stesso NuGet), `SkiaSharp` (nativo win-arm64 presente
   da anni in `SkiaSharp.NativeAssets.Win32`) e `Clipper2` (100% managed). Target `net8.0;net10.0`, AOT-compatibile,
   8 release nel 2026 (ultima 4.2.0 del 9 settembre 2026), issue tracker attivo. Tutte le alternative (RapidOCRCSharp
   ufficiale, Sdcb.PaddleOCR, PaddleOCRSharp, OcrLiteOnnxCs) hanno almeno un blocco: OpenCV/Emgu (licenza GPL o niente
   ARM64), Paddle Inference nativo solo x64, oppure sono abbandonate.
2. **Modelli gia' dentro il NuGet.** Il pacchetto (12,2 MB) contiene det + cls + rec PP-OCRv5 *latin* + dizionario
   (502 caratteri, verificato: contiene `à è é ì í î ò ó ù ú À È É Ì Ò Ù € ° « » ’`). Zero download per il default.
3. **Solo CPU.** I modelli sono minuscoli (4,8 MB det + 7,9 MB rec). QNN/NPU richiede modelli quantizzati a forma
   statica (i nostri hanno forma dinamica), e i pacchetti `Microsoft.ML.OnnxRuntime.QNN` e `.DirectML` sono fermi alla
   1.24.4 (marzo 2026) mentre il pacchetto CPU e' alla 1.30.0; DirectML e' dichiarato "in sustained engineering".
   Per un OCR on-demand da ~0,1-0,5 s non vale la complessita'.
4. **Portabilita' macOS.** Sia ONNX Runtime sia SkiaSharp hanno nativi osx-arm64: il motore OCR puo' stare nella
   libreria *core* dietro un'interfaccia `IOcrEngine`, senza nulla di Windows-specifico.
5. **Strategia a due fasi per la latenza**: `DetectBoxes()` sull'intera zona (~1000x600), scelta del box piu' vicino
   al puntatore, poi `Detect()` solo sul ritaglio di quel box (+ eventuali vicini). Cosi' si riconoscono 1-3 righe
   invece di 20.

Scelte operative consigliate:

| Voce | Scelta |
|---|---|
| Libreria | `RapidOcrNet` 4.2.0 (pin esatto), dietro `IOcrEngine` nel core |
| Runtime | `Microsoft.ML.OnnxRuntime` 1.30.0 (pin esplicito nel nostro csproj; RapidOcrNet richiede >= 1.29.0), CPU EP |
| Immagini | `SkiaSharp` 3.119.x (quella risolta da RapidOcrNet; **non** forzare la 4.x senza test) |
| Modelli default | PP-OCRv5: `ch_PP-OCRv5_mobile_det` + `latin_PP-OCRv5_rec_mobile` + `ppocrv5_latin_dict.txt` (nel NuGet) |
| Modelli "alta qualita'" | PP-OCRv6 small: `PP-OCRv6_det_small.onnx` (9,9 MB) + `PP-OCRv6_rec_small.onnx` (21,2 MB) + `ppocrv6_dict.txt` (75 KB), da ModelScope RapidAI, con SHA256 |
| Classificatore angolo | **Disattivato** (`DoAngle = false`): a schermo il testo non e' mai capovolto di 180 gradi |
| Thread | `IntraOpNumThreads = 4`, `InterOpNumThreads = 1`, sessioni create una volta e tenute vive, warm-up all'avvio |
| Distribuzione | due cartelle portabili: `dotnet publish -r win-x64 --self-contained` e `-r win-arm64 --self-contained` |
| Piano B | implementazione propria (~600-800 righe C#) descritta in "Dettagli tecnici", stessi modelli ONNX |

Da decidere sulla macchina con un test A/B: se PP-OCRv6 small legge meglio il testo piccolo della UI di Affinity
rispetto a PP-OCRv5 latin. Attenzione: il dizionario v6 ha 18.708 classi (CJK incluso) quindi sul rumore puo' emettere
ideogrammi: va filtrato l'output ai soli caratteri latini (vedi sotto).

---

## Dettagli tecnici

### 1. ONNX Runtime da C# su x64 e ARM64

**Pacchetto.** `Microsoft.ML.OnnxRuntime` 1.30.0, pubblicato il 10 settembre 2026 (storico recente: 1.29.0 12/8/2026,
1.28.0 25/7/2026, 1.27.1 11/7/2026, 1.27.0 16/6/2026). Il nupkg pesa ~150 MB perche' contiene i nativi di tutte le
piattaforme; dipende da `Microsoft.ML.OnnxRuntime.Managed` (API C#, netstandard2.0 e successive, quindi va bene su net10.0).

**win-arm64: si'.** La documentazione ufficiale C# dichiara per il pacchetto CPU: "Windows, Linux, Mac, X64, X86
(Windows-only), ARM64 (Windows-only)". Lo script che genera il nuspec nel repository
(`tools/nuget/generate_nuspec_for_native_nuget.py`) mette i nativi Windows in `runtimes/win-{x86,x64,arm,arm64}/native`.
Conferma indiretta: su PyPI `onnxruntime` 1.30.0 ha wheel `win_arm64` per cp311-cp314 (~14 MB), quindi la build
ARM64 Windows e' un artefatto di prima classe. Con `dotnet publish -r win-arm64` viene copiato solo
`runtimes/win-arm64/native/onnxruntime.dll` (stima 15-25 MB non compressi per architettura).

**Execution provider: restare su CPU.**

- **QNN (NPU Hexagon)**: pacchetto `Microsoft.ML.OnnxRuntime.QNN`, ultima versione 1.24.4 (17/3/2026), 92 MB.
  La documentazione dice: "The QNN HTP backend only supports quantized models" e "QNN EP does not support models with
  dynamic shapes". I modelli PP-OCR sono fp32 con H/W dinamici (det) e larghezza dinamica (rec): servirebbe
  quantizzare QDQ con dati di calibrazione e fissare le forme a bucket. Inoltre non esiste su Intel: doppio percorso
  di codice. Non ne vale la pena.
- **DirectML**: `Microsoft.ML.OnnxRuntime.DirectML` 1.24.4 (17/3/2026); la pagina dell'EP dichiara "DirectML is in
  sustained engineering" e consiglia Windows ML. Richiede `DisableMemPattern` + `ORT_SEQUENTIAL`. Per modelli da 5-20 MB
  l'overhead di upload/dispatch GPU mangia il guadagno.
- **Windows ML** (Windows App SDK; doc aggiornata 1/9/2026): stessa API ORT, EP scaricati da Windows Update, NPU/GPU
  solo da Windows 11 24H2. Porta con se' la dipendenza dal Windows App SDK, poco adatta a una cartella portabile
  senza installer. Da tenere come evoluzione futura, non ora.
- **CPU su ARM64**: ORT integra KleidiAI/NEON in MLAS dalla 1.22; la 1.30 aggiunge altri kernel Arm64. Esiste un
  avviso cosmetico noto: la libreria `cpuinfo` di ORT puo' non riconoscere i core Oryon dello Snapdragon X
  (warning nei log, l'inferenza funziona).

**API C# essenziali** (namespace `Microsoft.ML.OnnxRuntime`):

```csharp
using Microsoft.ML.OnnxRuntime;

static SessionOptions CreateCpuOptions(int threads = 4)
{
    var so = new SessionOptions
    {
        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        ExecutionMode          = ExecutionMode.ORT_SEQUENTIAL,
        IntraOpNumThreads      = threads,   // leave cores for audio/TTS
        InterOpNumThreads      = 1,
        EnableCpuMemArena      = false,     // RapidOCR python does the same: dynamic shapes make the arena grow
    };
    return so;
}

// One session per model, created once and kept for the whole app lifetime.
using var session = new InferenceSession(modelPath, CreateCpuOptions());
string inputName  = session.InputMetadata.Keys.First();      // "x" for PP-OCR models
string outputName = session.OutputMetadata.Keys.First();

// Zero-copy input over a managed float[] in NCHW layout.
using var input   = OrtValue.CreateTensorValueFromMemory(data, new long[] { 1, 3, h, w });
using var runOpts = new RunOptions();                          // runOpts.Terminate = true cancels a running inference
using var outputs = session.Run(runOpts, new[] { inputName }, new[] { input }, new[] { outputName });
ReadOnlySpan<float> result = outputs[0].GetTensorDataAsSpan<float>();
long[] shape = outputs[0].GetTensorTypeAndShape().Shape;       // det: [1,1,H,W]   rec: [1,T,C]
```

`InferenceSession.Run` e' thread-safe; `RunOptions.Terminate = true` interrompe un'inferenza in corso (utile per il
"secondo clic = stop").

### 2. Port C# esistenti: valutazione

| Libreria | Versione / data | Dipendenze native | win-arm64 | Licenza | Stato 2026 | Verdetto |
|---|---|---|---|---|---|---|
| **RapidOcrNet** (BobLd) | 4.2.0, 9/9/2026; 46 K download | ORT (>= 1.29.0), SkiaSharp (>= 3.119.1), Clipper2 2.0.0 (managed) | **Si'** (tutte le dipendenze hanno nativi arm64); non dichiarato esplicitamente nel README | Apache-2.0 | Molto attivo: 1.0.2 (29/3), 2.0.0 (13/5), 3.0.0 (13/7), 4.0.x (15-16/8), 4.1.0 (30/8), 4.2.0 (9/9) | **Scelta** |
| RapidOCRCSharp ufficiale (`RapidOCRLib`) | 0.9.5, 18/4/2026; 860 download | ORT >= 1.24.4, **Emgu.CV >= 4.12.0.5764**, Clipper2 | Tecnicamente si' (Emgu ha runtime win-arm64) | Codice Apache-2.0, ma **Emgu.CV e' GPLv3 oppure commerciale** | 18 commit, 93 stelle, poco usato | No (licenza Emgu, maturita') |
| Sdcb.PaddleOCR / Sdcb.PaddleInference | 3.0.x (PP-OCRv5; runtime `win64.mkl` 3.1.0.54) | **Paddle Inference nativo** + **OpenCvSharp4** | **No**: runtime Windows solo `win64` (x64); arm64 solo per Linux e macOS | Apache-2.0 | Attivo (1,5 K stelle) | No (niente win-arm64, footprint di centinaia di MB) |
| PaddleOCRSharp (raoyutian) | NuGet attivo, .NET 4.0 - 10.0 | `PaddleOCR.dll` C++ + paddle_inference + OpenCV | **No**: "win10_x64 ... CPU con AVX2" | C# open, DLL native di terzi | Attivo ma solo x64 | No |
| OcrLiteOnnxCs (benjaminwan) | ferma: VS2017, ORT 1.5.2, Emgu.CV 4.4.0.4099, clipper 6.2.1 | Emgu.CV | No (versioni vecchissime) | - | Abbandonata; e' l'antenata del codice RapidOCR C# | No (solo riferimento storico) |

**OpenCvSharp e ARM64 (verificato).** `OpenCvSharp4.runtime.win` (4.13.0.20260627) e' solo x64 (l'x86 e' stato
rimosso con la serie 4.13; la linea 4 e' in "maintenance mode"). Novita' 2026: esiste `OpenCvSharp5.runtime.win-arm64`
5.0.0.20260905 (5/9/2026, 28,9 MB, 81 download, senza FFmpeg) da abbinare a `OpenCvSharp5` (.NET 8+). Quindi OpenCV su
win-arm64 oggi e' *possibile*, ma e' recentissimo, ~29 MB in piu' per architettura, e inutile per noi: nessuna delle
librerie OCR sopra usa OpenCvSharp5.

### 3. Uso di RapidOcrNet nel progetto

API pubblica (dal sorgente `RapidOcr.cs`, ramo master, v4.2.0):

```csharp
public sealed partial class RapidOcr : IDisposable
{
    public void InitModels(int numThread = 0);                                  // bundled v5 latin, RELATIVE paths "models/v5/..."
    public void InitModels(SessionOptions op);
    public void InitModels(string detPath, string clsPath, string recPath, string keysPath, int numThread = 0);
    public void InitModels(string detPath, string clsPath, string recPath, string keysPath, SessionOptions op);
    public void InitModels(RapidOcrModelSet models, int numThread = 0);
    public void InitModels(RapidOcrModelSet models, SessionOptions op);
    public OcrResult Detect(SKBitmap originSrc, RapidOcrOptions options, CancellationToken cancellationToken = default);
    public IReadOnlyList<TextBox> DetectBoxes(SKBitmap originSrc, RapidOcrOptions options, CancellationToken ct = default);
    public Task<OcrResult> DetectAsync(SKBitmap originSrc, RapidOcrOptions options, CancellationToken ct = default);
    public static SessionOptions GetDefaultSessionOptions(int numThread = 0);   // ORT_ENABLE_EXTENDED, inter = intra = numThread
}
```

`RapidOcrOptions` (default tra parentesi): `Padding` (0), `ImgResize` (0), `LimitSideLen` (736), `MaxSideLen` (2000),
`MinSideLen` (30), `TextScore` (0.5), `BoxScoreThresh` (0.5), `BoxThresh` (0.3), `UnClipRatio` (1.6), `DoAngle` (true),
`ClsThresh` (0.9), `RecMaxDegreeOfParallelism` (1), `ReturnWordBox` (false). Preset: `Default` (Padding=50,
ImgResize=1024, pensato per i v5 inclusi), `PythonCompat` (nessun bordo, lato corto adattivo 736 = `limit_type: min`
di RapidOCR python), `PPOCRv6` (equivalente a PythonCompat). Model set: `RapidOcrModelSet.PPOCRv5Latin`,
`PPOCRv6Tiny`, `PPOCRv6Small`, `PPOCRv6Medium` (i v6 riusano il classificatore v5 perche' PP-OCRv6 non ne ha uno suo).
Il pacchetto copia i modelli in `<output>/models/v5/` tramite `build/RapidOcrNet.targets` (anche su `Publish`).

Schizzo di integrazione (core portabile + adattatore):

```csharp
// ---- Core (portable) ----
public readonly record struct OcrLine(string Text, float Confidence, RectF Bounds);

public interface IOcrEngine : IDisposable
{
    /// pixels: BGRA 8-bit, top-down, stride = width * 4. Returns lines sorted in reading order.
    Task<IReadOnlyList<OcrLine>> RecognizeAsync(ReadOnlyMemory<byte> bgra, int width, int height,
                                                PointF? focus, CancellationToken ct);
}

// ---- Adapter (still portable: SkiaSharp + ORT exist on macOS too) ----
public sealed class RapidOcrEngine : IOcrEngine
{
    private readonly RapidOcr _ocr = new();
    private readonly SemaphoreSlim _gate = new(1, 1);          // do not assume RapidOcr is re-entrant
    private readonly RapidOcrOptions _opt;

    public RapidOcrEngine(string modelsDir, bool useV6)
    {
        // ABSOLUTE paths: the preset paths are relative to the current directory, which for a tray app
        // started from a shortcut / autostart is NOT the exe folder.
        string v5 = Path.Combine(modelsDir, "v5"), v6 = Path.Combine(modelsDir, "v6");
        var set = useV6
            ? RapidOcrModelSet.PPOCRv6Small with {
                  DetModelPath = Path.Combine(v6, "PP-OCRv6_det_small.onnx"),
                  RecModelPath = Path.Combine(v6, "PP-OCRv6_rec_small.onnx"),
                  KeysPath     = Path.Combine(v6, "ppocrv6_dict.txt"),
                  ClsModelPath = Path.Combine(v5, "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx") }
            : RapidOcrModelSet.PPOCRv5Latin with {
                  DetModelPath = Path.Combine(v5, "ch_PP-OCRv5_mobile_det.onnx"),
                  RecModelPath = Path.Combine(v5, "latin_PP-OCRv5_rec_mobile_infer.onnx"),
                  KeysPath     = Path.Combine(v5, "ppocrv5_latin_dict.txt"),
                  ClsModelPath = Path.Combine(v5, "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx") };

        using var so = new SessionOptions {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = 4, InterOpNumThreads = 1 };
        _ocr.InitModels(set, so);

        _opt = RapidOcrOptions.PythonCompat with { DoAngle = false, TextScore = 0.5f };
    }

    public async Task<IReadOnlyList<OcrLine>> RecognizeAsync(ReadOnlyMemory<byte> bgra, int w, int h,
                                                             PointF? focus, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque));
            bgra.Span.CopyTo(bmp.GetPixelSpan());

            if (focus is null)                                   // scene text: read everything in the zone
                return Map(await _ocr.DetectAsync(bmp, _opt, ct));

            // Two-phase: detect all boxes, keep the one under / nearest to the pointer, recognize only that crop.
            var boxes = await _ocr.DetectBoxesAsync(bmp, _opt, ct);
            var hit = PickNearest(boxes, focus.Value);           // point-in-polygon first, then min distance
            if (hit is null) return [];
            SKRectI r = Inflate(BoundsOf(hit.BoxPoints), 12, w, h);
            using var crop = new SKBitmap(r.Width, r.Height);
            bmp.ExtractSubset(crop, r);
            return Map(await _ocr.DetectAsync(crop, _opt, ct), offset: r.Location);
        }
        finally { _gate.Release(); }
    }
}
```

Nota: RapidOcrNet **non espone** un'API "solo riconoscimento" su un ritaglio gia' pronto; il secondo `Detect()` sul
ritaglio piccolo rifa' anche la detection, ma su un'immagine minuscola costa pochi ms. Se servisse, una PR o un fork
leggero (la classe `TextRecognizer` e' interna) e' fattibile: licenza Apache-2.0.

**Filtro caratteri per il TTS** (obbligatorio con v6, innocuo con v5):

```csharp
static string KeepLatin(string s)
{
    var sb = new StringBuilder(s.Length);
    foreach (var rune in s.EnumerateRunes())
    {
        int c = rune.Value;
        bool ok = c is >= 0x20 and <= 0x7E          // Basic Latin
               || c is >= 0xA0 and <= 0x24F         // Latin-1 Supplement + Latin Extended-A/B (à è é ì ò ù ...)
               || c is >= 0x2010 and <= 0x203A      // dashes, quotes, ellipsis
               || c == 0x20AC;                      // euro sign
        if (ok) sb.Append(rune.ToString());
    }
    return sb.ToString();
}
```

### 4. Modelli: cosa, dove, quanto pesano

Tutte le dimensioni e gli SHA256 qui sotto sono stati **verificati il 21/9/2026** con richieste HTTP Range/HEAD
(`Content-Range` e header `X-Linked-ETag`), e coincidono con gli hash dichiarati in
`RapidAI/RapidOCR/python/rapidocr/default_models.yaml`.

Base URL ModelScope (repository `RapidAI/RapidOCR`, tag `v3.9.2`):
`https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2/`

| File (percorso relativo alla base) | Byte | SHA256 |
|---|---|---|
| `onnx/PP-OCRv5/det/ch_PP-OCRv5_det_mobile.onnx` | 4.819.576 | `4d97c44a20d30a81aad087d6a396b08f786c4635742afc391f6621f5c6ae78ae` |
| `onnx/PP-OCRv5/rec/latin_PP-OCRv5_rec_mobile.onnx` | 7.904.513 | `b20bd37c168a570f583afbc8cd7925603890efbcdc000a59e22c269d160b5f5a` |
| `onnx/PP-OCRv5/rec/en_PP-OCRv5_rec_mobile.onnx` | 7.872.351 | `c3461add59bb4323ecba96a492ab75e06dda42467c9e3d0c18db5d1d21924be8` |
| `onnx/PP-OCRv5/cls/ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx` | 1.018.508 | `54379ae5174d026780215fc748a7f31910dee36818e63d49e17dc598ecc82df7` |
| `paddle/PP-OCRv5/rec/latin_PP-OCRv5_rec_mobile/ppocrv5_latin_dict.txt` | 1.634 (502 righe) | - |
| `onnx/PP-OCRv6/det/PP-OCRv6_det_tiny.onnx` | 1.829.618 | `f42c0fbd294d95eac1a550e131b277dac97462c8025fa4b6c3cec1b7894bd3d5` |
| `onnx/PP-OCRv6/det/PP-OCRv6_det_small.onnx` | 9.929.594 | `090f04abcd9d9a7498bc4ebf677e4cb9bdce1fe4197ddb7e529f1ef44e1ff94f` |
| `onnx/PP-OCRv6/rec/PP-OCRv6_rec_tiny.onnx` | 4.489.813 | `e16e242de5937ad92609223f19bc2aff3727ee40b095f996907c24749bad251b` |
| `onnx/PP-OCRv6/rec/PP-OCRv6_rec_small.onnx` | 21.234.383 | `6f327246b50388f3c176ae304bd95767ea6dc0c9ae92153ef8cbe210b3c14884` |
| `paddle/PP-OCRv6/rec/PP-OCRv6_rec_small/ppocrv6_dict.txt` | 74.947 (18.708 righe) | - |
| `paddle/PP-OCRv6/rec/PP-OCRv6_rec_tiny/ppocrv6_tiny_dict.txt` | 27.156 (6.904 righe) | - |

Attenzione ai **nomi diversi**: dentro il NuGet RapidOcrNet i file si chiamano `ch_PP-OCRv5_mobile_det.onnx` e
`latin_PP-OCRv5_rec_mobile_infer.onnx`; su ModelScope `ch_PP-OCRv5_det_mobile.onnx` e `latin_PP-OCRv5_rec_mobile.onnx`.
I modelli ONNX di RapidAI contengono anche il dizionario nei metadati ONNX (chiave `character`, letta in python con
`custom_metadata_map`); RapidOcrNet pero' legge il dizionario da file `.txt`.

**Fonti ufficiali PaddlePaddle:**

- PP-OCRv5 (rilascio maggio 2025): su Hugging Face `PaddlePaddle/latin_PP-OCRv5_mobile_rec` c'e' **solo il formato
  Paddle** (`inference.pdiparams` 7.965.915 byte + `inference.json` + `inference.yml` col dizionario), licenza
  `apache-2.0`. Tar ufficiale:
  `https://paddle-model-ecology.bj.bcebos.com/paddlex/official_inference_model/paddle3.0.0/latin_PP-OCRv5_mobile_rec_infer.tar`.
  Per avere ONNX servirebbe `paddle2onnx` (Python, x64): evitarlo, usare la conversione RapidAI.
  Lingue dichiarate del modello latin: "French, German, Afrikaans, **Italian**, Spanish, Bosnian, Portuguese, ...
  Catalan, Quechua" (47 lingue), accuratezza 84,7% sul dataset latin. `en_PP-OCRv5_mobile_rec`: solo inglese, 85,25%.
- **PP-OCRv6** (rilascio 11 giugno 2026; blog HF 22/6/2026): tre taglie tiny/small/medium (1,5 M / 7,7 M / 34,5 M
  parametri), **un solo modello per 50 lingue** (cinese semplificato/tradizionale, inglese, giapponese + 46 lingue a
  scrittura latina; tiny = 49, senza giapponese). PaddlePaddle pubblica **ONNX ufficiali** su Hugging Face, licenza
  apache-2.0, ultima modifica 18/6/2026:
  - `PaddlePaddle/PP-OCRv6_small_det_onnx` -> `inference.onnx` 9.880.512 byte
  - `PaddlePaddle/PP-OCRv6_small_rec_onnx` -> `inference.onnx` 21.159.378 byte (sha256 `5435fd74...4634`), `inference.yml` 150.579 byte (contiene `character_dict`)
  - `PaddlePaddle/PP-OCRv6_tiny_det_onnx` -> 1.780.590 byte; `PaddlePaddle/PP-OCRv6_tiny_rec_onnx` -> 4.462.639 byte
  - URL diretto tipo: `https://huggingface.co/PaddlePaddle/PP-OCRv6_small_rec_onnx/resolve/main/inference.onnx`
  Gli ONNX ufficiali e quelli RapidAI **non sono lo stesso file** (hash e dimensioni diversi: RapidAI riconverte e
  aggiunge metadati). Per RapidOcrNet conviene la coppia RapidAI (onnx + dict .txt gia' pronto).
- Accuratezza dichiarata (dataset interni v6): det Hmean tiny 80,6 / small 84,1 / medium 86,2 (PP-OCRv5 mobile 79,0);
  rec tiny 73,5 / small 81,3 / medium 83,2.

Licenze: modelli PaddleOCR Apache-2.0 (model card HF), RapidOCR Apache-2.0, RapidOcrNet Apache-2.0, ONNX Runtime MIT,
SkiaSharp MIT, Clipper2 Boost Software License 1.0. Nella cartella portabile mettere un `THIRD-PARTY-NOTICES.txt`.

**Conversioni di terzi su Hugging Face** (`monkt/paddleocr-onnx`, `marsena/paddleocr-onnx-models`,
`ilaylow/PP_OCRv5_mobile_onnx`): esistono ma non sono ufficiali; non usarle come fonte primaria.

### 5. Download dei modelli a build-time

Per il default non serve nulla (i v5 latin arrivano dal NuGet). Per i v6 small, target MSBuild con i task integrati
`DownloadFile` e `VerifyFileHash`:

```xml
<!-- PuntaEAscolta.Ocr.csproj -->
<PropertyGroup>
  <OcrModelBase>https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2</OcrModelBase>
  <OcrModelCache>$(MSBuildThisFileDirectory)..\..\.models-cache\v6</OcrModelCache>
</PropertyGroup>

<ItemGroup>
  <OcrModel Include="PP-OCRv6_det_small.onnx" Url="$(OcrModelBase)/onnx/PP-OCRv6/det/PP-OCRv6_det_small.onnx"
            Sha256="090f04abcd9d9a7498bc4ebf677e4cb9bdce1fe4197ddb7e529f1ef44e1ff94f" />
  <OcrModel Include="PP-OCRv6_rec_small.onnx" Url="$(OcrModelBase)/onnx/PP-OCRv6/rec/PP-OCRv6_rec_small.onnx"
            Sha256="6f327246b50388f3c176ae304bd95767ea6dc0c9ae92153ef8cbe210b3c14884" />
  <OcrDict  Include="ppocrv6_dict.txt"        Url="$(OcrModelBase)/paddle/PP-OCRv6/rec/PP-OCRv6_rec_small/ppocrv6_dict.txt" />
</ItemGroup>

<Target Name="FetchOcrModels" BeforeTargets="AssignTargetPaths">
  <MakeDir Directories="$(OcrModelCache)" />
  <DownloadFile SourceUrl="%(OcrModel.Url)" DestinationFolder="$(OcrModelCache)" DestinationFileName="%(OcrModel.Identity)"
                SkipUnchangedFiles="true" Retries="3" Condition="!Exists('$(OcrModelCache)\%(OcrModel.Identity)')" />
  <DownloadFile SourceUrl="%(OcrDict.Url)" DestinationFolder="$(OcrModelCache)" DestinationFileName="%(OcrDict.Identity)"
                Condition="!Exists('$(OcrModelCache)\%(OcrDict.Identity)')" />
  <VerifyFileHash File="$(OcrModelCache)\%(OcrModel.Identity)" Algorithm="SHA256" Hash="%(OcrModel.Sha256)" />
  <ItemGroup>
    <None Include="$(OcrModelCache)\*.*" Link="models\v6\%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Target>
```

Alternativa piu' robusta (consigliata per build riproducibili e offline): **mettere i modelli nel repository**
(`assets/models/`, ~31 MB per v6 small; Apache-2.0 lo consente) e usare il target di download solo come script di
aggiornamento. Nota: l'app a runtime non deve mai scaricare nulla: OCR sempre offline.

### 6. Piano B: pipeline in C# puro senza OpenCV

Da usare solo se RapidOcrNet desse problemi su ARM64 o servisse controllo fine (mascheramento dei logit, rec-only,
bucket delle larghezze). Dipendenze: solo `Microsoft.ML.OnnxRuntime`; per il ridimensionamento si puo' usare SkiaSharp
oppure un bilineare scritto a mano sui byte BGRA (la cattura schermo e' gia' un buffer BGRA).

**6.1 Pre-elaborazione detection** (valori da `inference.yml` ufficiali):

- Canali in ordine **BGR** (`DecodeImage: img_mode: BGR`), layout NCHW float32.
- Normalizzazione: `scale = 1/255`, `mean = [0.485, 0.456, 0.406]`, `std = [0.229, 0.224, 0.225]` applicati *per indice
  di canale sull'immagine BGR* (cioe' 0.485 va sul canale B). Identico per PP-OCRv5 mobile det e PP-OCRv6 small det.
  (RapidOCR python e RapidOcrNet usano invece mean = std = 0.5 per i v6: vedi "Insidie".)
- Ridimensionamento: `limit_type = min`, `limit_side_len = 736` (scelta RapidOCR; ingrandisce le zone piccole, utile per
  testo UI minuscolo) oppure `resize_long: 960` (yml ufficiale v5). Poi ogni lato arrotondato al multiplo di 32:
  `max(32, round(side / 32) * 32)`. Conservare `ratioW = newW / srcW`, `ratioH = newH / srcH`.

```csharp
static float[] DetPreprocess(ReadOnlySpan<byte> bgra, int w, int h, int limitSide, out int nw, out int nh)
{
    float r = Math.Min(w, h) < limitSide ? limitSide / (float)Math.Min(w, h) : 1f;   // limit_type = min
    r = Math.Min(r, 2000f / Math.Max(w, h));                                         // hard cap on the long side
    nw = Math.Max(32, (int)MathF.Round(w * r / 32f) * 32);
    nh = Math.Max(32, (int)MathF.Round(h * r / 32f) * 32);
    byte[] resized = ResizeBilinearBgra(bgra, w, h, nw, nh);                         // or SkiaSharp

    ReadOnlySpan<float> mean = [0.485f, 0.456f, 0.406f], std = [0.229f, 0.224f, 0.225f];
    var t = new float[3 * nh * nw]; int plane = nh * nw;
    for (int i = 0, p = 0; i < plane; i++, p += 4)
        for (int c = 0; c < 3; c++)                                                  // c = 0:B 1:G 2:R (BGRA source)
            t[c * plane + i] = (resized[p + c] / 255f - mean[c]) / std[c];
    return t;
}
```

**6.2 Post-elaborazione DB** (output `[1,1,H,W]`, probabilita' 0..1):

Parametri ufficiali: PP-OCRv5 mobile det `thresh 0.3, box_thresh 0.6, unclip_ratio 1.5, max_candidates 1000`;
PP-OCRv6 small det `thresh 0.2, box_thresh 0.45, unclip_ratio 1.4, max_candidates 3000`. RapidOCR usa
`0.3 / 0.5 / 1.6`, `use_dilation: true`, `score_mode: fast`.

1. Binarizzare: `mask[i] = prob[i] > thresh`.
2. (Opzionale) dilatazione 2x2 per fondere lettere staccate.
3. Componenti connesse 8-vicini con flood fill iterativo (stack esplicito) o union-find a due passate: per ogni
   componente accumulare `minX, minY, maxX, maxY`, numero di pixel e somma delle probabilita'.
4. Scartare se `min(bw, bh) < 3`. Punteggio ("fast") = media di `prob` dentro il rettangolo; scartare se `< box_thresh`.
5. **Unclip** (DBNet restringe le regioni in training): `d = area * unclip_ratio / perimeter`. Per rettangolo
   asse-allineato `d = (bw * bh * ratio) / (2 * (bw + bh))`; espandere di `d` su ogni lato. (RapidOcrNet/RapidOCR fanno
   l'offset del poligono con Clipper2 `ClipperOffset`, `JoinType.Round`, poi rettangolo di area minima.)
6. Riportare alle coordinate sorgente dividendo per `ratioW/ratioH`, clamp ai bordi.
7. Ordinamento di lettura: per Y, e per X quando `|dy| < 10 px` (soglia RapidOCR).

Per l'uso a schermo (UI, documenti) i **rettangoli asse-allineati bastano**: il testo e' orizzontale. Per testo di scena
inclinato (foto di un cartello storto) serve il rettangolo di area minima: inviluppo convesso dei pixel di bordo
(monotone chain) + rotating calipers, poi ritaglio con warp affine bilineare (~60 righe). Consiglio: partire
asse-allineato, aggiungere il ruotato solo se i test sulle foto lo richiedono (DBNet + rec tollerano 5-10 gradi).

```csharp
static List<DetBox> DbPostprocess(ReadOnlySpan<float> prob, int W, int H, float thresh, float boxThresh, float unclip)
{
    var visited = new bool[W * H]; var stack = new Stack<int>(); var boxes = new List<DetBox>();
    for (int start = 0; start < prob.Length; start++)
    {
        if (visited[start] || prob[start] <= thresh) continue;
        int minX = W, minY = H, maxX = 0, maxY = 0;
        stack.Push(start); visited[start] = true;
        while (stack.Count > 0)
        {
            int i = stack.Pop(), x = i % W, y = i / W;
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
            {
                int nx = x + dx, ny = y + dy; if ((uint)nx >= (uint)W || (uint)ny >= (uint)H) continue;
                int j = ny * W + nx; if (visited[j] || prob[j] <= thresh) continue;
                visited[j] = true; stack.Push(j);
            }
        }
        int bw = maxX - minX + 1, bh = maxY - minY + 1; if (Math.Min(bw, bh) < 3) continue;
        float sum = 0; for (int y = minY; y <= maxY; y++) for (int x = minX; x <= maxX; x++) sum += prob[y * W + x];
        float score = sum / (bw * bh); if (score < boxThresh) continue;
        float d = bw * bh * unclip / (2f * (bw + bh));
        boxes.Add(new DetBox(minX - d, minY - d, maxX + 1 + d, maxY + 1 + d, score));
    }
    return boxes;
}
```

**6.3 Ritaglio.** Copia del rettangolo dalla sorgente a piena risoluzione (non dall'immagine ridimensionata). Se
`h / w >= 1.5` PaddleOCR ruota di 90 gradi (testo verticale): a schermo si puo' ignorare.

**6.4 Pre-elaborazione recognition** (`RecResizeImg: image_shape [3, 48, 320]`, identico v5 latin e v6):

- Altezza fissa **48**, larghezza `w' = ceil(48 * w / h)` mantenendo le proporzioni; limite superiore 3200
  (forma dinamica massima dichiarata `[8, 3, 48, 3200]`).
- Normalizzazione `(px / 255 - 0.5) / 0.5`, ordine BGR, NCHW.
- Come PaddleOCR/RapidOCR: tensore largo `max(320, w')`, con **zero a destra** oltre `w'`. (RapidOcrNet non fa il
  padding e passa la larghezza esatta: funziona, ma parole cortissime possono rendere meglio col padding.)
- Consiglio: arrotondare la larghezza del tensore a multipli di 32/64 ("bucket") per limitare il numero di forme
  diverse viste da ORT.

**6.5 Decodifica CTC greedy.** Output `[1, T, C]`, gia' softmax. `C = righe_dizionario + 2`: indice **0 = blank**,
indici `1..N` = righe del dizionario nell'ordine del file, indice `N+1` = **spazio** (`use_space_char`; il file non
contiene la riga dello spazio: verificato per tutti e tre i dizionari). Per latin v5: 502 + 2 = 504. RapidOcrNet fa
esattamente `keys = ["#"] + righe + [" "]`.

```csharp
static (string Text, float Confidence) CtcGreedyDecode(ReadOnlySpan<float> probs, int T, int C,
                                                         string[] keys /* [0]=blank, [^1]=" " */, bool[]? allowed = null)
{
    var sb = new StringBuilder(); float sum = 0; int n = 0, last = 0;
    for (int t = 0; t < T; t++)
    {
        var row = probs.Slice(t * C, C); int best = 0; float bestP = row[0];
        for (int c = 1; c < C; c++)
        {
            if (allowed is not null && !allowed[c]) continue;   // logit masking: Latin-only subset for the v6 dictionary
            if (row[c] > bestP) { bestP = row[c]; best = c; }
        }
        if (best != 0 && best != last) { sb.Append(keys[best]); sum += bestP; n++; }
        last = best;
    }
    return (sb.ToString(), n == 0 ? 0f : sum / n);
}
```

Il **mascheramento** (`allowed`) e' il vantaggio principale dell'implementazione propria con i modelli v6: invece di
filtrare *dopo* (perdendo il carattere), si sceglie il miglior carattere *latino* a ogni passo. Verificare
`keys.Length == C` all'avvio e fallire subito se non coincide (dizionario sbagliato = testo spazzatura).

**6.6 Classificatore d'angolo.** `ch_PP-LCNet_x0_25_textline_ori_cls_mobile` (1,0 MB), input `[3, 48, 192]`, etichette
`["0", "180"]`, soglia 0.9. Distingue solo 0/180 gradi. **Saltarlo**: a schermo il testo capovolto non esiste; fa
risparmiare un'inferenza per riga. PaddleOCR 3.x stesso lo mostra disattivato nel quick-start v6
(`use_textline_orientation=False`).

### 7. Latenza attesa su CPU

Dati ufficiali PaddleOCR (Intel Xeon Gold 6271C 2,6 GHz, 8 thread, motore Paddle, modalita' normale / alte prestazioni):

- `PP-OCRv5_mobile_det`: **57,77 / 28,15 ms** per immagine; `PP-OCRv5_server_det`: 383 ms (da evitare).
- `latin_PP-OCRv5_mobile_rec` / `PP-OCRv5_mobile_rec`: **21,20 / 5,32 ms** per riga.

Benchmark end-to-end PP-OCRv6 (200 immagini intere, I/O e pre/post inclusi, secondi per immagine):

| Hardware / backend | v6 medium | v6 small | v6 tiny | v5 server | v5 mobile |
|---|---|---|---|---|---|
| Intel Xeon 8350C / ONNX Runtime | 3,31 | 0,61 | 0,22 | 6,36 | 0,61 |
| Intel Xeon 8350C / OpenVINO | 1,40 | 0,59 | 0,20 | 7,30 | 0,78 |
| Apple M4 / ONNX Runtime | 5,55 | 1,29 | 0,35 | 7,20 | 1,10 |

Sono immagini intere con molte righe. **Stima** (non misurata) per una zona ~1000x600 (portata a ~1216x736) su un laptop
recente (Snapdragon X nativo ARM64 o Core Ultra, 4 thread), sessioni calde:

- detection: 60-200 ms;
- recognition: 10-30 ms per riga -> 5-20 righe = 50-500 ms in sequenza (meno con `RecMaxDegreeOfParallelism` 2-4);
- **totale pipeline completa: circa 150-600 ms; strategia a due fasi (det + 1-3 righe): circa 100-250 ms**;
- v6 small: simile a v5 mobile secondo la tabella; v6 tiny circa 3 volte piu' veloce ma meno accurato (73,5%).

Costi una tantum: caricamento delle sessioni 100-400 ms complessivi e prima inferenza 2-5 volte piu' lenta -> creare le
sessioni all'avvio dell'app e fare un'inferenza di warm-up su un'immagine finta. Medium/server: esclusi (secondi).

### 8. Testo piccolo della UI (Affinity) e ruolo rispetto a Windows.Media.Ocr

- DBNet lavora bene con altezza delle maiuscole di almeno ~12-16 px nell'immagine di ingresso. Il testo dei menu a
  scala 100% e' alto 9-11 px: **ingrandire 2x-3x** la cattura prima dell'OCR (bicubico/Lanczos; con SkiaSharp
  `SKSamplingOptions(SKCubicResampler.Mitchell)`). Il preset `limit_type = min, 736` ingrandisce gia' le zone con lato
  corto < 736 px: per una zona UI 600x200 significa ~3,7x, ottimo; per 1000x600 solo 1,23x, quindi per la UI conviene
  catturare una zona piu' piccola attorno al puntatore (es. 640x240) piuttosto che 1000x600.
- Catturare in pixel fisici (processo per-monitor DPI aware v2), altrimenti la cattura arriva gia' riscalata e sfocata.
- UI scura di Affinity (testo chiaro su fondo scuro): PP-OCR in genere regge entrambe le polarita'; tenere
  come esperimento l'inversione dell'immagine quando la luminanza media e' bassa.
- "Secondo parere": eseguire Windows.Media.Ocr e PP-OCR sulla stessa zona e preferire il risultato con confidenza
  media piu' alta / piu' caratteri alfabetici; PP-OCR da' `CharScores` per carattere, Windows.Media.Ocr no -> la regola
  di arbitraggio va tarata empiricamente.

---

## Insidie note

1. **Percorsi relativi dei modelli in RapidOcrNet.** `InitModels()` senza argomenti e i preset `RapidOcrModelSet.*`
   usano `models/v5/...` relativo alla *directory corrente*, non alla cartella dell'exe. Un'app tray avviata da
   collegamento o esecuzione automatica ha spesso CWD diversa -> `FileNotFound`/`OnnxRuntimeException`. Usare sempre
   percorsi assoluti da `AppContext.BaseDirectory`.
2. **`onnxruntime.dll` di sistema.** Windows ha una vecchia `onnxruntime.dll` in `System32` (Windows ML in-box). Se il
   nativo giusto non e' accanto all'app (publish con RID sbagliato, single-file mal configurato), il loader puo'
   prendere quella e si ottengono errori tipo "entry point not found" / versione API non supportata (issue ORT #15375).
   Verificare che in output ci sia `onnxruntime.dll` dell'architettura giusta; evitare `PublishSingleFile` o usare
   `IncludeNativeLibrariesForSelfExtract`.
3. **Processo nativo ARM64.** Il nativo `win-arm64` viene caricato solo se il processo e' ARM64. Pubblicare con RID
   esplicito (`-r win-arm64`), non "AnyCPU" avviato da un host x64. Una build x64 gira comunque su Snapdragon sotto
   emulazione Prism ma l'inferenza e' sensibilmente piu' lenta.
4. **Visual C++ runtime.** ORT su Windows richiede il "Visual C++ 2019 runtime" o successivo. Su un PC ARM64 pulito il
   redistributable ARM64 puo' mancare: per la cartella portabile copiare app-local `msvcp140.dll`, `vcruntime140.dll`,
   `vcruntime140_1.dll` dell'architettura giusta (sono nei VS Build Tools 2022 gia' installati, cartella
   `VC\Redist\MSVC\<ver>\{x64,arm64}\Microsoft.VC143.CRT`).
5. **Ordine dei canali BGR.** I modelli PP-OCR sono addestrati su immagini BGR (OpenCV). RapidOcrNet ha avuto proprio
   questo bug (issue #23, chiusa il 22/4/2026). Nell'implementazione propria e' un vantaggio: la cattura schermo e' gia'
   BGRA. L'errore non produce crash, solo accuratezza peggiore: difficile da notare.
6. **Normalizzazione det incoerente tra fonti.** Gli `inference.yml` ufficiali (v5 mobile det e v6 small det) indicano
   mean/std ImageNet; RapidOCR python usa mean = std = 0.5 nel suo `config.yaml`, e RapidOcrNet usa ImageNet per v5 e
   0.5 per v6. DBNet e' tollerante, ma soglie e qualita' possono spostarsi. Nell'implementazione propria seguire lo yml
   ufficiale e fare un A/B.
7. **Dizionario: blank e spazio.** Indice 0 = blank CTC, spazio aggiunto *in coda* dal codice, non presente nel file.
   Sbagliare di uno l'offset produce testo plausibile ma sbagliato (ogni lettera slittata). Controllare
   `C == righe + 2` all'avvio. Attenzione a BOM UTF-8 e a `ReadAllLines` che scarta righe: leggere in UTF-8 senza trim
   (alcune righe del dizionario sono caratteri "strani" come lo spazio ideografico U+3000).
8. **Dizionario v6 multilingue (18.708 classi).** Su rumore/icone il modello puo' emettere ideogrammi o kana, che il
   TTS italiano leggerebbe male o in modo imbarazzante. Filtro latino obbligatorio (o mascheramento nel piano B).
   Con v5 latin (502 classi) il problema non esiste: e' un argomento a favore di v5 come default.
9. **Icone scambiate per testo.** Nelle barre strumenti di Affinity/Photoshop DBNet trova "testo" dentro le icone e il
   rec produce stringhe tipo "O", "l1", "=". Filtrare: confidenza media >= 0,6-0,7, almeno 2 caratteri alfabetici,
   e non pronunciare nulla piuttosto che rumore (coerente col requisito "solo audio").
10. **Forme dinamiche e memoria.** Ogni nuova forma di input fa riallocare l'arena di ORT; con larghezze rec sempre
    diverse la memoria cresce. `EnableCpuMemArena = false` (come RapidOCR python: `enable_cpu_mem_arena: false`) e/o
    bucket delle larghezze.
11. **Thread.** Il default di ORT usa tutti i core fisici: puo' far "gracchiare" l'audio NAudio se il TTS parte in
    parallelo. Limitare a 4 thread intra-op. `GetDefaultSessionOptions(numThread)` di RapidOcrNet imposta *anche*
    inter-op allo stesso valore: meglio passare `SessionOptions` proprie.
12. **Rientranza di `RapidOcr`.** Nessuna garanzia documentata di thread-safety dell'istanza: serializzare con
    `SemaphoreSlim` e usare il `CancellationToken` (RapidOcrNet annulla anche dentro l'inferenza via
    `RunOptions.Terminate`) per il "secondo clic = stop".
13. **SkiaSharp 4.x.** E' uscita SkiaSharp 4.152.1 (17/9/2026); RapidOcrNet e' compilato contro 3.119.1. Non forzare
    la major 4 (possibili rotture binarie). Se un'altra parte dell'app usa SkiaSharp, allineare alla 3.119.x.
14. **`PackageRequireLicenseAcceptance = True`** in RapidOcrNet: alcuni flussi NuGet chiedono conferma interattiva
    (in Visual Studio); `dotnet restore` da riga di comando non blocca.
15. **Emgu.CV = GPLv3/commerciale.** Non adottare `RapidOCRLib` ufficiale ne' OcrLiteOnnxCs senza valutare la licenza.
16. **ModelScope** e' un servizio cinese (Alibaba): dall'Italia oggi risponde (verificato) ma non dare per scontata la
    disponibilita' futura dei tag (`v3.9.2`). Tenere copia dei modelli nel repository o in un rilascio GitHub proprio.
17. **Requisito locale di ORT**: la doc cita "English language package with en_US.UTF-8 locale" (rilevante su Linux; su
    Windows italiano ORT funziona, ma nel nostro codice usare sempre `CultureInfo.InvariantCulture` per parse/format).

---

## Domande aperte da verificare sulla macchina

1. **Smoke test ARM64 end-to-end** appena installato il .NET 10 SDK: console `net10.0`, `dotnet add package RapidOcrNet
   --version 4.2.0`, `dotnet run -r win-arm64` su uno screenshot di Affinity. Controllare con Process Explorer che
   `onnxruntime.dll` e `libSkiaSharp.dll` caricati siano quelli in `runtimes/win-arm64/native` (e non System32).
2. **Latenze reali** su Snapdragon X (nativo) e su un Intel x64: zona 1000x600 e zona 640x240, v5 latin vs v6 small vs
   v6 tiny, 1/2/4/8 thread, con e senza `DoAngle`. `OcrResult.DbNetTime`, `TextBlock.CrnnTime` danno gia' i parziali.
3. **Python sulla macchina: attenzione.** Il `python` nel PATH riporta `MSC v.1943 64 bit (AMD64)` con
   `platform.machine() == 'ARM64'`: e' la build **x64 sotto emulazione**, non ARM64 nativa (ha gia' `onnxruntime` 1.23.2,
   `numpy`, `PIL`). Un benchmark fatto li' sottostima le prestazioni native. Se si vuole un test rapido prima del .NET SDK,
   usare un Python ARM64 nativo (esistono wheel `onnxruntime-1.30.0-cp313-cp313-win_arm64.whl`).
4. **Qualita' sul testo piccolo di Affinity v3**: a 100%/125%/150% di scala, tema scuro e chiaro, con upscaling 1x/2x/3x;
   confronto con Windows.Media.Ocr sulle stesse catture. Misura: percentuale di voci di menu lette esattamente.
5. **v5 latin vs v6 small** su: accenti italiani (`perche'` con accento, `piu'`, `e'` maiuscola accentata), parole
   inglesi della UI, numeri con unita' (`12 px`, `100 %`), testo di scena (cartelli, magliette) inclinato 0-20 gradi.
   Verificare se v6 emette caratteri CJK spurii e quanto spesso.
6. **mean/std della detection v6**: ImageNet (yml ufficiale) contro 0.5/0.5 (RapidOCR/RapidOcrNet): quale da' piu'
   recall sul testo piccolo? (Richiede piano B o una modifica a `RapidOcrModelSet.DetMean/DetStd`, che sono `init`
   pubblici: si puo' provare con `with { DetMean = ..., DetStd = ... }` senza fork.)
7. **Dizionario v6**: l'ordine di `ppocrv6_dict.txt` (RapidAI) coincide con `character_dict` dell'`inference.yml`
   ufficiale HF? Serve solo se si vogliono usare gli ONNX ufficiali PaddlePaddle invece di quelli RapidAI.
8. **VC++ runtime** su PC ARM64 "pulito" (quello di Matteo): ORT e libSkiaSharp partono senza redistributable
   installato? Se no, copiare le DLL CRT app-local (vedi Insidie, punto 4).
9. **`DownloadFile` di MSBuild e redirect 302 di ModelScope**: verificare che segua il redirect e che
   `VerifyFileHash` passi; in caso contrario ripiegare su uno script PowerShell (`Invoke-WebRequest` + `Get-FileHash`).
10. **Dimensione della cartella portabile** per architettura: ORT nativo + libSkiaSharp + modelli (v5: ~14 MB; v6 small:
    +31 MB) + runtime .NET self-contained con WPF.
11. **Memoria a regime** con tre sessioni vive (det, rec, eventualmente cls non inizializzato): obiettivo < 250 MB.
    Verificare se RapidOcrNet permette di non caricare affatto il cls quando `DoAngle = false` (oggi `InitModels` lo
    carica sempre: costo ~1 MB di modello, trascurabile).
12. **Concorrenza audio**: con 4 thread ORT attivi, NAudio/TTS ha glitch? Eventualmente abbassare la priorita' del
    thread OCR o ridurre a 2-3 thread.

---

## Fonti

ONNX Runtime
- NuGet `Microsoft.ML.OnnxRuntime` 1.30.0 (10/9/2026): https://www.nuget.org/packages/Microsoft.ML.OnnxRuntime
- Doc C# (tabella piattaforme: "X64, X86 (Windows-only), ARM64 (Windows-only)"): https://onnxruntime.ai/docs/get-started/with-csharp.html
- Requisiti d'installazione (VC++ runtime, tabella NuGet, DirectML "sustained engineering"): https://onnxruntime.ai/docs/install/
- Script nuspec con `runtimes/win-{x86,x64,arm,arm64}/native`: https://github.com/microsoft/onnxruntime/blob/main/tools/nuget/generate_nuspec_for_native_nuget.py
- Release ORT: https://github.com/microsoft/onnxruntime/releases
- QNN EP (solo modelli quantizzati su HTP, niente forme dinamiche): https://onnxruntime.ai/docs/execution-providers/QNN-ExecutionProvider.html
- NuGet `Microsoft.ML.OnnxRuntime.QNN` 1.24.4 (17/3/2026): https://www.nuget.org/packages/Microsoft.ML.OnnxRuntime.QNN
- DirectML EP ("DirectML is in sustained engineering"): https://onnxruntime.ai/docs/execution-providers/DirectML-ExecutionProvider.html
- NuGet `Microsoft.ML.OnnxRuntime.DirectML` 1.24.4: https://www.nuget.org/packages/Microsoft.ML.OnnxRuntime.DirectML
- Windows ML overview (ms.date 1/9/2026): https://learn.microsoft.com/en-us/windows/ai/new-windows-ml/overview
- KleidiAI in ORT: https://onnxruntime.ai/blogs/arm-microsoft-kleidiai
- Snapdragon X non riconosciuto da cpuinfo (cosmetico): https://github.com/microsoft/foundry-local/issues/492
- `onnxruntime.dll` di System32: https://github.com/microsoft/onnxruntime/issues/15375 e https://learn.microsoft.com/en-in/answers/questions/2124577/how-to-reference-the-native-onnxruntime-dll-in-my
- PyPI `onnxruntime` 1.30.0 (wheel win_arm64): https://pypi.org/project/onnxruntime/

Librerie C#
- RapidOcrNet repo: https://github.com/BobLd/RapidOcrNet - NuGet 4.2.0 (9/9/2026): https://www.nuget.org/packages/RapidOcrNet
- RapidOcrNet sorgenti letti: https://github.com/BobLd/RapidOcrNet/blob/master/RapidOcrNet/RapidOcr.cs , `RapidOcrOptions.cs`, `RapidOcrModelSet.cs`, `TextDetector.cs`, `TextRecognizer.cs`, `RapidOcrNet.csproj`, `RapidOcrNet.targets`
- RapidOcrNet issue (BGR/RGB #23, CPU lenta #14, v6 #29): https://github.com/BobLd/RapidOcrNet/issues?q=is%3Aissue
- RapidOCRCSharp ufficiale: https://github.com/RapidAI/RapidOCRCSharp - NuGet `RapidOCRLib` 0.9.5: https://www.nuget.org/packages/RapidOCRLib
- Licenza Emgu CV (GPL/commerciale): https://www.emgu.com/wiki/index.php/Licensing:
- Sdcb PaddleSharp: https://github.com/sdcb/PaddleSharp - release: https://github.com/sdcb/PaddleSharp/releases - runtime win64: https://www.nuget.org/packages/Sdcb.PaddleInference.runtime.win64.mkl/
- PaddleOCRSharp: https://github.com/raoyutian/PaddleOCRSharp
- OcrLiteOnnxCs: https://gitee.com/benjaminwan/ocr-lite-onnx-cs - OcrLiteOnnx (C++): https://github.com/benjaminwan/OcrLiteOnnx
- OpenCvSharp: https://github.com/shimat/opencvsharp - `OpenCvSharp5.runtime.win-arm64` 5.0.0.20260905: https://www.nuget.org/packages/OpenCvSharp5.runtime.win-arm64 - `OpenCvSharp4.runtime.win`: https://www.nuget.org/packages/OpenCvSharp4.runtime.win
- SkiaSharp 4.152.1 (17/9/2026): https://www.nuget.org/packages/SkiaSharp - nativi Win32: https://www.nuget.org/packages/SkiaSharp.NativeAssets.Win32/ - ARM64 Windows dalla 2.80.0: https://github.com/mono/SkiaSharp/releases/tag/v2.80.0

Modelli
- Elenco modelli RapidOCR con URL e SHA256: https://github.com/RapidAI/RapidOCR/blob/main/python/rapidocr/default_models.yaml
- Config RapidOCR (soglie det, mean/std 0.5, arena disattivata): https://github.com/RapidAI/RapidOCR/blob/main/python/rapidocr/config.yaml
- Metadati `character` negli ONNX RapidAI: https://github.com/RapidAI/RapidOCR/blob/main/python/rapidocr/inference_engine/onnxruntime/main.py
- ModelScope RapidAI (base dei download): https://www.modelscope.cn/models/RapidAI/RapidOCR
- PP-OCRv5 multilingua (elenco lingue del modello latin, Italian incluso; tar ufficiale): https://github.com/PaddlePaddle/PaddleOCR/blob/main/docs/version3.x/algorithm/PP-OCRv5/PP-OCRv5_multi_languages.en.md
- HF `PaddlePaddle/latin_PP-OCRv5_mobile_rec` (apache-2.0): https://huggingface.co/PaddlePaddle/latin_PP-OCRv5_mobile_rec
- HF `PaddlePaddle/PP-OCRv5_mobile_det` (`inference.yml`): https://huggingface.co/PaddlePaddle/PP-OCRv5_mobile_det
- PP-OCRv6 introduzione e benchmark: http://www.paddleocr.ai/main/en/version3.x/algorithm/PP-OCRv6/PP-OCRv6.html
- Blog HF PP-OCRv6 (22/6/2026): https://huggingface.co/blog/PaddlePaddle/pp-ocrv6
- HF ONNX ufficiali v6: https://huggingface.co/PaddlePaddle/PP-OCRv6_small_det_onnx , https://huggingface.co/PaddlePaddle/PP-OCRv6_small_rec_onnx , https://huggingface.co/PaddlePaddle/PP-OCRv6_tiny_det_onnx , https://huggingface.co/PaddlePaddle/PP-OCRv6_tiny_rec_onnx
- Tabelle moduli PaddleOCR (tempi CPU det/rec, dimensioni): https://www.paddleocr.ai/latest/en/version3.x/module_usage/text_detection.html , https://www.paddleocr.ai/latest/en/version3.x/module_usage/text_recognition.html
- Conversioni ONNX di terzi (non ufficiali): https://huggingface.co/monkt/paddleocr-onnx , https://huggingface.co/marsena/paddleocr-onnx-models
