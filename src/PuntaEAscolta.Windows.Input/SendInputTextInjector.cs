using PuntaEAscolta.Core.Abstractions;
using PuntaEAscolta.Windows.Input.Interop;
using static PuntaEAscolta.Windows.Input.Interop.NativeMethods;

namespace PuntaEAscolta.Windows.Input;

/// <summary>
/// Inserisce testo nell'app in primo piano con SendInput e KEYEVENTF_UNICODE (un evento per unità UTF-16,
/// indipendente dal layout di tastiera), senza toccare gli appunti. Ogni evento porta la firma
/// <see cref="InputInjection.Tag"/> in dwExtraInfo, così l'hook del mouse e chi ascolta la tastiera
/// riconoscono gli eventi nostri. Non attiva finestre: il testo va dove sta il focus.
/// </summary>
public sealed class SendInputTextInjector : ITextInjector
{
    /// <summary>Eventi per ogni chiamata a SendInput (25 caratteri: pressione + rilascio).</summary>
    private const int BatchSize = 50;
    private const int BatchPauseMs = 5;

    /// <summary>Attesa del rilascio dei modificatori prima di inviare tasti virtuali (rilascio lento dei tasti).</summary>
    private const int VirtualKeyModifierTimeoutMs = 3000;

    private readonly ILog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SendInputTextInjector(ILog log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public Task TypeTextAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(text)) return Task.CompletedTask;
        return RunAsync(() => TypeText(text, ct), ct);
    }

    public Task PressEnterAsync(CancellationToken ct) =>
        RunAsync(() => PressKey(VK_RETURN, 1, ct), ct);

    public Task PressBackspaceAsync(int count, CancellationToken ct)
    {
        if (count <= 0) return Task.CompletedTask;
        return RunAsync(() => PressKey(VK_BACK, count, ct), ct);
    }

    // ---- interno ---------------------------------------------------------------------------

    private async Task RunAsync(Action work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Task.Run(work, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void TypeText(string text, CancellationToken ct)
    {
        // I caratteri Unicode arrivano uguali anche con un modificatore premuto; Invio e Tab (tasti virtuali) no:
        // Ctrl+Invio in Word inserisce un'interruzione di pagina.
        bool hasVirtualKeys = text.AsSpan().IndexOfAny('\n', '\r', '\t') >= 0;
        WaitModifiers(ct, mustBeReleased: hasVirtualKeys);

        var batch = new INPUT[BatchSize];
        int count = 0;
        int typed = 0;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (c == '\r')
            {
                // CR LF vale come un solo "a capo"; un CR isolato vale anch'esso come a capo.
                if (i + 1 < text.Length && text[i + 1] == '\n') continue;
                c = '\n';
            }

            if (c == '\n')
            {
                batch[count++] = InputInjection.KeyDown(VK_RETURN);
                batch[count++] = InputInjection.KeyUp(VK_RETURN);
            }
            else if (c == '\t')
            {
                batch[count++] = InputInjection.KeyDown(VK_TAB);
                batch[count++] = InputInjection.KeyUp(VK_TAB);
            }
            else if (c < (char)0x20 || c == (char)0x7F)
            {
                continue;   // altri caratteri di controllo: mai inviati
            }
            else
            {
                batch[count++] = InputInjection.UnicodeDown(c);
                batch[count++] = InputInjection.UnicodeUp(c);
            }
            typed++;

            if (count >= BatchSize)
            {
                Flush(batch, ref count, ct, pauseAfter: i + 1 < text.Length);
            }
        }
        Flush(batch, ref count, ct, pauseAfter: false);

        if (_log.IsDebugEnabled) _log.Debug($"Inseriti {typed} caratteri con SendInput");
    }

    private void PressKey(ushort vk, int times, CancellationToken ct)
    {
        WaitModifiers(ct, mustBeReleased: true);

        var batch = new INPUT[BatchSize];
        int count = 0;
        for (int i = 0; i < times; i++)
        {
            batch[count++] = InputInjection.KeyDown(vk);
            batch[count++] = InputInjection.KeyUp(vk);
            if (count >= BatchSize) Flush(batch, ref count, ct, pauseAfter: i + 1 < times);
        }
        Flush(batch, ref count, ct, pauseAfter: false);
    }

    /// <summary>
    /// Subito dopo una scorciatoia i modificatori sono ancora premuti e cambierebbero il significato dei tasti.
    /// Con <paramref name="mustBeReleased"/> (Invio, Tab, Backspace) si aspetta di più e, se restano premuti, si rinuncia:
    /// il servizio di dettatura lo dice a voce ("Inserimento non riuscito").
    /// </summary>
    private void WaitModifiers(CancellationToken ct, bool mustBeReleased)
    {
        int timeout = mustBeReleased ? VirtualKeyModifierTimeoutMs : InputInjection.ModifierReleaseTimeoutMs;
        if (InputInjection.WaitForModifiersRelease(timeout, ct)) return;
        if (mustBeReleased)
        {
            _log.Warn("Modificatori ancora premuti dopo l'attesa: tasti non inviati");
            throw new InvalidOperationException("Tasti Ctrl/Alt/Maiusc/Win ancora premuti: testo non inserito");
        }
        _log.Warn("Modificatori ancora premuti dopo l'attesa: il testo viene inserito comunque");
    }

    private void Flush(INPUT[] batch, ref int count, CancellationToken ct, bool pauseAfter)
    {
        if (count == 0) return;
        ct.ThrowIfCancellationRequested();

        int sent = InputInjection.Send(batch.AsSpan(0, count), out int lastError);
        if (sent < count)
        {
            _log.Warn($"SendInput ha accettato {sent} eventi su {count} (errore Win32 {lastError}): finestra elevata o input bloccato?");
            count = 0;
            throw new InvalidOperationException("Inserimento del testo rifiutato dal sistema (la finestra di destinazione potrebbe avere privilegi più alti)");
        }
        count = 0;

        if (pauseAfter)
        {
            // Breve pausa per non saturare la coda di input dell'app di destinazione.
            if (ct.WaitHandle.WaitOne(BatchPauseMs)) ct.ThrowIfCancellationRequested();
        }
    }
}
