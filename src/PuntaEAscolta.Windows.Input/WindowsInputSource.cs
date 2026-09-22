using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using PuntaEAscolta.Core;
using PuntaEAscolta.Core.Abstractions;
using PuntaEAscolta.Core.Settings;
using PuntaEAscolta.Windows.Input.Interop;
using static PuntaEAscolta.Windows.Input.Interop.NativeMethods;

namespace PuntaEAscolta.Windows.Input;

/// <summary>
/// Sorgente di input globale per Windows: hook WH_MOUSE_LL e RegisterHotKey su un thread dedicato
/// ("InputThread", priorità massima) con finestra message-only e ciclo GetMessage.
/// La callback dell'hook fa solo filtro e accodamento in un Channel; gli eventi vengono sollevati
/// (evento <see cref="Input"/>) da un thread del pool. La coppia DOWN+UP del pulsante attivatore viene
/// sempre inghiottita insieme: mai un UP inghiottito senza il suo DOWN e viceversa.
/// </summary>
public sealed class WindowsInputSource : IInputSource
{
    // ---- messaggi privati verso l'InputThread ----------------------------------------------

    private const uint WM_APP_APPLY = WM_APP + 1;
    private const uint WM_APP_STOPKEY = WM_APP + 2;
    private const uint WM_APP_REHOOK = WM_APP + 3;
    private const uint WM_APP_SLOWCALLBACK = WM_APP + 4;
    private const uint WM_APP_SHUTDOWN = WM_APP + 5;

    // ---- identificatori delle scorciatoie ----------------------------------------------------

    private const int HkReadAtPointer = 1;
    private const int HkReadSelection = 2;
    private const int HkStop = 3;
    private const int HkTogglePause = 4;
    private const int HkToggleDictation = 5;
    private const int HkEscape = 6;

    // ---- manutenzione dell'hook --------------------------------------------------------------

    private const nuint MaintenanceTimerId = 1;
    private const uint MaintenanceTimerMs = 30_000;
    private const long RehookIntervalMs = 10 * 60_000;
    private const uint IdleBeforeRehookMs = 3_000;
    private const int SlowCallbackMs = 20;
    private const int ThreadJoinTimeoutMs = 3_000;
    private const int ApplyTimeoutMs = 3_000;

    /// <summary>Le callback native sono statiche: una sola istanza viva per processo.</summary>
    private static WindowsInputSource? s_instance;
    private static readonly string s_windowClassName = "PuntaEAscolta.Input." + Environment.ProcessId;
    private static int s_classRegistered;

