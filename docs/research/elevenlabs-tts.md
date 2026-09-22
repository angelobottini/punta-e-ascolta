# ElevenLabs TTS e riproduzione audio in .NET 10 (x64 + ARM64)

Ricerca svolta il 21 settembre 2026 per il progetto "Punta e Ascolta". La prima stesura era solo documentale (nessun codice compilato), quindi gli sketch C# vanno considerati "da verificare al primo build". Le versioni e le date citate sono quelle trovate oggi nelle fonti ufficiali (elenco in fondo).

> **Aggiornamento dello stesso giorno - verifica indipendente.** Il documento e' stato ricontrollato punto per punto sulle fonti primarie (documentazione ElevenLabs, specifica OpenAPI pubblica `https://api.elevenlabs.io/openapi.json`, nuget.org) e, per NAudio, con due piccole prove di compilazione ed esecuzione sul Zenbook ARM64 (.NET SDK 10.0.401). Le affermazioni sbagliate sono state corrette nel testo e marcate con **[corretto]**; l'esito completo e' nella sezione 6 "Verifica indipendente". Nessuna chiamata autenticata all'API ElevenLabs e' stata fatta (non c'e' una chiave): tutto cio' che richiede la chiave resta "da verificare".

---

## 1. Raccomandazione

### 1.1 In sintesi

1. **Client ElevenLabs scritto a mano con `HttpClient`, nel core portabile** (niente SDK). Endpoint unico per la voce: `POST https://api.elevenlabs.io/v1/text-to-speech/{voice_id}/stream?output_format=pcm_44100`, header `xi-api-key`, corpo JSON. `optimize_streaming_latency` e' **deprecato**: non usarlo.
2. **Due modelli, scelti in base al tipo di testo**:
   - **Frasi (Word, selezione, OCR di testo lungo): `eleven_multilingual_v2`** - il modello di qualita' piu' stabile, 29 lingue con italiano, 10.000 caratteri per richiesta, 1 credito/carattere. Non accetta `language_code` (rileva la lingua dal testo, cosa che su una frase intera funziona bene).
   - **Etichette brevi (voci di menu, pulsanti, celle, 1-4 parole): `eleven_flash_v2_5` con `language_code` esplicito (`"it"` o `"en"`)** - circa 75 ms di inferenza, meta' prezzo, e soprattutto e' l'unico modo affidabile per *imporre* la lingua su testi brevissimi come "File", "Home", "Layer", dove l'autodetect di Multilingual v2 sbaglia facilmente. Su una o due parole la differenza di qualita' rispetto a v2 e' praticamente inudibile, e il risultato finisce in cache: si paga e si aspetta una volta sola.
   - **`eleven_v3`** (70+ lingue, accetta `language_code`, 5.000 caratteri, stesso prezzo di v2) va offerto come opzione "voce espressiva" nelle impostazioni, **non come default**: latenza piu' alta e resa piu' variabile, interpreta le parentesi quadre come "audio tag", non supporta i tag SSML `<break>`.
   - `eleven_turbo_v2_5` / `eleven_turbo_v2` sono **deprecati** a favore dei Flash: non usarli.
