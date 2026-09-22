# Hook mouse e tastiera, DPI, cattura schermo

Ricerca per "Punta e Ascolta" — stato: settembre 2026. Stack di riferimento: C# / .NET 10 (LTS, GA 11 novembre 2025, supporto fino a novembre 2028), Windows 11 x64 e ARM64, WPF + tray, libreria core portabile + layer Windows.

Nota di metodo: sulla macchina di sviluppo non c'e' ancora il .NET SDK, quindi gli sketch C# di questo documento NON sono stati compilati. Le firme P/Invoke sono state ricontrollate sulla documentazione Win32 ufficiale; i punti incerti sono marcati esplicitamente e riportati in "Domande aperte".

---

## Raccomandazione

1. **Trigger mouse: `WH_MOUSE_LL` su un thread dedicato ("InputThread")** con il proprio message loop (`GetMessageW`), priorita' `Highest`, callback `static` marcata `[UnmanagedCallersOnly]` passata come function pointer (niente delegate, quindi niente problema di GC). La callback fa solo: scarta `WM_MOUSEMOVE`, confronta il pulsante con la configurazione, aggiorna una maschera di bit, scrive un evento in un `Channel<T>` e ritorna `1` (inghiotte) oppure `CallNextHookEx`. Tutto il resto (riconoscimento click corto/lungo, UIA, cattura, OCR, TTS) gira su altri thread.
2. **Inghiottire sempre la coppia DOWN+UP** del pulsante configurato (middle, XBUTTON1, XBUTTON2). Mai inghiottire un UP di cui non si e' inghiottito il DOWN, e viceversa. Cosi' l'app bersaglio non vede nulla: niente autoscroll in Word/browser, niente pan in Affinity/Photoshop, e i menu aperti restano aperti (purche' la nostra app non attivi mai una finestra).
3. **Click corto vs pressione lunga: logica nel core portabile**, non nell'hook. Default consigliato per questo utente: **pressione lunga disattivata e azione sul DOWN** (latenza minima, nessuna dipendenza dalla durata della pressione, che con la paralisi cerebrale non e' controllabile). Se l'assistente abilita la pressione lunga: click corto deciso sull'UP, pressione lunga scattata da un timer allo scadere della soglia (senza aspettare l'UP). Aggiungere un anti-rimbalzo configurabile (es. 300-400 ms) contro i doppi click involontari.
4. **Eventi iniettati**: ignorare sempre i propri (firma in `dwExtraInfo`); ignorare gli altri (`LLMHF_INJECTED`) per default, ma con un'opzione "accetta eventi iniettati", perche' ausili di puntamento (dwell-click, head/eye tracker, software del mouse) generano click tramite `SendInput` e risultano "injected".
5. **Tastiera: `RegisterHotKey` (non `WH_KEYBOARD_LL`) per la v1**, registrati sullo stesso InputThread: un hotkey "leggi sotto il puntatore", uno separato "leggi selezione", uno "stop". `Esc` come stop va registrato **dinamicamente solo mentre la voce parla** (altrimenti ruberebbe Esc a tutto il sistema). `RegisterHotKey` non ha timeout, non puo' essere "rimosso in silenzio" e funziona anche quando in primo piano c'e' una finestra elevata. `WH_KEYBOARD_LL` solo in una fase successiva e solo se servono cose che `RegisterHotKey` non sa fare (tasto singolo modificatore, tenere premuto un tasto, distinguere Ctrl sinistro/destro). Per "qualsiasi tasto ferma la voce" usare Raw Input in sola osservazione, non un hook.
6. **Robustezza dell'hook**: non toccare `LowLevelHooksTimeout`; reinstallare l'hook (installa il nuovo, poi rimuovi il vecchio) a: sblocco sessione, resume, cambio display, e periodicamente quando il sistema e' inattivo e nessun pulsante trigger e' premuto. Misurare la durata della callback e loggare se supera 20-50 ms. Thread OCR/ONNX a priorita' `BelowNormal` e con numero di thread limitato, per non affamare l'InputThread (se l'InputThread non gira, il mouse di tutto il sistema scatta).
7. **UIPI**: v1 con manifest `asInvoker`, `uiAccess="false"`, non elevata. Word, Excel, Photoshop e Affinity girano non elevati, quindi l'hook li copre. Limite documentato: su finestre elevate (es. Gestione attivita', installer, regedit) il trigger mouse non arriva. NVDA in copia portabile ha lo stesso identico limite. `uiAccess="true"` richiede firma Authenticode attendibile + installazione in `%ProgramFiles%`: incompatibile con la distribuzione "cartella portabile", da valutare solo se in futuro si fara' un installer. Sconsigliato far girare l'app elevata (vedi "Insidie": Administrator Protection di Windows 11 cambia profilo utente e rompe DPAPI).
8. **DPI: Per-Monitor V2 dichiarato nel manifest** (non via API). Con PMv2 le coordinate di `MSLLHOOKSTRUCT.pt`, `GetCursorPos`/`GetPhysicalCursorPos`, i rettangoli UIA e i pixel di `BitBlt` sono tutti nello stesso spazio: pixel fisici del desktop virtuale, con origine sul monitor primario e **valori negativi** per i monitor a sinistra/sopra. Usare il punto del DOWN fornito dall'hook; per i trigger da tastiera `GetPhysicalCursorPos`.
9. **Cattura schermo: GDI `BitBlt` dal DC dello schermo (`GetDC(NULL)`) con `SRCCOPY | CAPTUREBLT`** in una DIB section 32 bpp top-down, via P/Invoke diretto (nessuna dipendenza da `System.Drawing`). E' la via piu' semplice e affidabile per una cattura singola, su richiesta, di un piccolo rettangolo: con DWM sempre attivo (Windows 8+) il DC dello schermo restituisce il desktop composto, quindi include menu popup, tooltip, finestre layered e contenuto Direct3D delle app accelerate; il cursore hardware **non** compare nell'immagine (niente da escludere). `Windows.Graphics.Capture` e DXGI Desktop Duplication sono piu' complessi, richiedono un device D3D11 e non danno vantaggi per questo caso d'uso; tenerli come piano B solo se i test su Affinity/Photoshop mostrassero aree nere.
10. **Mai rubare il focus**: nessuna finestra visibile durante l'uso normale; InputThread con finestra message-only (`HWND_MESSAGE`); nessun `MessageBox`/toast/`SetForegroundWindow` nel percorso di lettura; errori comunicati a voce o nel log. La finestra impostazioni WPF si apre solo su azione esplicita dell'assistente dal tray. Se mai servisse un overlay: `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_TOPMOST` + `SW_SHOWNOACTIVATE`.
11. **Istanza singola**: `Mutex` con nome `Local\...` tenuto in un campo statico + `EventWaitHandle` con nome per dire alla prima istanza "apri le impostazioni".
12. **P/Invoke**: `[LibraryImport]` (source generator, .NET 7+) con `nint`/`nuint`; x64 e ARM64 sono entrambi LLP64 con gli stessi layout, quindi **le stesse firme valgono per entrambe le architetture**. In alternativa `Microsoft.Windows.CsWin32` 0.3.x (generatore ufficiale). Pubblicare due build native (`win-x64`, `win-arm64`): un processo x64 emulato su Snapdragon che ospita un hook globale aggiunge latenza a ogni evento mouse del sistema.

---

## Dettagli tecnici

### 1. Architettura dei thread

```
InputThread (nativo, Highest, background)        GestureWorker (Task)            Pipeline lettura (core)
  finestra message-only                            legge Channel<InputEvent>       UIA (thread MTA dedicato)
  WH_MOUSE_LL  --TryWrite-->  Channel  ----------> click corto / lungo   ------->  cattura BitBlt -> OCR
  RegisterHotKey (WM_HOTKEY) --TryWrite-->         anti-rimbalzo                   TTS / cache / playback
  timer di manutenzione hook                       "secondo click = stop"
UI thread WPF (STA): tray + finestra impostazioni (solo su richiesta dell'assistente)
```

Separazione core/piattaforma (per il futuro macOS): nel core stanno `InputEvent`, `TriggerRole`, `PressGestureRecognizer`, la macchina a stati "parla / stop"; nel layer Windows stanno `InputThread`, le P/Invoke, `ScreenCapture`. Su macOS l'equivalente dell'hook che inghiotte e' un `CGEventTap` attivo (richiede il permesso Accessibilita'): l'interfaccia `ITriggerSource` del core non deve quindi esporre nulla di Win32.

