# PuntaEAscolta.Speech: note di implementazione

Stato al 22/09/2026: completo. `PuntaEAscolta.Speech` e `PuntaEAscolta.Speech.Tests` compilano con 0 errori e 0 avvisi; **117 test verdi** (15 esecuzioni di fila senza fallimenti, circa 1 s ciascuna). Nessuna chiamata reale a ElevenLabs: tutto passa da un `HttpMessageHandler` finto.

## Cosa c'è

| File | Contenuto |
|---|---|
| `SpeechService.cs` | `SpeechService : ISpeechServiceWithOutcome`. Costruttore di DESIGN `(cloud?, local, player, cache, settings, log)` più un sovraccarico con `TimeProvider`. |
| `SpeechOutcome.cs` | `enum SpeechOutcome { Completed, Stopped, Superseded, Failed }` e `interface ISpeechServiceWithOutcome : ISpeechService` (vedi "Proposte di contratto"). |
| `Playback/CloudCircuitBreaker.cs` | Interruttore automatico (interno). |
| `Playback/CloudAudioBuffer.cs` | Stream fra rete e lettore (interno): scarica con un'attività propria, segnala i primi byte, tiene la copia per la cache, sopravvive allo Stop. |
| `Dictation/DictationService.cs` | `DictationService : IDictationService`. Costruttore di DESIGN più sovraccarico con `TimeProvider`. |
| già presenti | `ElevenLabsSynthesizer`, `ElevenLabsAccountClient`, `ElevenLabsModels`, `ElevenLabsErrors`, `SpeechCache`, `ElevenLabsSpeechToText`, `VoiceCommands`, eccezioni tipizzate: tenuti, vedi "Correzioni". |

## SpeechService

