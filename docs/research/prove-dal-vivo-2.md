# Prove dal vivo dell'app corretta (secondo giro)

Data: 22/09/2026, 21:50-22:08. Macchina: Snapdragon X (ARM64), Windows 11 25H2, 1920x1200 al 125%, italiano. Compilazione Debug di `src\PuntaEAscolta.App` in una cartella privata (`scratchpad\build\live2`), 0 avvisi, 0 errori. `settings.json` solo nella cartella di output: `Speech.Volume` 0,2, `Speech.Provider` Windows, `General.DebugLog` true, `HotkeyReadSelection` `Win+Shift+F9`; per le prove della modalità icona anche `Input.AcceptInjectedEvents` true, poi rimesso a false.

## Metodo

- Sonda: `PuntaEAscolta.exe --read-at X,Y` (pixel fisici) da un PowerShell reso Per-Monitor V2 (`SetProcessDpiAwarenessContext(-4)`), come nel primo giro (`prove-dal-vivo.md`). Coordinate da `PointsToScreenPixelsX/Y` di Excel, `Window.GetPoint` di Word e rettangoli UIA.
- La colonna **ms** è `elapsedMs` dell'app (tempo di risoluzione, senza l'avvio del processo).
- Solo finestre mie: istanze Excel e Word create via COM con documenti nuovi chiusi senza salvare, una finestra di Esplora file sulla cartella di lavoro (chiusa con `Quit`), un'immagine mostrata in una finestra WinForms di un mio processo, Affinity avviato da me (non era in esecuzione). Il Word dell'utente ("Documento2 - Word") non è stato toccato. Blocco note non aperto.
- Script e catture: `scratchpad\live2\` (`excel.ps1`, `explorer.ps1`, `word.ps1`, `word2.ps1`, `word3.ps1`, `tray.ps1`, `showimg.ps1`, `affinity.ps1`, `*.png`).

## Risultati

| # | Scenario | Punto | Atteso | Ottenuto | Fonte | ms | Esito |
|---|---|---|---|---|---|---|---|
| a | Excel, A1 "Nome" (cella attiva, era il BUG 3) | 233,407 | "Nome" | "Nome" | UiaValue | 246 (prima lettura) | OK |
| a | Excel, A1 non attiva | 233,407 | "Nome" | "Nome" | UiaValue | 83 | OK |
| a | Excel, A2 testo | 233,431 | "Mele rosse del Trentino" | identico | UiaValue | 96 | OK |
| a | Excel, B2 numero (formato `#.##0,00`) | 313,431 | "1.250,50" | "1.250,50" | UiaValue | 81 | OK |
| a | Excel, C2 numero intero | 393,431 | "42" | "42" | UiaValue | 72 | OK |
| a | Excel, B3 formula `=B2*2` (non attiva e attiva) | 313,455 | "2501" | "2501" | UiaValue | 65-70 | OK |
| a | Excel, D5 e E2 vuote | 473,503 / 553,431 | "Cella vuota" | "Cella vuota" | UiaValue | 82-84 | OK |
| b | Esplora file, icone grandi: scritta sotto l'icona (era il BUG 2) | 393,392 / 542,392 / 690,392 | nome della cartella | "apidump", "build", "dictrace" (mai "Nome") | UiaValue | 124-131 | OK |
| b | Esplora file, icona sopra la scritta | 392,309 ... | nome | stessi nomi | UiaName | 104-111 | OK |
| c | Word, "Il gen. Rossi è arrivato alle 10. Poi ripartirà.": puntatore su "gen.", "Rossi", "arrivato", "10." | 417,421 ... 606,421 | "Il gen. Rossi è arrivato alle 10." | identico | UiaSentence | 95-173 | OK |
| c | Word, stessa riga, "ripartirà" | 677,421 | "Poi ripartirà." | identico | UiaSentence | 158 | OK |
| c | Word, "... alle 15.45 circa. Vedi pag. 12 ..." su "spostata" e "circa" (era il BUG 5) | 570,452 / 862,452 | la sola prima frase | "La riunione è stata spostata al pomeriggio, cioè alle 15.45 circa." | UiaSentence | 94-160 | OK |
| c | Word, "regolamento" | 437,482 | "Vedi pag. 12 e cfr. l'art. 5 del regolamento!" | identico | UiaSentence | 87 | OK |
| c | Word, pagina vuota sotto il testo (4 punti, due finestre diverse) (era il BUG 4) | 687,640 / 487,820 / 847,606 / 647,856 | silenzio o "Nessun testo", mai "Contenuto pagina 1" | nessun testo | None | 997-1469 | OK (lento, nota 4) |
| c | Word a tutto schermo, area grigia fuori pagina (lontana, vicina alla pagina, vicina alla riga di testo) | 202,706 / 412,556 / 392,396 | silenzio | nessun testo | None | 936-1297 | OK (lento, nota 4) |
| c | Word in finestra 1400x1000 a x=100, area grigia a sinistra della pagina | 232,720 / 232,520 | silenzio | **"Format"** e **"Appli"** (testo di un'altra finestra dietro Word) | OcrLine | 300-352 | **BUG A** |
| c | Word, barra di scorrimento orizzontale (compare solo con il puntatore sopra) | 700,996 | silenzio, non la riga della barra di stato | nessun testo, `barra-di-scorrimento` | None | 62 | OK |
| c | Word, barra di scorrimento verticale | 1480,634 | silenzio | nessun testo, `barra-di-scorrimento` | None | 67 | OK |
| d | Esc con l'app inattiva: `RegisterHotKey(NULL, id, 0, VK_ESCAPE)` dal mio processo (prima dell'avvio, ad app avviata, dopo le prove) | - | riesce (Esc non sottratto) | riesce tutte e tre le volte | - | - | OK |
| d | Immagine rumorosa "USCITA" (Pane WinForms), clic centrale, secondo clic nello stesso punto 495 ms dopo | 900,550 | lettura in sospeso annullata, nessuna voce | "Attivazione (clic) durante la ricerca del testo: lettura annullata", "Risoluzione annullata dopo 458 ms", "Lettura 1 annullata"; nessuna riga "Voce" | - | 458 | OK |
| d | Clic, poi secondo clic 482 ms dopo a 290 px di distanza | 900,550 → 1150,700 | lettura nuova nel punto nuovo | "durante la ricerca del testo con il puntatore spostato: lettura nuova", "Lettura 2 annullata", poi lettura a 1150,700: "Nessun testo" (detto) | None | 763 | OK |
| d | Esc tenuto dall'app durante la seconda lettura | - | `RegisterHotKey` fallisce | fallisce con errore 1409 (tasto già registrato) | - | - | OK |
| d | Clic, poi Esc iniettato 300 ms dopo, durante la ricerca | 900,550 | lettura annullata | Esc registrato durante la ricerca (1409 a +185 ms); "Scorciatoia di stop", "Risoluzione annullata dopo 288 ms", "Lettura 4 annullata", nessuna voce | - | 288 | OK |
| d | Esc durante la voce (primo tentativo, nota 5) | - | la voce si ferma | "Scorciatoia di stop", `PlaybackStopped` 1 ms dopo | - | 1 | OK |
| d | Secondo clic durante la voce (primo tentativo, nota 5) | - | la voce si ferma | "mentre la voce parla: stop", `PlaybackStopped` 2 ms dopo | - | 2 | OK |
| d | `--exit` | - | app chiusa | chiusa in 106 ms | - | 106 | OK |
| e | Affinity, prima icona a destra della barra superiore (era la nota 10) | 1363,74 | "Modalità anteprima" | nessun testo (ToolBar senza nome, OCR ritagliato vuoto, ONNX) | None | 811 | **BUG 10** |
| e | Affinity, altre icone della stessa barra | 1430,74 / 1497,74 / 1564,74 / 1631,74 / 1868,74 | "Disponi", "Trasforma", "Allineamento", "Effetto calamita", "Guida" | nessun testo per tutte | None | 770-811 | **BUG 10** |