```csharp
// Core (portabile)
public enum TriggerRole : byte { None, ReadUnderPointer, ReadSelection, Stop }
public enum Phase : byte { Down, Up, Hotkey }
public readonly record struct InputEvent(TriggerRole Role, Phase Phase, int X, int Y, uint TimeMs);

public interface ITriggerSource : IDisposable
{
    ChannelReader<InputEvent> Events { get; }
    void ApplyConfig(TriggerConfig config);   // snapshot immutabile
    void SetSpeaking(bool speaking);          // per registrare/deregistrare Esc
}
```

### 2. Firme P/Invoke (valide per x64 e ARM64)

Requisiti csproj: `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`, TFM `net10.0-windows10.0.xxxxx.0`.

```csharp
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential)]
internal struct POINT { public int X; public int Y; }          // int con segno: coordinate negative!

[StructLayout(LayoutKind.Sequential)]
internal struct MSLLHOOKSTRUCT                                  // 32 byte su 64 bit
{
    public POINT pt;            // coordinate schermo per-monitor-aware
    public uint mouseData;      // HIWORD = XBUTTON1 (1) / XBUTTON2 (2) oppure delta rotella
    public uint flags;          // LLMHF_INJECTED = 0x1, LLMHF_LOWER_IL_INJECTED = 0x2
    public uint time;
    public nuint dwExtraInfo;   // ULONG_PTR
}

[StructLayout(LayoutKind.Sequential)]
internal struct KBDLLHOOKSTRUCT
{
    public uint vkCode, scanCode, flags, time;   // LLKHF_EXTENDED 0x01, LLKHF_INJECTED 0x10,
    public nuint dwExtraInfo;                    // LLKHF_ALTDOWN 0x20, LLKHF_UP 0x80
}

[StructLayout(LayoutKind.Sequential)]
internal struct MSG                                             // 48 byte su 64 bit
{
    public nint hwnd; public uint message; public nuint wParam; public nint lParam;
    public uint time; public POINT pt;
}

internal static unsafe partial class Native
{
    internal const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14, HC_ACTION = 0;
    internal const uint WM_MOUSEMOVE = 0x0200, WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208,
                        WM_XBUTTONDOWN = 0x020B, WM_XBUTTONUP = 0x020C,
                        WM_HOTKEY = 0x0312, WM_TIMER = 0x0113, WM_APP = 0x8000, WM_QUIT = 0x0012;
    internal const uint LLMHF_INJECTED = 0x1;
    internal const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;
    internal static readonly nint HWND_MESSAGE = -3;

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint SetWindowsHookExW(int idHook,
        delegate* unmanaged<int, nuint, nint, nint> lpfn, nint hmod, uint dwThreadId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnhookWindowsHookEx(nint hhk);

    [LibraryImport("user32.dll")]
    internal static partial nint CallNextHookEx(nint hhk, int nCode, nuint wParam, nint lParam);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint GetModuleHandleW(string? lpModuleName);

    [LibraryImport("user32.dll")]                       // ritorna -1 in caso di errore: int, non bool
    internal static partial int GetMessageW(out MSG lpMsg, nint hWnd, uint min, uint max);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TranslateMessage(in MSG lpMsg);
    [LibraryImport("user32.dll")]
    internal static partial nint DispatchMessageW(in MSG lpMsg);
    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostMessageW(nint hWnd, uint msg, nuint wParam, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateWindowExW(uint exStyle, string className, string? windowName,
        uint style, int x, int y, int w, int h, nint parent, nint menu, nint hInstance, nint param);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyWindow(nint hWnd);

    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnregisterHotKey(nint hWnd, int id);

    [LibraryImport("user32.dll")]
    internal static partial nuint SetTimer(nint hWnd, nuint id, uint elapseMs, nint timerFunc);
    [LibraryImport("user32.dll")]
    internal static partial short GetAsyncKeyState(int vKey);

    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetPhysicalCursorPos(out POINT pt);
}
```

