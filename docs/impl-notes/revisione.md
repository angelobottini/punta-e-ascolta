# Revisione del 22/09/2026: correzioni applicate

Ingressi: i difetti confermati dalla revisione avversaria (elenco ricevuto **troncato** dopo `WINOCR-CROP-FRAGMENTS`), gli 11 bug delle prove dal vivo (`docs/research/prove-dal-vivo.md`) e le note di pubblicazione. Le voci delle aree "logica e app" e "voce e audio" che non erano nell'elenco ricevuto sono state ricostruite dai programmi di verifica lasciati dai revisori in `scratchpad\verify\logic-app` e `scratchpad\verify\speech-audio`, rieseguiti prima e dopo le correzioni: sono segnate come "da verifica" nella tabella.

Esito finale: soluzione compilata con **0 avvisi**; **501 test** (Logic 321, Speech 126, Windows 54; prima 364) verdi in 6 esecuzioni consecutive della soluzione intera; `--selftest` riuscito dalla cartella di compilazione e dalle due cartelle pubblicate.

## Difetti della revisione

| Voce | Gravità | Problema | Esito | Test |
|---|---|---|---|---|
| UIA-CACHE-PATTERN-PROPS | alta | `CachedValue` e `CachedToggleState` lanciavano sempre `E_INVALIDARG` (proprietà dei pattern non in cache): celle di Excel "Cella vuota", valori di caselle combinate e stati di spunta mai letti | **Corretto**: `ValueValue` (30045) e `ToggleToggleState` (30086) in `UiaIds.CachedProperties`; ripiego su `CurrentValue`/`CurrentToggleState`. Verificato con la sonda dei revisori: la cache di produzione ora restituisce "1851,75 EUR" e `ToggleState_On` | `UiaCacheTests` (EDIT e casella Win32 nascoste); casi del resolver in `UiTextResolverReviewTests` |
| WINOCR-CROP-FRAGMENTS | media | Il passaggio mirato (ritaglio 400x120) restituiva parole tagliate dai lati del ritaglio ("ta come PDF"), che bloccavano anche il ripiego ONNX | **Corretto**: fascia larga quanto l'immagine e alta 120 px (stiramento del contrasto sempre misurato sul 400x120 attorno al punto); righe tagliate dai bordi superiore e inferiore della fascia scartate. Menu sintetico dei revisori: 9/9 "Esporta come PDF" (prima frammenti), etichetta lunga intera; costo 30-50 ms | `WindowsOcrTests` (3 casi end-to-end col motore vero, 2 test di `DropLinesCutByCrop`) |
| (trovato durante WINOCR) | media | Con `OcrResult.TextAngle` diverso da zero (anche 3 gradi spuri) Windows OCR restituisce i riquadri nel sistema raddrizzato: righe spostate di 9-24 px, puntatore sulla riga sbagliata | **Corretto**: `WindowsOcrEngine.UndoTextAngle` riporta ogni riquadro. Coordinate identiche in 36 posizioni su `affinity-lowcontrast-normal.png`; primo passaggio invariato sulle immagini della ricerca; maglietta ruotata di 8 gradi ancora "PENSA POSITIVO" | `UndoTextAngle_BringsDeskewedBoxesBackToTheirRow` |
| F1 | media | Appunti con testo più immagine, RTF/HTML o dati di Office ridotti a solo testo; appunti svuotati se `CF_UNICODETEXT` non leggibile | **Corretto**: fotografia con `EnumClipboardFormats`, solo formati testuali noti (`ClipboardFormatPolicy`), copia byte per byte e ripristino di tutti i formati; con altri formati o dati non leggibili rinuncia senza toccare nulla | `ClipboardFormatPolicyTests` |
| F2 | media | Annullamento, tempo scaduto o appunti occupati dopo il Ctrl+C: nessun ripristino, la copia dell'app sostituiva gli appunti | **Corretto**: dopo il Ctrl+C il ripristino è sempre garantito; il chiamante riceve subito l'esito, il thread STA sorveglia fino a 1,5 s e ripristina (40 tentativi a 25 ms); la porta si libera solo dopo il ripristino | Nessun test automatico (userebbe gli appunti veri dell'utente); verificato a lettura |
| F3 | bassa | Due pause ravvicinate potevano lasciare l'app in pausa in silenzio (e nel file) dopo "Lettura riattivata" | **Corretto** in `TrayController`: si salva lo stato attuale dell'orchestratore, un salvataggio in coda alla volta, `ApplySettings` non rimanda la pausa quando il salvataggio viene dall'orchestratore; la pausa dal menu passa dal worker (con conferma a voce) | Nessun progetto di test per l'App: verificato a lettura e con la compilazione |
| F4 | media | Hook tolto da Windows mai reinstallato (attesa su pulsante premuto, 3 s di inattività ogni 10 minuti); log sincrono sull'InputThread | **Corretto**: niente attesa sul pulsante premuto; rilevamento dell'hook morto (cursore spostato senza callback da oltre 1 s, controllo ogni 5 s, al massimo una reinstallazione ogni 10 s); reinstallazione periodica ogni 2 minuti con 250 ms di inattività; tutti i log dell'InputThread su un thread del pool | Nessun test automatico (bisognerebbe far scadere l'hook bloccando il mouse); `--selftest` (hook installato e rimosso) |
| F5 | media | Esc, Invio, Spazio, lettere come scorciatoie fisse: tolte a tutti i programmi, anche ai tasti della dettatura | **Corretto**: `HotkeyMap.CheckGlobalSafety` (Esc mai; senza Ctrl/Alt/Win solo F1-F24, Pausa, Bloc Scorr, tasti multimediali), applicato alla registrazione e alla finestra impostazioni (`WindowsInputSource.ValidateHotkey`); nota aggiornata | `HotkeySafetyTests` |
| F6 | bassa | Ctrl+C inviato con modificatori ancora premuti (Ctrl+Maiusc+C, Ctrl+Alt+C) | **Corretto**: se dopo 1 s i modificatori sono premuti il Ctrl+C non parte; l'iniettore aspetta fino a 3 s prima di Invio/Tab/Backspace e poi rinuncia ("Inserimento non riuscito") | Nessun test automatico (stato reale della tastiera) |
| Voci successive dell'elenco | ? | Elenco ricevuto troncato | Non ricevute: vedi le voci "da verifica" sotto | - |

