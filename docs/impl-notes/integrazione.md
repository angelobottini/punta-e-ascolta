# Integrazione: registro dell'integratore

Data: 22/09/2026. Macchina: ASUS Zenbook Snapdragon X (ARM64), Windows 11 25H2, 1920x1200 al 125%, OCR solo it-IT, voci Elsa e Cosimo. SDK .NET 10.0.401 (runtime 10.0.12). Cartella di compilazione privata: `scratchpad\build\app` (debug) e `scratchpad\build\publish` (Release).

## Modifiche fuori dal modulo App

| Dove | Modifica | Perché |
|---|---|---|
| `PuntaEAscolta.slnx` | Aggiunta la cartella `/tools/` con `tools/PuntaEAscolta.OcrBench`. | Richiesto; il banco ora compila con la soluzione. |
| `src/PuntaEAscolta.Logic/Reading/ReadOrchestrator.cs` | `Trigger` ignora le attivazioni di lettura (clic, pressione lunga, scorciatoie di lettura) mentre `dictation.State == Recording` (log Info). Nuovo metodo privato `IsDictationRecording`, protetto da try/catch. | Segnalato da Speech: una lettura durante la registrazione finirebbe nel microfono e nel testo dettato. Stop, pausa e scorciatoia della dettatura restano invariati. |
| `tests/PuntaEAscolta.Logic.Tests/Reading/Fakes.cs` | `FakeDictation.State` ora impostabile (predefinito `Idle`). | Serve al test nuovo; gli altri test non cambiano. |
| `tests/PuntaEAscolta.Logic.Tests/Reading/ReadOrchestratorTests.cs` | Test `ReadActivations_AreIgnored_WhileDictationIsRecording`. | Copre la modifica: nessuna risoluzione né voce in registrazione, lettura di nuovo possibile dopo. |
| `docs/LEGGIMI.txt` | Nel menu dell'icona aggiunta la voce "Leggi la selezione". | Allineato al menu reale. |
| `src/PuntaEAscolta.App/PuntaEAscolta.App.csproj` (modulo proprio) | `Using Remove` di `System.Windows.Forms` e `System.Drawing`, `Using Include` di `System.IO` e `System.Net.Http`. | Tipi omonimi fra WPF e Windows Forms; WPF toglie System.IO e System.Net.Http dagli using impliciti. |

Nessuna modifica a `PuntaEAscolta.Core`, `Directory.*.props` o ad altri `.csproj`.

## Compilazione e test

- `dotnet build PuntaEAscolta.slnx --no-incremental`: **12 progetti, 0 avvisi, 0 errori** (anche in Release dentro `publish.ps1`).
- `dotnet test PuntaEAscolta.slnx`: **364/364** (Logic 247 = 246 + 1 nuovo, Speech 117), 4 esecuzioni di fila tutte verdi, circa 1 s per progetto.
- Controllo dei sorgenti toccati: nessuna sequenza backslash-u, nessun carattere invisibile o di controllo.

## Prove dalla cartella di compilazione (Debug)

| Prova | Esito |
|---|---|
| `--help`, argomento sbagliato | aiuto in italiano, codice 0; JSON con `ok:false`, codice 1 |
| `--selftest` | `ok:true` in 6,2 s. Per-Monitor V2 sì. OCR Windows: lingua it-IT, testo di prova letto in 61 ms. ONNX: riscaldamento 1,2 s a freddo, testo letto in 314 ms. Sintesi "Prova" con Elsa 215 ms (16 kHz). Riscaldamento del lettore 712 ms a freddo. Hook installato in 19 ms, rimosso in 4 ms. Avviso: `Win+Shift+A` già in uso da un altro programma. UIA: nessun elemento in quel punto (120 ms). |
| `--voices` | Microsoft Cosimo e Microsoft Elsa (it-IT); ElevenLabs: chiave assente. |
| `--ocr-file affinity-menu-file-popup.png --point 120 300 --engine windows` | 36 righe in 120 ms; scelta **"Apri Recenti"** (OcrLine); passaggio mirato 16 ms, stessa scelta. Errori noti del motore: `CtrI+Alt+H`, `Ctrl+p`. |
| stesso con `--engine onnx` | 36 righe in 1435 ms (a freddo, caricamento delle sessioni compreso), tutte corrette; scelta **"Apri Recenti"**; passaggio mirato 125 ms, stessa scelta. |
| `--read-at` al puntatore (nessun movimento, nessun clic) | Chrome, pagina di un articolo: `UiaSentence`, frase di 254 caratteri, lingua it, `Sensitive`, **91 ms** (UIA 34 ms). Il testo non è riportato qui. |
| `--read-at --zone` stesso punto | `OcrZone`, zona 1125x450 a 1,25, OCR Windows 166 ms, 10 righe (14 parole e 1 riga tagliate dal bordo scartate), **306 ms** totali. |
| `--speak "Punta e ascolta è pronto" --provider windows` con `settings.json` `{ "Speech": { "Volume": 0.2 } }` solo nella cartella di output | `Completed`, voce di Windows, riscaldamento 261 ms, **riproduzione avviata 22 ms** dopo `SpeakAsync`, 1,7 s in tutto. |
| `--set-key` con una chiave finta da stdin | salvata cifrata (DPAPI, `AQAAANCMnd8...`), nessuna traccia in chiaro né in `settings.json` né nel registro; senza stdin: errore e codice 1. File impostazioni poi ripristinato. |
| `--anteprima-impostazioni` | 7 PNG (finestra e 6 schede), impaginazione controllata a occhio. |

