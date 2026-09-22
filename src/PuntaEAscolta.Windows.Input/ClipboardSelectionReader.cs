using System.Runtime.InteropServices;
using PuntaEAscolta.Core.Abstractions;
using PuntaEAscolta.Windows.Input.Interop;
using static PuntaEAscolta.Windows.Input.Interop.NativeMethods;

namespace PuntaEAscolta.Windows.Input;

/// <summary>
/// Ripiego per "leggi la selezione": invia Ctrl+C (firmato) all'app in primo piano, aspetta che gli appunti
/// cambino, legge il testo e ripristina il testo precedente. Rinuncia (null) e non tocca nulla se gli appunti
/// contengono dati non testuali (immagini, file): l'utente lavora con Photoshop e Affinity.
/// Ogni operazione gira su un thread STA dedicato e mai due contemporaneamente.
/// </summary>
public sealed class ClipboardSelectionReader : IClipboardSelectionReader
{
    private const int OpenRetries = 12;
    private const int OpenRetryDelayMs = 15;
    private const int CopyTimeoutMs = 300;
    private const int PollIntervalMs = 10;

    private readonly ILog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ClipboardSelectionReader(ILog log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<string?> TryCopySelectionAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    tcs.TrySetResult(CopySelection(ct));
                }
                catch (OperationCanceledException)
                {
                    tcs.TrySetCanceled(ct);
                }
                catch (Exception ex)
                {
                    _log.Error("Lettura della selezione tramite appunti non riuscita", ex);
                    tcs.TrySetResult(null);
                }
            })
            {
                IsBackground = true,
                Name = "ClipboardSelectionReader",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- interno (thread STA) ---------------------------------------------------------------

    private string? CopySelection(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // 1. Stato attuale degli appunti: vuoti, testo, oppure altro (si rinuncia).
        uint sequenceBefore = GetClipboardSequenceNumber();
        if (!TryOpenClipboard(ct))
        {
            _log.Warn("Appunti occupati da un'altra applicazione: lettura della selezione saltata");
            return null;
        }

        bool previousWasEmpty;
        string? previousText = null;
        try
        {
            int formats = CountClipboardFormats();
            previousWasEmpty = formats == 0;
            if (!previousWasEmpty)
            {
                if (!IsClipboardFormatAvailable(CF_UNICODETEXT))
                {
                    if (_log.IsDebugEnabled) _log.Debug($"Appunti con {formats} formati non testuali: lettura della selezione saltata");
                    return null;
                }
                previousText = ReadUnicodeText();
            }
        }
        finally
        {
            CloseClipboard();
        }

        // 2. Ctrl+C firmato. Prima si aspetta che l'utente rilasci i modificatori della scorciatoia.
        if (!InputInjection.WaitForModifiersRelease(InputInjection.ModifierReleaseTimeoutMs, ct))
            _log.Warn("Modificatori ancora premuti: il Ctrl+C simulato potrebbe non funzionare");

        Span<INPUT> ctrlC =
        [
            InputInjection.KeyDown(VK_CONTROL),
            InputInjection.KeyDown(VK_C),
            InputInjection.KeyUp(VK_C),
            InputInjection.KeyUp(VK_CONTROL),
        ];
        int sent = InputInjection.Send(ctrlC, out int lastError);
        if (sent < ctrlC.Length)
        {
            _log.Warn($"Ctrl+C simulato rifiutato ({sent}/{ctrlC.Length} eventi, errore Win32 {lastError})");
            return null;
        }

        // 3. Attesa che l'app scriva negli appunti (numero di sequenza), al massimo 300 ms.
        long deadline = Environment.TickCount64 + CopyTimeoutMs;
        bool changed = false;
        while (Environment.TickCount64 < deadline)
        {
            if (GetClipboardSequenceNumber() != sequenceBefore) { changed = true; break; }
            if (ct.WaitHandle.WaitOne(PollIntervalMs)) ct.ThrowIfCancellationRequested();
        }
        if (!changed)
        {
            if (_log.IsDebugEnabled) _log.Debug("Nessuna selezione copiata entro il tempo limite: appunti invariati");
            return null;
        }

        // 4. Lettura del testo copiato e ripristino del contenuto precedente.
        string? copied = null;
        if (!TryOpenClipboard(ct))
        {
            _log.Warn("Appunti occupati dopo il Ctrl+C: impossibile leggere e ripristinare");
            return null;
        }
        try
        {
            if (IsClipboardFormatAvailable(CF_UNICODETEXT))
                copied = ReadUnicodeText();
        }
        finally
        {
            try
            {
                Restore(previousWasEmpty, previousText);
            }
            finally
            {
                CloseClipboard();
            }
        }

        if (string.IsNullOrWhiteSpace(copied)) return null;
        if (_log.IsDebugEnabled) _log.Debug($"Selezione letta dagli appunti: {copied.Length} caratteri");
        return copied;
    }

    private bool TryOpenClipboard(CancellationToken ct)
    {
        for (int attempt = 0; attempt < OpenRetries; attempt++)
        {
            if (OpenClipboard(0)) return true;
            if (ct.WaitHandle.WaitOne(OpenRetryDelayMs)) ct.ThrowIfCancellationRequested();
        }
        return false;
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

    /// <summary>Riporta gli appunti (già aperti) allo stato precedente: vuoti oppure con il testo di prima.</summary>
    private unsafe void Restore(bool wasEmpty, string? text)
    {
        if (!EmptyClipboard())
        {
            _log.Warn($"Impossibile svuotare gli appunti per il ripristino (errore Win32 {Marshal.GetLastPInvokeError()})");
            return;
        }
        if (wasEmpty || text is null) return;

        nuint bytes = (nuint)(text.Length + 1) * 2;
        nint hMem = GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, bytes);
        if (hMem == 0)
        {
            _log.Warn("Memoria globale non disponibile: appunti non ripristinati");
            return;
        }
        nint ptr = GlobalLock(hMem);
        if (ptr == 0)
        {
            GlobalFree(hMem);
            _log.Warn("GlobalLock non riuscito: appunti non ripristinati");
            return;
        }
        try
        {
            var dest = new Span<char>((void*)ptr, text.Length + 1);
            text.AsSpan().CopyTo(dest);
            dest[text.Length] = (char)0;
        }
        finally
        {
            GlobalUnlock(hMem);
        }

        if (SetClipboardData(CF_UNICODETEXT, hMem) == 0)
        {
            int err = Marshal.GetLastPInvokeError();
            GlobalFree(hMem);   // in caso di errore la memoria resta nostra
            _log.Warn($"SetClipboardData non riuscito (errore Win32 {err}): appunti non ripristinati");
        }
        // In caso di successo la memoria appartiene al sistema: non va liberata.
    }
}
