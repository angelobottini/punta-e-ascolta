using System.Runtime.InteropServices;
using PuntaEAscolta.Core.Abstractions;
using PuntaEAscolta.Windows.Input.Interop;
using static PuntaEAscolta.Windows.Input.Interop.NativeMethods;

namespace PuntaEAscolta.Windows.Input;

/// <summary>
/// Ripiego per "leggi la selezione": invia Ctrl+C (firmato) all'app in primo piano, aspetta che gli appunti
/// cambino, legge il testo e rimette gli appunti com'erano, formato per formato. Rinuncia (null) e non tocca nulla se
/// gli appunti contengono dati che non sa copiare fedelmente (immagini, file, celle di Excel, oggetti di Word): l'utente
/// lavora con Photoshop e Affinity, e l'assistente non deve perdere quello che aveva copiato.
/// Ogni operazione gira su un thread STA dedicato e mai due contemporaneamente: la successiva aspetta anche il
/// ripristino in ritardo della precedente.
/// </summary>
public sealed class ClipboardSelectionReader : IClipboardSelectionReader
{
    internal const int OpenRetries = 12;
    internal const int OpenRetryDelayMs = 15;
    internal const int CopyTimeoutMs = 300;
    private const int PollIntervalMs = 10;

    /// <summary>
    /// Caso peggiore dal momento della richiesta alla lettura del testo copiato: due aperture degli appunti con tutti i
    /// tentativi, attesa del rilascio dei modificatori e attesa della copia. Deve restare, con un margine, sotto il tempo
    /// massimo della fase "appunti" del risolutore (TextResolver.DefaultClipboardTimeoutMs, 4500 ms): lo verifica
    /// ClipboardTimeoutBudgetTests.
    /// </summary>
    internal const int WorstCaseBeforeCopyMs = 2 * OpenRetries * OpenRetryDelayMs + InputInjection.ModifierReleaseTimeoutMs + CopyTimeoutMs;

    /// <summary>Dopo il Ctrl+C, per quanto si aspetta ancora la copia dell'app per poter ripristinare gli appunti.</summary>
    private const int LateCopyWindowMs = 1500;
    private const int LateSettleMs = 30;
    private const int LateOpenRetries = 40;
    private const int LateOpenRetryDelayMs = 25;

    /// <summary>Oltre questa dimensione (testo formattato enorme) non si copia il contenuto: si rinuncia senza toccare nulla.</summary>
    private const long MaxSnapshotBytes = 32L * 1024 * 1024;

