# Pubblicazione: cartelle portatili x64 e ARM64

Data: 22/09/2026. Macchina: Snapdragon X (ARM64), Windows 11 25H2, 1920x1200 al 125%, voci Elsa e Cosimo. La cartella x64 è stata eseguita qui con l'emulazione Prism (processo `x64`, sistema `arm64`). La persona stava usando il PC: nessun clic reale, solo comandi da riga di comando e un avvio breve in modalità icona.

## Comandi

```
powershell -ExecutionPolicy Bypass -File tools\publish.ps1 -Runtime win-x64 -SkipTests -ArtifactsPath <scratchpad>\build\x64
powershell -ExecutionPolicy Bypass -File tools\publish.ps1 -Runtime win-arm64 -SkipTests -ArtifactsPath <scratchpad>\build\x64
powershell -ExecutionPolicy Bypass -File tools\publish.ps1 -Runtime all -SkipTests -ArtifactsPath <scratchpad>\build\x64   (dopo la correzione)
```

- x64 riuscito al primo tentativo: i pacchetti di runtime `win-x64` (.NET, WindowsDesktop 10.0.12) erano già nella cache NuGet, il Redist Visual C++ x64 è in Build Tools 14.44.35112. Compilazione Release **0 avvisi, 0 errori**; 14 s (x64), 10 s (ARM64), 11 s (entrambi, incrementale).
- `-ArtifactsPath` è supportato dallo script.

## Modifica a `tools\publish.ps1`

**Problema trovato**: nel Redist ARM64 `vcruntime140_1.dll` è un file **ARM64EC** (intestazione PE `0x8664` = x64, PDB `arm64ec\vcruntime140_1.arm64.pdb`). Lo script lo copiava nella cartella ARM64, dove non serve: `onnxruntime.dll` ARM64 non lo importa (la gestione eccezioni FH4 di `vcruntime140_1` esiste solo per il codice x64) e un processo ARM64 puro non lo può caricare. Innocuo ma sbagliato (e faceva fallire il controllo "tutte le DLL native sono ARM64").

**Correzione** (solo lo script): nuova funzione `Get-PeMachine` che legge il tipo di macchina dall'intestazione PE; le librerie Visual C++ vengono copiate solo se l'architettura coincide con quella della pubblicazione (`0x8664` per x64, `0xAA64` per ARM64). Le escluse sono elencate ("Non copiate (architettura diversa da arm64): vcruntime140_1.dll"). Aggiornata anche la descrizione nel blocco d'aiuto. File sempre UTF-8 con BOM, solo ASCII, righe LF come prima.

Risultato: x64 copia `msvcp140`, `msvcp140_1`, `vcruntime140`, `vcruntime140_1`; ARM64 copia `msvcp140`, `msvcp140_1`, `vcruntime140`. OCR ONNX ARM64 provato dopo la correzione: funziona (le prove qui sotto sono tutte sulle cartelle finali).

## Contenuto delle cartelle

| | `PuntaEAscolta-win-x64` | `PuntaEAscolta-win-arm64` |
|---|---|---|
| File | **313** | **311** (312 prima della correzione) |
| Dimensione | **224,0 MB** (234.916.598 byte) | **237,8 MB** (249.344.010 byte) |
| Compressa in zip (Optimal) | circa 94,9 MB | circa 89,8 MB |
| Cartelle | `Assets`, `it`, `models` | `Assets`, `it`, `models` |
| `models\v5` | 4 file, 13,1 MB (det 4,6 MB, rec latin 7,5 MB, cls 1,0 MB, dizionario) | uguale |
| `LEGGIMI.txt` | presente | presente |
| `settings.json`, `logs`, `cache` | assenti | assenti |
| `.lib` | nessuno | nessuno |

Differenze fra i due elenchi: solo x64 `D3DCompiler_47_cor3.dll` (WPF x64), `vcruntime140_1.dll`, `Microsoft.DiaSymReader.Native.amd64.dll`, `mscordaccore_amd64_amd64_10.0.1226.42308.dll`; solo ARM64 le due equivalenti `arm64`.

Sono inclusi i 9 PDB dei nostri assembly (circa 370 KB): danno righe e file nelle tracce d'errore del registro. Si possono togliere con `-p:DebugType=none` se si preferisce; non cambiato.

### Tipo di macchina (intestazione PE, letta file per file)