Riepilogo: 30 righe. 26 riuscite (due con nota di lentezza), 4 fallite per 2 bug (A e 10). I bug 2, 3, 4 e 5 del primo giro e la nota 11 (barra di scorrimento) sono risolti dal vivo; il secondo clic e Esc annullano davvero la ricerca del testo; Esc non resta sottratto al sistema a lettura finita.

## Bug e osservazioni (con riproduzione)

**BUG A - L'OCR di ripiego legge il testo di un'altra finestra.** `src/PuntaEAscolta.Logic/Reading/TextResolver.cs`, `OcrZoneAsync` (righe 287-309). Word in finestra (non a tutto schermo) 1400x1000 in (100,40), dietro una finestra dell'utente con testo; puntatore nell'area grigia 124 px a destra del bordo di Word (232,720 e 232,520): UIA restituisce il `Document` `_WwG` 1361x741 senza testo (`uia:niente`), l'OCR cattura una zona di 795x450 che esce da Word e sceglie la riga più vicina, che è di un'altra finestra: "Format", "Appli" (lingua "en" per la prima). Il ritaglio all'elemento (`ritaglio-elemento`) scatta solo per gli elementi piccoli (`IsSmallElement`), quindi qui la zona non è limitata né all'elemento né alla finestra sotto il puntatore. Con Word a tutto schermo lo stesso punto dà silenzio. Proposta: limitare sempre la zona OCR alla finestra di primo livello sotto il puntatore (e, per il `Document` di Word senza testo sotto il punto, silenzio come per la barra di scorrimento).