Note:
- `hmod`: per gli hook low-level la callback vive nel nostro processo e non viene iniettata; passare `GetModuleHandleW(null)` (handle dell'apphost .exe, sempre valido) e `dwThreadId = 0`.
- x64 e ARM64 hanno una sola calling convention: non serve specificare `CallConvStdcall`.
- Alternativa senza scrivere firme a mano: `Microsoft.Windows.CsWin32` (0.3.333 su NuGet al 3 settembre 2026) con `NativeMethods.txt` contenente i nomi delle API; genera `PInvoke.SetWindowsHookEx`, `MSLLHOOKSTRUCT`, ecc.

### 3. InputThread: hook che inghiotte il trigger

```csharp
internal sealed unsafe class InputThread : ITriggerSource
{
    private static InputThread? s_self;                       // la callback e' static
    private const nuint OwnInjectionTag = 0x50_45_41_31;      // "PEA1": firma dei nostri SendInput
    private const uint WM_APP_APPLYCONFIG = Native.WM_APP + 1, WM_APP_SPEAKING = Native.WM_APP + 2,
                       WM_APP_REHOOK = Native.WM_APP + 3;

    private readonly Channel<InputEvent> _events = Channel.CreateUnbounded<InputEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private TriggerConfig _config = TriggerConfig.Default;    // oggetto immutabile, sostituito atomicamente
    private nint _hwnd, _mouseHook;
    private int _swallowedMask;                               // bit per pulsante il cui DOWN e' stato inghiottito

    public ChannelReader<InputEvent> Events => _events.Reader;

    public InputThread()
    {
        s_self = this;
        _thread = new Thread(Run) { IsBackground = true, Name = "InputThread", Priority = ThreadPriority.Highest };
        _thread.Start();
        _ready.Wait();
    }

    private void Run()
    {
        // Finestra message-only con classe di sistema "STATIC": niente RegisterClass, niente WndProc gestita.
        _hwnd = Native.CreateWindowExW(0, "STATIC", null, 0, 0, 0, 0, 0, Native.HWND_MESSAGE, 0, 0, 0);
        InstallMouseHook();
        RegisterConfiguredHotkeys();
        Native.SetTimer(_hwnd, 1, 60_000, 0);                 // manutenzione periodica
        _ready.Set();

        while (Native.GetMessageW(out MSG m, 0, 0, 0) > 0)    // il loop serve anche a far arrivare le callback dell'hook
        {
            switch (m.message)
            {
                case Native.WM_HOTKEY:      OnHotkey((int)m.wParam); break;
                case Native.WM_TIMER:       MaybeReinstallHook(); break;
                case WM_APP_REHOOK:         ReinstallHook(); break;
                case WM_APP_APPLYCONFIG:    ReRegisterHotkeys(); break;
                case WM_APP_SPEAKING:       SetEscHotkey(m.wParam != 0); break;
                default: Native.TranslateMessage(in m); Native.DispatchMessageW(in m); break;
            }
        }
        if (_mouseHook != 0) Native.UnhookWindowsHookEx(_mouseHook);
        Native.DestroyWindow(_hwnd);
    }

    private void InstallMouseHook()
    {
        _mouseHook = Native.SetWindowsHookExW(Native.WH_MOUSE_LL, &MouseProc, Native.GetModuleHandleW(null), 0);
        if (_mouseHook == 0) Log.Error($"SetWindowsHookEx failed: {Marshal.GetLastPInvokeError()}");
    }

    private void ReinstallHook()                               // prima il nuovo, poi via il vecchio: nessun buco
    {
        nint old = _mouseHook;
        InstallMouseHook();
        if (old != 0) Native.UnhookWindowsHookEx(old);         // fallisce in modo innocuo se Windows l'aveva gia' rimosso
    }

    [UnmanagedCallersOnly]
    private static nint MouseProc(int nCode, nuint wParam, nint lParam)
    {
        try
        {
            if (nCode == Native.HC_ACTION && s_self is { } self &&
                self.OnMouse((uint)wParam, in *(MSLLHOOKSTRUCT*)lParam))
                return 1;                                      // inghiottito: ne' altri hook ne' la finestra bersaglio lo vedono
        }
        catch { /* mai far uscire eccezioni da una callback nativa */ }
        return Native.CallNextHookEx(0, nCode, wParam, lParam);
    }

    private bool OnMouse(uint msg, in MSLLHOOKSTRUCT d)
    {
        if (msg == Native.WM_MOUSEMOVE) return false;          // percorso veloce: fino a 1000 eventi/s
        int button; bool down;
        switch (msg)
        {
            case Native.WM_MBUTTONDOWN: button = 0; down = true; break;
            case Native.WM_MBUTTONUP:   button = 0; down = false; break;
            case Native.WM_XBUTTONDOWN: button = (int)(d.mouseData >> 16); down = true; break;   // 1 o 2
            case Native.WM_XBUTTONUP:   button = (int)(d.mouseData >> 16); down = false; break;
            default: return false;                             // sinistro, destro, rotella: mai toccati
        }
        TriggerConfig cfg = Volatile.Read(ref _config);
        if (!cfg.Enabled || d.dwExtraInfo == OwnInjectionTag) return false;
        if ((d.flags & Native.LLMHF_INJECTED) != 0 && cfg.IgnoreInjected) return false;
        TriggerRole role = cfg.RoleOfMouseButton(button);
        if (role == TriggerRole.None) return false;

        int bit = 1 << button;
        if (down)
        {
            if (cfg.BypassVk != 0 && Native.GetAsyncKeyState(cfg.BypassVk) < 0) return false;  // es. Ctrl+Shift: click vero
            _swallowedMask |= bit;
        }
        else
        {
            if ((_swallowedMask & bit) == 0) return false;     // UP orfano: il DOWN non era nostro, lascialo passare
            _swallowedMask &= ~bit;
        }
        _events.Writer.TryWrite(new InputEvent(role, down ? Phase.Down : Phase.Up, d.pt.X, d.pt.Y, d.time));
        return true;
    }
    // OnHotkey, SetEscHotkey, ApplyConfig (PostMessageW a _hwnd), Dispose (PostMessageW WM_QUIT) omessi.
}
```

Punti chiave:
- **Durata della callback**: nessuna allocazione significativa, nessun lock conteso, nessuna chiamata COM/UIA/file/log sincrono. `Channel.TryWrite` su canale unbounded non blocca.
- **Primo evento**: per evitare il costo JIT alla prima chiamata pubblicare con `PublishReadyToRun=true` e, all'avvio, chiamare `RuntimeHelpers.PrepareMethod` su `OnMouse` (la wrapper `[UnmanagedCallersOnly]` non e' invocabile da codice gestito, per questo la logica sta in un metodo normale).
- **Configurazione**: oggetto immutabile sostituito con `Volatile.Write`; la callback non prende lock.
- **Esclusioni per applicazione** (es. un gioco): non interrogare il processo in primo piano dentro la callback; mantenere un flag `volatile` aggiornato da `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` su un altro thread.
- **Pass-through**: un modificatore configurabile tenuto premuto lascia passare il click centrale vero (l'assistente puo' voler usare l'autoscroll). In alternativa la pressione lunga puo' re-iniettare un click centrale con `SendInput` e `dwExtraInfo = OwnInjectionTag`.
- **Debug**: fermarsi a un breakpoint nel processo blocca il mouse di tutto il sistema fino al timeout e poi Windows rimuove l'hook. Con `Debugger.IsAttached` conviene non installare l'hook mouse e provare con l'hotkey.

### 4. Click corto vs pressione lunga (core portabile)

```csharp
public sealed class PressGestureRecognizer(GestureOptions opt, Action<TriggerRole, int, int, bool> fire)
{
    private InputEvent? _pending; private CancellationTokenSource? _cts; private bool _longFired;
    private uint _lastActivationMs;

    public void OnEvent(InputEvent ev)
    {
        if (ev.Phase == Phase.Down)
        {
            if (unchecked(ev.TimeMs - _lastActivationMs) < opt.DebounceMs) return;     // tremore / doppio click
            if (!opt.LongPressEnabled) { Activate(ev, isLong: false); return; }        // DEFAULT: agisci sul DOWN
            _pending = ev; _longFired = false; _cts = new CancellationTokenSource();
            _ = LongPressTimerAsync(ev, _cts.Token);
        }
        else if (ev.Phase == Phase.Up && _pending is { } down)
        {
            _cts?.Cancel(); _pending = null;
            if (!_longFired) Activate(down, isLong: false);                            // click corto deciso sull'UP
        }
    }

    private async Task LongPressTimerAsync(InputEvent down, CancellationToken ct)
    {
        try { await Task.Delay(opt.HoldThresholdMs, ct); } catch (OperationCanceledException) { return; }
        _longFired = true; Activate(down, isLong: true);                               // scatta mentre e' ancora premuto
    }

    private void Activate(InputEvent ev, bool isLong)
    { _lastActivationMs = ev.TimeMs; fire(ev.Role, ev.X, ev.Y, isLong); }              // usa SEMPRE il punto del DOWN
}
```

Il "secondo click ferma la voce" sta a valle: se il motore voce sta parlando, l'attivazione corta = `Stop()`; l'anti-rimbalzo evita che un doppio click involontario avvii e fermi subito. Suggerimento: alla ricezione del DOWN il worker puo' gia' catturare lo schermo (20-40 ms) e buttare via l'immagine se il gesto si rivela diverso: cosi' l'immagine corrisponde esattamente all'istante del click.

### 5. `LowLevelHooksTimeout` e rimozione silenziosa

Fatti documentati:
- L'hook viene chiamato **nel contesto del thread che l'ha installato**, tramite un messaggio: quel thread **deve avere un message loop**.
- La callback deve terminare entro `HKCU\Control Panel\Desktop\LowLevelHooksTimeout` (ms). Da Windows 10 1709 il **massimo e' 1000 ms** (valori piu' alti vengono riportati a 1000). Se il valore non esiste il default riportato e' **300 ms** (blog del supporto Microsoft, Windows 7; non ridocumentato per Windows 11).
- Al timeout Windows passa l'evento oltre; da Windows 7 **l'hook viene rimosso in silenzio** e l'app non ha modo di saperlo. Il blog Microsoft precisa: sono tollerati 10 timeout, all'undicesimo l'hook viene sganciato.
- Microsoft raccomanda: hook su thread dedicato che passa il lavoro a un worker e ritorna subito; per la sola osservazione preferire Raw Input.