| | x64 | ARM64 |
|---|---|---|
| PE totali | 296 | 294 |
| Nativi | **28, tutti AMD64** | **26, tutti ARM64** |
| Gestiti solo IL (AnyCPU) | 125 | 125 |
| Gestiti ReadyToRun del framework + `PuntaEAscolta.dll` | 143 AMD64 | 143 ARM64 |

File chiave:

| File | x64 | ARM64 |
|---|---|---|
| `PuntaEAscolta.exe` | AMD64, 449.024 byte | ARM64, 426.496 byte |
| `onnxruntime.dll` | AMD64, 16,5 MB | ARM64, 16,7 MB |
| `libSkiaSharp.dll` | AMD64, 11,4 MB | ARM64, 9,9 MB |
| `msvcp140.dll` | AMD64 | ARM64 |
| `msvcp140_1.dll` | AMD64 | ARM64 |
| `vcruntime140.dll` | AMD64 | ARM64 |
| `vcruntime140_1.dll` | AMD64 | non copiato (non serve) |
| `coreclr.dll`, `clrjit.dll`, `hostfxr.dll`, `hostpolicy.dll`, `wpfgfx_cor3.dll`, `PresentationNative_cor3.dll`, `vcruntime140_cor3.dll` | AMD64 | ARM64 |

Importazioni (`dumpbin /dependents`): `onnxruntime.dll` x64 importa `MSVCP140`, `MSVCP140_1`, `VCRUNTIME140`, `VCRUNTIME140_1`; quello ARM64 le stesse **senza** `VCRUNTIME140_1`. `libSkiaSharp.dll` importa solo DLL di sistema.

Librerie caricate davvero (immagini mappate nel processo durante `--ocr-file --engine onnx`): sia il processo x64 emulato sia quello ARM64 caricano `msvcp140`, `msvcp140_1`, `vcruntime140` (e per x64 `vcruntime140_1`), `onnxruntime`, `libSkiaSharp`, `coreclr` **dalla cartella dell'app**, non da System32. Su un PC Intel senza Visual C++ Redistributable l'OCR ONNX quindi funziona.

## Prove su questo PC: x64 emulato contro ARM64 nativo

Tutte con codice di uscita 0 e `ok:true`. Tempi in ms; "primo" = prima esecuzione dopo una pubblicazione (file appena scritti, cache di traduzione di Prism vuota per x64).

### `--selftest`

| Misura | ARM64 | x64 (Prism) | x64/ARM64 |
|---|---|---|---|
| Durata interna (primo) | 5692 | 5309-6435 | circa 1,1 |
| Durata interna (a regime) | 2483-2563 | 3575-3742 | circa 1,45 |
| Durata con avvio del processo (a regime) | 2782-2852 | 4113-4321 | circa 1,5 |
| Architettura / Per-Monitor V2 | arm64 / sì | x64 su arm64 / sì | |
| OCR Windows, testo di prova | 47-51 | 89-102 (158 primo) | circa 1,9 |
| ONNX riscaldamento (caricamento sessioni + prova) | 491-495 (1113 primo) | 996-1133 (1673-1805 primo) | circa 2,1 |
| ONNX testo di prova | 267-285 | 447-527 (726 primo) | circa 1,8 |
| Sintesi "Prova" con Elsa (16 kHz) | 90-93 | 133-157 (182 primo) | circa 1,5 |
| Riscaldamento del lettore | 252-270 (615 primo) | 286-310 (398-647 primo) | circa 1,1 |
| Hook installato / rimosso | 25-30 / 4-11 | 45-64 / 8-26 | circa 1,8 |
| UIA sotto il puntatore | 77-87 | 115-127 (265 primo) | circa 1,5 |

Avvisi: nessuno in x64; ARM64 una volta "Nessun elemento di accessibilità sotto il puntatore" (normale). `Win+Shift+A` oggi risulta libero ("scorciatoie attive 1, problemi 0"), a differenza di `integrazione.md`. Microfono trovato, dettatura spenta, chiave ElevenLabs assente.

### `--voices`

Entrambe: Microsoft Cosimo e Microsoft Elsa (it-IT), ElevenLabs "assente". Durata con avvio: ARM64 374 ms, x64 562-631 ms.

### `--ocr-file docs\research\probe\affinity-menu-file-popup.png --point 120 300 --engine entrambi`