3. **Formato di uscita: PCM grezzo (`pcm_44100`, 16 bit little-endian mono; richiede piano Pro, che l'utente ha)**, con `pcm_24000` come ripiego configurabile. Il PCM si puo' dare in pasto al dispositivo audio byte per byte mentre arriva: nessun decoder, nessuna latenza di decodifica, Stop istantaneo, nessuna dipendenza da codec su ARM64.
4. **Cache su disco in WAV (PCM 16 bit mono)**, chiave = SHA-256 di `versione|provider|voice_id|model_id|language_code|output_format|voice_settings|testo normalizzato`, tetto 500 MB, eliminazione LRU basata su `LastWriteTimeUtc` (toccato ad ogni hit). Niente MP3: a 88 KB/s un'etichetta pesa circa 60 KB, 500 MB contengono migliaia di etichette.
5. **Riproduzione: NAudio**, completamente gestito (nessun binario nativo: gira nativo su ARM64 con il runtime .NET ARM64). **[corretto dopo la verifica] Scelta prudente per la v1: NAudio 2.4.0** (26 agosto 2026, ultima stabile del ramo 2.x, ancora mantenuto) con `WasapiOut` in modalita' condivisa (`WaveOutEvent` come ripiego): API ferma da anni, tutti gli esempi in rete valgono, provata oggi sul Zenbook ARM64 con .NET 10. **Alternativa gia' verificata: NAudio 3.1.0** (7 settembre 2026, richiede .NET 9+) con `WasapiPlayer`: esiste davvero su nuget.org, compila e suona sul Zenbook, ma e' una major di cinque settimane con tre rilasci stabili in 23 giorni (3.0.0 il 15/8, 3.0.1 il 18/8, 3.1.0 il 7/9, quest'ultima con cambi di rottura) e una 3.1.1-preview.1 gia' il giorno dopo. Tutto il codice NAudio va confinato dietro un'interfaccia `IAudioSink` di circa 150 righe nel layer Windows, cosi' il passaggio alla 3.x, quando si sara' assestata, e' indolore.
6. **Voce Windows di ripiego: `Windows.Media.SpeechSynthesis.SpeechSynthesizer` (WinRT, voci OneCore "Microsoft Elsa" e "Microsoft Cosimo")**, sintetizzando in memoria con `SynthesizeTextToStreamAsync` e riproducendo il WAV con lo stesso `IAudioSink` di NAudio, cosi' Stop e' identico e istantaneo per tutte le voci. `System.Speech` vede solo le voci SAPI desktop (per l'italiano solo "Microsoft Elsa Desktop") ed e' da scartare.
7. **Voce ElevenLabs: una voce italiana nativa della Voice Library aggiunta a "My Voices"**, non una voce "default". Le voci default (Sarah, George, Rachel...) sono anglofone, parlano italiano con accento, e soprattutto **scadono il 31 dicembre 2026** e non esistono per gli account creati da marzo 2026 in poi.
8. **Politica di errore orientata all'interattivita'**: niente retry con backoff; se ElevenLabs non risponde entro 1,5 s (etichette) o 4 s (frasi), o risponde 401/402/429/5xx, si parla subito con la voce Windows. Un "circuit breaker" evita di riprovare ad ogni clic quando la rete e' giu' o i crediti sono finiti.

### 1.2 Perche' due modelli se l'utente ha chiesto "qualita'"

La richiesta di qualita' riguarda l'ascolto di frasi. Per le etichette il problema dominante non e' la timbrica ma (a) la **lingua giusta** e (b) la **prontezza**. `eleven_multilingual_v2` ignora/non supporta `language_code`; su "File", "Home", "Stop", "Reset" la lingua viene indovinata e il risultato puo' cambiare da una generazione all'altra. Flash v2.5 e v3 onorano `language_code`. Tra i due, Flash ha latenza molto inferiore e costo dimezzato. La scelta resta comunque un'impostazione (`LabelModelId`), cosi' l'assistente vedente puo' provare `eleven_v3` o `eleven_multilingual_v2` anche per le etichette e decidere a orecchio.

Attenzione: Flash v2.5 **non normalizza i numeri** per default (e `apply_text_normalization: "on"` per Flash/Turbo e' riservato a Enterprise). Regola pratica: se l'etichetta contiene cifre o simboli (`100%`, `12 pt`, `1.234,56`), usare `eleven_multilingual_v2` oppure convertire in lettere nel core prima dell'invio.

---

## 2. Dettagli tecnici

### 2.1 API HTTP Text-to-Speech (stato a settembre 2026)

| Elemento | Valore |
|---|---|
| Base URL | `https://api.elevenlabs.io` (`api.us.elevenlabs.io` forza i server USA; residenza dati UE/India solo Enterprise) |
| File completo | `POST /v1/text-to-speech/{voice_id}` |
| Streaming (chunked) | `POST /v1/text-to-speech/{voice_id}/stream` - stessi parametri; il corpo della risposta sono i byte audio grezzi nel formato richiesto |
| Header | `xi-api-key: <chiave>`, `Content-Type: application/json` |
| Query `output_format` | default `mp3_44100_128`. Valori: `mp3_22050_32`, `mp3_24000_48`, `mp3_44100_32/64/96/128/192`, `pcm_8000/16000/22050/24000/32000/44100/48000`, `opus_48000_32/64/96/128/192`, `ulaw_8000`, `alaw_8000`. **[corretto]** I valori `wav_8000...48000` esistono **solo** sull'endpoint non in streaming (`POST /v1/text-to-speech/{voice_id}`); l'enum di `/stream` nella specifica OpenAPI **non** li contiene. Per noi e' indifferente (usiamo `pcm_*`), ma non vanno proposti nelle impostazioni |
| Vincoli di piano | `mp3_44100_192` da Creator in su; PCM (e WAV) a 44,1 kHz da **Pro** in su. **[corretto]** Anche la pagina `/stream` e' specifica: "PCM with 44.1kHz sample rate requires you to be subscribed to Pro tier or above" (non "tutto il PCM"). Per `pcm_48000` la documentazione non indica alcun vincolo di piano: da provare con la chiave. La pagina dei piani conferma per Pro "44.1kHz PCM audio output via API" |
| Query `enable_logging` | default `true`; `false` = "zero retention mode", **solo Enterprise** ("Zero retention mode may only be used by enterprise customers") |
| Query `optimize_streaming_latency` | 0-4, **deprecato** |

Corpo JSON (campi rilevanti):

| Campo | Tipo / default | Note |
|---|---|---|
| `text` | string, obbligatorio | |
| `model_id` | string, default `eleven_multilingual_v2` | |
| `language_code` | ISO 639-1, opzionale | Impone la lingua. **Non supportato da `eleven_multilingual_v2`**: ometterlo del tutto per quel modello. Se il modello non supporta il codice, viene ignorato |
| `voice_settings` | oggetto opzionale | `stability` 0.5, `similarity_boost` 0.75, `style` 0, `use_speaker_boost` true, `speed` 1.0 (intervallo utile circa 0.7-1.2: utile per un ascoltatore che preferisce un parlato piu' lento) |
| `seed` | int 0-4294967295 | determinismo "best effort" |
| `previous_text` / `next_text` | string | continuita' prosodica tra spezzoni |
| `previous_request_ids` / `next_request_ids` | max 3 | "request stitching" |
| `apply_text_normalization` | `auto` (default) / `on` / `off` | per Flash v2.5 `on` solo Enterprise |
| `apply_language_text_normalization` | bool, default false | oggi solo giapponese |
| `pronunciation_dictionary_locators` | max 3 | utile in futuro per sigle/nome dei programmi |
| `use_pvc_as_ivc` | deprecato | |

Risposta 200: audio. Header utili: `request-id`, `x-trace-id` e **[corretto] `character-cost`** (caratteri addebitati: e' questo il nome documentato nella pagina "API reference - Introduction"; `x-character-count` non compare in nessuna fonte ufficiale), piu' `current-concurrent-requests` e `maximum-concurrent-requests` (descritti nel blog ufficiale sul rate limiting, non nella reference). La reference di `/stream` documenta solo le risposte 200 e 422: la presenza effettiva degli header sulla risposta in streaming resta da verificare con la chiave.

### 2.2 Modelli

| `model_id` | Lingue (IT?) | Latenza dichiarata | Limite caratteri | Prezzo API | `language_code` | Uso nel progetto |
|---|---|---|---|---|---|---|
| `eleven_v3` | 70+ (si') | non dichiarata; fonti terze parlano di ~500 ms e oltre | 5.000 | $0,10 / 1K car. (1 credito/car.) | si' | opzione "espressiva" per frasi |
| `eleven_v3_conversational` | 70+ (si') | ~280 ms | n.d. | $0,05 / 1K | si' (presunto) | **da verificare** se utilizzabile su `/v1/text-to-speech` (nasce per Agents / Text to Dialogue WebSocket) |
| `eleven_multilingual_v2` | 29 (si') | non dichiarata (tipicamente 300-600 ms al primo byte) | 10.000 | $0,10 / 1K (1 credito/car.) | **no** | **default per le frasi** |
| `eleven_flash_v2_5` | 32 (si') | ~75 ms di inferenza | 40.000 | $0,05 / 1K (0,5 crediti/car.) | si' | **default per le etichette** |
| `eleven_flash_v2` | solo inglese | ~75 ms | 30.000 | $0,05 / 1K | - | no |
| `eleven_turbo_v2_5`, `eleven_turbo_v2` | - | - | - | - | - | **deprecati**, sostituiti dai Flash ("functionally equivalent ... except the latency on the Flash models is lower") |
| `eleven_monolingual_v1`, `eleven_multilingual_v1` | - | - | - | - | - | **[aggiunto]** deprecati, rimozione annunciata per il 9 luglio 2026 (changelog 8 giugno 2026): non usarli |

Note di verifica sulla tabella: (a) i prezzi in dollari sono quelli della pagina `pricing/api`, che oggi dice "API usage is billed in US dollars, not credits"; l'equivalenza "1 credito/carattere" e "0,5 crediti/carattere" **non** compare piu' nella pagina Models e vale solo per gli abbonamenti sul vecchio listino a crediti: il dato affidabile per modello e' `model_rates.character_cost_multiplier` di `GET /v1/models`. (b) Quali modelli *onorano* `language_code` la documentazione lo dice solo per esclusione ("This parameter is not supported for multilingual_v2 models", "If the model does not support the provided language code, it will be ignored"); per `eleven_v3` c'e' un indizio forte (gli endpoint Text to Dialogue, che hanno `eleven_v3` come modello predefinito, espongono lo stesso `language_code`), ma la prova vera va fatta a orecchio con la chiave.

Concorrenza piano Pro: 10 richieste parallele per Multilingual v2 (e per "tutti gli altri" modelli non Flash/Turbo, quindi anche v3), 20 per Flash/Turbo; STT 40, STT realtime 30. L'app ne usa 1-2 (ogni nuovo clic annulla la richiesta precedente), quindi il 429 da concorrenza non dovrebbe mai presentarsi.

Suggerimento: nelle impostazioni, invece di cablare l'elenco, interrogare `GET /v1/models` (restituisce `model_id`, `name`, `can_do_text_to_speech`, `languages[]` con `language_id`/`name`, `maximum_text_length_per_request`, `max_characters_request_subscribed_user`, `model_rates.character_cost_multiplier`, `concurrency_group`, `requires_alpha_access`) e mostrare solo i modelli TTS che includono `it`.

Note su `eleven_v3`: il cursore stabilita' ha tre posizioni (Creative / Natural / Robust); per uso assistivo serve "Robust" o "Natural". Le Professional Voice Clone "non sono ancora pienamente ottimizzate per v3". Niente WebSocket per v3, ma gli endpoint HTTP `convert` e `stream` funzionano.

### 2.3 Voci

- **`GET /v2/voices`** (quello da usare): parametri `page_size` (max 100, default 10), `next_page_token`, `search` (cerca in nome, descrizione, etichette, categoria), `sort` (`created_at_unix` | `name`), `sort_direction` (`asc` | `desc`), `voice_type` (`personal`, `community`, `default`, `workspace`, `non-default`, `non-community`, `saved`), `category` (`premade`, `cloned`, `generated`, `professional`), `fine_tuning_state`, `collection_id`, `voice_ids` (fino a 100), `include_total_count` (default `true`), e - **[aggiunto]** presenti oggi nella specifica - i filtri `gender`, `age`, `language`, `accent`, `use_cases`, `min_notice_period_days`, `include_custom_rates`, `include_live_moderated`, `high_quality`. Risposta: `voices[]`, `has_more`, `total_count`, `next_page_token`. Per ogni voce: `voice_id`, `name`, `category` (nella risposta anche `famous` e `high_quality`), `description`, `labels` (accent, gender, language...), `preview_url`, `verified_languages[]`, `high_quality_base_model_ids`, `sharing`, `is_owner`, `is_legacy`, `settings`, `available_for_tiers`.
- `GET /v1/voices`: restituisce tutte le voci dell'account senza paginazione (unico parametro: `show_legacy`). **[corretto]** Non e' marcato come deprecato, ma la specifica avverte: "Stops working once the user's workspace exceeds 500 voices". Non ha ricerca: usare la v2.
- **Voice Library**: `GET /v1/shared-voices?language=it&page_size=30&sort=usage_character_count_1y` (altri filtri: `gender`, `age`, `accent`, `locale`, `category`=`professional|high_quality|famous`, `use_cases`, `descriptives`, `featured`, `include_custom_rates`, `min_notice_period_days`, `owner_id`, `search`, `page` da 0; `page_size` default 30, max 100; `sort` = `created_date` (default) | `usage_character_count_1y` | `trending` | `cloned_by_count`). Richiede la chiave API. Ogni elemento ha `public_owner_id`, `voice_id`, `name`, `preview_url`, `rate` (moltiplicatore di costo: alcune voci costano piu' di 1 credito/carattere - filtrare con `include_custom_rates=false`), `notice_period` (preavviso con cui l'autore puo' ritirare la voce), `free_users_allowed`, `verified_languages`.
- **Aggiungere una voce della Library all'account**: via web (Voice Library, filtro lingua "Italian", pulsante "Add to My Voices") oppure via API `POST /v1/voices/add/{public_user_id}/{voice_id}` con corpo `{"new_name":"..."}` (`new_name` obbligatorio; esiste anche `bookmarked`, default `true`); la risposta contiene il `voice_id` da usare nelle chiamate TTS. Il `public_user_id` e' il campo `public_owner_id` restituito da `GET /v1/shared-voices`: **non** e' l'identificativo utente che si legge nell'URL delle anteprime audio. Le voci della Library non sono utilizzabili via API dagli account gratuiti (con Pro nessun problema). Per l'assistente la via piu' semplice resta il sito; l'app deve solo elencare le voci gia' presenti nell'account con `GET /v2/voices`.
- **Voci default/premade**: "All our Default voices will expire on December 31, 2026" e sono disponibili solo agli account creati prima di marzo 2026. ElevenLabs pubblica una tabella di sostituzione (Roger -> Darian, Sarah -> Talia, George -> Eldrin, ecc.), ma sono tutte voci anglofone. La documentazione stessa raccomanda "choose a voice with an accent that matches your target language". Conclusione: **non basare il progetto su una voce default**.
- **Voci italiane native da provare.** **[corretto]** La pagina ufficiale "Italian Text to Speech" (`elevenlabs.io/text-to-speech/italian`) contiene, nei dati incorporati nell'HTML, 20 voci italiane con `voiceId`, nome, categoria, descrizione e URL dell'anteprima: gli ID qui sotto sono stati estratti da li' il 21/09/2026 e sono quindi di fonte primaria. Restano **da confermare con la chiave** (`GET /v1/shared-voices?language=it&search=<nome>`): che la voce sia ancora condivisa, `rate` (costo), `notice_period`, `category` (PVC = piu' lenta) e `public_owner_id` per l'aggiunta via API. Il genere non e' un campo della pagina: e' dedotto da nome e descrizione. Le anteprime non sono state ascoltate.

| # | Nome (come sulla pagina ufficiale) | `voice_id` | Genere (dedotto) | Categoria / stile dichiarato | Fiducia nell'ID |
|---|---|---|---|---|---|
| 1 | **Carmelo La Rosa - Deep and Balanced** | `HuK8QKF35exsCh2e7fLT` | M, mezza eta' | *Educational*: "e-learning, news, webinar, istitutional" - la piu' adatta a leggere etichette e frasi in modo neutro | alta (pagina ufficiale) |
| 2 | **Tiziana - Smart, Balanced and Credible** | `RXoaSpLaWTEckJgPUBG3` | F, adulta | *Educational*: voce radiofonica "smart and friendly", stile podcast | alta (pagina ufficiale) |
| 3 | **MarcoTrox - Warm, Balanced and Polished** | `W71zT1VwIFFx3mMGH2uZ` | M, mezza eta' | *Narration*: doppiatore professionista, narrazione pulita | alta (pagina ufficiale + catalogo terzo concordi) |
| 4 | **Violetta - Ringing, Bright and Joyful** | `gfKKsLN1k0oYYN9n2dXX` | F, mezza eta' | *Narration*: "warm, clear, and engaging ... informative audio content" | alta (pagina ufficiale) |
| 5 | **Antonio - Natural, Balanced and Calm** | `JfznbVXrGXYh0gZo9Lcp` | M | *Narration*: naturale e calmo; descrizione dell'autore quasi assente | alta per l'ID, bassa per l'idoneita' (mai ascoltata, nessuna descrizione) |

Altre voci della stessa pagina ufficiale (stessa affidabilita' dell'ID): Marco - Deep, Rich and Reflective `13Cuh3NuYvWOVQtLbRN8` (M); Antonio Farina - Expressive and Warm `uScy1bXtKz8vPzfdFsFw` (M); Nicola Lorusso - Balanced and Mature `sKbNSlHXq99bttvf8rRF` (M); Andy - Warm, Nuanced and Mature `DLMxnwJE0a28JQLTMJPJ` (M); Giovanni Rossi - Deep and Sympathetic `fzDFBB4mgvMlL36gPXcz` (M); Samanta - Reassuring, Warm and Deep `fQmr8dTaOQq116mo2X7F` (F); Linda Fiore - Prickly, Cheerful and Full `3DPhHWXDY263XJ1d2EPN` (F); Chris Basetta - Smiley and Engaging `t3hJ92dgZhDVtsff084B` (M, social media, poco adatta).

**[corretto]** ID della prima stesura **non confermati** da fonte primaria (venivano dal catalogo di terze parti json2video e sulla pagina ufficiale non compaiono): Carmelo La Rosa `pWHqWjkaSNybDOvgMt58` (sulla pagina ufficiale Carmelo La Rosa e' `HuK8QKF35exsCh2e7fLT`), Chris Basetta `g1X9mrbeBlMAWtcs2Dfp` (sulla pagina ufficiale e' `t3hJ92dgZhDVtsff084B`; l'altro potrebbe essere una variante), Lorena `Ifz6upLTTyg10iqpfWL5`, Giusy `8KInRSd4DtD5L5gK7itu`, Vera `ko0CPqOV7GGUcPkJMPej`, Ginevra `QITiGyM4owEZrBEf0QV8`, Alessandra `Ap2b3ZnSIW7h0QbBbxCq`. Possono essere giusti, ma non vanno cablati.

Per un'app che legge etichette e frasi neutre conviene una voce "neutra, da e-learning" (Carmelo La Rosa, Tiziana) piu' che una voce da audiolibro drammatico. **Nessun ID va cablato nel codice**: l'assistente aggiunge 2-3 voci a "My Voices" dal sito e l'app le elenca con `GET /v2/voices` e ne riproduce `preview_url`; la scelta finale va fatta a orecchio dall'utente.

Nota latenza: secondo la guida ufficiale le voci default, sintetiche e IVC sono piu' rapide delle Professional Voice Clone; molte voci professionali della Library sono PVC. La cache attenua il problema.

### 2.4 Crediti e abbonamento

`GET /v1/user/subscription` -> `tier`, `status` (`trialing`, `active`, `incomplete`, `past_due`, `free`, `free_disabled`), `character_count` (usati), `character_limit`, `next_character_count_reset_unix` (puo' essere `null`), `voice_slots_used`, `voice_limit`, `can_extend_character_limit`, `max_credit_limit_extension` (intero oppure `"unlimited"`), `current_overage` (`amount`, `currency`), `billing_period`, `character_refresh_period`, `has_open_invoices`... **[corretto]** `max_character_limit_extension` e `allowed_to_extend_character_limit` esistono ancora ma sono marcati *deprecated* (usare `max_credit_limit_extension != 0`). Crediti residui = `character_limit - character_count`. Se la chiave API e' limitata per permessi, serve anche il permesso di lettura "User". Consiglio per l'assistente: creare una chiave dedicata con permessi minimi (Text to Speech, Voices in lettura, User in lettura) e un tetto mensile di crediti.

Stima **[corretta]**: oggi le pagine ufficiali danno due numeri diversi per il piano Pro da $99/mese, a seconda del listino: la pagina dei piani dice "600k credits per month" (la prima stesura diceva "storicamente 500.000"); la pagina `pricing/api` (listino del 7 maggio 2026, "API usage is billed in US dollars, not credits") dice 990.000 caratteri inclusi con v3 / Multilingual v2 (= $99 a $0,10 per 1.000 caratteri; il doppio con Flash a $0,05). Quale valga per l'account di Angelo dipende dal listino su cui si trova l'abbonamento ("Switch to new pricing" in "Manage subscription"): **leggere `character_limit` dalla risposta reale**. Ordine di grandezza: fra 6.000 e 9.900 frasi da 100 caratteri al mese con Multilingual v2. Le etichette si pagano una sola volta grazie alla cache. **I crediti vengono addebitati sull'intero testo della richiesta anche se l'utente ferma l'ascolto dopo un secondo**: per questo i testi lunghi vanno spezzati per frase (2.8).

### 2.5 Errori

Forma attuale del corpo d'errore:

```json
{ "detail": { "type": "...", "code": "...", "message": "...", "status": "...", "request_id": "...", "param": "..." } }
```

Forme storiche ancora possibili: `{"detail":{"status":"quota_exceeded","message":"..."}}` e, per il 422, `{"detail":[{"loc":[...],"msg":"...","type":"..."}]}`. Il parser deve tollerarle tutte.

**[corretto] Le due fonti ufficiali non coincidono**, quindi l'app deve decidere in base all'identificatore (`detail.code` oppure `detail.status`) prima che in base al codice HTTP:

- la pagina "Errors" della documentazione API (tabella qui sotto) usa i nomi nuovi: 402 `insufficient_credits`, 404 `voice_not_found`, 429 `concurrent_limit_exceeded`;
- gli articoli del centro assistenza, tuttora pubblicati, usano i nomi vecchi: `quota_exceeded`, `voice_not_found` e `max_character_limit_exceeded` sotto "Error Code 400 or 401" (con corpo `{"detail":{"status":"quota_exceeded","message":"This request exceeds your quota of ... You have 0 credits remaining ..."}}`), e per il 429 `too_many_concurrent_requests` (limiti: Free 2, Starter 3, Creator 5, Pro 10, Scale 15, Business 15) e `system_busy`.

Regola pratica: `quota_exceeded` **o** `insufficient_credits`, con qualunque HTTP (400, 401, 402) = crediti finiti; `too_many_concurrent_requests` **o** `concurrent_limit_exceeded` = concorrenza. Quale forma arrivi davvero oggi non e' verificabile senza chiave.

| HTTP | `type` / `code` (pagina "Errors") | Azione nell'app |
|---|---|---|
| 400 | `validation_error`: `invalid_parameters`, `text_too_long`, `empty_text`, `invalid_voice_id`, `invalid_voice_settings`, `unsupported_model`, `invalid_output_format` (**[corretto]**: `invalid_audio_format` riguarda l'audio in ingresso, non `output_format`); storici: `max_character_limit_exceeded`, `quota_exceeded`, `voice_not_found` | log + voce Windows; e' un bug di configurazione (tranne `quota_exceeded`: trattarlo come 402) |
| 401 | `authentication_error`: `invalid_api_key`, `missing_api_key`, `invalid_authorization_header`, `unauthorized` (storicamente anche `quota_exceeded`) | disattivare ElevenLabs finche' la chiave non cambia; avviso nel tray per l'assistente |
| 402 | `payment_required`: `insufficient_credits` | disattivare fino a `next_character_count_reset_unix` (ricontrollo ogni ora) |
| 403 | `authorization_error`: `feature_not_available`, `subscription_required`, `insufficient_permissions`, `voice_access_denied`, `model_access_denied`, `forbidden` (anche IP fuori dalla allowlist della chiave) | es. `pcm_44100` senza Pro: riprovare una volta con `pcm_24000`, poi voce Windows |
| 404 | `not_found`: `voice_not_found`, `model_not_found` | voce ritirata dalla Library: avviso all'assistente, voce Windows |
| 422 | errore di validazione (array) - e' l'unico errore elencato nella reference di `/stream` | log + voce Windows |
| 429 | `rate_limit_error`: `rate_limit_exceeded`, `concurrent_limit_exceeded`, `system_busy`; storico: `too_many_concurrent_requests` | nessun backoff: voce Windows per questo enunciato |
| 500 / 503 | `internal_error`; `service_unavailable`, `maintenance` | voce Windows; dopo 3 errori consecutivi sospendere ElevenLabs per 60 s |

Timeout consigliati (`HttpClient.Timeout = InfiniteTimeSpan`, tutto governato da `CancellationTokenSource`): connessione 3 s (`SocketsHttpHandler.ConnectTimeout`); header di risposta entro 1,5 s per le etichette e 4 s per le frasi; stallo tra un chunk e l'altro 5 s. Se il fallimento avviene **prima** del primo audio si passa alla voce Windows; se avviene **dopo** (flusso interrotto a meta' frase) ci si ferma e basta: ripartire da capo con un'altra voce confonderebbe.

### 2.6 Limiti di lunghezza

10.000 caratteri (`eleven_multilingual_v2`), 5.000 (`eleven_v3`), 40.000 (`eleven_flash_v2_5`). L'app non deve mai avvicinarsi: spezzare a confine di frase in blocchi da 300-1.000 caratteri.

### 2.7 Privacy

- Zero Retention Mode (`enable_logging=false`) e' **solo Enterprise**; con il piano Pro testo e audio vengono conservati secondo la Privacy Policy e compaiono nella cronologia dell'account (cancellabili da web o con `DELETE /v1/history/{history_item_id}`).
- Residenza dati UE: solo Enterprise.
- Conseguenza di progetto: il testo dei documenti Word/Excel dell'utente esce dal PC. Prevedere nelle impostazioni l'interruttore "Usa ElevenLabs solo per i comandi dell'interfaccia; leggi i documenti con la voce di Windows" e documentarlo per l'assistente.
- Chiave API cifrata con DPAPI: pacchetto `System.Security.Cryptography.ProtectedData`, `ProtectedData.Protect(bytes, entropy, DataProtectionScope.CurrentUser)`. Il blob e' legato a utente+PC: copiando la cartella portabile su un altro PC la chiave va reinserita. Nasconderlo dietro `ISecretStore` (su macOS sara' il Keychain).

### 2.8 Architettura proposta (separazione core / Windows)

```
PuntaEAscolta.Core (net10.0, portabile)
  ITtsProvider, ElevenLabsClient (HttpClient puro), TtsCache, TextNormalizer,
  SpeechOrchestrator (cache -> ElevenLabs -> voce locale), IAudioSink, ISecretStore, ILocalVoice
PuntaEAscolta.Windows (net10.0-windows10.0.19041.0)
  NAudioSink : IAudioSink, WinRtLocalVoice : ILocalVoice, DpapiSecretStore : ISecretStore
```

Il core consegna all'`IAudioSink` byte PCM + formato; non conosce NAudio. Su macOS bastera' un altro `IAudioSink` (AVAudioEngine) e un altro `ILocalVoice` (AVSpeechSynthesizer).

### 2.9 Sketch C# - client ElevenLabs (core)

```csharp
public sealed record VoiceSettings(double Stability = 0.5, double SimilarityBoost = 0.75,
    double Style = 0.0, bool UseSpeakerBoost = true, double Speed = 1.0);

public sealed record TtsRequest(string Text, string VoiceId, string ModelId, string? LanguageCode,
    VoiceSettings Settings, string OutputFormat = "pcm_44100",
    string? PreviousText = null, string? NextText = null);

public sealed record ElevenLabsError(int Status, string? Code, string? Message, string? RequestId);
public sealed class ElevenLabsException(ElevenLabsError e) : Exception($"{e.Status} {e.Code}: {e.Message}")
{ public ElevenLabsError Error { get; } = e; }

public sealed class ElevenLabsClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull   // omette language_code nullo
    };
    private readonly HttpClient _http;
    private readonly Func<string> _apiKey;                              // letto da ISecretStore ad ogni richiesta

    public ElevenLabsClient(Func<string> apiKey)
    {
        _apiKey = apiKey;
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(3),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),      // tiene calda la connessione TLS
            AutomaticDecompression = DecompressionMethods.None
        };
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.elevenlabs.io/"),
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
    }

    /// Restituisce la risposta con il corpo NON ancora letto (streaming). Il chiamante la deve Dispose-are.
    public async Task<HttpResponseMessage> OpenSpeechStreamAsync(TtsRequest r, CancellationToken ct)
    {
        var url = $"v1/text-to-speech/{Uri.EscapeDataString(r.VoiceId)}/stream?output_format={r.OutputFormat}";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.TryAddWithoutValidation("xi-api-key", _apiKey());
        req.Content = JsonContent.Create(new
        {
            Text = r.Text,
            ModelId = r.ModelId,
            // multilingual_v2 non supporta language_code: non inviarlo
            LanguageCode = r.ModelId == "eleven_multilingual_v2" ? null : r.LanguageCode,
            VoiceSettings = r.Settings,
            PreviousText = r.PreviousText,
            NextText = r.NextText
        }, options: Json);

        var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.IsSuccessStatusCode) return resp;
        try { throw new ElevenLabsException(await ReadErrorAsync(resp, ct)); }
        finally { resp.Dispose(); }
    }

    public async Task<SubscriptionInfo> GetSubscriptionAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "v1/user/subscription");
        req.Headers.TryAddWithoutValidation("xi-api-key", _apiKey());
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) throw new ElevenLabsException(await ReadErrorAsync(resp, ct));
        return (await resp.Content.ReadFromJsonAsync<SubscriptionInfo>(Json, ct))!;
    }

    // GET v2/voices?page_size=100[&next_page_token=...][&search=...]
    public async IAsyncEnumerable<VoiceDto> ListVoicesAsync(string? search,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string? token = null;
        do
        {
            var url = "v2/voices?page_size=100"
                    + (search is null ? "" : "&search=" + Uri.EscapeDataString(search))
                    + (token is null ? "" : "&next_page_token=" + Uri.EscapeDataString(token));
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("xi-api-key", _apiKey());
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) throw new ElevenLabsException(await ReadErrorAsync(resp, ct));
            var page = (await resp.Content.ReadFromJsonAsync<VoicesPage>(Json, ct))!;
            foreach (var v in page.Voices) yield return v;
            token = page.HasMore ? page.NextPageToken : null;
        } while (token is not null);
    }

    private static async Task<ElevenLabsError> ReadErrorAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        string body = await resp.Content.ReadAsStringAsync(ct);
        string? code = null, msg = null, reqId = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("detail", out var d))
            {
                if (d.ValueKind == JsonValueKind.Object)
                {
                    code  = Str(d, "code") ?? Str(d, "status");          // forma nuova / forma storica
                    msg   = Str(d, "message");
                    reqId = Str(d, "request_id");
                }
                else if (d.ValueKind == JsonValueKind.Array && d.GetArrayLength() > 0)
                { code = "validation_error"; msg = Str(d[0], "msg"); }   // 422
                else if (d.ValueKind == JsonValueKind.String) msg = d.GetString();
            }
        }
        catch (JsonException) { msg = body.Length > 200 ? body[..200] : body; }
        return new ElevenLabsError((int)resp.StatusCode, code, msg, reqId);

        static string? Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString() : null;
    }

    public void Dispose() => _http.Dispose();
}

public sealed record SubscriptionInfo(string Tier, string Status, long CharacterCount, long CharacterLimit,
    long NextCharacterCountResetUnix);
public sealed record VoicesPage(List<VoiceDto> Voices, bool HasMore, string? NextPageToken);
public sealed record VoiceDto(string VoiceId, string Name, string? Category,
    Dictionary<string, string>? Labels, string? PreviewUrl);
```

All'avvio conviene "scaldare" la connessione (DNS + TLS + HTTP/2) con una `GetSubscriptionAsync` in background: serve comunque per mostrare i crediti e fa risparmiare 100-200 ms al primo clic.

### 2.10 Sketch C# - riproduzione in streaming con NAudio (layer Windows)

Scritto per NAudio 3.1 (`WasapiPlayer`); nei commenti l'equivalente 2.4 (che dopo la verifica e' la scelta prudente: basta scambiare le due righe del player). **[corretto - misurato]** Le firme sono state controllate compilando contro i pacchetti reali: in **entrambe** le versioni (2.4.0 e 3.1.0) `BufferedWaveProvider.BufferDuration` e' di **sola lettura** (errore CS0200 se lo si mette nell'inizializzatore) e la durata si passa al costruttore `BufferedWaveProvider(WaveFormat, TimeSpan?)` (default 5 s). In NAudio 3 la lettura e' `Read(Span<byte>)`, ma `AddSamples(byte[],int,int)` esiste ancora accanto a `AddSamples(ReadOnlySpan<byte>)`.

```csharp
public sealed class NAudioSink : IAudioSink
{
    /// Riproduce PCM 16 bit mono mentre arriva da HTTP; opzionalmente lo copia nella cache.
    public async Task PlayPcmStreamAsync(Stream network, int sampleRate, Stream? cacheCopy, CancellationToken ct)
    {
        var format = new WaveFormat(sampleRate, 16, 1);
        var buffer = new BufferedWaveProvider(format, TimeSpan.FromSeconds(30))   // durata solo dal costruttore (2.4.0 e 3.1.0)
        {
            ReadFully = true,                            // durante il download: silenzio se il buffer si svuota, non stop
            DiscardOnBufferOverflow = false
        };

        // NAudio 2.4 (scelta prudente): using var player = new WasapiOut(AudioClientShareMode.Shared, true, 100);
        //                               (using NAudio.CoreAudioApi; nessun "await using": WasapiOut e' solo IDisposable)
        await using var player = new WasapiPlayerBuilder()
            .WithSharedMode().WithEventSync().WithLatency(100)
            .Build();

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.PlaybackStopped += (_, e) =>
        { if (e.Exception is null) done.TrySetResult(); else done.TrySetException(e.Exception); };
        player.Init(buffer);

        // STOP ISTANTANEO: il secondo clic annulla ct -> si ferma il device e si svuota il buffer
        using var stopReg = ct.Register(() => { player.Stop(); buffer.ClearBuffer(); });

        byte[] chunk = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            int prebuffer = format.AverageBytesPerSecond * 150 / 1000;   // ~150 ms prima di partire
            int leftover = 0; bool started = false; int n;
            while ((n = await network.ReadAsync(chunk.AsMemory(leftover, chunk.Length - leftover), ct)) > 0)
            {
                int total = leftover + n;
                int aligned = total & ~1;                                 // i campioni sono a 16 bit: mai byte spaiati
                while (buffer.BufferLength - buffer.BufferedBytes < aligned)
                    await Task.Delay(20, ct);                             // contropressione: niente "Buffer full"
                buffer.AddSamples(chunk, 0, aligned);
                cacheCopy?.Write(chunk, 0, aligned);
                leftover = total - aligned;
                if (leftover == 1) chunk[0] = chunk[aligned];
                if (!started && buffer.BufferedBytes >= prebuffer) { player.Play(); started = true; }
            }
            if (!started) player.Play();                                  // etichette piu' corte del prebuffer
            buffer.ReadFully = false;                                     // download finito: svuota e poi PlaybackStopped
            await done.Task.WaitAsync(ct);
        }
        finally { ArrayPool<byte>.Shared.Return(chunk); }
    }

    /// Cache hit o voce Windows: un WAV completo (file o MemoryStream).
    public async Task PlayWavAsync(Stream wav, CancellationToken ct)
    {
        using var reader = new WaveFileReader(wav);
        await using var player = new WasapiPlayerBuilder().WithSharedMode().WithLatency(100).Build();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.PlaybackStopped += (_, _) => done.TrySetResult();
        player.Init(reader);                                              // in shared mode NAudio ricampiona da solo
        using var stopReg = ct.Register(() => player.Stop());
        player.Play();
        await done.Task.WaitAsync(ct);
    }
}
```

Note:

- **WASAPI condiviso vs WaveOut**: `WasapiPlayer`/`WasapiOut` in shared mode ricampiona automaticamente al formato del mixer (da NAudio 2.1) e ha latenza configurabile (default 200 ms; 100 ms e' un valore prudente, scendere solo dopo prove sul Snapdragon). `WaveOut` (ex `WaveOutEvent`, API winmm) e' l'alternativa piu' vecchia e collaudata: `waveOutReset` rende Stop immediato, ma la latenza tipica e' maggiore (2-3 buffer da 100-150 ms). Tenerla come secondo ripiego selezionabile da impostazioni ("Motore audio: WASAPI / WaveOut").
- In NAudio 3 `WasapiPlayerBuilder.WithDefaultDeviceStreamRouting()` fa seguire al flusso il dispositivo predefinito (cuffie collegate/scollegate a meta' riproduzione). Creando un player per ogni enunciato il problema e' gia' quasi nullo; attivarlo se si adotta il flusso "sempre aperto" descritto nelle insidie.
- NAudio 3 (**verificato aprendo i `.nupkg` 3.1.0**): `NAudio.WinMM` contiene solo `lib/net9.0-windows7.0`; `NAudio.Core` e `NAudio.Wasapi` contengono solo `lib/net9.0`; il meta-pacchetto `NAudio` ha le gambe `net9.0`, `net9.0-windows7.0` e `net9.0-windows10.0.19041`. Il progetto Windows deve avere un TFM `-windows` (il nostro `net10.0-windows10.0.19041.0`, necessario comunque per WinRT, va bene: restore e build riusciti). Il core portabile non deve referenziare NAudio.
- NAudio 2.4.0 con il nostro TFM: `NAudio.Wasapi` 2.4.0 offre `net9.0-windows10.0.26100` e `netstandard2.0`; poiche' il nostro TFM e' `windows10.0.19041`, NuGet sceglie l'asset **`netstandard2.0`** (verificato in `project.assets.json` e a runtime). Funziona; non e' adatto a Native AOT/trimming, che a noi non servono (WPF).
- Meglio referenziare i pacchetti singoli (`NAudio.Core`, `NAudio.Wasapi`, `NAudio.WinMM`) che il meta-pacchetto `NAudio`, il quale trascina anche `NAudio.Asio`, `NAudio.Midi`, `NAudio.WinForms`. **Versione esatta, mai flottante**: su nuget.org `NAudio.Wasapi` ha anche una vecchia versione spuria **22.0.0** (del 2022, non in elenco, dipende da NAudio.Core 2.2.0).

### 2.11 Cache su disco (core)

- **Normalizzazione del testo prima dell'hash**: rimozione emoji/emoticon (requisito di progetto), rimozione degli acceleratori (`&File`, `_File`), dei puntini finali dei menu (`Salva con nome...`), delle parentesi angolari (i modelli v2/Flash interpretano `<break>` e simili), compressione degli spazi, `Normalize(NormalizationForm.FormC)`, `Trim()`. **Non** forzare il minuscolo (puo' cambiare la pronuncia delle sigle).
- **Chiave**: `SHA256("v1|elevenlabs|{voiceId}|{modelId}|{lang}|{outputFormat}|{stability:F2}|{similarity:F2}|{style:F2}|{speed:F2}|{boost}|{seed}|{testo}")` in esadecimale. Cambiare voce o impostazioni invalida naturalmente la cache.
- **Layout**: `%LOCALAPPDATA%\PuntaEAscolta\cache\ab\abcdef...wav` (oppure `cache\` accanto all'eseguibile, essendo distribuzione portabile: decidere una volta sola; la cartella deve essere scrivibile). WAV con header: autodescrittivo, l'assistente puo' ascoltarlo con un doppio clic.
- **Scrittura atomica**: si scrive `*.tmp` durante lo streaming con `WaveFileWriter`; solo a download **completo** si fa `File.Move(tmp, final, overwrite: true)`. Se l'utente ferma l'ascolto: per i testi brevi conviene **lasciar finire il download in background** (i crediti sono gia' stati addebitati) e salvare; per i testi lunghi si annulla e si cancella il `.tmp`. Mai mettere in cache audio parziale.
- **LRU senza indice**: ad ogni hit `File.SetLastWriteTimeUtc(path, DateTime.UtcNow)`; quando il totale supera il tetto (default 500 MB, configurabile) si eliminano i file piu' vecchi fino al 90% del tetto. Totale calcolato all'avvio con una enumerazione e poi aggiornato in memoria. Nessun file indice che si possa corrompere. Non usare `LastAccessTime` (su NTFS l'aggiornamento puo' essere disattivato).
- **Dimensioni**: `pcm_44100` = 88,2 KB/s (500 MB = circa 94 minuti, cioe' circa 8.000 etichette da 0,7 s); `pcm_24000` = 48 KB/s; `mp3_44100_128` = 16 KB/s.
- Beneficio collaterale: l'uscita di ElevenLabs non e' deterministica; con la cache la stessa etichetta suona **sempre uguale**, cosa che aiuta il riconoscimento da parte dell'utente.

```csharp
public static string ComputeKey(TtsPlan p, string normalizedText)
{
    string canon = string.Join('|', "v1", p.Provider, p.VoiceId, p.ModelId, p.LanguageCode ?? "-", p.OutputFormat,
        p.Settings.Stability.ToString("F2", CultureInfo.InvariantCulture),
        p.Settings.SimilarityBoost.ToString("F2", CultureInfo.InvariantCulture),
        p.Settings.Style.ToString("F2", CultureInfo.InvariantCulture),
        p.Settings.Speed.ToString("F2", CultureInfo.InvariantCulture),
        p.Settings.UseSpeakerBoost ? "1" : "0", normalizedText);
    return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canon)));
}
```

**Se in futuro si volesse l'MP3 per la cache** (non raccomandato ora): evitare `Mp3FileReader` con il codec ACM; scegliere tra `MediaFoundationReader`/`StreamMediaFoundationReader` (usa il decoder MP3 di Windows Media Foundation, presente anche su Windows 11 ARM64 ma assente nelle edizioni "N" senza Media Feature Pack) e **NLayer 3.0.0 + `NLayer.NAudioSupport` 3.0.0** (27 agosto 2026, `net9.0`, dipende da `NAudio.Core` >= 3.0.1; decoder MP3 interamente gestito, quindi identico su x64, ARM64 e in futuro macOS). Per NAudio 2.x restare su `NLayer.NAudioSupport` 2.x.

### 2.12 Voce Windows di ripiego (layer Windows)

```csharp
// TFM: net10.0-windows10.0.19041.0 (proiezioni WinRT incluse; nessun pacchetto aggiuntivo)
using Windows.Media.SpeechSynthesis;

public sealed class WinRtLocalVoice : ILocalVoice, IDisposable
{
    private readonly SpeechSynthesizer _synth = new();

    public WinRtLocalVoice(string? preferredDisplayName, string language = "it-IT", double rate = 1.0)
    {
        var all = SpeechSynthesizer.AllVoices;                           // solo voci installate e firmate Microsoft
        _synth.Voice = all.FirstOrDefault(v => v.DisplayName == preferredDisplayName)
                    ?? all.FirstOrDefault(v => v.Language.Equals(language, StringComparison.OrdinalIgnoreCase))
                    ?? SpeechSynthesizer.DefaultVoice;
        _synth.Options.SpeakingRate = rate;                              // 0.5 .. 6.0
        _synth.Options.AppendedSilence = SpeechAppendedSilence.Min;      // taglia il silenzio in coda
        _synth.Options.IncludeWordBoundaryMetadata = false;
        _synth.Options.IncludeSentenceBoundaryMetadata = false;
    }

    public async Task<MemoryStream> SynthesizeWavAsync(string text, CancellationToken ct)
    {
        using SpeechSynthesisStream s = await _synth.SynthesizeTextToStreamAsync(text).AsTask(ct);
        var ms = new MemoryStream((int)s.Size);
        await s.AsStreamForRead().CopyToAsync(ms, ct);                   // RIFF/WAVE PCM: leggere il formato dall'header
        ms.Position = 0;
        return ms;                                                       // -> NAudioSink.PlayWavAsync(ms, ct)
    }

    public void Dispose() => _synth.Dispose();
}
```

- Funziona da app desktop non pacchettizzata (WPF), senza capability. Voci it-IT OneCore: **Microsoft Elsa** (F) e **Microsoft Cosimo** (M); su Windows in italiano sono preinstallate, altrimenti Impostazioni > Data/ora e lingua > Riconoscimento vocale > Gestisci voci > Aggiungi voci.
- La sintesi non e' in streaming (restituisce il WAV completo) ma e' locale e veloce; per testi lunghi sintetizzare una frase alla volta.
- Le "voci naturali" dell'Assistente vocale di Windows 11 **non sono accessibili alle app di terze parti** e l'elenco ufficiale delle voci naturali non riporta l'italiano.
- **`System.Speech`** (pacchetto NuGet `System.Speech`, solo Windows): espone soltanto le voci SAPI 5 desktop (per l'italiano "Microsoft Elsa Desktop"; Cosimo non c'e'), non vede le voci OneCore senza trucchi di registro, e introduce un secondo percorso audio. Si puo' fermare con `SpeakAsyncCancelAll()` e scrivere su stream con `SetOutputToWaveStream`, ma non offre nulla che WinRT non dia gia'. **Scartato.**

### 2.13 Orchestrazione (core)

```csharp
public async Task SpeakAsync(SpeechItem item, CancellationToken ct)      // ct annullato dal secondo clic
{
    string text = _normalizer.Normalize(item.Text);
    if (text.Length == 0) return;

    TtsPlan plan = _policy.Select(item.Kind, text);   // Label -> flash_v2_5 + language_code; Sentence -> multilingual_v2
    string key = TtsCache.ComputeKey(plan, text);

    if (_cache.TryOpenRead(key, out Stream? wav))
    { await using (wav) await _audio.PlayWavAsync(wav, ct); return; }

    if (_gate.AllowsElevenLabs && !(item.IsDocumentText && _settings.DocumentsLocalOnly))
    {
        bool audioStarted = false;
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(plan.FirstByteBudget);                     // 1,5 s etichette / 4 s frasi
            using var resp = await _eleven.OpenSpeechStreamAsync(plan.ToRequest(text), budget.Token);
            budget.CancelAfter(Timeout.InfiniteTimeSpan);                 // header arrivati: disarma il timer
            await using var net = await resp.Content.ReadAsStreamAsync(ct);
            await using var cacheWriter = _cache.BeginWrite(key, plan.SampleRate);   // *.tmp -> commit a fine stream
            audioStarted = true;
            await _audio.PlayPcmStreamAsync(net, plan.SampleRate, cacheWriter.Stream, ct);
            cacheWriter.Commit();
            _gate.ReportSuccess();
            return;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }   // Stop dell'utente
        catch (Exception ex)                                                           // timeout, rete, 4xx/5xx
        {
            _gate.ReportFailure(ex);         // 401 -> chiuso; 402 -> chiuso fino al reset; 3 errori -> pausa 60 s
            if (audioStarted) return;        // flusso interrotto a meta': non ripartire con un'altra voce
        }
    }

    using var local = await _localVoice.SynthesizeWavAsync(text, ct);
    await _audio.PlayWavAsync(local, ct);
}
```

Testi lunghi ("seleziona e leggi"): dividere in frasi, richiedere la frase n+1 mentre suona la n (pipeline di profondita' 1), passare `previous_text`/`next_text` per la continuita' prosodica. Vantaggi: primo audio piu' rapido, Stop spreca al massimo una frase di crediti, ogni frase e' cacheabile.

---

## 3. Insidie note

1. **Voci default in scadenza il 31/12/2026** e assenti sugli account creati da marzo 2026: qualsiasi `voice_id` "premade" cablato (es. Rachel `21m00Tcm4TlvDq8ikWAM`) smettera' di funzionare. Usare una voce della Library aggiunta all'account e gestire il 404 `voice_not_found`.
2. **Le voci della Library possono essere ritirate dall'autore** (con preavviso `notice_period`) e alcune hanno un `rate` > 1 (costano di piu'). Scegliere voci con preavviso lungo e `include_custom_rates=false`; tenere una seconda voce di riserva nelle impostazioni.
3. **`language_code` con `eleven_multilingual_v2`**: la documentazione dice "This parameter is not supported for multilingual_v2 models" e, nella stessa frase, "If the model does not support the provided language code, it will be ignored": **[corretto]** secondo la documentazione il rischio non e' un 400 ma che venga ignorato in silenzio. Comunque non inviarlo per quel modello. Per imporre la lingua servono Flash v2.5 o (con buona probabilita', vedi 2.2) v3.
4. **Testi brevissimi e autodetect della lingua**: "File", "Home", "Reset", numeri puri ("1.234,56") con Multilingual v2 possono uscire in inglese o con accento variabile. Contromisure: Flash/v3 con `language_code`; convertire i numeri in lettere nel core; (da provare) aggiungere un punto finale all'etichetta e/o un `previous_text` italiano per orientare il modello.
5. **Flash v2.5 non normalizza numeri e simboli** e `apply_text_normalization:"on"` per Flash/Turbo e' solo Enterprise.
6. **`eleven_v3`**: `[testo tra quadre]` viene interpretato come audio tag (es. `[Ctrl]` o `[1]` dall'OCR): togliere o sostituire le quadre; nessun `<break>` SSML; piu' incline ad "allucinazioni" con stabilita' Creative e su prompt molto corti; latenza superiore; niente WebSocket.
7. **Parentesi angolari nel testo** (OCR, celle Excel con `<`/`>`): i modelli v2/Flash supportano `<break time="..."/>` e tag fonema; ripulire per evitare interpretazioni indesiderate.
8. **Crediti addebitati per intero alla richiesta**, anche se l'utente ferma l'audio: spezzare per frase; non pre-generare "per sicurezza".
9. **Allineamento a 16 bit**: i chunk HTTP possono avere lunghezza dispari; passare byte spaiati a `AddSamples` produce rumore bianco per tutto il resto del flusso. Gestire il byte di riporto (vedi sketch).
10. **`BufferedWaveProvider` pieno**: `AddSamples` lancia `InvalidOperationException("Buffer full")` se `DiscardOnBufferOverflow` e' false, e scarta audio se e' true. Serve contropressione (attendere spazio) perche' la rete e' piu' veloce del tempo reale.
11. **`ReadFully`**: `true` mentre si scarica (un buco di rete diventa silenzio, non fine riproduzione), `false` a download finito (altrimenti `PlaybackStopped` non arriva mai).
12. **Inizio tagliato su cuffie Bluetooth/USB**: molte cuffie "si svegliano" in 100-300 ms e mangiano l'inizio del primo suono; su un'etichetta da mezzo secondo e' grave. Opzioni da provare con le cuffie reali: (a) anteporre 150-300 ms di silenzio a freddo (impostazione "pre-roll"); (b) tenere aperto il flusso audio con silenzio per 30-60 s dopo l'ultimo enunciato. Non tenerlo aperto all'infinito: un flusso audio attivo impedisce la sospensione automatica del PC.
13. **NAudio 3 e' nuovo** (date da nuget.org: 3.0.0 il 15/8/2026, 3.0.1 il 18/8/2026, 3.1.0 il 7/9/2026, 3.1.1-preview.1 l'8/9/2026; le note di rilascio della 3.1.0 dichiarano cambi di rottura: `WaveFormat` senza `[StructLayout]`, `AudioClient.IsFormatSupported` con `out WaveFormat`). **[corretto]** Il bug n. 1442 ("`WasapiPlayer` can be left into an invalid state if the first ever `IWaveProvider.Read` call throws") e' stato aperto e chiuso il 10/9/2026, cioe' **dopo** la 3.1.0 (7/9) e la 3.1.1-preview.1 (8/9): la correzione e' nel sorgente ma **in nessun pacchetto pubblicato su nuget.org** a oggi. Con `BufferedWaveProvider` (la cui `Read` non lancia eccezioni) l'impatto pratico e' basso, ma conferma che la 3.x si sta ancora assestando. Per questo la scelta prudente e' la 2.4.0. In ogni caso bloccare la versione esatta nel `.csproj`, niente intervalli flottanti. API cambiate rispetto a tutti gli esempi in rete: `WaveOutEvent` -> `WaveOut` (`DesiredLatency` -> `BufferMilliseconds`), `WasapiOut` -> `WasapiPlayer` (builder), `Read(byte[],int,int)` -> `Read(Span<byte>)`. **Verificato compilando contro la 3.1.0**: i vecchi tipi `WasapiOut`, `WasapiCapture`, `WaveOutEvent`, `WaveInEvent` esistono ancora ma sono `[Obsolete]` (avviso CS0618: "Use WasapiPlayerBuilder to create a WasapiPlayer instead", "WaveOutEvent has been renamed to WaveOut"). Non riverificato: "`WaveFileWriter` non chiude piu' lo stream passato dal chiamante".
14. **TFM**: NAudio 3 lato Windows richiede un TFM `-windows` (errore di restore NU1202 con `net10.0` puro). Il core portabile non deve dipendere da NAudio.
15. **ARM64**: ne' NAudio ne' NLayer dichiarano test su ARM64. Non contengono codice nativo (solo P/Invoke/COM verso winmm, WASAPI, Media Foundation, tutti presenti su Windows 11 ARM64), quindi il rischio e' basso, ma va verificato presto sul Zenbook. Pubblicare `win-arm64` e `win-x64` separati (self-contained) oppure framework-dependent "portable": in entrambi i casi nessuna DLL nativa da portarsi dietro per l'audio.
16. **DPAPI lega la chiave a utente+PC**: la cartella portabile copiata altrove richiede di reinserire la chiave; prevedere il messaggio nelle impostazioni.
17. **Privacy**: senza Enterprise non esiste zero-retention; testo e audio restano nella cronologia ElevenLabs. Offrire la modalita' "documenti solo con voce Windows".
18. **Formato del WAV WinRT**: non assumere 16 kHz o 22,05 kHz; leggere l'header con `WaveFileReader`. In shared mode il ricampionamento lo fa NAudio.
19. **Errori con forme diverse** (`detail` oggetto nuovo, oggetto storico con `status`, array per il 422, quota storicamente segnalata con 401): parser tollerante, mai deserializzazione rigida.

---

## 4. Domande aperte da verificare sulla macchina

1. Con la chiave reale: `GET /v1/user/subscription` -> `tier`, `character_limit` effettivo del piano Pro, data di reset. `GET /v1/models` -> elenco reale dei modelli TTS, presenza di `eleven_v3_conversational` e se e' accettato da `/v1/text-to-speech/{id}/stream` (sarebbe un candidato interessante per le etichette: qualita' v3, ~280 ms, meta' prezzo).
2. L'account e' stato creato prima di marzo 2026? (Determina se le voci default esistono ancora; in ogni caso non usarle.)
3. Ascolto comparato in italiano, stessa voce, stesse 10 etichette e 5 frasi: `eleven_multilingual_v2` vs `eleven_flash_v2_5` (con `language_code:"it"`) vs `eleven_v3` (Natural/Robust). Misurare anche il tempo al primo byte dall'Italia per ciascuno, a connessione calda e fredda.
4. Verificare i `voice_id` italiani della tabella 2.3 con `GET /v1/shared-voices?language=it&search=...`, controllare `rate`, `notice_period`, `category` (PVC?) e scegliere 2-3 voci da far ascoltare all'utente.
5. Cosa succede inviando `language_code` a `eleven_multilingual_v2` (ignorato o 400?) e `enable_logging=false` con piano Pro (ignorato o errore?).
6. L'header documentato per i caratteri addebitati e' `character-cost` (vedi 2.1): controllare che arrivi anche sulla risposta di `/stream`, insieme a `request-id`, `current-concurrent-requests` e `maximum-concurrent-requests`.
7. Trucchi per le etichette brevi con Multilingual v2: punto finale, `previous_text` italiano. Efficaci?
8. NAudio su Snapdragon X con .NET 10 ARM64. **Gia' misurato nella verifica (sezione 6.3)**: sia 2.4.0 (`WasapiOut`) sia 3.1.0 (`WasapiPlayer`) in shared mode a 100 ms accettano PCM 44,1 kHz 16 bit mono (il mixer del dispositivo e' 48 kHz float stereo: la conversione e' automatica), suonano e si fermano; firma del costruttore `BufferedWaveProvider(WaveFormat, TimeSpan?)`. **Resta da misurare**: assenza di glitch su enunciati lunghi, tempo di `Init()+Play()` a freddo, Stop percepito come istantaneo con le cuffie reali, stessa prova con `WaveOutEvent`/`WaveOut`.
9. Con le cuffie reali dell'utente: l'inizio delle etichette viene tagliato? Quanto pre-roll serve? Il flusso tenuto aperto con silenzio blocca la sospensione (`powercfg /requests`)?
10. `SpeechSynthesizer.AllVoices` sul Zenbook: Elsa e Cosimo presenti? Formato del WAV prodotto? Tempo di sintesi di un'etichetta e di una frase di 200 caratteri su ARM64.
11. Dove mettere la cache nella distribuzione portabile (accanto all'exe o in `%LOCALAPPDATA%`) - decisione di prodotto.
12. Eseguibile x64 sotto emulazione Prism su ARM64: l'audio funziona ugualmente? (Utile solo come rete di sicurezza se si distribuisse un solo binario.)

---

## 5. Fonti

ElevenLabs (consultate il 21/09/2026):

- Create speech: https://elevenlabs.io/docs/api-reference/text-to-speech/convert
- Stream speech: https://elevenlabs.io/docs/api-reference/text-to-speech/stream
- Modelli: https://elevenlabs.io/docs/models e https://elevenlabs.io/docs/overview/models
- Capacita' Text to Speech (formati, lingue, limiti): https://elevenlabs.io/docs/overview/capabilities/text-to-speech
- Best practice / prompting v3: https://elevenlabs.io/docs/overview/capabilities/text-to-speech/best-practices
- Ottimizzazione latenza: https://elevenlabs.io/docs/eleven-api/guides/how-to/best-practices/latency-optimization
- Streaming audio (concetti): https://elevenlabs.io/docs/eleven-api/concepts/audio-streaming
- Errori: https://elevenlabs.io/docs/eleven-api/resources/errors
- Errore 429 (help center; `help.elevenlabs.io` risponde 403 ai client automatici, la stessa pagina e' in): https://elevenlabs.io/docs/help-center/technical/api-error-code-429
- Errori 400/401 (help center, nomi storici `quota_exceeded` ecc.): https://elevenlabs.io/docs/help-center/technical/api-error-code-400-or-401
- Concorrenza TTS per piano: https://elevenlabs.io/docs/help-center/technical/how-many-text-to-speech-requests-can-i-make-and-can-i-increase-it
- Specifica OpenAPI pubblica (enum e default esatti): https://api.elevenlabs.io/openapi.json
- Header di risposta (`character-cost`, `request-id`): https://elevenlabs.io/docs/api-reference/introduction
- Changelog 8 giugno 2026 (rimozione modelli v1 e `scribe_v1`): https://elevenlabs.io/docs/changelog/2026/6/8
- WebSocket TTS (non supporta `eleven_v3`): https://elevenlabs.io/docs/eleven-api/guides/how-to/websockets/realtime-tts
- Zero Retention Mode: https://elevenlabs.io/docs/eleven-api/resources/zero-retention-mode
- List voices (v2): https://elevenlabs.io/docs/api-reference/voices/search
- Voice Library (shared voices): https://elevenlabs.io/docs/api-reference/voices/voice-library/get-shared
- Add shared voice: https://elevenlabs.io/docs/api-reference/voices/voice-library/share
- Voci default e scadenza: https://elevenlabs.io/docs/help-center/product/voices/my-voices/what-are-default-voices
- Voci (panoramica): https://elevenlabs.io/docs/overview/capabilities/voices
- Abbonamento utente: https://elevenlabs.io/docs/api-reference/user/subscription/get
- Chiavi API (permessi, tetto crediti): https://elevenlabs.io/docs/overview/administration/workspaces/api-keys
- Prezzi API: https://elevenlabs.io/pricing/api
- Pagina TTS italiano (voci in evidenza): https://elevenlabs.io/text-to-speech/italian
- Catalogo di terze parti con voice_id italiani (non ufficiale): https://json2video.com/ai-voices/elevenlabs/languages/italian/

NAudio / NLayer:

- NuGet NAudio 3.1.0: https://www.nuget.org/packages/NAudio
- Release NAudio (3.1.0, 3.0.0, 2.4.0): https://github.com/naudio/NAudio/releases
- Note di rilascio: https://github.com/naudio/NAudio/blob/main/RELEASE_NOTES.md
- Guida di migrazione 2 -> 3: https://github.com/naudio/NAudio/blob/v3.0.0/Docs/MigratingFromNAudio2.md
- WasapiPlayer: https://github.com/naudio/NAudio/blob/main/Docs/WasapiPlayer.md
- WasapiOut (legacy): https://github.com/naudio/NAudio/blob/main/Docs/WasapiOut.md
- Modernizzazione interop COM: https://github.com/naudio/NAudio/blob/main/Docs/Architecture/MODERNIZATION.md
- Issue 1442 (stato di WasapiPlayer): https://github.com/naudio/NAudio/issues/1442
- TFM Windows richiesto da NAudio 3 (caso reale): https://github.com/christopherthompson81/vernacula/issues/127
- NLayer.NAudioSupport 3.0.0: https://www.nuget.org/packages/NLayer.NAudioSupport

Microsoft:

- Windows.Media.SpeechSynthesis.SpeechSynthesizer: https://learn.microsoft.com/en-us/uwp/api/windows.media.speechsynthesis.speechsynthesizer
- System.Speech.Synthesis.SpeechSynthesizer: https://learn.microsoft.com/en-us/dotnet/api/system.speech.synthesis.speechsynthesizer
- Lingue e voci supportate dall'Assistente vocale (Elsa, Cosimo): https://support.microsoft.com/en-us/accessibility/windows/narrator/appendix-a-supported-languages-and-voices
- Voci naturali non disponibili alle app di terze parti (Microsoft Q&A): https://learn.microsoft.com/en-us/answers/questions/4125876/why-are-natural-voices-only-available-for-narrator

---

## 6. Verifica indipendente

Controllo avversariale svolto il 21 settembre 2026 da un secondo agente, partendo dall'ipotesi che ogni affermazione potesse essere vecchia o inventata. Fonti ammesse: solo primarie (documentazione `elevenlabs.io/docs`, specifica OpenAPI pubblica `https://api.elevenlabs.io/openapi.json` scaricata oggi - 2,15 MB, senza autenticazione -, pagine prezzi ufficiali, `nuget.org` e relative API `api.nuget.org`). **Nessuna chiamata autenticata** all'API ElevenLabs: non c'e' una chiave. Le pagine della documentazione sono state lette tramite un estrattore automatico che riassume; dove contava il testo esatto (enum, default, descrizioni) ho usato la specifica OpenAPI o l'HTML grezzo.

Legenda: **CONFERMATO** = trovato identico nella fonte primaria; **CORRETTO** = la prima stesura era sbagliata o imprecisa, testo gia' sistemato sopra; **NON VERIFICABILE** = richiede la chiave API o l'ascolto; **MISURATO** = provato su questa macchina.

### 6.1 API Text to Speech

| # | Affermazione | Esito | Evidenza |
|---|---|---|---|
| 1 | Streaming: `POST /v1/text-to-speech/{voice_id}/stream`, header `xi-api-key`, corpo JSON; query `output_format`, `enable_logging`, `optimize_streaming_latency` | CONFERMATO | OpenAPI, path `/v1/text-to-speech/{voice_id}/stream`; https://elevenlabs.io/docs/api-reference/text-to-speech/stream |
| 2 | `optimize_streaming_latency` deprecato; `use_pvc_as_ivc` deprecato | CONFERMATO | OpenAPI: `deprecated: true` su entrambi |
| 3 | Default `output_format` = `mp3_44100_128`; valori `pcm_8000...pcm_48000`, `opus_*`, `ulaw_8000`, `alaw_8000` | CONFERMATO | OpenAPI (enum di `/stream`) |
| 4 | `wav_*` fra i valori di `/stream` | **CORRETTO** | L'enum di `/stream` non contiene `wav_*`; li contiene solo `POST /v1/text-to-speech/{voice_id}` (OpenAPI) |
| 5 | "La pagina `/stream` dice genericamente che il PCM richiede Pro" | **CORRETTO** | Testo esatto su `/stream`: "PCM with 44.1kHz sample rate requires you to be subscribed to Pro tier or above"; su convert: "PCM and WAV formats with 44.1kHz sample rate requires ... Pro tier or above". Pagina piani, Pro: "44.1kHz PCM audio output via API" (https://elevenlabs.io/pricing) |
| 6 | `mp3_44100_192` da Creator in su | CONFERMATO | stessa descrizione |
| 7 | `enable_logging=false` = zero retention, solo Enterprise | CONFERMATO | OpenAPI: "Zero retention mode may only be used by enterprise customers"; https://elevenlabs.io/docs/eleven-api/resources/zero-retention-mode ("Enterprise customers can use Zero Retention Mode"; copre TTS, STT, Text to Dialogue, Voice Changer, Agents). Cosa succede inviandolo con un piano Pro: NON VERIFICABILE |
| 8 | Campi del corpo (`text`, `model_id` default `eleven_multilingual_v2`, `language_code`, `voice_settings`, `seed` 0-4294967295, `previous_text`/`next_text`, `previous/next_request_ids` max 3, `apply_text_normalization` auto/on/off, `apply_language_text_normalization`, `pronunciation_dictionary_locators` max 3) | CONFERMATO | OpenAPI, schema `Body_text_to_speech_stream` |
| 9 | `voice_settings`: stability 0.5, similarity_boost 0.75, style 0, use_speaker_boost true, speed 1.0; velocita' utile 0,7-1,2 | CONFERMATO | OpenAPI `VoiceSettingsResponseModel`; best practices: minimo 0.7, massimo 1.2 |
| 10 | `language_code` non supportato da `eleven_multilingual_v2` | CONFERMATO | OpenAPI: "This parameter is not supported for multilingual_v2 models" |
| 11 | Inviare `language_code` a Multilingual v2 rischia un 400 | **CORRETTO** | La stessa descrizione dice "it will be ignored". Comportamento reale: NON VERIFICABILE |
| 12 | Flash v2.5 e v3 onorano `language_code` | PARZIALE | Nessuna pagina elenca i modelli che lo supportano. Indizio per v3: gli endpoint `/v1/text-to-dialogue*` (modello predefinito `eleven_v3`) hanno lo stesso parametro. Da provare a orecchio |
| 13 | Header `x-character-count` | **CORRETTO** | Il nome documentato e' `character-cost` (con `request-id`, `x-trace-id`): https://elevenlabs.io/docs/api-reference/introduction. `current-concurrent-requests` / `maximum-concurrent-requests`: solo nel blog ufficiale https://elevenlabs.io/blog/ai-rate-limiting-for-voice |
| 14 | `api.us.elevenlabs.io`; residenza UE/India solo Enterprise | CONFERMATO | https://elevenlabs.io/docs/eleven-api/guides/how-to/best-practices/latency-optimization |

### 6.2 Modelli, prezzi, concorrenza

| # | Affermazione | Esito | Evidenza |
|---|---|---|---|
| 1 | Id correnti: `eleven_v3`, `eleven_v3_conversational`, `eleven_multilingual_v2`, `eleven_flash_v2_5`, `eleven_flash_v2` | CONFERMATO | https://elevenlabs.io/docs/overview/models (la pagina elenca anche `eleven_ttv_v3`, `eleven_multilingual_sts_v2`, `eleven_multilingual_ttv_v2`, `eleven_english_sts_v2`, che non sono TTS) |
| 2 | `eleven_turbo_v2_5` / `eleven_turbo_v2` deprecati a favore dei Flash | CONFERMATO | pagina Models; OpenAPI: "Deprecated: Use eleven_flash_v2 instead." |
| 3 | (mancava) `eleven_monolingual_v1` e `eleven_multilingual_v1` | AGGIUNTO | "deprecated and will be removed on July 9, 2026": https://elevenlabs.io/docs/changelog/2026/6/8 |
| 4 | Limiti: v3 5.000, Multilingual v2 10.000, Flash v2.5 40.000, Flash v2 30.000 caratteri | CONFERMATO | tabella "Character limit" della pagina Models |
| 5 | Lingue: v3 70+, Multilingual v2 29, Flash v2.5 32 (tutte con l'italiano) | CONFERMATO | pagina Models e https://elevenlabs.io/docs/overview/capabilities/text-to-speech |
| 6 | Latenze: Flash ~75 ms, v3 Conversational ~280 ms | CONFERMATO | pagina Models |
| 7 | Prezzi: Flash/Turbo $0,05, Multilingual v2 $0,10, v3 $0,10, v3 Conversational $0,05 per 1.000 caratteri | CONFERMATO | https://elevenlabs.io/pricing/api |
| 8 | "1 credito/carattere", "0,5 crediti/carattere" | NON VERIFICABILE | La pagina Models di oggi non parla di crediti; `pricing/api`: "API usage is billed in US dollars, not credits". Vale per i piani sul vecchio listino; leggere `model_rates.character_cost_multiplier` da `GET /v1/models` |
| 9 | Pro = $99/mese, "storicamente 500.000 crediti" | **CORRETTO** | https://elevenlabs.io/pricing : Pro "600k credits per month"; https://elevenlabs.io/pricing/api : Pro 990.000 caratteri inclusi (v3 / Multilingual v2). Valore reale dell'account: NON VERIFICABILE senza chiave |
| 10 | Concorrenza Pro: 10 (Multilingual v2 e altri), 20 (Flash/Turbo) | CONFERMATO | tabella della pagina Models (Pro: 10 / 20 / STT 40 / STT realtime 30) e help "How many Text to Speech requests" (Pro: Flash and Turbo 20, All Others 10) |
| 11 | Flash v2.5 non normalizza i numeri; `apply_text_normalization:"on"` solo Enterprise per v2.5 | CONFERMATO | pagina Models: "By default, normalization is disabled for Flash v2.5 ... Enterprise customers can now enable ..." |
| 12 | v3: cursore Creative/Natural/Robust, audio tag fra quadre, niente `<break>` SSML, PVC non ottimizzate | CONFERMATO | https://elevenlabs.io/docs/overview/capabilities/text-to-speech/best-practices |
| 13 | v3 non supportato dal WebSocket TTS | CONFERMATO | "That endpoint does not support the `eleven_v3` model": https://elevenlabs.io/docs/eleven-api/guides/how-to/websockets/realtime-tts |
| 14 | `eleven_v3_conversational` utilizzabile su `/v1/text-to-speech` | NON VERIFICABILE | La pagina Models lo lega ad Agents e al "Text to Dialogue WebSocket"; nella OpenAPI compare solo nell'enum dei modelli per gli agenti |
| 15 | Voci default/IVC piu' rapide delle PVC | CONFERMATO | pagina latency optimization |

### 6.3 Voci, abbonamento, errori

| # | Affermazione | Esito | Evidenza |
|---|---|---|---|
| 1 | "All our Default voices will expire on December 31, 2026" e "only available for accounts that were created before March 2026"; tabella Roger -> Darian, Sarah -> Talia, George -> Eldrin | CONFERMATO (testuale) | https://elevenlabs.io/docs/help-center/product/voices/my-voices/what-are-default-voices |
| 2 | `GET /v2/voices`: parametri e campi di risposta | CONFERMATO, con aggiunte | OpenAPI: in piu' `gender`, `age`, `language`, `accent`, `use_cases`, `min_notice_period_days`, `include_custom_rates`, `include_live_moderated`, `high_quality`, `fine_tuning_state`, `collection_id`; `voice_type` comprende `non-community` |
| 3 | `GET /v1/voices` "legacy" | **CORRETTO** | Non deprecato; parametro `show_legacy`; "Stops working once the user's workspace exceeds 500 voices" (OpenAPI) |
| 4 | `GET /v1/shared-voices` e relativi filtri/campi | CONFERMATO | OpenAPI; https://elevenlabs.io/docs/api-reference/voices/voice-library/get-shared |
| 5 | `POST /v1/voices/add/{public_user_id}/{voice_id}` con `{"new_name": ...}` -> `{"voice_id": ...}` | CONFERMATO | https://elevenlabs.io/docs/api-reference/voices/voice-library/share (in piu': `bookmarked`, default true) |
| 6 | ID delle voci italiane | **CORRETTO** | 20 voci con ID estratte dall'HTML di https://elevenlabs.io/text-to-speech/italian ; confermati MarcoTrox, Marco, Antonio Farina; diversi da quanto scritto Carmelo La Rosa e Chris Basetta; non confermabili Lorena, Giusy, Vera, Ginevra, Alessandra (solo fonte terza). Disponibilita' attuale, `rate`, `notice_period`: NON VERIFICABILE senza chiave |
| 7 | `GET /v1/user/subscription` e campi | CONFERMATO, con correzione | OpenAPI `ExtendedSubscriptionResponseModel`: `max_character_limit_extension` e `allowed_to_extend_character_limit` sono deprecati; nuovo `max_credit_limit_extension` |
| 8 | Forma del corpo d'errore `{detail:{type,code,message,status,request_id,param}}` | CONFERMATO | https://elevenlabs.io/docs/eleven-api/resources/errors |
| 9 | 401 `invalid_api_key` / `missing_api_key`; 402 `insufficient_credits`; 403 `feature_not_available`...; 404 `voice_not_found`; 429 `rate_limit_exceeded` / `concurrent_limit_exceeded` / `system_busy`; 500; 503 | CONFERMATO | stessa pagina |
| 10 | Nomi storici (`quota_exceeded`, 429 `too_many_concurrent_requests`) | CONFERMATO e ampliato | https://elevenlabs.io/docs/help-center/technical/api-error-code-400-or-401 e https://elevenlabs.io/docs/help-center/technical/api-error-code-429 : le due famiglie di nomi convivono nelle fonti ufficiali; il parser deve accettarle entrambe |
| 11 | `invalid_audio_format` come errore per `output_format` | **CORRETTO** | il codice giusto e' `invalid_output_format` |
| 12 | Chiavi API con permessi per endpoint e tetto di crediti | CONFERMATO | https://elevenlabs.io/docs/overview/administration/workspaces/api-keys (anche allowlist IP -> 403) |

### 6.4 NAudio (nuget.org + prove sul Zenbook)

Dati da `api.nuget.org` (indice delle versioni e registrazione del pacchetto), oggi:

| Versione di `NAudio` | Pubblicata (UTC) | Note |
|---|---|---|
| 2.2.1 | 2023-09-04 | |
| 2.3.0 | 2026-03-12 | |
| **2.4.0** | **2026-08-26** | **ultima stabile 2.x**; `NAudio.Wasapi` 2.4.0: `netstandard2.0` + `net9.0-windows10.0.26100` |
| 3.0.0 | 2026-08-15 | minimo `net9.0` |
| 3.0.1 | 2026-08-18 | non citata nella prima stesura |
| **3.1.0** | **2026-09-07** | ultima stabile in assoluto; note di rilascio con cambi di rottura |
| 3.1.1-preview.1 | 2026-09-08 | anteprima |

- "Esiste davvero una NAudio 3.x con `WasapiPlayer` su nuget.org?" **Si' - CONFERMATO e MISURATO.** Ho scaricato `naudio.wasapi.3.1.0.nupkg`: contiene `lib/net9.0/NAudio.Wasapi.dll` con i tipi `NAudio.Wave.WasapiPlayer`, `WasapiPlayerBuilder` (`WithSharedMode`, `WithExclusiveMode`, `WithEventSync`, `WithPollingSync`, `WithLatency(int)` "Default is 200ms", `WithDefaultDeviceStreamRouting`, `WithLowLatency`, `WithCategory`, `Build`, `BuildAsync`), `WasapiRecorder`, `WasapiRecorderBuilder`. `WasapiPlayer` espone `Init(IWaveProvider)`, `Play`, `Stop`, `Pause`, `PlaybackStopped`, `Dispose`, `DisposeAsync`.
- **MISURATO** (progetto `net10.0-windows10.0.19041.0`, SDK 10.0.401, processo Arm64, uscita "Speakers (Qualcomm Aqstic Audio Adapter Device)", mixer 48 kHz float stereo):
  - NAudio 3.1.0: restore e build senza avvisi; `WasapiPlayerBuilder().WithSharedMode().WithEventSync().WithLatency(100).Build()` + `BufferedWaveProvider` 44,1 kHz 16 bit mono: suona; `Play()` -> attesa 200 ms -> `Stop()` ritorna a 217 ms dall'inizio (Stop immediato), evento `PlaybackStopped` ricevuto.
  - NAudio 2.4.0: stesso test con `new WasapiOut(AudioClientShareMode.Shared, true, 100)`: suona, `Stop()` ritorna a 225 ms, `PlaybackStopped` ricevuto; l'assembly caricato e' l'asset `netstandard2.0`.
  - Lo sketch 2.10 della prima stesura **non compilava**: `BufferDuration` e' di sola lettura in entrambe le versioni (CS0200). Corretto.
  - Con la 3.1.0 l'uso di `WasapiOut`, `WasapiCapture`, `WaveOutEvent`, `WaveInEvent` produce l'avviso CS0618 (obsoleti ma presenti).
- `NLayer.NAudioSupport` 3.0.0 (`net9.0`, dipende da `NLayer` 3.0.0 e `NAudio.Core` >= 3.0.1): CONFERMATO dal `.nuspec`.
- **Quale versione e' la scelta prudente?** La **2.4.0**, con versione esatta nel `.csproj`. Motivi: stessa funzionalita' per il nostro uso (uscita WASAPI condivisa con conversione di formato, Stop immediato: misurato), API invariata da anni e coperta da tutta la documentazione esistente, ramo ancora mantenuto (due rilasci nel 2026). La 3.1.0 funziona su questa macchina ed e' un'alternativa legittima, ma ha avuto tre rilasci stabili in 23 giorni, uno dei quali con cambi di rottura, un'anteprima correttiva il giorno dopo l'ultimo, e un bug di `WasapiPlayer` (issue 1442, aperta e chiusa il 10/9/2026 secondo l'API pubblica di GitHub) la cui correzione non e' ancora in nessun pacchetto pubblicato: meglio lasciarla assestare e passarci dietro `IAudioSink`. Ne' la 2.4.0 ne' la 3.1.0 sono state provate su enunciati lunghi o con cuffie Bluetooth.

Codice delle prove (usa e getta): `%LOCALAPPDATA%\Temp\claude\C--Users-Angelo-Desktop-Matteo\d74c920e-45bd-48a5-abcd-2768715aa453\scratchpad\spikes\factcheck-elevenlabs\` (`ApiCheck2`, `ApiCheck3`, `ApiCheck3b`, piu' `openapi.json` e le pagine HTML scaricate).

### 6.5 Cosa resta non verificabile senza chiave API

1. Se `pcm_44100` (e `pcm_48000`) viene davvero accettato con il piano dell'account, e quale errore arriva altrimenti.
2. `character_limit` reale (600k crediti, 990k caratteri o vecchio valore) e listino dell'abbonamento.
3. Forma reale degli errori di quota e concorrenza (nomi nuovi o storici) e codice HTTP associato.
4. Effetto di `language_code` su Multilingual v2 (ignorato?) e su v3 (onorato?).
5. `eleven_v3_conversational` sull'endpoint HTTP di streaming.
6. Disponibilita', `rate`, `notice_period` e tipo (PVC?) delle voci italiane proposte; resa all'ascolto.
7. Presenza degli header `character-cost` e di concorrenza sulla risposta di `/stream`.
8. Esistono ancora le voci default sull'account (creato prima di marzo 2026?).
