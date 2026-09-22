# PuntaEAscolta.Windows.Automation: note di implementazione

Stato al 22/09/2026: compila senza avvisi (`net10.0-windows10.0.19041.0`, ARM64 nativo), verificato con una console usa-e-getta su Chrome, Explorer (barra applicazioni XAML) e anteprima Word Online in Gmail.

## Che cosa c'è

Unico tipo pubblico: `UiaTextSource : IUiTextSource`, costruttore `(ILog log)`.

| File | Ruolo |
|---|---|
| `UiaTextSource.cs` | Facciata: serializza le richieste (una in volo), le esegue sul thread UIA, applica il cane da guardia (2000 ms), gestisce l'annullamento. Non lancia mai. |
| `UiaWorker.cs` | Thread MTA dedicato, in background, senza finestre, con coda `BlockingCollection`. Possiede la `UiaSession`. `Abandon()` lo lascia morire da solo quando la chiamata bloccata torna. |
| `UiaSession.cs` | `CUIAutomation8` con `ConnectionTimeout` 1000, `TransactionTimeout` 2500, `AutoSetFocus` 0; `RawViewWalker`; richiesta di cache (vista raw, solo elemento) con ControlType, Name, ClassName, FrameworkId, AutomationId, BoundingRectangle, IsEnabled, IsOffscreen, HelpText, FullDescription, ItemStatus, AcceleratorKey, AccessKey, IsPassword, LabeledBy, ProcessId, NativeWindowHandle, IsTextPatternAvailable e i pattern Value, Toggle, LegacyIAccessible, Text. |
| `ElementReader.cs` | La logica di raccolta (gira sul thread UIA): `GetElementAt`, `GetSelection`, `FindTooltip`. |
| `ElementSnapshot.cs` | Lettura protetta delle proprietà dalla cache; conversione dei rettangoli (guardia su NaN, infinito, degeneri, oltre 1e6). |
| `ControlTypeMap.cs` | ControlType -> `UiElementKind`; insiemi "controllo con etichetta", "ospite di testo", "può risalire". |
| `ProcessNameCache.cs` | pid -> nome processo, scadenza 60 s, massimo 256 voci. |
| `NativeMethods.cs` | `WindowFromPoint`, `GetAncestor`, `IsHungAppWindow`, `EnumWindows`, `GetWindowRect`, `GetWindowLongPtr`, `GetClassName`, `DwmGetWindowAttribute` (cloaked). Solo lettura. |
| `UiaIds.cs` | Costanti numeriche UIA. |

### GetElementAtAsync

