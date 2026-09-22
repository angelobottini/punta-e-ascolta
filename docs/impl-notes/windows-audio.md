# PuntaEAscolta.Windows.Audio: note di implementazione

Stato al 22/09/2026: modulo completo e verificato su questo PC (Snapdragon X, ARM64, Windows 11 25H2, altoparlanti Qualcomm Aqstic, NAudio 2.4.0). Compila con 0 errori e 0 avvisi. Self-check: **103/103 controlli superati** (vedi "Come provare").

## Cosa c'è

| File | Contenuto |
|---|---|
| `NAudioPlayer.cs` | `IAudioPlayer`. Un dispositivo WASAPI condiviso nuovo per ogni lettura, pre-buffer, fine naturale, Stop immediato, ripiego WaveOut. `WarmUpAsync`, `KeepWarmDuration`, `IsEngineWarm`. |
| `AudioEngineKeepWarm.cs` (interno) | Stream WASAPI inizializzato e **mai avviato** che tiene pronto il motore audio (vedi Decisioni). |
| `WindowsVoiceSynthesizer.cs` | `ISpeechSynthesizer` con voci OneCore, SSML, `static GetVoices()`, `WarmUpAsync`. |
| `WindowsVoiceInfo.cs` | `record (Id, DisplayName, Language, Gender)`; `Gender` è "Female"/"Male", da tradurre in chi lo mostra. |
| `WavHeader.cs` (interno) | Lettura RIFF: formato reale, blocco `data`, float riconosciuto, solo frame interi. |
| `NAudioRecorder.cs` | `IAudioRecorder`: elenco microfoni, cattura WASAPI condivisa, uscita 16 kHz mono 16 bit. |
| `Pcm16kMonoConverter.cs` (interno) | Catena di conversione del microfono (media canali, WDL, 16 bit) con limite di memoria. |
| `MonoMixSampleProvider.cs` (interno) | Media dei canali, robusto alle letture parziali. |
| `DpapiSecretProtector.cs` | `ISecretProtector`: DPAPI utente corrente + entropia fissa, Base64; `Unprotect` restituisce null su qualsiasi errore. |
| `SystemSounds.cs` | Segnali 22,05 kHz mono 16 bit, picco 0,25: `DictationStart`, `DictationStop`, `Error`, `Ack`. Restituiscono `(byte[] Pcm, PcmFormat Format)`. |

## NAudioPlayer

- `PlayAsync`: nuova sessione (interrompe la precedente); dispositivo predefinito (`Render`, `Role.Console`) riletto ogni volta; `WasapiOut` condiviso, eventi, latenza 100 ms; aperto in parallelo alla lettura dello stream.
- Lettura: attività propria, blocchi da 8 KB, solo frame interi (i byte spaiati restano per il giro dopo), `BufferedWaveProvider` da 10 s con contropressione (attesa di 20 ms quando manca spazio). Volume con `VolumeSampleProvider` (0..1, mai il volume di sistema).
- Avvio a 120 ms di audio nel buffer oppure a fine stream. Mentre lo stream è aperto un buco di rete diventa silenzio (`ReadFully = true`).
- Fine naturale: 50 ms di silenzio in coda, poi `ReadFully = false`; il dispositivo si ferma da solo e `PlaybackStopped` completa `PlayAsync`. Guardiano: se il dispositivo non parte entro 10 s o non segnala la fine entro durata + 1,3 s, arresto forzato con avviso.
- Stream vuoto: `PlayAsync` ritorna subito senza aprire il dispositivo. Stream senza dati per 10 s: si considera finito (avviso). Errore di lettura a metà: si suona quanto ricevuto (errore nel log).
- `Stop()` e annullamento del token: non bloccanti, chiudono dispositivo e lettura; il `Dispose` del dispositivo (fino a 70 ms) avviene in sottofondo. Una nuova `PlayAsync` può partire subito.
- Guasto WASAPI all'apertura o durante la riproduzione: un solo ripiego su `WaveOutEvent` (150 ms, 3 buffer) sui dati rimasti.
- **Eccezioni: `PlayAsync` non lancia mai** (salvo `ArgumentNullException` per argomenti null). Fine, Stop, annullamento, formato non valido, stream illeggibile, nessun dispositivo: il Task si completa normalmente, gli errori vanno nel log. Chi suona più pezzi controlla il proprio token dopo ogni chiamata (`SpeechService` lo fa già con `token.ThrowIfCancellationRequested()`).
- `IsPlaying` è vero dall'inizio di `PlayAsync` (pre-buffer incluso) fino a fine, Stop o errore; falso subito dopo `Stop()`.
- Log: tempi solo a livello Debug; mai il testo (il lettore non lo conosce).

## WindowsVoiceSynthesizer