Conseguenze di progetto:
- Non modificare la chiave di registro (e' per-utente, vale per tutti i programmi, e comunque e' limitata a 1000 ms).
- Cause tipiche di timeout in .NET: GC bloccante che sospende anche l'InputThread, processo paginato su disco dopo lunga inattivita', CPU saturata da OCR/ONNX, breakpoint. Mitigazioni: heap piccolo, GC concurrent (default), `Highest` sull'InputThread, worker pesanti a `BelowNormal` e `intra_op_num_threads` limitato in ONNX Runtime.
- **Reinstallazione**: `SystemEvents.SessionSwitch` (`SessionUnlock`), `SystemEvents.PowerModeChanged` (`Resume`), `SystemEvents.DisplaySettingsChanged` -> `PostMessageW(_hwnd, WM_APP_REHOOK)`. La chiamata a `SetWindowsHookExW` deve avvenire sull'InputThread (l'hook appartiene al thread che lo installa). In piu', ogni 60 s, se `GetLastInputInfo` indica inattivita' di qualche secondo e `_swallowedMask == 0`, reinstallare. Costo trascurabile; effetto collaterale: si torna in testa alla catena degli hook (gli LL hook sono chiamati dal piu' recente).
- **Watchdog opzionale (fase 2)**: `RegisterRawInputDevices` (usage page 1, usage 2, `RIDEV_INPUTSINK`, `hwndTarget = _hwnd`) consegna `WM_INPUT` anche in background; se Raw Input vede `RI_MOUSE_MIDDLE_BUTTON_DOWN` ma l'hook non ha contato nessun evento entro ~200 ms, l'hook e' morto -> reinstalla. Costo: un secondo risveglio del thread per ogni movimento del mouse; attivarlo solo se il problema si presenta davvero.

### 6. Trigger da tastiera: `RegisterHotKey` vs `WH_KEYBOARD_LL`

| | `RegisterHotKey` | `WH_KEYBOARD_LL` |
|---|---|---|
| Timeout / rimozione silenziosa | No | Si' (stessi rischi dell'hook mouse) |
| Finestra elevata in primo piano | Funziona | Non riceve nulla (UIPI) |
| Consuma il tasto | Sempre | A scelta (ritorno 1) |
| Tasto singolo senza modificatori (F9, Pausa, F13-F24 di un pulsante esterno) | Si' (`fsModifiers = 0`), ma il tasto sparisce per tutte le app | Si', anche senza consumarlo |
| Solo modificatore, tenere premuto, Ctrl sx/dx | No | Si' |
| Combinazione gia' usata da altri | Fallisce (`ERROR_HOTKEY_ALREADY_REGISTERED` 1409): segnalarlo nella finestra impostazioni | Nessun controllo |

Dettagli `RegisterHotKey`: con `hWnd = NULL` o con la nostra finestra message-only il `WM_HOTKEY` viene **postato** nella coda del thread che ha registrato: registrare e deregistrare sempre dall'InputThread. Usare `MOD_NOREPEAT` (0x4000). F12 e' riservato al debugger; le combinazioni con `MOD_WIN` sono riservate al sistema.

```csharp
private const int HK_READ_POINTER = 1, HK_READ_SELECTION = 2, HK_STOP = 3, HK_ESC = 4;

private void SetEscHotkey(bool speaking)          // eseguito sull'InputThread (WM_APP_SPEAKING)
{
    if (speaking) Native.RegisterHotKey(_hwnd, HK_ESC, Native.MOD_NOREPEAT, 0x1B /* VK_ESCAPE */);
    else          Native.UnregisterHotKey(_hwnd, HK_ESC);
}
```

Con questo schema, mentre la voce parla Esc ferma la voce e **non** arriva all'app (il menu aperto resta aperto); a voce ferma Esc torna normale. Se si preferisce che Esc arrivi comunque all'app, serve `WH_KEYBOARD_LL` in sola osservazione oppure Raw Input tastiera con `RIDEV_INPUTSINK` (consigliato per "qualsiasi tasto ferma la voce": nessun rischio di timeout).

Attenzione al layout italiano: **AltGr = Ctrl+Alt**. Un hotkey `Ctrl+Alt+<tasto>` scatta anche con AltGr+<tasto> e ruba caratteri come `@ # [ ] { } EUR`. Preferire tasti funzione con modificatori (es. `Ctrl+Shift+F9`) o tasti F13-F24 emessi da pulsanti esterni.

Hotkey "leggi selezione": quando arriva `WM_HOTKEY` i modificatori sono ancora fisicamente premuti. Se il fallback per leggere la selezione simula Ctrl+C, i modificatori tenuti lo corrompono: attendere il rilascio (`GetAsyncKeyState`) o usare prima UIA `TextPattern.GetSelection`. Un pulsante mouse (es. XBUTTON1 = leggi selezione) non ha questo problema.

