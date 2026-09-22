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
- Senza voce ma con una risoluzione in corso, una nuova attivazione annulla la vecchia e parte subito.
- Gesti: `LongPressEnabled = false` agisce sul DOWN; con pressione lunga il clic breve si decide sull'UP e la pressione lunga (timer `TimeProvider`, `LongPressMs`) dà `ZoneAroundPointer` con il punto del DOWN. Anti-rimbalzo `DebounceMs` sul DOWN rispetto all'ultima attivazione; lo stesso anti-rimbalzo vale per le scorciatoie di lettura.
- Pausa: i clic (anche un timer di pressione lunga già avviato) sono ignorati, le scorciatoie funzionano. Valore iniziale da `General.Paused`. Il setter `Paused` non ferma la voce; la scorciatoia `TogglePause` ferma la voce, inverte lo stato e lo conferma a voce ("Lettura in pausa" / "Lettura riattivata", `System`): è l'unico riscontro possibile in un'app solo audio.
- **Evento pubblico in più** `PausedChanged(bool)` (non nel contratto): serve all'app per `IInputSource.SetPaused`, icona e salvataggio quando la pausa cambia da tastiera. Sollevato dal thread che cambia lo stato (worker o chiamante del setter).
- `ToggleDictation`: senza servizio dice "Dettatura non disponibile" (`System`). Con il servizio ferma prima la **nostra** lettura (non la rilettura della dettatura) e chiama `ToggleAsync` in un compito a parte, senza serializzare le chiamate (una seconda pressione deve poter annullare la prima).
- Testo trovato: `SpeechRequest(Text, Kind, LanguageHint, Sensitive)` dall'esito, `Chunks` = `SentenceSplitter.SplitSentences(testo, 400)`, sempre valorizzato (anche con un solo pezzo). Nessun testo e `SpeakWhenNothingFound`: `NothingFoundText` come `System`, lingua "it".
- `OutcomeProduced` è sollevato anche per "nessun testo", sul thread della lettura, prima di parlare; non per le letture annullate. Eccezioni dei gestori registrate e ignorate.
- `OperationCanceledException` della voce senza nostro annullamento (Esc gestito da altri) è registrata a livello Debug, non come errore.
- `Dispose` ferma worker, lettura e timer; non chiude voce né dettatura (appartengono a chi compone).

## Risolutore: decisioni

- Ordine per `AtPointer`: selezione (solo se il punto cade in uno dei suoi rettangoli e `ReadSelectionWhenPointerInside`) > elemento UIA (`UiTextResolver`; saltato per `OcrOnlyProcesses`) > suggerimento (testo, altrimenti OCR del rettangolo allargato di 8 px) > OCR della zona. `Selection`: qualunque selezione, poi appunti. `ZoneAroundPointer`: solo OCR, tutta la zona.
- Zona = `ZoneWidth x ZoneHeight x DpiScale` centrata sul puntatore; puntatore e rettangolo dell'elemento riportati nello spazio dell'immagine effettivamente catturata (anche se ritagliata dal monitor).
- Motori: Auto = primario; se nulla vicino al puntatore e il primario implementa `IPointOcrEngine`, passaggio mirato e nuova scelta; poi secondario se ancora nulla, oppure se l'elemento è `Image`/`Pane`/`Custom`/`Document` e il primario ha trovato meno di 2 righe (in quel caso vince il secondario se trova testo). `WindowsOnly`: mai il secondario. `OnnxOnly`: solo il secondario (primario se ONNX non è disponibile). Il passaggio mirato parte solo se quello normale è riuscito (non dopo tempo scaduto o errore).
- Ritaglio: elemento UIA senza testo e piccolo (meno di 300x120 px scalati) = `clipTo`; nessun ripiego senza ritaglio (meglio "Nessun testo" che l'etichetta del vicino).
- Testo tagliato dal bordo (facoltativo, `DiscardCutText`, attivo): un lato "taglia" solo se coincide con il rettangolo richiesto (non se la cattura è stata ristretta dal monitor). Lati sinistro e destro: si tolgono le parole che li toccano (2 px); sopra e sotto: si toglie la riga, salvo quella del puntatore. Non si applica all'OCR dei suggerimenti.
- Tempi massimi per fase: UIA 1500 ms, OCR 3000 ms, appunti 2000 ms (`WaitAsync`: una fase che ignora il token non blocca). L'annullamento della richiesta si propaga subito.
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

- `IReadOrchestrator.PausedChanged` (oggi solo sulla classe concreta).

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
orchestrator.PausedChanged += p => { input.SetPaused(p); /* icona, General.Paused */ };
speech.SpeakingChanged += s => input.SetStopKeyActive(s);      // Esc arriva come HotkeyAction.Stop
```