- Voce da `Speech.WindowsVoiceName`: Id, nome esatto o parte del nome ("Elsa"); se non installata, avviso una sola volta e prima voce it-IT, poi la predefinita. Con `LanguageHint == "en"` e una voce inglese installata si usa quella (su questo PC non ce ne sono).
- SSML 1.0 con `xml:lang` della voce e `<prosody rate="1.5">` da `Speech.WindowsRate` (0,5-3,0; nessun prosody a 1,0). Testo escapato (`& < > " '`), caratteri di controllo e surrogati spaiati sostituiti da spazio. Se l'SSML viene rifiutato: ripiego su testo semplice con `Options.SpeakingRate` (riportata a 1,0 prima di ogni SSML, per non sommare le due velocità).
- WAV completo in memoria (la sintesi OneCore non è in streaming), intestazione letta per il formato reale (16 kHz mono 16 bit per Elsa e Cosimo); eventuale float convertito a 16 bit.
- **Silenzio iniziale tolto**: le voci OneCore iniziano con 100-190 ms sotto -60 dBFS; si lascia 20 ms prima del primo campione sopra 32/32768. Guadagno di latenza percepita 80-170 ms.
- Testo da `Text`, oppure dai `Chunks` uniti se `Text` è vuoto. Testo vuoto: stream vuoto. Eccezioni: `OperationCanceledException` all'annullamento, `InvalidOperationException` se non c'è alcuna voce o il WAV è irriconoscibile, errori WinRT. Il testo va nel log solo a livello Debug.

## NAudioRecorder

- `GetDevices()`: microfoni attivi; `IsDefault` = predefinito per le comunicazioni (poi multimediale, console).
- `Start(deviceId)`: Id WASAPI o predefinito; `InvalidOperationException` se nessun microfono; un secondo `Start` è ignorato con avviso. Conversione al volo nel thread di cattura, memoria limitata a 5 minuti (9,6 MB), avviso una volta al superamento. `Stop()` restituisce il PCM (vuoto se non si registrava).
- **Non provato con il microfono** (per istruzione: nessuna registrazione). Provati l'elenco dei dispositivi e la catena di conversione con audio sintetico.

## Decisioni

