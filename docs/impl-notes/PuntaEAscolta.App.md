# PuntaEAscolta.App: note di implementazione

Stato al 22/09/2026: completo. Compila con 0 errori e 0 avvisi insieme a tutta la soluzione; provato su questo PC (Snapdragon X, ARM64, 125%) da riga di comando, in modalità icona di notifica (avvio e chiusura) e pubblicato (`publish\PuntaEAscolta-win-arm64`). Le misure sono in `integrazione.md`.

## Che cosa esiste

| File | Contenuto |
|---|---|
| `Program.cs` | `Main`: con argomenti modalità riga di comando (console UTF-8), senza argomenti icona di notifica. |
| `Hosting/AppServices.cs` | Radice di composizione: crea i servizi nell'ordine delle dipendenze e li chiude in ordine inverso. `OpenSettings` (solo impostazioni e registro), `CreateHttpClient` (uno solo, `Timeout` 30 s, `ConnectTimeout` 5 s). |
| `Hosting/AppLog.cs` | `ILog` che inoltra al `FileLog` (i messaggi arrivati prima, ad esempio dall'archivio impostazioni, restano in memoria e vengono scritti dopo); copia facoltativa su stderr con la maschera della chiave di `FileLog.Mask`. |
| `Hosting/ScopedSettingsStore.cs` | Archivio solo in memoria (prove di voce con la bozza non salvata, `--provider`). |
| `Hosting/SpeechProbe.cs` | Sonde di misura del lettore e della voce locale (solo riga di comando). |
| `Hosting/SingleInstance.cs` | Mutex `Local\PuntaEAscolta.IstanzaUnica`, eventi `...ApriImpostazioni` (seconda istanza) e `...Esci` (`--exit`). |
| `Hosting/StartupRegistration.cs` | Avvio con Windows: valore `PuntaEAscolta` in `HKCU\...\Run` con il percorso tra virgolette. |
| `Hosting/ConsoleSetup.cs` | stdout/stderr UTF-8; se stdout non è rediretto ci si aggancia alla console di chi ha lanciato (`AttachConsole`), con ripristino della tabella codici. |
| `Tray/TrayHost.cs`, `Tray/TrayController.cs` | Modalità normale: WPF `Application` (`OnExplicitShutdown`, nessuna finestra principale) + `NotifyIcon` di Windows Forms; collegamenti fra i servizi. |
| `Tray/TrayIcons.cs`, `Tray/ForegroundTracker.cs` | Icona normale e "in pausa" (simbolo disegnato sopra `app.ico`); ultima finestra in primo piano per "Leggi la selezione". |
| `SettingsUi/SettingsWindow*.cs`, `ISettingsHost.cs` | Finestra impostazioni WPF costruita in codice (niente XAML, niente associazioni), 6 schede. |
| `CommandLine/*` | Riga di comando: argomenti, JSON, comandi, autodiagnosi, caricamento PNG, anteprima della finestra. |
| `Native/NativeMethods.cs` | Poche P/Invoke (console, finestra in primo piano, WinEvent, icone, DPI). |

## Composizione e collegamenti

Ordine: `JsonSettingsStore(cartella dell'exe)` + `FileLog(dati\logs, () => General.DebugLog)` → `DpapiSecretProtector` → `HttpClient` → `WindowsOcrEngine(() => Ocr.WindowsOcrLanguage)` (primario) → `OnnxOcrEngine` (secondario) → `GdiScreenCapture`, `UiaTextSource`, `ClipboardSelectionReader` → `NAudioPlayer`, `WindowsVoiceSynthesizer`, `ElevenLabsSynthesizer`, `SpeechCache(dati\cache)`, `SpeechService` → `NAudioRecorder`, `ElevenLabsSpeechToText`, `SendInputTextInjector`, `DictationService` → `TextResolver`. Solo in modalità icona: `ReadOrchestrator` e `WindowsInputSource`.

- `input.Input += orchestrator.HandleInput`.
- `speech.SpeakingChanged` e `orchestrator.BusyChanged` → `UpdateStopKey`: sotto un lock rilegge `speech.IsSpeaking || orchestrator.IsBusy` e chiama `input.SetStopKeyActive(occupato && Input.EscStopsSpeech)`. Così Esc funziona anche mentre si cerca il testo (fase silenziosa) e annulla la ricerca; a lettura finita Esc torna al sistema anche se gli eventi arrivano in ordine sparso.
- `settings.Changed` → `input.Apply(...)` solo se `Input`/`Dictation` sono cambiati (impronta JSON); allinea `orchestrator.Paused` a `General.Paused`; `cache.Trim()` se il limite scende; aggiorna icona e menu. Serializzato con un lock (salvataggi dalla finestra e dalla pausa possono sovrapporsi).
- `orchestrator.PausedChanged` → `input.SetPaused`, icona, salvataggio di `General.Paused` su un thread del pool (mai sul worker dell'orchestratore) con `JsonSettingsStore.Update`: si rilegge il file e si cambia solo la pausa, senza riscrivere valori cambiati da fuori. Anche **Salva**, **Salva chiave** e **Rimuovi chiave** della finestra passano da `Update` (i campi della finestra sul file riletto).
- `orchestrator.OutcomeProduced` → ultimo esito per la scheda Diagnostica. `dictation.StateChanged` → descrizione dell'icona.
- Avvio: `player.WarmUpAsync` + `windowsVoice.WarmUpAsync` in sottofondo; `onnx.WarmUpAsync` dopo 3 s (non in modo `WindowsOnly`). **Silenzio all'avvio**, salvo `input.LastProblems` non vuoto: si dice "Punta e Ascolta. Attenzione. ..." con la voce locale (`SpeechKind.System`), con "+" letto "più".
- Chiusura (menu Esci, `--exit`, fine sessione di Windows): input, orchestratore, icona, poi i servizi in ordine inverso, infine registro e mutex.
- Eccezioni: `DispatcherUnhandledException` (gestita e registrata), `Forms.Application.ThreadException` (niente finestre di errore di Windows Forms), `TaskScheduler.UnobservedTaskException`, `AppDomain.UnhandledException` (registrata).

## Icona di notifica

Menu: Pausa/Riprendi, **Impostazioni...** (anche doppio clic), Leggi la selezione, Apri cartella dei log, Esci. In pausa l'icona ha un piccolo simbolo di pausa e la descrizione dice "in pausa"; durante la dettatura la descrizione mostra lo stato.

"Leggi la selezione" dal menu: il clic sull'icona attiva la barra delle applicazioni, quindi l'app riporta in primo piano l'ultima finestra usata (tracciata con `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)`, escluse barra, desktop e l'app stessa), aspetta 300 ms e manda all'orchestratore un `HotkeyEvent(ReadSelection)`. È l'unico punto in cui l'app attiva una finestra: è un comando esplicito dal menu, fuori dal percorso di lettura.

## Finestra impostazioni

Mai mostrata da sola: solo dal menu, dal doppio clic sull'icona o rilanciando l'eseguibile (la seconda istanza bussa alla prima e cede il permesso di primo piano con `AllowSetForegroundWindow`). Ogni campo registra un "raccoglitore" che al salvataggio scrive il valore in una copia di `settings.Current` o restituisce un errore in italiano (nessun `MessageBox`: gli errori vanno nella riga di stato in basso). **Salva** applica subito (tramite `Changed`) e aggiorna la chiave `Run`.

- **Generale**: avvio con Windows (con lo stato attuale della chiave `Run`), pausa, log dettagliato, cartelle (impostazioni, dati, log) con pulsanti Apri.
- **Attivazione**: pulsante del mouse, pressione prolungata e durata, anti-rimbalzo, clic iniettati, 4 scorciatoie validate con `HotkeyGesture.TryParse` (rifiutato `Ctrl+Alt`, che è AltGr), Esc, problemi attuali (`LastProblems`, riletti dopo il salvataggio).
- **Lettura**: opzioni di `ReadingSettings`, programmi solo OCR (virgole, ".exe" tolto), OCR (modo, lingua dall'elenco di Windows, zona, distanze, blocchi) e disponibilità dei due motori.
- **Voce**: fornitore, volume; voce di Windows (salvata col nome visualizzato, es. "Microsoft Elsa") e velocità, **Prova voce di Windows**; ElevenLabs: `PasswordBox`, **Verifica** (`ValidateKeyAsync`, poi abbonamento con caratteri usati, limite, residui e rinnovo, poi elenco voci e modelli dell'account), **Salva chiave** (DPAPI, salvata subito), **Rimuovi chiave**, voce dall'account, modelli (modificabili), formato, stabilità/somiglianza/stile/velocità, lingua delle etichette, tempi massimi del primo audio, **Prova voce ElevenLabs**, stato dell'interruttore (`CloudSuspendedUntil`); riservatezza; cache (attiva, limite, dimensione, **Svuota**). Con una chiave già salvata, crediti e voci si caricano all'apertura. Le prove usano i valori della finestra anche non salvati (archivio in memoria) e il lettore condiviso.
- **Dettatura**: attiva, scorciatoia, pulsante del mouse, microfono (elenco WASAPI, "Predefinito di Windows" = vuoto), lingua, modello, durata massima, rilettura, inserimento automatico, modo di inserimento, comandi vocali.
- **Diagnostica**: ultimo esito (fonte, tipo, lingua, ms, passaggi; il testo solo con il log dettagliato attivo), stato (versione, pausa, problemi, OCR, voce, dettatura), ultime 200 righe del registro, Aggiorna, Copia, Apri cartella dei log.

## Riga di comando

`PuntaEAscolta.exe --help` (in italiano). Uscita JSON su stdout (chiavi in inglese, messaggi in italiano), codice 0 = eseguito, 1 = errore o argomenti non validi. Comandi: `--read-at [X,Y|puntatore] [--zone] [--speak]`, `--read-selection [--speak]`, `--ocr-file <png> --point X,Y` (anche `X Y`) `[--engine windows|onnx|entrambi] [--scale f]`, `--speak "testo" [--provider windows|elevenlabs|auto]`, `--voices`, `--selftest`, `--set-key [--verifica]` (chiave da stdin), in più `--exit` (chiude l'app in esecuzione e attende la fine), `--anteprima-impostazioni <cartella>` (PNG di ogni scheda della finestra, costruita fuori schermo senza attivarla), `--attendi <ms>`, `--verbose` (registro anche su stderr, livello Debug compreso).

- `--ocr-file`: PNG caricato con GDI+ in `CapturedImage` (BGRA, alfa 255, `DpiScale` = scala del monitor attuale o `--scale`); stampa tutte le righe del passaggio completo, la scelta di `PointerTextSelector` e anche il passaggio mirato (`IPointOcrEngine`) con la sua scelta.
- `--speak`: prima il riscaldamento (come all'avvio dell'app), poi `SpeakWithOutcomeAsync`; riporta esito, voce usata, ms fino all'inizio della riproduzione e totali.
- `--selftest`: silenzioso. Processo e DPI (Per-Monitor V2), impostazioni, lingue OCR, prova OCR su un'immagine sintetica con i due motori, voci e sintesi senza riproduzione, microfoni, riscaldamento del lettore, hook del mouse installato per 1 s, UIA sotto il puntatore, chiave ElevenLabs (verificata solo se presente). `ok=false` e codice 1 solo per problemi essenziali; il resto va in `warnings`.

Nota: l'eseguibile è WinExe. Da cmd usare `start /wait PuntaEAscolta.exe ...`, da PowerShell aggiungere `| Out-String`.

## Decisioni

- WPF e Windows Forms insieme: nel `.csproj` tolti gli using impliciti `System.Windows.Forms` e `System.Drawing` (tipi omonimi) e rimessi `System.IO` e `System.Net.Http` (che WPF toglie).
- Finestra costruita in codice invece che in XAML: niente compilazione di markup, controllo completo dei valori, facile da provare fuori schermo.
- `--provider` e le prove di voce non toccano `settings.json`: si usa una copia in memoria.
- Istanza singola indipendente dalla cartella: le copie x64 e ARM64 non devono intercettare il mouse insieme.
- All'avvio la chiave `Run` viene solo aggiornata se l'opzione è attiva (cartella spostata); viene tolta solo salvando dalla finestra.

## Limiti

- Finestra impostazioni e menu dell'icona non provati con clic reali (la persona usa il PC): la finestra è stata costruita e disegnata fuori schermo (`--anteprima-impostazioni`), menu e pulsanti no. "Leggi la selezione" dal menu non provato.
- Nessuna prova con una chiave ElevenLabs vera (verifica, crediti, voci, prova voce) né con il microfono.
- `--set-key` con l'app aperta **rifiuta** (codice 1, "Chiudere prima l'app con --exit...") prima di leggere la chiave: l'app non rilegge `settings.json` da sola. Scelta preferita a "salva e avvisa l'app" (più semplice, niente stati a metà). Se l'app viene aperta durante `--verifica`, la chiave si salva comunque e il messaggio chiede di riavviarla.
- Su questo PC `Win+Shift+A` (predefinito di "Leggi la selezione") è occupato: a ogni avvio l'app lo dice. Consiglio per l'assistente: `Win+Shift+F9` (provata: libera).
- Pubblicazione x64 non eseguita qui (script pronto, stesso percorso).

## Come provare

```
set DOTNET_ROOT=C:\Users\Angelo\AppData\Local\Microsoft\dotnet
%DOTNET_ROOT%\dotnet.exe build PuntaEAscolta.slnx --artifacts-path <cartella privata>
<cartella privata>\bin\PuntaEAscolta.App\debug\PuntaEAscolta.exe --selftest
... --voices | --read-at | --read-at 700,500 --zone | --ocr-file docs\research\probe\affinity-menu-file-popup.png --point 120 300 --engine entrambi
... --speak "Punta e ascolta è pronto" --provider windows      (volume da settings.json accanto all'exe)
... (senza argomenti: icona di notifica)   poi   ... --exit
powershell -ExecutionPolicy Bypass -File tools\publish.ps1 [-Runtime win-arm64] [-SkipTests] [-ArtifactsPath <cartella>]
```

## Revisione del 22/09/2026 (vedi `revisione.md`)

- Pausa: il salvataggio in `settings.json` registra lo stato ATTUALE dell'orchestratore (un solo salvataggio in coda alla volta) e non rimanda più `General.Paused` all'orchestratore quando è lui ad averlo deciso. Prima due pressioni ravvicinate potevano lasciare l'app in pausa, in silenzio e anche dopo il riavvio, subito dopo che la voce aveva detto "Lettura riattivata". "Pausa" dal menu dell'icona passa ora dal worker dell'orchestratore come la scorciatoia (con conferma a voce).
- Finestra impostazioni: le scorciatoie passano anche da `WindowsInputSource.ValidateHotkey` (niente Esc, niente tasti senza Ctrl/Alt/Win salvo F1-F24, Pausa, Bloc Scorr e tasti multimediali).
- Secondo giro: Esc attivo anche durante la ricerca del testo (`BusyChanged`); salvataggi mirati con `JsonSettingsStore.Update`; `--set-key` rifiuta con l'app aperta. Nessun progetto di test per l'App: verificato con la compilazione e `--selftest`.
- Rifinitura: se all'apertura della finestra impostazioni `settings.json` non era stato letto (`BackupPending`), la finestra prova a ricaricarlo (`Load`, che applica i valori veri all'app); se resta illeggibile **Salva è disattivato** con un messaggio (i campi mostrano i predefiniti e li scriverebbero tutti sopra le impostazioni vere). Salva chiave, Rimuovi chiave e la pausa toccano un solo campo e restano attivi. `AppServices` passa `GdiScreenCapture.GetTopLevelWindowBounds` a `TextResolver.WindowBoundsAtPoint`.
