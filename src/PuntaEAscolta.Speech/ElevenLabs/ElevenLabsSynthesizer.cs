using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PuntaEAscolta.Core.Abstractions;
using PuntaEAscolta.Core.Settings;
using PuntaEAscolta.Speech.Cache;

namespace PuntaEAscolta.Speech.ElevenLabs;

/// <summary>
/// Sintetizzatore ElevenLabs: POST /v1/text-to-speech/{voice_id}/stream?output_format=pcm_44100 con intestazione xi-api-key.
/// Restituisce lo stream PCM appena arrivano le intestazioni della risposta (HttpCompletionOption.ResponseHeadersRead).
/// La chiave API viene decifrata a ogni chiamata dalle impostazioni e non finisce mai nei log.
/// Se il piano dell'account rifiuta pcm_44100 (riservato al piano Pro) riprova una volta con pcm_24000 e se lo ricorda
/// finché le impostazioni non cambiano.
/// </summary>
public sealed class ElevenLabsSynthesizer : ISpeechSynthesizer, IDisposable
{
    public const string ProviderId = "elevenlabs";
    public const string BaseUrl = "https://api.elevenlabs.io";

    private readonly HttpClient _http;
    private readonly ISettingsStore _settings;
    private readonly ISecretProtector _protector;
    private readonly ILog _log;
    private readonly object _sync = new();
    private string? _forcedOutputFormat;
    private bool _disposed;

