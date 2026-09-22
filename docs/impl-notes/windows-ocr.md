# PuntaEAscolta.Windows.Ocr: note di implementazione

Stato: completo, compila con 0 errori e 0 avvisi (`net10.0-windows10.0.19041.0`). Verificato il 22/09/2026 sullo Zenbook Snapdragon X (ARM64, 125%, solo it-IT installato).

## Cosa c'e

| File | Contenuto |
|---|---|
| `WindowsOcrEngine.cs` | `WindowsOcrEngine : IOcrEngine`, costruttore `(Func<string> languageTag, ILog log)`, `Name = "windows"`. In piu il metodo pubblico `Task<OcrResult> RecognizeLowContrastAsync(CapturedImage image, double x, double y, CancellationToken ct)` (secondo passaggio). |
| `OcrPreprocessor.cs` | Pre-trattamento GDI+ (interno): ingrandimento, margine, ritaglio, grigi + stiramento del contrasto, rispetto di `OcrEngine.MaxImageDimension`. |
| `PreparedImage.cs` | Immagine preparata + parametri per riportare i rettangoli alle coordinate originali (`ToOriginal`). |

### Primo passaggio: `RecognizeAsync`
- Fattore = `clamp(2.0 / DpiScale, 1.0, 3.0)` arrotondato a 0,25 (1,5x al 125%, 2x al 100%, 1,25x al 150%, 1x dal 200%). `DpiScale` non valido (0, NaN) vale 1,0.
- `HighQualityBicubic` + `PixelOffsetMode.HighQuality` + `WrapMode.TileFlipXY`; a fattore 1,0 copia esatta (`NearestNeighbor`).
- Margine 24 px per lato riempito con il colore piu frequente lungo il bordo della regione; il margine cresce (simmetrico) se serve a raggiungere 150x100 px.
- Colore conservato: nessuna inversione, nessuna gamma, nessun livello automatico.
- Se `max(lato) * fattore + 48 > MaxImageDimension` (10000 su questa build) il fattore viene ridotto quanto basta.

### Secondo passaggio: `RecognizeLowContrastAsync(image, x, y, ct)`
- `(x, y)` sono in **pixel dell'immagine passata** (le stesse coordinate dei `Box` restituiti), non di schermo. Il chiamante che ha il puntatore in `ScreenPoint` fa `x = p.X - image.ScreenBounds.X`.
- Revisione del 22/09/2026: **fascia alta 120 px e larga quanto l'immagine**, centrata sulla riga di (x, y); 2x; scala di grigi e stiramento fra il 1o e il 99,5o percentile della luminanza del **ritaglio 400x120 attorno al punto** (come misurato nella sonda: nessun tetto al guadagno, sotto 8 livelli di escursione la zona e piatta e non si stira). Prima il ritaglio era 400x120 anche in larghezza e le parole tagliate dai suoi lati tornavano come testo ("ta come PDF" per "Esporta come PDF..."); il chiamante non poteva riconoscerle perché quei lati non sono bordi della cattura. Ora i lati della fascia coincidono con quelli della cattura (li filtra `TextResolver.DropCutText`) e le righe che toccano i bordi superiore e inferiore della fascia vengono scartate qui (`OcrPreprocessor.DropLinesCutByCrop`). Stesso margine e stessa dimensione minima del primo passaggio. Costo misurato: 30-50 ms invece di 15-20 (zona 1125x450).
- Da usare solo se il primo passaggio non trova nulla sulla riga del puntatore: costa 12-22 ms.

