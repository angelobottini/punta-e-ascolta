# Sonda empirica Word / Excel via UI Automation (COM UIA3) — 21 settembre 2026

Sonda per "Punta e Ascolta": che cosa restituisce davvero UIA su Word ed Excel su questa macchina, e codice C# riusabile per le due funzioni piu difficili ("leggi la FRASE sotto il puntatore in Word" e "leggi la selezione corrente").

> **Convenzione.** Tutto cio che sta sotto "MISURATO" viene dai log della sonda (`probe/office-uia3-sample/word-log.txt`, `excel-log.txt`). Cio che e solo letto/dedotto e marcato "(letto)" o "(non verificato)".

## 0. Ambiente e metodo (MISURATO)

| Voce | Valore |
|---|---|
| Macchina | ASUS Zenbook, Snapdragon X ARM64, Windows 11 (10.0.26220), display 1920x1200, **DPI finestra 120 = scala 125 %** |
| Client | console .NET **10.0.12**, `win-arm64`, processo **Arm64 nativo**, thread principale **MTA** |
| Interop | NuGet `Interop.UIAutomationClient` **10.19041.0** (assembly `Version=10.0.19041.0`): restore e build OK, gira nativo su ARM64 |
| Office | Word / Excel **16.0.20326.20158**, Click-to-Run `Platform=x64`, lingua it-it. I processi WINWORD/EXCEL risultano **x64 (emulati)** secondo `GetProcessInformation(ProcessMachineTypeInfo)` — anche quello dell'utente |
| Creazione client UIA | `new CUIAutomation8()` + cast a `IUIAutomation2` + timeout: **15,8 – 21,8 ms** |
| DPI | `SetProcessDpiAwarenessContext(-4)` a inizio `Main`; coordinate UIA e `Window.GetPoint` di Word coincidono al pixel (±1 px in verticale) |

Quindi: **client ARM64 nativo -> provider UIA dentro un Office x64 emulato funziona**, con latenze di pochi ms.

Firme dell'interop confermate dal compilatore: `ConnectionTimeout`/`TransactionTimeout` sono `uint`; `AutoSetFocus` e `int`; `GetBoundingRectangles()` torna un array convertibile a `double[]`; `ElementFromPoint(tagPOINT)`; enum `TextUnit.TextUnit_Paragraph`, `TextPatternRangeEndpoint.TextPatternRangeEndpoint_End`.

**Metodo.** Un unico tool C# (`OfficeProbe`) crea la PROPRIA istanza di Word/Excel via COM (`Activator.CreateInstance` + `dynamic`), identifica il PID nuovo per differenza prima/dopo e lo verifica tramite l'hwnd della finestra, lavora solo su documento/cartella nuovi, chiude senza salvare. Nessun clic e nessun tasto: i punti vengono da `Window.GetPoint` (Word) e da `PointsToScreenPixelsX/Y(0)` + `Range.Left/Top` (Excel); la finestra e stata resa TOPMOST con `SetWindowPos(SWP_NOACTIVATE)` per garantire che `ElementFromPoint` colpisse la mia finestra (verificato a ogni punto con `WindowFromPoint` -> PID). L'istanza Word dell'utente (PID 18000) non e mai stata toccata ed era viva alla fine di ogni esecuzione.

Esecuzioni: Word x3, Excel x2. I numeri sotto sono dell'ultima esecuzione salvo indicazione.

---

## 1. Word: la FRASE sotto il puntatore (MISURATO)

### 1.1 Che cosa torna `ElementFromPoint` sul testo

Catena degli antenati (raw view) puntando su una parola:

```
[0] Edit      (50004) class=''     aid='Body'                          Name='Contenuto pagina 1'   TextPattern=True
[1] Custom    (50025) class=''     aid='UIA_AutomationId_Word_Page_1'  Name='Pagina 1'             TextPattern=True
[2] Document  (50030) class='_WwG' fw=Win32                            Name='Documento1'           TextPattern=True
[3] Pane      class='_WwB'   [4] Pane class='_WwF'   [5] Window class='OpusApp' Name='Documento1 - Word'
```

- L'elemento sotto il puntatore e **`Edit` "Contenuto pagina N"** (non il `Document`), `ClassName` e `FrameworkId` **vuoti**, `Name` lungo **18 caratteri**: il Name NON contiene il testo del documento. Nessun `ValuePattern`, `LegacyIAccessible.Value` vuoto.
- **`TextPattern` e disponibile gia sull'elemento colpito (0 livelli di risalita)**, e anche su pagina e documento. `TextPattern2` = si. `SupportedTextSelection` = Multiple.
- In una **cella di tabella**: `DataItem` (50029) senza nome -> `Table` (50036) -> `Edit Body`...; anche qui TextPattern a 0 livelli.
- Su un'**immagine in linea**: `Image` (50006) `Name='Immagine 1'`, `ItemStatus='In linea con il testo'`, rettangolo = quello dell'immagine (547,819,420x130).

