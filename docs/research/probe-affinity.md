# Sonda su Affinity 3.3 (Canva, ARM64): cosa espone a UI Automation e quanto bene lo legge l'OCR di Windows

Data delle prove: 21/09/2026, ASUS Zenbook Snapdragon X, Windows 11 25H2 (build 26220), schermo 1920x1200 con scala 125% (`AppliedDPI=120`), interfaccia di Affinity in italiano, tema scuro.

Legenda: **[MISURA]** = misurato su questa macchina (log e immagini in `docs/research/probe/`); **[MISURA-OFFLINE]** = misurato da me oggi rielaborando le immagini già catturate, senza riaprire Affinity; **[LETTO]** = dedotto da documentazione o conoscenza generale, non verificato qui; **[NON MISURATO]** = dato mancante.

---

## 0. Sintesi

1. **Affinity 3.3 NON è invisibile a UI Automation.** È un'applicazione **WPF su .NET Framework 4.8** (`FrameworkId='WPF'`), processo ARM64 nativo. Barra dei menu, **tutte le voci dei menu aperti** (con nome pulito, senza scorciatoia), schede dei pannelli, selettore degli studi, tooltip e nomi degli strumenti sono nell'albero UIA. **[MISURA]**
2. **Il contenuto dei pannelli (Colore, Paragrafo, Pagine, Trasforma ...) invece non è esposto**: `FromPoint` lì restituisce il `TabControl` dell'intero pannello (331x425 px) con nome vuoto. Per i pannelli, l'area di lavoro e la barra di stato l'OCR resta obbligatorio. **[MISURA]**
3. I 40 pulsanti hanno tutti `Name` vuoto; 7 hanno `HelpText`; i 17 strumenti della palette hanno però un figlio `Text` **fuori schermo con rettangolo vuoto** che contiene il nome ("Strumento Penna"). Le barre con icone espongono il nome allo stesso modo. **[MISURA]**
4. Il popup di un menu è una **finestra top-level separata** (`HwndWrapper[Affinity.exe;;<guid>]`, `WS_EX_LAYERED|TOPMOST|NOACTIVATE|TOOLWINDOW`); **`Graphics.CopyFromScreen` lo cattura per intero**, sottomenu compreso, e né la cattura né `FromPoint` lo chiudono. **[MISURA]**
5. **OCR di Windows (motore it-IT) sul menu File reale: 26 etichette su 26 corrette già a 1x, comprese le 11 disabilitate**; le scorciatoie passano da 3/10 (1x) a 8/10 (1.5x-3x), 9/10 a 2x in grigi, 7/10 a 4x. **L'inversione dei colori non cambia nulla** (stessi rettangoli e stesso testo, carattere per carattere). **[MISURA]**
6. **Auto-levels / gamma su tutta l'immagine peggiorano il testo chiaro** (a 1.5x: 23-25 etichette su 26 invece di 26; "Ctrl" diventa "Ctr1"/"Ctd"), ma sono **indispensabili per il testo a contrasto molto basso** (differenza di luminanza sotto circa 45 su 255: "S: 100", "Esporta" disabilitato: 0 letture su 6 senza trattamento, 6 su 6 con). Quindi: trattamento solo in un **secondo passaggio**. **[MISURA]**
7. Le scorciatoie a destra arrivano dall'OCR come **righe separate** sulla stessa Y dell'etichetta (0 fusioni su 10 righe con scorciatoia in 15 varianti): si tolgono scartando la riga, non ripulendo il testo. **[MISURA]**
8. In una cattura 900x300 la riga sotto il puntatore si isola bene con i rettangoli: con la logica proposta (segmenti per spazi > 0,6 altezze di riga, regola per le scorciatoie, secondo passaggio) **20/25 casi esatti a 1.5x in media 38 ms**, 24/25 se si ignorano maiuscole/spazi e si allarga la regola "etichetta più vicina". 1x è insufficiente (16/25; la riga evidenziata "Apri Recenti" diventa "ri Recenti"), 3x è peggiore e più lento. **[MISURA-OFFLINE]**
9. I tooltip compaiono (4 su 4 sui controlli abilitati entro 2,2 s), sono una finestra top-level separata, **esposti a UIA come `ToolTip` con `Name` = testo**, trasparenti al hit-test, chiari su scuro e leggibili dall'OCR a 1.5x/2x (4 su 4). Il tempo esatto di comparsa **non è stato misurato**.

---

## 1. Metodo e limiti

