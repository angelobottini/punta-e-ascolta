# PuntaEAscolta.Logic, parte B (motore): note di implementazione

Stato al 22/09/2026. La parte A (`Text/*`, `Resolution/*`, 155 test) è invariata.

## Che cosa esiste

| File | Contenuto |
|---|---|
| `Reading/ReadOrchestrator.cs` | `ReadOrchestrator : IReadOrchestrator`, costruttore di DESIGN 3.1. Canale + un worker, gesti, stop, pausa, dettatura, spezzatura in frasi. |
| `Reading/GestureRecognizer.cs` | Riconoscitore dei gesti (interno). Ritoccato: anti-rimbalzo solo su intervalli positivi, callback del timer protetta. |
| `Reading/TextResolver.cs` | `TextResolver : ITextResolver`. Rivisto: passaggio OCR mirato (`IPointOcrEngine`), scelta dei motori per modalità, testo tagliato dal bordo, `WaitAsync` per i tempi massimi, nessuna sequenza `\u` nel sorgente. |
| `Settings/JsonSettingsStore.cs`, `Logging/FileLog.cs` | Invariati (verificati con i test). |
| `tests/.../Reading/Fakes.cs` | Finti condivisi: UI, cattura, OCR (con registro chiamate), OCR mirato, appunti, voce, dettatura, impostazioni, log, `FakeTimeProvider`. |
| `tests/.../Reading`, `/Settings`, `/Logging` | 91 test nuovi: orchestratore 28, risolutore 41 (38 + 3 casi di una teoria), impostazioni 12, registro 10. |

## Orchestratore: decisioni