- **Fornitore**: `Windows` sempre locale. `Auto` ed `ElevenLabs` si comportano uguale: cloud se `IsConfigured` e interruttore chiuso, altrimenti locale (anche con `ElevenLabs` non si resta mai muti). `SpeechKind.System` e, con `DocumentsWithLocalVoiceOnly`, i testi `Sensitive` vanno sempre in locale e non toccano né rete né cache.
- **Cache**: solo se il cloud è un `ElevenLabsSynthesizer` (chiave da `DescribeForCache`) e `CacheEnabled`. Si consulta **per prima**, anche con il cloud sospeso o senza chiave: l'audio in cache è legittimo. Mai con fornitore `Windows`.
- **Primo audio**: si aspetta la richiesta cloud **fino ai primi byte PCM** (non alle sole intestazioni) per `FirstAudioTimeoutLabelMs` (etichette) o `FirstAudioTimeoutSentenceMs` (frasi e blocchi), limitati a 100-30.000 ms; poi voce locale subito. Conta come `Timeout` per l'interruttore. Con la cache attiva la richiesta abbandonata **non** viene annullata: finisce in sottofondo e va in cache (vedi "Audio già pagato"); senza cache viene annullata e la connessione chiusa.
- **Audio già pagato** (secondo giro della revisione): quando un pezzo va al cloud con la cache attiva (chiave di cache calcolata), la richiesta non è legata alla lettura ma alla vita del servizio. Se viene abbandonata (Stop o sostituzione prima dei primi byte, pezzo successivo preparato in anticipo e non suonato, tempo massimo del primo audio superato) `FinishCloudInBackground` la lascia finire senza suonare, al massimo 15 s dall'abbandono (primi byte e fine dello stream insieme) e fino alla chiusura del servizio, e salva in cache solo l'audio completo; allo scadere la annulla. La risposta tardiva non tocca l'interruttore (niente `RecordSuccess` dopo un tempo scaduto) e i suoi errori vanno solo nel log di dettaglio. Stop resta immediato: la lettura non aspetta mai queste richieste.
- **Copia per la cache**: `CloudAudioBuffer` legge la rete con un token proprio (annullare una lettura di `HttpClient` chiude la connessione) e passa i byte al lettore. Si salva solo se lo stream finisce normalmente. Dopo uno Stop lo scaricamento prosegue in sottofondo fino a 15 s, poi si salva o si scarta. Rete ferma per 8 s a metà stream: errore, il lettore riceve fine stream (non resta appeso). Errore di rete a metà: il pezzo finisce lì, niente cache, un errore per l'interruttore.
- **Interruttore**: `InvalidKey` → cloud spento finché non arriva `ISettingsStore.Changed` (qualunque salvataggio). `QuotaExceeded` → 30 min. `Network`, `Timeout`, `Server` e anche `Configuration` (voce inesistente, 404: si ripeterebbe uguale a ogni clic) → al 3° consecutivo 60 s; se la prima prova dopo la pausa fallisce, 5 min, e così via finché una richiesta riesce. `RateLimited` e `NotConfigured` → solo ripiego per quella lettura. Durante la sospensione nessun tentativo in linea. Gli errori di richieste partite prima della sospensione non la allungano.
- **Pezzi**: `Chunks` in ordine (pezzi vuoti saltati; se sono tutti vuoti si usa `Text`). Il pezzo successivo si prepara appena il corrente ha il suo audio (cloud o locale): mai più di uno in anticipo; se la lettura si ferma, il pezzo preparato finisce in cache (con la cache attiva) invece di essere buttato. Un pezzo non sintetizzabile si salta.
- **Stop e sostituzione**: `Stop()` annulla la lettura, chiama `IAudioPlayer.Stop()` e rende subito `IsSpeaking = false` con `SpeakingChanged(false)`; idempotente. Una nuova `SpeakAsync` annulla la precedente e ne attende la fine (al massimo 2 s, poi `player.Stop()` forzato); `SpeakingChanged` non sfarfalla (true... false una volta sola). `IsSpeaking` è vero anche durante l'attesa del primo audio, così un secondo clic ferma anche una lettura non ancora partita.
- **Eccezioni**: `SpeakAsync` non solleva mai, salvo `OperationCanceledException` quando è il **token del chiamante** a essere annullato. Stop e sostituzione fanno terminare normalmente la chiamata (esito `Stopped` / `Superseded` con `SpeakWithOutcomeAsync`).
- **Volume**: letto dalle impostazioni a ogni pezzo, limitato a 0-1 (NaN → 1).
- **Log**: testo letto solo se `IsDebugEnabled`; tempi del primo audio a livello Debug; cambi di stato dell'interruttore a Info/Warn.
- In più: `CloudSuspendedUntil` (null se il cloud è disponibile, `DateTimeOffset.MaxValue` se attende il cambio delle impostazioni), utile alla finestra impostazioni.

## DictationService

Stati: `Idle → Recording → Transcribing → ReadingBack → Idle`.