- La sonda è stata eseguita da un agente precedente con script PowerShell 5.1 (processo ARM64) in `scratchpad\probe\` (`common.ps1`, `05-menu.ps1`, `07-ocr-variants.ps1`, `08-ocr-geometry.ps1`, `09-tooltips.ps1`, `10-samples.ps1`, `12-lowcontrast.ps1`) e con un piccolo eseguibile .NET 10 ARM64 (`uia3\Program.cs`, pacchetto `Interop.UIAutomationClient` 10.19041.0, `CUIAutomation8`). Affinity è stato aperto, interrogato e richiuso; nessun documento è stato creato, aperto o salvato.
- Io **non ho riaperto Affinity né inviato input**. Ho letto log, script e immagini; ho verificato a occhio la verità di riferimento sulle immagini (`affinity-menu-file2-popup.png`, `affinity-menu-visualizza-popup.png`, `affinity-samples-full.png`, `affinity-menu-file2-hover.png`, tooltip, basso contrasto); ho aggiunto tre rielaborazioni **offline** sulle stesse immagini (script in `scratchpad\spikes\affinity-report\`, risultati copiati in `docs/research/probe/affinity-offline-extra.txt`, `affinity-selection-eval.txt`, `affinity-selection-eval-20H.txt`).
- Ho letto in sola lettura i metadati del pacchetto installato (`Get-AppxPackage`, manifest, `Affinity.exe.config`, intestazione PE).
- **Limite principale: tutte le prove sono state fatte senza alcun documento aperto.** Molti comandi e strumenti erano disabilitati; testo sulla tela, pannello Livelli con contenuto, barra contestuale con uno strumento attivo, finestre di dialogo (Nuovo documento, Esporta, Preferenze), elenchi a discesa aperti e tema chiaro **non sono stati provati**.
- I tempi OCR della sonda originale sono stati presi mentre sulla macchina giravano altri 7 agenti: sono rumorosi (stessa immagine 2x: 146 ms nella sonda, 62 ms nella mia ripetizione). Vanno letti come ordini di grandezza.

---

## 2. Finestra e processo **[MISURA]**

| Dato | Valore |
| --- | --- |
| Pacchetto | `Canva.Affinity_3.3.0.4850_arm64__8a0j1tnjnt4a4` (MSIX, `EntryPoint="Windows.FullTrustApplication"`) |
| Eseguibile | `C:\Program Files\WindowsApps\Canva.Affinity_3.3.0.4850_arm64__8a0j1tnjnt4a4\App\Affinity.exe`, versione file 3.3.0.4850, PE machine `0xAA64` (ARM64 nativo) |
| Runtime | `Affinity.exe.config`: `<supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.8" />`; nella cartella `App` non c'è un runtime .NET proprio (ci sono `Serif.Affinity.dll`, `Serif.Windows.dll`) -> **WPF su .NET Framework** |
| Avvio usato dalla sonda | `shell:AppsFolder\Canva.Affinity_8a0j1tnjnt4a4!Canva.Affinity` |
| Nome processo | `Affinity` (pid 10888 durante la sonda), un solo processo |
| Finestra principale | classe `HwndWrapper[Affinity.exe;;002c344e-...]`, titolo `Affinity`, massimizzata: rect (-9,-9,1929,1149), style `0x17CF0000`, ex `0x00040100` |
| Schermata di benvenuto | seconda finestra top-level, stessa famiglia di classe, **titolo vuoto**, rect uguale; in UIA compare anche come figlia `Window` della principale |
| Classe delle finestre | sempre `HwndWrapper[Affinity.exe;;<GUID diverso per ogni finestra>]`: finestra principale, popup dei menu e tooltip **non si distinguono dal nome di classe**, solo dagli stili |
| Geometria a 125% | barra dei menu alta 37 px; righe dei menu 33 px (separatori 6 px); altezza delle maiuscole 11 px, riquadro OCR di una riga 11-15 px; passo delle righe nei pannelli circa 30 px |
| Motore OCR della sonda | `Windows.Media.Ocr`, lingua `it-IT` (unica lingua OCR installata), `MaxImageDimension=10000` |

Il percorso dell'eseguibile e l'elenco delle finestre stampati da `01-launch.ps1` sono andati solo su console e non sono stati salvati: i dati sopra vengono dai log dei menu e dalla mia lettura del pacchetto.

---

## 3. Cosa espone UI Automation

### 3.1 Conteggi **[MISURA]**

Finestra principale, nessun documento aperto (`affinity-uia-tree-main.txt`, `affinity-uia3-com.txt`):

| Client | Vista | Elementi | Con `Name` | Con `HelpText` | Tempo |
| --- | --- | --- | --- | --- | --- |
| Gestito (`System.Windows.Automation`, PowerShell), visita con `TreeWalker` fino a profondità 7 | raw | 178 | 87 | 7 | 1628 ms |
| COM UIA3 (`CUIAutomation8`, .NET 10 ARM64), `FindAll(Descendants)` | control | 101 | 43 | 7 | 120 ms |
| COM UIA3, `FindAllBuildCache` con `RawViewCondition` + cache di `Name` | raw | 183 | 91 | - | 198 ms |

Per tipo (vista raw gestita): Button 40 (**0 con nome**), Text 61 (59 con nome), MenuItem 10 (10), TabItem 13 (13), ListItem 4 (4), ToolBar 14 (0), Separator 18, Thumb 7, Tab 4, Custom 3, Menu 1, List 1, StatusBar 1 (senza figli), Window 1.

Con la schermata di benvenuto aperta (due finestre insieme, `affinity-uia-tree-welcome.txt`): 502 elementi, 271 con nome, 89 con `HelpText`, 4197 ms con la visita gestita. La schermata di benvenuto è ben esposta: nomi dei file recenti, schede, `HelpText` "Rimuovi", "Attiva/Disattiva preferito", "Home", "Apri", "Nuova".

COM UIA3 da processo .NET 10 **ARM64** verso Affinity ARM64 funziona senza accorgimenti (`new CUIAutomation8()`, `ElementFromHandle`, `ElementFromPoint`, `FindAllBuildCache`, `IUIAutomationElement6.CurrentFullDescription` sempre vuoto).

### 3.2 Qualità dei nomi **[MISURA]**