- `HandleInput` fa solo `TryWrite` su un canale illimitato. Un solo worker legge il canale e **non esegue letture**: per ogni richiesta avvia un compito con il proprio `CancellationTokenSource`, annullato dalla richiesta successiva, dallo stop, dalla pausa. Così il worker è sempre libero di ricevere il secondo clic.
- "Sta parlando" = `ISpeechService.IsSpeaking` **oppure** una nostra `SpeakAsync` ancora in corso (copre l'attesa del primo audio anche con servizi che la escludono da `IsSpeaking`). In quello stato qualunque attivazione di lettura (clic, pressione lunga, scorciatoie di lettura) fa `Stop()` e annulla la lettura in corso, niente altro.
- **Secondo giro della revisione**: anche durante la ricerca del testo (fase silenziosa, 0,3-2 s e fino a 4,5 s con un'app bloccata) un'attivazione **ferma e annulla** la lettura in attesa, niente altro: prima il secondo clic la ricominciava uguale e Matteo non poteva fermarla. Unica eccezione: puntatore spostato di più di `PendingReadMoveThresholdPx` (24 px fisici, non scalati) dal punto della lettura in attesa, ed entrambe letture legate al puntatore (non `Selection`): allora è una lettura nuova nel punto nuovo e la vecchia viene annullata. L'anti-rimbalzo resta com'era.
- **Evento pubblico in più** `BusyChanged(bool)` e proprietà `IsBusy` (non nel contratto, il Core è congelato): vero da quando parte una lettura (anche mentre cerca il testo) o un messaggio vocale fino alla fine, a uno stop o a un annullamento; uno stop lo azzera subito anche se la risoluzione annullata non è ancora tornata. L'app lo combina con `SpeakingChanged` per registrare Esc anche durante la ricerca: Esc (`HotkeyAction.Stop`) annulla risoluzione e voce. Gli eventi arrivano da thread diversi senza ordine garantito: chi li riceve rilegge `IsBusy` sotto un proprio lock.
- Gesti: `LongPressEnabled = false` agisce sul DOWN; con pressione lunga il clic breve si decide sull'UP e la pressione lunga (timer `TimeProvider`, `LongPressMs`) dà `ZoneAroundPointer` con il punto del DOWN. Anti-rimbalzo `DebounceMs` sul DOWN rispetto all'ultima attivazione; lo stesso anti-rimbalzo vale per le scorciatoie di lettura.
- Pausa: i clic (anche un timer di pressione lunga già avviato) sono ignorati, le scorciatoie funzionano. Valore iniziale da `General.Paused`. Il setter `Paused` non ferma la voce; la scorciatoia `TogglePause` ferma la voce, inverte lo stato e lo conferma a voce ("Lettura in pausa" / "Lettura riattivata", `System`): è l'unico riscontro possibile in un'app solo audio.
- **Evento pubblico in più** `PausedChanged(bool)` (non nel contratto): serve all'app per `IInputSource.SetPaused`, icona e salvataggio quando la pausa cambia da tastiera. Sollevato dal thread che cambia lo stato (worker o chiamante del setter).
- `ToggleDictation`: senza servizio dice "Dettatura non disponibile" (`System`). Con il servizio ferma prima la **nostra** lettura (non la rilettura della dettatura) e chiama `ToggleAsync` in un compito a parte, senza serializzare le chiamate (una seconda pressione deve poter annullare la prima).
- Testo trovato: `SpeechRequest(Text, Kind, LanguageHint, Sensitive)` dall'esito, `Chunks` = `SentenceSplitter.SplitSentences(testo, 400)`, sempre valorizzato (anche con un solo pezzo). Nessun testo e `SpeakWhenNothingFound`: `NothingFoundText` come `System`, lingua "it".
- `OutcomeProduced` è sollevato anche per "nessun testo", sul thread della lettura, prima di parlare; non per le letture annullate. Eccezioni dei gestori registrate e ignorate.
- `OperationCanceledException` della voce senza nostro annullamento (Esc gestito da altri) è registrata a livello Debug, non come errore.
- `Dispose` ferma worker, lettura e timer; non chiude voce né dettatura (appartengono a chi compone).

## Risolutore: decisioni

- Ordine per `AtPointer`: selezione (solo se il punto cade in uno dei suoi rettangoli e `ReadSelectionWhenPointerInside`) > elemento UIA (`UiTextResolver`; saltato per `OcrOnlyProcesses`) > suggerimento (testo, altrimenti OCR del rettangolo allargato di 8 px) > OCR della zona. `Selection`: qualunque selezione, poi appunti, poi (dal 23/09/2026) il flusso del puntatore se non c'è niente di selezionato. `ZoneAroundPointer`: solo OCR, tutta la zona.
- Suggerimento accettato solo se è messo come un suggerimento (`IsPlacedLikeTooltip`, valori al 100% per la scala del monitor): bordo superiore fra 10 px sopra e 100 px sotto il puntatore, bordo sinistro non oltre 60 px a destra, bordo destro non oltre 300 px a sinistra. Altrimenti `suggerimento:lontano` nella diagnostica e si passa all'OCR della zona (prima una tendina o un popup di WPF entro 400 px veniva letto al posto del testo sotto il puntatore). Limite noto: un suggerimento che Windows mette sopra il puntatore perché in fondo allo schermo non c'è posto viene ignorato.
- Zona = `ZoneWidth x ZoneHeight x DpiScale` centrata sul puntatore; puntatore e rettangolo dell'elemento riportati nello spazio dell'immagine effettivamente catturata (anche se ritagliata dal monitor).
- Motori: Auto = primario; se nulla vicino al puntatore e il primario implementa `IPointOcrEngine`, passaggio mirato e nuova scelta; poi secondario se ancora nulla, oppure se l'elemento è `Image`/`Pane`/`Custom`/`Document` e il primario ha trovato meno di 2 righe (in quel caso vince il secondario se trova testo). `WindowsOnly`: mai il secondario. `OnnxOnly`: solo il secondario (primario se ONNX non è disponibile). Il passaggio mirato parte solo se quello normale è riuscito (non dopo tempo scaduto o errore).
- Ritaglio: elemento UIA senza testo e piccolo (meno di 300x120 px scalati) = `clipTo`; nessun ripiego senza ritaglio (meglio "Nessun testo" che l'etichetta del vicino).
- Testo tagliato dal bordo (facoltativo, `DiscardCutText`, attivo): un lato "taglia" solo se coincide con il rettangolo richiesto (non se la cattura è stata ristretta dal monitor). Lati sinistro e destro: si tolgono le parole che li toccano (2 px); sopra e sotto: si toglie la riga, salvo quella del puntatore. Non si applica all'OCR dei suggerimenti.
- Zona dell'OCR limitata alla finestra di primo livello sotto il puntatore (`TextResolver.WindowBoundsAtPoint`, impostato da `AppServices` con `GdiScreenCapture.GetTopLevelWindowBounds`; il Core congelato non ha un contratto per questo). I lati tolti così non contano come tagli (`DropCutText` riceve la zona intera), come il bordo del monitor. Diagnostica `finestra=LxA`. Vale anche per `ZoneAroundPointer`. Prima l'area grigia di Word in una finestra leggeva le righe della finestra dietro (prove dal vivo 2, BUG A).
- Tempi massimi per fase: UIA 1500 ms (`DefaultUiaTimeoutMs`; il cane da guardia di `UiaTextSource`, 1400 ms, deve restare sotto: vedi `UiaTimeoutBudgetTests`), OCR 3000 ms, appunti 4500 ms (2000 fino al 23/09/2026; `WaitAsync`: una fase che ignora il token non blocca). L'annullamento della richiesta si propaga subito, e anche al token della fase in corso: `GuardAsync` lo annulla esplicitamente. Senza, quando Cancel arrivava dal worker (niente contesto di sincronizzazione) la continuazione di `WaitAsync` girava dentro Cancel e chiudeva il token collegato della fase prima che scattasse: l'OCR ONNX lavorava fino in fondo (prove dal vivo 2, osservazione 1; test `RequestCancellation_AlsoCancelsTheRunningStageToken`).
- Tipo: riga OCR fino a 60 caratteri e suggerimenti (anche letti con OCR) = `Label`; il resto `Sentence`. `LabelLanguage` forza la lingua solo per le etichette.
- `Diagnostics`: `Tipo@x,y`, ogni fase con esito e ms (`elemento:no(3ms)`, `ocr:win:tempo-scaduto(3001ms)`), zona, righe, motore scelto, `tagliati=`, `troncato=`, `lingua=`, `totale=`. Il testo letto va nel log solo a livello Debug.

## Limiti

- Caso peggiore (tutte le fasi al tempo massimo): circa 3 x 1,5 s di UIA + 3 x 3 s di OCR. Le fasi che non onorano il token restano in sottofondo finché finiscono (non attese).
- "Meno di 2 righe" conta le righe di tutta la zona, non quelle vicine al puntatore.
- Il suggerimento trovato da `FindTooltipAsync` potrebbe appartenere a un elemento vicino (dipende dalla piattaforma).
- Nelle impostazioni un valore di enum sconosciuto rende il file "corrotto": rinominato `.bad`, si riparte dai predefiniti (comportamento di `JsonSettingsStore`, verificato da un test).
- Nessun test udibile: il modulo non contiene voce reale.

## Misure

- 246 test verdi (155 + 91) in circa 0,6 s, 5 esecuzioni consecutive stabili; 0 avvisi, 0 errori.
- Latenza della logica `HandleInput` -> `SpeakAsync` con finti immediati (40 clic): mediana 0,01 ms, massimo 1,4-2,7 ms (primo clic a freddo).

## Proposte per il contratto (non applicate)

- `IReadOrchestrator.PausedChanged`, `IsBusy` e `BusyChanged` (oggi solo sulla classe concreta).

## Come provare

```
set DOTNET_ROOT=C:\Users\Angelo\AppData\Local\Microsoft\dotnet
%DOTNET_ROOT%\dotnet.exe test tests\PuntaEAscolta.Logic.Tests --artifacts-path <cartella privata>
%DOTNET_ROOT%\dotnet.exe test tests\PuntaEAscolta.Logic.Tests --artifacts-path <cartella privata> --filter "FullyQualifiedName~Reading" --logger "console;verbosity=detailed"
```

## Per chi compone l'app

```csharp
var resolver = new TextResolver(uia, capture, windowsOcr, onnxOcr, clipboard, settingsStore, log);
var orchestrator = new ReadOrchestrator(resolver, speech, dictationOrNull, settingsStore, log);
input.Input += orchestrator.HandleInput;                       // non blocca
orchestrator.PausedChanged += p => { input.SetPaused(p); /* icona, General.Paused con settingsStore.Update */ };
// Esc arriva come HotkeyAction.Stop: attivo mentre la voce parla E mentre si cerca il testo (sotto un lock, rileggendo gli stati)
void UpdateEsc() { lock (gate) input.SetStopKeyActive((speech.IsSpeaking || orchestrator.IsBusy) && settings.Input.EscStopsSpeech); }
speech.SpeakingChanged += _ => UpdateEsc();
orchestrator.BusyChanged += _ => UpdateEsc();
```

## Impostazioni (secondo giro della revisione)

- `JsonSettingsStore.Update(Action<AppSettings>)`: sotto il lock rilegge `settings.json` dal disco (se esiste e si interpreta), applica la modifica e salva; restituisce la copia salvata e solleva `Changed`. Serve per cambiare un solo valore (pausa, chiave dalla finestra, campi della finestra) senza riscrivere i valori cambiati da fuori con quelli vecchi in memoria. File che non si legge o non si interpreta: si parte da `Current` e il file viene prima copiato in `settings.json.bak`.
- Lettura all'avvio: un file bloccato (`IOException`, `UnauthorizedAccessException`) viene riprovato dopo 100, 250 e 500 ms; se resta illeggibile si usano i predefiniti **in memoria**, il file non viene toccato (niente `.bad`) e il primo salvataggio lo copia in `settings.json.bak` prima di sovrascriverlo (una sola volta; se la copia non riesce il salvataggio fallisce). `BackupPending` dice se la copia è in attesa. `Load()` con il file bloccato tiene i valori in memoria. Rifinitura: una lettura riuscita dentro `Update` non annulla la copia in attesa (chi chiama `Update` può scrivere i predefiniti mostrati dalla finestra impostazioni: il file vero finisce comunque in `.bak`); `Update` e `Load` fanno i nuovi tentativi (fino a 850 ms) **fuori** dal lock e sotto il lock una sola lettura senza attese, così `Current` (letto a ogni clic e dalla voce) non aspetta.

## Frasi (secondo giro della revisione)

- `SentenceSplitter`: i mesi abbreviati (gen, feb, mar, apr, mag, giu, lug, ago, set, sett, ott, nov, dic) chiudono la frase davanti a una maiuscola **solo** subito dopo il numero del giorno (1-31, anche "1°"): "il 10 gen. Porta i documenti" si spezza, "il gen. Rossi è arrivato" (generale) no. Gli altri ambigui ("circa", "via", "ecc.", giorni della settimana...) non cambiano.

## Revisione del 22/09/2026 (vedi `revisione.md`)

- `LabelCleaner`: scorciatoia fra parentesi in coda ("Grassetto (CTRL+G)", "(CTRL+barra spaziatrice)", "(F1)"); "(elemento aggiunto)" e "Avvio dell'accesso rapido - " di Esplora file. Un modificatore scritto a parole conta solo se seguito da + o -: "Altro", "Alto", "Controllo", "Opzione", "Windows", "SUPER 95" non sono più scorciatoie (prima l'OCR le cancellava).
- `EmojiFilter`: occhi delle emoticon solo ":", ";", "=" (più "xD", "8-)", "B-)"): "Windows XP", "8) Salva il file", "Unità D: piena", "x3" restano intatti.
- `SentenceSplitter`: abbreviazioni che sono anche parole comuni o chiudono spesso una frase ("circa", "via", "no", "min", "ecc.", mesi e giorni...) chiudono la frase se la parola dopo comincia con una maiuscola; davanti a cifre e minuscole restano abbreviazioni.
- `UiTextResolver`: area di testo grande (pagina di Word, Blocco note, corpo di un messaggio) con il puntatore fuori dal testo -> null (niente "Contenuto pagina 1", mai il documento intero); valore su più righe o oltre 200 caratteri mai letto come valore di un campo; Edit dentro una voce di elenco con valore uguale al nome della voce (Esplora file) -> solo il nome del file; ripieghi (HelpText, descrizioni, LegacyName) filtrati come il Name (nomi di tipo .NET, identificatori); MenuItem senza nome alto al massimo 12 px -> separatore, silenzio; nome minuscolo attaccato ("newdocnew") con HelpText -> HelpText; cella di tabella non di foglio di calcolo con testo lungo -> frase, non tutta la cella.
- `PointerTextSelector` (zona intera): righe raggruppate in blocchi (colonne, cartelli); prima il blocco sotto il puntatore, poi gli altri dall'alto e da sinistra; righe a capo di una stessa frase unite senza pausa.
- `TextResolver`: barra di scorrimento (o suo figlio) senza nome -> silenzio, niente OCR della riga vicina.

## Prove dal vivo del 23/09/2026 (portatile con il solo touchpad)

- Diagnosi: senza rotellina e con `HotkeyReadAtPointer` vuota, l'unica attivazione possibile era la scorciatoia "leggi la selezione" (nel log solo "Attivazione (scorciatoia): Selection"), che per costruzione leggeva solo il testo selezionato.
- `ReadRequestKind.Selection`: selezione (accessibilità), poi appunti, poi **se non c'è niente di selezionato** (selezione vuota e appunti vuoti, rifiutati o scaduti) il flusso del puntatore, senza chiedere di nuovo la selezione (`ResolveAtPointerAsync(..., selectionAlreadyChecked: true)`). Diagnostica: `selezione:nessuna -> puntatore`. L'annullamento durante la fase degli appunti non passa al puntatore. Test: `SelectionRequest_WithoutSelection_ReadsTheElementUnderThePointer`, `..._WithoutClipboardReader_ReadsAtPointer`, `..._FallsBackToOcrUnderThePointer`, `..._ClipboardTimeout_FallsBackToPointer`, `..._CancelledDuringClipboard_DoesNotReadThePointer`.
- Fase "appunti" 2000 -> 4500 ms (`TextResolver.DefaultClipboardTimeoutMs`): l'attesa del rilascio dei modificatori è salita a 3 s (vedi `windows-input.md`).
- Orchestratore: ogni attivazione va nel registro a livello **Info** (prima Debug): "Attivazione (clic) a X,Y: lettura AtPointer", "Attivazione (scorciatoia ReadSelection) a X,Y: lettura Selection", e le varianti "mentre la voce parla: stop", "durante la ricerca del testo...". Mai il testo. Test: `EveryActivation_IsLoggedAtInfo_WithOriginAndPoint_ButNeverTheText`, `ActivationWhileSpeaking_IsLoggedAtInfo_AsStop`.
- Predefiniti nel Core (`InputSettings`): `HotkeyReadAtPointer = "Ctrl+Shift+Space"`, `AcceptInjectedEvents = true` (`InputDefaultsTests`). Un `settings.json` già esistente tiene i valori che contiene (nessuna migrazione): `tools/publish.ps1` crea cartelle senza `settings.json`.
