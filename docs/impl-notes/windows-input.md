# PuntaEAscolta.Windows.Input: note di implementazione

Stato al 22/09/2026: modulo completo, compila con 0 errori e 0 avvisi (`net10.0-windows10.0.19041.0`, `AllowUnsafeBlocks`). Nessun pacchetto NuGet, nessuna dipendenza da `System.Drawing`.

## Cosa c'è

| File | Contenuto |
|---|---|
| `Interop/NativeMethods.cs`, `Interop/NativeStructs.cs` | P/Invoke `[LibraryImport]` e strutture Win32 (identiche su x64 e ARM64). Aggiunti `WM_QUIT`, `WM_DESTROY` e `SetProcessDpiAwarenessContext` (solo per programmi di prova senza manifest). |
| `Interop/InputInjection.cs` | Costruzione degli `INPUT` con firma `Tag` ("PEA1") in `dwExtraInfo`, `Send`, attesa del rilascio dei modificatori. |
| `HotkeyMap.cs` | Nomi dei tasti (inglese e italiano, F1-F24, tastierino, OEM, carattere del layout) in codici tasto virtuali. |
| `NativePointer.cs` | `GetPhysicalPosition()` in pixel fisici del desktop virtuale. |
| `WindowsInputSource.cs` | `IInputSource`: hook `WH_MOUSE_LL` + `RegisterHotKey` su InputThread dedicato. |
| `GdiScreenCapture.cs` | `IScreenCapture`: `BitBlt(SRCCOPY | CAPTUREBLT)` in DIB section 32 bpp top-down. |
| `SendInputTextInjector.cs` | `ITextInjector`: `KEYEVENTF_UNICODE` per unità UTF-16, Invio e Backspace. |
| `ClipboardSelectionReader.cs` | `IClipboardSelectionReader`: Ctrl+C firmato, lettura e ripristino degli appunti. |

## WindowsInputSource