1. `WindowFromPoint` -> radice -> `IsHungAppWindow`: se la finestra non risponde si torna `null` subito (Warn nel log).
2. `ElementFromPointBuildCache` (un solo giro cross-process). Su `UIA_E_ELEMENTNOTAVAILABLE` un solo nuovo tentativo dopo 30 ms: nelle pagine Chrome in movimento il nodo colpito sparisce fra il colpo e la lettura (osservato).
3. Normalizzazione (sonda Affinity):
   - `Text` o `Image` con genitore MenuItem, TabItem, ListItem, Button, SplitButton, CheckBox, RadioButton, TreeItem, Hyperlink, HeaderItem -> si usa il genitore; se il suo nome è vuoto o è un nome di tipo .NET (almeno due punti, segmenti identificatori) si tiene il testo del figlio.
   - Controllo con etichetta e nome cattivo -> `FindAll` dei `Text` discendenti in vista raw (anche fuori schermo, anche con rettangolo vuoto): primo nome non vuoto (Debug se non sono tutti uguali).
   - `Separator`, oppure `MenuItem` con nome cattivo e altezza fra 1 e 10 px -> `Kind = MenuItem`, `Name = null`.
   - Il nome NON viene ripulito (trattini bassi, `StudioPage, Title = X`, LRM dell'orologio): spetta a `LabelCleaner` in Logic.
4. `Value` dal ValuePattern in cache, `ToggleState` dal TogglePattern, `LabeledByName` dall'elemento `LabeledBy`. `LegacyIAccessible` (Name, Description, Value: chiamate vive) **solo se il nome è ancora vuoto**, come raccomandato da probe-office (raddoppia il costo su Office).
5. `ParentKind`/`ParentName` dal genitore raw dell'elemento normalizzato. `ProcessName` dalla cache. `ElapsedMs` misurato sul thread UIA.
6. Contesto di testo (`UiTextContext`), vedi sotto.

### TextPattern (frase sotto il punto)

Ospite di testo, nell'ordine: l'elemento stesso se ha il TextPattern ed è Document, Edit, DataItem, Custom, Pane o Text (Word: `Edit "Contenuto pagina N"`, 0 livelli; TextBlock UWP); altrimenti risalita fino a 6 genitori raw (PDF) fermandosi a una `Window`; altrimenti **via finestra Win32**: `ElementFromHandle(WindowFromPoint)` -> primo figlio con TextPattern (Chromium: `Chrome_RenderWidgetHostHWND` -> Document radice, il cui `RangeFromPoint` funziona anche dentro gli iframe; misurato 3-5 ms a caldo). Si accetta solo se il processo coincide con quello dell'elemento colpito.

Poi l'algoritmo validato 17/17 su Word: `RangeFromPoint` -> `ExpandToEnclosingUnit(Line)` -> verifica sui rettangoli della riga (0,6 h verticale, 1,0 h orizzontale) -> `Paragraph` -> prefisso con `MoveEndpointByRange` -> `offset = lunghezza del prefisso`, `GetText(8000)`; per paragrafi oltre 8000 caratteri finestra di +-6 righe. **Non si spezza in frasi**: lo fa Logic. Quando il punto non è sopra testo (o `RangeFromPoint` fallisce) si restituisce `UiTextContext("", 0, false)`: dice a Logic che l'elemento è un documento ma lì non c'è testo. `Text` è `null` quando non esiste alcun ospite.

Attenzione per Logic: il paragrafo può essere fatto solo di U+FFFC (oggetti incorporati, es. pagine-immagine di Word Online) con `PointerOverText = true`; dopo la pulizia la frase è vuota e si deve passare al passo successivo (OCR).

### GetSelectionAsync

`GetFocusedElementBuildCache` -> TextPattern sull'elemento o sul primo antenato che lo ha (fino a 8 livelli) -> `GetSelection` -> testo concatenato (`GetText(8000)` per intervallo) e rettangoli per riga. `null` se nessuna selezione o solo cursore (testo vuoto). Vale solo per l'app in primo piano.

### FindTooltipAsync

`EnumWindows` -> finestre visibili, non "cloaked" da DWM, non del nostro processo, alte meno di 120 px e larghe meno di 800, con `WS_EX_TOOLWINDOW` oppure classe `tooltips_class32`, `HwndWrapper*`, `Xaml_WindowedPopupClass`, entro 400 px dal punto; ordinate per distanza. Per le prime 3: `ElementFromHandleBuildCache` -> `Name` di un elemento `ToolTip` (radice o discendente); in mancanza, i `Text` discendenti uniti con a capo, ma **solo** se la finestra ha l'aspetto sicuro di un suggerimento (classe nota, oppure `WS_EX_TOOLWINDOW` + `WS_EX_NOACTIVATE`), per non leggere barre fluttuanti. Se nessuna espone testo si restituisce `UiTooltipInfo(null, rettangolo)` della finestra sicura più vicina, per l'OCR. Nessuna ricerca `Descendants` dalla radice del desktop.

### Cane da guardia e thread

- Una sola richiesta in volo (`SemaphoreSlim`): il cane da guardia misura il lavoro vero, non l'attesa in coda. Se la porta resta occupata oltre 2000 ms la richiesta rinuncia (Warn).
- Lavoro oltre 2000 ms -> `null`, `Warn`, thread abbandonato (`CompleteAdding`, resta in background e termina da solo se la chiamata torna), nuovo thread creato subito. Verificato: null dopo 2030 ms, richiesta successiva servita dal nuovo thread in 111 ms (di cui il riscaldamento).
- Annullamento del chiamante: la richiesta torna subito `null`; il lavoro prosegue sotto il cane da guardia, che libera la porta. Verificato: ritorno in 299 ms con token a 300 ms.
- Tutte le eccezioni sono catturate: `COMException` transitorie (ELEMENTNOTAVAILABLE, TIMEOUT, RPC scollegato, E_FAIL) a livello Debug; le altre COM/InvalidCast/Argument ecc. a livello Warn con HRESULT; il resto a Error. Il thread di lavoro non lascia mai uscire eccezioni.
- Log: il testo utente (nomi, paragrafi) compare solo a livello Debug; nessuna chiave o dato sensibile.

## Decisioni

- Nessuna modifica al contratto necessaria. `UiTextContext.ParagraphText` non è annullabile, quindi "documento ma non sopra testo" è codificato come stringa vuota + `PointerOverText = false`.
- `Image` figlio di un controllo con etichetta viene normalizzato al genitore come i `Text` (pulsanti a sola icona di WPF/UWP): estensione prudente della regola Affinity.
- Nomi di tipo .NET: regex `^[A-Za-z_]\w*(\.[A-Za-z_]\w*){2,}$` (almeno due punti) per non scambiare `Relazione.docx` per un nome cattivo; in ogni caso il nome originale viene sostituito solo se si trova un candidato migliore.
- Cache in vista raw (`TreeFilter = RawViewCondition`) per trovare i `Text` fuori schermo di Affinity.
- Il thread UIA non impone il DPI: **il processo che ospita il modulo deve essere Per-Monitor-V2** (manifest o `SetProcessDpiAwarenessContext(-4)` prima di ogni chiamata), altrimenti `WindowFromPoint`, `GetWindowRect` e il confronto con i rettangoli UIA (sempre fisici) non coincidono.

## Limiti e non verificato

- Tooltip: la logica gira senza errori ma non è stata provata su un suggerimento visibile (niente mouse durante la verifica). Da tarare su Affinity, Office e Chrome: classi e stili delle finestre, ritardo di comparsa.
- Selezione: provata solo con il focus su una console (nessun TextPattern -> `null`). Il percorso è quello della sonda Office (7/7).
- Word, Excel, Affinity, Photoshop non erano aperti durante la verifica: il codice segue passo passo `docs/research/probe/office-uia3-sample/UiaPointReader.cs`, ma la prova end-to-end resta da fare con le app del committente.
- Il thread abbandonato dopo un blocco resta vivo finché la chiamata COM non torna (nessun `Abort` in .NET); gli oggetti COM di quel thread restano in memoria. Accettabile: evento raro.
- Costo della risalita nei browser: fino a 6 `GetParentElementBuildCache` (1-2 ms ciascuno) prima del percorso via finestra. Sul Gmail aperto: 53-65 ms totali a freddo, 10-30 ms a caldo; prima chiamata verso ogni processo 80-160 ms (riscaldamento, come misurato dalla sonda Office).
- `LabeledBy`: si prova `CachedName` e, se non è in cache, una chiamata viva; solo quando l'elemento ha davvero un `LabeledBy`.

## Come provare

Console usa-e-getta in `C:\Users\Angelo\AppData\Local\Temp\claude\C--Users-Angelo-Desktop-Matteo\d74c920e-45bd-48a5-abcd-2768715aa453\scratchpad\spikes\uia` (`UiaSpike.csproj`, riferisce il progetto del modulo; imposta Per-Monitor-V2):

```
"C:\Users\Angelo\AppData\Local\Microsoft\dotnet\dotnet.exe" build ...\spikes\uia\UiaSpike.csproj -c Debug --artifacts-path <cartella privata>
...\bin\UiaSpike\debug\UiaSpike.exe            # GetElementAtAsync al puntatore e a 3 punti fissi, FindTooltipAsync, GetSelectionAsync, Dispose
...\bin\UiaSpike\debug\UiaSpike.exe watchdog   # lavoro che dorme 4 s: null a 2 s, nuovo thread, annullamento a 300 ms
...\bin\UiaSpike\debug\UiaSpike.exe climb      # stampa la catena degli antenati e prova il percorso via finestra
...\bin\UiaSpike\debug\UiaSpike.exe diag       # ElementFromPointBuildCache proprietà per proprietà
```

Risultati osservati: orologio della barra applicazioni -> `Button "Orologio 10:03 22/09/2026"` (colpito il `Text`, normalizzato al genitore, XAML, explorer, 10-13 ms); Chrome (5,5) -> `Pane HorizontalTabStripRegionViewOld` senza nome (-> OCR); anteprima Word Online -> `Image "Pagina 3"` con contesto di testo dal Document radice; nessun tooltip, nessuna selezione, Dispose pulito con il thread terminato.

## Revisione del 22/09/2026

- **Valore e stato di spunta erano sempre vuoti**: `AddPattern` mette in cache solo l'oggetto pattern; `CachedValue` e `CachedToggleState` lanciano `E_INVALIDARG` se non sono in cache anche le proprietà `ValueValue` (30045) e `ToggleToggleState` (30086). L'eccezione veniva assorbita da `ElementSnapshot.Safe`: `UiElementInfo.Value` era sempre null e `ToggleState` sempre None (celle di Excel "Cella vuota", caselle combinate senza valore, file di Esplora file letti "Nome"). Le due proprietà sono ora in `UiaIds.CachedProperties`; `ElementReader.ReadValue`/`ReadToggle` ripiegano su `CurrentValue`/`CurrentToggleState` (un giro in più, solo se il pattern esiste) quando la cache non le contiene.
- Verifica automatica in `tests/PuntaEAscolta.Windows.Tests` (`UiaCacheTests`): EDIT e casella Win32 nascoste, mai mostrate, lette con la stessa richiesta di cache della sessione.
- Da verificare dal vivo: l'icona della barra superiore di Affinity (`ToolBar` senza nome, prove dal vivo nota 10) resta "Nessun testo": per un `ToolBar` non si cercano i figli `Text`, perché ne ha uno per icona e con rettangoli vuoti non si sa quale sia sotto il puntatore.
