# UI Automation da .NET 10 — dal punto dello schermo al testo da pronunciare

Ricerca per "Punta e Ascolta" — stato al 21 settembre 2026.
Ambito: come ottenere, dato un punto dello schermo, l'elemento UI Automation (UIA) piu specifico e il suo testo "pronunciabile", in C# su .NET 10, per Windows 11 x64 e ARM64.

> Nota di metodo. Sulla macchina di sviluppo non c'e ancora il .NET SDK, quindi **nessuno degli sketch C# di questo documento e stato compilato**. Le firme dei metodi seguono l'interop generato da `tlbimp` (pacchetto `Interop.UIAutomationClient`) e vanno verificate al primo build. Dove un'affermazione non viene da documentazione ufficiale ma da esperienza/sorgenti di terzi, e indicato.

---

## Raccomandazione

**Usare l'API COM "UIA3" (`IUIAutomation` / `CUIAutomation8`) tramite l'assembly di interop del pacchetto NuGet `Interop.UIAutomationClient` 10.19041.0, incapsulata in un nostro wrapper sottile dentro il layer Windows.** Niente `System.Windows.Automation` gestito; FlaUI.UIA3 resta come piano B e come riferimento di codice.

Motivi, in ordine di peso:

1. **Timeout e robustezza.** Solo l'API COM espone `IUIAutomation2.ConnectionTimeout` / `TransactionTimeout` (default 2 s / 20 s) e `AutoSetFocus`. Per un'app assistiva che non deve mai bloccarsi su un'app che non risponde, 20 s di default sono inaccettabili e vanno abbassati. L'API gestita non lo permette.
2. **Copertura funzionale.** L'API COM ha `TextPattern2`, `TextRange2/3`, `TextChildPattern`, `FullDescription`, `ElementFromPointBuildCache` con cache request completa. L'API gestita (`UIAutomationClient.dll` di WPF) e ferma al modello del 2006-2010 e la stessa Microsoft, in testa alla sua documentazione, rimanda alla "Windows Automation API" nativa per le informazioni aggiornate.
3. **ARM64 senza sorprese.** L'assembly di interop e puro IL (solo metadati di interfacce COM): lo stesso file funziona in un processo `win-x64` e in uno `win-arm64`. Il codice nativo (`UIAutomationCore.dll`) e un componente di sistema, nativo ARM64 su Windows 11 on Arm. Il COM interop integrato (RCW) di .NET e supportato su `win-arm64`.
4. **Superficie minima.** Ci servono una quindicina di chiamate UIA. FlaUI e pensato per il test automation (retry, input simulato, wrapper per ogni pattern): e codice in piu fra noi e i timeout/le eccezioni, l'ultima release e del 25 febbraio 2025, e comunque usa sotto lo stesso identico pacchetto di interop. Andare diretti da controllo totale su cache request, threading ed errori.
5. **Build semplice.** `<COMReference>` non funziona con `dotnet build` (errore MSB4803, richiede MSBuild di .NET Framework): il pacchetto NuGet gia pronto evita `tlbimp`/`midl` e Visual Studio.

Alternativa "moderna" valutata e scartata per ora: **CsWin32** (`Microsoft.Windows.CsWin32`) genera le interfacce `IUIAutomation*` dai metadati Win32 ed e compatibile con trimming/NativeAOT (`allowMarshaling: false` + ComWrappers). Ma obbliga a gestire a mano `BSTR`/`VARIANT`/`SAFEARRAY` e puntatori; WPF comunque non e trimmabile, quindi il vantaggio AOT non ci serve. Da riconsiderare solo se il processo "motore" venisse separato dalla UI WPF.

Decisioni di progetto collegate:

- Tutte le chiamate UIA su **un thread dedicato MTA senza finestre** (mai sul thread dell'hook del mouse, mai sul thread UI WPF), con **watchdog** e thread sostituibile se una chiamata non torna.
- Processo **Per-Monitor V2 DPI aware**: UIA lavora in coordinate fisiche.
- `ConnectionTimeout` circa 1000 ms, `TransactionTimeout` circa 2000-3000 ms, `AutoSetFocus = false`.
- Una sola chiamata cross-process per l'elemento: `ElementFromPointBuildCache` con le proprieta che servono gia in cache.
- Catena di fallback esplicita: UIA (Name e simili) → tooltip visibile → euristiche sul vicinato → **OCR**. L'OCR non e un'eccezione: per Photoshop, per i pannelli di Affinity e per i pulsanti solo-icona senza nome e il percorso normale.
- Pubblicazione **non trimmed**, self-contained, due cartelle portabili (`win-x64`, `win-arm64`). Niente `uiAccess` (incompatibile con la distribuzione in cartella portabile): le finestre elevate restano fuori portata di UIA.

---

## Dettagli tecnici

### 1. Le tre opzioni a confronto

| | `System.Windows.Automation` gestito | COM UIA3 via `Interop.UIAutomationClient` (raccomandato) | FlaUI.UIA3 |
|---|---|---|---|
| Dove sta | `UIAutomationClient.dll`, `UIAutomationTypes.dll`, `UIAutomationClientSideProviders.dll` dentro `Microsoft.WindowsDesktop.App` (serve `UseWPF=true`) | NuGet `Interop.UIAutomationClient` **10.19041.0** (17 lug 2020, autore Roemer/FlaUI; target netstandard2.0 / net35+; puro IL) | NuGet `FlaUI.UIA3` **5.0.0** (25 feb 2025; target net48, net6.0-windows, net8.0-windows → compatibile net10.0-windows); dipende da `FlaUI.Core` 5.0.0 e da `Interop.UIAutomationClient` >= 10.19041.0 |
| Implementazione | P/Invoke verso la vecchia API "flat" di `UIAutomationCore.dll` (`UiaNodeFromPoint`, ...) + proxy client-side gestiti | RCW .NET sopra `CUIAutomation8` (COM, in-proc client in `UIAutomationCore.dll`) | Wrapper a oggetti sopra la colonna centrale |
| Timeout configurabili | No | Si: `IUIAutomation2.ConnectionTimeout/TransactionTimeout` | Si (li espone come `TimeSpan` su `UIA3Automation`) |
| `TextPattern2`, `TextRange3`, `FullDescription`, `TextChild`, Annotation | No | Si (IDL SDK 10.0.19041: fino a `IUIAutomation6`, `IUIAutomationElement9`) | Si |
| Caching | `CacheRequest` (thread-static, scomodo) | `IUIAutomationCacheRequest` esplicita | Wrapper di quella COM |
| Eccezioni | `ElementNotAvailableException`, ecc. | `COMException` con HRESULT + eccezioni mappate (`TimeoutException`, ...) | Eccezioni FlaUI tradotte da `Com.Call` |
| ARM64 | Si (WPF e supportato su win-arm64) | Si (IL puro + COM di sistema) | Si in pratica (stessa base); nessuna dichiarazione ufficiale |
| Manutenzione | Congelata (solo bugfix WPF) | Interfacce COM immutabili: il pacchetto del 2020 resta valido | Attivo ma lento; ultima release feb 2025 |
| Giudizio | Scartata | **Scelta** | Piano B / riferimento |

Note:

- Il repository `FlaUI/UIAutomation-Interop` contiene gli IDL degli SDK 10.0.16299 → 10.0.22621; il pacchetto pubblicato su NuGet e fermo alla 10.19041.0. Tutto cio che ci serve (timeouts, TextPattern2, TextRange3, LegacyIAccessible, cache) esiste da Windows 8/10 1809 ed e quindi incluso.
- Namespace dell'interop: `Interop.UIAutomationClient` (FlaUI lo usa come `using UIA = Interop.UIAutomationClient;`). Non collide con `System.Windows.Automation` di WPF, ma per pulizia conviene mettere il codice UIA in una class library separata **senza** `UseWPF`.
- `EmbedInteropTypes` (NoPIA) e supportato su .NET moderno in Windows; si puo anche lasciare la DLL di interop accanto all'eseguibile (circa 200 KB). Partire senza embedding (meno sorprese con i cast fra interfacce), valutarlo dopo.
- Il COM interop integrato viene disattivato da trimming/AOT (`BuiltInComInteropSupport=false`): **non** usare `PublishTrimmed`/`PublishAot` per l'eseguibile che contiene UIA.

Schema di progetto suggerito:

```xml
<!-- PuntaEAscolta.Windows.Uia.csproj : class library, no WPF -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <Platforms>AnyCPU</Platforms>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Interop.UIAutomationClient" Version="10.19041.0" />
    <ProjectReference Include="..\PuntaEAscolta.Core\PuntaEAscolta.Core.csproj" />
  </ItemGroup>
</Project>

<!-- App (WPF + tray): RuntimeIdentifiers win-x64;win-arm64, SelfContained, PublishTrimmed=false,
     app.manifest con <dpiAwareness>PerMonitorV2</dpiAwareness> -->
```

### 2. Creazione del client e impostazioni globali

```csharp
using UIA = Interop.UIAutomationClient;

internal sealed class UiaContext
{
    public UIA.IUIAutomation Automation { get; }
    public UIA.IUIAutomationCacheRequest ProbeCache { get; }
    public UIA.IUIAutomationTreeWalker RawWalker { get; }

    public UiaContext()   // MUST run on the dedicated MTA thread
    {
        // CUIAutomation8 = CLSID that implements IUIAutomation2+ (timeouts). Windows 8+.
        var automation = new UIA.CUIAutomation8();
        Automation = automation;

        var a2 = (UIA.IUIAutomation2)automation;
        a2.ConnectionTimeout  = 1000;   // ms, default 2000
        a2.TransactionTimeout = 2500;   // ms, default 20000
        a2.AutoSetFocus = 0;            // never let UIA move focus as a side effect (BOOL as int in the interop)

        RawWalker = automation.RawViewWalker;

        var cr = automation.CreateCacheRequest();
        cr.TreeScope = UIA.TreeScope.TreeScope_Element;
        cr.AutomationElementMode = UIA.AutomationElementMode.AutomationElementMode_Full;
        cr.TreeFilter = automation.RawViewCondition;      // default is ControlView
        foreach (int pid in new[] {
            UiaIds.Name, UiaIds.ControlType, UiaIds.LocalizedControlType, UiaIds.HelpText,
            UiaIds.ItemStatus, UiaIds.BoundingRectangle, UiaIds.ClassName, UiaIds.FrameworkId,
            UiaIds.ProcessId, UiaIds.NativeWindowHandle, UiaIds.IsOffscreen, UiaIds.IsPassword,
            UiaIds.IsEnabled, UiaIds.AutomationId,
            UiaIds.IsTextPatternAvailable, UiaIds.IsValuePatternAvailable,
            UiaIds.IsLegacyIAccessiblePatternAvailable })
            cr.AddProperty(pid);
        ProbeCache = cr;
    }
}

internal static class UiaIds   // numeric ids are part of the public UIA contract
{
    public const int BoundingRectangle = 30001, ProcessId = 30002, ControlType = 30003,
        LocalizedControlType = 30004, Name = 30005, IsEnabled = 30010, AutomationId = 30011,
        ClassName = 30012, HelpText = 30013, IsPassword = 30019, NativeWindowHandle = 30020,
        IsOffscreen = 30022, FrameworkId = 30024, ItemStatus = 30026,
        IsTextPatternAvailable = 30040, IsValuePatternAvailable = 30043, ValueValue = 30045,
        ExpandCollapseState = 30070, SelectionItemIsSelected = 30079, ToggleState = 30086,
        IsLegacyIAccessiblePatternAvailable = 30090,
        LegacyName = 30092, LegacyValue = 30093, LegacyDescription = 30094,
        LegacyRole = 30095, LegacyState = 30096, LegacyHelp = 30097,
        FullDescription = 30159;

    public const int ValuePattern = 10002, ExpandCollapsePattern = 10005, GridItemPattern = 10007,
        SelectionItemPattern = 10010, TextPattern = 10014, TogglePattern = 10015,
        LegacyIAccessiblePattern = 10018, TextPattern2 = 10024, TextChildPattern = 10029;

    public const int Button = 50000, CheckBox = 50002, ComboBox = 50003, Edit = 50004,
        Hyperlink = 50005, Image = 50006, ListItem = 50007, List = 50008, Menu = 50009,
        MenuBar = 50010, MenuItem = 50011, RadioButton = 50013, StatusBar = 50017, Tab = 50018,
        TabItem = 50019, Text = 50020, ToolBar = 50021, ToolTip = 50022, Tree = 50023,
        TreeItem = 50024, Custom = 50025, Group = 50026, DataGrid = 50028, DataItem = 50029,
        Document = 50030, SplitButton = 50031, Window = 50032, Pane = 50033, HeaderItem = 50035,
        Table = 50036, TitleBar = 50037, Separator = 50038;
}
```

(L'interop contiene gia classi di costanti `UIA_PropertyIds`, `UIA_PatternIds`, `UIA_ControlTypeIds`; la tabella sopra serve solo a rendere leggibili gli sketch.)

### 3. Modello di threading

Documentazione ufficiale ("Understanding Threading Issues", aggiornata 14 lug 2025): se il client puo toccare anche la propria UI, **tutte** le chiamate UIA vanno fatte da un thread separato, che **non possiede finestre** ed e **MTA**; gli handler di eventi vanno aggiunti/rimossi da un thread non-UI MTA, sempre lo stesso.

Conseguenze per noi:

- **Thread dell'hook** (`WH_MOUSE_LL`): ha un message loop e deve rispondere in pochi ms; se la callback supera `LowLevelHooksTimeout` Windows rimuove silenziosamente l'hook. Nella callback solo: leggere `MSLLHOOKSTRUCT.pt`, accodare la richiesta, decidere se "mangiare" il click, tornare. **Mai UIA qui.**
- **Thread UI WPF** (STA): mai UIA qui. Oggetti UIA creati su STA e usati da altri thread verrebbero marshalati verso lo STA → lentezza e deadlock.
- **Thread UIA dedicato**: `ApartmentState.MTA`, `IsBackground = true`, nessuna finestra, coda di richieste. `CUIAutomation8` e gli elementi si creano e si usano qui. Gli oggetti creati in MTA sono comunque utilizzabili da qualunque thread MTA (il thread pool .NET e MTA), ma un thread dedicato da serializzazione e, soprattutto, la possibilita di **abbandonarlo** se resta bloccato.
- Verso il resto dell'app escono solo DTO del core (`PointedItem { Text, Kind, Source, Bounds, ... }`), mai oggetti COM.

```csharp
public sealed class UiaWorker : IDisposable
{
    private readonly BlockingCollection<Action<UiaContext>> _queue = new();
    private readonly Thread _thread;
    public volatile bool Poisoned;               // set by the watchdog when a call never returned

    public UiaWorker()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "UIA-MTA" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    private void Loop()
    {
        var ctx = new UiaContext();              // COM objects live and die on this thread
        foreach (var job in _queue.GetConsumingEnumerable())
        {
            try { job(ctx); } catch { /* each job reports its own errors through its TCS */ }
        }
    }

    public Task<T> RunAsync<T>(Func<UiaContext, T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(ctx => { try { tcs.SetResult(work(ctx)); } catch (Exception ex) { tcs.SetException(ex); } });
        return tcs.Task;
    }

    public void Dispose() => _queue.CompleteAdding();
}

public sealed class UiaService
{
    private UiaWorker _worker = new();
    private int _zombies;

    public async Task<T?> TryAsync<T>(Func<UiaContext, T> work, TimeSpan deadline) where T : class
    {
        var w = _worker;
        var task = w.RunAsync(work);
        if (await Task.WhenAny(task, Task.Delay(deadline)) != task)
        {
            // The target app is probably hung inside a cross-process call. Threads cannot be aborted
            // in .NET: abandon this worker (background thread) and start a fresh one.
            w.Poisoned = true;
            if (Interlocked.Increment(ref _zombies) <= 3) _worker = new UiaWorker();
            _ = task.ContinueWith(t => { _ = t.Exception; Interlocked.Decrement(ref _zombies); w.Dispose(); });
            return null;                          // caller falls back to OCR
        }
        try { return await task; }
        catch (Exception ex) when (UiaErrors.IsExpected(ex)) { return null; }
    }
}
```

### 4. Dal punto all'elemento piu specifico

**Coordinate.** UIA usa coordinate **fisiche** ("Understanding Screen Scaling Issues", 14 lug 2025): `ElementFromPoint`, `BoundingRectangle`, `GetClickablePoint`, `RangeFromPoint`, `GetBoundingRectangles`. `MSLLHOOKSTRUCT.pt` e documentato come "per-monitor-aware screen coordinates". Con manifest `PerMonitorV2` le due cose coincidono e non serve alcuna conversione (servirebbe solo per posizionare *nostre* finestre WPF, che ragionano in DIP). Non usare `System.Windows.Forms.Cursor.Position`/`GetCursorPos` da un processo non DPI-aware.

**Chiamata principale.** Una sola transazione cross-process:

```csharp
static UIA.IUIAutomationElement? ElementAt(UiaContext ctx, int x, int y)
{
    var pt = new UIA.tagPOINT { x = x, y = y };
    for (int attempt = 0; attempt < 2; attempt++)
    {
        try { return ctx.Automation.ElementFromPointBuildCache(pt, ctx.ProbeCache); }
        catch (COMException ex) when ((uint)ex.HResult == 0x80040201) { /* UIA_E_ELEMENTNOTAVAILABLE: retry once */ }
    }
    return null;
}
```

La documentazione di `ElementFromPoint` dice esplicitamente che puo tornare `UIA_E_ELEMENTNOTAVAILABLE` se l'elemento sparisce durante la chiamata e che il client deve ritentare.

**Cosa restituisce.** UIA individua l'HWND sotto il punto e chiede al provider (`ElementProviderFromPoint`, o `accHitTest` per i proxy MSAA) l'elemento piu profondo che *il provider* conosce. Di solito e gia la foglia giusta (voce di menu, pulsante del ribbon, cella). Non lo e quando:

- il framework fa hit-test grossolano (Qt, Java, vecchi toolkit, alcuni pannelli custom) → torna `Pane`/`Group`/`Custom`/`Window`;
- Chromium/Electron non ha ancora attivato l'accessibilita del renderer → torna il contenitore (vedi §11);
- c'e una finestra overlay trasparente di un'altra utility sopra;
- WPF: l'hit-test torna spesso il `TextBlock` interno (foglia **troppo** profonda, vedi sotto) oppure il contenitore se il contenuto ha `IsHitTestVisible=false`.

**Discesa (drill-down) con budget.** Solo se il tipo tornato e un contenitore e non ha nome utile:

```csharp
static readonly HashSet<int> Containers = new() {
    UiaIds.Window, UiaIds.Pane, UiaIds.Group, UiaIds.Custom, UiaIds.ToolBar, UiaIds.List,
    UiaIds.Tab, UiaIds.Table, UiaIds.DataGrid, UiaIds.Tree, UiaIds.Menu, UiaIds.MenuBar, UiaIds.StatusBar };

static UIA.IUIAutomationElement Deepest(UiaContext ctx, UIA.IUIAutomationElement start, int x, int y, Stopwatch budget)
{
    var current = start;
    var childCache = ctx.ProbeCache;             // same properties, TreeScope_Element, raw view
    for (int depth = 0; depth < 6 && budget.ElapsedMilliseconds < 400; depth++)
    {
        if (!Containers.Contains(current.CachedControlType)) break;
        UIA.IUIAutomationElementArray kids;
        try { kids = current.FindAllBuildCache(UIA.TreeScope.TreeScope_Children,
                                               ctx.Automation.CreateTrueCondition(), childCache); }
        catch (Exception ex) when (UiaErrors.IsExpected(ex)) { break; }

        UIA.IUIAutomationElement? best = null; double bestArea = double.MaxValue;
        int n = Math.Min(kids.Length, 300);      // never enumerate a 10k-item list
        for (int i = 0; i < n; i++)
        {
            var k = kids.GetElement(i);
            var r = k.CachedBoundingRectangle;   // tagRECT, physical pixels
            if (k.CachedIsOffscreen != 0) continue;
            if (x < r.left || x >= r.right || y < r.top || y >= r.bottom) continue;
            double area = (double)(r.right - r.left) * (r.bottom - r.top);
            if (area > 0 && area < bestArea) { best = k; bestArea = area; }
        }
        if (best is null) break;
        current = best;
    }
    return current;
}
```

**Risalita (normalizzazione).** La foglia non e sempre la cosa giusta da leggere. Dato reale dalla sonda fatta su questa macchina su Affinity v3 (file `docs/research/probe/affinity-uia-tree-main.txt`, prodotto da un'altra attivita di ricerca; Affinity risulta un'app **WPF**, `FrameworkId='WPF'`, e un albero UIA lo espone):

- `MenuItem Name='File'` ha come figlio `Text Name='_File'` → il `TextBlock` interno porta l'underscore del tasto di accesso WPF;
- `ListItem Name='Serif.Affinity.Workspaces.Workspace'` (e il `ToString()` dell'oggetto dati) ha come figlio `Text Name='Vettore'` → qui e il **figlio** ad avere il nome giusto;
- 40 `Button` su 40 hanno `Name=''`; 7 hanno `HelpText` utile (`'Home'`, `'Gestione studio'`, `'Esporta'`, `'Imposta riempimento'`, ...);
- nelle `ToolBar` ci sono `Text` fuori schermo (`IsOffscreen=True`, rettangolo vuoto, `AutomationId='text'`) con l'etichetta del gruppo (`'Disponi'`, `'Trasforma'`, `'Allineamento'`) accanto a un `Button` senza nome.

Regola generale che ne deriva: costruire una lista di **candidati** = [elemento, genitore se l'elemento e `Text`/`Image` e il genitore e `Button`/`MenuItem`/`ListItem`/`TabItem`/`TreeItem`/`CheckBox`/`RadioButton`/`Hyperlink`/`DataItem`/`SplitButton`, figli `Text` diretti se l'elemento non ha nome valido] e scegliere il primo testo che supera `IsSpeakable` (vedi §5). Il genitore si ottiene con `ctx.RawWalker.GetParentElementBuildCache(el, ctx.ProbeCache)`; massimo 2 livelli.

### 5. Che cosa pronunciare: proprieta e priorita per ControlType

Proprieta disponibili, dalla piu alla meno affidabile come "etichetta":

1. `Name` — per definizione "lo stesso testo dell'etichetta a schermo", senza `&` dei mnemonici e senza il tipo di controllo.
2. `LabeledBy.Name` — per Edit/ComboBox etichettati da un testo statico.
3. `ValuePattern.Value` — il *contenuto* (Edit, ComboBox, cella Excel).
4. `HelpText` — tooltip/descrizione; in WPF il peer usa il `ToolTip` stringa come HelpText quando non c'e altro (spiega i 7 pulsanti di Affinity).
5. `LegacyIAccessible.Name` / `.Description` / `.Value` / `.Help` — utile con provider MSAA-only (vecchie toolbar Win32, app legacy). Richiederlo **solo come fallback**: su provider UIA nativi attiva il bridge UIA→MSAA, e lento e puo fallire.
6. `FullDescription` (30159), `ItemStatus` (30026; breve stato testuale: aggiungerlo dopo il nome se non vuoto), `ItemType`.
7. `LocalizedControlType` ("pulsante") — solo come ultima risorsa configurabile; di norma **non** pronunciarlo (l'utente vuole il nome, non il ruolo).
8. `AutomationId`/`ClassName` — mai pronunciarli; servono alle euristiche (es. `PART_Close` → "Chiudi" con una piccola tabella di mapping per i pulsanti di sistema senza nome).

| ControlType | Testo principale | Fallback in ordine | Note |
|---|---|---|---|
| Button, SplitButton, MenuItem, TabItem, Hyperlink, TreeItem, ListItem, HeaderItem | `Name` | genitore/figlio Text → `LabeledBy` → `HelpText` → Legacy.Name → Legacy.Description → tooltip visibile → OCR | stato opzionale: `Toggle` (attivo), `SelectionItem.IsSelected`, `IsEnabled=false` ("non disponibile") |
| CheckBox, RadioButton | `Name` (+ stato da `TogglePattern`/`SelectionItem`) | come sopra | lo stato e informazione utile per chi non legge |
| Edit | se ha `TextPattern` e testo lungo → frase al puntatore (§9); altrimenti `Value` se non vuoto, altrimenti `Name`/`LabeledBy` | HelpText (placeholder) | `IsPassword=true` → mai leggere il valore |
| ComboBox | `Name` + `Value` (voce selezionata) | `SelectionPattern` → nome dell'item selezionato | es. "Tipo di carattere: Calibri" |
| Document | `TextPattern` → frase al puntatore | `Name` | Word, editor, pagine web |
| Text | `Name` | se `TextPattern` disponibile e nome lungo → frase/riga al puntatore | nel web un nodo di testo puo essere un intero paragrafo |
| DataItem (cella) | `Value.Value` | `Name` | Excel: vedi §8 |
| Image | `Name` | HelpText → FullDescription → **OCR** | foto con scritte → sempre OCR |
| ToolTip | `Name` | figli Text | |
| Slider, Spinner, ProgressBar | `Name` + `RangeValue.Value`/`Value` | | |
| TitleBar, Window | `Name` solo se il puntatore e sulla barra del titolo | | altrimenti drill-down/OCR |
| Pane, Group, Custom, ToolBar, List, Table, StatusBar, Tab | drill-down (§4) | `Name` se sensato → OCR | mai leggere un contenitore intero |
| Separator, Thumb, ScrollBar | niente | | silenzio e una risposta valida |

Filtro `IsSpeakable` e pulizia (vanno nel **core portabile**, sono stringhe pure):

```csharp
public static class SpeakableText
{
    // "Serif.Affinity.Workspaces.Workspace", "System.Windows.Controls.ListBoxItem: ..." etc.
    static readonly Regex TypeName = new(@"^[A-Za-z_]\w*(\.[A-Za-z_]\w*){2,}(:.*)?$", RegexOptions.Compiled);

    public static bool IsSpeakable(string? s) =>
        !string.IsNullOrWhiteSpace(s) && s.Any(char.IsLetterOrDigit) && !TypeName.IsMatch(s.Trim());

    public static string Clean(string s, bool wpfAccessKey)
    {
        int tab = s.IndexOf('\t');                       // "Salva\tCtrl+S" in some Win32 menus
        if (tab >= 0) s = s[..tab];
        s = s.Replace("\uFFFC", " ").Replace("\a", " "); // embedded object / Word cell mark
        if (wpfAccessKey) s = Regex.Replace(s, @"_(?=[^_])", "", RegexOptions.None, TimeSpan.FromMilliseconds(50))
                                   .Replace("__", "_");  // "_File" -> "File"
        s = Regex.Replace(s, @"&(?=[^&])", "").Replace("&&", "&");   // Win32 mnemonic leaked via MSAA
        s = EmojiFilter.Strip(s);                        // Extended_Pictographic, ZWJ U+200D, VS16 U+FE0F,
                                                         // skin tones U+1F3FB-1F3FF, regional indicators, keycaps
        return Regex.Replace(s, @"\s+", " ").Trim();
    }
}
```

Estrazione (lato Windows):

```csharp
static string? BestLabel(UiaContext ctx, UIA.IUIAutomationElement el)
{
    bool wpf = el.CachedFrameworkId == "WPF";
    string? Try(string? raw) => SpeakableText.IsSpeakable(raw) ? SpeakableText.Clean(raw!, wpf) : null;

    var text = Try(el.CachedName);
    if (text is null && el.CurrentLabeledBy is { } lbl) text = Try(lbl.CurrentName);
    text ??= Try(el.CachedHelpText);

    if (text is null && (bool)el.GetCachedPropertyValue(UiaIds.IsLegacyIAccessiblePatternAvailable))
    {
        try
        {
            var legacy = (UIA.IUIAutomationLegacyIAccessiblePattern)el.GetCurrentPattern(UiaIds.LegacyIAccessiblePattern);
            text = Try(legacy.CurrentName) ?? Try(legacy.CurrentDescription) ?? Try(legacy.CurrentHelp);
        }
        catch (Exception ex) when (UiaErrors.IsExpected(ex)) { }
    }

    var status = Try(el.CachedItemStatus);
    return text is null ? null : status is null ? text : $"{text}, {status}";
}

static string? ValueOf(UIA.IUIAutomationElement el)
{
    if (el.CachedIsPassword != 0) return null;
    if (!(bool)el.GetCachedPropertyValue(UiaIds.IsValuePatternAvailable)) return null;
    var vp = (UIA.IUIAutomationValuePattern)el.GetCurrentPattern(UiaIds.ValuePattern);
    return vp.CurrentValue;
}
```

Attenzione: i getter `Cached*` lanciano se la proprieta non era nella cache request; `GetCurrentPattern` torna `null` se il pattern non c'e (il cast su `null` e innocuo, il successivo accesso no: controllare).

### 6. Pulsanti solo-icona e tooltip

Ordine consigliato quando `Name` e vuoto:

1. `HelpText`, `LabeledBy`, Legacy (vedi sopra).
2. **Tooltip attualmente visibile.** Il tipo di controllo `ToolTip` ha per specifica `Name` = testo mostrato; la documentazione dice anche che, quando il tooltip non e focalizzabile, il suo contenuto dovrebbe essere disponibile come `HelpText` dell'elemento a cui si riferisce (spesso non lo e). Due modi per trovarlo:
   - *senza eventi* (consigliato per iniziare): al momento del trigger, `EnumWindows` sulle finestre top-level visibili con classe nota — `tooltips_class32` (Win32 comuni, Photoshop), `Xaml_WindowedPopupClass` (WinUI), popup WPF (`HwndWrapper[...]` la cui radice UIA e `ToolTip`), `NetUIToolWindow`/simili per Office — il cui rettangolo e vicino al puntatore (entro circa 400 px). Testo con `GetWindowText` oppure `ElementFromHandle(hwnd).CurrentName` (+ figli `Text`).
   - *con eventi*: `AddAutomationEventHandler(UIA_ToolTipOpenedEventId /*20000*/, root, TreeScope_Subtree, ...)` dal thread MTA; si memorizza l'ultimo tooltip (testo, rettangolo, ora). E quello che la documentazione raccomanda ai client, ma un handler sul desktop intero ha un costo e una superficie di guasto maggiore; in alternativa piu leggera `SetWinEventHook(EVENT_OBJECT_SHOW)` filtrando per classe.
   - Nota pratica: il clic centrale, se **non** viene inoltrato all'app (l'hook lo consuma), non fa sparire il tooltip; se viene inoltrato, molte app lo chiudono subito → catturare il tooltip *prima* di qualsiasi altra cosa.
3. Euristiche di vicinato (Affinity): figli/fratelli `Text` anche `IsOffscreen` dentro la stessa `ToolBar`; mapping di `AutomationId` noti (`PART_Close`, `PART_Mini`, `PART_Maxi`).
4. **OCR** della zona attorno al puntatore (o del rettangolo del tooltip, se c'e ma non ha testo UIA).
5. Ultima risorsa configurabile: `LocalizedControlType` oppure silenzio.

### 7. Menu popup Win32 (#32768) e ribbon di Office

**Menu Win32 standard** (classe finestra `#32768`, usati da Blocco note classico, molte app Win32 e — da verificare — Photoshop): non hanno un provider UIA nativo, UIA li espone tramite il proxy MSAA. `ElementFromPoint` torna direttamente il `MenuItem` sotto il puntatore, con `Name` = testo della voce (il proxy toglie `&`; la scorciatoia sta in `AcceleratorKey`/`LegacyIAccessible.KeyboardShortcut`, ma alcuni menu la lasciano nel nome dopo un `\t`: tagliarla). Per specifica il `MenuItem` Win32 supporta sempre `Invoke` e `Toggle` solo quando e spuntato. Limiti: voci **owner-draw** (`MFT_OWNERDRAW`) hanno nome vuoto → OCR; separatori → silenzio. Il menu popup e una finestra top-level separata dall'app: nessun problema per `ElementFromPoint`, ma non cercare di risalire al menu padre per il contesto.

**Ribbon e menu di Office** (finestre `NetUIHWND`, framework "NetUI"): provider UIA nativo e di buona qualita. I pulsanti, anche solo-icona (Grassetto, Corsivo, Incolla), hanno `Name` localizzato; `HelpText` contiene spesso la descrizione estesa (supertip); `SplitButton` e composto da due parti distinte (pulsante e freccia/`MenuItem`) e `ElementFromPoint` torna la parte sotto il puntatore; gallerie = `List`/`ListItem` con nome (es. nomi degli stili). Backstage (File) e menu contestuali sono anch'essi NetUI. Regola: pronunciare solo `Name`; `HelpText` solo se `Name` manca (eventuale "secondo clic = descrizione estesa" e una decisione di UX, non tecnica). *(Dettagli NetUI: conoscenza consolidata ma non documentata ufficialmente → da confermare con la sonda, vedi Domande aperte.)*

### 8. Celle di Excel

Dal sorgente di NVDA (`source/NVDAObjects/UIA/excel.py`, ramo master) — che e la migliore documentazione di fatto sul provider UIA di Excel:

- la cella e un elemento `DataItem` (NVDA lo mappa a TABLECELL) dentro la griglia del foglio (finestra `EXCEL7`);
- il **`Name` e la coordinata**, non il contenuto; nelle build recenti la lettera e tra virgolette e separata da uno spazio: `"A" 1` (NVDA toglie virgolette e spazio per ottenere `A1`);
- il **contenuto visualizzato sta in `ValuePattern.Value`** (`UIA_ValueValuePropertyId`);
- Excel espone proprieta custom registrate (formula, formato numerico, convalida dati); le celle unite sono un solo elemento UIA;
- NVDA considera il supporto UIA di Excel "sperimentale, non completo", consigliato solo da build 16.0.13522.10000; per Word invece usa UIA di default da Word 16.0.15000 su Windows 11.

Quindi per la cella: pronunciare `Value` (testo formattato come appare), opzionalmente preceduto dalla coordinata ripulita (impostazione per l'assistente: "solo contenuto" / "coordinata + contenuto"); cella vuota → silenzio oppure "vuota" (configurabile).

```csharp
static string? ExcelCell(UIA.IUIAutomationElement el, bool sayCoords)
{
    if (el.CachedControlType != UiaIds.DataItem) return null;
    var value = ValueOf(el);
    var coords = el.CachedName?.Replace("\"", "").Replace(" ", "");       // "\"B\" 3" -> "B3"
    if (string.IsNullOrWhiteSpace(value)) return null;
    return sayCoords && !string.IsNullOrEmpty(coords) ? $"{coords}: {value}" : value;
}
```

Piano B se `ElementFromPoint` sulla griglia non torna la cella (da verificare): modello a oggetti di Excel, `Window.RangeFromPoint(x, y)` (coordinate schermo in pixel) raggiunto con `AccessibleObjectFromWindow(hwndEXCEL7, OBJID_NATIVEOM /*-16*/, IID_IDispatch)` e late binding (`dynamic`), poi `Range.Text`. Funziona anche in modalita modifica? No: in edit mode l'OM di Excel rifiuta le chiamate (`RPC_E_CALL_REJECTED`/0x800AC472) → gestire e ricadere su OCR.

### 9. Word: la FRASE sotto il puntatore

`TextUnit` UIA ha solo: `Character(0)`, `Format(1)`, `Word(2)`, `Line(3)`, `Paragraph(4)`, `Page(5)`, `Document(6)`. **Non esiste l'unita "frase".** (Word offre la navigazione per frase solo tramite un'estensione custom via UIA Remote Operations, usata da NVDA: troppo per noi.) Strategia: prendere il **paragrafo**, calcolare l'**offset** del punto dentro il paragrafo, tagliare la frase nel core portabile.

Algoritmo:

1. `ElementFromPoint` → se l'elemento non ha `TextPattern` (puo essere un figlio: collegamento, cella di tabella, elemento di pagina) risalire con il `RawViewWalker` finche `IsTextPatternAvailable` e vero (max circa 8 livelli; il documento di Word e la finestra di classe `_WwG`, ControlType `Document`).
2. `IUIAutomationTextPattern.RangeFromPoint(pt)` → range **degenere** (vuoto) *piu vicino* al punto. Attenzione: "piu vicino" significa che anche puntando nel margine o in uno spazio bianco si ottiene un range valido → va **validato** (passo 3). Se il punto e sopra un oggetto incorporato (immagine, tabella Excel) la documentazione dice che torna un range che avvolge l'oggetto → in quel caso passare all'OCR.
3. Validazione: clonare, `ExpandToEnclosingUnit(TextUnit_Word)` (o `Character`), `GetBoundingRectangles()` e controllare che il punto sia dentro uno dei rettangoli con una tolleranza (es. 12 px fisici scalati col DPI). Se no → "nessun testo sotto il puntatore" → tentare OCR (potrebbe essere un'immagine nel documento).
4. `para = range.Clone(); para.ExpandToEnclosingUnit(TextUnit_Paragraph)`. `ExpandToEnclosingUnit` "normalizza" il range all'unita richiesta; se l'unita non e supportata il provider passa a quella piu grande successiva.
5. Offset: `prefix = para.Clone(); prefix.MoveEndpointByRange(End, range, Start)` → il prefisso copre [inizio paragrafo, punto); `offset = prefix.GetText(-1).Length`.
6. `paraText = para.GetText(max)`; `SentenceSplitter.SentenceAt(paraText, offset)` nel core.
7. Pulizia **dopo** il taglio (per non sfasare gli offset): `\r`, `\a` (U+0007, fine cella/riga tabella di Word), `\v`, U+FFFC, spazi multipli.

```csharp
static string? SentenceAtPoint(UiaContext ctx, UIA.IUIAutomationElement el, int x, int y)
{
    var host = FindTextHost(ctx, el);                        // climb until IsTextPatternAvailable
    if (host is null) return null;
    var tp = (UIA.IUIAutomationTextPattern)host.GetCurrentPattern(UiaIds.TextPattern);
    var pt = new UIA.tagPOINT { x = x, y = y };

    UIA.IUIAutomationTextRange caret;
    try { caret = tp.RangeFromPoint(pt); }                    // degenerate range nearest to pt
    catch (Exception ex) when (UiaErrors.IsExpected(ex)) { return null; }   // E_INVALIDARG if pt outside element
    if (caret is null) return null;

    // validate that the pointer really is on text
    var probe = caret.Clone();
    probe.ExpandToEnclosingUnit(UIA.TextUnit.TextUnit_Word);
    if (!ContainsPoint((double[])probe.GetBoundingRectangles(), x, y, tolerancePx: 12)) return null;

    var para = caret.Clone();
    para.ExpandToEnclosingUnit(UIA.TextUnit.TextUnit_Paragraph);

    var prefix = para.Clone();
    prefix.MoveEndpointByRange(UIA.TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, caret,
                               UIA.TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start);

    const int Max = 8000;
    string before = prefix.GetText(Max) ?? "";
    string all    = para.GetText(Max) ?? "";
    int offset = Math.Min(before.Length, all.Length);
    if (before.Length >= Max) return LocalWindowFallback(caret);  // huge paragraph: see below

    return SentenceSplitter.SentenceAt(all, offset);              // portable core
}

// rects = [x0,y0,w0,h0, x1,y1,w1,h1, ...] one per visible line; empty if degenerate/off-screen
static bool ContainsPoint(double[]? r, int x, int y, int tolerancePx)
{
    if (r is null) return false;
    for (int i = 0; i + 3 < r.Length; i += 4)
        if (x >= r[i] - tolerancePx && x <= r[i] + r[i + 2] + tolerancePx &&
            y >= r[i + 1] - tolerancePx && y <= r[i + 1] + r[i + 3] + tolerancePx) return true;
    return false;
}
```

Paragrafi enormi (`LocalWindowFallback`): invece del paragrafo, allargare il range degenere con `MoveEndpointByUnit(Start, TextUnit_Character, -600)` e `MoveEndpointByUnit(End, TextUnit_Character, +600)`; l'offset diventa il numero di caratteri effettivamente percorsi all'indietro (valore di ritorno, in valore assoluto). La frase puo risultare troncata ai bordi della finestra: accettabile.

`SentenceSplitter` (core, testabile senza Windows): fine frase = `.`, `!`, `?`, `…` seguiti da spazio/fine testo/virgolette chiuse; **non** spezzare su abbreviazioni italiane e inglesi (`sig.`, `dott.`, `prof.`, `es.`, `ecc.`, `art.`, `pag.`, `n.`, `p.es.`, `Mr.`, `Dr.`, `e.g.`, `i.e.`), numeri decimali e ordinali (`3.5`, `1.` a inizio elenco), iniziali (`G. Verdi`), puntini multipli; trattare `\r`, `\n`, `\v`, `\a` come fine frase forte (titoli ed elenchi non hanno il punto). "Fino al punto o fino allo stop" = si pronuncia dall'inizio della frase che contiene l'offset fino al suo terminatore.

Stranezze note del provider UIA di Word (dai commenti nel sorgente NVDA `wordDocument.py`): carattere `\x07` come marca di fine riga/cella di tabella; Word rifiuta di espandere a `Line` sull'ultima riga vuota; alcune proprieta custom mandavano in crash build specifiche (16.0.1493x) — motivo in piu per limitarsi a `RangeFromPoint`/`ExpandToEnclosingUnit`/`GetText`/`GetBoundingRectangles`.

`TextPattern2` (`UIA_TextPattern2Id` 10024) aggiunge `GetCaretRange(out isActive)` e `RangeFromAnnotation`: non servono per "punta e leggi", ma `GetCaretRange` e utile per una futura funzione "leggi la frase dove sto scrivendo" durante la dettatura.

Piano B per Word, se UIA si rivela lento o impreciso: modello a oggetti, `Window.RangeFromPoint(x, y)` → `Range.Expand(wdSentence /*3*/)` → `Range.Text`. E l'unico modo per avere la frase "secondo Word". Si raggiunge con `AccessibleObjectFromWindow(hwnd_WwG, OBJID_NATIVEOM, IID_IDispatch)` (nessuna dipendenza dalle PIA di Office, late binding). Controindicazioni: chiamate rifiutate quando Word ha un dialogo modale (`RPC_E_CALL_REJECTED` 0x80010001, `RPC_E_SERVERCALL_RETRYLATER` 0x8001010A), marshaling STA, Visualizzazione protetta senza OM. Tenerlo come fallback dietro un'interfaccia, non come via principale.

### 10. "Seleziona e leggi" e puntatore dentro la selezione

```csharp
static string? SelectionText(UiaContext ctx, UIA.IUIAutomationElement? hint /* element under pointer or null */)
{
    var start = hint ?? ctx.Automation.GetFocusedElement();
    var host = FindTextHost(ctx, start);
    if (host is null) return null;
    var tp = (UIA.IUIAutomationTextPattern)host.GetCurrentPattern(UiaIds.TextPattern);
    if (tp.SupportedTextSelection == UIA.SupportedTextSelection.SupportedTextSelection_None) return null;

    var ranges = tp.GetSelection();                 // IUIAutomationTextRangeArray; may be null
    if (ranges is null || ranges.Length == 0) return null;
    var sb = new StringBuilder();
    for (int i = 0; i < ranges.Length; i++)
        sb.Append(ranges.GetElement(i).GetText(20000)).Append(' ');
    var text = sb.ToString();
    return string.IsNullOrWhiteSpace(text) ? null : text;   // a caret = one degenerate range = empty text
}

static bool PointerInsideSelection(UIA.IUIAutomationTextPattern tp, int x, int y)
{
    var ranges = tp.GetSelection();
    if (ranges is null) return false;
    for (int i = 0; i < ranges.Length; i++)
        if (ContainsPoint((double[])ranges.GetElement(i).GetBoundingRectangles(), x, y, tolerancePx: 4)) return true;
    return false;
}
```

- `GetSelection` senza selezione torna un solo range degenere (il cursore): testo vuoto → "nessuna selezione".
- `GetBoundingRectangles` torna un rettangolo **per ogni riga visibile** (interamente o in parte) della selezione, in coordinate fisiche, e un array vuoto se il range e degenere o tutto fuori schermo/coperto. Hit-test per riga = esattamente "il puntatore e sopra il testo selezionato".
- Regola d'interazione proposta: al trigger, se l'host di testo sotto il puntatore ha una selezione non vuota **e** il puntatore e dentro i suoi rettangoli → leggere la selezione; altrimenti → frase al puntatore. Un trigger separato (altro pulsante/scorciatoia) legge sempre la selezione dell'elemento con il focus.
- Excel: la "selezione" e la cella/intervallo: `GetFocusedElement()` → `DataItem`, oppure `SelectionPattern.GetCurrentSelection()` sulla griglia (NVDA segnala build in cui riporta 0 elementi: fallback sul focus).
- App senza `TextPattern` (Photoshop, Affinity, PDF in alcuni viewer): UIA non puo dare la selezione. L'unica alternativa generica e la copia negli Appunti simulata (Ctrl+C) con salvataggio/ripristino degli Appunti: e iniezione di input, fuori dall'ambito di questa ricerca e da trattare con cautela.

### 11. Chromium / Edge / Electron nel 2026

- **Chrome 138** (stabile fine giugno 2025) ha abilitato di default il **provider UIA nativo** su Windows (post ufficiale "Native UI Automation for Windows in Chromium", 14 ago 2025). Prima i client UIA passavano dal bridge MSAA→UIA di Windows (lento, impreciso). Il rollout graduale era iniziato con Chrome 126 (post del 15 mag 2024).
- La policy enterprise di ritorno al vecchio comportamento, `UiAutomationProviderEnabled`, era prevista "fino a Chrome 146" ed e stata **rimossa in Chrome 147** (7 aprile 2026, fonte: community Blue Prism). Oggi quindi UIA nativo e l'unica strada su Chrome aggiornato.
- **Edge**: stesso motore; il provider UIA di Chromium e stato scritto in gran parte da Microsoft e Edge lo aveva attivo prima di Chrome. *(Confidenza media sul "quando"; irrilevante in pratica nel 2026.)*
- **Electron**: eredita dalla versione di Chromium. **Electron 37** (giugno 2025) = Chromium 138 → UIA nativo. App Electron vecchie (Chromium < 138) restano sul bridge MSAA: `ElementFromPoint` funziona comunque, con nomi corretti ma pattern piu poveri.
- **Attivazione pigra, ancora vera nel 2026.** Chromium non costruisce l'albero di accessibilita del renderer finche non rileva una tecnologia assistiva. Segnalazione del 20 maggio 2026 (VS Code 1.121, issue trycua/cua #1616): senza attivazione si vedono solo 3 elementi della barra del titolo. L'attivazione avviene quando la finestra `Chrome_RenderWidgetHostHWND` riceve `WM_GETOBJECT` (UIA lo manda quando si chiede un elemento in quella finestra: `ElementFromPoint`/`ElementFromHandle`), oppure avviando l'app con `--force-renderer-accessibility`. L'albero arriva in modo **asincrono**: la *prima* interrogazione puo tornare solo `Document`/`Pane` generico.
  - Strategia: se la finestra sotto il puntatore ha classe `Chrome_WidgetWin_*` / `Chrome_RenderWidgetHostHWND` e il risultato e un contenitore senza nome → attendere 250-400 ms e **ritentare una volta**; nel frattempo preparare l'OCR. Dopo la prima attivazione l'app resta accessibile per tutta la sessione.
- Contenuto web: i nodi di testo arrivano come `Text` con `Name` = testo del nodo (puo essere un paragrafo intero) e Chromium documenta il supporto di `TextPattern`. Provare `RangeFromPoint` sul `Document` per la frase; se non affidabile, applicare `SentenceSplitter` al `Name` del nodo `Text` (senza offset → leggere dal principio del nodo, con un tetto di lunghezza).

### 12. Timeout, eccezioni, app bloccate

**Timeout UIA** (`IUIAutomation2`, serve `CUIAutomation8`, Windows 8+): `ConnectionTimeout` = attesa per ottenere un elemento dal provider (default **2 s**); `TransactionTimeout` = attesa per informazioni su un elemento (default **20 s**, "perche alcune operazioni elaborano centinaia di elementi"). Valori in millisecondi. Noi non facciamo mai ricerche massive: 1000 / 2500 ms sono ragionevoli (alzare il secondo a 4000 ms solo se i paragrafi lunghi di Word lo richiedono).

**Difese a strati contro i blocchi** (i timeout UIA non coprono tutti i casi: proxy MSAA, provider in-proc lenti, COM che attende):

1. *Prima di UIA*: `WindowFromPoint(pt)` → `GetAncestor(hwnd, GA_ROOT)` → `IsHungAppWindow(root)`; se bloccata → niente UIA, direttamente OCR (la cattura dello schermo non dipende dall'app).
2. Timeout UIA abbassati (sopra).
3. Watchdog sul job (§3): scadenza complessiva 1200-1500 ms per etichette, 3000 ms per Word; alla scadenza si parte con l'OCR e si sostituisce il worker.
4. Mai enumerazioni illimitate: `FindAll` solo `TreeScope_Children`, con tetto sul numero di figli e budget di tempo.
5. Secondo clic = stop: la richiesta in corso va marcata come annullata (il risultato tardivo si butta).

**Codici di errore UIA** (pagina ufficiale, 14 lug 2025) e come arrivano in .NET con il COM interop integrato:

| HRESULT | Costante | In .NET |
|---|---|---|
| 0x80040201 | `UIA_E_ELEMENTNOTAVAILABLE` (elemento distrutto/virtualizzato) | `COMException` (HResult) — il piu frequente: menu che si chiude, tooltip che sparisce |
| 0x80040200 | `UIA_E_ELEMENTNOTENABLED` | `COMException` |
| 0x80040202 | `UIA_E_NOCLICKABLEPOINT` | `COMException` |
| 0x80040203 | `UIA_E_PROXYASSEMBLYNOTLOADED` | `COMException` |
| 0x80040204 | `UIA_E_NOTSUPPORTED` | `COMException` |
| 0x80131505 | `UIA_E_TIMEOUT` (= `COR_E_TIMEOUT`) | **`TimeoutException`** |
| 0x80131509 | `UIA_E_INVALIDOPERATION` (= `COR_E_INVALIDOPERATION`) | **`InvalidOperationException`** |
| 0x80070005 | `E_ACCESSDENIED` (UIPI, processo protetto) | `UnauthorizedAccessException` |
| 0x80070057 / 0x80004001 | `E_INVALIDARG` / `E_NOTIMPL` | `ArgumentException` / `NotImplementedException` |
| 0x800706BA, 0x80010108, 0x800706BE | RPC server non disponibile / disconnesso / chiamata fallita (app chiusa a meta) | `COMException` |

(La mappatura HRESULT→eccezione e quella standard del runtime; da confermare al primo test. Con l'API gestita si avrebbero invece `ElementNotAvailableException` ecc.)

```csharp
internal static class UiaErrors
{
    public static bool IsExpected(Exception ex) => ex is COMException or TimeoutException
        or InvalidOperationException or UnauthorizedAccessException or ArgumentException
        or NotImplementedException or InvalidCastException or NullReferenceException;
}
```

Regola: ogni singolo accesso a proprieta/pattern puo fallire in qualunque momento (l'UI cambia sotto i piedi). Un fallimento **non e un errore da mostrare**: e il segnale per passare al fallback successivo. Log su file per la diagnosi dell'assistente, mai dialoghi.

Rilascio RCW: gli elementi UIA sono oggetti COM; con poche decine di oggetti per richiesta il GC basta. Evitare `Marshal.FinalReleaseComObject` sparsi (rischio "COM object separated from its RCW"); eventualmente `GC.Collect` leggero dopo richieste pesanti, solo se si osserva crescita di memoria in `UIAutomationCore`.

### 13. UIPI e finestre elevate

Dalla pagina "Security Considerations for Assistive Technologies" (aggiornata 18 feb 2026):

- un'app **senza** `uiAccess` parte a integrita media e **non puo accedere** alla UI di processi elevati;
- con `uiAccess=true` e utente non amministratore → "medium+", ancora niente UI "high"; con `uiAccess` + utente amministratore → accesso alla UI elevata;
- requisiti di `uiAccess`: firma **Authenticode**, installazione in **percorso sicuro** (es. `Program Files`, che richiede UAC per scriverci), manifest con `uiAccess="true"`. Nessuno scenario da accesso al desktop sicuro (prompt UAC, schermata di accesso).

La nostra distribuzione e una **cartella portabile**: `uiAccess` e di fatto escluso (si puo rivalutare se in futuro ci sara un installer firmato). Effetti pratici, senza `uiAccess`:

- sopra una finestra elevata (Gestione attivita, installer, app "Esegui come amministratore") `ElementFromPoint` fallisce con accesso negato o torna solo un elemento generico → fallback;
- anche l'hook `WH_MOUSE_LL` di un processo a integrita media, per quanto noto, non riceve gli eventi quando l'input va a una finestra a integrita piu alta → in quelle finestre il trigger potrebbe semplicemente non scattare *(da verificare; non documentato in modo esplicito)*;
- l'OCR da cattura schermo continua a funzionare sulle finestre elevate (non sul desktop sicuro).
- **Non** eseguire Punta e Ascolta come amministratore per aggirare il problema: tutto cio che l'app avvia erediterebbe l'elevazione, e il drag&drop/gli Appunti con le app normali si complicano.

Per l'utente finale (Word, Excel, Photoshop, Affinity, browser: tutti a integrita media) il limite e irrilevante.

### 14. Separazione core / piattaforma (in vista di macOS)

Nel core portabile (`netstandard`/`net10.0`, nessun riferimento Windows):

```csharp
public enum PointedKind { Label, Value, Sentence, Cell, Tooltip, Selection, OcrText, Nothing }
public sealed record PointedItem(string Text, PointedKind Kind, string Source /* "uia","tooltip","ocr" */,
                                 string? AppId, RectI? Bounds);

public interface IPointerInspector   // Windows: UIA; macOS (future): AXUIElementCopyElementAtPosition
{
    Task<PointedItem?> InspectAsync(int x, int y, CancellationToken ct);
    Task<PointedItem?> ReadSelectionAsync(CancellationToken ct);
}
```

Nel core: `SpeakableText`, `EmojiFilter`, `SentenceSplitter`, la politica di priorita (tabella §5 espressa su un DTO neutro `ElementSnapshot { Role, Name, Value, Help, Description, Status, ParentName, ChildTexts, FrameworkHint }`), la cache audio, l'orchestrazione dei fallback. Nel layer Windows: solo la produzione degli `ElementSnapshot` (UIA), tooltip Win32, hook, OCR, audio. Su macOS l'equivalente di `ElementFromPoint` e `AXUIElementCopyElementAtPosition`, con attributi `AXTitle`/`AXDescription`/`AXValue`/`AXHelp`: la stessa politica di priorita si riusa cosi com'e.

---

## Insidie note

1. **Default di 20 s** per `TransactionTimeout`: senza `CUIAutomation8` + `IUIAutomation2` un'app bloccata congela la lettura per 20 secondi. Con `new CUIAutomation()` (CLSID vecchio) il cast a `IUIAutomation2` non e garantito: usare sempre `CUIAutomation8`.
2. **UIA sul thread sbagliato**: nell'hook → Windows rimuove l'hook per timeout; sul thread UI WPF → deadlock/lentezza quando il puntatore e sopra la nostra stessa finestra impostazioni. Inoltre oggetti creati in STA e usati altrove vengono marshalati.
3. **DPI**: processo non DPI-aware o `GetCursorPos` "logico" → elemento sbagliato su schermi al 125-200% (lo Zenbook e certamente scalato). Manifest PerMonitorV2 obbligatorio; attenzione ai multi-monitor con scale diverse.
4. **`RangeFromPoint` torna il range "piu vicino"**, non "quello sotto": senza validazione con `GetBoundingRectangles` si leggerebbe una frase anche puntando nel margine o su un'immagine.
5. **Nessuna unita "frase"** in UIA; `ExpandToEnclosingUnit` con unita non supportata ripiega silenziosamente su una piu grande (si puo ricevere l'intero documento: mettere sempre un tetto a `GetText(max)`).
6. **Offset e pulizia**: calcolare l'offset sul testo grezzo e pulire *dopo*; altrimenti `\r`, U+FFFC, `\a` sfasano la frase.
7. **Foglia troppo profonda in WPF**: `Text '_File'` sotto `MenuItem 'File'`; underscore dei tasti di accesso; `Name` = nome di tipo .NET (`Serif.Affinity.Workspaces.Workspace`) quando lo sviluppatore non ha impostato `AutomationProperties.Name`.
8. **Nome vuoto sui pulsanti-icona** (Affinity: 40 su 40): `HelpText` ne salva una parte, il resto richiede tooltip/OCR. Non pronunciare `AutomationId` o `ClassName`.
9. **Excel: `Name` e la coordinata** (`"A" 1`), il contenuto e in `Value`. Celle unite = un solo elemento. `SelectionPattern` a volte riporta 0 selezionati.
10. **Chromium pigro**: prima interrogazione = contenitore vuoto; serve un ritentativo ritardato. App Electron vecchie = bridge MSAA.
11. **Menu owner-draw e pannelli custom** (Photoshop, parti di Affinity): nessun nome → OCR. Il menu popup `#32768` e una finestra a se: sparisce in un attimo → `UIA_E_ELEMENTNOTAVAILABLE` frequente.
12. **`Cached*` lancia** se la proprieta non e nella cache request; `GetCurrentPattern` puo tornare `null`; i `VARIANT` tornano come `object` (bool per i flag, `int` per ControlType, `double[]` per i rettangoli, a volte `DBNull`/"NotSupported" sentinel → controllare il tipo prima del cast).
13. **`LegacyIAccessible` su provider nativi** e costoso e puo fallire: solo come fallback, mai nella cache request di base.
14. **`FindAll` su alberi grandi** (griglia Excel, liste virtualizzate, DOM web): mai `TreeScope_Descendants`/`Subtree`.
15. **`<COMReference>` + `dotnet build`** → MSB4803. Usare il pacchetto NuGet di interop.
16. **Trimming/AOT** disattivano il COM interop integrato → eccezioni a runtime alla prima `new CUIAutomation8()`.
17. **Eventi UIA** (se si useranno per i tooltip): aggiungere e rimuovere gli handler dallo stesso thread MTA, mai da piu thread; un handler globale bloccato rallenta *le altre app*.
18. **Tooltip e clic**: se il clic centrale viene inoltrato all'app, il tooltip sparisce prima di poterlo leggere; nei browser il clic centrale apre link/avvia lo scorrimento automatico → il trigger va consumato dall'hook.
19. **Password**: `IsPassword=true` → mai leggere `Value` (UIA di solito lo nega, ma non sempre i proxy MSAA).
20. **Finestre elevate**: niente UIA, forse niente trigger. Documentarlo per l'assistente invece di combatterlo.

---

## Domande aperte da verificare sulla macchina

(Richiedono il .NET 10 SDK e una piccola console "sonda" che stampa, per il punto sotto il mouse dopo 3 s: catena degli antenati, proprieta in cache, pattern disponibili, tempi in ms. Nessuna di queste verifiche e stata fatta in questa ricerca.)

1. **Build ARM64**: `Interop.UIAutomationClient` 10.19041.0 in un processo `win-arm64` .NET 10: `new CUIAutomation8()`, cast a `IUIAutomation2`, set dei timeout. Verificare anche verso app x64 emulate (Affinity e x64 o ARM64? Photoshop e ARM64 nativo) e verso Office (Arm64X).
2. **Firme esatte dell'interop**: `AutoSetFocus` (int o bool?), tipo di ritorno di `GetBoundingRectangles` (`double[]` o `Array`), `CachedBoundingRectangle` (`tagRECT`), nomi degli enum (`TextUnit.TextUnit_Paragraph`, ...).
3. **Word (Microsoft 365, build installata)**: quale elemento torna `ElementFromPoint` sul testo (Document `_WwG` o un figlio)? Quanti livelli per trovare `TextPattern`? `RangeFromPoint` e preciso al carattere? Tempi di `ExpandToEnclosingUnit(Paragraph)` + `GetText` su documenti lunghi, in tabelle, intestazioni, caselle di testo, commenti, Visualizzazione protetta, layout Web/Lettura.
4. **Excel**: `ElementFromPoint` sulla griglia torna il `DataItem` della cella *sotto il mouse* (non quella attiva)? Formato reale di `Name` nella build italiana; `Value` per date/valute/formule/errori; cella in modifica; celle unite; intestazioni di riga/colonna; barra della formula.
5. **Ribbon Office**: conferma di `Name`/`HelpText` su pulsanti-icona, `SplitButton`, gallerie, menu contestuali, mini-toolbar, backstage; `ClassName`/`FrameworkId` effettivi di NetUI.
6. **Affinity v3**: i **menu aperti** (popup WPF) espongono i `MenuItem` con nome? (La sonda esistente copre solo la barra.) `ElementFromPoint` torna `MenuItem` o il `TextBlock '_File'`? I pannelli (Livelli, Colore, Strumenti) cosa espongono? Tooltip WPF: classe finestra e `Name`? Perche NVDA non leggeva i menu (focus/eventi, non albero)? Percentuale di pulsanti coperta da `HelpText`.
7. **Photoshop**: menu = `#32768` standard con nomi? Pannelli e barra opzioni: qualcosa via UIA/MSAA o solo OCR? Classe dei tooltip.
8. **Tooltip**: `GetWindowText` su `tooltips_class32` torna il testo? Elenco reale delle classi tooltip per Office, Affinity, Photoshop, Edge/Chrome, Esplora file. Il tooltip sopravvive al clic centrale consumato dall'hook?
9. **Chromium/Edge/Electron**: alla prima interrogazione su una finestra mai toccata, cosa torna? Dopo quanto l'albero e pronto? `RangeFromPoint` sul `Document` web funziona? Comportamento in VS Code, Teams, WhatsApp Desktop (WebView2).
10. **Tempi**: latenza tipica di `ElementFromPointBuildCache` (obiettivo < 50 ms) per app; costo del fallback Legacy; tempo totale trigger→testo.
11. **App bloccata**: simulare un'app che non risponde (processo sospeso) e verificare che (a) `IsHungAppWindow` la intercetti, (b) i timeout UIA scattino come `TimeoutException`, (c) il watchdog sostituisca il worker senza perdite.
12. **Finestre elevate**: cosa torna esattamente `ElementFromPoint` (eccezione o elemento generico)? L'hook `WH_MOUSE_LL` riceve il clic centrale sopra una finestra elevata?
13. **Multi-monitor con scale diverse** (portatile + monitor esterno): coordinate dell'hook = coordinate UIA?
14. **Menu Start, barra delle applicazioni, Esplora file, Impostazioni (WinUI/XAML)**: qualita dei nomi, elementi `ListItem` con nomi composti ("Documenti, cartella, ...") da accorciare?
15. **`EmbedInteropTypes=true`** funziona con i cast tra `IUIAutomationElement` e le interfacce derivate/pattern? Se da problemi, lasciare la DLL di interop.

---

## Fonti

Documentazione Microsoft (Win32 / UI Automation):
- IUIAutomation::ElementFromPoint — https://learn.microsoft.com/en-us/windows/win32/api/uiautomationclient/nf-uiautomationclient-iuiautomation-elementfrompoint
- IUIAutomation2 (interfaccia) — https://learn.microsoft.com/en-us/windows/win32/api/uiautomationclient/nn-uiautomationclient-iuiautomation2
- IUIAutomation2::ConnectionTimeout (default 2 s) — https://learn.microsoft.com/en-us/windows/win32/api/uiautomationclient/nf-uiautomationclient-iuiautomation2-put_connectiontimeout
- IUIAutomation2::TransactionTimeout (default 20 s) — https://learn.microsoft.com/en-us/windows/win32/api/uiautomationclient/nf-uiautomationclient-iuiautomation2-put_transactiontimeout
- CUIAutomation8 — https://learn.microsoft.com/en-us/previous-versions/windows/desktop/legacy/hh448746(v=vs.85)
- Understanding Threading Issues (14 lug 2025) — https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-threading
- Understanding Screen Scaling Issues (14 lug 2025) — https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-screenscaling
- Security Considerations for Assistive Technologies / uiAccess (18 feb 2026) — https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-securityoverview
- UIA Error Codes (14 lug 2025) — https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-error-codes
- Automation Element Property Identifiers (Name, HelpText, ItemStatus, FullDescription, ...) — https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-automation-element-propids
- ToolTip Control Type — https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-supporttooltipcontroltype
- MenuItem Control Type (incl. "Legacy Issues" per i menu Win32) — https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-supportmenuitemcontroltype
- IUIAutomationTextPattern::RangeFromPoint — https://learn.microsoft.com/en-us/windows/win32/api/uiautomationclient/nf-uiautomationclient-iuiautomationtextpattern-rangefrompoint
- IUIAutomationTextRange::MoveEndpointByRange — https://learn.microsoft.com/en-us/windows/win32/api/uiautomationclient/nf-uiautomationclient-iuiautomationtextrange-moveendpointbyrange
- IUIAutomationTextRange::GetBoundingRectangles — https://learn.microsoft.com/en-us/windows/win32/api/uiautomationclient/nf-uiautomationclient-iuiautomationtextrange-getboundingrectangles
- TextUnit enumeration — https://learn.microsoft.com/en-us/windows/win32/api/uiautomationcore/ne-uiautomationcore-textunit
- TextPatternRange.ExpandToEnclosingUnit (ripiego sull'unita piu grande) — https://learn.microsoft.com/en-us/dotnet/api/system.windows.automation.text.textpatternrange.expandtoenclosingunit
- MSLLHOOKSTRUCT (pt in coordinate per-monitor-aware) — https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-msllhookstruct
- WM_GETOBJECT — https://learn.microsoft.com/en-us/windows/win32/winauto/wm-getobject
- Raymond Chen, "Reading the word under the cursor via UI Automation" (16 feb 2015) — https://devblogs.microsoft.com/oldnewthing/20150216-00/?p=44673

.NET:
- UI Automation Overview (.NET, con nota che rimanda all'API Windows per le novita) — https://learn.microsoft.com/en-us/dotnet/framework/ui-automation/ui-automation-overview
- Sorgente del client UIA gestito in WPF — https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/UIAutomation/UIAutomationClient/System/Windows/Automation/AutomationElement.cs
- Announcing .NET 10 (11 nov 2025, LTS fino a nov 2028) — https://devblogs.microsoft.com/dotnet/announcing-dotnet-10/
- .NET 10 Supported OS (Windows 11 Arm64) — https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md
- MSB4803 / ResolveComReference non supportato da `dotnet build` — https://learn.microsoft.com/en-us/answers/questions/895702/error-msb4803-the-task-resolvecomreference-is-not e https://github.com/dotnet/runtime/issues/97125
- CsWin32, supporto AOT/ComWrappers (`allowMarshaling:false`) — https://github.com/microsoft/CsWin32/discussions/1169 ; https://microsoft.github.io/CsWin32/docs/getting-started.html

Pacchetti e sorgenti di terzi:
- NuGet FlaUI.UIA3 5.0.0 (25 feb 2025) — https://www.nuget.org/packages/FlaUI.UIA3/
- NuGet Interop.UIAutomationClient 10.19041.0 (17 lug 2020) — https://www.nuget.org/packages/Interop.UIAutomationClient
- FlaUI — https://github.com/FlaUI/FlaUI ; `UIA3Automation.cs` (CUIAutomation8, timeouts, FromPoint) — https://github.com/FlaUI/FlaUI/blob/master/src/FlaUI.UIA3/UIA3Automation.cs ; `UIA3TextRange.cs` — https://github.com/FlaUI/FlaUI/blob/master/src/FlaUI.UIA3/UIA3TextRange.cs
- FlaUI/UIAutomation-Interop (IDL SDK 10.0.16299 → 10.0.22621) — https://github.com/FlaUI/UIAutomation-Interop
- FlaUI-MCP (fork con build win-arm64, .NET 8: prova pratica che FlaUI/UIA3 gira su ARM64) — https://github.com/starpia-forge/FlaUI-MCP
- NVDA, Excel via UIA — https://github.com/nvaccess/nvda/blob/master/source/NVDAObjects/UIA/excel.py
- NVDA, Word via UIA — https://github.com/nvaccess/nvda/blob/master/source/NVDAObjects/UIA/wordDocument.py
- NVDA PR #13437 (UIA di default in Word >= 16.0.15000 su Windows 11) — https://github.com/nvaccess/nvda/pull/13437 ; PR #12861 (Excel UIA) — https://github.com/nvaccess/nvda/pull/12861

Chromium / Electron:
- "Native UI Automation for Windows in Chromium" (14 ago 2025; default da Chrome 138) — https://developer.chrome.com/blog/windows-uia-support-update
- "Introducing UIA support on Windows" (15 mag 2024; rollout da Chrome 126) — https://developer.chrome.com/blog/windows-uia-support
- Chromium docs, UI Automation — https://chromium.googlesource.com/chromium/src.git/+/refs/heads/main/docs/accessibility/browser/uiautomation.md
- Rimozione della policy `UiAutomationProviderEnabled` in Chrome 147 (7 apr 2026) — https://community.blueprism.com/t5/Blogs/Chrome-147-Removes-UIA-Workaround-Update-Your-Blu/ba-p/125213
- Electron 37.0.0 (Chromium 138) — https://www.electronjs.org/blog/electron-37-0
- Albero Chromium/Electron invisibile senza attivazione (VS Code 1.121, 20 mag 2026) — https://github.com/trycua/cua/issues/1616
- Attivazione dell'accessibilita Chromium via WM_GETOBJECT a `Chrome_RenderWidgetHostHWND` (wiki UIA-v2 di Descolada) — https://github.com/Descolada/UIA-v2/wiki/08.-Common-pitfalls;-tips-and-tricks

Osservazioni locali (non web):
- Sonda UIA su Affinity v3 eseguita su questa macchina da un'altra attivita di ricerca: `C:\Users\Angelo\Desktop\Matteo\docs\research\probe\affinity-uia-tree-main.txt` (FrameworkId WPF; 40 Button senza Name, 7 con HelpText; `MenuItem 'File'` con figlio `Text '_File'`; `ListItem` con Name = nome di tipo .NET).