| Caso | Cosa c'è nell'albero | Conseguenza |
| --- | --- | --- |
| Voci di menu | `MenuItem Name='Salva con nome…'` con figli `Text Name='Salva con _nome…'` (trattino basso = tasto di accesso) e `Text Name='Ctrl+Maiusc+S'` (stringa vuota se non c'è scorciatoia) | pronunciare il `Name` del `MenuItem`: è già senza trattino basso e **senza scorciatoia** |
| Separatori dei menu | `MenuItem Name='Serif.Affinity.Workspaces.WorkspaceMenuSeparator'`, alto 6 px (12 nel menu File, 8 in Visualizza) | riconoscere i nomi che sono nomi di tipo .NET e tacere |
| Selettore studi (Vettore, Pixel, Layout, Canva AI) | `ListItem Name='Serif.Affinity.Workspaces.Workspace'` con figlio `Text Name='Layout'` | il nome buono è nel **figlio** |
| Schede dei pannelli | `TabItem Name='StudioPage, Title = Colore'` con figlio `Text Name='Colore'` (`AutomationId='TabTitle'`) | usare il figlio, oppure estrarre ciò che segue `Title = ` |
| Pulsanti | 40 su 40 con `Name=''`; 7 con `HelpText` ("Home", "Gestione studio", "Esporta ", "Personalizza strumenti", "Imposta riempimento", "Imposta tratto", "Scambia colore tratto con colore di riempimento [Shift + X]") | `HelpText` come seconda scelta |
| Palette strumenti | 18 `Button AutomationId='btn'`, 17 con figlio `Text AutomationId='text'`, `IsOffscreen=True`, **rettangolo vuoto**, `Name='Strumento Penna'` ecc. | cercare i `Text` discendenti **anche se fuori schermo e senza rettangolo** |
| Barre con icone | `ToolBar Name=''` i cui figli sono `Text` fuori schermo con il nome ("Modalità anteprima", "Disponi", "Trasforma", "Allineamento", "Effetto calamita", "Guida"); `FromPoint` sull'icona restituisce la `ToolBar` | se tutti i `Text` figli hanno lo stesso nome, è il nome dell'icona; una barra ne ha due diversi ("Modalità di visualizzazione Vettore" / "... Pixel"): lì il nome è ambiguo e serve il tooltip |
| Schermata di benvenuto | `RadioButton Name='home' HelpText='Home'`, `Name='newdocopen' HelpText='Apri'` | se il nome sembra un identificatore, preferire `HelpText` |
| Rettangoli | elementi fuori schermo con rettangolo vuoto (gestito: `Rect.Empty`, COM: `0,0,0,0`); un elemento della schermata di benvenuto ha rettangolo **infinito** (la sonda è andata in errore convertendo "∞" in intero) | proteggere il codice da rettangoli vuoti, infiniti e NaN |
| Contenuto dei pannelli, area di lavoro, barra contestuale, barra di stato | **niente**: `Tab` senza altri figli che le schede; `Custom ContextBarHost` con il solo pulsante di overflow; `StatusBar` senza figli (senza documento aperto) | OCR |

`IsEnabled` è stato registrato solo dal test COM e solo per pochi elementi ("Esporta " e "Strumento Penna": `enabled=0`). **Per le voci di menu disabilitate lo stato `IsEnabled` non è stato registrato**; nell'albero sono comunque presenti con lo stesso nome delle voci abilitate.

---

## 4. `FromPoint` su menu aperti e pannelli **[MISURA]**

### 4.1 Menu aperti (File: 26 voci + 12 separatori; Visualizza: 19 voci + 8 separatori)

18 punti provati (6 per ciascuna delle tre aperture), al centro della riga e sull'etichetta, senza e con il mouse realmente sopra:

- Il risultato è **sempre** il `MenuItem` della riga (`Name` pulito, rettangolo 478x33 o 422x33) **oppure** il suo figlio `Text` con l'etichetta (`Name` con trattino basso, es. `_Chiudi`, `Apri _Recenti`), il cui genitore è il `MenuItem`. Mai un contenitore, mai un elemento sbagliato.
- Sul separatore restituisce il `MenuItem` separatore con il nome di tipo .NET.
- Il risultato **non dipende dalla posizione reale del mouse** (stesso esito con e senza hover).
- Tempi del client gestito: 12-32 ms la prima chiamata dopo l'apertura, poi 1-6 ms. COM UIA3 sulla finestra principale: 3-6 ms per chiamata.
- Le voci del menu sono figlie UIA del `MenuItem` della barra ("File"), non della finestra popup; la finestra popup è `Window Class='Popup'` e ha come genitore UIA la finestra `Affinity`.
- Con il puntatore sopra la colonna delle scorciatoie `FromPoint` non è stato provato: dall'albero ci si aspetta il `Text` della scorciatoia (o quello vuoto), sempre figlio dello stesso `MenuItem`. La regola "risali al `MenuItem`" copre anche questo caso. **[NON MISURATO]**

### 4.2 Finestra principale (10 punti gestiti + 10 punti COM)

| Punto | Risultato di `FromPoint` | Utile? |
| --- | --- | --- |
| Voce della barra dei menu "Documento" | `Text '_Documento'` -> genitore `MenuItem 'Documento'` | sì |
| Pulsante "Esporta" (disabilitato) | `Text 'Esporta '` -> genitore `Button` con `HelpText='Esporta '` | sì |
| Studio "Layout" | `ListItem 'Serif.Affinity.Workspaces.Workspace'` -> figlio `Text 'Layout'` | sì, dal figlio |
| Scheda "Navigatore" | `TabItem 'StudioPage, Title = Navigatore'` -> figlio `Text 'Navigatore'` | sì, dal figlio |
| Strumento Penna (palette) | `Button 'btn'` nome vuoto -> figlio `Text 'Strumento Penna'` fuori schermo | sì, dal figlio |
| Icona "Modalità anteprima" | `ToolBar` nome vuoto -> figli `Text 'Modalità anteprima'` fuori schermo | sì, dai figli |
| Etichette e campi dentro i pannelli ("Interlinea", "Somma spazio prima e dopo", "Spaziatura", "S: 100", "Solo tra paragrafi", "Pagine mastro", icona contagocce) | `Tab` (il `TabControl` dell'intero pannello, fino a 329x962 px), nome vuoto, figli = sole schede | **no -> OCR** |
| Area di lavoro vuota | `Window 'Affinity'` | **no -> OCR** |

Tempi: 3-17 ms (150 ms la primissima chiamata a freddo del client gestito).

---

## 5. Popup dei menu: finestra separata e cattura dello schermo **[MISURA]**

- All'apertura di un menu compare **una nuova finestra top-level** dello stesso processo: classe `HwndWrapper[Affinity.exe;;<guid>]`, titolo vuoto, style `0x96000000` (`WS_POPUP|WS_VISIBLE|WS_CLIPSIBLINGS|WS_CLIPCHILDREN`), ex `0x08080088` = `WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`, `cloaked=0`. File: rect (55,35)-(541,973) = 486x938; Visualizza: (556,35)-(986,718) = 430x683.
- La finestra in primo piano resta quella principale di Affinity.
- **`Graphics.CopyFromScreen` (BitBlt dallo schermo) contiene il popup per intero**, e anche il sottomenu "Apri Recenti" aperto dall'hover: verificato a occhio su `affinity-menu-file2-full.png`, `affinity-menu-file2-hover.png`, `affinity-menu-file2-popup.png`, `affinity-menu-visualizza-popup.png`. Nessuna zona nera o mancante nonostante `WS_EX_LAYERED`.
- Il menu resta aperto dopo la cattura e dopo le chiamate `FromPoint` (`ExpandCollapseState=Expanded` prima e dopo).
- Apertura con clic reale: circa 1,1 s fra il clic e la prima lettura (il tempo comprende una pausa fissa di 900 ms dello script, quindi non è una misura della latenza di Affinity).
- Se il sottomenu sia a sua volta una finestra separata **non è stato registrato** (lo script elenca le finestre nuove solo subito dopo l'apertura); per come funziona WPF è molto probabile **[LETTO]**.
- **Non provato**: se un clic centrale (che la nostra app intercetterà e consumerà) chiude il menu o il tooltip.
- Incidente della sonda, utile come avvertenza: nella prima prova la finestra di Affinity era ridotta a icona e `ExpandCollapsePattern.Expand()` ha aperto il popup a (0,0) sopra un'altra applicazione; `Esc` non lo ha richiuso, è servito `Collapse()`. La nostra app deve usare UIA **solo in lettura**, mai i pattern di azione. I dati validi sono quelli delle prove `file2` e `visualizza` (apertura con clic reale).

---

## 6. OCR di Windows sui menu scuri: tabella delle varianti

### 6.1 Menu File, 486x938 px, 36 righe di testo = 26 etichette (11 disabilitate) + 10 scorciatoie **[MISURA]**

Verità di riferimento controllata a occhio su `affinity-menu-file2-popup.png` e coincidente con l'albero UIA. Confronto esatto, maiuscole comprese, dopo aver tolto i puntini finali. Ridimensionamento `HighQualityBicubic`; "grigi" = `ColorMatrix` in scala di grigi con contrasto x1,8. OCR = mediana di 3 esecuzioni del solo `RecognizeAsync`; prep = ridimensionamento + matrice colore, una sola esecuzione (GDI+ da PowerShell, rumoroso).

| Variante | Immagine | Etichette | Disabilitate | Scorciatoie | **Righe giuste / totali** | OCR ms | Prep ms |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 1x normale | 486x938 | 26/26 | 11/11 | 3/10 | **29/36** | 32 | 35 |
| 1x invertita | 486x938 | 26/26 | 11/11 | 3/10 | 29/36 | 32 | 71 |
| 1.5x normale | 729x1407 | 26/26 | 11/11 | 8/10 | **34/36** | 56 | 27 |
| 1.5x invertita | 729x1407 | 26/26 | 11/11 | 8/10 | 34/36 | 56 | 66 |
| 2x normale | 972x1876 | 26/26 | 11/11 | 8/10 | 34/36 | 146 | 152 |
| 2x invertita | 972x1876 | 26/26 | 11/11 | 8/10 | 34/36 | 135 | 106 |
| 2x grigi | 972x1876 | 26/26 | 11/11 | 9/10 | **35/36** | 122 | 104 |
| 2x grigi invertita | 972x1876 | 26/26 | 11/11 | 9/10 | 35/36 | 114 | 189 |
| 2.5x invertita | 1215x2345 | 26/26 | 11/11 | 8/10 | 34/36 | 127 | 136 |
| 3x normale | 1458x2814 | 26/26 | 11/11 | 8/10 | 34/36 | 134 | 92 |
| 3x invertita | 1458x2814 | 26/26 | 11/11 | 8/10 | 34/36 | 119 | 199 |
| 3x grigi | 1458x2814 | 26/26 | 11/11 | 8/10 | 34/36 | 111 | 331 |
| 3x grigi invertita | 1458x2814 | 26/26 | 11/11 | 8/10 | 34/36 | 106 | 148 |
| 4x normale | 1944x3752 | 26/26 | 11/11 | 7/10 | 33/36 | 217 | 139 |
| 4x invertita | 1944x3752 | 26/26 | 11/11 | 7/10 | 33/36 | 195 | 432 |

Osservazioni:

- **Normale e invertita danno risultati identici fino al pixel** (stesso testo, stessi rettangoli) a ogni scala: l'inversione è inutile per questo motore.
- Errori tipici: a 1x `Ctrl` -> `CtrI` (7 scorciatoie su 10); `Ctrl+P` non è mai esatto (`Ctrl+p`, `Ctrl + p` o riga assente); a 4x `Ctrl+Alt+W` -> `Ctrl+Alt+VV/` e sparisce `Ctrl+Alt+H`: **ingrandire troppo peggiora**.
- Le frecce dei sottomenu a destra non generano righe spurie.

### 6.2 Stesso menu con gamma e auto-levels su tutta l'immagine **[MISURA]** + **[MISURA-OFFLINE]**

Pipeline di `12-lowcontrast.ps1`: grigi -> gamma -> stiramento fra i percentili 1% e 99,5% -> eventuale inversione. Prima colonna dei tempi: sonda originale (una esecuzione, macchina carica); seconda: mia ripetizione (mediana di 3).

| Variante | Etichette | Scorciatoie | **Righe giuste / totali** | OCR ms (sonda) | OCR ms (ripetizione) |
| --- | --- | --- | --- | --- | --- |
| 1.5x normale | 26/26 | 8/10 | 34/36 | 107 | 53 |
| 1.5x gamma 0,5 + levels | 23/26 | 5/10 | 28/36 | 243 | 46 |
| 1.5x gamma 0,5 + levels + inversione | 23/26 | 5/10 | 28/36 | 199 | 55 |
| 1.5x gamma 0,35 + levels + inversione | 23/26 | 5/10 | 28/36 | 103 | 54 |
| 1.5x solo levels + inversione | 25/26 | 5/10 | 30/36 | 113 | 50 |
| 2x normale | 26/26 | 8/10 | 34/36 | 169 | 62 |
| 2x gamma 0,5 + levels | 25/26 | 7/10 | 32/36 | 101 | 64 |
| 2x gamma 0,5 + levels + inversione | 25/26 | 7/10 | 32/36 | 141 | 62 |
| 2x gamma 0,35 + levels + inversione | 24/26 | 7/10 | 31/36 | 118 | 69 |
| 2x solo levels + inversione | 26/26 | 9/10 | **35/36** | 167 | 61 |

La mia ripetizione mostra **quali** righe si perdono: sempre voci **chiare e abilitate** ("Apri…", "Accedi…" letto `Accedi„`, "Esci" che sparisce, "Nuovo processo immagine" spezzato in due) e le scorciatoie chiare (`Ctr1+N`, `Ctd+O`, `CtrI+AIt+Maiusc+N`); le 11 voci disabilitate restano 11/11 in tutte le varianti. La gamma gonfia i tratti del testo già ben contrastato.

### 6.3 Menu Visualizza, 430x683 px, 19 etichette (10 disabilitate) + 3 scorciatoie (`Ctrl+Maiusc+W`, `\`, `Tab`) **[MISURA-OFFLINE]**

| Variante | Etichette | Disabilitate | Scorciatoie | Righe giuste / totali | OCR ms |
| --- | --- | --- | --- | --- | --- |
| 1x normale | 19/19 | 10/10 | 1/3 | 20/22 | 17 |
| 1.5x normale | 19/19 | 10/10 | 1/3 | 20/22 | 27 |
| 2x normale | 19/19 | 10/10 | 2/3 | 21/22 | 36 |
| 3x normale | 19/19 | 10/10 | 2/3 | 21/22 | 83 |
| 4x normale | 19/19 | 10/10 | 2/3 | 21/22 | 131 |
| solo levels + inversione, 1x / 1.5x / 2x / 3x / 4x | 19/19 | 10/10 | come sopra | come sopra | 17 / 30 / 45 / 78 / 209 |

La scorciatoia di un solo carattere `\` non viene mai rilevata (nessuna riga): innocuo.

---

## 7. Testo a basso contrasto

### 7.1 Quanto contrasto c'è **[MISURA-OFFLINE]** (luminanza 0-255 campionata sulle catture a 1x: minimo = sfondo, massimo = cuore del tratto)

| Testo | Sfondo | Testo | Differenza |
| --- | --- | --- | --- |
| Voce di menu abilitata | 27 | 240 | 213 |
| Voce di menu disabilitata (e sua scorciatoia) | 27 | 147 | 120 |
| Riga di menu evidenziata dall'hover ("Apri Recenti") | 102 | 240 | 138 |
| Intestazione di sezione nel pannello ("Spaziatura") | 40 | 242 | 202 |
| Etichetta disabilitata "Somma spazio prima e dopo" | 54 | 121 | 67 |
| Etichetta grigia "Interlinea" | 54 | 113 | 59 |
| Campo esadecimale disabilitato "FF0000" | 54 | 97 | 43 |
| Pulsante "Esporta" disabilitato | 71 | 105 | 34 |
| Valori piccoli "H: 0 / S: 100 / L: 50" nel pannello Colore | 54 | 84 | 30 |
| Tooltip (testo scuro su chiaro) | 242 | 87 | 155 |

### 7.2 Lettura **[MISURA]** (`affinity-ocr-lowcontrast.txt`: 5 punti x 2 regioni (900x300 e 400x120) x 3 scale (1.5x, 2x, 3x) x 5 pipeline; conta la riga trovata sotto il puntatore)

| Testo atteso | Senza trattamento | gamma 0,5 + levels | idem + inversione | gamma 0,35 + levels + inv. | solo levels + inv. |
| --- | --- | --- | --- | --- | --- |
| "S: 100" (diff. 30) | **0/6** | 6/6 | 6/6 | 6/6 | 6/6 (letto `s: 100`) |
| "Esporta" disabilitato (diff. 34) | **0/6** | 6/6 | 6/6 | 6/6 | 6/6 |
| "FF0000" (diff. 43) | 2/6 (una volta esatto, una `FFOOOO`) | 4/6 | 4/6 | 6/6 | 4/6 (quasi sempre `n: FFOOOO`: zeri letti come O) |
| "Somma spazio prima e dopo" (diff. 67) | 6/6 | 6/6 | 6/6 | 6/6 | 6/6 |
| "Interlinea" (diff. 59) | 6/6 | 6/6 | 6/6 | 5/6 (`Interli`) | 6/6 |
| Voci di menu disabilitate (diff. 120) | 11/11 e 10/10 a ogni scala, anche 1x | 11/11 | 11/11 | 11/11 | 11/11 |

Conclusioni dai dati:

- Le **voci di menu disabilitate non sono un problema**: si leggono come quelle abilitate, a qualsiasi scala.
- Il confine è intorno a una differenza di luminanza di **45-60**: sopra si legge senza trattamento, sotto il motore non rileva nemmeno la riga e nessun ingrandimento aiuta (0/6 a 1.5x, 2x e 3x).
- Sotto soglia basta **grigi + auto-levels**; la gamma non aggiunge nulla e l'inversione è di nuovo ininfluente.
- Il trattamento costa poco sul ritaglio piccolo: 400x120 a 2x = 6-24 ms di preparazione + 4-30 ms di OCR.
- A 1x (test `10-samples`) "Somma spazio prima e dopo" diventa `dapo` e "Esporta" diventa `Esparü`: 1x non va usato.
- I valori numerici restano fragili (`FFOOOO`, `10096` per "100 %" a 2x): vanno pronunciati così come sono, senza contarci troppo.

---

## 8. Scorciatoie da tastiera al margine destro delle righe

**[MISURA]**

- In UIA la scorciatoia è un `Text` figlio separato (`Name='Ctrl+Alt+Maiusc+N'`, stringa vuota se assente) e **non compare nel `Name` del `MenuItem`**: sul percorso UIA non c'è niente da togliere. (La proprietà `AcceleratorKey` non è stata registrata.)
- Nell'OCR la scorciatoia arriva come **`OcrLine` distinta**, sulla stessa Y dell'etichetta, più a destra: il blocco delle scorciatoie inizia a x = sinistra del popup + 328 px (File, largo 486) o + 298 px (Visualizza, largo 430); il vuoto fra fine etichetta e scorciatoia è stato di almeno 142 px (142-262 px nei casi controllati), cioè oltre 10 volte l'altezza della riga. **0 righe fuse su 10 in tutte le 15 varianti**, e anche nelle catture 900x300 ("Salva con nome..." e "CtrI+Maiusc+S" separate).
- Forme viste nell'OCR: `Ctrl+N`, `CtrI+W`, `Ctr1+N`, `Ctd+O`, `CtrI+AIt+Maiusc+N`, `Ctrl + p`, `Ctrl+Alt+VV/`, `Tab`; `\` mai rilevato.

Come toglierle (ordine consigliato):

1. **Per posizione**: fra i segmenti sulla riga del puntatore, quello che ha un altro segmento alla sua sinistra sulla stessa riga e corrisponde al modello qui sotto è una scorciatoia e non si pronuncia.
2. **Modello tollerante agli errori OCR**: `^(Ctr\S?|Ctd|Alt|AIt|Maiusc|Shift|Tab|Canc|Ins|Invio|Esc|Win|F\d{1,2})(\s*\+\s*\S+)*$`.
3. **Puntatore sopra la scorciatoia** -> pronunciare l'etichetta più vicina a sinistra sulla stessa riga. Verificato offline: (420,224) -> "Nuovo da Appunti"; (880,694) su `Tab` -> "Attiva/Disattiva UI", a tutte le scale.
4. **Rete di sicurezza per un'eventuale fusione** (mai vista qui): spezzare la riga dove lo spazio fra due parole supera 1,5 altezze di riga e scartare il segmento finale che corrisponde al modello.

---

## 9. Isolare la riga sotto il puntatore in una cattura di circa 900x300

### 9.1 Dati della sonda **[MISURA]**

Criterio usato: riga candidata se la Y del puntatore cade nel riquadro della riga allargato di 0,35 altezze; poi si guarda se la X è dentro il riquadro.

- I rettangoli OCR, riportati alle coordinate dello schermo, coincidono con quelli UIA entro 1-2 px (es. "Salva con nome...": OCR x=81, larghezza 115; UIA x=81, larghezza 116). Con righe a passo 33 px e riquadri alti 11-15 px non c'è mai ambiguità verticale fra righe vicine.
- Menu con sottomenu aperto (`affinity-ocr-geometry.txt`, 6 punti x 4 scale): sulla riga del puntatore ci sono 1-3 righe OCR (etichetta, scorciatoia, voce del sottomenu a destra) e la X le distingue sempre. Problemi solo a **1x**: la riga evidenziata "Apri Recenti" diventa `ri Recenti`; il nome di file lungo viene spezzato in due pezzi sbagliati. Da 1.5x in su tutti e 6 i casi sono risolvibili (4 per contenimento diretto, 1 con la regola della scorciatoia, 1 con "etichetta più vicina").
- Finestra principale (`affinity-samples-log.txt`, 10 punti): il motore **fonde in una sola riga elementi affiancati**: le schede ("Trasforma Navigatore Cronologia"), etichetta + casella combinata ("Utilizza spazio prima Solo tra paragrafi"), la barra dei menu ("File Modifica Documento" a 1.5x e 2x). Serve quindi scendere al livello delle **parole**.
- Spazi fra parole, in altezze di riga (H), a 1.5x: spazio normale **0,29-0,38 H**; fra schede dei pannelli **0,81-1,00 H**; fra etichetta e casella combinata **1,05 H**; fra voci della barra dei menu **2,28-2,33 H**; fra freccetta di espansione e titolo ("v Spaziatura") 0,68 H; fra icona letta come lettera e nome ("V Vettore", "O Pixel") 0,58-0,62 H. **La soglia giusta è circa 0,6 H** (la soglia di 1 H ipotizzata in `ocr-windows.md` non separerebbe le schede).
- Glifi letti come testo: freccette dei pannelli (`>`, `v`), icone (`V`, `O`, `a`, `02`). A 0,6 H finiscono quasi sempre in un segmento a sé di un solo carattere, che si può scartare.
- A **3x** la voce evidenziata in arancione "Layout" non viene più rilevata (a 1x, 1.5x, 2x sì).
- Tempi OCR sulla regione 900x300: 1x 16-39 ms, 1.5x 24-67 ms, 2x 36-93 ms, 3x 71-171 ms.

### 9.2 Prova end-to-end della logica proposta **[MISURA-OFFLINE]** (`affinity-selection-eval.txt`)

25 casi su immagini reali (15 nella finestra principale, 6 nel menu File con sottomenu, 4 nel menu Visualizza). Passo 1: regione 900x300, nessun trattamento colore, segmenti a 0,6 H, scarto dei segmenti di un carattere, regola delle scorciatoie, "etichetta più vicina sulla riga" entro 10 H. Passo 2, solo se il passo 1 non trova nulla: ritaglio 400x120 a 2x con grigi + auto-levels.

| Scala del passo 1 | Esatti | di cui dal passo 2 | Tempo passo 1 (prep + OCR) medio / max | Passo 2 medio |
| --- | --- | --- | --- | --- |
| 1x | 16/25 | 0 | 22 / 65 ms | 24 ms |
| **1.5x** | **20/25** | 1 | **38 / 55 ms** | 14 ms |
| 2x | 19/25 | 1 | 55 / 71 ms | 14 ms |
| 3x | 18/25 | 1 | 108 / 131 ms | 16 ms |

Errori a 1.5x: `s: 100` (solo maiuscola), `100%` (solo spazio), `profiIe` dentro un nome di file (I maiuscola al posto di l), e i due casi "puntatore nel vuoto della riga di menu" (a 125 px e 205 px dalla fine dell'etichetta, cioè 10-17 H: fuori dal limite di 10 H). Errori in più a 2x: `FFOOOO`, `10096`, `[Nessuno stile)`. A 3x: "Layout" perso e sostituito dal vicino "Canva Al" (errore vero).

Variante (`affinity-selection-eval-20H.txt`): limite "etichetta più vicina" a 20 H e confronto "equivalente per la voce" (senza maiuscole né spazi): **24/25 a 1.5x**, 22/25 a 2x.

Rischio misurato della regola "etichetta più vicina": con il puntatore sull'icona dello strumento Sposta (24,172) la riga contiene "Pagine" a 55 px e l'OCR da solo lo leggerebbe: **falso positivo**. In Affinity non succede se UIA viene prima (quel punto dà `Button` + `Text 'Strumento Sposta'`). Controlli negativi corretti: area di lavoro vuota e altre due icone -> nessun testo.

---

## 10. Tooltip **[MISURA]** (`affinity-tooltips-log.txt`, 7 bersagli, attesa fissa di 2,2 s)

| Domanda | Risposta |
| --- | --- |
| Compaiono? | Sì su **4 bersagli su 7**: due icone della barra superiore ("Modalità anteprima", "Trasforma"), un'icona piccola del pannello Pagine ("Duplica mastro selezionata"), il contagocce del pannello Colore ("Selettore colore"). **No** sui 3 strumenti della palette, nemmeno dopo 5 s su "Strumento Penna": senza documento aperto gli strumenti erano disabilitati (`enabled=0`). Con gli strumenti abilitati **non è stato provato**. |
| Dopo quanto tempo? | **[NON MISURATO]**: lo script controlla una sola volta, 2,2 s dopo l'arresto del mouse; a quel punto c'erano già. Il ritardo predefinito di WPF (`ToolTipService.InitialShowDelay`) segue il tempo di hover del sistema, di norma 400 ms **[LETTO]**. Non misurata neanche la durata di permanenza. |
| Finestra separata? | Sì: nuova finestra top-level del processo, classe `HwndWrapper[Affinity.exe;;<guid>]` (la stessa famiglia di menu e finestra principale), style `0x96000000`, ex `0x080800A8` = `LAYERED | TOPMOST | NOACTIVATE | TOOLWINDOW | TRANSPARENT`. Si distingue dal popup di menu solo per `WS_EX_TRANSPARENT` (0x20). |
| Posizione | Angolo in alto a sinistra a **(x del puntatore, y del puntatore + 17 px)** in tutti e 4 i casi; altezza 32 px, larghezza 82-195 px. |
| Esposto a UIA? | Sì: `Window Class='Popup'` -> **`ToolTip Name='Modalità anteprima'`** -> `Text` con lo stesso nome. Trovato anche con `RootElement.FindAll(Descendants, ControlType=ToolTip AND ProcessId)` (1 risultato su 4 casi su 4; il costo di questa ricerca dalla radice del desktop non è stato misurato). |
| `FromPoint` sopra il tooltip | Restituisce ciò che sta **sotto** (`Window 'Affinity'` o il `TabControl`): il tooltip è trasparente al hit-test. `FromPoint` sull'icona, con tooltip visibile, dà lo stesso elemento di prima. |
| Leggibile dall'OCR? | Ritaglio del solo tooltip a 1x (es. 147x32 px): **risultato vuoto** (immagine troppo piccola per il motore). Stesso ritaglio a 2x: corretto 4/4 in 4-15 ms. Nella regione 900x300 a 1.5x intorno al puntatore: corretto 4/4 (32-53 ms), accenti compresi. |
| Trovarlo via OCR senza UIA **[MISURA-OFFLINE]** | Sulla riga del puntatore non c'è testo (si punta un'icona); la regola "segmento con Y fra y e y+60 e inizio X fra x-20 e x+40" trova il tooltip 4/4 a 1.5x e 2x; sui controlli negativi dà 3 volte nulla e una volta il glifo `>` (scartabile perché di un solo carattere). |

**Non provato**: se il tooltip sopravvive al clic centrale intercettato dalla nostra app.

---

## 11. Misure mancanti (da non dare per scontate)

1. Tempo di comparsa e durata dei tooltip; tooltip degli strumenti della palette con un documento aperto.
2. Effetto del clic centrale (consumato dall'hook) su menu aperti e tooltip.
3. `FromPoint` con il puntatore sopra la colonna delle scorciatoie e sopra le voci di un sottomenu; se il sottomenu è una finestra separata.
4. `IsEnabled` e `AcceleratorKey` delle voci di menu.
5. Tutto ciò che richiede un documento aperto: testo sulla tela, pannello Livelli popolato, barra contestuale, barra di stato, righello, finestre di dialogo, caselle combinate aperte, menu contestuali (clic destro).
6. Tema chiaro di Affinity; altre scale dello schermo (100%, 150%, 200%): **tutte le scale OCR consigliate qui valgono per 125%**.
7. Costo della ricerca UIA dei `ToolTip` dalla radice del desktop; costo di `CopyFromScreen` (non cronometrato nella sonda).
8. Output a console di `01-launch.ps1` e `04-closewelcome.ps1` (elenco finestre all'avvio, `FromPoint` sulla schermata di benvenuto): non salvato su file, perso.
9. Motore ONNX PaddleOCR sugli stessi ritagli: non provato in questa sonda.

---

## Conclusioni per il progetto

### A. Correzione di un presupposto

Per **Affinity 3.3** il presupposto "interfaccia invisibile agli screen reader" è vero solo in parte. **Menu, barra dei menu, schede, studi, nomi degli strumenti e tooltip si leggono da UIA**, in 3-6 ms, con testo esatto (accenti, puntini, nessun errore `CtrI`). L'OCR resta necessario per: contenuto dei pannelli, area di lavoro, e tutto ciò che UIA restituisce come contenitore senza nome. La catena giusta è **UIA prima, OCR quando UIA restituisce un contenitore o un nome inutilizzabile**; mai solo OCR.

### B. Logica di selezione consigliata

1. `ElementFromPoint` (COM UIA3, solo lettura, con timeout). Mai usare pattern di azione.
2. **Normalizzazione dell'elemento**:
   - `Text` con genitore `MenuItem`, `TabItem`, `ListItem` o `Button` -> risalire al genitore.
   - Nome "buono" del genitore -> pronunciarlo. Per i `MenuItem` è già senza trattino basso e senza scorciatoia.
   - Nome "cattivo" = vuoto, oppure nome di tipo .NET (`^[A-Za-z_]\w*(\.[A-Za-z_]\w*)+$`, es. `Serif.Affinity.Workspaces.Workspace`), oppure `StudioPage, Title = X` (estrarre `X`), oppure identificatore minuscolo senza spazi con `HelpText` presente (`home` -> "Home").
   - Con nome cattivo: nell'ordine `HelpText` -> `Text` discendenti **anche fuori schermo e con rettangolo vuoto** (se hanno tutti lo stesso nome; togliere il trattino basso del tasto di accesso) -> tooltip (punto 3) -> OCR (punto 4).
   - `MenuItem` con nome di tipo .NET e altezza di pochi pixel = separatore -> silenzio.
   - Contenitori (`Tab`, `Window`, `Pane`, `Custom`, `StatusBar`, `Menu`, `List`, `ToolBar` senza `Text` univoco) o elemento molto più grande di una riga di testo -> OCR.
   - Proteggersi da rettangoli vuoti, `0,0,0,0`, infiniti.
3. **Tooltip**: se l'elemento è un'icona senza nome, cercare fra le finestre top-level visibili dello stesso processo quella con `WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE|WS_EX_TRANSPARENT` vicina al puntatore (in Affinity: angolo a (x, y+17), alta 32 px) e leggere il `Name` del suo figlio `ToolTip`. Evitare la ricerca `Descendants` dalla radice del desktop. Eventuale breve attesa (da tarare dopo aver misurato il ritardo reale) se il puntatore è fermo su un'icona muta.
4. **OCR intorno al puntatore**:
   - cattura `CopyFromScreen` di circa 900x300 px centrata sul puntatore (contiene anche popup di menu e tooltip);
   - se UIA ha restituito un elemento "a riga" (alto meno di circa 3 righe di testo) ma senza nome, usare il suo rettangolo per limitare i candidati;
   - segmentare ogni `OcrLine` in **segmenti** dove lo spazio fra parole supera **0,6 x altezza della riga**;
   - candidati = segmenti il cui riquadro, allargato di 0,35 altezze sopra e sotto, contiene la Y del puntatore; scartare i segmenti di un solo carattere (freccette, icone);
   - scegliere il segmento che contiene la X (tolleranza 0,5 altezze);
   - se è una scorciatoia (modello del capitolo 8) -> etichetta più vicina a sinistra; le scorciatoie non si pronunciano mai;
   - se nessun segmento contiene la X -> etichetta più vicina sulla riga entro 10-20 altezze, **solo se UIA non ha già identificato un'icona** (altrimenti falso positivo tipo "Pagine" sull'icona dello strumento);
   - se la riga è vuota -> passo 2 (sotto); se ancora vuota -> regola del tooltip (segmento subito sotto il puntatore: Y in (y, y+60], inizio X in [x-20, x+40]); altrimenti dire che non c'è testo;
   - togliere dal testo finale un eventuale primo carattere isolato `>`/`v` e i puntini finali.

### C. Pipeline di preprocessing OCR consigliata per interfacce scure

| Passo | Cosa | Motivo (dati) |
| --- | --- | --- |
| 1 | Regione 900x300, **scala 1.5x** a 125% di scala schermo (2x è equivalente ma più lento), `HighQualityBicubic`, **nessuna inversione, nessun auto-levels, nessuna gamma** | 34/36 sul menu File, 20/25 nella prova end-to-end, 38 ms medi; 1x perde la riga evidenziata e sbaglia parole; 3x-4x peggiorano (Layout perso, `VV/`) e costano 2-4 volte; levels/gamma a 1.5x fanno scendere le etichette a 23-25/26 |
| 2 (solo se il passo 1 non trova nulla sulla riga) | Ritaglio **400x120** intorno al puntatore, **2x**, grigi + **auto-levels ai percentili 1% - 99,5%**, senza gamma, senza inversione | recupera "S: 100", "Esporta" e simili (0/6 -> 6/6); 10-50 ms in tutto; a 2x "solo levels" è anche la variante migliore sul menu (35/36), quindi non fa danni |
| Mai | Inversione dei colori | risultati identici fino al pixel in tutte le prove |
| Mai | Gamma < 1 sull'immagine intera | gonfia il testo chiaro: `Ctr1`, `Ctd`, "Esci" sparito |
| Attenzione | Ritagli più piccoli di circa 150x64 px | il tooltip 147x32 a 1x dà risultato vuoto: ingrandire a 2x e/o aggiungere margine |
| Obiettivo di scala | altezza delle maiuscole di circa 16-22 px nell'immagine data al motore (qui 11 px a schermo -> 1.5x-2x) | da ricalibrare a 100%, 150%, 200%: non misurato |

Budget di tempo misurato per il percorso OCR completo: passo 1 circa 40 ms (massimo 55-105 ms), passo 2 circa 15-20 ms; il percorso UIA costa 3-6 ms. Entrambi restano ben sotto la soglia percepibile prima dell'avvio della voce.

### D. Cose da fare prima di fidarsi

Ripetere la sonda con **un documento aperto** (strumenti abilitati, Livelli popolato, barra contestuale, testo sulla tela, una finestra di dialogo), misurare il **ritardo dei tooltip** con un campionamento ogni 50 ms e verificare che **il clic centrale intercettato non chiuda menu e tooltip**.

---

## File di evidenza

In `C:\Users\Angelo\Desktop\Matteo\docs\research\probe\`:

- Alberi UIA: `affinity-uia-tree-main.txt`, `affinity-uia-tree-welcome.txt`, `affinity-uia3-com.txt`
- Menu: `affinity-menu-file-log.txt` (prima prova, finestra ridotta a icona: geometria non valida), `affinity-menu-file2-log.txt`, `affinity-menu-visualizza-log.txt` e relative immagini `-full`, `-hover`, `-popup`
- OCR: `affinity-ocr-variants.txt`, `affinity-ocr-geometry.txt` (+ `affinity-ocr-geometry-crop1..6.png`), `affinity-ocr-lowcontrast.txt` (+ `affinity-lowcontrast-*.png`), `affinity-ocr-input-3x-*.png`
- Campioni e tooltip: `affinity-samples-log.txt`, `affinity-samples-full.png`, `affinity-tooltips-log.txt`, `affinity-tooltip-1..7-full.png`, `affinity-tooltip-4..7-popup.png`, `affinity-tooltip-longhover.png`
- Rielaborazioni offline di oggi: `affinity-offline-extra.txt`, `affinity-selection-eval.txt`, `affinity-selection-eval-20H.txt`

Script: sonda originale in `C:\Users\Angelo\AppData\Local\Temp\claude\C--Users-Angelo-Desktop-Matteo\d74c920e-45bd-48a5-abcd-2768715aa453\scratchpad\probe\`; rielaborazioni offline in `...\scratchpad\spikes\affinity-report\` (`13-offline-extra.ps1`, `14-selection-eval.ps1`, `15-selection-eval-20H.ps1`).