### 1.2 Come Word rappresenta il testo in `GetText`

| Cosa | Rappresentazione misurata |
|---|---|
| Fine paragrafo | `\r` (U+000D) incluso in coda al testo di Paragraph e dell'ultima Line. **L'ultimo paragrafo del documento NON ha `\r`** (COM `Content.Text` = 893 caratteri, UIA = 892) |
| A capo automatico | **nessun carattere**: la Line finisce con lo spazio, il testo del paragrafo non contiene interruzioni |
| Interruzione manuale (Maiusc+Invio) | `\v` (U+000B) dentro il paragrafo |
| Fine cella di tabella | `\a` (U+0007) in coda (COM da `\r\a`, UIA solo `\a`) |
| Emoji | coppia surrogata UTF-16 (2 unita), identica a COM: gli offset tornano (`disegno` a offset 46 nel paragrafo, sia COM sia UIA) |
| Immagine in linea | testo **vuoto** (len 0), non U+FFFC |
| Unita Word | include lo spazio finale (`'Rossi '`); la punteggiatura e una "parola" a se (`'. '`) |

### 1.3 Correttezza dell'algoritmo

Algoritmo: `RangeFromPoint` (range degenere) -> validazione sui rettangoli della **Line** -> clone espanso a **Paragraph** -> `prefix` = clone con `End` spostato su `caret.Start` (`MoveEndpointByRange`) -> `offset = prefix.GetText().Length` -> `SentenceSplitter.SentenceAt(paragrafo, offset)`.

**17 bersagli su 17 corretti** (11 al centro della parola + 6 sui bordi). Tutti i casi difficili richiesti sono passati:

| Bersaglio | Frase ottenuta |
|---|---|
| `Rossi` (dopo `sig.`) | Il sig. Rossi arriva alle 15.30. |
| `mele` (dopo `15.30.` e `3,5`) | Porta con se 3,5 kg di mele! |
| `bene` | Va bene? |
| `giardino`, `fermarsi` (frase su 3 righe, a capo automatico) | La seconda frase del paragrafo ... senza fermarsi. |
| `cartella` | Matteo disegna ogni giorno ... dei documenti. |
| `disegno` (dopo l'emoji) | Oggi Matteo e contento [emoji] perche ha finito il disegno. |
| `Bianchi` (`dott.ssa`, `«Bravo!».`) | La dott.ssa Bianchi lo ha visto ieri e ha detto: «Bravo!». |
| `importa` (`12.50`, `ecc. ma`) | Costa 12.50 euro, ecc. ma non importa. |
| `Seconda` (dopo `\v`) | Seconda riga dopo l'interruzione. |
| `chiudi` (dopo `relazione.docx`, `Salva...`) | Poi chiudi tutto. |
| bordo destro di `15.30.` / bordo sinistro di `Porta` | frase giusta da entrambi i lati del confine |
| bordo destro di `bene?` (caret prima di `\r`) e di `capito?` (fine documento) | Va bene? / Hai capito? |

Altri casi: **zoom 150 %** 3/3 corretti; **celle di tabella** 4/4 corretti (il `\a` viene tolto dalla pulizia); **immagine** -> `NotOnText` (corretto: va all'OCR).

**Bug trovato e corretto durante la sonda:** alla prima esecuzione il bordo destro di `bene?` dava `NotOnText`: il caret cade *prima* del `\r` e lo splitter trattava il `\r` come frase a se (vuota). Correzione: un'interruzione forte subito dopo una frase chiusa appartiene a quella frase. Da tenere come test unitario del core.

### 1.4 Millisecondi per chiamata (n = 23-59 per riga)

| Chiamata | min | mediana | max |
|---|---|---|---|
| `ElementFromPoint` (a caldo) | 2,0 | 2,9 | 5,3 |
| `ElementFromPoint` **prima chiamata verso il processo** | — | — | **78 / 100 / 228** (tre esecuzioni) |
| `FindTextHost` | 0,1 | 0,2 | 5,0 |
| `RangeFromPoint` | 0,2 | 0,3 | 4,2 |
| `ExpandToEnclosingUnit` Character/Word/Line/Paragraph | 0,2 | 0,3-0,5 | 1,9 |
| `GetText` (qualsiasi unita) | 0,1 | 0,2 | 0,6 |
| `GetBoundingRectangles` (paragrafo di 6 righe) | 0,1 | 0,5 | 1,9 |
| `MoveEndpointByRange` | 0,1 | 0,2 | 1,9 |
| **`GetSentenceAtPoint` totale** | **3,9** | **5,8** | **18,1** |

Il costo e quasi tutto `ElementFromPoint`; la parte TextPattern vale meno di 2 ms.

### 1.5 Documento grande (231 083 caratteri, 58 pagine; un paragrafo gigante da 20 960 caratteri)

| Caso | Totale | Esito |
|---|---|---|
| paragrafo normale | 10,5 ms (38,5 ms alla prima chiamata dopo la sostituzione del testo) | corretto |
| paragrafo gigante, `MaxChars=8000` (testo troncato a 8000) | 8,1 ms | corretto |
| paragrafo gigante, `MaxChars=40000` (20 960 caratteri letti) | 18,1 ms | corretto |
| ripiego "finestra" **±600 caratteri** (`MoveEndpointByUnit(Character)`) | **144,9 ms** | corretto |
| ripiego "finestra" **±6 righe** (`MoveEndpointByUnit(Line)`) — versione finale | **10,6 ms** (spostamento 2,9 ms) | corretto |

- Un paragrafo che attraversa piu pagine viene restituito intero anche partendo dall'`Edit` della pagina 1.
- `DocumentRange` dell'`Edit` "Contenuto pagina 1" = **solo la pagina** (4757 caratteri, 1,2 ms).
- `DocumentRange.GetText(-1)` sull'elemento `Document` (`_WwG`) ha restituito **65 000 caratteri su 231 083** in 3,2 ms: sembra un tetto del provider (osservato, non documentato).

---

## 2. Selezione (MISURATO)

Selezione fatta via COM (`Range.Select`) da `tavolo` a `colori`: 179 caratteri su 3 righe.

- `TextPattern.GetSelection()` -> 1 range; `GetText` **identico** al testo COM.
- `GetBoundingRectangles()` -> array piatto `x,y,w,h` di **3 rettangoli, uno per riga**, stretti sul testo selezionato:
  `(787,468,546x22)`, `(547,493,773x22)`, `(547,517,45x22)`.
- Classificazione dentro/fuori dai rettangoli: **7 su 7 corretti**, compresi i casi insidiosi (`comuni` = stessa riga ma prima della selezione; `salva` = subito dopo la fine; altro paragrafo; fine paragrafo).
- Solo cursore (selezione collassata): 1 range degenere, testo vuoto, 1 rettangolo -> `HasSelection=false`. **Il criterio giusto e "testo non vuoto", non "numero di rettangoli".**
- `TryGetSelectionAtPoint` totale: **3,1 – 10,5 ms**, mediana 3,5.
- **Elemento con il focus:** quando Word e in primo piano `GetFocusedElement()` torna il `Document` `_WwG` (`Name` di 10 caratteri, TextPattern = si) e `TryGetFocusedSelection` legge i 179 caratteri in 6,0 ms. Quando Word NON e in primo piano torna un elemento dell'app in primo piano (nella prima esecuzione: un `Group` Chromium con `Name` di 664 caratteri) -> la scorciatoia "leggi selezione" legge sempre e solo l'app attiva, come e giusto.
- `app.Activate()` via COM non ha portato Word in primo piano; `SetForegroundWindow` si (ritorno `True`).

---

## 3. Barra multifunzione, schede, barra di stato (MISURATO)

Proprieta ottenute con `ElementFromPoint` al centro dell'elemento:

| Elemento | ControlType / classe | Name | FullDescription | Altro |
|---|---|---|---|---|
| Grassetto (solo icona) | Button `NetUIRibbonButton`, aid `Bold` | `Grassetto` | `Applica il grassetto al testo.` | AccessKey `CTRL+G; ALT, H, 1` |
| Corsivo (solo icona) | Button, aid `Italic` | `Corsivo` | `Applica il corsivo al testo.` | `CTRL+I; ALT, H, 2` |
| Allinea al centro (solo icona) | Button, aid `AlignCenter` | `Allinea al centro` | `Allinea il contenuto al centro.` | `CTRL+E; ALT, H, I L` |
| Elenchi puntati (meta sinistra dello SplitButton) | Button | `Elenchi puntati` | supertip di 2 frasi con `\n\n` | padre: SplitButton `Elenchi puntati` |
| Colore carattere (meta sinistra) | Button | **`Colore carattere Rosso`** (include lo stato) | `Consente di cambiare il colore del testo.` | padre: SplitButton `Colore carattere` |
| **Incolla, meta inferiore** | **MenuItem**, aid `PasteMenu_Dropdown` | **`Altre opzioni`** | `Visualizza le varie opzioni per incollare...` | **padre: SplitButton `Incolla`** |
| Excel: Somma automatica / Unisci (freccia) | MenuItem `..._Dropdown` | **`Altre opzioni`** | descrizione specifica | padre: SplitButton col nome vero |
| Scheda Inserisci | TabItem `NetUIRibbonTab`, aid `TabInsert` | `Inserisci` | vuota | DefaultAction `Passaggio`, `ALT, Y` |
| Barra di stato: pagina | Button `NetUISimpleButton` | `Numero pagina Pagina 1 di 1` | `Il numero di pagina corrente...` | padre StatusBar `Barra di stato` |
| Barra di stato: parole | **Text** | `Conteggio parole 151 parole` | `Il numero di parole nel documento...` | |
| Excel barra di stato | Text / Button | `Modalita Cella Pronto`, `Media 773,6875`, `Somma 3094,75` | | |

Fatti chiave:

- **`HelpText` e SEMPRE vuoto** (0 su 112 elementi in Word, 0 su 121 in Excel). Il supertip sta in **`FullDescription`** (proprieta 30159). `LegacyIAccessible.Description` e `Help` sono vuoti; `Legacy.Name` = `Name`.
- **Copertura dei nomi:** 112 elementi cliccabili visibili in Word, **1 solo senza nome** (un `NetUIAnchor` contenitore della galleria Stili); in Excel **0 su 121**. I pulsanti solo-icona di Office hanno tutti un nome italiano.
- `FrameworkId` = `Win32`; le classi sono `NetUI...`.
- **Trappola:** la freccia degli `SplitButton` si chiama sempre "Altre opzioni": da sola non dice nulla. Il nome utile e quello del padre.
- `DescribeElementAtPoint` totale: **6,5 – 17,4 ms** (mediana 12,3; 29,8 ms la prima volta). La parte cara e la lettura del pattern LegacyIAccessible.
- Enumerare l'intero ribbon (`FindAllBuildCache` sul sottoalbero, 136 elementi): **94 – 113 ms**. Fatto solo per la sonda; l'app non deve mai farlo.

---

## 4. Aree vuote: come NON leggere l'intero documento (MISURATO)

Word era in tema scuro: l'area fuori pagina e quasi nera (`#0F0E0F`), non grigia.

| Punto | `ElementFromPoint` | `RangeFromPoint` | Distanza dalla riga piu vicina | Esito |
|---|---|---|---|---|
| E1 pagina bianca, 200 px sotto l'ultimo paragrafo | Edit "Contenuto pagina 1" | **OK**, range su `il` dell'ultima riga | 215 px | NotOnText |
| E2 bianco a DESTRA di una riga corta | Edit | OK, range sul `\r` | 234 px | NotOnText |
| E3 margine sinistro, 60 px dal testo | Edit | OK, range su `riga` | 60 px | NotOnText |
| E4 margine superiore | **Custom "Pagina 1"** | OK, range su `con` | 71 px | NotOnText |
| E5 / E6 fuori dalla pagina | **Document `_WwG` "Documento1"** | OK, range su una parola del testo | 244 / 246 px | NotOnText |
| E8 margine destro | Edit | OK | 60 px | NotOnText |
| E7 spazio fra due paragrafi, 7 px sotto la riga | Edit | OK, range su `arriva` | 7 px | **Ok: legge la frase di P1** |

Conclusioni misurate:

1. **`RangeFromPoint` non fallisce MAI** in Word: torna sempre il range degenere *piu vicino*, anche a 250 px di distanza e anche fuori dalla pagina. Nessuna eccezione, nessun `null`. **Senza validazione geometrica l'app leggerebbe una frase puntando nel vuoto.**
2. Nessun `Name` contiene il documento (18, 8 e 10 caratteri). Il rischio "leggo tutto il documento" non viene dal `Name` ma da `DocumentRange`/`GetText(-1)`: **non usarli mai**; usare sempre Paragraph + tetto `MaxChars`.
3. La validazione va fatta sull'unita **Line**, non Word/Character: sul bordo destro di una parola il caret cade sulla parola successiva (`'. '`, `'\r'`) e il rettangolo Word/Character sta a 2-5 px dal punto, mentre il rettangolo Line lo contiene sempre (distanza 0 in 17 casi su 17). I rettangoli Line sono stretti sul testo reale della riga, quindi escludono correttamente il bianco a destra delle righe corte.
4. **Tolleranza relativa all'altezza riga** (versione finale): accetto se `dy <= 0,6 x h` e `dx <= 1,0 x h` (h = 22 px a 125 %, 11 pt -> 13 px in verticale). Si adatta da sola a DPI e zoom. Con la tolleranza fissa di 6 px della prima versione, E7 (7 px) dava NotOnText: per un utente con puntamento impreciso e meglio leggere la riga vicina.
5. **Immagine:** `TextPattern` presente, riga con rettangolo = immagine (distanza 0) ma **testo vuoto** -> `NotOnText`. Regola piu semplice e robusta: `ControlType == Image` -> OCR diretto, usando **il rettangolo dell'elemento come regione di OCR**.

---

## 5. Excel (MISURATO)

Catena: `DataItem` (50029) class `XLSpreadsheetCell` -> `DataGrid` `XLSpreadsheetGrid` (Name `Grigia` [sic]) -> Pane `Foglio Foglio1` -> Pane `ExcelGrid` -> ... -> Window `XLMAIN`.

| Cella | Name / AutomationId | `ValuePattern.Value` | COM `Range.Text` | COM `Formula` | ItemStatus |
|---|---|---|---|---|---|
| B2 | `B2` | `Prodotto` | `Prodotto` | | |
| B3 | `B3` | `Mele rosse` | `Mele rosse` | | |
| C3 | `C3` | `1234,5000` | `1234,5000` | `1234.5` | |
| D3 | `D3` | `1,5000 €` | `1,5000 €` | `1.5` | |
| D4 | `D4` | `1851,75000 €` | `1851,75000 €` | `=C3*D3` | **`Contiene Formula.`** |
| E6 (vuota) | `E6` | `` (stringa vuota) | `` | | |

- **`ElementFromPoint` torna la cella SOTTO IL PUNTATORE, non quella attiva** (cella attiva A1, poi D4: stesso risultato). Verificato con `Window.RangeFromPoint` COM sullo stesso punto: 7 su 7 coincidenti.
- **`Name` = coordinata semplice (`C3`)**, senza virgolette ne spazio in questa build (la ricerca, da NVDA, prevedeva `"C" 3`: tenere comunque la pulizia). `AutomationId` = stessa coordinata.
- **`ValuePattern.Value` = testo VISUALIZZATO**, identico a `Range.Text` e allo schermo (verificato sullo screenshot), con separatori italiani e simbolo di valuta. (Il formato `#,##0.00` impostato via COM non localizzato e stato interpretato da Excel italiano come 4 decimali: stranezza dei miei dati di prova, non di UIA.)
- **La formula non e esposta sulla cella**: solo `ItemStatus='Contiene Formula.'`. `Legacy.Value` = `Value`.
- **Barra della formula:** `Edit` class `XLFormulaBarEditor`, aid `FormulaBar`, Name `Barra della formula`, **niente ValuePattern ma TextPattern**: `DocumentRange.GetText` = **`=C3*D3`** (3,2 ms), cioe la formula della cella ATTIVA. `GetSentenceAtPoint` li sopra da `NotOnText` se il puntatore e lontano dal testo (campo largo 1508 px, testo corto): per questo elemento conviene leggere l'intero `DocumentRange` (con tetto).
- Intestazioni: `DataItem` "Intestazione colonna" class `XLGridColumnHeader` Name `C`; "Intestazione di riga" `XLGridRowHeader` Name `3`. Nessun ValuePattern. Casella Nome: `Edit` Win32 Name `Casella Nome`.
- **Attenzione: la cella dichiara `IsTextPatternAvailable = True`** e `GetSentenceAtPoint` su C3 torna `1234,5000`. Funziona, ma il percorso giusto per le celle e `Value`: controllare `ControlType == DataItem` PRIMA di tentare TextPattern.
- Selezione B2:D4: `SelectionPattern.GetCurrentSelection()` sul Pane `Foglio Foglio1` -> **9 elementi** con nome e valore, ordinati **per colonna** (B2,B3,B4,C2,...), 3,8 ms. Per leggerli per riga vanno riordinati.
- **Geometria:** `PointsToScreenPixelsX(Range.Left)` usato direttamente e **sbagliato** (199 invece di 263). Corretto: `PointsToScreenPixelsX(0) + Left x DPI/72 x zoom/100` -> coincide al pixel con il rettangolo UIA (263,363,86x24). Serve solo alle sonde, non all'app.
- Latenze: prima `ElementFromPoint` verso Excel **105 ms** (`DescribeElementAtPoint` 122,5 ms), poi 2,5 – 3,4 ms; `DescribeElementAtPoint` 7 – 20 ms.

---

## 6. Latenze, blocchi, eccezioni (MISURATO)

- **Tipiche:** `ElementFromPoint` ~3 ms; frase completa ~6 ms; selezione ~3,5 ms; descrizione elemento ~12 ms.
- **Massime nel percorso dell'app:** 228 ms (primissimo `ElementFromPoint` verso un processo Word appena avviato); 105 ms verso Excel. E il costo di connessione al provider, una volta per processo bersaglio.
- **Chiamate oltre 300 ms: solo `CreateInstance` di Office** (Word 2867 / 1305 ms, Excel 1084 ms), che l'app non fa mai. **Nessuna chiamata UIA ha superato 300 ms.**
- **`COMException`: nessuna** in 5 esecuzioni; il `Retry` per `RPC_E_CALL_REJECTED` / `RPC_E_SERVERCALL_RETRYLATER` non e mai scattato. Timeout impostati: connessione 1000 ms, transazione 3000 ms, mai raggiunti. Non ho quindi potuto verificare come arrivano in .NET gli errori UIA (la tabella della ricerca resta "letta").
- Unico errore di logica incontrato: lo splitter sul `\r` finale (§1.3), corretto.

---

## Conclusioni per il progetto

1. **La strada scelta e confermata.** `Interop.UIAutomationClient` 10.19041.0 + `CUIAutomation8` funziona nativo su ARM64 verso Office x64 emulato. Frase, selezione ed etichette stanno tutte sotto i 20 ms: il budget di latenza va speso su TTS/OCR, non su UIA.
2. **Word: l'algoritmo paragrafo + offset funziona (17/17)** ed e il percorso principale. Il piano B con il modello a oggetti (`Window.RangeFromPoint` -> `wdSentence`) **non serve**.
3. **Validare SEMPRE `RangeFromPoint`** sui rettangoli dell'unita **Line**, con tolleranza relativa all'altezza riga (0,6 h verticale, 1,0 h orizzontale). E l'unica difesa contro la lettura di testo puntando nel vuoto.
4. **Mai `DocumentRange` / `GetText(-1)`**; sempre Paragraph con `MaxChars` (8000 va bene; il ripiego per paragrafi enormi va fatto **per righe**, 10 ms, non per caratteri, 145 ms).
5. **Ordine delle decisioni per ControlType** sull'elemento colpito: `Image` -> OCR sul rettangolo dell'elemento; `DataItem` con ValuePattern -> `Value` (cella Excel; vuota -> silenzio o "vuota"); `Edit`/`Document`/`Custom`/`DataItem` con TextPattern -> frase; tutto il resto -> etichetta.
6. **Etichette Office: pronunciare `Name`.** `HelpText` e inutile (sempre vuoto); la descrizione estesa e in **`FullDescription`** (candidata per un eventuale "secondo clic = spiegami"). Se `Name` e "Altre opzioni" e il padre e uno `SplitButton`, pronunciare **nome del padre + nome** ("Incolla, altre opzioni"). I nomi della barra di stato sono gia frasi complete.
7. **Non leggere LegacyIAccessible nel percorso normale**: non aggiunge nulla su Office e raddoppia il costo della descrizione. Solo come ripiego quando `Name` e vuoto.
8. **Selezione:** criterio "testo non vuoto"; hit-test sui rettangoli per riga (affidabile 7/7). La scorciatoia "leggi selezione" usa `GetFocusedElement` e quindi vale solo per l'app in primo piano.
9. **Pulizia dopo il taglio**, mai prima: `\r`, `\v`, `\a` vanno tolti solo dalla frase gia estratta; `\r`, `\v`, `\a` valgono come fine frase forte.
10. **Excel:** leggere `Value` (= cio che si vede), non `Name`. Opzione per l'assistente: "coordinata + contenuto". La formula si ottiene solo dalla barra della formula e solo per la cella attiva. Selezione multipla: riordinare per riga.
11. **Riscaldamento:** la prima interrogazione verso ogni processo costa 80-230 ms; accettabile, ma si puo mascherare avviando subito il segnale acustico di "ho capito".
12. **Test unitari del core** da derivare da questa sonda: le 17 frasi + i casi di bordo (`bene?\r`, fine documento senza `\r`, `\v`, `\a`, emoji, `ecc. ma`, `«Bravo!».`).

### Problemi e limiti di questa sonda

- **Excel non termina dopo `Quit()`** quando e stato avviato via COM: in entrambe le esecuzioni il processo (senza finestra) era ancora vivo 15-20 s dopo l'uscita del tool, anche rilasciando tutti gli RCW prima di `Quit`. Ho terminato **solo** il mio PID, dopo averne riverificato l'identita (PID scritto dal tool, ora di avvio, assente dall'elenco "prima", nessuna finestra, diverso da 18000). Word invece e uscito pulito 3 volte su 3. Irrilevante per l'app (non avvia mai Office), rilevante per chi rifara la sonda.
- **NON provati:** Visualizzazione protetta (i file scaricati si aprono cosi: e lo scenario piu a rischio, l'albero UIA potrebbe essere diverso), documenti `.doc` in modalita compatibilita, intestazioni/pie di pagina, caselle di testo, commenti, revisioni, layout Lettura/Web, colonne multiple, PowerPoint, Outlook. Per la Visualizzazione protetta serve una prova con il consenso dell'utente su un file scaricato.
- Non ho potuto provocare errori UIA (app bloccata, elemento distrutto): la mappatura HRESULT -> eccezione resta non verificata.
- Tema scuro di Office: l'area fuori pagina e nera, non grigia (conta per eventuali euristiche sul colore).
- Tetto di 65 000 caratteri di `DocumentRange.GetText(-1)` sul `Document`: osservato una volta, causa non indagata.
- Un comando di pulizia di un file temporaneo e stato bloccato dalla protezione dello strumento; l'ho semplicemente omesso (il file viene comunque riscritto a ogni esecuzione).

---

## Codice finale

File completi (compilano con .NET 10, `win-arm64` e `win-x64`): `docs/research/probe/office-uia3-sample/`

- `UiaPointReader.cs` — **parte riusabile**: `UiaPointReader` (le tre funzioni sotto + helper), `UiaIds`, i record dei risultati e `SentenceSplitter` (portabile, da spostare nel core).
- `Program.cs` — driver usa-e-getta della sonda (crea le proprie istanze Office).
- `OfficeProbe.csproj`, `word-log.txt`, `excel-log.txt`.

Vincoli d'uso: creare e usare `UiaPointReader` su **un solo thread MTA dedicato**; processo **Per-Monitor-V2**; coordinate in pixel fisici.

```csharp
using UIA = Interop.UIAutomationClient;

public UiaPointReader(int connectionTimeoutMs = 1000, int transactionTimeoutMs = 3000)
{
    var a = new UIA.CUIAutomation8();                 // CLSID that implements IUIAutomation2+ (timeouts)
    Automation = a;
    var a2 = (UIA.IUIAutomation2)a;
    a2.ConnectionTimeout = (uint)connectionTimeoutMs;
    a2.TransactionTimeout = (uint)transactionTimeoutMs;
    a2.AutoSetFocus = 0;                              // never move focus as a side effect
    _raw = a.RawViewWalker;
}

/// <param name="toleranceLines">accepted distance from the pointed line, as a fraction of the LINE HEIGHT
/// (vertical: toleranceLines * h, horizontal: 1.0 * h). Scales by itself with DPI and zoom.</param>
public SentenceResult GetSentenceAtPoint(int x, int y, UIA.IUIAutomationElement? element = null, double toleranceLines = 0.6)
{
    try
    {
        var pt = new UIA.tagPOINT { x = x, y = y };
        var el = element ?? Automation.ElementFromPoint(pt);
        if (el is null) return new(SentenceStatus.NoElement, null, null, 0, double.NaN);

        var host = FindTextHost(el);                  // Word: 0 levels up (Edit 'Contenuto pagina N')
        if (host is null) return new(SentenceStatus.NoTextPattern, null, null, 0, double.NaN);
        var tp = (UIA.IUIAutomationTextPattern)host.GetCurrentPattern(UiaIds.TextPattern);

        // degenerate range NEAREST to the point: Word never fails here, even 250 px away -> must validate
        var caret = tp.RangeFromPoint(pt);
        if (caret is null) return new(SentenceStatus.NotOnText, null, null, 0, double.NaN);

        var line = caret.Clone();
        line.ExpandToEnclosingUnit(UIA.TextUnit.TextUnit_Line);
        var lineRects = ToDoubles(line.GetBoundingRectangles());
        double dist = DistanceToRects(lineRects, x, y);
        if (!IsNearLine(lineRects, x, y, toleranceLines)) return new(SentenceStatus.NotOnText, null, null, 0, dist);

        var para = caret.Clone();
        para.ExpandToEnclosingUnit(UIA.TextUnit.TextUnit_Paragraph);

        var prefix = para.Clone();
        prefix.MoveEndpointByRange(UIA.TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, caret,
                                   UIA.TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start);
        string before = prefix.GetText(MaxChars) ?? "";
        string all = para.GetText(MaxChars) ?? "";

        if (before.Length >= MaxChars)
        {
            // huge paragraph: window of +-6 LINES around the caret (10 ms; by Character it costs 145 ms)
            var win = caret.Clone();
            win.MoveEndpointByUnit(UIA.TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start, UIA.TextUnit.TextUnit_Line, -6);
            win.MoveEndpointByUnit(UIA.TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, UIA.TextUnit.TextUnit_Line, 6);
            var wprefix = win.Clone();
            wprefix.MoveEndpointByRange(UIA.TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, caret,
                                        UIA.TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start);
            before = wprefix.GetText(4000) ?? "";
            all = win.GetText(4000) ?? "";
        }

        int offset = Math.Min(before.Length, all.Length);
        string sentence = SentenceSplitter.SentenceAt(all, offset);   // cleans AFTER cutting
        if (sentence.Length == 0) return new(SentenceStatus.NotOnText, null, all, offset, dist);   // e.g. inline picture
        return new(SentenceStatus.Ok, sentence, all, offset, dist);
    }
    catch (Exception ex) when (IsExpected(ex))
    {
        return new(SentenceStatus.Error, null, null, 0, double.NaN, $"{ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
    }
}

/// Reads the selection of the text host under the point and tells whether the point is inside it.
public bool TryGetSelectionAtPoint(int x, int y, out SelectionResult result, double tolerancePx = 2)
{
    result = new(false, false, false, "", Array.Empty<double>());
    try
    {
        var el = Automation.ElementFromPoint(new UIA.tagPOINT { x = x, y = y });
        if (el is null) return false;
        var host = FindTextHost(el);
        if (host is null) return false;
        result = ReadSelection(host, x, y, tolerancePx);
        return result.HasSelection;
    }
    catch (Exception ex) when (IsExpected(ex)) { return false; }
}

/// Selection of the element that has the keyboard focus ("speak the current selection" hotkey).
public bool TryGetFocusedSelection(out SelectionResult result)
{
    result = new(false, false, false, "", Array.Empty<double>());
    try
    {
        var el = Automation.GetFocusedElement();      // Word in foreground: Document '_WwG', TextPattern = yes
        if (el is null) return false;
        var host = FindTextHost(el);
        if (host is null) return false;
        result = ReadSelection(host, int.MinValue, int.MinValue, 0);
        return result.HasSelection;
    }
    catch (Exception ex) when (IsExpected(ex)) { return false; }
}

private SelectionResult ReadSelection(UIA.IUIAutomationElement host, int x, int y, double tolerancePx)
{
    var tp = (UIA.IUIAutomationTextPattern)host.GetCurrentPattern(UiaIds.TextPattern);
    var ranges = tp.GetSelection();
    if (ranges is null || ranges.Length == 0) return new(true, false, false, "", Array.Empty<double>());

    var sb = new StringBuilder();
    var rects = new List<double>();
    for (int i = 0; i < ranges.Length; i++)
    {
        var r = ranges.GetElement(i);
        sb.Append(r.GetText(MaxChars));
        rects.AddRange(ToDoubles(r.GetBoundingRectangles()));   // flat x,y,w,h: one rectangle per visible line
    }
    string text = sb.ToString();
    bool has = text.Length > 0;                       // a caret = one degenerate range = empty text (but 1 rectangle!)
    var arr = rects.ToArray();
    bool inside = has && DistanceToRects(arr, x, y) <= tolerancePx;
    return new(true, has, inside, text, arr);
}

public ElementDescription? DescribeElementAtPoint(int x, int y)
{
    try
    {
        var el = Automation.ElementFromPoint(new UIA.tagPOINT { x = x, y = y });
        return el is null ? null : Describe(el);      // Describe(): see UiaPointReader.cs (every property read is guarded)
    }
    catch (Exception ex) when (IsExpected(ex)) { return null; }
}

// ElementDescription.SpokenText - label policy measured on Office
//   DataItem + ValuePattern -> Value (Excel cell: Name is only the coordinate)
//   else Name -> LegacyName -> HelpText -> FullDescription -> LegacyDescription -> Value
// TODO for the implementers: when Name is generic ("Altre opzioni") and the raw parent is a SplitButton,
//   speak parent.Name + ", " + Name.

public UIA.IUIAutomationElement? FindTextHost(UIA.IUIAutomationElement el, out int levels)
{
    levels = 0;
    UIA.IUIAutomationElement? cur = el;
    while (cur is not null && levels <= MaxClimb)     // MaxClimb = 8
    {
        if (cur.GetCurrentPropertyValue(UiaIds.IsTextPatternAvailable) is bool b && b) return cur;
        cur = _raw.GetParentElement(cur);
        levels++;
    }
    return null;
}

public static double[] ToDoubles(object? safeArray) => safeArray switch
{
    double[] d => d,
    Array a => a.Cast<object>().Select(Convert.ToDouble).ToArray(),
    _ => Array.Empty<double>()
};

/// rects = flat [x,y,w,h, ...]. 0 when the point is inside one rectangle; +inf when there is none.
public static double DistanceToRects(double[] rects, int x, int y)
{
    double best = double.PositiveInfinity;
    for (int i = 0; i + 3 < rects.Length; i += 4)
    {
        double dx = Math.Max(Math.Max(rects[i] - x, x - (rects[i] + rects[i + 2])), 0);
        double dy = Math.Max(Math.Max(rects[i + 1] - y, y - (rects[i + 1] + rects[i + 3])), 0);
        best = Math.Min(best, Math.Sqrt(dx * dx + dy * dy));
    }
    return best;
}

/// Tolerance relative to the rectangle height, so it follows font size, zoom and DPI.
public static bool IsNearLine(double[] rects, int x, int y, double toleranceLines)
{
    for (int i = 0; i + 3 < rects.Length; i += 4)
    {
        double h = rects[i + 3];
        if (h <= 0) continue;
        double dx = Math.Max(Math.Max(rects[i] - x, x - (rects[i] + rects[i + 2])), 0);
        double dy = Math.Max(Math.Max(rects[i + 1] - y, y - (rects[i + 1] + h)), 0);
        if (dx <= h && dy <= toleranceLines * h) return true;
    }
    return false;
}

public static bool IsExpected(Exception ex) => ex is COMException or TimeoutException or InvalidOperationException
    or UnauthorizedAccessException or ArgumentException or NotImplementedException or InvalidCastException
    or NullReferenceException;
```

`SentenceSplitter` (regole misurate sui 17 casi: abbreviazioni che non chiudono mai la frase, punto seguito da minuscola = non e un confine, `\r \n \v \a` = confine forte che appartiene alla frase precedente, spazi finali attribuiti alla frase che li precede, pulizia solo dopo il taglio) e nel file `UiaPointReader.cs`: non lo riporto qui perche contiene sequenze di escape Unicode che vanno copiate dal sorgente.