- `ToggleAsync` in `Idle`: se `!Dictation.Enabled` o `!stt.IsConfigured` dice "Dettatura non disponibile" e resta `Idle`. Altrimenti dice "Registro" (aspettando la fine, così la parola non finisce nel microfono) e poi `recorder.Start(MicrophoneDeviceId o null)`; se il microfono configurato manca riprova con il predefinito; se non c'è microfono dice "Microfono non disponibile". Timer `MaxSeconds` (limitato a 1-600 s) che ferma da solo.
- `ToggleAsync` in `Recording`: ferma il registratore, passa a `Transcribing` e **ritorna subito**; trascrizione, rilettura e inserimento girano in un'attività propria (il token del chiamante serve solo per l'avvio). Segnale di fine registrazione: "Ricevuto", detto mentre la trascrizione è in corso (DESIGN 3.2 chiede segnali di inizio e fine).
- Trascrizione con tempo massimo 30 s → "Dettatura non riuscita". `SpeechToTextException` → "Dettatura non riuscita" (o "Dettatura non disponibile" se `NotConfigured`). Testo vuoto → "Non ho capito".
- Comandi a frase intera (`VoiceCommands`): cancella → `PressBackspaceAsync(ultimoInserito.Length)` e "Cancellato" (o "Niente da cancellare"); a capo → `PressEnterAsync` e "A capo" (un "cancella" subito dopo toglie solo l'a capo); rileggi → rilegge l'ultimo testo dettato (o "Niente da rileggere").
- Testo normale: con `ReadBack` lo rilegge (`Sentence`, `Sensitive: true`, lingua dalla dettatura) in `ReadingBack`, poi 600 ms di pausa, poi con `AutoInsert` `TypeTextAsync(testo + " ")`. Senza rilettura inserisce subito.
- **Annullare**: un richiamo durante `Transcribing` o `ReadingBack` (pausa compresa) → "Annullato", niente inserimento. Anche uno **Stop della voce durante la rilettura** (clic della rotellina: l'orchestratore chiama `ISpeechService.Stop()`) annulla con "Annullato". Se invece la rilettura viene **sostituita da un'altra lettura** si annulla in silenzio, per non parlarci sopra. `Cancel()`: in registrazione scarta l'audio e dice "Annullato"; nelle fasi successive come un richiamo.
- `AutoInsert = false`: dopo la rilettura si aspetta un nuovo richiamo per 15 s (conferma e inserisce); senza conferma "Annullato". Un richiamo durante la rilettura vera e propria annulla, come sempre.
- Nessun taglio sulle pause (nessun VAD). Richiami durante il segnale "Registro" ignorati. `Dispose` ferma il microfono e chiude in silenzio.
- Log: mai il testo dettato salvo Debug; durata registrata, numero di caratteri inseriti, comandi riconosciuti.

## Correzioni al codice già scritto

- La chiave API poteva arrivare nei log se il server (o un proxy) la rimandava nel corpo dell'errore: il messaggio del server finiva nell'eccezione e da lì in `Warn`. Aggiunti `ElevenLabsErrors.Redact` (usato da `ElevenLabsSynthesizer` e `ElevenLabsAccountClient`) e `ScribeError.Redact` in `ElevenLabsSpeechToText`: la chiave diventa `***`. Coperto da test in entrambi i client.
- Nient'altro da correggere: il resto è coperto dai test così com'era.

## Proposte di contratto (PuntaEAscolta.Core, non modificato)

1. Spostare in Core `SpeechOutcome` e `Task<SpeechOutcome> SpeakWithOutcomeAsync(SpeechRequest, CancellationToken)` (oggi `ISpeechServiceWithOutcome` in Speech). La dettatura ne ha bisogno per distinguere "rilettura finita", "fermata dall'utente" e "sostituita". Con un `ISpeechService` qualunque funziona lo stesso, ma senza questa distinzione (la rilettura conta sempre come finita).
2. Documentare in `ISpeechService.SpeakAsync` che Stop e sostituzione fanno terminare la chiamata normalmente e che solo l'annullamento del token del chiamante produce `OperationCanceledException`.

## Limiti

- Nessuna prova con la rete e la chiave vere: forma reale degli errori, accettazione di `pcm_44100`, tempi reali di ElevenLabs restano da misurare (vedi `docs/research/elevenlabs-tts.md` sez. 6.5).
- Tutto l'audio di un pezzo cloud resta in memoria finché il pezzo non è finito (circa 88 KB/s a 44,1 kHz: 2-3 MB per 400 caratteri; un testo non spezzato di 3000 caratteri arriva sui 20 MB).
- La cache non distingue un `Stop` da un errore del lettore: in entrambi i casi lo scaricamento prosegue in sottofondo.
- Un salvataggio qualunque delle impostazioni (anche la pausa) riapre l'interruttore: al massimo una richiesta in più, subito rifiutata.
- La pausa di 600 ms dopo la rilettura e la parola "Ricevuto" sono scelte mie: facili da cambiare (`DictationService.ReadBackGrace`, `DictationPhrases`).

## Misure (test, Snapdragon X, Debug)

- Hit della cache: riproduzione avviata **1,7 ms** dopo la chiamata a `SpeakAsync`.
- Cloud muto con tempo massimo 200 ms: voce locale dopo **214 ms**.
- `Stop()` durante la lettura: `SpeakAsync` termina in **meno di 1 ms**.

## Come provare

```
"C:\Users\Angelo\AppData\Local\Microsoft\dotnet\dotnet.exe" test tests\PuntaEAscolta.Speech.Tests --artifacts-path <cartella propria>
```

Suddivisione: `ElevenLabsSynthesizerTests` (forma della richiesta, `language_code` solo per Flash, errori nelle due famiglie di nomi, ripiego su `pcm_24000`), `ElevenLabsSpeechToTextTests` (campi multipart, `scribe_v2` → `scribe_v1`, ripiego WAV, errori), `ElevenLabsAccountClientTests`, `SpeechCacheTests`, `CloudCircuitBreakerTests`, `SpeechServiceTests` (cache, ripiego, interruttore, pezzi, Stop a metà stream, chiave mai nei log), `VoiceCommandsTests`, `DictationServiceTests` (macchina a stati, comandi, annullamenti, durata massima, tempo massimo di trascrizione). Le prove di tempo usano un `ManualTimeProvider`; nessun suono reale.

## Per l'integratore (PuntaEAscolta.App)

```csharp
var cacheDir = Path.Combine(settings.DataDirectory, "cache");
var cache = new SpeechCache(cacheDir, () => settings.Current.Speech.CacheMaxMegabytes, log);
var elevenLabs = new ElevenLabsSynthesizer(http, settings, protector, log);
var speech = new SpeechService(elevenLabs, windowsVoice, player, cache, settings, log);
var stt = new ElevenLabsSpeechToText(http, settings, protector, log);
var dictation = new DictationService(recorder, stt, injector, speech, settings, log);
```

- `CacheEnabled` è rispettato dal servizio vocale; per svuotare: `cache.Clear()`, dimensione: `cache.GetSizeBytes()`, dopo aver abbassato il limite: `cache.Trim()`.
- Un solo `HttpClient` condiviso (SocketsHttpHandler con `ConnectTimeout` di qualche secondo). Il `Timeout` di `HttpClient` vale solo fino alle intestazioni (`ResponseHeadersRead`): i tempi veri li governa il servizio.
- `SpeechService` e `DictationService` non chiudono le dipendenze ricevute (lettore, registratore, sintetizzatori): le chiude la composizione.
- L'orchestratore attuale è compatibile: lancia `ToggleAsync` su un'attività propria con il token di chiusura e ferma solo la propria voce. Se durante `Recording` l'utente clicca per leggere, la lettura finisce nel microfono: valutare di ignorare le letture mentre `dictation.State == Recording`.

## Revisione del 22/09/2026 (vedi `revisione.md`)

- Chiave API nei log: i corpi d'errore non JSON venivano troncati (200 caratteri per Scribe, 2000 per la sintesi) PRIMA di togliere la chiave; un taglio a metà chiave ne lasciava un pezzo (fino a 30 caratteri su 37 nella prova). Ora si toglie la chiave dal corpo intero e solo dopo si tronca (`ElevenLabsErrors.Redact` tronca sempre a 2000 caratteri).
- Dettatura: se l'annullamento dell'utente e la fine della rilettura si incrociavano (un thread ha deciso l'annullamento ma non ha ancora annullato il token), la pipeline poteva inserire il testo appena annullato. `Session.Cancel` chiamato mentre un altro thread sta annullando ora aspetta (al massimo 1 s) che il token risulti annullato.
- ~~Rimasto aperto: il pezzo successivo già scaricato in anticipo viene scartato se la lettura si ferma e, alla rilettura, pagato di nuovo.~~ Corretto nel secondo giro: vedi "Audio già pagato" sopra. Test: `Stop_during_first_chunk_puts_the_prefetched_second_chunk_in_cache`, `Stop_before_the_first_bytes_lets_the_paid_request_finish_into_the_cache`, `Timeout_counts_until_the_first_pcm_bytes_and_the_late_audio_still_reaches_the_cache`, `Abandoned_request_that_never_sends_audio_is_dropped_after_the_background_limit`, `Late_cloud_audio_after_a_timeout_does_not_reset_the_circuit_breaker`, `Without_cache_an_abandoned_request_is_still_cancelled`.
- Costo: fino a due connessioni in più aperte in sottofondo per lettura fermata (pezzo corrente e successivo), per al massimo 15 s ciascuna.
