# Punta e Ascolta: progetto tecnico

Documento di riferimento per chi sviluppa i moduli. Le ricerche e le misure che lo giustificano sono in `docs/research/`.

## 1. Scopo

App Windows 11 (Intel x64 e ARM64, cartella portatile) per una persona con esiti di PCI che non legge e non scrive, ma usa Word, Excel, Photoshop e Affinity. **A richiesta** l'app pronuncia ciò che si trova sotto il puntatore.

Requisiti confermati dal committente (Angelo, 21/09/2026):

| Tema | Decisione |
|---|---|
| Attivazione | Tasto configurabile, predefinito **clic della rotellina**. Un secondo clic mentre parla **ferma la voce**. Esc ferma la voce (solo mentre parla). |
| Menu, pulsanti | Si legge solo la voce puntata. Per i pulsanti a sola icona si legge il nome o il suggerimento. |
| Word e documenti | Si legge **la frase** sotto il puntatore, fino al punto o allo stop. |
| Excel | Si legge il contenuto della cella puntata. |
| Immagini (cartelli, magliette) | OCR della **zona attorno al puntatore**. |
| Selezione | "Seleziona e leggi": clic dentro il testo selezionato, oppure scorciatoia dedicata. |
| Emoji ed emoticon | **Mai** pronunciate. |
| Riscontro | **Solo audio**. Nessun riquadro, nessun pannello. |
| Letture automatiche | Nessuna. Si legge solo a richiesta. |
| Voce | Voce di Windows predefinita e di riserva; voci **ElevenLabs** (piano Pro, modello di qualità, multilingue italiano e inglese). Nessun avviso sui crediti. |
| Chiave API | Sul PC dell'utente, cifrata con DPAPI. Mai nei log, mai in chiaro su disco. |
| Dettatura | Da prevedere: sarà la via preferenziale di scrittura. Cuffie con microfono. |
| Rete | Normalmente disponibile. |
| Impostazioni | Le gestisce l'assistente: interfaccia testuale in italiano. |
| Distribuzione | Cartella portatile, nessun installer. Nessun limite di dimensione. |
| Futuro | Possibile versione macOS: la logica deve restare separata dallo strato Windows. |
| NVDA | Non usato. App autonoma. |

## 2. Architettura

Linguaggio: C# su .NET 10. La logica non dipende da Windows; lo strato Windows implementa le interfacce di `PuntaEAscolta.Core`.

```
src/
  PuntaEAscolta.Core                 net10.0   contratti, modelli, impostazioni          (CONGELATO)
  PuntaEAscolta.Logic                net10.0   testo, risoluzione, orchestratore, impostazioni su file, log
  PuntaEAscolta.Speech               net10.0   ElevenLabs TTS e STT, cache audio, servizio vocale, dettatura
  PuntaEAscolta.Ocr.Onnx             net10.0   motore OCR ONNX (PaddleOCR tramite RapidOcrNet)
  PuntaEAscolta.Windows.Input        net10.0-windows10.0.19041.0   hook mouse, scorciatoie, cattura schermo, SendInput, appunti
  PuntaEAscolta.Windows.Automation   net10.0-windows10.0.19041.0   UI Automation COM (UIA3)
  PuntaEAscolta.Windows.Ocr          net10.0-windows10.0.19041.0   Windows.Media.Ocr con pre-trattamento
  PuntaEAscolta.Windows.Audio        net10.0-windows10.0.19041.0   NAudio (riproduzione, microfono), voce di Windows, DPAPI
  PuntaEAscolta.App                  WinExe    icona di notifica, finestra impostazioni WPF, riga di comando, composizione
tests/
  PuntaEAscolta.Logic.Tests, PuntaEAscolta.Speech.Tests   xUnit
tools/publish.ps1                    pubblica le due cartelle portatili
```

Regole per tutti i moduli:

- `PuntaEAscolta.Core` è congelato. Chi ha bisogno di una modifica al contratto NON la fa: la descrive in `docs/impl-notes/<modulo>.md` e si adatta nel proprio modulo.
- Le versioni dei pacchetti NuGet stanno solo in `Directory.Packages.props` (gestione centralizzata). I `.csproj` sono già pronti: non vanno modificati se non per aggiungere file di contenuto del proprio modulo.
- Ogni sviluppatore compila con una cartella di output privata per non interferire con gli altri: `dotnet build <progetto> --artifacts-path <cartella temporanea propria>`.
- Commenti, messaggi all'utente e testi dell'interfaccia in **italiano**. Identificatori in inglese.
- Mai attivare finestre, mai `MessageBox`, mai spostare il focus nel percorso di lettura: i menu aperti devono restare aperti.
- Mai registrare nei log la chiave API. Il testo letto va nei log solo se `ILog.IsDebugEnabled`.
- Niente eccezioni non gestite nei thread di lavoro: si registra e si prosegue. L'app non deve mai bloccare il mouse del sistema.

### 2.1 Flusso di una lettura

```
clic rotellina (hook, inghiottito)  ─►  IInputSource.Input  ─►  IReadOrchestrator.HandleInput (accoda)
    se la voce sta parlando           ─►  ISpeechService.Stop()  (fine)
    altrimenti                        ─►  ITextResolver.ResolveAsync(ReadRequest)
         1. selezione sotto il puntatore        (IUiTextSource.GetSelectionAsync)
         2. elemento sotto il puntatore         (IUiTextSource.GetElementAtAsync → UiTextResolver)
              documento con testo → frase sotto il punto (SentenceSplitter)
              cella → Value;  controllo → Name, poi descrizioni
         3. suggerimento visibile               (IUiTextSource.FindTooltipAsync, eventuale OCR del suo rettangolo)
         4. OCR della zona                      (IScreenCapture → IOcrEngine Windows, poi ONNX se serve → PointerTextSelector)
    pulizia del testo (LabelCleaner, EmojiFilter), lingua (LanguageGuesser), spezzatura in frasi
    ─►  ISpeechService.SpeakAsync  (cache → ElevenLabs → ripiego voce di Windows)  ─►  IAudioPlayer
```

Obiettivi di latenza dal clic al primo suono: etichetta già in cache meno di 150 ms; etichetta nuova via accessibilità con ElevenLabs meno di 700 ms; percorso OCR meno di 1 s.

### 2.2 Thread

| Thread | Compito | Vincoli |
|---|---|---|
| InputThread (priorità alta) | `WH_MOUSE_LL`, `RegisterHotKey`, ciclo messaggi | La callback dell'hook fa solo filtro e accodamento. Mai UIA, mai I/O. |
| UiaThread (MTA, senza finestre) | Tutte le chiamate UI Automation | Timeout brevi, watchdog, thread sostituibile se una chiamata non torna. |
| Worker dell'orchestratore | Una richiesta alla volta; la nuova annulla la precedente | `CancellationToken` ovunque. |
| Pool a priorità bassa | OCR ONNX | Non deve affamare l'InputThread. |
| Thread UI WPF | Solo icona di notifica e finestra impostazioni | Mai nel percorso di lettura. |

## 3. Moduli, classi pubbliche attese e responsabilità

I costruttori qui indicati sono quelli che `PuntaEAscolta.App` userà nella composizione: vanno rispettati.

### 3.1 PuntaEAscolta.Logic (spazio dei nomi `PuntaEAscolta.Logic`)

Parte A, funzioni pure con molti test:

- `Text.SentenceSplitter` (static): `string ExtractSentence(string paragraph, int offset)`; `IReadOnlyList<string> SplitSentences(string text, int maxChunkChars = 400)`. Regole italiane: non spezzare su abbreviazioni (`sig.`, `sig.ra`, `dott.`, `dr.`, `prof.`, `ing.`, `avv.`, `ecc.`, `es.`, `pag.`, `n.`, `art.`, `tel.`, `S.p.A.`, iniziali puntate), numeri e orari (`15.30`, `3,5`, `1.000`), puntini di sospensione; gestire `! ? …`, virgolette e parentesi di chiusura, ritorni a capo, caratteri di controllo che Word inserisce (`\r`, `\a`, `\v`, `\f`, U+FFFC). Se la frase supera `MaxCharsPerRead` si tronca a fine parola.
- `Text.EmojiFilter` (static): `string Strip(string text)`. Rimuove emoji Unicode (pittogrammi, bandiere, modificatori di tono, ZWJ, selettori di variante, tastierini), dingbat decorativi ed emoticon testuali isolate (`:-)`, `:)`, `;-)`, `:D`, `<3`, `^_^`...) senza toccare punteggiatura legittima (es. `ore 8:30`). Poi normalizza gli spazi.
- `Text.LabelCleaner` (static): `string Clean(string raw, UiElementKind kind, ReadingSettings settings)`. Toglie marcatori di tasto di accesso (`&File`, `_File`, `File(&F)`), la scorciatoia in coda (`Nuovo\tCtrl+N`, `Salva Ctrl+S`, `Esci Alt+F4`, `Ctrl+Maiusc+N`, `⌘N`), i puntini finali dei comandi (`Apri...` si legge `Apri`), frecce di sottomenu (`▸ > ›`), spazi multipli. Versione per righe OCR: `string CleanOcrLine(string line, ReadingSettings settings)` che toglie la scorciatoia riconosciuta a destra e simboli spuri isolati a inizio riga (icone scambiate per lettere, segni di spunta).
- `Text.LanguageGuesser`: `string? Guess(string text)` restituisce `"it"`, `"en"` o null. Euristica leggera su parole funzionali, desinenze, lettere accentate e un piccolo lessico di termini inglesi tipici delle interfacce (File, Edit, View, Layer, Brush, Save As, Export, Undo...). In dubbio restituisce `"it"`. Rispetta `SpeechSettings.LabelLanguage`.
- `Resolution.UiTextResolver`: `UiResolution? Resolve(UiElementInfo info, ReadingSettings settings)` con `record UiResolution(ReadSource Source, string Text, SpeechKind Kind, bool Sensitive)`. Priorità: documento o campo con `Text.PointerOverText` → frase (Source `UiaSentence`, Sensitive true); `DataItem` o cella → `Value` (vuota → `EmptyCellText`); `Edit` e `ComboBox` → etichetta più valore, mai il valore se `IsPassword`; altri controlli → `Name`, poi `LabeledByName`, `HelpText`, `FullDescription`, `LegacyDescription`, `LegacyName`. **Deve restituire null** (così si passa a tooltip e OCR) quando: non c'è nulla di pronunciabile; il nome coincide con un nome di classe o di tipo (`Affinity.Foo.BarViewModel`, `System.Windows...`, `PaneClassDC`); l'elemento è un contenitore (`Pane`, `Window`, `Group`, `Custom`, `Document` senza testo puntato, `Image`, `List`, `Tree`, `Tab`, `ToolBar`, `MenuBar`, `TitleBar`) con area grande, cioè più di 300x120 px o nome più lungo di 120 caratteri; `Image` senza nome utile. Stato di spunta aggiunto solo se `SpeakToggleState`.
- `Resolution.PointerTextSelector`: `OcrSelection? Select(OcrResult result, double pointerX, double pointerY, OcrSettings settings, bool wholeZone)` con `record OcrSelection(ReadSource Source, string Text, int LineCount)`. Sceglie la riga che contiene verticalmente il puntatore, altrimenti la più vicina entro `MaxLineDistanceInLineHeights`; se la riga è molto larga e ha una grande lacuna orizzontale (colonne diverse, etichetta e scorciatoia), usa solo il gruppo di parole più vicino al puntatore; con `GroupLinesIntoBlocks` unisce le righe adiacenti con altezza simile (rapporto entro 0,7-1,4), sovrapposizione orizzontale e distanza verticale minore di `BlockMaxGapInLineHeights` (i menu, più spaziati, restano righe singole; cartelli e paragrafi diventano un blocco, letto dall'alto in basso); `wholeZone` legge tutte le righe ordinate. Scarta righe fatte solo di simboli o di un solo carattere non alfanumerico.

Parte B, motore:

- `Reading.TextResolver : ITextResolver`, costruttore `(IUiTextSource ui, IScreenCapture capture, IOcrEngine primaryOcr, IOcrEngine? secondaryOcr, IClipboardSelectionReader? clipboard, ISettingsStore settings, ILog log)`. Applica il flusso 2.1. OCR in modalità `Auto`: prima il motore primario (Windows); si passa al secondario (ONNX) se il primario non trova righe vicine al puntatore, oppure se l'elemento sotto il puntatore è un'immagine o un'area di disegno e il primario ha trovato poco. `OcrOnlyProcesses` salta l'accessibilità. `ReadRequestKind.Selection`: accessibilità, poi ripiego sugli appunti. La zona di cattura è `ZoneWidth x ZoneHeight` moltiplicata per `DpiScale`, centrata sul puntatore; per i tooltip senza testo si cattura il loro rettangolo con 8 px di margine.
- `Reading.ReadOrchestrator : IReadOrchestrator`, costruttore `(ITextResolver resolver, ISpeechService speech, IDictationService? dictation, ISettingsStore settings, ILog log, TimeProvider? time = null)`. Contiene il riconoscitore dei gesti: azione sul DOWN se `LongPressEnabled` è false; con pressione lunga abilitata, clic breve deciso sull'UP e pressione lunga scattata da timer; anti-rimbalzo `DebounceMs`; **se la voce sta parlando qualunque attivazione la ferma e basta**; `HotkeyAction.Stop` ferma; `ToggleDictation` delega alla dettatura; `TogglePause` inverte `Paused`. Quando non trova testo e `SpeakWhenNothingFound` è attivo pronuncia `NothingFoundText` come `SpeechKind.System`. Spezza i testi lunghi con `SentenceSplitter.SplitSentences` e li passa in `SpeechRequest.Chunks`.
- `Settings.JsonSettingsStore : ISettingsStore`, costruttore `(string appDirectory, ILog? log = null)`. File `settings.json` nella cartella dell'app se scrivibile, altrimenti `%AppData%\PuntaEAscolta`. `DataDirectory` nella stessa posizione (`cache`, `logs`). Salvataggio atomico, enum come stringhe, tollerante a file mancante, corrotto o parziale (in caso di file corrotto lo rinomina `.bad` e riparte dai predefiniti).
- `Logging.FileLog : ILog`, costruttore `(string directory, Func<bool> debugEnabled)`. File giornaliero, massimo 7 file, scrittura protetta da lock, mai eccezioni verso il chiamante.

### 3.2 PuntaEAscolta.Speech (spazio dei nomi `PuntaEAscolta.Speech`)

- `ElevenLabs.ElevenLabsSynthesizer : ISpeechSynthesizer`, costruttore `(HttpClient http, ISettingsStore settings, ISecretProtector protector, ILog log)`. `POST /v1/text-to-speech/{voice_id}/stream?output_format=pcm_44100`, intestazione `xi-api-key`, corpo JSON con `text`, `model_id`, `voice_settings`, e `language_code` solo per i modelli che lo accettano. Restituisce lo stream PCM appena arrivano le intestazioni. Eccezioni tipizzate: `SpeechProviderException(Reason)` con `InvalidKey`, `QuotaExceeded`, `RateLimited`, `Network`, `Timeout`, `Server`, `NotConfigured`. Se il piano rifiuta `pcm_44100` riprova una volta con `pcm_24000`.
- `ElevenLabs.ElevenLabsAccountClient`, costruttore `(HttpClient http)`: `GetVoicesAsync(apiKey)`, `GetSubscriptionAsync(apiKey)`, `ValidateKeyAsync(apiKey)`. Usato dalla finestra impostazioni.
- `Cache.SpeechCache`, costruttore `(string directory, Func<int> maxMegabytes, ILog log)`. Chiave SHA-256 di fornitore, voce, modello, lingua, formato, parametri voce e testo normalizzato. File PCM grezzo con piccola intestazione propria o WAV. Scrittura atomica, **mai audio parziale** (se la lettura viene fermata a metà il pezzo non entra in cache, a meno che lo scaricamento non venga completato in sottofondo), limite di dimensione con eliminazione dei meno usati, `Clear()`, `GetSizeBytes()`.
- `SpeechService : ISpeechService`, costruttore `(ISpeechSynthesizer? cloud, ISpeechSynthesizer local, IAudioPlayer player, SpeechCache cache, ISettingsStore settings, ILog log)`. Sceglie il fornitore (`Auto`, `Windows`, `ElevenLabs`; `System` e, con `DocumentsWithLocalVoiceOnly`, i testi `Sensitive` sempre in locale). Tempo massimo per il primo audio (`FirstAudioTimeoutLabelMs`, `FirstAudioTimeoutSentenceMs`), poi ripiego immediato sulla voce locale. Interruttore automatico: dopo errori ripetuti o chiave non valida sospende il cloud per un intervallo crescente, senza tentativi ripetuti in linea. Pezzi in sequenza con preparazione anticipata di un solo pezzo. `Stop()` immediato e idempotente. Una nuova `SpeakAsync` annulla la precedente.
- `Dictation.ElevenLabsSpeechToText : ISpeechToText`, costruttore `(HttpClient http, ISettingsStore settings, ISecretProtector protector, ILog log)`.
- `Dictation.DictationService : IDictationService`, costruttore `(IAudioRecorder recorder, ISpeechToText stt, ITextInjector injector, ISpeechService speech, ISettingsStore settings, ILog log)`. Alternanza avvio e arresto, durata massima, nessun taglio automatico sulle pause (la parola disartrica ha pause lunghe), rilettura del testo riconosciuto prima dell'inserimento, comandi a frase intera (cancella, a capo, rileggi), segnali acustici brevi di inizio e fine registrazione. Vedi `docs/research/dictation.md`.

### 3.3 PuntaEAscolta.Ocr.Onnx

- `OnnxOcrEngine : IOcrEngine`, costruttore `(ILog log, string? modelsDirectory = null)`. Inizializzazione pigra e `Task WarmUpAsync()`. Solo CPU, thread limitati, priorità bassa. Filtra i caratteri non latini. Coordinate riportate all'immagine originale. Vedi `docs/research/ocr-onnx.md` e `docs/research/spike-ocr-onnx.md`.

### 3.4 PuntaEAscolta.Windows.Input

- `WindowsInputSource : IInputSource`, costruttore `(ILog log)`. Vedi `docs/research/input-capture.md`: hook su thread dedicato, coppia DOWN e UP sempre inghiottita insieme, eventi iniettati, reinstallazione dell'hook a sblocco sessione, ripresa e cambio schermo, `RegisterHotKey` sullo stesso thread, Esc registrato solo mentre la voce parla. Attivatore della dettatura anche da pulsante del mouse.
- `GdiScreenCapture : IScreenCapture`, costruttore `()`. `BitBlt` con `CAPTUREBLT`, ritaglio sul monitor del puntatore, scala DPI del monitor.
- `SendInputTextInjector : ITextInjector`, `ClipboardSelectionReader : IClipboardSelectionReader` (costruttori `(ILog log)`), `NativePointer.GetPhysicalPosition()`.

### 3.5 PuntaEAscolta.Windows.Automation

- `UiaTextSource : IUiTextSource`, costruttore `(ILog log)`. Vedi `docs/research/uia-dotnet.md`, `docs/research/probe-office.md` e il codice di esempio in `docs/research/probe/office-uia3-sample`. UIA3 COM, thread MTA dedicato con watchdog, `ElementFromPointBuildCache`, discesa e risalita limitate per trovare l'elemento più utile, `TextPattern.RangeFromPoint` con verifica dei rettangoli, selezione dell'elemento con il focus, ricerca dei tooltip (UIA `ToolTip` e finestre `tooltips_class32` o popup piccoli vicino al puntatore), nome del processo.

### 3.6 PuntaEAscolta.Windows.Ocr

- `WindowsOcrEngine : IOcrEngine`, costruttore `(Func<string> languageTag, ILog log)`. Pre-trattamento misurato in `docs/research/ocr-windows.md` e `docs/research/probe-affinity.md`: ingrandimento bicubico adattivo alla scala del monitor, margine, scala di grigi con stiramento del contrasto, rispetto di `MaxImageDimension`, un `RecognizeAsync` alla volta.

### 3.7 PuntaEAscolta.Windows.Audio

- `NAudioPlayer : IAudioPlayer`, costruttore `(ILog log)`. Riproduzione da stream PCM con piccolo pre-buffer, fine automatica a fine dati, `Stop()` immediato, dispositivo predefinito riletto a ogni lettura. Vedi `docs/research/spike-audio.md` e `docs/research/probe/audio-sample`.
- `NAudioRecorder : IAudioRecorder`, costruttore `(ILog log)`.
- `WindowsVoiceSynthesizer : ISpeechSynthesizer`, costruttore `(ISettingsStore settings, ILog log)`, più `static IReadOnlyList<WindowsVoiceInfo> GetVoices()`. `Windows.Media.SpeechSynthesis`, SSML per velocità e lingua, uscita PCM.
- `DpapiSecretProtector : ISecretProtector`, costruttore `()`.
- `SystemSounds`: brevi segnali acustici generati in PCM (inizio e fine dettatura, errore).

### 3.8 PuntaEAscolta.App

- Composizione manuale di tutti i servizi, istanza singola, manifest con DPI Per-Monitor V2 e `asInvoker`.
- Icona di notifica: Pausa o Riprendi, Impostazioni, Apri cartella dei log, Esci.
- Finestra impostazioni WPF in italiano: Generale, Attivazione, Lettura, Voce (chiave ElevenLabs in `PasswordBox`, pulsante di verifica, elenco voci dall'account, prova voce, crediti residui, cache), Dettatura, Diagnostica.
- Riga di comando per prove automatiche, senza interfaccia: `--read-at X,Y [--zone] [--speak]` stampa l'esito in JSON; `--ocr-file immagine --point X,Y`; `--speak "testo"`; `--selftest`.
- Avvio con Windows opzionale (chiave `Run` dell'utente).
- `tools/publish.ps1`: due cartelle portatili `publish/PuntaEAscolta-win-x64` e `publish/PuntaEAscolta-win-arm64`, self-contained, non ridotte.

## 4. Verifica

- Test unitari abbondanti per `Logic` (frasi, emoji, etichette, lingua, risoluzione, selezione OCR, gesti, archivio impostazioni) e `Speech` (client HTTP con gestore fittizio, cache, ripiego, interruttore, pezzi, stop).
- Prove dal vivo su questa macchina tramite la riga di comando: Blocco note, Esplora file, Word, Excel, immagini sintetiche, screenshot reali di Affinity in `docs/research/probe`.
- Il percorso ElevenLabs non è verificabile senza la chiave: va provato dall'assistente dalla finestra impostazioni.

## 5. Limiti noti dichiarati

- Finestre eseguite come amministratore: il clic della rotellina non viene intercettato e l'accessibilità non le raggiunge (servirebbe un'app firmata installata in Programmi).
- Testo molto piccolo o a bassissimo contrasto può essere letto male dall'OCR.
- Il pulsante scelto come attivatore perde la sua funzione normale nelle altre app (con la rotellina: scorrimento automatico e panoramica).
