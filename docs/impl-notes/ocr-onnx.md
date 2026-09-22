# PuntaEAscolta.Ocr.Onnx: note di implementazione

Stato: **funzionante e misurato** il 22/09/2026 sullo Zenbook Snapdragon X (ARM64 nativo, 125%, 8 core, Windows 11 25H2). Compila con 0 errori e 0 avvisi (Debug e Release). Prima di oggi il codice non era mai stato eseguito.

## Cosa c'è

| File | Contenuto |
|---|---|
| `OnnxOcrEngine.cs` | `OnnxOcrEngine : IOcrEngine, IPointOcrEngine`, costruttore `(ILog log, string? modelsDirectory = null)` (predefinito `<cartella app>\models\v5`), `Name = "ONNX PP-OCRv5"`. `WarmUpAsync()`, `RecognizeAsync` (tutta l'immagine), `RecognizeNearAsync(image, x, y, ct)` = `RecognizeAroundPointAsync` (percorso mirato), `IsAvailable`, `UnavailableReason`, `LastTimings` (`OnnxOcrTimings`), parametri regolabili come proprietà. |
| `BitmapUtils.cs` (interno) | Copia BGRA -> SKBitmap, ritaglio di regioni, letterbox, ritaglio raddrizzato dei quadrilateri con il sistema di riferimento (`CropFrame`), allungamento orizzontale, ricerca dell'inchiostro (`FindInk`). |
| `LatinTextFilter.cs` | Filtro dei caratteri per la voce (vedi sotto). |
| `tools/PuntaEAscolta.OcrBench` | Banco di prova (console `net10.0`, **fuori** dalla soluzione). |

Uso tipico: `var ocr = new OnnxOcrEngine(log); _ = ocr.WarmUpAsync();` all'avvio (circa 450 ms, su un thread proprio), poi `RecognizeAroundPointAsync` per la UI e `RecognizeAsync` per le immagini.

## Pipeline

1. **Rilevamento** (DBNet `ch_PP-OCRv5_mobile_det`, normalizzazione ImageNet come RapidOcrNet per v5, BGR corretto: la cattura è già BGRA).
   - `RecognizeAsync`: tutta l'immagine; lato corto portato a 736 (1x..3x) con due tetti: lato lungo 2000 e **800.000 pixel** (il costo è proporzionale ai pixel: circa 0,4 ms ogni 1000 pixel, post-elaborazione C# di RapidOcrNet compresa).
   - `RecognizeNearAsync`: solo una **finestra 512x160 al 100%** (x `DpiScale`: 640x200 al 125%) centrata sul punto, a **1,5x**. Se la riga puntata (con le parole attaccate sulla stessa fascia) tocca un bordo interno della finestra, si rileva di nuovo su tutta la larghezza (bordo laterale) o su tutta l'immagine (bordo sopra/sotto). I riquadri tagliati dalla finestra finale vengono scartati (frammenti di altre colonne).
   - Secondo passaggio a 2x solo se non si trova nulla o se l'altezza mediana dei riquadri è sotto 14 px (testo minuscolo), entro i tetti.
2. **Scelta delle righe** (solo percorso mirato): le righe la cui fascia verticale contiene il punto; se nessuna, quelle entro 1,5 altezze di riga; più le righe attaccate dello stesso blocco (spazio < 0,2 altezze di riquadro, sovrapposte in orizzontale), massimo 6.
3. **Riconoscimento** (`latin_PP-OCRv5_rec_mobile`, 504 classi, dizionario verificato: 502 righe + blank + spazio): ritaglio raddrizzato dall'immagine originale, **allungato 1,6 volte in orizzontale**, 2 ritagli in parallelo.
4. **Righe**: filtro caratteri, confidenza media per carattere >= **0,6**, scartate le righe senza lettere né cifre. Riquadro della riga = **zona di inchiostro** dentro il ritaglio (non il riquadro del rilevatore, più alto di circa il 60%), riportata sull'immagine originale anche per i ritagli ruotati. Parole: posizioni dalle colonne CTC. Infine le righe spezzate in parole sulla stessa fascia vengono **unite** se lo spazio è < 0,6 altezze.

## Correzioni e decisioni (tutte misurate con il banco)

1. **Funziona su ARM64 nativo**: `onnxruntime.dll` e `libSkiaSharp.dll` caricate da `runtimes\win-arm64\native` (build) o dalla cartella dell'app (publish), non da System32. Nessun uso errato dell'API RapidOcrNet 4.2.0 (verificata per riflessione e sul sorgente del commit del pacchetto): `TextDetector`/`TextRecognizer` sono pubblici, `GetTextBoxes` restituisce coordinate dell'immagine passata, `CharCols`/`ColCount` danno le colonne CTC.
2. **Doppie perse** ("paccheto", "Aced", "Preferit..", "Apr..."): il CTC emette un simbolo ogni 8 px dell'ingresso alto 48, troppo pochi per i caratteri stretti della UI. Allungando il ritaglio del 60% il menu File passa da **29/36 a 36/36** (1,3x: 34/36; 2x: 36/36 ma più lento).
3. **Percorso mirato a finestra**: il rilevamento su tutta la zona costava 300-550 ms; nella finestra 90-110 ms. Accuratezza invariata (36/36, 21/21 sulle righe dei menu).
4. **Riquadri stretti sull'inchiostro**: con i riquadri del rilevatore le voci di menu distano 0,4 altezze e il `PointerTextSelector` le avrebbe unite in blocchi; ora `Home` = (26,9) 41x13 contro (26,7; 10,0) 39x11 di Windows OCR.
5. **Unione delle parole**: nel percorso mirato il testo grande dei cartelli veniva spezzato ("È" | "VIETATO" | "L'ACCESSO"): ora una riga sola. Le schede affiancate (Livelli / Carattere / Paragrafo, spazio circa 0,9) e etichetta/scorciatoia dei menu restano separate.
6. **Selezione per fascia verticale**: un punto nello spazio vuoto fra "Nuovo" e "Ctrl+N" non trovava nulla (la distanza euclidea superava il raggio); ora conta la distanza verticale, quella orizzontale solo per l'ordine.
7. **Secondo passaggio 2x** solo sotto 14 px: a 20 px scattava sulle etichette a basso contrasto del pannello Colore raddoppiando il tempo (330 ms contro 150 ms) senza guadagno.
8. **Filtro caratteri**: tolti un carattere invisibile letterale (trattino morbido) e una sequenza di escape dal sorgente; i codici ora sono numerici. Il dizionario v5 latin non ha CJK (solo la virgola ideografica, mappata a ","), ma ha greco, numeri romani, numeri cerchiati, frecce, spunte e operatori: greco simile al latino -> latino, romani -> lettere, cerchiati -> cifre, esclusi anche i separatori e i controlli bidirezionali U+2028-202F.
9. **Dispose sicuro**: annulla l'inferenza in corso (token collegato, RapidOcrNet termina la `Run` di ONNX Runtime), attende il semaforo (massimo 5 s) e solo allora libera le sessioni native; prima le liberava durante l'uso (rischio di crash nativo). Buffer più corto del dichiarato: `Warn` e risultato vuoto.
10. **Thread**: 4 intra-op (8 peggiora: 400-550 ms contro 230 ms sul rilevamento grande, 2 è 1,5 volte più lento), niente attesa attiva (`allow_spinning = 0`, costa circa 30 ms sul rilevamento grande), arena disattivata, riconoscimento con parallelismo 2 (riconoscimento del menu completo da 988 a 628 ms). La priorità BelowNormal vale solo per il thread di lavoro: i thread di ONNX Runtime e di `Parallel.For` restano a priorità normale.

## Misure

Build Debug del banco (le librerie native sono le stesse della Release; la Release pubblicata dà gli stessi tempi entro il 10%). "Completo" = `RecognizeAsync`; "mirato" = `RecognizeNearAsync` al centro di ogni riga trovata (mediana) e sul punto indicato. Esatto = testo identico; i puntini finali contano.

| Immagine | Completo: corrette | Completo ms (freddo / caldo) | Mirato ms (mediana righe / punto) | Mirato uguale al completo |
|---|---|---|---|---|
| `affinity-menu-file-popup.png` 486x938, scuro | **36/36** (26/26 etichette + 10/10 scorciatoie) | 1082 / 897 | 107 / 101 (`Nuovo`) | 36/36 |
| `affinity-menu-visualizza-popup.png` 430x683 | **21/21** (19/19 + `Ctrl+Maiusc+W`, `Tab`) | 888 / 719 | 97 / 111 | 21/21 |
| `affinity-ocr-geometry-crop1.png` 900x300 | 17/17 | 885 / 719 | 132 / 124 (`Apri Recenti`) | 18/23 (differenze su icone e righe tagliate dal bordo) |
| crop2 | 18/19 (`2025,jpg`) | 965 / 797 | 132 / 141 (`Salva con nome...`) | 21/21 |
| crop3 | 13/13 | 735 / 584 | 134 / 131 (`Ctrl+Alt+Maiusc+N`) | 15/16 |
| crop4 | 20/20 (menu principale compreso) | 763 / 627 | 158 / 126 (`Nuovo`) | 24/25 |
| crop5 | 11/12 (`2025,jpg`) | 818 / 640 | 129 / 350 (nome file lungo: finestra allargata) | 15/15 |
| crop6 | 6/7 (`2025,jpg`) | 740 / 537 | 125 / 116 (`Cancella`) | 13/13 |
| `affinity-lowcontrast-normal.png` ridotta a 900x300 | **10/10** (`H: 0`, `S: 100`, `L: 50`, `n: FF0000`, `Opacità`, `100 %`, 4 schede) | 533 / 398 | 146; i 6 punti della nota Windows: **6/6** in 129-150 ms | 10/10 |
| scena: cartello giallo 1000x600 (sintetico) | 2/2 (`ATTENZIONE:`, `È VIETATO L'ACCESSO`) | 499 / 356 | 324 (finestra allargata) | 2/2 |
| scena: bianco su rosso ruotato di 8 gradi | 2/2 | 508 / 360 | 152 | 2/2 |
| scena: cartello piccolo 360x220, rumore e JPEG 35 | 1/2 (`lunedi` senza accento) | 456 / 311 | 109 | - |

Totale UI (menu, ritagli, basso contrasto): **152/155 righe esatte (98%)**; gli unici errori sono `.jpg` letto `,jpg`. Scene: 5/6 (accento perso a 13 px di altezza).

Confronto con Windows OCR (`windows-ocr.md`, stesse immagini): menu File 26/26 etichette ma 8/10 scorciatoie (`CtrI`, `Ctrl+p`), 120-170 ms; Visualizza `CtrI+Maiusc+W`; basso contrasto: `H: 0` e `L: 50` mai letti, `S: 100` solo col secondo passaggio, `FFOOOO`, `0 100%`. ONNX: 36/36 e 10/10, ma il passaggio completo costa 5-8 volte di più.

Zona reale dell'app (1125x450 al 125%, ritagliata da `affinity-menu-file-full.png` e `affinity-samples-full.png`, Release ARM64): completo 1370-1540 ms a caldo (35 righe, di cui 1060 ms di riconoscimento: le righe lunghe della chat allungate 1,6x pesano), **mirato 133-147 ms** (`Nuovo da Preferiti...`, `Salva come pacchetto...`, `Esporta` disabilitato in 141 ms).

Avvio: caricamento sessioni circa 250 ms, riscaldamento circa 200 ms (totale 430-600 ms). Primo mirato dopo il riscaldamento 230-250 ms, poi 100-150 ms.

Memoria (working set): 28 MB a vuoto, **100-110 MB dopo il riscaldamento**, **140-150 MB a regime col solo percorso mirato** (picco 166-188 MB); col passaggio completo picco 300-330 MB, poi 160-200 MB. Con 600.000 pixel di tetto il picco scende a 250 MB e il completo è circa il 20% più veloce, con risultati quasi uguali (una riga in più o in meno per immagine: `Acced.i...`, `L 50`).

x64 in emulazione (Prism) sulla stessa macchina, Release: riscaldamento 1714 ms, completo menu File 1215 ms a caldo, mirato 145 ms, working set 219 MB. Su un Intel nativo non misurato.

Robustezza (`OcrBench --robustezza`): token già annullato e annullamento dopo 40 ms -> `OperationCanceledException` (47-58 ms), la richiesta successiva funziona; buffer corto, 0x0, 1x1 -> vuoto; ritaglio 110x22 su "Home" -> `Home` in 42 ms (letterbox); punto fuori dall'immagine -> vuoto; striscia 3000x40 -> 13 righe; 2560x1440 -> 321 righe in 5 s; 4 chiamate concorrenti serializzate, risultati identici; coordinate di righe e parole dentro l'immagine; modelli mancanti -> `IsAvailable = false` con motivo; `Dispose` durante un riconoscimento -> vuoto, nessun crash.

## Pubblicazione

`dotnet publish tools/PuntaEAscolta.OcrBench -r win-x64|win-arm64 --self-contained -c Release`: nessun avviso, `models\v5` presente (4 file, 13,7 MB; il classificatore `ch_PP-LCNet...cls` da 1 MB non viene usato), nessun conflitto con il `RapidOcrNet.targets`.

| | win-x64 | win-arm64 |
|---|---|---|
| Cartella intera del banco (runtime .NET incluso, niente WPF) | 119 MB, 210 file | 127 MB, 210 file |
| Parte OCR (onnxruntime 16 MB, libSkiaSharp, gestiti ORT/Skia/RapidOcrNet/Clipper2, modulo) + modelli | 28,8 + 13,7 = 42,5 MB | 27,6 + 13,7 = 41,3 MB |

DLL native (architettura verificata dall'intestazione PE, tutte corrette): `onnxruntime.dll`, `onnxruntime_providers_shared.dll`, `libSkiaSharp.dll`, più quelle del runtime .NET (`coreclr`, `clrjit`, `clrgc`, `clrgcexp`, `hostfxr`, `hostpolicy`, `mscordaccore`, `mscordbi`, `mscorrc`, `clretwrc`, `msquic`, `System.IO.Compression.Native`, `Microsoft.DiaSymReader.Native.*`). Inutili nella cartella: `onnxruntime.lib`, `onnxruntime_providers_shared.lib`.

**Attenzione (per chi integra)**: `onnxruntime.dll` importa `MSVCP140.dll`, `MSVCP140_1.dll`, `VCRUNTIME140.dll` (e `VCRUNTIME140_1.dll` su x64), che il publish self-contained **non** copia. Qui sono installate; sul PC di Matteo potrebbero mancare e allora il motore risulta non disponibile (`UnavailableReason` = libreria nativa mancante, l'app prosegue con Windows OCR). Proposta per `tools/publish.ps1`: copiare app-local `msvcp140.dll`, `msvcp140_1.dll`, `vcruntime140.dll`, `vcruntime140_1.dll` da `C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Redist\MSVC\14.44.35112\{x64,arm64}\Microsoft.VC143.CRT\` e togliere i due `.lib`.

## Ruolo consigliato rispetto a Windows OCR

- **Windows OCR resta il primario**: 40-170 ms sulla zona intera, nessun costo di memoria, niente dipendenze.
- **ONNX come secondario, ma col percorso mirato**: `RecognizeAroundPointAsync` costa 100-180 ms (fino a 350 ms se la riga è più larga della finestra) e legge ciò che Windows sbaglia o non vede: basso contrasto (pannelli di Affinity), voci disabilitate, `l`/`I`, `0`/`O`, scorciatoie.
- **Proposta per `TextResolver`** (Logic, non modificato qui): oggi il ripiego chiama `RecognizeAsync` sul secondario, cioè la zona intera: 1,4-1,5 s sulla zona 1125x450 con molto testo, fuori dal budget di 1 s. Suggerisco: se il secondario è `IPointOcrEngine` e non si legge l'intera zona (`wholeZone` falso) usare `RecognizeAroundPointAsync`; riservare `RecognizeAsync` a `wholeZone` e alle immagini (cartelli: 300-500 ms, poche righe).
- **Scene (cartelli, magliette)**: ONNX è il motore adatto (testo ruotato, colori, rumore); le righe spezzate in parole vengono riunite.
- Il `PointerTextSelector` può usare le righe ONNX come quelle di Windows: riquadri stretti sull'inchiostro, parole con riquadro, `Confidence` valorizzata (0..1).

## Limiti

- Icone lette come lettere singole (`a`, `T`, `W`, `B` nelle barre strumenti di Affinity, confidenza alta): da filtrare a valle se disturbano.
- Punto/virgola nei nomi di file (`2025,jpg`), accenti su testo sotto 15 px (`lunedi`).
- Passaggio completo lento con molte righe (circa 30 ms per riga lunga): un riconoscimento a lotti (più ritagli in un solo tensore, come PaddleOCR) richiederebbe una sessione di riconoscimento propria al posto di `TextRecognizer`; non fatto.
- Tarature fatte al 125% su Affinity scuro; 100%, 150%, 200% e temi chiari non misurati (la finestra segue `DpiScale`, il secondo passaggio 2x copre il testo minuscolo).
- Righe verticali non gestite (niente rotazione di 90 gradi: scelta voluta, una lettera singola alta sarebbe stata ruotata).

## Come provare

```
set DOTNET_ROOT=C:\Users\Angelo\AppData\Local\Microsoft\dotnet
dotnet build tools\PuntaEAscolta.OcrBench --artifacts-path <cartella privata>
OcrBench <immagine.png> [x y] [--attese righe.txt] [--giri N] [--ogni-riga] [--solo-vicino] [--zona X,Y] [--scala f] [--debug]
OcrBench --genera <cartella>          (tre cartelli sintetici con le righe attese)
OcrBench --robustezza <immagine.png>  (annullamento, buffer errati, concorrenza, Dispose)
```

`--attese` confronta con un file di righe attese (una per riga, UTF-8) e riporta esatte, uguali per la voce (puntini finali, maiuscole) e sbagliate con la riga più vicina; `--ogni-riga` ripete il percorso mirato al centro di ogni riga trovata; `--zona X,Y` ritaglia 1125x450 attorno al punto come l'app al 125%. Opzioni di taratura: `--thread`, `--parallelo`, `--allunga`, `--finestra LxA`, `--scalavicino`, `--maxpixel`, `--piccolo`, `--latocorto`, `--confmin`, `--inverti`.
