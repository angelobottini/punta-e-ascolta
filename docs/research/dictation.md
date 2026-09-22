# Dettatura (speech-to-text) per "Punta e Ascolta"

Ricerca del 21 settembre 2026. Solo documentazione e fonti pubbliche: niente e' stato compilato, installato o provato sulla macchina. Dove un'affermazione non e' verificata su documentazione ufficiale lo dico esplicitamente. Prosa in italiano, codice e identificatori in inglese.

---

## 1. Raccomandazione

### In una frase

Costruire una pipeline di dettatura **nostra**, a pulsante ("push-to-talk" con modalita' *toggle*), basata su **ElevenLabs Scribe v2 in modalita' batch** (`POST /v1/speech-to-text`, `model_id=scribe_v2`), con **rilettura vocale obbligatoria** del testo riconosciuto prima dell'inserimento, e inserimento nel programma attivo tramite `SendInput` + `KEYEVENTF_UNICODE` (con incolla dagli appunti come alternativa per-applicazione). Il motore STT deve stare dietro un'interfaccia (`ISpeechToTextProvider`) perche' **nessuna fonte dimostra che un motore commerciale regga il parlato disartrico in italiano**: va misurato con la voce di Matteo e, se serve, sostituito.

### Perche' non basta Win+H

La digitazione vocale di Windows (Win+H) e' gratuita, supporta l'italiano e funziona in Word ed Excel, ma per *questo* utente ha quattro limiti strutturali:

1. **Nessuna rilettura.** Il testo finisce direttamente nel documento; la nostra app non lo vede mai. Chi non sa leggere non puo' verificare cosa e' stato scritto (potremmo rileggerlo dopo con UI Automation in Word, ma non in Affinity).
2. **Motore non sostituibile.** E' Azure Speech generico; se il parlato di Matteo non viene capito non c'e' nulla da regolare.
3. **Si ferma da sola.** Si interrompe al cambio di focus e dopo pause di silenzio; un parlato lento e con pause lunghe (tipico della disartria) la fa spegnere di continuo.
4. **Richiede un campo di testo riconosciuto dal sistema** e mostra una barra con testo scritto. In Affinity v3 (UI proprietaria, invisibile agli screen reader) e' tutt'altro che garantito che funzioni.

Win+H resta comunque utile come **seconda opzione a costo quasi zero**: 10 righe di codice (`SendInput` di `VK_LWIN`+`H`) dietro un'interfaccia `IExternalDictationLauncher`.

### Cosa spedire in v1 (minimo e robusto)

| # | Funzione | Note |
|---|----------|------|
| 1 | Azione trigger `DictationToggle` configurabile (tasto mouse o tastiera), distinta dal trigger di lettura | Modalita' `Toggle` di default (1 clic = inizia, 1 clic = fine); `Hold` opzionale. Tenere premuto puo' essere difficile con una PCI. |
| 2 | Cattura microfono 16 kHz mono 16 bit dal microfono della cuffia scelto nelle impostazioni | NAudio dietro `IAudioCapture`. Nessun VAD che tronca: decide l'utente quando ha finito. Limite di sicurezza (es. 90 s). |
| 3 | Trascrizione batch con `scribe_v2` | Un solo `POST` multipart, PCM grezzo (`file_format=pcm_s16le_16`), `language_code=it`, `tag_audio_events=false`, `diarize=false`. Nessun WebSocket in v1. |
| 4 | Pulizia testo | Stesso filtro emoji della lettura, rimozione di eventuali tag tra parentesi, tabella "sostituzioni personali". |
| 5 | **Rilettura vocale** ("Ho capito: ...") e poi inserimento | Default proposto: legge, poi inserisce da solo se l'utente non annulla (clic durante la rilettura = annulla). Alternative configurabili: conferma esplicita; inserisci-poi-rileggi. |
| 6 | Inserimento testo | Default `Type` (`SendInput`/`KEYEVENTF_UNICODE`, a blocchi). Profilo per-applicazione che puo' passare a `Paste`. L'incolla non deve mai distruggere un'immagine copiata (vedi 2.5). |
| 7 | Annulla ultima dettatura | Azione trigger `DictationUndo` (gesto del mouse) + comando vocale a enunciato intero "cancella". |
| 8 | Comandi vocali **solo a enunciato intero** | "cancella"/"annulla", "a capo", "rileggi". Tabella di alias modificabile dall'assistente. |
| 9 | Segnali acustici (earcon) | Inizio registrazione, fine registrazione, inserito, annullato, errore. Solo audio, coerente col vincolo "nessun feedback visivo". |
| 10 | `IExternalDictationLauncher` = Win+H | Azione trigger separata `WindowsVoiceTypingToggle`, disattivata di default. |

### Cosa rimandare a v2

- **Streaming in tempo reale** con `scribe_v2_realtime` su WebSocket (`wss://api.elevenlabs.io/v1/speech-to-text/realtime`), commit manuale o VAD, rilettura a segmenti: serve per dettature lunghe (lettere, temi), non per etichette o frasi brevi.
- **Comandi vocali in linea** ("... virgola ... punto a capo ...") dentro una frase.
- **Provider locale Whisper** (`Whisper.net` 1.9.1, supporta Windows ARM64) per uso offline e soprattutto come via per un **modello personalizzato** sulla voce di Matteo se i motori generici non bastano.
- Provider **Azure Speech** (unico grande fornitore con "Custom Speech" per `it-IT`).
- `keyterms` (vocabolario personale lato ElevenLabs, +0,05 $/h), compressione Opus per connessioni lente, integrazione specifica con Word Dictate.
- Banco di prova integrato "registra 20 frasi e confronta i motori".

### Decisione architetturale da prendere subito (anche se la dettatura arriva dopo)

1. Il sistema dei trigger deve produrre **azioni astratte** (`TriggerAction` enum), non "clic centrale = leggi". Aggiungere la dettatura = aggiungere valori all'enum e binding.
2. Deve esistere un **arbitro dell'audio** unico: avviare la registrazione ferma il TTS; la rilettura usa lo stesso `ISpeechOutput` della lettura; il trigger "ferma la voce" durante la rilettura vale come "annulla".
3. `ITextInjector` e `IAudioCapture` vivono nel layer piattaforma; `ISpeechToTextProvider` (HTTP/WebSocket puri) e la macchina a stati `DictationController` vivono nel core portabile.

---

## 2. Dettagli tecnici

### 2.1 Opzioni integrate in Windows e Microsoft 365

#### 2.1.1 Digitazione vocale (Win+H)

Fatti (pagina di supporto Microsoft, consultata il 21/09/2026):

- Richiede **connessione internet**, microfono funzionante e **cursore in una casella di testo**: "Voice typing uses online speech recognition, which is powered by Azure Speech services".
- **Italiano (Italia)** e' nell'elenco delle lingue supportate. La lingua di dettatura segue la **lingua di input** corrente (layout tastiera), senza rilevamento automatico.
- **Punteggiatura automatica**: impostazione nella rotella della barra, **disattivata di default**. Altre impostazioni: "Voice typing launcher", filtro volgarita', "wait time before acting", microfono predefinito.
- La versione italiana della pagina elenca comandi italiani: "Sospendi dettatura", "Termina dettatura", "Termina ascolto", "Elimina elemento", "Cancella elemento", "Seleziona elemento", e i nomi della punteggiatura ("virgola", "punto", "punto interrogativo"...). Non risulta un comando italiano documentato per "nuova riga". **Attenzione**: la pagina inglese contiene anche la frase "Dictation commands are available in US English only" (verosimilmente riferita alla sezione Windows 10): da verificare sulla macchina.
- "Fluid dictation" (correzione on-device con SLM sui Copilot+ PC) e' documentata per **tutte le varianti dell'inglese**; fonti secondarie riportano estensione a francese, tedesco e spagnolo da luglio 2026. **Non italiano.**
- Comportamento osservato da piu' fonti secondarie: si ferma al cambio di finestra, quando si digita sulla tastiera e dopo un periodo di silenzio; un microfono debole viene letto come silenzio.
- In **Excel** non esiste un pulsante "Dettatura" di Office: si usa Win+H nella cella selezionata e poi si conferma con Invio/Tab.
- Per **Affinity/Photoshop** non ho trovato nessuna fonte. Win+H inserisce testo tramite il framework di input di sistema: in campi di testo disegnati dall'applicazione puo' non agganciarsi. Da provare.

Avvio programmatico. Non esiste un'API pubblica; la via e' simulare la scorciatoia. La documentazione di `SendInput` lo prevede esplicitamente: "An accessibility application can use SendInput to inject keystrokes corresponding to application launch shortcut keys that are handled by the shell."

```csharp
// Windows layer. Meglio generare i P/Invoke con Microsoft.Windows.CsWin32; qui a mano per chiarezza.
[StructLayout(LayoutKind.Sequential)]
internal struct INPUT { public uint type; public InputUnion U; }           // sizeof == 40 su x64 e ARM64

[StructLayout(LayoutKind.Explicit)]
internal struct InputUnion
{
    [FieldOffset(0)] public MOUSEINPUT mi;      // DEVE esserci: determina la dimensione della union
    [FieldOffset(0)] public KEYBDINPUT ki;
}

[StructLayout(LayoutKind.Sequential)]
internal struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public nuint dwExtraInfo; }

[StructLayout(LayoutKind.Sequential)]
internal struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public nuint dwExtraInfo; }

internal static class Keys
{
    public const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_UNICODE = 0x0004;
    public const ushort VK_LWIN = 0x5B, VK_RETURN = 0x0D, VK_BACK = 0x08, VK_CONTROL = 0x11;
    public static readonly nuint OurTag = 0x50454131;   // "PEA1": per riconoscere i nostri eventi nei nostri hook

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint cInputs, INPUT[] pInputs, int cbSize);

    public static INPUT Vk(ushort vk, bool down) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = down ? 0 : KEYEVENTF_KEYUP, dwExtraInfo = OurTag } }
    };
}

public sealed class WindowsVoiceTypingLauncher : IExternalDictationLauncher
{
    public bool IsAvailable => true;
    public Task ToggleAsync(CancellationToken ct)
    {
        // MAI chiamare dall'interno della callback dell'hook low-level: accodare su un altro thread.
        var seq = new[] { Keys.Vk(Keys.VK_LWIN, true), Keys.Vk((ushort)'H', true),
                          Keys.Vk((ushort)'H', false), Keys.Vk(Keys.VK_LWIN, false) };
        uint sent = Keys.SendInput((uint)seq.Length, seq, Marshal.SizeOf<INPUT>());
        if (sent != seq.Length) throw new InvalidOperationException("SendInput blocked");
        return Task.CompletedTask;
    }
}
```

Win+H e' un interruttore: un secondo invio chiude la barra. La nostra app **non sa** se la barra e' aperta, se sta ascoltando o cosa ha scritto.

#### 2.1.2 Accesso vocale (Voice Access)

- Le pagine di supporto Microsoft (EN e IT, consultate il 21/09/2026) elencano ora **l'italiano** fra le lingue supportate; "What's new in Voice access": "Voice access now supports Italian". La pagina non riporta data ne' build; l'aggiunta e' del 2026. Requisito: Windows 11 22H2 o successivo.
- Riconoscimento **on-device, funziona senza internet** (serve internet solo per scaricare il modello della lingua).
- E' un sistema completo di controllo del PC, sempre in ascolto, con sovrapposizioni numerate e una barra in alto con testo: pensato per chi legge. "Fluid dictation" non e' disponibile in italiano.
- Non e' integrabile da codice. Va considerato come **alternativa che l'assistente puo' provare a parte**, non come parte dell'app. Nota: la macchina di sviluppo riporta OS 10.0.26220 (ramo Insider/Beta di 25H2), quindi potrebbe avere funzioni non ancora presenti sul PC di Matteo.

#### 2.1.3 Dettatura di Microsoft 365 in Word

- Richiede **abbonamento Microsoft 365** e internet. **Italiano** e' lingua supportata (non "preview"); punteggiatura automatica disponibile; comandi italiani come "nuova riga", "virgola", "punto".
- Scorciatoia documentata: `Alt` + `` ` `` (accento grave). **Sulla tastiera italiana il tasto accento grave non esiste**: la scorciatoia potrebbe non essere raggiungibile. Alternativa piu' solida: invocare il pulsante "Dettatura" della barra multifunzione via UI Automation (`InvokePattern`). Da verificare.
- Non esiste in Excel. Stessi limiti di Win+H per noi: nessuna rilettura, motore non sostituibile.

#### 2.1.4 `Windows.Media.SpeechRecognition` (WinRT)

La grammatica di dettatura predefinita e' un servizio web, "optimized to recognize short phrases", fino a circa 10 secondi di parlato, e richiede il consenso "Online speech recognition". E' tecnologia vecchia: **non la consiglio** come provider, nemmeno di ripiego.

### 2.2 ElevenLabs Speech-to-Text (Scribe)

#### 2.2.1 Modelli (pagina "Models", 21/09/2026)

| `model_id` | Uso | Note |
|------------|-----|------|
| `scribe_v2` | Batch | 90+ lingue, timestamp per parola, diarizzazione fino a 32 parlanti, entity detection, keyterm prompting fino a 1000 termini, `no_verbatim`. Annunciato il 9 gennaio 2026. |
| `scribe_v2_realtime` | Streaming WebSocket | Latenza dichiarata ~150 ms "excluding application & network latency"; PCM 8-48 kHz e u-law; niente diarizzazione; keyterms ridotti (fino a 50, 20 caratteri ciascuno). |
| `scribe_v2_medical` | Batch clinico | Non pertinente. |
| `scribe_v1` | **Deprecato, rimozione annunciata** | Changelog 8 giugno 2026: "The `scribe_v1` model is deprecated and will be removed on July 9, 2026." **[precisato dopo la verifica]** Al 21/09/2026 la pagina Models lo elenca ancora come *deprecated* ("outclassed by v2 models"), non come rimosso; se l'API lo rifiuti davvero non e' verificabile senza chiave. In ogni caso non usarlo. |

Italiano: nella documentazione "Transcription" `ita` e' nella fascia **"Excellent", WER <= 5%** (su parlato tipico; nessun dato su parlato disartrico).

#### 2.2.2 Endpoint batch

`POST https://api.elevenlabs.io/v1/speech-to-text`, header `xi-api-key: <key>`, corpo `multipart/form-data`.

Campi che ci interessano:

| Campo | Valore per noi | Note |
|-------|----------------|------|
| `model_id` | `scribe_v2` | obbligatorio |
| `file` | audio | < 3 GB secondo la pagina "capabilities", "less than 5.0GB" secondo reference e OpenAPI; durata minima 100 ms. Va fornito esattamente uno fra `file` e `source_url` (`cloud_storage_url` e' deprecato). Formati: AAC, AIFF, OGG, MP3, OPUS, WAV, FLAC, M4A, WebM |
| `file_format` | `pcm_s16le_16` | enum `pcm_s16le_16` \| `other`, **default `other`**. PCM 16 bit, 16 kHz, mono, little-endian: "Latency will be lower than with passing an encoded waveform" (testo della OpenAPI). |
| `language_code` | `it` | ISO-639-1 o ISO-639-3 (`ita`). Fissarlo: il rilevamento automatico sbaglia piu' facilmente con parlato atipico. |
| `tag_audio_events` | `false` | **default `true`**: va impostato esplicitamente a `false`, altrimenti nel testo compaiono "(risate)", "(musica)"... |
| `diarize` | `false` | default `false` |
| `timestamps_granularity` | `word` | enum `none` \| `word` \| `character`, default `word`; serve `words[].logprob` per stimare la confidenza; `none` se non la usiamo |
| `temperature` | `0` | intervallo 0-2; se omesso "we will use a temperature based on the model you selected which is usually 0" |
| `no_verbatim` | da provare | default `false`; rimuove esitazioni e false partenze (solo `scribe_v2`) |
| `keyterms` | v2 | fino a 1000 termini, < 50 caratteri, <= 5 parole; **costo extra** (la OpenAPI dice "+20% surcharge" e, oltre 100 termini, durata minima fatturabile di 20 s per richiesta; la pagina prezzi dice +0,05 $/h) |
| query `enable_logging` | non impostare | `false` = Zero Retention Mode, **solo Enterprise** |

Risposta 200 (sincrona): `text`, `language_code`, `language_probability`, `words[]` (`text`, `type` = `word`/`spacing`/`audio_event`, `start`, `end`, `logprob`), `transcription_id`, `audio_duration_secs`.

```csharp
// Core (portabile): solo HttpClient + System.Text.Json.
public sealed class ElevenLabsScribeProvider(HttpClient http, ISecretStore secrets) : ISpeechToTextProvider
{
    public string Id => "elevenlabs-scribe-v2";
    public SttCapabilities Capabilities => SttCapabilities.Batch | SttCapabilities.Keyterms;

    public async Task<SttResult> TranscribeAsync(AudioClip clip, SttOptions o, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent("scribe_v2"), "model_id" },
            { new StringContent(o.LanguageCode ?? "it"), "language_code" },
            { new StringContent("false"), "tag_audio_events" },
            { new StringContent("false"), "diarize" },
            { new StringContent("word"), "timestamps_granularity" },
            { new StringContent("0"), "temperature" },
            { new StringContent("pcm_s16le_16"), "file_format" },
        };
        var audio = new ByteArrayContent(clip.Pcm16kMono);                   // 32 KB per secondo
        audio.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(audio, "file", "utterance.pcm");

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.elevenlabs.io/v1/speech-to-text") { Content = form };
        req.Headers.Add("xi-api-key", secrets.GetElevenLabsKey());            // stessa chiave DPAPI del TTS

        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new SttException((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));

        var dto = await resp.Content.ReadFromJsonAsync<ScribeResponse>(ct) ?? throw new SttException(0, "empty body");
        double? conf = dto.Words?.Where(w => w.Type == "word").Select(w => Math.Exp(w.Logprob)).DefaultIfEmpty().Average();
        return new SttResult(dto.Text.Trim(), dto.LanguageCode, conf, TimeSpan.FromSeconds(dto.AudioDurationSecs ?? 0), Id);
    }
}
```

A mia conoscenza non esiste un SDK .NET ufficiale ElevenLabs (la documentazione mostra esempi Python e JavaScript/TypeScript; non verificato in modo esaustivo): usare `HttpClient` diretto e' comunque la scelta piu' semplice e portabile.

#### 2.2.3 Realtime (v2)

`wss://api.elevenlabs.io/v1/speech-to-text/realtime` (esistono anche host regionali US/EU/India/Singapore). Autenticazione lato desktop: header `xi-api-key` (i token monouso servono solo per client web).

Query: `model_id=scribe_v2_realtime`, `audio_format` (default `pcm_16000`; anche `pcm_8000`...`pcm_48000`, `ulaw_8000`), `language_code`, `commit_strategy` = `manual` | `vad`, `vad_silence_threshold_secs`, `vad_threshold`, `min_speech_duration_ms`, `min_silence_duration_ms`, `include_timestamps`, `include_language_detection`, `keyterms`, `enable_logging`. Presenti oggi nella reference e non citati nella prima stesura: `no_verbatim`, `entity_detection` (aggiunto col changelog del 3 agosto 2026, produce l'evento `committed_transcript_entities`), `secondary_languages`, `filter_background_audio`, e il parametro `token` per i client web.

Client -> server:

```json
{ "message_type": "input_audio_chunk", "audio_base_64": "...", "commit": false, "sample_rate": 16000, "previous_text": "solo nel primo chunk" }
```

Server -> client: `session_started` (`session_id`, `config`), `partial_transcript` (`text`), `committed_transcript` (`text`), `committed_transcript_with_timestamps` (`text`, `language_code`, `words[]` con `logprob`), piu' messaggi d'errore con forma `{ "message_type": "...", "error": "..." }` e tipi: `error`, `auth_error`, `quota_exceeded`, `commit_throttled`, `unaccepted_terms`, `rate_limited`, `queue_overflow`, `resource_exhausted`, `session_time_limit_exceeded`, `input_error`, `invalid_request`, `chunk_size_exceeded`, `insufficient_audio_activity`, `transcriber_error`, `warning`.

Regole dalla guida "Transcripts and commit strategies": con `manual` il servizio fa comunque commit automatico dopo circa **36 s** di audio accumulato; "Committing every 20-30 seconds is good practice"; commit troppo ravvicinati peggiorano il modello (`commit_throttled`). Chunk consigliati 0,1-1 s. Valori VAD di esempio: `vad_silence_threshold_secs=1.5`, `vad_threshold=0.4`, `min_speech_duration_ms=100`, `min_silence_duration_ms=100`. Per un parlato con pause lunghe, il VAD va allargato (2,5-3 s) o evitato (`manual` + commit allo stop).

```csharp
public async IAsyncEnumerable<SttEvent> StreamAsync(IAsyncEnumerable<ReadOnlyMemory<byte>> pcm, SttOptions o,
                                                    [EnumeratorCancellation] CancellationToken ct)
{
    using var ws = new ClientWebSocket();
    ws.Options.SetRequestHeader("xi-api-key", secrets.GetElevenLabsKey());
    var uri = new Uri("wss://api.elevenlabs.io/v1/speech-to-text/realtime" +
                      "?model_id=scribe_v2_realtime&audio_format=pcm_16000&language_code=it&commit_strategy=manual");
    await ws.ConnectAsync(uri, ct);

    var sender = Task.Run(async () =>
    {
        await foreach (var chunk in pcm.WithCancellation(ct))                 // chunk da ~250 ms = 8000 byte
            await SendJsonAsync(ws, new { message_type = "input_audio_chunk",
                                          audio_base_64 = Convert.ToBase64String(chunk.Span),
                                          commit = false, sample_rate = 16000 }, ct);
        // fine registrazione: commit finale (chunk di silenzio breve + commit = true)
        await SendJsonAsync(ws, new { message_type = "input_audio_chunk",
                                      audio_base_64 = Convert.ToBase64String(new byte[3200]),
                                      commit = true, sample_rate = 16000 }, ct);
    }, ct);

    await foreach (var msg in ReceiveJsonAsync(ws, ct))
    {
        switch (msg.GetProperty("message_type").GetString())
        {
            case "partial_transcript":   yield return new SttEvent.Partial(msg.GetProperty("text").GetString()!); break;
            case "committed_transcript": yield return new SttEvent.Committed(msg.GetProperty("text").GetString()!); break;
            case "session_started":      break;
            default:                     yield return new SttEvent.Error(msg.ToString()); break;
        }
    }
    await sender;
}
```

#### 2.2.4 Costi e limiti (pagina `elevenlabs.io/pricing/api`, 21/09/2026)

- `scribe_v2`: **0,22 $/ora**; piano **Pro (99 $/mese): 450 ore incluse**. **[corretto dopo la verifica]** La prima stesura diceva 100 ore: e' il valore della colonna *Creator* (22 $/mese). La riga della pagina e': 4 h 30 min (Free), 27 h (Starter), 100 h (Creator), **450 h (Pro)**, 1.359 h (Scale), 4.500 h (Business), cioe' prezzo del piano diviso 0,22 $/h.
- `scribe_v2_realtime`: **0,39 $/ora**; Pro: **254 ore incluse** (non 56, che e' di nuovo il valore Creator; riga: 2 h 30 min, 15 h, 56 h, **254 h**, 767 h, 2.538 h).
- La stessa pagina dice "API usage is billed in US dollars, not credits". Deduzione mia (non scritta nella pagina): poiche' ogni valore "incluso" coincide con prezzo del piano / tariffa, e' verosimilmente un unico budget di 99 $ da dividere fra TTS e STT, non 450 ore *piu'* 990.000 caratteri. Da controllare in dashboard.
- Extra: keyterm prompting +0,05 $/ora, entity detection +0,07 $/ora.
- Concorrenza Pro: 40 richieste STT batch, 30 sessioni realtime.
- Il post del 7 maggio 2026 ("We've lowered API & Agents pricing and introduced PAYG") introduce un nuovo listino; gli abbonati esistenti passano al nuovo con "Switch to new pricing". **Verificare in dashboard su quale listino e' il piano di Angelo** (nel vecchio schema a crediti le ore incluse si calcolano diversamente).

Uso realistico: anche 30 minuti di parlato al giorno sono ~15 ore/mese, cioe' circa 3,3 $ di consumo su 99 $: ampiamente dentro il piano. Il costo non e' un fattore.

#### 2.2.5 Latenza attesa

ElevenLabs pubblica numeri solo per il realtime (~150 ms, esclusa rete). Per il batch non ci sono numeri ufficiali per clip brevi; un enunciato di 5-10 s in PCM grezzo pesa 160-320 KB, quindi l'upload e' trascurabile e il tempo totale dipende dal servizio. **Da misurare** da casa di Matteo; obiettivo accettabile: < 2 s dalla fine della registrazione all'inizio della rilettura.

### 2.3 Cattura del microfono con NAudio

Stato dei pacchetti (nuget.org e GitHub Releases, 21/09/2026):

- **NAudio 3.1.0** (7 settembre 2026; 3.0.0 del 15 agosto, 3.0.1 del 18 agosto, 3.1.1-preview.1 dell'8 settembre 2026). Richiede **`net9.0` o successivo** (va bene con .NET 10). Assembly spezzato in `NAudio.Core`, `NAudio.Wasapi`, `NAudio.WinMM`, ecc.; core cross-platform e compatibile Native AOT. `WasapiCapture`/`WasapiOut` sono `[Obsolete]` a favore di **`WasapiRecorder`/`WasapiPlayer`** (costruiti con builder); `WaveInEvent` rinominato `WaveIn` (il vecchio nome resta come alias obsoleto); i provider usano `Span<T>`. **Verificato** aprendo il pacchetto e compilando sul Zenbook (vedi "Verifica indipendente").
- **NAudio 2.4.0** (26 agosto 2026): ultima stabile del ramo 2.x, ancora mantenuto (2.3.0 il 12 marzo 2026), API classiche. Con il nostro TFM NuGet usa l'asset `netstandard2.0`. Dalla 2.2: "WasapiCapture and WasapiLoopbackCapture support sample rate conversion so you can capture at a sample rate of your choice" (citazione non riverificata).

Consiglio **[aggiornato dopo la verifica]**: allinearsi alla scelta fatta per la riproduzione (stesso pacchetto). La verifica di `elevenlabs-tts.md` indica come scelta prudente la **2.4.0** (la 3.x ha avuto tre rilasci stabili in 23 giorni, cambi di rottura nella 3.1.0 e una correzione a `WasapiPlayer` - issue 1442 del 10/9/2026 - non ancora pubblicata su nuget.org); la 3.1.0 resta un'alternativa che compila e gira su questa macchina. In entrambi i casi **fissare la versione esatta** e tenere `IAudioCapture` abbastanza sottile da poter cambiare.

```csharp
// Windows layer, NAudio 3.x. Verificato in compilazione contro NAudio.Wasapi 3.1.0 (net10.0-windows10.0.19041.0, ARM64):
// WasapiRecorderBuilder().WithDevice(dev).WithFormat(new WaveFormat(16000,16,1)).Build(); l'evento DataAvailable e' un
// CaptureDataAvailableHandler(ReadOnlySpan<byte> buffer, flags, devicePosition, qpcPosition); DisposeAsync esiste.
// NON verificato: la cattura vera e propria (il microfono non e' stato aperto durante la verifica).
public sealed class WasapiMicCapture : IAudioCapture
{
    private WasapiRecorder? _rec;
    private readonly MemoryStream _pcm = new();
    private Channel<ReadOnlyMemory<byte>>? _frames;                           // solo per provider streaming

    public IReadOnlyList<AudioInputDevice> ListDevices()
    {
        using var en = new MMDeviceEnumerator();
        return en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                 .Select(d => new AudioInputDevice(d.ID, d.FriendlyName)).ToList();
    }

    public Task StartAsync(AudioCaptureOptions o, CancellationToken ct)
    {
        using var en = new MMDeviceEnumerator();
        MMDevice dev = o.DeviceId is { } id && TryGet(en, id, out var chosen)
            ? chosen
            : en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications); // le cuffie sono di norma il device "Communications"

        _pcm.SetLength(0);
        _frames = o.Streaming ? Channel.CreateUnbounded<ReadOnlyMemory<byte>>() : null;
        _rec = new WasapiRecorderBuilder()
            .WithDevice(dev)
            .WithFormat(new WaveFormat(16000, 16, 1))                          // shared mode: converte WASAPI
            .Build();
        _rec.DataAvailable += (buffer, flags, devicePos, qpcPos) =>
        {
            _pcm.Write(buffer);                                                // lo span vale solo dentro la callback: copiare
            _frames?.Writer.TryWrite(buffer.ToArray());
        };
        _rec.StartRecording();
        return Task.CompletedTask;
    }

    public async Task<AudioClip> StopAsync()
    {
        _rec?.StopRecording();
        if (_rec is not null) await _rec.DisposeAsync();
        _frames?.Writer.TryComplete();
        return new AudioClip(_pcm.ToArray(), SampleRate: 16000, Channels: 1);
    }
}

// Equivalente NAudio 2.4.0:
//   var cap = new WasapiCapture(dev) { WaveFormat = new WaveFormat(16000, 16, 1) };
//   cap.DataAvailable += (s, e) => _pcm.Write(e.Buffer, 0, e.BytesRecorded);
//   cap.StartRecording(); ... cap.StopRecording(); // attendere RecordingStopped prima di leggere il buffer
```

Note pratiche:

- Salvare nelle impostazioni **`MMDevice.ID`** (stabile) e il `FriendlyName` solo per mostrarlo. Se il device non c'e' piu', ripiegare su `Role.Communications` e avvisare a voce.
- `WASAPI` preferibile a `WaveIn` (WinMM): latenza minore, scelta del device per ID, conversione di formato in shared mode.
- **Formato di upload in v1: PCM grezzo**. 16 kHz x 16 bit x mono = 32 KB/s -> 30 s = 0,96 MB. Nessun encoder, nessuna dipendenza nativa, e `file_format=pcm_s16le_16` e' il percorso a latenza minore. In v2, per enunciati lunghi o rete lenta: Opus con **Concentus 2.2.2** (C# puro, quindi identico su x64/ARM64/macOS) + `Concentus.OggFile` -> ~3 KB/s. Evitare `MediaFoundationEncoder` (solo Windows, assente nelle edizioni "N").
- Tagliare il silenzio iniziale/finale con una soglia di energia e **scartare** clip < 300 ms o senza energia: i modelli STT su silenzio o rumore possono inventare testo.
- Fermare qualunque TTS **prima** di aprire il microfono.

### 2.4 Trigger push-to-talk

- Azioni nuove nell'enum dei trigger: `DictationToggle`, `DictationHoldStart`/`DictationHoldEnd`, `DictationCancel`, `DictationConfirm`, `DictationUndo`, `DictationRepeat`, `WindowsVoiceTypingToggle`.
- Sorgenti: l'hook mouse low-level gia' previsto (`WH_MOUSE_LL`: `XBUTTON1`/`XBUTTON2` sono candidati naturali se il mouse li ha) e un hook tastiera (`WH_KEYBOARD_LL`) o `RegisterHotKey` per un tasto singolo (es. `F9`, o un pulsante esterno/pedale USB che si presenta come tastiera).
- L'evento trigger va **consumato** (non passato all'applicazione) e non deve cambiare il focus: la dettatura scrive dove c'e' il cursore di testo, non dove punta il mouse.
- Negli hook ignorare gli eventi iniettati da noi (`LLKHF_INJECTED` + `dwExtraInfo == OurTag`).
- Default `Toggle` con limite massimo (es. 90 s, con earcon di avviso a 10 s dalla fine); `Hold` opzionale.

### 2.5 Inserimento del testo nell'applicazione attiva

#### Confronto

| | `SendInput` + `KEYEVENTF_UNICODE` (`Type`) | Appunti + Ctrl+V (`Paste`) |
|---|---|---|
| Appunti dell'utente | **intatti** | sovrascritti: vanno salvati e ripristinati |
| Velocita' | carattere per carattere; bene fino a qualche centinaio di caratteri | istantaneo anche per testi lunghi |
| Compatibilita' | qualunque finestra che passi da `TranslateMessage`/`WM_CHAR` (Word, Excel, campi standard, WPF, Chromium). Toolkit che leggono solo `WM_KEYDOWN` ignorano `VK_PACKET` | ovunque funzioni Ctrl+V |
| A capo | `'\n'` va inviato come `VK_RETURN`, non come carattere | incluso nel testo; in Excel su cella selezionata divide su piu' righe/celle |
| Annulla | Backspace x N (o Ctrl+Z, ma in Word possono servire piu' passi) | un solo Ctrl+Z |
| Effetti collaterali | correzione automatica di Word scatta come per la digitazione (di solito desiderabile) | in Word compare il pulsante "Opzioni Incolla"; cronologia appunti/cloud se non esclusi |
| Rischi | tasti modificatori fisicamente premuti interferiscono; UIPI blocca verso finestre elevate **senza errore** | `OpenClipboard` puo' fallire se un'altra app lo tiene aperto; tempi di ripristino incerti |

Dalla documentazione: con `KEYEVENTF_UNICODE` "`wVk` must be 0", `wScan` contiene il carattere UTF-16, il sistema sintetizza `VK_PACKET` e il flag "can only be combined with the KEYEVENTF_KEYUP flag". `SendInput` "is subject to UIPI" e "neither GetLastError nor the return value will indicate the failure was caused by UIPI blocking"; inoltre "does not reset the keyboard's current state".

**Scelta proposta.** Matteo lavora con Photoshop e Affinity: negli appunti c'e' spesso un'immagine o un livello copiato, con formati proprietari e a rendering ritardato che non si possono salvare/ripristinare in modo affidabile. Quindi:

- Default globale **`Type`**, a blocchi (es. 32 caratteri, pausa 5-10 ms fra i blocchi).
- Profilo per-applicazione (per nome processo) che puo' forzare `Paste` dove `Type` non funziona.
- `Paste` salva/ripristina **solo se gli appunti contengono esclusivamente testo (o sono vuoti)**; se contengono altro, ripiega su `Type` e, se anche quello e' escluso dal profilo, avvisa a voce senza toccare gli appunti.

```csharp
public sealed class SendInputTextInjector : ITextInjector
{
    public async Task<InjectionReceipt> InsertAsync(string text, InjectionOptions o, CancellationToken ct)
    {
        await WaitModifiersReleasedAsync(ct);                                 // GetAsyncKeyState su Ctrl/Shift/Alt/Win
        var target = GetCurrentTarget();
        int typed = 0;
        foreach (var block in Chunk(text, 32))
        {
            var inputs = new List<INPUT>(block.Length * 2);
            foreach (char c in block)                                         // unita' UTF-16: i surrogati vanno come due pacchetti consecutivi
            {
                if (c == '\r') continue;
                if (c == '\n')
                {
                    if (o.NewlinePolicy == NewlinePolicy.Drop) continue;      // es. Excel
                    inputs.Add(Keys.Vk(Keys.VK_RETURN, true)); inputs.Add(Keys.Vk(Keys.VK_RETURN, false));
                }
                else
                {
                    inputs.Add(Unicode(c, down: true)); inputs.Add(Unicode(c, down: false));
                }
                typed++;
            }
            var arr = inputs.ToArray();
            if (Keys.SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>()) != arr.Length)
                throw new TextInjectionException("SendInput blocked (UIPI or another input block)");
            await Task.Delay(o.InterBlockDelayMs, ct);
        }
        return new InjectionReceipt(target, InjectionMode.Type, typed);
    }

    private static INPUT Unicode(char c, bool down) => new()
    {
        type = Keys.INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = c,
              dwFlags = Keys.KEYEVENTF_UNICODE | (down ? 0 : Keys.KEYEVENTF_KEYUP), dwExtraInfo = Keys.OurTag } }
    };

    public Task<bool> UndoAsync(InjectionReceipt r, CancellationToken ct) =>
        r.Mode == InjectionMode.Paste ? SendChordAsync(Keys.VK_CONTROL, (ushort)'Z', ct)
                                      : SendRepeatedAsync(Keys.VK_BACK, r.UnitsTyped, ct);
}
```

Incolla "fatta bene" (Win32 diretto, non `System.Windows.Clipboard`, che e' noto per eccezioni intermittenti):

1. Thread STA con una finestra message-only proprietaria degli appunti.
2. `OpenClipboard(hwnd)` con tentativi (5 x 20 ms) -> leggere e conservare `CF_UNICODETEXT` se gli appunti sono solo testo.
3. `EmptyClipboard()`; `SetClipboardData(CF_UNICODETEXT, NULL)` = **rendering ritardato**: quando l'applicazione di destinazione legge davvero il dato arriva `WM_RENDERFORMAT`, che ci fa da "ricevuta di lettura".
4. Aggiungere i formati registrati documentati da Microsoft per non sporcare cronologia e sincronizzazione cloud: `ExcludeClipboardContentFromMonitorProcessing` (qualsiasi dato), `CanIncludeInClipboardHistory` (DWORD 0), `CanUploadToCloudClipboard` (DWORD 0). `CloseClipboard()`.
5. `SendInput` di Ctrl+V. In `WM_RENDERFORMAT` chiamare `SetClipboardData(CF_UNICODETEXT, hGlobal)` (senza `OpenClipboard`) e segnalare un `TaskCompletionSource`.
6. Attendere la ricevuta (timeout 1 s) + 100-150 ms, poi ripristinare il testo precedente. Un gestore di appunti di terze parti puo' richiedere il rendering prima dell'applicazione: per questo il piccolo ritardo resta.

Regole per applicazione:

- **Word**: `Type` funziona; `'\n'` -> Invio (nuovo paragrafo). Aggiungere uno spazio finale dopo ogni enunciato (configurabile) per concatenare le dettature.
- **Excel**: `Type` su cella selezionata entra in modifica e sostituisce il contenuto; opzione di profilo "conferma con Invio dopo l'inserimento"; `NewlinePolicy.Drop`.
- **Photoshop / Affinity (strumento testo)**: sconosciuto. Provare `Type` e `Paste` a mano; scegliere nel profilo. Per Affinity la finestra in primo piano deve avere gia' un riquadro di testo in modifica.
- **Finestre elevate** (programmi avviati come amministratore): l'inserimento fallisce in silenzio. Non eseguire la nostra app elevata per aggirarlo; limitarsi a rilevare il caso (integrita' del processo in primo piano) e dirlo a voce.
- Registrare la finestra in primo piano all'inizio della registrazione (`GetForegroundWindow`) e, se al momento dell'inserimento e' cambiata, **non inserire**: dire "La finestra e' cambiata" e tenere il testo in sospeso per `DictationRepeat`/`DictationConfirm`.

### 2.6 Ciclo di rilettura e comandi vocali

Macchina a stati (core, senza dipendenze Windows):

```
Idle --Toggle--> Recording --Toggle/MaxDuration--> Transcribing --ok--> ReadingBack --(auto|Confirm)--> Inserting --> Idle
        ^             |                                  |                   |
        |           Cancel                            errore              Cancel / StopSpeech
        +-------------+----------------------------------+-------------------+--> (earcon "annullato") --> Idle
```

- **ReadingBack**: `ISpeechOutput.SpeakAsync("Ho capito: " + text)`. Per ridurre l'attesa conviene poter scegliere la voce di rilettura: voce Windows (immediata) oppure ElevenLabs (modello a bassa latenza, senza cache perche' il testo e' sempre nuovo).
- **Modalita' di conferma** (`ConfirmMode`):
  - `ReadThenAutoInsert` (default proposto): legge, attende ~1,5 s, inserisce. Un clic del trigger o "ferma voce" durante rilettura/attesa annulla. Due soli clic per frase.
  - `ReadThenConfirm`: inserisce solo con `DictationConfirm`; timeout = annulla.
  - `InsertThenRead`: inserisce subito e poi rilegge; `DictationUndo` per tornare indietro.
  - `NoReadBack`: per quando si usa gia' la lettura "frase sotto il puntatore".
- **Bassa confidenza**: se la media di `exp(logprob)` e' sotto soglia, o il testo e' vuoto, dire "Non ho capito bene" + testo, e richiedere conferma esplicita anche in `ReadThenAutoInsert`.
- **Comandi a enunciato intero** (v1): normalizzare (minuscole, via punteggiatura e accenti) e confrontare l'intero enunciato con una tabella:

```csharp
public sealed record VoiceCommandTable(IReadOnlyDictionary<string, DictationCommand> Map)
{
    public static VoiceCommandTable DefaultItalian => new(new Dictionary<string, DictationCommand>
    {
        ["cancella"] = DictationCommand.UndoLast, ["annulla"] = DictationCommand.UndoLast,
        ["a capo"] = DictationCommand.NewLine,   ["nuova riga"] = DictationCommand.NewLine,
        ["rileggi"] = DictationCommand.RepeatLast, ["ripeti"] = DictationCommand.RepeatLast,
    });

    public bool TryMatch(string utterance, out DictationCommand cmd) => Map.TryGetValue(Normalize(utterance), out cmd);
}
```

  L'assistente puo' aggiungere **alias**: cio' che il motore scrive di solito quando Matteo dice "cancella" (es. "cancela", "cancellah"). E' una personalizzazione a costo zero.
- Ogni comando vocale deve avere anche un **equivalente gestuale** (azione trigger): le parole brevi e isolate sono proprio quelle che l'STT riconosce peggio nel parlato disartrico, mentre il clic e' un gesto che Matteo controlla gia'.
- **Sostituzioni personali** (`Replacements`): tabella "sentito -> voluto" per nomi propri e parole sistematicamente sbagliate; in v2 affiancata da `keyterms`.

### 2.7 Parlato disartrico: cosa dicono le evidenze

- **Motori commerciali, zero-shot, inglese** (corpus TORGO; arXiv 2512.17474, dic. 2025, rivisto ago. 2026; testati AssemblyAI, Whisper large-v3, Deepgram Nova-3, GPT-4o, Gemini 2.5; **ElevenLabs Scribe non testato**): disartria lieve ~1-5% WER, moderata ~18-22%, **grave oltre il 49-51% per tutti i sistemi**. Whisper fra i migliori sistemi convenzionali; i modelli multimodali non danno vantaggi.
- **Caso grave, olandese** (arXiv 2606.30237): WER > 70% sia per ascoltatori umani sia per Whisper large-v3, Google Chirp 3 e Omnilingual.
- **Conta l'adattamento, non la marca.** Interspeech 2025 Speech Accessibility Project Challenge (400+ ore, 500+ parlanti): 12 squadre su 22 battono il baseline whisper-large-v2; la migliore arriva a **8,11% WER** dopo fine-tuning. Microsoft dichiara miglioramenti dal 18% al 60% su Azure Speech grazie ai dati SAP, **solo per l'inglese**.
- **Italiano**: esiste il corpus **EasyCall** (21.386 registrazioni, 31 parlanti disartrici, comandi) e l'app **CapisciAMe** con esperimenti di fine-tuning di Whisper su parlato disartrico italiano (MDPI Electronics 13(7):1389, 2024). Su EasyCall un modello multilingue generico (MMS) zero-shot ha CER 63% a livello di frase, 34,5% dopo fine-tuning con dati aumentati (arXiv 2505.14874): i modelli generici in italiano disartrico partono male.
- **Voiceitt** (riconoscimento personalizzato per parlato atipico) dichiara "20+ lingue"; non ho trovato conferma dell'italiano ne' di un'integrazione di dettatura su Windows.

Conseguenze per il progetto:

1. Non promettere nulla prima di una prova con la voce di Matteo. Se la disartria e' lieve, `scribe_v2` o Win+H andranno bene; se e' moderata serviranno rilettura + sostituzioni + alias; se e' grave la dettatura libera probabilmente non e' praticabile con motori generici e la strada e' un modello personalizzato (Whisper fine-tuned, provider locale) o un vocabolario chiuso di frasi.
2. Provider **sostituibile** e **banco di prova**: stesse 20-30 frasi registrate una volta, passate a tutti i provider; l'assistente giudica a orecchio/lettura.
3. Scelte di progetto che aiutano comunque: fine enunciato decisa dall'utente (niente VAD), lingua fissa, `temperature=0`, microfono a braccetto vicino alla bocca, nessun limite di tempo stretto, conferma esplicita a bassa confidenza.

### 2.8 Interfacce che il core deve esporre

```csharp
namespace PuntaEAscolta.Core.Dictation;

// ---- Piattaforma (implementate nel layer Windows; domani macOS) ----
public interface IAudioCapture : IAsyncDisposable
{
    IReadOnlyList<AudioInputDevice> ListDevices();
    Task StartAsync(AudioCaptureOptions options, CancellationToken ct);         // sempre PCM 16 kHz mono s16le
    IAsyncEnumerable<ReadOnlyMemory<byte>> ReadFramesAsync(CancellationToken ct); // per provider streaming (v2)
    Task<AudioClip> StopAsync();
    event EventHandler<AudioCaptureFault>? Faulted;                              // cuffia scollegata, accesso negato
}

public interface ITextInjector
{
    ForegroundTarget GetCurrentTarget();                                         // token opaco: finestra + nome processo + elevata?
    Task<InjectionReceipt> InsertAsync(string text, InjectionOptions options, CancellationToken ct);
    Task SendKeyAsync(EditKey key, CancellationToken ct);                        // Enter, Backspace, Tab
    Task<bool> UndoAsync(InjectionReceipt receipt, CancellationToken ct);
}

public interface IExternalDictationLauncher                                      // Windows: Win+H. macOS: dettatura di sistema
{
    bool IsAvailable { get; }
    Task ToggleAsync(CancellationToken ct);
}

public interface IEarconPlayer { void Play(Earcon earcon); }

// ---- Core portabile ----
[Flags] public enum SttCapabilities { Batch = 1, Streaming = 2, Offline = 4, Keyterms = 8 }

public interface ISpeechToTextProvider
{
    string Id { get; }
    SttCapabilities Capabilities { get; }
    Task<SttResult> TranscribeAsync(AudioClip clip, SttOptions options, CancellationToken ct);
    IAsyncEnumerable<SttEvent> StreamAsync(IAsyncEnumerable<ReadOnlyMemory<byte>> pcmFrames,
                                           SttOptions options, CancellationToken ct); // NotSupportedException se !Streaming
}

public sealed record AudioClip(byte[] Pcm16kMono, int SampleRate, int Channels);
public sealed record SttOptions(string? LanguageCode = "it", IReadOnlyList<string>? Keyterms = null, string? PreviousText = null);
public sealed record SttResult(string Text, string? Language, double? Confidence, TimeSpan AudioDuration, string ProviderId);
public abstract record SttEvent
{
    public sealed record Partial(string Text) : SttEvent;
    public sealed record Committed(string Text) : SttEvent;
    public sealed record Error(string Detail) : SttEvent;
}

public enum DictationState { Idle, Recording, Transcribing, ReadingBack, AwaitingConfirm, Inserting }
public enum ConfirmMode { ReadThenAutoInsert, ReadThenConfirm, InsertThenRead, NoReadBack }
public enum InjectionMode { Type, Paste }

public sealed class DictationSettings
{
    public bool Enabled { get; set; }
    public string ProviderId { get; set; } = "elevenlabs-scribe-v2";
    public string? FallbackProviderId { get; set; }
    public string Language { get; set; } = "it";
    public string? MicrophoneDeviceId { get; set; }
    public bool HoldToTalk { get; set; }                                         // false = Toggle
    public int MaxUtteranceSeconds { get; set; } = 90;
    public ConfirmMode ConfirmMode { get; set; } = ConfirmMode.ReadThenAutoInsert;
    public double LowConfidenceThreshold { get; set; } = 0.55;
    public InjectionMode DefaultInjection { get; set; } = InjectionMode.Type;
    public Dictionary<string, AppInjectionProfile> PerApp { get; set; } = new(); // chiave: nome processo
    public bool AppendTrailingSpace { get; set; } = true;
    public Dictionary<string, string> CommandAliases { get; set; } = new();
    public Dictionary<string, string> Replacements { get; set; } = new();
}

public sealed class DictationController(
    IAudioCapture capture, IReadOnlyDictionary<string, ISpeechToTextProvider> providers, ITextInjector injector,
    ISpeechOutput speech, IEarconPlayer earcons, ITextSanitizer sanitizer, DictationSettings settings)
{
    public DictationState State { get; private set; }
    public event EventHandler<DictationState>? StateChanged;                     // utile anche ai test
    public Task HandleAsync(TriggerAction action, CancellationToken ct);         // unico punto d'ingresso dai trigger
}
```

Punti che rendono l'aggiunta indolore:

- `ISpeechOutput`, `ITextSanitizer` (filtro emoji), `ISecretStore` (DPAPI) e il sistema di trigger sono **gli stessi** della funzione di lettura: vanno progettati fin da ora come servizi condivisi, non come dettagli interni del lettore.
- `DictationController` e' testabile senza Windows con implementazioni finte di tutte le interfacce.
- Per macOS: `IAudioCapture` -> AVAudioEngine; `ITextInjector` -> `CGEventKeyboardSetUnicodeString` oppure NSPasteboard + Cmd+V (serve il permesso Accessibilita'); i provider HTTP/WebSocket e Concentus sono gia' portabili; `Whisper.net` ha runtime macOS.

---

## 3. Insidie note

1. **`scribe_v1` e' deprecato** con rimozione annunciata per il 9 luglio 2026 (la pagina Models lo elenca ancora come deprecato; rimozione effettiva non verificabile senza chiave). Molti esempi in rete lo usano ancora: usare sempre `scribe_v2`.
2. **Cuffie Bluetooth**: aprire il microfono fa passare la cuffia al profilo "hands-free" (audio telefonico). Conseguenze: la voce TTS peggiora di colpo, il cambio profilo dura 1-2 s e taglia l'inizio della registrazione. Preferire cuffia **USB o con dongle 2,4 GHz**; se Bluetooth, tenere il flusso di cattura aperto o inserire un pre-roll dopo l'earcon.
3. **Privacy microfono**: per app desktop non pacchettizzate non c'e' richiesta di consenso; se "Consenti alle app desktop di accedere al microfono" e' spento la cattura fallisce o restituisce silenzio.
4. **Chiave API**: le chiavi ElevenLabs hanno permessi per endpoint. Una chiave creata solo per il Text to Speech puo' ricevere 401/403 su `/v1/speech-to-text`.
5. **Privacy dei dati**: l'audio della voce di Matteo va ai server ElevenLabs; la Zero Retention Mode (`enable_logging=false`) e' riservata ai clienti Enterprise. Informare la famiglia.
6. **`SendInput` e UIPI**: verso finestre elevate fallisce senza alcun codice d'errore.
7. **Tasti modificatori premuti** al momento dell'iniezione (anche per tremore o appoggio involontario) trasformano il testo in scorciatoie. Attendere il rilascio con `GetAsyncKeyState` prima di scrivere.
8. **Mai iniettare input dentro la callback dell'hook low-level**: la callback ha un tempo massimo (`LowLevelHooksTimeout`) e Windows rimuove in silenzio gli hook lenti. Accodare su un worker.
9. **I nostri eventi iniettati rientrano nei nostri hook**: marcarli con `dwExtraInfo` e filtrarli.
10. **`sizeof(INPUT)`** deve essere 40 byte a 64 bit: se la union non include `MOUSEINPUT`, `SendInput` restituisce 0. Vale identico su ARM64.
11. **A capo**: inviare `'\n'` come `KEYEVENTF_UNICODE` non produce un Invio affidabile; usare `VK_RETURN`. In Excel Invio conferma la cella e sposta la selezione.
12. **Appunti**: in Photoshop/Affinity il contenuto copiato e' spesso un'immagine con formati proprietari a rendering ritardato; un salva/ripristina generico puo' perdere dati o bloccare per secondi. Per questo `Paste` solo quando gli appunti sono testo o vuoti.
13. **Cronologia appunti (Win+V) e sincronizzazione cloud**: senza i formati di esclusione ogni frase dettata finisce nella cronologia.
14. **Win+H**: la punteggiatura automatica e' spenta di default; la lingua segue il layout di tastiera attivo; si ferma al cambio di focus e nel silenzio; se un'altra utility ha registrato Win+H la scorciatoia non arriva alla shell.
15. **Alt+`** per la Dettatura di Word non ha un tasto corrispondente sulla tastiera italiana.
16. **NAudio 3** e' una major recentissima con API di cattura nuove (`WasapiRecorder`); lo span passato a `DataAvailable` vale solo dentro la callback.
17. **Allucinazioni su silenzio/rumore**: senza un controllo di energia, un clic accidentale puo' inserire testo inventato. La rilettura prima dell'inserimento e' anche la rete di sicurezza per questo.
18. **Commit realtime**: commit troppo frequenti degradano il riconoscimento (`commit_throttled`); `previous_text` e' ammesso solo nel primo chunk; commit automatico a ~36 s.
19. **Parole isolate brevi** ("cancella") sono il caso peggiore per l'STT con parlato atipico: non affidare funzioni essenziali solo ai comandi vocali.
20. **Listino ElevenLabs cambiato a maggio 2026**: le "ore incluse" (Pro: 450 h batch / 254 h realtime, non 100 / 56 come scritto nella prima stesura) valgono per il nuovo listino e sono verosimilmente un budget in dollari condiviso con il TTS; un abbonamento sul vecchio schema a crediti consuma crediti condivisi con il TTS.

---

## 4. Domande aperte da verificare sulla macchina

1. **Accuratezza reale sulla voce di Matteo** (la domanda che decide tutto): registrare con la cuffia 20-30 frasi tipiche e confrontare `scribe_v2`, Win+H, Whisper large-v3. Misurare anche l'effetto di `no_verbatim` e di `language_code` fisso.
2. **Affinity v3**: nello strumento testo funzionano (a) `KEYEVENTF_UNICODE`, (b) Ctrl+V di testo semplice, (c) Win+H? Stesse tre prove in **Photoshop**, in una **cella Excel** e in **Word**.
3. **Latenza batch** end-to-end per clip da 3, 10 e 30 s dalla rete di casa (obiettivo < 2 s per 10 s di audio). Se e' troppo alta, anticipare il realtime.
4. **Win+H in italiano**: la punteggiatura automatica funziona? I comandi "Elimina elemento", "Termina dettatura" funzionano (la pagina inglese dice "US English only")? Dopo quanti secondi di silenzio si ferma?
5. **Accesso vocale in italiano**: e' disponibile sulla build installata (dev: 10.0.26220; PC di Matteo: da controllare)? Come se la cava con la voce di Matteo offline?
6. **Word Dictate**: Matteo ha Microsoft 365? `Alt`+`` ` `` e' raggiungibile con layout italiano? Il pulsante "Dettatura" e' invocabile via UIA `InvokePattern`?
7. **Cuffia**: USB o Bluetooth? ID e nome dell'endpoint di cattura; e' il device `Role.Communications`? Con Bluetooth, quanto dura il cambio profilo?
8. **NAudio 3.1.0 su win-arm64 con .NET 10**: `WasapiRecorderBuilder().WithFormat(new WaveFormat(16000,16,1))` in shared mode converte davvero a 16 kHz mono sul codec audio Snapdragon? Firma esatta di `DataAvailable`.
9. **Account ElevenLabs**: vecchio listino a crediti o nuovo? Ore STT incluse visibili in dashboard? La chiave API esistente ha il permesso Speech to Text?
10. **Ergonomia**: Matteo riesce a tenere premuto un tasto (Hold) o e' meglio Toggle? Quale pulsante fisico? `ReadThenAutoInsert` e' confortevole o servono conferme esplicite? Da decidere osservandolo.
11. **Durata tipica degli enunciati**: se detta parole singole (nomi di file, etichette) il batch basta per sempre; se vuole scrivere lettere, il realtime in v2 diventa prioritario.
12. **Correzione automatica di Word** con testo digitato via `SendInput`: altera la lunghezza del testo in modo da rompere "Backspace x N"? In tal caso usare Ctrl+Z anche per `Type` in Word.

---

## 5. Fonti

ElevenLabs
- Modelli (id, deprecazioni): https://elevenlabs.io/docs/overview/models
- API reference batch: https://elevenlabs.io/docs/api-reference/speech-to-text/convert
- API reference realtime (WebSocket): https://elevenlabs.io/docs/api-reference/speech-to-text/v-1-speech-to-text-realtime
- Commit strategies: https://elevenlabs.io/docs/eleven-api/guides/how-to/speech-to-text/realtime/transcripts-and-commit-strategies
- Capacita', lingue, fasce WER, formati: https://elevenlabs.io/docs/overview/capabilities/speech-to-text
- Prezzi API (Pro: 450 h batch / 254 h realtime; 100 h / 56 h sono i valori Creator): https://elevenlabs.io/pricing/api
- Specifica OpenAPI pubblica (default ed enum esatti dei campi multipart): https://api.elevenlabs.io/openapi.json
- Post nuovo listino (7 mag 2026): https://elevenlabs.io/blog/weve-lowered-api-agents-pricing-and-introduced-pay-as-you-go
- Limiti di concorrenza STT: https://elevenlabs.io/docs/help-center/product/core-capabilities/speech-to-text/how-many-speech-to-text-requests-can-i-make-and-can-i-increase-it
- Changelog 8 giu 2026 (rimozione `scribe_v1`): https://elevenlabs.io/docs/changelog/2026/6/8
- Annuncio Scribe v2 (9 gen 2026): https://elevenlabs.io/blog/introducing-scribe-v2
- Annuncio Scribe v2 Realtime: https://elevenlabs.io/blog/introducing-scribe-v2-realtime
- Zero Retention Mode (Enterprise): https://elevenlabs.io/docs/eleven-api/resources/zero-retention-mode
- Chiavi API e permessi: https://elevenlabs.io/docs/overview/administration/workspaces/api-keys

Microsoft
- Digitazione vocale (EN): https://support.microsoft.com/en-us/windows/use-voice-typing-to-talk-instead-of-type-on-your-pc-fec94565-c4bd-329d-e59a-af033fa5689f
- Digitazione vocale (IT): https://support.microsoft.com/it-it/accessibility/windows/use-voice-typing-to-talk-instead-of-type-on-your-pc
- Configurare l'accesso vocale (lingue, offline): https://support.microsoft.com/en-us/topic/set-up-voice-access-9fc44e29-12bf-4d86-bc4e-e9bb69df9a0e
- Novita' dell'accesso vocale (italiano): https://support.microsoft.com/en-us/topic/what-s-new-in-voice-access-57a5e801-f741-40d2-a508-d84821ee0f53
- Fluid dictation: https://support.microsoft.com/en-us/accessibility/windows/voice-access/fluid-dictation
- Dettatura in Word: https://support.microsoft.com/en-us/office/dictate-your-documents-in-word-3876e05f-3fcc-418f-b8ab-db7ce0d11d3c
- `SendInput`: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput
- `KEYBDINPUT`: https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-keybdinput
- Formati appunti (cronologia e cloud): https://learn.microsoft.com/en-us/windows/win32/dataxchg/clipboard-formats
- `Windows.Media.SpeechRecognition`: https://learn.microsoft.com/en-us/windows/apps/design/input/speech-recognition
- Azure Speech, lingue e Custom Speech: https://learn.microsoft.com/en-us/azure/ai-services/speech-service/language-support
- Microsoft Ability Summit 2025 (dati SAP su Azure): https://blogs.microsoft.com/blog/2025/03/18/microsoft-ability-summit-2025-accessibility-in-the-ai-era/

Librerie
- NAudio su NuGet (3.1.0, 2.4.0): https://www.nuget.org/packages/NAudio/
- NAudio.Wasapi: https://www.nuget.org/packages/NAudio.Wasapi/
- NAudio releases: https://github.com/naudio/NAudio/releases
- NAudio 3.0.0 note di rilascio: https://github.com/naudio/NAudio/releases/tag/v3.0.0
- `WasapiRecorder`: https://github.com/naudio/NAudio/blob/main/Docs/WasapiRecorder.md
- Annuncio NAudio 3 preview (22 mag 2026): https://markheath.net/post/2026/5/22/announcing-naudio-3-preview
- Whisper.net 1.9.1: https://www.nuget.org/packages/Whisper.net e https://github.com/sandrohanea/whisper.net
- Concentus 2.2.2: https://www.nuget.org/packages/Concentus

Parlato disartrico
- Zero-shot con ASR commerciali e MLLM su TORGO: https://arxiv.org/abs/2512.17474
- Interspeech 2025 SAP Challenge: https://arxiv.org/abs/2507.22047
- Caso olandese grave, umani vs ASR: https://arxiv.org/html/2606.30237
- EasyCall corpus (italiano): https://arxiv.org/pdf/2104.02542
- Voice conversion per ASR disartrico in lingue a poche risorse (italiano incluso): https://arxiv.org/html/2505.14874v1
- Interfaccia vocale edge / CapisciAMe / Whisper: https://www.mdpi.com/2079-9292/13/7/1389
- Riconoscimento disartrico cross-eziologia: https://arxiv.org/pdf/2501.14994
- Voiceitt: https://www.voiceitt.com/

Fonti secondarie (usate solo per comportamenti non documentati da Microsoft)
- Win+H si ferma da solo: https://www.yaps.ai/blog/windows-voice-typing-keeps-stopping
- Dettatura in Excel: https://www.yaps.ai/blog/voice-typing-in-excel
- Fluid dictation e lingue 2026: https://dictaflow.io/blog/windows-fluid-dictation-copilot-plus-2026.html

---

## 6. Verifica indipendente

Controllo avversariale del 21 settembre 2026, limitato alle affermazioni su **ElevenLabs Scribe** e su **NAudio** (il resto del documento - Win+H, SendInput, appunti, letteratura sulla disartria - **non** e' stato riverificato). Fonti: solo primarie (`elevenlabs.io/docs`, specifica OpenAPI pubblica `https://api.elevenlabs.io/openapi.json` scaricata oggi, pagina prezzi ufficiale, `nuget.org`). Nessuna chiamata autenticata (non c'e' una chiave API); il microfono non e' stato aperto. Dettagli sul metodo e sulle prove NAudio: sezione 6 di `elevenlabs-tts.md`.

Legenda: **CONFERMATO**, **CORRETTO** (testo gia' sistemato sopra), **NON VERIFICABILE** (serve la chiave), **MISURATO** (provato su questa macchina).

| # | Affermazione | Esito | Evidenza |
|---|---|---|---|
| 1 | Id dei modelli: `scribe_v2` (batch), `scribe_v2_realtime` (WebSocket), `scribe_v2_medical` | CONFERMATO | https://elevenlabs.io/docs/overview/models |
| 2 | `scribe_v1` "rimosso" | **CORRETTO** (precisato) | Changelog: "deprecated and will be removed on July 9, 2026" (https://elevenlabs.io/docs/changelog/2026/6/8); la pagina Models oggi lo elenca ancora come deprecato; nella OpenAPI non e' mai nominato. Rimozione effettiva: NON VERIFICABILE |
| 3 | `POST https://api.elevenlabs.io/v1/speech-to-text`, `multipart/form-data`, header `xi-api-key`, `model_id` obbligatorio | CONFERMATO | OpenAPI, schema `Body_Speech_to_Text_v1_speech_to_text_post` (`required: ["model_id"]`) |
| 4 | Nomi dei campi multipart: `model_id`, `file`, `language_code`, `tag_audio_events`, `diarize`, `timestamps_granularity`, `temperature`, `file_format`, `no_verbatim`, `keyterms` | CONFERMATO | OpenAPI (in piu': `num_speakers`, `diarization_threshold`, `additional_formats`, `source_url`, `webhook*`, `seed`, `use_multi_channel`, `multichannel_output_style`, `entity_detection`, `entity_redaction*`, `detect_speaker_roles`, `use_speaker_library`; `cloud_storage_url` deprecato) |
| 5 | `file_format=pcm_s16le_16` = PCM 16 bit, 16 kHz, mono, little-endian, con latenza minore | CONFERMATO (testuale) | OpenAPI: enum `pcm_s16le_16` \| `other`, default `other`; "Latency will be lower than with passing an encoded waveform" |
| 6 | `tag_audio_events` va messo a `false` | CONFERMATO | default `true` nella OpenAPI |
| 7 | `timestamps_granularity`: `word`, oppure `none` | CONFERMATO | enum `none` \| `word` \| `character`, default `word` |
| 8 | `temperature` 0-2 | CONFERMATO | "Accepts values between 0.0 and 2.0 ... If omitted ... usually 0" |
| 9 | `keyterms`: max 1000, < 50 caratteri, <= 5 parole, costo extra | CONFERMATO | OpenAPI (che parla di "+20% surcharge"; la pagina prezzi di +0,05 $/h) |
| 10 | Limite del file "< 3 GB (la reference dice < 5 GB)" | CONFERMATO | 3 GB: https://elevenlabs.io/docs/overview/capabilities/speech-to-text ; 5.0GB: OpenAPI |
| 11 | Risposta: `text`, `language_code`, `language_probability`, `words[]`, `transcription_id`, `audio_duration_secs` | CONFERMATO | https://elevenlabs.io/docs/api-reference/speech-to-text/convert |
| 12 | Italiano (`ita`) nella fascia "Excellent", WER <= 5% | CONFERMATO | pagina capabilities STT |
| 13 | Realtime: `wss://api.elevenlabs.io/v1/speech-to-text/realtime`, host regionali, header `xi-api-key` o `token`, parametri di query, messaggio `input_audio_chunk` (`audio_base_64`, `commit`, `sample_rate`, `previous_text`), tipi di messaggio del server | CONFERMATO | https://elevenlabs.io/docs/api-reference/speech-to-text/v-1-speech-to-text-realtime (in piu': `no_verbatim`, `entity_detection`, `secondary_languages`, `filter_background_audio`, evento `committed_transcript_entities`) |
| 14 | Regole di commit (36 s, 20-30 s, `commit_throttled`) | NON RIVERIFICATO | pagina "Transcripts and commit strategies" non riletta in questa verifica |
| 15 | Prezzi: `scribe_v2` 0,22 $/h, realtime 0,39 $/h, keyterm +0,05 $/h, entity detection +0,07 $/h | CONFERMATO | https://elevenlabs.io/pricing/api |
| 16 | "Pro: 100 ore incluse (batch), 56 ore (realtime)" | **CORRETTO** | Nell'HTML della pagina prezzi la riga "Hours included" e' 4h30 / 27 / 100 / **450** / 1359 / 4500 (batch) e 2h30 / 15 / 56 / **254** / 767 / 2538 (realtime) per Free / Starter / Creator / **Pro** / Scale / Business: 100 e 56 sono i valori Creator |
| 17 | Concorrenza Pro: 40 richieste STT batch, 30 sessioni realtime | CONFERMATO | tabella della pagina Models (Pro: STT 40, Realtime STT 30) |
| 18 | Zero Retention Mode (`enable_logging=false`) solo Enterprise, anche per STT | CONFERMATO | OpenAPI ("may only be used by enterprise customers") e https://elevenlabs.io/docs/eleven-api/resources/zero-retention-mode (elenca "Speech to Text") |
| 19 | Una chiave limitata al TTS puo' essere rifiutata su `/v1/speech-to-text` | PLAUSIBILE | La pagina delle chiavi conferma lo "scope" per endpoint e il 401/403; il codice esatto restituito e' NON VERIFICABILE |
| 20 | Nessun SDK .NET ufficiale | NON RIVERIFICATO | |
| 21 | NAudio 3.1.0 (7/9/2026), 3.0.0 (15/8/2026), 2.4.0 (26/8/2026) su nuget.org | CONFERMATO | `api.nuget.org` (registrazione del pacchetto); esiste anche 3.0.1 (18/8) e 3.1.1-preview.1 (8/9) |
| 22 | `WasapiRecorder` / `WasapiRecorderBuilder` (`WithDevice`, `WithFormat`, `Build`), `DataAvailable` con span valido solo nella callback, `WasapiCapture` e `WaveInEvent` obsoleti | CONFERMATO e MISURATO (solo compilazione) | tipi e commenti XML dentro `naudio.wasapi.3.1.0.nupkg`; lo sketch di 2.3 (parte builder + evento) compila con `net10.0-windows10.0.19041.0` su ARM64; avvisi CS0618 sui tipi vecchi. La cattura reale a 16 kHz mono sul codec Snapdragon resta da provare (domanda aperta 8) |
| 23 | `Whisper.net` 1.9.1 e `Concentus` 2.2.2 esistono su nuget.org | CONFERMATO | indice versioni di `api.nuget.org` (Whisper.net ha anche 1.9.2-preview1); supporto ARM64 di Whisper.net non riverificato |

Conseguenze per il progetto: nessuna modifica alla pipeline proposta (batch `scribe_v2`, PCM grezzo, `language_code=it`, `tag_audio_events=false`). L'unico errore di sostanza era il numero di ore incluse, che va a favore del progetto (il margine e' maggiore). Per NAudio vale la scelta fatta per la riproduzione (2.4.0 prudente, 3.1.0 alternativa gia' compilata).