### Comune ai due passaggi
- Motore: `TryCreateFromLanguage(languageTag())` -> `TryCreateFromUserProfileLanguages()` -> prima di `AvailableRecognizerLanguages`. `languageTag()` viene letto a ogni chiamata e il motore ricreato quando cambia (costa 0,5 ms). Tag non valido, vuoto o senza riconoscitore installato: `Warn` una volta e ripiego. Se il ripiego fallisce si resta sul motore precedente. `"it"` funziona (risolve in it-IT).
- Un solo riconoscimento alla volta (`SemaphoreSlim(1,1)`): chiamate concorrenti vengono messe in coda, non falliscono.
- Pixel al motore: `SoftwareBitmap.CreateCopyFromBuffer(Bgra8, BitmapAlphaMode.Ignore)`. Misurato: con alfa 255 e con alfa 0 i tre modi `Ignore`, `Premultiplied`, `Straight` danno risultati identici (36 righe sul menu File); si usa `Ignore` perche il pre-trattamento produce sempre alfa 255 e protegge comunque dalle catture con alfa 0. La sorgente viene letta come `Format32bppRgb` proprio per ignorare l'alfa delle catture.
- Risultato: `OcrLine.Text` = parole unite da spazi, `Box` = unione dei riquadri delle parole, `Confidence = null` (il motore non la fornisce). Parole vuote e righe senza parole vengono scartate. Coordinate: `(X - margine) / fattore + origineRitaglio`.
- **Inclinazione stimata dal motore** (revisione del 22/09/2026): quando `OcrResult.TextAngle` non è zero (capita anche con 3 gradi spuri su schermate d'interfaccia) i riquadri delle parole sono nel sistema raddrizzato; il centro di ogni riquadro viene ruotato di nuovo di `TextAngle` attorno al centro dell'immagine preparata (`WindowsOcrEngine.UndoTextAngle`). Senza questa correzione, in `affinity-lowcontrast-normal.png` le righe risultavano spostate di 9-24 px secondo la posizione del punto e il puntatore finiva sulla riga sbagliata; con la correzione le coordinate sono uguali in tutte le 36 posizioni provate. Sulle immagini della ricerca senza inclinazione i risultati del primo passaggio sono identici a prima.
- Eccezioni: verso il chiamante esce solo `OperationCanceledException` (token annullato, anche in coda sul semaforo). Ogni altro errore viene registrato (`Error`) e produce `OcrResult.Empty("windows")`. Immagine incoerente (buffer corto, lato 0) -> `Warn` + vuoto. Dopo `Dispose` -> vuoto, `IsAvailable = false`.
- Il testo riconosciuto finisce nel log solo con `ILog.IsDebugEnabled` (una riga con tempi, dimensioni e fattore).

## Decisioni e scoperte (misurate con la sonda)

1. **`OcrEngine.RecognizeAsync` di WinRT completa in modo sincrono sul thread chiamante** (97 ms bloccanti, `Task.IsCompleted = true` al ritorno). Per non bloccare mai il chiamante tutto il lavoro (GDI+ + OCR) parte con `Task.Run` sul pool: il chiamante riottiene il controllo in 0 ms e l'annullamento di una richiesta in coda funziona.
2. **`CompositingQuality.HighQuality` NON va impostato**: quadruplica il tempo del `DrawImage` (60 ms contro 16 ms su 900x300 -> 1,5x). Formato dei pixel sorgente/destinazione e `ImageAttributes` non influiscono (15-19 ms in tutte le combinazioni). `HighQualityBilinear` risparmierebbe solo 3 ms: si tiene il bicubico misurato nella ricerca.
3. Il semaforo non viene eliminato in `Dispose` (un'attesa in corso non deve trovarsi un oggetto eliminato; `SemaphoreSlim` senza `WaitHandle` non possiede risorse di sistema).
4. I buffer dell'immagine preparata vengono presi da `ArrayPool<byte>.Shared` e restituiti subito dopo la creazione della `SoftwareBitmap`.

## Numeri (Debug, macchina in uso, immagini in `docs/research/probe/`, `DpiScale = 1,25`)

Primo passaggio:

| Immagine | Preparata | Righe | Esito | Tempo totale (di cui pre-trattamento) |
|---|---|---|---|---|
| `affinity-menu-file-popup.png` 486x938 | 777x1455, 1,5x | 36 | **26/26 etichette**, 8/10 scorciatoie (`CtrI+Alt+H`, `Ctrl+p`: stessi errori della ricerca) | 120-170 ms (43-61) |
| `affinity-menu-visualizza-popup.png` 430x683 | 693x1072 | 21 | **19/19 etichette**, `Tab` ok, `CtrI+Maiusc+W` | 50-100 ms (15-36) |
| `affinity-ocr-geometry-crop1..6.png` 900x300 | 1398x498 | 13-21 | tutte le etichette intere corrette; errori tipici: `STRADE_vettorialesvg` (punto perso), `profiIe`, `Icasax3.af`, `2025Jpg`, `C.tr1+O` (riga tagliata dal bordo inferiore), frammenti al bordo sinistro (`mento`, `orse`, `211a in Esplora risorse`) | 40-110 ms (14-34) |

Riquadri verificati a occhio: `Home` a (26,7; 10,0) 39x11 px nel popup del menu File, testo alto 11-15 px, cioe le coordinate dell'immagine originale.

Secondo passaggio su `affinity-lowcontrast-normal.png` (attenzione: il file e gia il ritaglio 900x300 ingrandito 2x dalla sonda, 1800x600; provato com'e e riportato a 900x300):

| Punto (900x300) | Atteso | Passaggio 1 | Passaggio 2 | ms |
|---|---|---|---|---|
| (600,150) | S: 100 | nulla | **`s: 100`** (anche sulla 1800x600) | 17-21 |
| (850,164) | FF0000 | `0000` | `FFOOOO` | 22 |
| (832,214) | 100 % | `0 100%` | `0 100%` | 20 |
| (612,191) | Opacita | ok | ok | 17 |
| (595,132) | H: 0 | nulla | nulla | 13 |
| (597,166) | L: 50 | nulla | nulla (`L: SO` compare solo con il ritaglio centrato su FF0000) | 13 |

"Esporta" (pulsante disabilitato citato nella ricerca) non e presente in questo file: e nella cattura intera `affinity-samples-full.png` a (1754,75), non ritestata qui.

Robustezza (`winocr.exe --robustness`): token gia annullato -> `OperationCanceledException`; buffer corto -> vuoto + `Warn`; ritaglio 110x22 su "Home" -> `Home` corretto in 3-6 ms grazie al margine a 150x100 (a 1x senza margine la ricerca dava vuoto); immagine 7000x120 -> fattore ridotto da 1,5 a 1,42, preparata 10000x219, nessuna eccezione; 4 chiamate concorrenti -> tutte 36 righe, serializzate (79, 169, 269, 346 ms); cambio lingua a caldo it-IT -> en-US -> `zz-!!` -> vuoto -> it: sempre 36 righe con i `Warn` attesi.

## Limiti

- Nessuna confidenza: `Confidence` e sempre `null`. Errori sistematici del motore: `l`/`I` (`CtrI`), `0`/`O` (`FFOOOO`), maiuscola/minuscola (`s: 100`, `Ctrl+p`), punti persi (`vettorialesvg`). Da compensare a valle (dizionario etichette, LabelCleaner).
- Testo tagliato dal bordo della cattura produce frammenti: li toglie `TextResolver.DropCutText` (bordi della cattura) e, nel secondo passaggio, `DropLinesCutByCrop` (bordi della fascia).
- Il secondo passaggio recupera bene le etichette lunghe a basso contrasto, non quelle di 3-4 caratteri (`H: 0`).
- Il ritaglio 400x120 e il 2x del secondo passaggio sono tarati al 125%: a 100% e 200% non misurati.
- `RecognizeLowContrastAsync` non fa parte di `IOcrEngine`: il chiamante deve conoscere il tipo concreto (o fare un cast). **Proposta per il Core** (non applicata, Core congelato): interfaccia opzionale `IOcrEngineSecondPass { Task<OcrResult> RecognizeLowContrastAsync(CapturedImage, double x, double y, CancellationToken); }` oppure un parametro `ImagePoint? focus` in `RecognizeAsync`.
- I tempi sopra sono di una build Debug con la macchina in uso: oscillano del 50%.

## Come provare

Sonda a perdere in `C:\Users\Angelo\AppData\Local\Temp\claude\C--Users-Angelo-Desktop-Matteo\d74c920e-45bd-48a5-abcd-2768715aa453\scratchpad\spikes\winocr\` (`winocr.csproj`, `Program.cs`, `Bench.cs`), risultati in `run3.txt`:

```
set DOTNET_ROOT=C:\Users\Angelo\AppData\Local\Microsoft\dotnet
%DOTNET_ROOT%\dotnet.exe build winocr.csproj -c Debug --artifacts-path <cartella privata>
<cartella privata>\bin\winocr\debug\winocr.exe --debug          (menu, ritagli, basso contrasto)
<cartella privata>\bin\winocr\debug\winocr.exe --robustness     (lingua, annullamento, concorrenza, limiti)
<cartella privata>\bin\winocr\debug\winocr.exe --bench          (varianti GDI+ del pre-trattamento)
```

Il modulo da solo:

```
%DOTNET_ROOT%\dotnet.exe build src\PuntaEAscolta.Windows.Ocr\PuntaEAscolta.Windows.Ocr.csproj -c Debug --artifacts-path <cartella privata>
```