## Voci ricostruite dai programmi di verifica dei revisori (non nell'elenco ricevuto)

| Voce | Problema riprodotto | Esito | Test |
|---|---|---|---|
| Logica F2 (da verifica) | `IsKeyboardShortcut` scambiava "Altro", "Alto", "Controllo", "Opzione", "Windows", "SUPER 95" per scorciatoie: l'OCR cancellava queste voci | **Corretto**: una parola conta come modificatore solo se seguita da + o -, tasto obbligatorio | `LabelCleanerReviewTests`, `MenuRow_LabelLikeModifierWord_IsNotTakenForShortcut` |
| Logica F4 (da verifica) | Edit su più righe con il puntatore fuori dal testo: letto tutto il corpo | **Corretto** (vedi bug dal vivo 4) | `LargeMultilineEdit_PointerOffText_NeverReadsWholeDocument`, `SmallEdit_WithMultilineValue_SpeaksOnlyTheLabel` |
| Logica F7 (da verifica) | `EmojiFilter` toglieva "XP", "8)", "D:", "X)", "x3" | **Corretto**: occhi solo ":", ";", "=" (più "xD", "8-)", "B-)") | `EmojiFilterReviewTests` |
| Voce A (da verifica) | Dettatura: annullamento incrociato con la fine della rilettura, il testo annullato veniva inserito | **Corretto**: `Session.Cancel` aspetta che il token dell'altro thread risulti annullato | `Cancel_racing_with_stopped_read_back_never_inserts_the_rejected_text` |
| Voce B e C (da verifica) | Chiave API: corpi d'errore troncati prima di togliere la chiave, fino a 30 caratteri su 37 nel log | **Corretto**: prima si toglie la chiave dal corpo intero, poi si tronca | 7 casi in `ElevenLabsSpeechToTextTests` e `ElevenLabsSynthesizerTests`, più `Redact_TruncatesLongMessagesAfterRemovingTheKey` |
| Voce D (da verifica) | Pezzo successivo già scaricato scartato allo stop e pagato di nuovo | **Aperto**: solo crediti, nessun effetto per l'utente; richiede di cambiare la cache nel mezzo della preparazione anticipata | - |
| Voce E (da verifica) | Una lettura rimasta indietro chiamava `PlayAsync` con token già annullato e interrompeva la nuova | **Corretto** in `NAudioPlayer`: token già annullato = ritorno immediato senza toccare la riproduzione | Nessun test automatico (riprodurrebbe audio sul PC) |
| (trovato con i test) | `TextResolver.GuardAsync`: se `WaitAsync` scadeva prima del `CancelAfter`, la fase non veniva mai annullata (lavoro inutile in sottofondo; test instabile sotto carico) | **Corretto**: annullamento esplicito del token della fase allo scadere | Il test già esistente ora è stabile |