- **InputThread** (`ThreadPriority.Highest`, background) creato al primo `Start`: finestra message-only (`HWND_MESSAGE`) con classe propria e `WndProc` statica `[UnmanagedCallersOnly]`, ciclo `GetMessageW`. `Start` attende che il thread sia pronto (circa 40 ms misurati) e poi applica le impostazioni; al ritorno `LastProblems` è già valorizzato.
- **Hook**: callback statica `MouseProc` passata come function pointer (nessun delegate, nessun rischio di GC). Uscita immediata su `WM_MOUSEMOVE`; il resto è in `OnMouse` (precompilato con `RuntimeHelpers.PrepareMethod`): riconosce rotellina (`WM_MBUTTON*`) e pulsanti laterali (`WM_XBUTTON*` con `HIWORD(mouseData)` 1 o 2), ignora i nostri eventi (`dwExtraInfo == Tag`) e gli iniettati (`LLMHF_INJECTED`) salvo `AcceptInjectedEvents`, scrive `TriggerButtonEvent` (pulsante di lettura) oppure `HotkeyEvent(ToggleDictation)` (pulsante della dettatura, solo sul DOWN) in un `Channel<InputEvent>` e ritorna 1. Tutto il resto passa con `CallNextHookEx`.
- **Coppia DOWN+UP**: maschera di bit per pulsante (`_swallowedMask`, più `_dictationMask` per sapere cosa emettere sull'UP). Un UP viene inghiottito solo se il suo DOWN è stato inghiottito, anche se nel frattempo sono cambiate le impostazioni o è scattata la pausa. Verificato con clic iniettati (vedi "Come provare").
- **Pausa** (`SetPaused`): i DOWN passano all'app sottostante; le scorciatoie restano attive (serve per `TogglePause`).
- **Consumatore**: task che legge il canale e solleva `Input` su un thread del pool; le eccezioni dei gestori vengono registrate e non fermano il consumatore.
- **Scorciatoie**: `RegisterHotKey` con `MOD_NOREPEAT` sull'InputThread (id 1-5: leggi sotto il puntatore, leggi selezione, ferma, pausa, dettatura; id 6: Esc). Il punto dell'evento è `NativePointer.GetPhysicalPosition()`. Problemi in italiano in `LastProblems`: scorciatoia non riconosciuta, già in uso da un altro programma (`ERROR_HOTKEY_ALREADY_REGISTERED`), assegnata a più azioni, `Ctrl+Alt+X` che coincide con AltGr (avviso), pulsante della dettatura uguale a quello di lettura. La scorciatoia della dettatura viene registrata solo se `Dictation.Enabled`.
- **Esc**: `SetStopKeyActive(true)` registra `VK_ESCAPE` -> `HotkeyAction.Stop`, `false` lo toglie; rispetta `InputSettings.EscStopsSpeech`. È sempre deregistrato alla chiusura.
- **Apply a caldo**: le impostazioni vengono passate all'InputThread con `WM_APP`; le scorciatoie vengono tutte deregistrate e reregistrate, lo snapshot immutabile letto dalla callback viene sostituito senza lock. `Apply` attende la conferma (massimo 3 s).
- **Reinstallazione dell'hook** (prima il nuovo, poi via il vecchio): a `WM_WTSSESSION_CHANGE` (sblocco, connessione console/remota, logon; via `WTSRegisterSessionNotification`), `WM_POWERBROADCAST` ripresa (via `RegisterSuspendResumeNotification`), `WM_DISPLAYCHANGE`, e con un timer ogni 30 s se sono passati 10 minuti dall'ultima installazione, nessun pulsante attivatore è premuto e l'utente è inattivo da almeno 3 s (`GetLastInputInfo`). Se l'installazione iniziale fallisce, il timer riprova. Una callback oltre 20 ms viene segnalata nel log (il log avviene fuori dalla callback, tramite `PostMessage`).
- **Dispose**: `WM_APP_SHUTDOWN` -> `PostQuitMessage`; il thread deregistra scorciatoie e notifiche, toglie l'hook, distrugge le finestre. `Join` con timeout di 3 s; se scade, l'hook viene rimosso dal thread chiamante.
- **Una sola istanza viva per processo** (le callback native sono statiche): un secondo costruttore prima del `Dispose` lancia `InvalidOperationException`.

### Scelte e deviazioni da segnalare

- Le finestre message-only **non ricevono i messaggi di broadcast** (`WM_DISPLAYCHANGE`). Oltre alla finestra message-only viene creata una seconda finestra di primo livello **nascosta e mai mostrata** (`WS_POPUP`, `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`, dimensione 0) solo per riceverli: è lo stesso schema di `Microsoft.Win32.SystemEvents`. Non può rubare il focus.
- Nei messaggi di `LastProblems` la scorciatoia è ripetuta come l'ha scritta l'assistente ("Win+Shift+A"), non nella forma canonica di `HotkeyGesture.ToString()` ("Shift+Win+A").
- Sotto debugger l'hook viene installato comunque (con avviso nel log): un punto di interruzione blocca il mouse di tutto il sistema finché Windows non rimuove l'hook.
- Anti-rimbalzo, pressione lunga e "secondo clic = stop" NON sono qui: stanno nell'orchestratore (`PuntaEAscolta.Logic`), come da DESIGN.md 3.1. Questo modulo emette eventi grezzi.
- Nessuna modifica proposta al contratto di `PuntaEAscolta.Core`.

## GdiScreenCapture

- `Capture(desired, anchor)`: monitor di `anchor` (`MonitorFromPoint` + `GetMonitorInfoW`), `desired` intersecato con il monitor; se non lo tocca affatto viene spostato al suo interno mantenendo le dimensioni. Rettangolo vuoto -> immagine 0x0 senza eccezioni. `BitBlt` da `GetDC(NULL)`; alfa forzato a 255. Errori GDI -> `Win32Exception` (il chiamante registra e prosegue).
- `DpiScale` = `GetDpiForMonitor(MDT_EFFECTIVE_DPI) / 96`, ripiego `GetDpiForSystem`.
- Misurato su questa macchina: 900x300 in 25-35 ms, scala 1,25, contenuto corretto (menu e testo), nessun cursore.
- Le coordinate sono fisiche solo se il processo è Per-Monitor V2 (manifest dell'App). `WindowsInputSource.Start` scrive un avviso nel log se non lo è.

## SendInputTextInjector

- Ogni carattere = pressione + rilascio `KEYEVENTF_UNICODE`; `\n` (anche `\r\n`) diventa Invio, `\t` Tab, altri caratteri di controllo vengono saltati. Blocchi di 50 eventi (25 caratteri) per `SendInput`, pausa di 5 ms fra i blocchi, controllo del `CancellationToken` a ogni blocco. Prima di scrivere attende (max 1 s) il rilascio di Ctrl/Alt/Shift/Win.
- Se `SendInput` accetta meno eventi del richiesto (tipico: finestra elevata, UIPI) registra un avviso e lancia `InvalidOperationException` con messaggio in italiano: il servizio di dettatura può così avvisare a voce.
- Le chiamate sono serializzate (`SemaphoreSlim`) e girano su un thread del pool.

## ClipboardSelectionReader

- Thread STA dedicato per ogni chiamata, una alla volta. Sequenza: numero di sequenza degli appunti -> apertura (fino a 12 tentativi a 15 ms) -> se `CountClipboardFormats() == 0` appunti vuoti; se manca `CF_UNICODETEXT` (immagine, file...) **restituisce null senza toccare nulla**; altrimenti memorizza il testo -> attesa rilascio modificatori -> Ctrl+C firmato -> attesa che il numero di sequenza cambi (max 300 ms, passo 10 ms) -> lettura del testo copiato -> ripristino (svuota e riscrive il testo precedente, oppure lascia vuoto).
- Limite: se gli appunti contenevano testo formattato (RTF, HTML) viene ripristinato solo il testo semplice. Se l'app non copia nulla entro 300 ms gli appunti restano invariati e il risultato è null.
- Non provato contro le app dell'utente (solo ragionamento a livello di unità), come richiesto.

## Come provare

Programma usa e getta (fuori dal repository): `scratchpad\spikes\input\InputSpike.csproj` (console con manifest Per-Monitor V2, riferimento al progetto). Cosa fa:

1. `Start` con impostazioni predefinite, `SetStopKeyActive`, `Apply` con scorciatoie duplicate e `Ctrl+Alt+F9` (verifica dei messaggi in `LastProblems`), hook attivo 5 s con stampa degli eventi.
2. Con `--inject-x2`: installa prima una "rete di sicurezza" (secondo `WH_MOUSE_LL` che inghiotte ogni X2 sfuggito), poi inietta clic X2 senza firma e verifica: attivatore X2 con iniettati accettati -> 2 eventi nostri, 0 sfuggiti; iniettati ignorati -> 0 nostri, 2 alla rete; in pausa -> 0 nostri, 2 alla rete; pausa fra DOWN e UP -> 2 nostri, 0 alla rete. Tutti i casi sono risultati conformi il 22/09/2026.
3. Cattura 900x300 attorno al puntatore e salva `capture.png` (verificato a occhio: contenuto e scala corretti).

Compilare e lanciare con l'SDK locale e `--artifacts-path` privato, come per gli altri moduli.

## Osservazioni utili per gli altri moduli

- Su questa macchina **`Win+Shift+A` (predefinito di `HotkeyReadSelection`) risulta già in uso da un altro programma**: `RegisterHotKey` fallisce con 1409 e il problema compare in `LastProblems`. L'App dovrebbe mostrarlo nelle impostazioni e proporre un'alternativa (es. `Win+Shift+F9`).
- I gestori di `Input` girano su un thread del pool: devono solo accodare (l'orchestratore lo fa già).
- `SetStopKeyActive` e `SetPaused` sono non bloccanti e possono essere chiamati da qualunque thread. `Start` e `Apply` attendono la conferma dell'InputThread (pochi ms, massimo 3 s): chiamarli dal thread di composizione o dal thread UI, mai dall'InputThread stesso.