Sketch `WH_KEYBOARD_LL` (solo se necessario; stessa struttura dell'hook mouse, stesso thread):

```csharp
[UnmanagedCallersOnly]
private static nint KeyboardProc(int nCode, nuint wParam, nint lParam)
{
    try
    {
        if (nCode == Native.HC_ACTION && s_self is { } self)
        {
            ref readonly KBDLLHOOKSTRUCT k = ref *(KBDLLHOOKSTRUCT*)lParam;
            bool up = (k.flags & 0x80) != 0, injected = (k.flags & 0x10) != 0;
            if (!injected && self.OnKey(k.vkCode, up)) return 1;   // OnKey: confronto con config + TryWrite
        }
    }
    catch { }
    return Native.CallNextHookEx(0, nCode, wParam, lParam);
}
```

### 7. UIPI: finestre elevate

- Un processo a integrita' media non riceve, tramite hook low-level e Raw Input, l'input destinato a finestre di processi a integrita' piu' alta; non puo' nemmeno iniettare input ne' leggere l'albero UIA di quelle finestre. `RegisterHotKey` invece continua a funzionare.
- Un processo con **UIAccess** puo', secondo la documentazione Microsoft, impostare la finestra in primo piano e leggere l'input a tutti i livelli di integrita' tramite hook low-level, raw input, `GetKeyState`, `GetAsyncKeyState`.

| Opzione | Requisiti | Pro | Contro |
|---|---|---|---|
| A. `asInvoker`, non elevata (**consigliata v1**) | nessuno | portabile, nessun UAC, DPAPI e impostazioni nel profilo giusto | nessun trigger mouse/UIA su finestre elevate (caso raro per questo utente) |
| B. Elevata (`requireAdministrator` o attivita' pianificata "privilegi piu' elevati" al logon) | account admin | copre le finestre elevate | superficie d'attacco; con **Administrator Protection** di Windows 11 il processo elevato gira con un account amministratore gestito dal sistema e un profilo separato: DPAPI `CurrentUser` (chiave ElevenLabs), `%APPDATA%`, cache audio non coincidono piu'; l'avvio automatico richiede Task Scheduler |
| C. `uiAccess="true"` | firma Authenticode con catena attendibile sulla macchina (un certificato self-signed va messo nelle Trusted Root del computer) **e** percorso sicuro: `%ProgramFiles%` (o `%ProgramFiles(x86)%`, `%SystemRoot%\system32`). Il controllo della firma e' sempre applicato, anche disattivando la policy sui percorsi | stesso comportamento di NVDA installato; nessun prompt UAC | incompatibile con la cartella portabile; va avviata con ShellExecute (non `CreateProcess` da processo normale); debug scomodo |

Manifest per l'opzione A (insieme al DPI, vedi sotto): `<requestedExecutionLevel level="asInvoker" uiAccess="false" />`.

Precedente utile: la guida utente di NVDA (2026.2) elenca tra i limiti delle copie portabili proprio l'impossibilita' di interagire con applicazioni avviate con privilegi amministrativi, a meno di avviare NVDA stesso con quei privilegi (sconsigliato).

### 8. DPI: Per-Monitor V2 e coordinate

`app.manifest` (nel csproj: `<ApplicationManifest>app.manifest</ApplicationManifest>`; il manifest viene incorporato nell'apphost .exe, anche in pubblicazione self-contained):

```xml
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0" xmlns:asmv3="urn:schemas-microsoft-com:asm.v3">
  <assemblyIdentity version="1.0.0.0" name="PuntaEAscolta.app"/>
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v2">
    <security><requestedPrivileges xmlns="urn:schemas-microsoft-com:asm.v3">
      <requestedExecutionLevel level="asInvoker" uiAccess="false"/>
    </requestedPrivileges></security>
  </trustInfo>
  <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1"><application>
    <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}"/>  <!-- Windows 10 / 11 -->
  </application></compatibility>
  <asmv3:application><asmv3:windowsSettings>
    <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true</dpiAware>
    <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
  </asmv3:windowsSettings></asmv3:application>
</assembly>
```

- Microsoft raccomanda il manifest, non l'API: dopo la creazione del primo HWND la modalita' non si puo' piu' cambiare, e WPF/SystemEvents creano finestre molto presto.
- Con PMv2 l'applicazione "vede i pixel grezzi di ogni display": nessuna virtualizzazione di `GetCursorPos`, `GetWindowRect`, `GetMonitorInfo`. `MSLLHOOKSTRUCT.pt` e' documentato come "per-monitor-aware screen coordinates".
- La documentazione UIA ("Understanding Screen Scaling Issues") richiede che il client sia DPI-aware e usi coordinate fisiche per `ElementFromPoint`; `BoundingRectangle` e' in coordinate fisiche dello schermo. Per i trigger da tastiera usare `GetPhysicalCursorPos`.
- Verifica all'avvio (da loggare): `AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(), (nint)(-4))` dove `-4` = `DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2`. Se e' falso, tutte le coordinate saranno sbagliate sui display scalati (lo Zenbook lavora tipicamente al 150-200 %).
- **Multi-monitor**: origine (0,0) = angolo alto-sinistro del primario; monitor a sinistra o sopra hanno coordinate negative. Mai passare per `LOWORD/HIWORD` o `uint`. Limiti del desktop virtuale: `GetSystemMetrics(76..79)` (`SM_XVIRTUALSCREEN`, `SM_YVIRTUALSCREEN`, `SM_CXVIRTUALSCREEN`, `SM_CYVIRTUALSCREEN`).
- Ritagliare il rettangolo di cattura **sul monitor che contiene il puntatore** (`MonitorFromPoint` + `GetMonitorInfoW`): evita immagini a cavallo di due monitor con scale diverse. Dimensionare il rettangolo in DIP e moltiplicare per `dpi/96` (`GetDpiForMonitor`, `shcore.dll`, `MDT_EFFECTIVE_DPI = 0`): al 200 % il testo UI ha gia' il doppio dei pixel e serve meno upscaling prima dell'OCR.

```csharp
[StructLayout(LayoutKind.Sequential)] internal struct RECT { public int Left, Top, Right, Bottom; }
[StructLayout(LayoutKind.Sequential)] internal struct MONITORINFO { public uint cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; } // cbSize = 40

[LibraryImport("user32.dll")] internal static partial nint MonitorFromPoint(POINT pt, uint flags);      // MONITOR_DEFAULTTONEAREST = 2
[LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
internal static partial bool GetMonitorInfoW(nint hMonitor, ref MONITORINFO lpmi);
[LibraryImport("shcore.dll")] internal static partial int GetDpiForMonitor(nint hMonitor, int dpiType, out uint dpiX, out uint dpiY);
[LibraryImport("user32.dll")] internal static partial nint GetThreadDpiAwarenessContext();
[LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
internal static partial bool AreDpiAwarenessContextsEqual(nint a, nint b);
```

### 9. Cattura di un rettangolo attorno al puntatore

Confronto:

| | GDI `BitBlt` / `Graphics.CopyFromScreen` | `Windows.Graphics.Capture` (WGC) | DXGI Desktop Duplication |
|---|---|---|---|
| Codice | ~40 righe P/Invoke, sincrono | device D3D11 + interop COM (`IGraphicsCaptureItemInterop::CreateForMonitor`, Windows 10 1903+) + frame pool + copia su texture staging | device D3D11, `IDXGIOutput1::DuplicateOutput`, `AcquireNextFrame`, staging, gestione rotazione |
| Menu popup, tooltip, finestre layered | Si' (desktop composto da DWM; `CAPTUREBLT` e' di fatto ininfluente da Windows 8 ma innocuo) | Si' con cattura del **monitor**; No con cattura della singola **finestra** (i popup sono finestre top-level separate) | Si' |
| App accelerate (D3D/OpenGL in finestra) | Si' | Si' | Si' |
| Cursore | Mai nell'immagine | Incluso per default; `IsCursorCaptureEnabled = false` (Windows 10 2004+) | Separato dall'immagine nella maggior parte dei casi (forma a parte) |
| Effetti visibili | Nessuno | **Bordo giallo** attorno al monitor catturato; `IsBorderRequired = false` esiste da build 20348 / Windows 11 e per doc richiede consenso + capability di pacchetto (comportamento per app non pacchettizzate da verificare) | Nessuno |
| Errori da gestire | `BitBlt` false | device lost, elemento chiuso | `DXGI_ERROR_ACCESS_LOST` (cambio modalita', desktop sicuro), `DXGI_ERROR_UNSUPPORTED` su GPU discreta nei sistemi ibridi, massimo 4 duplicazioni concorrenti |
| Contenuto protetto (`SetWindowDisplayAffinity`, DRM) | nero / assente | nero / assente | nero / assente |
| Adatto a "uno scatto su richiesta" | Si' (10-40 ms tipici) | Sovradimensionato (avvio sessione + primo frame) | Sovradimensionato |

Scelta: **BitBlt via P/Invoke**, che restituisce direttamente byte BGRA utilizzabili per `SoftwareBitmap` (`Windows.Media.Ocr`) o per un tensore ONNX, senza `System.Drawing`. `Graphics.CopyFromScreen(x, y, 0, 0, size, CopyPixelOperation.SourceCopy | CopyPixelOperation.CaptureBlt)` fa la stessa cosa internamente ed e' un'alternativa di 3 righe, ma richiede il pacchetto `System.Drawing.Common` 10.0.x (solo Windows) in un progetto WPF puro.

```csharp
[StructLayout(LayoutKind.Sequential)]
internal struct BITMAPINFOHEADER { public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
    public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant; } // 40 byte
[StructLayout(LayoutKind.Sequential)]
internal struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public uint bmiColors; }

[LibraryImport("user32.dll")] internal static partial nint GetDC(nint hWnd);
[LibraryImport("user32.dll")] internal static partial int ReleaseDC(nint hWnd, nint hDC);
[LibraryImport("gdi32.dll")] internal static partial nint CreateCompatibleDC(nint hdc);
[LibraryImport("gdi32.dll")] internal static partial nint CreateDIBSection(nint hdc, in BITMAPINFO bmi, uint usage, out nint bits, nint hSection, uint offset);
[LibraryImport("gdi32.dll")] internal static partial nint SelectObject(nint hdc, nint obj);
[LibraryImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
internal static partial bool BitBlt(nint hdcDst, int x, int y, int cx, int cy, nint hdcSrc, int x1, int y1, uint rop);
[LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool GdiFlush();
[LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool DeleteObject(nint obj);
[LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool DeleteDC(nint hdc);

public sealed record CapturedImage(byte[] Bgra, int Width, int Height, int ScreenX, int ScreenY, uint Dpi);

public static CapturedImage CaptureAround(int px, int py, int widthDip = 640, int heightDip = 200)
{
    nint mon = MonitorFromPoint(new POINT { X = px, Y = py }, 2);
    var mi = new MONITORINFO { cbSize = 40 }; GetMonitorInfoW(mon, ref mi);
    GetDpiForMonitor(mon, 0, out uint dpi, out _);
    int w = (int)(widthDip * dpi / 96), h = (int)(heightDip * dpi / 96);
    int x = Math.Clamp(px - w / 2, mi.rcMonitor.Left, Math.Max(mi.rcMonitor.Left, mi.rcMonitor.Right - w));
    int y = Math.Clamp(py - h / 2, mi.rcMonitor.Top,  Math.Max(mi.rcMonitor.Top,  mi.rcMonitor.Bottom - h));
    w = Math.Min(w, mi.rcMonitor.Right - x); h = Math.Min(h, mi.rcMonitor.Bottom - y);

    const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;
    nint screen = GetDC(0), mem = CreateCompatibleDC(screen);
    var bmi = new BITMAPINFO { bmiHeader = new() { biSize = 40, biWidth = w, biHeight = -h /* top-down */, biPlanes = 1, biBitCount = 32 } };
    nint dib = CreateDIBSection(screen, in bmi, 0, out nint bits, 0, 0), old = SelectObject(mem, dib);
    try
    {
        if (!BitBlt(mem, 0, 0, w, h, screen, x, y, SRCCOPY | CAPTUREBLT))     // x, y possono essere negativi
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        GdiFlush();
        var px32 = new byte[w * h * 4];
        Marshal.Copy(bits, px32, 0, px32.Length);
        for (int i = 3; i < px32.Length; i += 4) px32[i] = 255;             // l'alfa letto dallo schermo e' 0/indefinito
        return new CapturedImage(px32, w, h, x, y, dpi);
    }
    finally { SelectObject(mem, old); DeleteObject(dib); DeleteDC(mem); ReleaseDC(0, screen); }
}
```

Note: l'OCR di testo piccolo (menu Affinity a 9-10 pt) migliora molto con upscaling 2-3x (bicubico/Lanczos) prima del riconoscimento, soprattutto al 100 % di scala; tenere la mappatura pixel-immagine -> coordinate schermo (`ScreenX/ScreenY`) per scegliere la riga di testo piu' vicina al puntatore. La cattura va eseguita sul worker, mai nell'InputThread.

### 10. Non rubare mai il focus

- Se la nostra app attiva una finestra, i menu classici Win32 e molti popup custom (Affinity, Photoshop) si chiudono all'istante: e' il fallimento peggiore per questo prodotto.
- Avvio WPF senza finestra: niente `StartupUri`, `ShutdownMode = OnExplicitShutdown`; creare solo tray icon e `InputThread`. La finestra impostazioni si crea su richiesta dal tray (li' l'attivazione e' voluta).
- Nel percorso di lettura vietati: `MessageBox`, `Window.Show()` normale, toast, `SetForegroundWindow`, `AutomationElement.SetFocus()`, `InvokePattern`, `LegacyIAccessible.DoDefaultAction`. Solo lettura di proprieta' UIA.
- Eventuale overlay futuro (il requisito attuale e' solo audio):

```csharp
// WPF: ShowActivated = false, Focusable = false, Topmost = true, ShowInTaskbar = false; poi in OnSourceInitialized:
const int GWL_EXSTYLE = -20; const long WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x80, WS_EX_TRANSPARENT = 0x20;
[LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static partial nint GetWindowLongPtr(nint hWnd, int index);
[LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] internal static partial nint SetWindowLongPtr(nint hWnd, int index, nint value);
// SetWindowLongPtr(hwnd, GWL_EXSTYLE, GetWindowLongPtr(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT);
```

  (`GetWindowLongPtrW`/`SetWindowLongPtrW` esistono come export solo a 64 bit: va bene, i target sono x64 e ARM64.)

### 11. Istanza singola

```csharp
internal static class SingleInstance
{
    private static Mutex? s_mutex;                                      // statico: non deve essere raccolto dal GC
    private const string MutexName = @"Local\PuntaEAscolta.Instance";   // Local\ = per sessione utente
    private const string ShowName  = @"Local\PuntaEAscolta.ShowSettings";

    public static bool TryAcquire(Action onShowSettingsRequested)
    {
        s_mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            if (EventWaitHandle.TryOpenExisting(ShowName, out var ev)) ev.Set();   // avvisa la prima istanza
            return false;                                                          // e termina
        }
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowName);
        new Thread(() => { while (show.WaitOne()) onShowSettingsRequested(); }) { IsBackground = true }.Start();
        return true;
    }
}
```

La seconda istanza non mostra nulla durante l'uso normale; se l'assistente fa doppio click sull'eseguibile mentre l'app e' gia' attiva, si apre la finestra impostazioni della prima.

### 12. x64 e ARM64

- Stesse firme P/Invoke e stessi layout (LLP64: `int`/`uint` 32 bit, puntatori e `nint` 64 bit).
- WPF supporta Windows ARM64 da .NET 6; pubblicare `dotnet publish -r win-arm64 --self-contained` e `-r win-x64 --self-contained` (due cartelle portabili, o una con due sottocartelle e un piccolo launcher).
- Evitare di far girare la build x64 in emulazione sullo Snapdragon: l'hook LL e' nel percorso di ogni evento mouse del sistema.
- Dipendenze native (ONNX Runtime, eventuali codec) devono avere binari `win-arm64`: fuori dallo scopo di questo documento, ma condiziona il packaging.

---

## Insidie note

1. **Hook rimosso in silenzio** dopo timeout ripetuti; nessuna notifica. Progettare per callback < 1 ms tipico e prevedere la reinstallazione.
2. **Thread senza message loop = hook morto e mouse a scatti.** Mai installare l'hook sul thread UI di WPF (layout, GC, finestre modali bloccano il pump).
3. **Delegate raccolto dal GC**: l'errore classico dei tutorial C# (crash casuali `ExecutionEngineException`/access violation). Risolto alla radice usando `[UnmanagedCallersOnly]` + function pointer; se si usa un delegate, tenerlo in un campo `static readonly`.
4. **Eccezioni che escono da `[UnmanagedCallersOnly]`** terminano il processo: `try/catch` totale.
5. **DOWN/UP spaiati**: se la configurazione cambia o l'app viene messa in pausa mentre il pulsante e' giu', l'UP va comunque inghiottito (maschera `_swallowedMask`). Un UP orfano verso l'app bersaglio e' innocuo; un DOWN senza UP no (pan/autoscroll che resta agganciato).
6. **Raw Input non viene bloccato dall'hook**: ritornare 1 ferma i messaggi `WM_MBUTTON*`/`WM_POINTER*`, ma un'app che legge i pulsanti via `WM_INPUT` li vede lo stesso. Tipico dei giochi; da verificare per il pan di Affinity/Photoshop.
7. **`LLMHF_INJECTED`**: risultano iniettati anche i click generati da software assistivi, utility del produttore del mouse, desktop remoto. Ignorarli a priori puo' rendere l'app inutilizzabile proprio con gli ausili: opzione configurabile.
8. **Software del mouse** (Logitech Options e simili) puo' rimappare i tasti laterali in scorciatoie da tastiera: l'hook non vedra' mai `WM_XBUTTON*`.
9. **UIPI**: con una finestra elevata sotto il puntatore/in primo piano il click centrale non arriva all'hook e passa all'app. Comportamento atteso con l'opzione A; documentarlo per l'assistente.
10. **Administrator Protection** (Windows 11 24H2/25H2, in distribuzione graduale dal 2025-2026, disattivata per default): l'elevazione usa un account amministratore gestito dal sistema con profilo separato. Un'app elevata non ritroverebbe chiave DPAPI, impostazioni e cache. Altro motivo per non elevare.
11. **AltGr = Ctrl+Alt** sul layout italiano: evitare hotkey `Ctrl+Alt+lettera/simbolo`.
12. **`RegisterHotKey` fallisce** se la combinazione e' gia' presa: controllare il valore di ritorno e mostrarlo nelle impostazioni; ha affinita' di thread (registrazione e `WM_HOTKEY` sullo stesso thread).
13. **Esc registrato in modo permanente** toglierebbe Esc a tutto il sistema: solo dinamico mentre si parla, e deregistrare anche in caso di errore/uscita (`finally`).
14. **DPI non dichiarato o dichiarato tardi**: coordinate virtualizzate, UIA e cattura sfasati sui display scalati. Dichiarare nel manifest; verificare a runtime e loggare.
15. **Coordinate negative** con monitor a sinistra/sopra del primario; `pt` dell'hook su `WM_MOUSEMOVE` puo' essere fuori dai limiti dello schermo (posizione proposta): usare solo il punto degli eventi pulsante e fare clamp.
16. **Canale alfa a 0** nei pixel letti dallo schermo: forzare 255 (o usare `BitmapAlphaMode.Ignore`) prima di creare il `SoftwareBitmap`, altrimenti alcune pipeline vedono un'immagine trasparente.
17. **HDR**: con HDR attivo le catture GDI possono risultare slavate/sovraesposte; per l'OCR di solito basta una normalizzazione del contrasto. WGC gestisce meglio l'HDR (piano B).
18. **Contenuto protetto** (`WDA_MONITOR`, `WDA_EXCLUDEFROMCAPTURE`, DRM) appare nero o assente con qualsiasi API: l'OCR non trovera' nulla; risposta vocale "nessun testo trovato".
19. **WGC**: bordo giallo attorno al monitor a ogni cattura se non si ottiene la modalita' borderless; cattura "per finestra" non include i menu popup.
20. **Desktop Duplication**: fallisce con `DXGI_ERROR_UNSUPPORTED` se il processo gira sulla GPU discreta di un sistema ibrido; max 4 client; `ACCESS_LOST` a ogni cambio modalita'/UAC/lock.
21. **Breakpoint nel processo con hook attivo**: mouse bloccato per tutto il sistema fino al timeout. Disattivare l'hook sotto debugger.
22. **`SystemEvents`** usa eventi statici: deregistrarsi all'uscita; i gestori girano su un thread di sistema di .NET, quindi limitarsi a `PostMessageW` verso l'InputThread.
23. **Mutex raccolto dal GC** in Release se tenuto in una variabile locale di `Main`: campo statico.
24. **Modern Standby** (Snapdragon X): i processi desktop possono essere congelati a lungo; al risveglio il primo evento puo' trovare il processo paginato -> rischio timeout. Reinstallare al resume e allo sblocco.

---

## Domande aperte da verificare sulla macchina

(Eseguibili appena installato il .NET 10 SDK; alcune verificabili prima con un prototipo Python 3.13 ARM64 + `ctypes`, che usa le stesse API.)

1. **Tocco a tre dita del touchpad di precisione = pulsante centrale**: arriva all'hook come `WM_MBUTTONDOWN/UP`? Con `LLMHF_INJECTED` impostato? (Su alcuni dispositivi i gesti del touchpad vengono emessi come scorciatoie da tastiera.)
2. **Affinity v3 e Photoshop**: inghiottendo `WM_MBUTTON*` il pan con il pulsante centrale sparisce davvero, o leggono il mouse via Raw Input / Wintab / `WM_POINTER`? Stessa prova con autoscroll in Word, Edge, Chrome.
3. **Menu aperti**: con un menu di Affinity/Word aperto, il click centrale inghiottito lascia il menu aperto? (Atteso si'. Verificare anche i sottomenu che si aprono al passaggio.)
4. **BitBlt su Affinity v3 / Photoshop** (rendering GPU su Adreno): menu, pannelli, tooltip e area di lavoro compaiono correttamente? Tempo di cattura per 640x200 DIP al 150-200 %? Con HDR attivo?
5. **Timeout reale su Windows 11 25H2**: il valore `LowLevelHooksTimeout` esiste nel registro? Misurare durata massima della callback con OCR/ONNX a pieno carico e verificare se il mouse scatta.
6. **Sopravvivenza dell'hook** a: blocco/sblocco (Win+L), Modern Standby lungo, chiusura coperchio, cambio utente rapido, prompt UAC, connessione/disconnessione monitor esterno. `SystemEvents.SessionSwitch` e `PowerModeChanged` arrivano su questo hardware?
7. **Conflitti hotkey** con Word/Excel/Photoshop/Affinity e con le scorciatoie di Windows 11 (incluso il tasto Copilot); scelta dei default.
8. **Mouse dell'utente**: ha tasti laterali? C'e' software del produttore che li rimappa? La rotella-click e' troppo dura per l'utente (meglio XBUTTON o pulsante esterno che emette F13-F24)?
9. **Ausili di puntamento** usati dall'utente (se presenti): i loro click risultano iniettati?
10. **Administrator Protection**: e' attiva su questa macchina? (Impostazioni > Sicurezza di Windows > Protezione account.)
11. **WGC borderless da app non pacchettizzata** su build 26200: `IsBorderRequired = false` ha effetto senza capability? (Solo se servira' il piano B.)
12. **`WTSRegisterSessionNotification` e `RegisterSuspendResumeNotification` su finestra message-only**: funzionano? (Alternativa a `SystemEvents` per tenere tutto sull'InputThread.)
13. **Latenza end-to-end** DOWN -> inizio audio con azione sul DOWN vs sull'UP, per decidere il default definitivo.

---

## Fonti

Hook e input
- LowLevelMouseProc (aggiornato 2025-07; timeout, rimozione silenziosa, massimo 1000 ms da Windows 10 1709, thread dedicato): https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelmouseproc
- LowLevelKeyboardProc: https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc
- MSLLHOOKSTRUCT (pt per-monitor-aware, XBUTTON, LLMHF_INJECTED): https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-msllhookstruct
- SetWindowsHookExW (hook LL chiamati sul thread installatore, nota .NET sul GC): https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowshookexw
- "Global hooks getting lost on Windows 7" (Microsoft, 2010: default 300 ms, 10 timeout tollerati, consigliato Raw Input): https://learn.microsoft.com/en-us/archive/blogs/alejacma/global-hooks-getting-lost-on-windows-7
- AutoHotkey, discussione su timeout e ri-registrazione dell'hook: https://www.autohotkey.com/boards/viewtopic.php?t=62932
- RegisterHotKey (WM_HOTKEY postato al thread, MOD_NOREPEAT, F12 riservato): https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey
- Raw Input (panoramica e uso): https://learn.microsoft.com/en-us/windows/win32/inputdev/about-raw-input , https://learn.microsoft.com/en-us/windows/win32/inputdev/using-raw-input

UIPI / UIAccess
- Impostazioni UAC (2026-05: percorsi sicuri per UIAccess, controllo firma sempre applicato): https://learn.microsoft.com/en-us/windows/security/application-security/application-control/user-account-control/settings-and-configuration
- Policy "Only elevate UIAccess applications that are installed in secure locations" (capacita' dei processi UIAccess: foreground, lettura input a tutti i livelli via hook LL/raw input): https://learn.microsoft.com/en-us/windows/security/threat-protection/security-policy-settings/user-account-control-only-elevate-uiaccess-applications-that-are-installed-in-secure-locations
- Appunti UIPI (RegisterHotKey non bloccato, SendInput e WH_KEYBOARD_LL si'): https://github.com/Chaoses-Ib/Windows/blob/main/Kernel/Security/UIPI.md
- NVDA 2026.2 User Guide, limiti delle copie portabili: https://download.nvaccess.org/releases/2026.2/documentation/userGuide.html
- Administrator Protection in Windows 11 25H2: https://patchmypc.com/blog/administrator-protection-windows-11-25h2/ , https://call4cloud.nl/administrator-protection-windows-25h2/

DPI
- Setting the default DPI awareness for a process (manifest PerMonitorV2, XML di esempio): https://learn.microsoft.com/en-us/windows/win32/hidpi/setting-the-default-dpi-awareness-for-a-process
- High DPI Desktop Application Development on Windows (aggiornato 2026-09-10; PMv2 "seeing the raw pixels", virtualizzazione): https://learn.microsoft.com/en-us/windows/win32/hidpi/high-dpi-desktop-application-development-on-windows
- UI Automation: Understanding Screen Scaling Issues: https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-screenscaling

Cattura schermo
- "Ways to capture the screen" (Microsoft, 2013; BitBlt, CAPTUREBLT, Desktop Duplication; commento: CAPTUREBLT ininfluente da Windows 8.1): https://learn.microsoft.com/en-us/archive/blogs/dsui_team/ways-to-capture-the-screen
- Capturing an Image (GDI): https://learn.microsoft.com/en-us/windows/desktop/gdi/capturing-an-image
- Graphics.CopyFromScreen: https://learn.microsoft.com/en-us/dotnet/api/system.drawing.graphics.copyfromscreen
- IGraphicsCaptureItemInterop::CreateForMonitor (Windows 10 1903+): https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.capture.interop/nf-windows-graphics-capture-interop-igraphicscaptureiteminterop-createformonitor
- GraphicsCaptureSession.IsBorderRequired (build 20348+, consenso + capability): https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.isborderrequired
- GraphicsCaptureSession.IsCursorCaptureEnabled: https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.iscursorcaptureenabled
- Win32CaptureSample (robmikh): https://github.com/robmikh/Win32CaptureSample
- Desktop Duplication API (rotazione, puntatore separato): https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/desktop-dup-api
- IDXGIOutput1::DuplicateOutput (limite 4 client, errori): https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/nf-dxgi1_2-idxgioutput1-duplicateoutput
- Errore DDA su GPU discreta in sistemi ibridi: https://learn.microsoft.com/en-us/troubleshoot/windows-client/shell-experience/error-when-dda-capable-app-is-against-gpu
- SetWindowDisplayAffinity (WDA_EXCLUDEFROMCAPTURE da Windows 10 2004): https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity

.NET e strumenti
- Announcing .NET 10 (GA 11 novembre 2025, LTS): https://devblogs.microsoft.com/dotnet/announcing-dotnet-10/
- WPF su ARM64 da .NET 6: https://github.com/dotnet/wpf/discussions/5598
- Microsoft.Windows.CsWin32 (0.3.333, 2026-09-03): https://www.nuget.org/packages/Microsoft.Windows.CsWin32
- Deployment x64/ARM64 di app desktop .NET (Rick Strahl, 2025-04): https://weblog.west-wind.com/posts/2025/Apr/18/The-Strong-ARM-of-NET-Wrestling-with-x64-and-Arm64-Desktop-App-Deployment