| | ARM64 | x64 (Prism) | x64/ARM64 |
|---|---|---|---|
| OCR Windows, passaggio completo | 36 righe, 111-122 ms | 36 righe, 169-195 ms | circa 1,6 |
| OCR Windows, passaggio mirato | 17-18 ms | 24-32 ms | circa 1,5 |
| ONNX, passaggio completo (sessioni a freddo comprese) | 36 righe, 1504-1623 ms (rilevamento 488-533, riconoscimento 741-801) | 36 righe, 2507-3011 ms (rilevamento 726-960, riconoscimento 1119-1354) | circa 1,7 |
| ONNX, passaggio mirato | 122-131 ms | 213-256 ms | circa 1,8 |
| Scelta (entrambi i motori, entrambi i passaggi) | **"Apri Recenti"** | **"Apri Recenti"** | |
| Durata con avvio del processo | 2,1-2,4 s | 3,7-4,3 s | circa 1,8 |

Le 36 righe ONNX sono **identiche** fra x64 e ARM64 (testo e confidenza), tutte corrette (`Ctrl+Alt+H`, `Ctrl+P`). L'OCR di Windows ha gli stessi errori noti su entrambe (`CtrI+Alt+H`, `Ctrl+p`). Valori ARM64 in linea con la build Debug di `integrazione.md` (ONNX 1435 ms, mirato 125 ms).

### `--read-at` al puntatore (nessun movimento, nessun clic, senza voce)

Stesso punto (1485,946, finestra dell'app Claude), eseguiti in alternanza. Il testo letto non è riportato.

| | ARM64 | x64 (Prism) | x64/ARM64 |
|---|---|---|---|
| Lettura UIA (`UiaName` 73 caratteri, poi `UiaSentence` 9 caratteri: la pagina cambiava) | 108-112 ms | 213-228 ms | circa 2 |
| `--zone` (`OcrZone`, zona 997x450 a 1,25, OCR Windows) | 248-269 ms (OCR 117-131) | 460-464 ms (OCR 197-207) | circa 1,8 |
| Durata con avvio del processo | 355-515 ms | 815-994 ms | |

Caso peggiore visto (x64, prima prova, puntatore a 600,600 su un'area vuota del Blocco note): UIA niente, OCR Windows 186 ms, ONNX 2409 ms con caricamento a freddo delle sessioni, "niente vicino al puntatore", **2967 ms** in tutto. In modalità icona le sessioni ONNX sono già caricate 3 s dopo l'avvio, quindi il caso reale è più corto.

### Altre prove x64

- `--anteprima-impostazioni`: 7 PNG corretti (WPF disegna bene in emulazione, versione "0.1.0 (x64)"); 6,7-8,3 s contro 4,9 s ARM64.
- Modalità icona, avvio breve e `--exit`: "Pronto" 1,56 s dopo l'avvio del processo, avvio **silenzioso** (problemi 0); voce di Windows riscaldata in 316 ms, lettore in 304 ms; ONNX caricato dopo 3 s in 1112 ms (702 + 376). A regime: working set 204 MB, privata 106 MB, 33 thread, 821 handle (ARM64 in `integrazione.md`: 162-169 MB, 76 MB, 33-34, 727). `--exit`: chiusa in 320 ms, "Servizi chiusi", "Punta e Ascolta chiuso". Nessuna chiave `Run` creata.
- Registro delle prove x64: solo righe INFO, nessun avviso o errore.

## In sintesi

- La cartella x64 è corretta e completa: tutto AMD64, VC++ x64 accanto all'exe e usate davvero, modelli presenti, niente file dell'utente.
- In emulazione su Snapdragon tutto funziona; è circa **1,5-2 volte più lenta** della ARM64 nativa (ONNX circa 1,7-2,1 volte, UIA circa 1,5-2 volte). Su questo PC va usata la cartella ARM64; la x64 è per i PC Intel/AMD, dove girerà nativa (tempi reali da misurare su un PC Intel).
- Cartelle lasciate pulite: tolte le cartelle `logs` create dalle prove (nessuna `cache`, nessun `settings.json`).

## Da sapere

1. `publish.ps1` conserva `logs`, `cache` e `settings.json` quando si ripubblica sopra una cartella già usata (voluto). Prima di copiare una cartella su un altro PC controllare che non ci siano, altrimenti si portano dietro registri e impostazioni di prova.
2. La prima esecuzione dopo ogni pubblicazione è più lenta (x64 fino a 6,4 s di autodiagnosi contro 3,6 s): Prism ricostruisce la sua cache di traduzione; lo stesso accade in misura minore in ARM64 (file appena scritti).
3. Non provati: clic reale della rotellina, menu dell'icona e finestra interattiva con la build x64; nessuna prova su un vero PC Intel.