1. **Stream di mantenimento** (`KeepWarmDuration`, predefinito 3 minuti dopo l'ultima lettura, `TimeSpan.Zero` lo disattiva): su questo PC l'apertura di un `WasapiOut` costa 200-290 ms se nessun altro stream esiste sul dispositivo, 20-55 ms se esiste un `AudioClient` inizializzato e mai avviato. Nessun suono, nessun thread, nessuna elaborazione. Si ricrea se cambia il dispositivo predefinito, si chiude per inattività o con `Dispose`. Rispetta "un dispositivo nuovo per ogni lettura". Effetti collaterali possibili: l'app compare nel mixer del volume durante quei minuti; un programma che chiede l'uso esclusivo del dispositivo lo trova occupato. Consumo non misurato.
2. **50 ms di silenzio in coda**: senza, NAudio ferma lo stream prima degli ultimi ~30 ms (toni a bordi netti: 385 ms uditi su 400 ms; con 30 ms o più di silenzio 414,5 ms, cioè intatti più la coda di risonanza del dispositivo). Costo: `PlayAsync` ritorna circa 50 ms dopo l'ultimo suono.
3. `Stop()` chiama direttamente `WasapiOut.Stop()`, che in NAudio 2.4 ritorna in 2-5 ms: non serve azzerare il volume dello stream.
4. Buffer di 10 s (non 30): 880 KB a 44,1 kHz; la contropressione rallenta la lettura della rete, che resta comunque più veloce del tempo reale.
5. Letture di rete con `WaitAsync(10 s)` e array privato (non del pool): una lettura abbandonata non può scrivere in memoria altrui; il suo eventuale errore viene osservato.

## Misure (22/09/2026, volume 0,15, loopback WASAPI come sonda)

| Misura | Risultato |
|---|---|
| `WarmUpAsync` lettore + voce in parallelo, primo nel processo | 260-560 ms |
| `GetVoices()` | 12-113 ms la prima volta, poi 0,8 ms |
| Sintesi "Prova della voce di Windows" | Elsa 80-280 ms (prima), Cosimo 50-80 ms; etichetta breve a motore caldo 5 ms |
| Velocità SSML (durata rispetto a 1,0) | 1,5 → 1,55×; 0,7 → 0,72×; 2,5 → 2,38× |
| PlayAsync → `Play()` del dispositivo | senza mantenimento 196-292 ms; con mantenimento 24-58 ms (la prima 220-280) |
| PlayAsync → primo suono nel mix (voce, mantenimento attivo, silenzio tolto) | 72-95 ms |
| `Stop()`: ritorno / PlayAsync completato / ultimo suono nel mix | 2-5 ms / 2-5 ms / 27-36 ms |
| Annullamento via token | Cancel 2-3 ms, PlayAsync completato 2-16 ms, ultimo suono 29-34 ms |
| `Stop()` da un thread STA nuovo | 8-12 ms (creazione del thread compresa), ultimo suono 34-41 ms |
| Nuova PlayAsync subito dopo Stop | primo suono 94 ms dopo |
| Fine naturale | PlayAsync ritorna fra 8 ms prima e 51 ms dopo l'ultimo suono nel mix; coda intatta (toni: 414,5 ms uditi su 400 + risonanza, come con 100 ms di silenzio aggiunto) |
| 2 × 20 cicli play/stop (ritardi 0-300 ms) | nessuna eccezione né errore; handle 514 → 516 → 518, thread 23 → 23, memoria gestita +1 KB |
| PCM 44,1 kHz a pezzi 1,5-6 KB con ritardi 10-60 ms | fine rilevata; con un blocco di rete di 400 ms: vuoto udibile di 333-363 ms, poi prosegue |
| Contropressione (30 s disponibili subito) | dopo 1,5 s letti 11,5 s (10 s di buffer + consumato) |
| Rete bloccata | fine dopo 10,1 s |
| DPAPI | andata e ritorno, dato alterato / Base64 errato / altra entropia / vuoto → null |
| Microfoni | 1: Microphone Array (Qualcomm Aqstic ACX), predefinito |
| Catena microfono (sintetica) | 48 kHz float stereo 3 s → 3,000 s a 16 kHz, 1000 Hz conservati, RMS corretto; pacchetto da 5 s senza perdite; limite rispettato |

## Limiti

- Registrazione reale dal microfono non provata (solo elenco e conversione).
- Ripiego su `WaveOutEvent` non provocato in prova (richiederebbe di guastare WASAPI); `WaveOutEvent` su questo PC funziona (ricerca del 21/09: Init circa 100 ms).
- Cambio del dispositivo predefinito durante l'uso non provato (non si cambiano le impostazioni di sistema): il codice rilegge il predefinito a ogni lettura e ricrea lo stream di mantenimento.
- `docs/research/spike-audio.md` e `docs/research/probe/audio-sample`, citati in DESIGN 3.7, non esistono; le misure precedenti sono nei log del prototipo (cartella temporanea di sessione).

## Proposte di contratto (Core congelato, nessuna modifica fatta)

- `IAudioPlayer.PlayAsync` potrebbe restituire l'esito (`Completed`, `Stopped`, `Failed`) invece di `Task`, per non dover dedurre la fermata dal token.
- `PcmFormat` non distingue float da interi: qui si converte tutto in interi.
- Nessuna impostazione per `KeepWarmDuration`: se serve, `SpeechSettings.KeepAudioWarmMinutes`.

## Per l'integratore (App)

- `new NAudioPlayer(log)`, `new WindowsVoiceSynthesizer(settings, log)`, `new NAudioRecorder(log)`, `new DpapiSecretProtector()`.
- All'avvio, fuori dal thread UI: `await Task.WhenAll(player.WarmUpAsync(), windowsVoice.WarmUpAsync())` (apre anche lo stream di mantenimento).
- Segnali: `var (pcm, format) = SystemSounds.DictationStart(); await player.PlayAsync(new MemoryStream(pcm), format, volume, ct);`
- Finestra impostazioni: `WindowsVoiceSynthesizer.GetVoices()`; salvare in `WindowsVoiceName` l'`Id` oppure il `DisplayName`.

## Come provare

Programma di prova (usa e getta) nella cartella temporanea della sessione, `scratchpad\spikes\audio` (`AudioSpike.csproj`, `SelfCheck.cs`, `Loopback.cs`), con riferimento al progetto:

```
dotnet build <scratchpad>\spikes\audio\AudioSpike.csproj --artifacts-path <scratchpad>\build\windows-audio-spike
set DOTNET_ROOT=C:\Users\Angelo\AppData\Local\Microsoft\dotnet
AudioSpike.exe all            (tutto, circa 60 s, suoni brevi a volume 0,15)
AudioSpike.exe <comando> [debug]
```

Comandi: `warmup voices ssml wav synth rate latency stop cycles concurrent edge net tones dpapi devices chain silence backpressure tail`, più `stall` (10 s) e `rawinit` (misura grezza dell'apertura del dispositivo). I log delle prove sono in `scratchpad\spikes\audio\logs\v2-*.txt`. Le misure di fine e arresto usano una cattura loopback dell'uscita (solo in memoria, nessun microfono); con altro audio in riproduzione le misure non sono affidabili.