**BUG 10 - Icone della barra superiore di Affinity mute (causa trovata).** `src/PuntaEAscolta.Windows.Automation/ElementReader.cs`, `Normalize` (righe 183-187), e `ControlTypeMap.IsLabelledControl` (`ControlTypeMap.cs` riga 56). `FromPoint` sulle icone restituisce direttamente una `ToolBar` senza nome; la ricerca del nome nei `Text` discendenti (`FindDescendantTextName`) scatta solo per i tipi di `IsLabelledControl`, che non comprende `ToolBar`. Nell'albero (sotto) ogni icona è una `ToolBar` a sé con un solo nome ripetuto nei figli, quindi la regola "ToolBar senza nome, piccola, con tutti i `Text` discendenti dello stesso nome -> quel nome" basterebbe per tutte le icone a destra. Il caso con nomi diversi (la barra di sinistra con Vettore/Pixel) resta ambiguo: vedi la struttura.

Osservazioni:

1. **L'OCR ONNX continua dopo l'annullamento.** Nelle tre prove di annullamento il registro mostra la riga `OCR ONNX: ... totale 331-460 ms` da 230 a 380 ms dopo "Lettura N annullata": l'inferenza non si interrompe (probabilmente `src/PuntaEAscolta.Ocr.Onnx/OnnxOcrEngine.cs`, il token è controllato solo fra le fasi). Non si sente nulla, ma occupa la CPU e il motore ONNX se il bambino clicca subito altrove.
2. Nella lettura nuova a 1150,700 la zona OCR comprendeva anche righe della finestra a destra (Claude): scartate come tagliate o lontane, risultato "Nessun testo" corretto. Stesso meccanismo del BUG A, qui innocuo.
3. Word: la barra di scorrimento orizzontale moderna compare solo con il puntatore vicino e UIA la dichiara `Orientation=Vertical` pur essendo 1361x21: la regola attuale (tipo `ScrollBar`, senza guardare l'orientamento) è quella giusta.
4. **Pagina vuota e area grigia di Word: silenzio giusto ma lento** (0,9-1,5 s): l'app passa per OCR Windows, OCR mirato e ONNX prima di concludere "niente". Dall'icona si sente "Nessun testo" dopo circa un secondo. Si potrebbe rispondere subito quando l'elemento è l'`Edit` "Contenuto pagina N" o il `Document` `_WwG` e il testo UIA sotto il punto manca (`TextResolver.cs` dopo `uia:niente`).
5. Primo tentativo della prova d: la finestra dell'immagine lanciata con `-WindowStyle Hidden` non è comparsa (il primo `ShowWindow` del processo eredita `SW_HIDE`) e i clic sono finiti su una finestra di Esplora file dell'utente, letta ("Dimensione, 5,13 MB") ma non modificata: il clic centrale è stato consumato dall'app. Quel giro vale come prova di stop durante la voce (righe d). Ripetuto con la finestra visibile e controllo `WindowFromPoint` prima dei clic.
6. Metodo: il Word nascosto rimasto aperto nel primo giro nasceva da `$w.Quit(0)` in PowerShell, che fallisce in silenzio ("L'argomento '1' deve essere un PSReference"): si usa `$w.Quit()` o `Quit([ref]0)`. In questo giro una mia istanza (PID 3524) è rimasta aperta così ed è stata chiusa con `WM_CLOSE` sulla sua finestra; le altre due (23628, 12984) si sono chiuse. Il processo nascosto del primo giro (22520) non c'è più.

## Struttura della barra superiore di Affinity (per mappare l'icona al nome)

Affinity 3.3 (WPF), finestra massimizzata, nessun documento aperto, schermata di benvenuto chiusa con Esc. Albero **raw** con UIA gestito da un PowerShell Per-Monitor V2. Rettangoli in pixel fisici. `HelpText` sempre vuoto; `LegacyIAccessible` non aggiunge nulla.

- La barra è fatta di **tante `ToolBar` sorelle, una per icona**, tutte `Name=''`, `AutomationId=''`, `ClassName='ToolBar'`, `IsOffscreen=False`. `FromPoint` restituisce la `ToolBar` stessa (non un figlio): il nome va preso dai figli.
- Ogni `ToolBar` di un'icona ha 3 figli, in quest'ordine:
  1. `Text` con `AutomationId='text'`, `Name` = nome dell'icona, rettangolo vuoto, `IsOffscreen=True`, `IsEnabled=False` senza documento (`True` per Guida);
  2. `Button` con `AutomationId='PART_DropDownButton'`, `Name=''`, rettangolo vuoto e fuori schermo (tranne "Effetto calamita", vedi sotto);
  3. `Text` con `ClassName='TextBlock'`, stesso `Name` del primo.
- I separatori sono `ToolBar` 16x34 o 20x34 **senza figli**. Lo spazio vuoto centrale è una `ToolBar` 723x34 senza figli.

| Icona (x centro) | ToolBar (x, y, l x a) | Nome nei Text figli | Note |
|---|---|---|---|
| 1363 | 1341,57 46x34 | Modalità anteprima | prima icona a destra |
| - | 1390,57 16x34 | - | separatore |
| 1430 | 1408,57 46x34 | Disponi | |
| - | 1457,57 16x34 | - | separatore |
| 1497 | 1475,57 46x34 | Trasforma | |
| - | 1524,57 16x34 | - | separatore |
| 1564 | 1542,57 46x34 | Allineamento | |
| - | 1591,57 16x34 | - | separatore |
| 1631 | 1609,57 64x34 | Effetto calamita | unico `PART_DropDownButton` con rettangolo: 1655,57 18x34, visibile (la freccia a destra dell'icona), `IsEnabled=False` |
| - | 1676,57 20x34 | - | separatore |
| - | 1825,57 20x34 | - | separatore |
| 1868 | 1847,57 46x34 | Guida | |
| 538 e 583 | 512,53 101x43 | "Modalità di visualizzazione Vettore", "Modalità di visualizzazione Pixel" + un `TextBlock` senza nome | **due icone nella stessa ToolBar**, ciascun `Text` seguito dal suo `PART_DropDownButton`, tutti con rettangolo vuoto |

Risposta alla domanda sui rettangoli: i `Button` fratelli dei `Text` **non** hanno rettangoli utilizzabili (vuoti e fuori schermo), quindi non si può abbinare il pulsante puntato al nome per posizione dei pulsanti. Per le icone di destra non serve: una `ToolBar` = un'icona = un nome. Per la `ToolBar` di sinistra con due nomi l'unica informazione è l'ordine dei figli (Vettore prima di Pixel), che con ogni probabilità segue l'ordine da sinistra a destra: si potrebbe dividere la larghezza della `ToolBar` per il numero di `Text` con `AutomationId='text'` e scegliere per indice, oppure restare in silenzio (caso ambiguo). Il pulsante "Esporta" (1700-1800) e le schede "Vettore/Pixel/Layout/Canva AI" non sono `ToolBar` e già funzionavano nel primo giro.

## Stato finale

- Excel: nessun processo. Word: solo quello dell'utente (PID 18076, "Documento2 - Word"), mai toccato; nessuna istanza nascosta mia. Affinity (avviato da me, PID 19932): chiuso senza salvare, nessun clic. Esplora file: la mia finestra chiusa, quelle dell'utente intatte. App: chiusa con `--exit`. Finestra dell'immagine: chiusa.
- `settings.json` della cartella di output con `AcceptInjectedEvents` false. Il repository non è stato modificato oltre a questo file.
- Il registro dell'app (`logs\punta-e-ascolta-20260922.log` nella cartella di output) contiene testo letto con `DebugLog` attivo, compresi nomi di file e righe OCR delle finestre dell'utente vicine ai punti di prova.