    private readonly ILog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ClipboardSelectionReader(ILog log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<string?> TryCopySelectionAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            // La porta si libera sul thread STA, dopo l'eventuale ripristino in ritardo: il chiamante riceve il risultato
            // (o l'annullamento) subito, ma la lettura successiva non parte finché gli appunti non sono tornati a posto.
            var thread = new Thread(() =>
            {
                try
                {
                    CopySelection(ct, result);
                }
                catch (OperationCanceledException)
                {
                    result.TrySetCanceled(ct);
                }
                catch (Exception ex)
                {
                    _log.Error("Lettura della selezione tramite appunti non riuscita", ex);
                }
                finally
                {
                    result.TrySetResult(null);
                    _gate.Release();
                }
            })
            {
                IsBackground = true,
                Name = "ClipboardSelectionReader",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        catch
        {
            _gate.Release();
            throw;
        }

        using (ct.Register(static state =>
               {
                   var (tcs, token) = ((TaskCompletionSource<string?>, CancellationToken))state!;
                   tcs.TrySetCanceled(token);
               }, (result, ct)))
        {
            return await result.Task.ConfigureAwait(false);
        }
    }

    // ---- interno (thread STA) ---------------------------------------------------------------

    private void CopySelection(CancellationToken ct, TaskCompletionSource<string?> result)
    {
        ct.ThrowIfCancellationRequested();

        // 1. Fotografia degli appunti: vuoti, oppure solo formati testuali che si sanno rimettere tali e quali.
        if (!TryOpenClipboard(OpenRetries, OpenRetryDelayMs, ct))
        {
            _log.Warn("Appunti occupati da un'altra applicazione: lettura della selezione saltata");
            return;
        }
        ClipboardSnapshot? snapshot;
        uint sequenceBefore;
        try
        {
            snapshot = TakeSnapshot();
            // Letto ad appunti ancora aperti: include l'eventuale rendering ritardato fatto durante la fotografia.
            sequenceBefore = GetClipboardSequenceNumber();
        }
        finally
        {
            CloseClipboard();
        }
        if (snapshot is null) return;

        // 2. Ctrl+C firmato, solo a modificatori rilasciati: con Maiusc o Alt ancora premuti l'app riceverebbe un altro
        //    comando (Ctrl+Maiusc+C, Ctrl+Alt+C) e potrebbe modificare il documento. Si aspetta fino a 3 s.
        if (!InputInjection.WaitForModifiersRelease(InputInjection.ModifierReleaseTimeoutMs, ct))
        {
            _log.Info($"Modificatori ancora premuti dopo {InputInjection.ModifierReleaseTimeoutMs} ms: Ctrl+C non inviato (rilasciare prima i tasti della scorciatoia)");
            return;
        }
        ct.ThrowIfCancellationRequested();
        if (GetClipboardSequenceNumber() != sequenceBefore)
        {
            _log.Debug("Appunti cambiati durante l'attesa dei modificatori: lettura della selezione saltata");
            return;
        }

        Span<INPUT> ctrlC =
        [
            InputInjection.KeyDown(VK_CONTROL),
            InputInjection.KeyDown(VK_C),
            InputInjection.KeyUp(VK_C),
            InputInjection.KeyUp(VK_CONTROL),
        ];
        int sent = InputInjection.Send(ctrlC, out int lastError);
        long sentAt = Environment.TickCount64;
        if (sent < ctrlC.Length)
        {
            _log.Warn($"Ctrl+C simulato rifiutato ({sent}/{ctrlC.Length} eventi, errore Win32 {lastError})");
            // Se una parte degli eventi è passata l'app potrebbe copiare lo stesso: si sorveglia e si ripristina.
            if (sent > 0) LateRestore(sequenceBefore, sentAt, snapshot);
            return;
        }

        // Da qui in poi gli appunti vanno SEMPRE rimessi a posto, anche se il chiamante annulla o il tempo scade:
        // l'app può scrivere la copia dopo (Excel con molte celle, Word con una selezione lunga).
        bool restored = false;
        try
        {
            // 3. Attesa che l'app scriva negli appunti (numero di sequenza), al massimo 300 ms.
            bool changed = false;
            while (Environment.TickCount64 - sentAt < CopyTimeoutMs)
            {
                if (GetClipboardSequenceNumber() != sequenceBefore) { changed = true; break; }
                if (ct.WaitHandle.WaitOne(PollIntervalMs)) break;
            }
            if (!changed)
            {
                if (ct.IsCancellationRequested) result.TrySetCanceled(ct);
                else result.TrySetResult(null);
                if (_log.IsDebugEnabled) _log.Debug("Nessuna selezione copiata entro il tempo limite: si sorveglia ancora per ripristinare gli appunti");
                return;
            }

            // 4. Lettura del testo copiato e ripristino del contenuto precedente.
            if (!TryOpenClipboard(OpenRetries, OpenRetryDelayMs, CancellationToken.None))
            {
                result.TrySetResult(null);
                _log.Warn("Appunti occupati dopo il Ctrl+C: nuovo tentativo di ripristino in corso");
                return;
            }
            string? copied = null;
            try
            {
                if (IsClipboardFormatAvailable(CF_UNICODETEXT))
                    copied = ReadUnicodeText();
                restored = Restore(snapshot);
            }
            finally
            {
                CloseClipboard();
            }

            if (string.IsNullOrWhiteSpace(copied))
            {
                result.TrySetResult(null);
                return;
            }
            if (_log.IsDebugEnabled) _log.Debug($"Selezione letta dagli appunti: {copied.Length} caratteri");
            result.TrySetResult(copied);
        }
        finally
        {
            if (!restored) LateRestore(sequenceBefore, sentAt, snapshot);
        }
    }

    /// <summary>
    /// Ripristino dopo un annullamento, un tempo scaduto o appunti occupati: si aspetta (senza il token del chiamante)
    /// che la copia dell'app arrivi entro 1,5 s dal Ctrl+C e poi si rimettono gli appunti com'erano. Se la copia non
    /// arriva, gli appunti non sono mai stati toccati.
    /// </summary>
    private void LateRestore(uint sequenceBefore, long sentAt, ClipboardSnapshot snapshot)
    {
        try
        {
            while (GetClipboardSequenceNumber() == sequenceBefore)
            {
                if (Environment.TickCount64 - sentAt >= LateCopyWindowMs)
                {
                    if (_log.IsDebugEnabled) _log.Debug("Nessuna copia arrivata dopo il Ctrl+C: appunti invariati");
                    return;
                }
                Thread.Sleep(PollIntervalMs);
            }
            Thread.Sleep(LateSettleMs);   // l'app può scrivere più formati in fila

            if (!TryOpenClipboard(LateOpenRetries, LateOpenRetryDelayMs, CancellationToken.None))
            {
                _log.Warn("Appunti ancora occupati: il contenuto precedente degli appunti non è stato ripristinato");
                return;
            }
            try
            {
                if (Restore(snapshot)) _log.Info("Appunti ripristinati dopo una copia arrivata in ritardo");
            }
            finally
            {
                CloseClipboard();
            }
        }
        catch (Exception ex)
        {
            _log.Error("Ripristino degli appunti in ritardo non riuscito", ex);
        }
    }

    private static bool TryOpenClipboard(int retries, int delayMs, CancellationToken ct)
    {
        for (int attempt = 0; attempt < retries; attempt++)
        {
            if (OpenClipboard(0)) return true;
            if (ct.WaitHandle.WaitOne(delayMs)) ct.ThrowIfCancellationRequested();
        }
        return false;
    }

    /// <summary>Contenuto degli appunti copiato byte per byte, formato per formato (vuoto = appunti vuoti).</summary>
    private sealed record ClipboardSnapshot(List<(uint Format, byte[] Data)> Formats);

    /// <summary>
    /// Fotografia degli appunti già aperti. null = non si tocca nulla: formati non testuali, formato non leggibile
    /// (proprietario che non risponde al rendering ritardato), dimensione eccessiva, oppure testo assente.
    /// </summary>
    private ClipboardSnapshot? TakeSnapshot()
    {
        if (CountClipboardFormats() == 0) return new ClipboardSnapshot(new List<(uint, byte[])>());

        var formats = new List<(uint Format, byte[] Data)>();
        bool hasUnicodeText = false;
        long total = 0;
        uint format = EnumClipboardFormats(0);
        while (format != 0)
        {
            string? name = ClipboardFormatPolicy.IsRegistered(format) ? GetFormatName(format) : null;
            if (!ClipboardFormatPolicy.IsPreservableText(format, name))
            {
                if (_log.IsDebugEnabled)
                    _log.Debug($"Appunti con dati non testuali (formato {format}{(name is null ? "" : " \"" + name + "\"")}): lettura della selezione saltata");
                return null;
            }
            if (format == CF_UNICODETEXT) hasUnicodeText = true;
            if (!ClipboardFormatPolicy.IsSynthesized(format))
            {
                byte[]? data = ReadGlobal(format);
                if (data is null)
                {
                    _log.Debug($"Formato {format} degli appunti non leggibile: lettura della selezione saltata");
                    return null;
                }
                total += data.Length;
                if (total > MaxSnapshotBytes)
                {
                    _log.Debug("Appunti troppo grandi per essere copiati e ripristinati: lettura della selezione saltata");
                    return null;
                }
                formats.Add((format, data));
            }
            format = EnumClipboardFormats(format);
        }
        int error = Marshal.GetLastPInvokeError();
        if (error != 0)
        {
            _log.Debug($"Elenco dei formati degli appunti interrotto (errore Win32 {error}): lettura della selezione saltata");
            return null;
        }
        if (!hasUnicodeText)
        {
            _log.Debug("Appunti senza testo Unicode leggibile: lettura della selezione saltata");
            return null;
        }
        return new ClipboardSnapshot(formats);
    }

    private static unsafe string? GetFormatName(uint format)
    {
        char* buffer = stackalloc char[256];
        int length = GetClipboardFormatNameW(format, buffer, 256);
        return length > 0 ? new string(buffer, 0, length) : null;
    }

    /// <summary>Copia del blocco di memoria globale di un formato (appunti già aperti). null se il dato non è leggibile.</summary>
    private static unsafe byte[]? ReadGlobal(uint format)
    {
        nint handle = GetClipboardData(format);
        if (handle == 0) return null;
        nuint size = GlobalSize(handle);
        if (size > MaxSnapshotBytes) return null;
        nint ptr = GlobalLock(handle);
        if (ptr == 0) return null;
        try
        {
            var data = new byte[(int)size];
            new ReadOnlySpan<byte>((void*)ptr, data.Length).CopyTo(data);
            return data;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    /// <summary>Legge CF_UNICODETEXT dagli appunti già aperti. Null se il dato manca o non è leggibile.</summary>
    private static unsafe string? ReadUnicodeText()
    {
        nint handle = GetClipboardData(CF_UNICODETEXT);
        if (handle == 0) return null;
        nint ptr = GlobalLock(handle);
        if (ptr == 0) return null;
        try
        {
            nuint bytes = GlobalSize(handle);
            int maxChars = (int)Math.Min(bytes / 2, int.MaxValue / 2);
            if (maxChars <= 0) return string.Empty;
            // Il blocco termina con uno zero, ma per sicurezza si limita la lettura alla dimensione del blocco.
            var span = new ReadOnlySpan<char>((void*)ptr, maxChars);
            int end = span.IndexOf((char)0);
            return new string(end >= 0 ? span[..end] : span);
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    /// <summary>
    /// Riporta gli appunti (già aperti) allo stato fotografato: li svuota e riscrive ogni formato salvato.
    /// Restituisce false solo se non è stato possibile svuotarli (in quel caso nulla è cambiato).
    /// </summary>
    private unsafe bool Restore(ClipboardSnapshot snapshot)
    {
        if (!EmptyClipboard())
        {
            _log.Warn($"Impossibile svuotare gli appunti per il ripristino (errore Win32 {Marshal.GetLastPInvokeError()})");
            return false;
        }

        foreach (var (format, data) in snapshot.Formats)
        {
            nint hMem = GlobalAlloc(GMEM_MOVEABLE, (nuint)Math.Max(1, data.Length));
            if (hMem == 0)
            {
                _log.Warn($"Memoria globale non disponibile: formato {format} degli appunti non ripristinato");
                continue;
            }
            nint ptr = GlobalLock(hMem);
            if (ptr == 0)
            {
                GlobalFree(hMem);
                _log.Warn($"GlobalLock non riuscito: formato {format} degli appunti non ripristinato");
                continue;
            }
            try
            {
                data.AsSpan().CopyTo(new Span<byte>((void*)ptr, data.Length));
            }
            finally
            {
                GlobalUnlock(hMem);
            }

            if (SetClipboardData(format, hMem) == 0)
            {
                int err = Marshal.GetLastPInvokeError();
                GlobalFree(hMem);   // in caso di errore la memoria resta nostra
                _log.Warn($"SetClipboardData non riuscito per il formato {format} (errore Win32 {err})");
            }
            // In caso di successo la memoria appartiene al sistema: non va liberata.
        }
        return true;
    }
}

/// <summary>
/// Quali formati degli appunti si sanno copiare e rimettere tali e quali (blocchi di memoria globale con testo o piccoli
/// marcatori). Con qualunque altro formato presente (immagini, file, metafile, celle di Excel, oggetti OLE di Office,
/// formati propri delle app) il ripiego sugli appunti rinuncia senza toccare nulla.
/// </summary>
internal static class ClipboardFormatPolicy
{
    /// <summary>Formati registrati accettati (nomi esatti): testo formattato dei browser e di Office, marcatori della cronologia.</summary>
    private static readonly HashSet<string> s_registered = new(StringComparer.Ordinal)
    {
        "HTML Format",
        "Rich Text Format",
        "CanIncludeInClipboardHistory",
        "CanUploadToCloudClipboard",
        "ExcludeClipboardContentFromMonitorProcessing",
        "Clipboard Viewer Ignore",
        "Chromium internal source URL",
        "Chromium internal source RFH token",
        "text/_moz_htmlcontext",
        "text/_moz_htmlinfo",
        "text/x-moz-url-priv",
    };

    /// <summary>I formati registrati hanno identificatori da 0xC000 a 0xFFFF.</summary>
    internal static bool IsRegistered(uint format) => format is >= 0xC000 and <= 0xFFFF;

    internal static bool IsPreservableText(uint format, string? registeredName) => format switch
    {
        NativeMethods.CF_UNICODETEXT or NativeMethods.CF_TEXT or NativeMethods.CF_OEMTEXT or NativeMethods.CF_LOCALE => true,
        _ => IsRegistered(format) && registeredName is not null && s_registered.Contains(registeredName),
    };

    /// <summary>Testo ANSI e OEM: Windows li ricava da CF_UNICODETEXT, non si salvano.</summary>
    internal static bool IsSynthesized(uint format) => format is NativeMethods.CF_TEXT or NativeMethods.CF_OEMTEXT;
}