## Bug delle prove dal vivo

| # | Bug | Esito | Test |
|---|---|---|---|
| 1 | "Grassetto (CTRL+G)" | **Corretto**: scorciatoia fra parentesi tolta (anche "(Ctrl+MAIUSC+X)", "(CTRL+barra spaziatrice)", "(F1)"), sia per l'accessibilità sia per l'OCR | `Clean_ShortcutInParentheses_IsRemoved` e altri |
| 2 | File di Esplora file letto "Nome" | **Corretto**: il valore ora arriva (cache UIA) e un Edit dentro una voce di elenco con valore uguale al nome della voce dice solo "lib.ps1"; nella vista dettagli "Ultima modifica, 22/09/2026 20:12". Da riprovare dal vivo | `ExplorerFileNameLabel_SpeaksOnlyTheFileName`, `ExplorerDetailsColumn_SpeaksColumnAndValue` |
| 3 | Cella A1 con testo letta "Cella vuota" | **Corretto** alla radice (cache UIA): il valore visualizzato arriva anche lontano dal testo. Da riprovare dal vivo | `ExcelActiveCell_PointerAwayFromText_SpeaksValue`, `UiaCacheTests` |
| 4 | Pagina vuota di Word: "Contenuto pagina 1" | **Corretto**: area di testo grande con il puntatore fuori dal testo -> suggerimento e OCR ("Nessun testo" se non c'è nulla) | `WordEmptyPageArea_PageNameIsNotSpoken` |
| 5 | "circa." non chiudeva la frase | **Corretto**: abbreviazioni ambigue chiudono la frase davanti a una maiuscola | `SentenceSplitterReviewTests` (frase della prova dal vivo compresa) |
| 6 | Separatore di Affinity letto come nome di tipo | **Corretto**: ripieghi filtrati come il Name; MenuItem senza nome alto al massimo 12 px = silenzio | `AffinityMenuSeparator_TypeNameInLegacyName_IsSilent` |
| 7 | "newdocnew", "newdocopen" | **Corretto**: nome minuscolo attaccato di 7 o più caratteri con HelpText -> HelpText ("Nuova", "Apri") | `LowercaseIdentifierName_PrefersHelpText`, `NormalName_IsNotReplacedByHelpText` |
| 8 | Zona: righe di colonne diverse mescolate | **Corretto**: lettura per blocchi, prima quello sotto il puntatore, poi gli altri dall'alto e da sinistra; righe a capo della stessa frase senza pausa | 3 test in `PointerTextSelectorTests` |
| 9 | "(elemento aggiunto)", "Avvio dell'accesso rapido - " | **Corretto** in `LabelCleaner` | `Clean_ExplorerNavigationPaneNames` |
| 10 | Icona della barra superiore di Affinity muta | **Aperto**: serve una sonda dal vivo su Affinity (la barra è un `ToolBar` senza nome con un figlio `Text` per icona; senza rettangoli non si sa quale sia sotto il puntatore). Non ho aperto Affinity per non disturbare l'utente | - |
| 11 | Barra di scorrimento di Word: OCR della riga vicina | **Corretto**: barra di scorrimento (o suo figlio, per esempio il cursore) senza nome -> silenzio. Non riprovato dal vivo: dipende da che cosa restituisce UIA su quella barra | `ScrollBarWithoutName_IsSilent_NoOcrOfNeighbourLine` |

## Pubblicazione

- `tools\publish.ps1 -SkipTests` (cartella intermedia privata): 18 s, 0 avvisi. x64 313 file, 224,1 MB, 4 librerie Visual C++; ARM64 311 file, 237,8 MB, 3 librerie (`vcruntime140_1.dll` ARM64EC non copiata, come previsto).
- `--selftest` su `publish\PuntaEAscolta-win-arm64`: esito positivo, nessun problema né avviso, 5,6 s (primo avvio dopo la pubblicazione); su x64 emulato: positivo, 6,3 s.
- Le due cartelle sono state lasciate pulite (nessun `logs`, `settings.json`, `cache`, nessun `.lib`).

## Rischi residui

- **Non provato dal vivo** sulle app dell'utente: Esplora file, Excel, Word, Blocco note e Affinity non sono stati riaperti (l'utente poteva essere al PC). I bug 2, 3 e 11 sono corretti per ragionamento e con test, non riprovati sullo schermo. Da ripetere le righe corrispondenti di `prove-dal-vivo.md`.
- **Valori ora letti ovunque** (cache UIA): un controllo con `ValuePattern` che restituisce un testo enorme lo trasferisce a ogni lettura. Nelle app viste (Word, Excel, Chrome, Esplora file) non succede; il resolver comunque non legge mai come valore un testo su più righe o oltre 200 caratteri.
- **Appunti**: con una copia da Word o Excel negli appunti il ripiego "leggi la selezione" tramite appunti resta spento (scelta voluta: meglio non leggere che perdere la copia). Una copia dell'app che arriva dopo 1,5 s dal Ctrl+C sostituisce ancora gli appunti. Il ripristino multi-formato non è stato provato sugli appunti veri.
- **Hook**: il rilevamento dell'hook morto può reinstallare l'hook senza bisogno (cursore spostato da un programma con `SetCursorPos`, spinta contro il bordo dello schermo): al massimo una volta ogni 10 s, senza effetti per l'utente.
- **Scorciatoie già salvate** con Esc o un tasto senza modificatore ora non vengono registrate: all'avvio l'app lo dice a voce ("Attenzione...") e la finestra impostazioni chiede di cambiarle.
- **Pausa dal menu** ora dice "Lettura in pausa" / "Lettura riattivata" come la scorciatoia.
- **Abbreviazioni ambigue**: "gen. Rossi" (generale) davanti a un nome viene ora spezzato come fine frase; si è preferito il caso dei mesi ("il 10 gen. Poi...").
- **OCR**: il passaggio mirato costa 30-50 ms invece di 15-20; il ritaglio resta tarato al 125%.
- Elenco dei difetti ricevuto troncato: eventuali altre voci dell'area "logica e app" o "voce e audio" non ricostruibili dai programmi di verifica non sono state viste.
- Durante il lavoro sono comparsi nel repository quattro commit con parte di queste modifiche (06e812e, b5895c4, ce2953d, f30365e), non fatti da chi ha applicato le correzioni; il resto (note di implementazione, questo file, la correzione di `GuardAsync`) è nella copia di lavoro, non committato.