## Modalità icona di notifica

- Avvio senza argomenti: dal primo messaggio del registro a "Pronto" circa 0,6 s; voce di Windows riscaldata in 0,6 s, lettore in 0,3 s, ONNX dopo 3 s (circa 1 s, sul suo thread). Con `Win+Shift+A` occupato l'app lo ha detto a voce (volume 0,2); con `HotkeyReadSelection = Win+Shift+F9` (libera) avvio **silenzioso**, "scorciatoie attive 1, problemi 0".
- A regime (ONNX caricato): working set 162-169 MB, privata 76 MB, 33-34 thread, 727 handle.
- `--exit`: app chiusa in 106-213 ms con tutti i servizi rilasciati ("Servizi chiusi", "Punta e Ascolta chiuso"), anche con il riscaldamento ONNX in corso.
- Non provati (la persona stava usando il PC): clic della rotellina reale, menu dell'icona, finestra impostazioni interattiva, doppio avvio che apre le impostazioni.

## Pubblicazione

`tools\publish.ps1 -Runtime win-arm64 -SkipTests -ArtifactsPath <scratchpad>\build\publish`: Release, 0 avvisi, 8-14 s. Cartella `publish\PuntaEAscolta-win-arm64`: **312 file, 237,8 MB** (runtime .NET, WPF, Windows Forms, ONNX, modelli `models\v5`), librerie Visual C++ ARM64 copiate accanto all'eseguibile (`msvcp140`, `msvcp140_1`, `vcruntime140`, `vcruntime140_1` dal Redist di Build Tools 14.44.35112), `.lib` tolti, `LEGGIMI.txt` copiato.

`publish\PuntaEAscolta-win-arm64\PuntaEAscolta.exe --selftest`: **`ok:true`**, codice 0, 6,4 s. ARM64, Per-Monitor V2; OCR Windows 43-50 ms; ONNX disponibile (riscaldamento 1,1 s, prova 310-357 ms); sintesi 97 ms; hook installato; UIA 90-111 ms (`Text` di Chrome). Unico avviso: `Win+Shift+A` occupato. La cartella `logs` creata dalla prova è stata tolta, così la cartella resta pulita da distribuire.

Lo script conserva `settings.json`, `cache` e `logs` se si ripubblica sopra una cartella già in uso; senza `-Runtime` pubblica anche `win-x64` (non eseguito qui: servono i pacchetti di runtime x64, forse da scaricare da NuGet).

## Cose da sapere per chi prosegue

1. **Scorciatoia occupata**: su questo PC `Win+Shift+A` è di un altro programma; l'app lo annuncia a ogni avvio. Cambiarla dalla scheda Attivazione (es. `Win+Shift+F9`).
2. **Chiave ElevenLabs**: da impostare dalla finestra (Voce → incolla → Verifica → Salva chiave → voce → Salva) oppure con `--set-key` ad app chiusa (`--exit` prima). Tutto il percorso ElevenLabs resta da provare con la chiave vera.
3. **Proposte di contratto** già raccolte dagli altri moduli e ancora valide: `IReadOrchestrator.PausedChanged` (l'app usa l'evento della classe concreta), `SpeechOutcome`/`SpeakWithOutcomeAsync` in Core. Nessuna proposta nuova dall'App.
4. La voce di menu "Leggi la selezione" riporta in primo piano la finestra dell'utente prima di leggere (unica eccezione alla regola "mai attivare finestre", perché è un comando esplicito fuori dal percorso di lettura).