    public ElevenLabsSynthesizer(HttpClient http, ISettingsStore settings, ISecretProtector protector, ILog log)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _log = log ?? NullLog.Instance;
        _settings.Changed += OnSettingsChanged;
    }

    public string Id => ProviderId;

    /// <summary>Vero se chiave API (decifrabile) e voce sono impostate.</summary>
    public bool IsConfigured =>
        TryGetApiKey() is not null && !string.IsNullOrWhiteSpace(_settings.Current.Speech.ElevenLabsVoiceId);

    /// <summary>Caratteri addebitati dall'ultima risposta (intestazione character-cost), se presente.</summary>
    public int? LastCharacterCost { get; private set; }

    /// <summary>Formato configurato nelle impostazioni, normalizzato a un PCM conosciuto.</summary>
    public string ConfiguredOutputFormat => ElevenLabsPlan.NormalizeOutputFormat(_settings.Current.Speech.ElevenLabsOutputFormat);

    /// <summary>Formato che verrà chiesto alla prossima richiesta: quello configurato, oppure pcm_24000 se il piano ha rifiutato l'altro.</summary>
    public string EffectiveOutputFormat
    {
        get { lock (_sync) return _forcedOutputFormat ?? ConfiguredOutputFormat; }
    }

    /// <summary>
    /// Chiave di cache per la richiesta: usa il formato CONFIGURATO (non quello effettivo), così l'eventuale ripiego a
    /// pcm_24000 resta trasparente e la stessa etichetta non viene mai scaricata due volte; la frequenza reale sta nell'intestazione del file.
    /// </summary>
    public SpeechCacheKey DescribeForCache(SpeechRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var plan = ElevenLabsPlan.Create(_settings.Current.Speech, request, ConfiguredOutputFormat);
        return new SpeechCacheKey(ProviderId, plan.VoiceId, plan.ModelId, plan.LanguageCode, plan.OutputFormat,
            plan.Settings.ToCacheString(), request.Text);
    }

    public async Task<SpeechAudio> SynthesizeAsync(SpeechRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        string? apiKey = TryGetApiKey();
        if (apiKey is null)
        {
            throw new SpeechProviderException(SpeechProviderReason.NotConfigured, "ElevenLabs: chiave API non impostata o non decifrabile su questo PC.");
        }
        var speech = _settings.Current.Speech;
        if (string.IsNullOrWhiteSpace(speech.ElevenLabsVoiceId))
        {
            throw new SpeechProviderException(SpeechProviderReason.NotConfigured, "ElevenLabs: nessuna voce scelta nelle impostazioni.");
        }
        if (string.IsNullOrWhiteSpace(request.Text))
        {
            throw new SpeechProviderException(SpeechProviderReason.Configuration, "ElevenLabs: testo vuoto.");
        }

        var plan = ElevenLabsPlan.Create(speech, request, EffectiveOutputFormat);
        try
        {
            return await SendAsync(plan, apiKey, request, ct).ConfigureAwait(false);
        }
        catch (OutputFormatRejectedException ex) when (plan.OutputFormat != ElevenLabsPlan.FallbackOutputFormat)
        {
            _log.Warn($"ElevenLabs: formato {plan.OutputFormat} rifiutato dal piano dell'account (HTTP {ex.Error.Status} {ex.Error.Code}): riprovo con {ElevenLabsPlan.FallbackOutputFormat} e lo ricordo.");
            lock (_sync) _forcedOutputFormat = ElevenLabsPlan.FallbackOutputFormat;
            var retryPlan = plan with { OutputFormat = ElevenLabsPlan.FallbackOutputFormat };
            try
            {
                return await SendAsync(retryPlan, apiKey, request, ct).ConfigureAwait(false);
            }
            catch (OutputFormatRejectedException ex2)
            {
                throw ElevenLabsErrors.ToException(ex2.Error);
            }
        }
        catch (OutputFormatRejectedException ex)
        {
            throw ElevenLabsErrors.ToException(ex.Error);
        }
    }

    private async Task<SpeechAudio> SendAsync(ElevenLabsPlan plan, string apiKey, SpeechRequest request, CancellationToken ct)
    {
        string url = $"{BaseUrl}/v1/text-to-speech/{Uri.EscapeDataString(plan.VoiceId)}/stream?output_format={plan.OutputFormat}";
        var body = new TtsRequestBody
        {
            Text = request.Text,
            ModelId = plan.ModelId,
            LanguageCode = plan.LanguageCode,
            VoiceSettings = VoiceSettingsBody.From(plan.Settings),
            ApplyTextNormalization = "auto"
        };

        if (_log.IsDebugEnabled)
        {
            _log.Debug($"ElevenLabs: richiesta {request.Kind} modello={plan.ModelId} lingua={plan.LanguageCode ?? "-"} formato={plan.OutputFormat} caratteri={request.Text.Length} testo=\"{request.Text}\"");
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
        httpRequest.Headers.TryAddWithoutValidation("xi-api-key", apiKey);
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/*"));
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        httpRequest.Content = new StringContent(JsonSerializer.Serialize(body, ElevenLabsJson.Options), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not SpeechProviderException)
        {
            var mapped = ElevenLabsErrors.FromTransport(ex);
            _log.Warn(mapped.Message);
            throw mapped;
        }

        if (!response.IsSuccessStatusCode)
        {
            ElevenLabsError error;
            try
            {
                error = ElevenLabsErrors.Redact(await ElevenLabsErrors.ReadAsync(response, ct).ConfigureAwait(false), apiKey);
            }
            finally
            {
                response.Dispose();
            }
            if (ElevenLabsErrors.IsOutputFormatRejection(error)) throw new OutputFormatRejectedException(error);
            var exception = ElevenLabsErrors.ToException(error);
            _log.Warn(exception.Message);
            throw exception;
        }

        RecordCharacterCost(response);

        Stream content;
        try
        {
            content = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            response.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            response.Dispose();
            var mapped = ElevenLabsErrors.FromTransport(ex);
            _log.Warn(mapped.Message);
            throw mapped;
        }

        return new SpeechAudio(new ResponseOwningStream(content, response), new PcmFormat(plan.SampleRate));
    }

    private void RecordCharacterCost(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("character-cost", out var values)
            && int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int cost))
        {
            LastCharacterCost = cost;
            if (_log.IsDebugEnabled) _log.Debug($"ElevenLabs: caratteri addebitati {cost}.");
        }
    }

    private string? TryGetApiKey()
    {
        string protectedKey = _settings.Current.Speech.ElevenLabsProtectedApiKey;
        if (string.IsNullOrWhiteSpace(protectedKey)) return null;
        try
        {
            string? plain = _protector.Unprotect(protectedKey);
            return string.IsNullOrWhiteSpace(plain) ? null : plain.Trim();
        }
        catch (Exception ex)
        {
            _log.Warn($"ElevenLabs: chiave API non decifrabile ({ex.GetType().Name}).");
            return null;
        }
    }

    private void OnSettingsChanged(AppSettings _)
    {
        lock (_sync)
        {
            if (_forcedOutputFormat is not null) _log.Info("ElevenLabs: impostazioni cambiate, torno al formato configurato.");
            _forcedOutputFormat = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _settings.Changed -= OnSettingsChanged;
    }

    /// <summary>Segnale interno: il server ha rifiutato output_format; si può riprovare con il formato di ripiego.</summary>
    private sealed class OutputFormatRejectedException(ElevenLabsError error) : Exception(error.Message)
    {
        public ElevenLabsError Error { get; } = error;
    }

    /// <summary>Stream di rete che, alla chiusura, rilascia anche la HttpResponseMessage (e quindi la connessione).</summary>
    private sealed class ResponseOwningStream(Stream inner, HttpResponseMessage response) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { inner.Dispose(); } catch (Exception) { /* connessione già chiusa */ }
                try { response.Dispose(); } catch (Exception) { /* idem */ }
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            try { await inner.DisposeAsync().ConfigureAwait(false); } catch (Exception) { /* connessione già chiusa */ }
            try { response.Dispose(); } catch (Exception) { /* idem */ }
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