    private readonly ILog _log;
    private readonly Channel<InputEvent> _events = Channel.CreateUnbounded<InputEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true, AllowSynchronousContinuations = false });
    private readonly CancellationTokenSource _cts = new();
    private readonly ManualResetEventSlim _ready = new();
    private readonly ManualResetEventSlim _applied = new();
    private readonly object _lifecycle = new();
    private readonly object _pendingLock = new();
    private readonly HashSet<int> _registeredHotkeys = new();

    private Thread? _thread;
    private Task? _consumer;
    private uint _threadId;
    private nint _hwnd;
    private nint _broadcastHwnd;
    private nint _mouseHook;
    private nint _powerNotification;
    private bool _sessionNotification;
    private long _lastHookInstallMs;
    private int _disposed;

    // Stato toccato SOLO dall'InputThread (callback dell'hook e WndProc girano lì).
    private int _swallowedMask;
    private int _dictationMask;
    private bool _escapeRegistered;
    private bool _escapeEnabledBySettings = true;

    // Stato letto dalla callback e scritto da altri thread: snapshot immutabili o volatile.
    private volatile HookConfig _config = HookConfig.Disabled;
    private volatile bool _paused;
    private volatile bool _stopKeyRequested;
    private volatile string[] _lastProblems = Array.Empty<string>();
    private InputSettings? _pendingInput;
    private DictationSettings? _pendingDictation;

    public WindowsInputSource(ILog log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        if (Interlocked.CompareExchange(ref s_instance, this, null) is not null)
            throw new InvalidOperationException("Esiste già un'istanza attiva di WindowsInputSource in questo processo");
    }

    public event Action<InputEvent>? Input;

    public IReadOnlyList<string> LastProblems => _lastProblems;

    // ---- IInputSource -------------------------------------------------------------------------

    public void Start(InputSettings input, DictationSettings dictation)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(dictation);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        lock (_lifecycle)
        {
            if (_thread is null)
            {
                LogDpiAwareness();
                _thread = new Thread(Run)
                {
                    IsBackground = true,
                    Name = "InputThread",
                    Priority = ThreadPriority.Highest,
                };
                _thread.Start();
                _ready.Wait();
                if (_hwnd == 0)
                {
                    _lastProblems = new[] { "Impossibile avviare il thread di input: pulsante del mouse e scorciatoie non disponibili" };
                    return;
                }
                _consumer = Task.Run(ConsumeAsync);
            }
        }

        Apply(input, dictation);
    }

    public void Apply(InputSettings input, DictationSettings dictation)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(dictation);
        if (Volatile.Read(ref _disposed) != 0 || _hwnd == 0) return;

        lock (_lifecycle)
        {
            lock (_pendingLock)
            {
                _pendingInput = input;
                _pendingDictation = dictation;
            }
            _applied.Reset();
            if (!PostMessageW(_hwnd, WM_APP_APPLY, 0, 0))
            {
                _log.Error($"Impossibile inviare le impostazioni all'InputThread (errore Win32 {Marshal.GetLastPInvokeError()})");
                return;
            }
            if (!_applied.Wait(ApplyTimeoutMs))
                _log.Warn("L'InputThread non ha confermato le nuove impostazioni entro il tempo limite");
        }
    }

    public void SetStopKeyActive(bool active)
    {
        _stopKeyRequested = active;
        if (_hwnd != 0 && Volatile.Read(ref _disposed) == 0)
            PostMessageW(_hwnd, WM_APP_STOPKEY, active ? 1u : 0u, 0);
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        if (_log.IsDebugEnabled) _log.Debug(paused ? "Intercettazione del mouse in pausa" : "Intercettazione del mouse ripresa");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        Thread? thread;
        lock (_lifecycle) thread = _thread;

        if (thread is not null && thread.IsAlive)
        {
            bool posted = _hwnd != 0 && PostMessageW(_hwnd, WM_APP_SHUTDOWN, 0, 0);
            if (!posted) PostThreadMessageW(_threadId, WM_QUIT, 0, 0);
            if (!thread.Join(ThreadJoinTimeoutMs))
            {
                _log.Warn("L'InputThread non è terminato entro il tempo limite: rimozione forzata dell'hook");
                nint hook = Interlocked.Exchange(ref _mouseHook, 0);
                if (hook != 0) UnhookWindowsHookEx(hook);
            }
        }

        _cts.Cancel();
        _events.Writer.TryComplete();
        try { _consumer?.Wait(500); } catch (AggregateException) { /* annullamento atteso */ }

        Interlocked.CompareExchange(ref s_instance, null, this);
        _cts.Dispose();
        _ready.Dispose();
        _applied.Dispose();
    }

    // ---- consumatore: solleva Input su un thread del pool ------------------------------------

    private async Task ConsumeAsync()
    {
        ChannelReader<InputEvent> reader = _events.Reader;
        CancellationToken ct = _cts.Token;
        try
        {
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (reader.TryRead(out InputEvent? ev))
                {
                    Action<InputEvent>? handler = Input;
                    if (handler is null) continue;
                    try
                    {
                        handler(ev);
                    }
                    catch (Exception ex)
                    {
                        _log.Error("Errore nel gestore dell'evento di input", ex);
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* chiusura */ }
        catch (ChannelClosedException) { /* chiusura */ }
        catch (Exception ex)
        {
            _log.Error("Il consumatore degli eventi di input si è fermato", ex);
        }
    }

    // ---- InputThread ----------------------------------------------------------------------------

    private void Run()
    {
        try
        {
            _threadId = GetCurrentThreadId();
            nint hInstance = GetModuleHandleW(null);
            if (!EnsureWindowClass(hInstance)) return;

            // Finestra message-only: riceve WM_HOTKEY, WM_TIMER, i nostri WM_APP e le notifiche registrate
            // (sessione, alimentazione). Non riceve i messaggi di broadcast come WM_DISPLAYCHANGE.
            _hwnd = CreateWindowExW(0, s_windowClassName, "PuntaEAscolta Input", 0, 0, 0, 0, 0, HWND_MESSAGE, 0, hInstance, 0);
            if (_hwnd == 0)
            {
                _log.Error($"Impossibile creare la finestra di input (errore Win32 {Marshal.GetLastPInvokeError()})");
                return;
            }

            // Finestra di primo livello nascosta, mai mostrata né attivata: serve solo a ricevere WM_DISPLAYCHANGE
            // (stesso schema di Microsoft.Win32.SystemEvents).
            _broadcastHwnd = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, s_windowClassName, "PuntaEAscolta Notifiche",
                WS_POPUP, 0, 0, 0, 0, 0, 0, hInstance, 0);
            if (_broadcastHwnd == 0)
                _log.Warn($"Finestra per le notifiche di sistema non creata (errore Win32 {Marshal.GetLastPInvokeError()}): nessuna reinstallazione al cambio schermo");

            _sessionNotification = WTSRegisterSessionNotification(_hwnd, NOTIFY_FOR_THIS_SESSION);
            if (!_sessionNotification)
                _log.Warn($"Notifiche di sessione non disponibili (errore Win32 {Marshal.GetLastPInvokeError()})");
            _powerNotification = RegisterSuspendResumeNotification(_hwnd, DEVICE_NOTIFY_WINDOW_HANDLE);
            if (_powerNotification == 0)
                _log.Warn($"Notifiche di ripresa dal sospeso non disponibili (errore Win32 {Marshal.GetLastPInvokeError()})");

            PrepareHookPath();
            if (Debugger.IsAttached)
                _log.Warn("Debugger collegato: un punto di interruzione blocca il mouse di tutto il sistema finché l'hook non scade");
            InstallOrReinstallHook("avvio");

            if (SetTimer(_hwnd, MaintenanceTimerId, MaintenanceTimerMs, 0) == 0)
                _log.Warn("Timer di manutenzione dell'hook non creato");
        }
        catch (Exception ex)
        {
            _log.Error("Avvio dell'InputThread non riuscito", ex);
            Shutdown();
            return;
        }
        finally
        {
            _ready.Set();
        }

        try
        {
            while (GetMessageW(out MSG msg, 0, 0, 0) > 0)
            {
                TranslateMessage(in msg);
                DispatchMessageW(in msg);
            }
        }
        catch (Exception ex)
        {
            _log.Error("Ciclo messaggi dell'InputThread interrotto", ex);
        }
        finally
        {
            Shutdown();
        }
    }

    private unsafe bool EnsureWindowClass(nint hInstance)
    {
        if (Volatile.Read(ref s_classRegistered) != 0) return true;

        fixed (char* className = s_windowClassName)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = (nint)(delegate* unmanaged<nint, uint, nuint, nint, nint>)&WndProc,
                hInstance = hInstance,
                lpszClassName = className,
            };
            if (RegisterClassExW(in wc) == 0)
            {
                int err = Marshal.GetLastPInvokeError();
                if (err != ERROR_CLASS_ALREADY_EXISTS)
                {
                    _log.Error($"Impossibile registrare la classe della finestra di input (errore Win32 {err})");
                    return false;
                }
            }
        }
        Volatile.Write(ref s_classRegistered, 1);
        return true;
    }

    /// <summary>Compila subito il percorso della callback, così il primo clic non paga il costo del JIT.</summary>
    private static void PrepareHookPath()
    {
        try
        {
            MethodInfo? onMouse = typeof(WindowsInputSource).GetMethod(nameof(OnMouse), BindingFlags.Instance | BindingFlags.NonPublic);
            if (onMouse is not null) RuntimeHelpers.PrepareMethod(onMouse.MethodHandle);
        }
        catch (Exception) { /* solo un'ottimizzazione */ }
    }

    private void Shutdown()
    {
        try
        {
            if (_hwnd != 0)
            {
                KillTimer(_hwnd, MaintenanceTimerId);
                foreach (int id in _registeredHotkeys) UnregisterHotKey(_hwnd, id);
                _registeredHotkeys.Clear();
                if (_escapeRegistered) { UnregisterHotKey(_hwnd, HkEscape); _escapeRegistered = false; }
                if (_sessionNotification) { WTSUnRegisterSessionNotification(_hwnd); _sessionNotification = false; }
                if (_powerNotification != 0) { UnregisterSuspendResumeNotification(_powerNotification); _powerNotification = 0; }
            }
            nint hook = Interlocked.Exchange(ref _mouseHook, 0);
            if (hook != 0) UnhookWindowsHookEx(hook);
            if (_broadcastHwnd != 0) { DestroyWindow(_broadcastHwnd); _broadcastHwnd = 0; }
            if (_hwnd != 0) { DestroyWindow(_hwnd); _hwnd = 0; }
            _events.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            _log.Error("Chiusura dell'InputThread con errori", ex);
        }
    }

    // ---- WndProc (InputThread) ---------------------------------------------------------------

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        WindowsInputSource? self = s_instance;
        if (self is not null)
        {
            try
            {
                if (self.HandleMessage(hwnd, msg, wParam, out nint result)) return result;
            }
            catch (Exception ex)
            {
                try { self._log.Error($"Errore nella gestione del messaggio 0x{msg:X4} sull'InputThread", ex); } catch { /* mai propagare */ }
            }
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private bool HandleMessage(nint hwnd, uint msg, nuint wParam, out nint result)
    {
        result = 0;
        switch (msg)
        {
            case WM_HOTKEY:
                OnHotkey((int)wParam);
                return true;

            case WM_TIMER:
                if (wParam == MaintenanceTimerId) MaybeReinstallHookWhenIdle();
                return true;

            case WM_APP_APPLY:
                ApplyOnThread();
                return true;

            case WM_APP_STOPKEY:
                UpdateEscapeRegistration();
                return true;

            case WM_APP_REHOOK:
                InstallOrReinstallHook("richiesta esplicita");
                return true;

            case WM_APP_SLOWCALLBACK:
                _log.Warn($"La callback dell'hook del mouse ha impiegato {(long)wParam} ms (limite {SlowCallbackMs} ms)");
                return true;

            case WM_APP_SHUTDOWN:
                PostQuitMessage(0);
                return true;

            case WM_WTSSESSION_CHANGE:
                if (wParam is WTS_SESSION_UNLOCK or WTS_CONSOLE_CONNECT or WTS_SESSION_LOGON or WTS_REMOTE_CONNECT)
                    InstallOrReinstallHook("sblocco della sessione");
                return true;

            case WM_POWERBROADCAST:
                if (wParam is PBT_APMRESUMESUSPEND or PBT_APMRESUMEAUTOMATIC)
                    InstallOrReinstallHook("ripresa dal sospeso");
                result = 1;
                return true;

            case WM_DISPLAYCHANGE:
                if (hwnd == _broadcastHwnd || hwnd == _hwnd)
                    InstallOrReinstallHook("cambio dello schermo");
                return true;

            default:
                return false;
        }
    }

    private void OnHotkey(int id)
    {
        HotkeyAction action;
        switch (id)
        {
            case HkReadAtPointer: action = HotkeyAction.ReadAtPointer; break;
            case HkReadSelection: action = HotkeyAction.ReadSelection; break;
            case HkStop: action = HotkeyAction.Stop; break;
            case HkTogglePause: action = HotkeyAction.TogglePause; break;
            case HkToggleDictation: action = HotkeyAction.ToggleDictation; break;
            case HkEscape: action = HotkeyAction.Stop; break;
            default: return;
        }
        ScreenPoint point = NativePointer.GetPhysicalPosition();
        _events.Writer.TryWrite(new HotkeyEvent(action, point, Environment.TickCount64));
    }

    // ---- hook del mouse (InputThread) -----------------------------------------------------------

    private unsafe void InstallOrReinstallHook(string reason)
    {
        nint old = _mouseHook;
        nint hook = SetWindowsHookExW(WH_MOUSE_LL, &MouseProc, GetModuleHandleW(null), 0);
        if (hook == 0)
        {
            _log.Error($"Installazione dell'hook del mouse non riuscita ({reason}, errore Win32 {Marshal.GetLastPInvokeError()})");
            return;
        }
        // Prima il nuovo, poi via il vecchio: nessun intervallo scoperto.
        Interlocked.Exchange(ref _mouseHook, hook);
        if (old != 0) UnhookWindowsHookEx(old);   // fallisce in modo innocuo se Windows l'aveva già rimosso
        _lastHookInstallMs = Environment.TickCount64;
        if (old == 0) _log.Info($"Hook del mouse installato ({reason})");
        else _log.Info($"Hook del mouse reinstallato ({reason})");
    }

    /// <summary>Ogni 10 minuti, se l'utente è inattivo da qualche secondo e nessun pulsante attivatore è premuto.</summary>
    private unsafe void MaybeReinstallHookWhenIdle()
    {
        if (_mouseHook == 0)
        {
            InstallOrReinstallHook("nuovo tentativo dopo un errore");
            return;
        }
        if (Environment.TickCount64 - _lastHookInstallMs < RehookIntervalMs) return;
        if (_swallowedMask != 0) return;

        var info = new LASTINPUTINFO { cbSize = (uint)sizeof(LASTINPUTINFO) };
        if (GetLastInputInfo(ref info))
        {
            uint idleMs = unchecked((uint)Environment.TickCount - info.dwTime);
            if (idleMs < IdleBeforeRehookMs) return;
        }
        InstallOrReinstallHook("manutenzione periodica");
    }

    [UnmanagedCallersOnly]
    private static unsafe nint MouseProc(int nCode, nuint wParam, nint lParam)
    {
        // Uscita rapida: WM_MOUSEMOVE arriva fino a 1000 volte al secondo.
        if (nCode == HC_ACTION && (uint)wParam != WM_MOUSEMOVE)
        {
            try
            {
                WindowsInputSource? self = s_instance;
                if (self is not null && self.OnMouse((uint)wParam, in *(MSLLHOOKSTRUCT*)lParam))
                    return 1;   // inghiottito: né gli altri hook né la finestra bersaglio lo vedono
            }
            catch
            {
                // Mai far uscire un'eccezione da una callback nativa: si lascia passare l'evento.
            }
        }
        return CallNextHookEx(0, nCode, wParam, lParam);
    }

    /// <summary>Solo filtro e accodamento: nessuna allocazione oltre al record dell'evento, nessun lock, nessun I/O.</summary>
    private bool OnMouse(uint msg, in MSLLHOOKSTRUCT data)
    {
        int button;
        bool down;
        switch (msg)
        {
            case WM_MBUTTONDOWN: button = 0; down = true; break;
            case WM_MBUTTONUP: button = 0; down = false; break;
            case WM_XBUTTONDOWN: button = XButtonIndex(data.mouseData); down = true; break;
            case WM_XBUTTONUP: button = XButtonIndex(data.mouseData); down = false; break;
            default: return false;   // sinistro, destro, rotella: mai toccati
        }
        if (button < 0) return false;

        long started = Stopwatch.GetTimestamp();
        int bit = 1 << button;
        var point = new ScreenPoint(data.pt.X, data.pt.Y);
        long now = Environment.TickCount64;

        if (!down)
        {
            // UP: inghiottito solo se il suo DOWN è stato inghiottito, qualunque cosa sia cambiata nel frattempo.
            if ((_swallowedMask & bit) == 0) return false;
            _swallowedMask &= ~bit;
            bool dictation = (_dictationMask & bit) != 0;
            _dictationMask &= ~bit;
            if (!dictation) _events.Writer.TryWrite(new TriggerButtonEvent(false, point, now));
        }
        else
        {
            if (_paused) return false;
            if (data.dwExtraInfo == InputInjection.Tag) return false;   // generato da noi
            HookConfig cfg = _config;
            if ((data.flags & LLMHF_INJECTED) != 0 && !cfg.AcceptInjected) return false;

            if (button == cfg.ReadButton)
            {
                _swallowedMask |= bit;
                _dictationMask &= ~bit;
                _events.Writer.TryWrite(new TriggerButtonEvent(true, point, now));
            }
            else if (button == cfg.DictationButton)
            {
                _swallowedMask |= bit;
                _dictationMask |= bit;
                _events.Writer.TryWrite(new HotkeyEvent(HotkeyAction.ToggleDictation, point, now));
            }
            else
            {
                return false;
            }
        }

        long elapsedMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (elapsedMs > SlowCallbackMs && _hwnd != 0)
            PostMessageW(_hwnd, WM_APP_SLOWCALLBACK, (nuint)elapsedMs, 0);   // il log avviene fuori dalla callback
        return true;
    }

    // ---- impostazioni e scorciatoie (InputThread) ---------------------------------------------

    private void ApplyOnThread()
    {
        try
        {
            InputSettings? input;
            DictationSettings? dictation;
            lock (_pendingLock)
            {
                input = _pendingInput;
                dictation = _pendingDictation;
            }
            if (input is null || dictation is null) return;

            var problems = new List<string>();

            int readButton = ButtonIndex(input.MouseTrigger);
            int dictationButton = dictation.Enabled ? ButtonIndex(dictation.MouseTrigger) : -1;
            if (dictationButton >= 0 && dictationButton == readButton)
            {
                problems.Add("Il pulsante del mouse per la dettatura coincide con il pulsante di lettura: la dettatura da mouse è disattivata");
                dictationButton = -1;
            }
            _config = new HookConfig(readButton, dictationButton, input.AcceptInjectedEvents);

            foreach (int id in _registeredHotkeys) UnregisterHotKey(_hwnd, id);
            _registeredHotkeys.Clear();

            var taken = new HashSet<(uint Mods, uint Vk)>();
            RegisterHotkey(HkReadAtPointer, input.HotkeyReadAtPointer, "leggi sotto il puntatore", taken, problems);
            RegisterHotkey(HkReadSelection, input.HotkeyReadSelection, "leggi la selezione", taken, problems);
            RegisterHotkey(HkStop, input.HotkeyStop, "ferma la voce", taken, problems);
            RegisterHotkey(HkTogglePause, input.HotkeyTogglePause, "pausa", taken, problems);
            if (dictation.Enabled)
                RegisterHotkey(HkToggleDictation, dictation.Hotkey, "dettatura", taken, problems);

            _escapeEnabledBySettings = input.EscStopsSpeech;
            UpdateEscapeRegistration();

            _lastProblems = problems.ToArray();
            _log.Info($"Input configurato: attivatore {DescribeButton(readButton)}, dettatura da mouse {DescribeButton(dictationButton)}, " +
                      $"eventi iniettati {(input.AcceptInjectedEvents ? "accettati" : "ignorati")}, scorciatoie attive {_registeredHotkeys.Count}, problemi {problems.Count}");
            foreach (string problem in problems) _log.Warn(problem);
        }
        catch (Exception ex)
        {
            _log.Error("Applicazione delle impostazioni di input non riuscita", ex);
            _lastProblems = new[] { "Errore interno nell'applicazione delle impostazioni di input" };
        }
        finally
        {
            _applied.Set();
        }
    }

    private void RegisterHotkey(int id, string? text, string label, HashSet<(uint Mods, uint Vk)> taken, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        if (!HotkeyGesture.TryParse(text, out HotkeyGesture? gesture) || gesture is null ||
            !HotkeyMap.TryGetVirtualKey(gesture.Key, out uint vk, out string canonicalKey))
        {
            problems.Add($"Scorciatoia \"{text.Trim()}\" per \"{label}\" non riconosciuta");
            return;
        }

        string name = text.Trim();   // nei messaggi si ripete la forma scritta dall'assistente
        uint mods = HotkeyMap.ToModifiers(gesture);
        if (!taken.Add((mods, vk)))
        {
            problems.Add($"Scorciatoia {name} assegnata a più azioni: per \"{label}\" viene ignorata");
            return;
        }
        if (gesture.Ctrl && gesture.Alt && !gesture.Win)
            problems.Add($"La scorciatoia {name} coincide con AltGr+{canonicalKey} sulla tastiera italiana e può rubare caratteri");

        if (!RegisterHotKey(_hwnd, id, mods, vk))
        {
            int err = Marshal.GetLastPInvokeError();
            problems.Add(err == ERROR_HOTKEY_ALREADY_REGISTERED
                ? $"Scorciatoia {name} già in uso da un altro programma"
                : $"Impossibile registrare la scorciatoia {name} (errore Win32 {err})");
            return;
        }
        _registeredHotkeys.Add(id);
    }

    /// <summary>Esc è registrato solo mentre la voce parla e solo se le impostazioni lo prevedono.</summary>
    private void UpdateEscapeRegistration()
    {
        bool wanted = _stopKeyRequested && _escapeEnabledBySettings && _hwnd != 0;
        if (wanted == _escapeRegistered) return;

        if (wanted)
        {
            if (RegisterHotKey(_hwnd, HkEscape, MOD_NOREPEAT, VK_ESCAPE))
                _escapeRegistered = true;
            else
                _log.Warn($"Impossibile registrare Esc come \"ferma la voce\" (errore Win32 {Marshal.GetLastPInvokeError()})");
        }
        else
        {
            UnregisterHotKey(_hwnd, HkEscape);
            _escapeRegistered = false;
        }
    }

    // ---- utilità -------------------------------------------------------------------------------

    /// <summary>HIWORD(mouseData) di WM_XBUTTON*: 1 = XBUTTON1, 2 = XBUTTON2; altri valori non sono pulsanti nostri.</summary>
    private static int XButtonIndex(uint mouseData) => (mouseData >> 16) switch
    {
        1 => 1,
        2 => 2,
        _ => -1,
    };

    private static int ButtonIndex(TriggerButton button) => button switch
    {
        TriggerButton.Middle => 0,
        TriggerButton.X1 => 1,
        TriggerButton.X2 => 2,
        _ => -1,
    };

    private static string DescribeButton(int index) => index switch
    {
        0 => "rotellina",
        1 => "pulsante laterale 1",
        2 => "pulsante laterale 2",
        _ => "nessuno",
    };

    private void LogDpiAwareness()
    {
        try
        {
            bool perMonitorV2 = AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(), DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            if (!perMonitorV2)
                _log.Warn("Il processo non è Per-Monitor DPI Aware V2: le coordinate possono risultare sbagliate sugli schermi con scala diversa dal 100%");
        }
        catch (Exception) { /* diagnostica facoltativa */ }
    }

    /// <summary>Snapshot immutabile letto dalla callback dell'hook senza lock.</summary>
    private sealed record HookConfig(int ReadButton, int DictationButton, bool AcceptInjected)
    {
        public static readonly HookConfig Disabled = new(-1, -1, false);
    }
}
