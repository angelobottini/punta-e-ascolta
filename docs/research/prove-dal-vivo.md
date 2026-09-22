# Prove dal vivo dell'app reale

Data: 22/09/2026, 20:12-20:35. Macchina: Snapdragon X (ARM64), Windows 11 25H2, 1920x1200 al 125%, voci Elsa e Cosimo. Compilazione Debug in una cartella privata (`scratchpad\build\live`), 0 avvisi. `settings.json` solo nella cartella di output: `Speech.Volume` 0,2, `Provider` Windows, `General.DebugLog` true, `HotkeyReadSelection` `Win+Shift+F9`; per lo scenario 7 anche `Input.AcceptInjectedEvents` true.

## Metodo

- Sonda principale: `PuntaEAscolta.exe --read-at X,Y` (pixel fisici), lanciata da un processo PowerShell reso Per-Monitor V2 (`SetProcessDpiAwarenessContext(-4)`). Coordinate da rettangoli UIA, `Window.GetPoint` di Word, `PointsToScreenPixelsX/Y` di Excel e catture dello schermo.
- La colonna **ms** è `elapsedMs` dell'app, cioè il tempo di risoluzione. L'avvio del processo a riga di comando aggiunge circa 3 s, che nella modalità icona non ci sono.
- Solo finestre mie: un Blocco note aperto su un mio file, una finestra di Esplora file sulla cartella di lavoro, istanze Word ed Excel create via COM con documenti nuovi chiusi senza salvare, immagini mostrate in una finestra WinForms del mio processo. Affinity è stato avviato da me (prima non era in esecuzione): clic solo sulla barra dei menu, `Esc`, chiusura senza salvare. Il Word dell'utente è rimasto aperto e intatto.
- Nessun testo personale dell'utente è riportato qui.

## Risultati

| # | Scenario | Punto | Atteso | Ottenuto | Fonte | ms | Esito |
|---|---|---|---|---|---|---|---|
| 1 | Blocco note, parola "gatto" | 780,204 | frase lunga che contiene "gatto" | frase giusta (161 caratteri) | UiaSentence | 154 | OK |
| 1 | Blocco note, "prima" / "Terza" / "Secondo" / "mentre" (2a riga della stessa frase) | vari | la frase di ciascuna | tutte giuste | UiaSentence | 87-92 | OK |
| 1 | Barra dei menu File / Modifica / Visualizza | 118,153 ... | nome | "File", "Modifica", "Visualizza" | UiaName | 88-92 | OK |
| 1 | Pulsante Grassetto del Blocco note | 684,152 | "Grassetto" | **"Grassetto (CTRL+G)"** | UiaName | 102 | **BUG 1** |
| 1 | Area vuota del documento | 700,500 | silenzio / "Nessun testo" | nessun testo (OCR Windows + ONNX) | None | 1448 (ONNX a freddo, solo riga di comando) | OK |
| 1 | Barra di stato | 180,752 | posizione | "Riga 1, 1 colonne" (nome UIA del Blocco note) | UiaName | 100 | OK |
| 1 | Menu File aperto (clic): "Nuova scheda", "Salva con nome", "Stampa", "Recenti" | 168,197 ... | nome della voce | tutti giusti, senza scorciatoia | UiaName | 91-124 | OK |
| 1 | Menu File, puntatore sulla colonna "Ctrl+N" | 350,198 | "Nuova scheda" | "Nuova scheda" | UiaName | 111 | OK |
| 2 | Esplora file: "Nuovo", "Ordina" | 204,262 / 702,262 | nome | giusti | UiaName | 104-155 | OK |
| 2 | Esplora file: icone "Taglia" e "Indietro" (solo icona) | 297,262 / 163,201 | nome | "Taglia", "Indietro" | UiaName | 107-111 | OK |
| 2 | Riquadro di spostamento: "Download", "Desktop" | 231,478 / 224,438 | "Download", "Desktop" | "Download (elemento aggiunto)", "Avvio dell'accesso rapido - Desktop (elemento aggiunto)" | UiaName | 117-139 | parziale (BUG 9) |
| 2 | Barra degli indirizzi "scratchpad" | 931,201 | "scratchpad" | "scratchpad" | UiaName | 121 | OK |
| 2 | Nome del file "lib.ps1" (scritta sotto l'icona) | 542,431 | "lib.ps1" | **"Nome"** | UiaName | 116 | **BUG 2** |
| 2 | Icona del file "lib.ps1" | 542,380 | "lib.ps1" | "lib.ps1" | UiaName | 94 | OK |
| 3 | Word: "Rossi" e "pagato" (frase con "sig.", "dott.", "10.30", "1.250,50") | 387,490 / 1028,490 | "Il sig. Rossi è arrivato alle ore 10.30 con il dott. Bianchi e ha pagato 1.250,50 euro." | identica | UiaSentence | 149-152 | OK |
| 3 | Word: "spostata" | 490,526 | "La riunione del 3 ott. 2026 è stata spostata al pomeriggio, cioè alle 15.45 circa." | **questa frase + "Vedi pag. 12 e cfr. l'art. 5 del regolamento!"** | UiaSentence | 141 | **BUG 5** |
| 3 | Word: "regolamento" | 1378,526 | "Vedi pag. 12 e cfr. l'art. 5 del regolamento!" | le due frasi unite, come sopra | UiaSentence | 146 | **BUG 5** |
| 3 | Word: "cena", "Verdi" | 481,572 / 640,572 | "Chi viene alla cena?", "Il prof. Verdi ha detto che arriverà alle 9 in punto." | identiche | UiaSentence | 78-85 | OK |
| 3 | Barra multifunzione: Grassetto, Copia formato, Aumenta rientro (solo icona) | 253,190 ... | nome | "Grassetto", "Copia formato", "Aumenta rientro" | UiaName | 99-173 | OK |
| 3 | Pagina vuota sotto il testo (2 punti) e area grigia fuori pagina | 807,809 / 507,939 / 140,600 | silenzio o "Nessun testo", mai il documento | **"Contenuto pagina 1"** (mai il documento intero) | UiaName | 88-185 | **BUG 4** |
| 3 | Barra di scorrimento orizzontale (punto finito lì per errore) | 754,989 | silenzio | "tAccessibiIità: conforme" (riga OCR della barra di stato sotto) | OcrLine | 310 | discutibile (nota 11) |
| 3 | `--read-selection` con Word in primo piano e frase in parte selezionata | - | "serve per la prova della selezione" | identico | Selection | 55 | OK |
| 3 | `--read-at` dentro la selezione | 652,689 | la selezione | la selezione | Selection | 124 | OK |
| 3 | `--read-at` fuori dalla selezione, stessa frase | 364,689 | la frase intera | la frase intera | UiaSentence | 148 | OK |
| 4 | Excel: cella di testo A2 | 233,431 | "Mele rosse del Trentino" | identico | UiaSentence | 197 | OK |
| 4 | Excel: cella numerica B2 (formato `#.##0,00`) | 313,431 | "1.250,50" | "1.250,50" | UiaSentence | 92 | OK |
| 4 | Excel: formula B3 | 313,455 | "2501" | "2501" | UiaSentence | 67 | OK |
| 4 | Excel: cella vuota D5 | 473,503 | "Cella vuota" | "Cella vuota" | UiaValue | 87 | OK |
| 4 | Excel: intestazione di colonna C, di riga 2 | 393,385 / 178,431 | "C", "2" | "C", "2" | UiaName | 64-70 | OK |
| 4 | Excel: cella A1 con testo "Nome" (cella attiva) | 233,407 | "Nome" | **"Cella vuota"** | UiaValue | 90 | **BUG 3** |
| 5 | Affinity, schermata di benvenuto: schede "Home", "Guida", titolo | 688,155 ... | nome | giusti | UiaName | 98-147 | OK |
| 5 | Affinity, benvenuto: icone "+" e cartella | 1870,79 / 1794,79 | "Nuova", "Apri" (HelpText) | **"newdocnew", "newdocopen"** | UiaName | 102-105 | **BUG 7** |
| 5 | Barra dei menu: File, Documento, Guida | 80,19 ... | nome | giusti | UiaName | 99-123 | OK |
| 5 | Menu File aperto: "Nuovo da Appunti", "Apri Recenti", "Salva con nome" (disabilitata), "Esporta" | 140,222 ... | come in `probe-affinity.md` (nome pulito, senza puntini né scorciatoia) | tutti giusti | UiaName | 107-119 | OK |
| 5 | Menu File, puntatore sulla scorciatoia `Ctrl+Alt+Maiusc+N` | 447,224 | "Nuovo da Appunti" | "Nuovo da Appunti" | UiaName | 137 | OK |
| 5 | Menu File, separatore | 200,347 | silenzio | **"Serif.Affinity.Workspaces.WorkspaceMenuSeparator"** | UiaDescription | 97 | **BUG 6** |
| 5 | Palette: strumento Sposta, strumento Nodo | 23,172 / 23,217 | "Strumento Sposta", "Strumento Nodo" | identici (dai figli `Text` fuori schermo) | UiaName | 110-113 | OK |
| 5 | Studio "Layout", scheda "Colore", pulsante "Esporta" (disabilitato) | 305,74 ... | nome | giusti | UiaName | 99-103 | OK |
| 5 | Etichetta nel pannello "Pagine mastro" | 123,197 | "Pagine mastro" | "Pagine mastro" | OcrLine | 287 | OK |
| 5 | Icona della barra superiore (a destra, prima icona) | 1363,74 | nome dell'icona | nessun testo (ToolBar senza nome, OCR vuoto) | None | 824 | da verificare (nota 10) |
| 6 | Cartello giallo "ATTENZIONE: È VIETATO L'ACCESSO" (3 righe) | 750,480 | il testo intero | "ATTENZIONE: È VIETATO L'ACCESSO" | OcrBlock | 395 | OK |
| 6 | Maglietta rossa, scritta bianca ruotata di 8° "PENSA POSITIVO" | 750,540 | il testo intero | "PENSA POSITIVO" | OcrBlock | 331 | OK |
| 6 | Cartello "fotografico" con rumore "USCITA DI SICUREZZA" | 750,470 | il testo intero | "USCITA Dl SICUREZZA" (errore del motore: "Dl") | OcrBlock | 396 | OK con errore OCR |
| 6 | Le tre immagini con `--zone` | stessi punti | tutto il testo della zona, in ordine | testo della zona, ma **righe di colonne diverse mescolate** | OcrZone | 260-296 | **BUG 8** |
| 7 | Modalità icona: avvio senza argomenti | - | "Pronto" nel registro | "Pronto" 0,56 s dopo il primo messaggio; ONNX riscaldato dopo 3,6 s | - | - | OK |
| 7 | Clic centrale iniettato (SendInput) sul Blocco note, parola "gatto" | 780,204 | lettura della frase | frase giusta; attivazione 31 ms dopo il clic, testo in 101 ms, primo audio 33 ms, riproduzione avviata **circa 290 ms** dopo il clic | UiaSentence | 101 | OK |
| 7 | Secondo clic centrale iniettato durante la voce (1,6 s dopo) | 780,204 | la voce si ferma | "Attivazione (clic) mentre la voce parla: stop", `PlaybackStopped` **3 ms** dopo, "Lettura 1 annullata" | - | 3 | OK |
| 7 | Il clic centrale non arriva al Blocco note | - | nessun autoscorrimento | cursore rimasto la I (stesso handle prima e dopo i due clic), nessun cambiamento visibile | - | - | OK |
| 7 | `--exit` | - | app chiusa | chiusa in 105 ms ("Servizi chiusi", "Punta e Ascolta chiuso") | - | 105 | OK |
| 8 | Word, frase con 😀, 👍, ❤️ e ":-)" | 810,631 | testo senza emoji né ":-)" | "Oggi è una bella giornata e siamo tutti felici davvero!"; con `--speak`: `Completed`, voce di Windows, riproduzione avviata a 184 ms, 3,7 s in tutto | UiaSentence | 90 | OK |

Riepilogo: 50 righe di verifica. 38 riuscite, 9 con un bug (8 bug distinti), 1 parziale, 1 discutibile, 1 da verificare. Nessun crash, nessun blocco. Il percorso clic → voce → secondo clic → stop funziona dal vivo.

## Bug trovati (con riproduzione)

1. **Scorciatoia tra parentesi non tolta** - `src/PuntaEAscolta.Logic/Text/LabelCleaner.cs` (`TrailingShortcut`). Blocco note di Windows 11, pulsante Grassetto: il nome UIA è `Grassetto (CTRL+G)` e l'app lo dice così. La regex accetta solo scorciatoie separate da tabulazione, da due spazi o da uno spazio seguito dal modificatore, non `" (CTRL+G)"`. Stessa forma negli altri pulsanti del Blocco note (`Corsivo (CTRL+I)`, `Barrato (Ctrl+MAIUSC+X)`, `Rimuovi formattazione (CTRL+barra spaziatrice)`) e nei suggerimenti di Office.
2. **Nome di file in Esplora file letto come "Nome"** - `src/PuntaEAscolta.Windows.Automation/UiaTextSource.cs` (il valore non viene letto) oppure `src/PuntaEAscolta.Logic/Resolution/UiTextResolver.cs` (ramo 3, Edit). Visualizzazione icone grandi, puntatore sulla scritta sotto l'icona: `FromPoint` restituisce `Edit Name='Nome'`, `ValuePattern.Value='lib.ps1'`, genitore `ListItem 'lib.ps1'`. L'app dice solo "Nome" (fonte UiaName), quindi il valore non arriva al resolver. Con il valore l'app direbbe "Nome, lib.ps1". Meglio ancora: un Edit dentro un ListItem dovrebbe salire al ListItem.
3. **Cella di Excel con testo letta "Cella vuota"** - `UiaTextSource.cs` (lettura del valore delle celle) e `UiTextResolver.cs` (ramo 2). Cartella nuova, A1 = "Nome" (cella attiva), `--read-at` al centro di A1 (233,407; cattura controllata): "Cella vuota", fonte UiaValue. Le celle A2 e B2 sono state lette solo attraverso il contesto di testo (fonte UiaSentence, non UiaValue): il valore UIA delle celle sembra arrivare vuoto e il ripiego "Cella vuota" scatta quando manca anche il contesto di testo, come nella cella attiva.
4. **Pagina vuota di Word: "Contenuto pagina 1"** - `UiTextResolver.cs`, ramo 3 (Edit con contesto di testo e puntatore non sul testo). Documento nuovo con tre paragrafi: 120 e 250 px sotto l'ultimo paragrafo e nell'area grigia a sinistra della pagina l'app dice "Contenuto pagina 1" (il nome UIA della pagina). Il documento intero non viene mai letto, ma ci si aspetta silenzio o "Nessun testo".
5. **"circa" e altre parole intere nell'elenco delle abbreviazioni** - `src/PuntaEAscolta.Logic/Text/SentenceSplitter.cs`, righe 14-25. In "...alle 15.45 circa. Vedi pag. 12..." la frase non si chiude dopo "circa.", perché "circa" è nell'elenco (l'abbreviazione vera è "ca."). Le due frasi vengono lette insieme. Lo stesso rischio c'è con altre parole italiane comuni a fine frase che sono nell'elenco: "via", "no", "min", "sec", "set", "mar", "dom", "all", "col", "gen", "ma"... Proposta: per queste parole spezzare se dopo viene una maiuscola.
6. **Separatore di menu di Affinity letto ad alta voce** - `UiTextResolver.cs`, riga 110: quando il Name è scartato perché è un nome di tipo, `FirstUsable(HelpText, FullDescription, LegacyDescription, LegacyName)` riprende lo stesso nome di tipo, probabilmente da `LegacyName`, senza il filtro dei nomi .NET. Menu File aperto, puntatore sul separatore (200,347; elemento 478x6): "Serif.Affinity.Workspaces.WorkspaceMenuSeparator". In `probe-affinity.md` 3.2 la regola è "MenuItem con nome di tipo .NET e alto pochi pixel = separatore, silenzio".
7. **Identificatori minuscoli non riconosciuti** - `UiTextResolver.cs`, regex `Identifier` (riga 31) / `UsableName`. Schermata di benvenuto di Affinity, icone "+" e cartella: l'app dice "newdocnew" e "newdocopen" (la seconda con lingua "en"). `HelpText` contiene "Nuova" e "Apri" (`probe-affinity.md` 3.2: "identificatore minuscolo senza spazi con HelpText presente, preferire HelpText"). La regex riconosce camelCase e snake_case, non le parole tutte minuscole attaccate.
8. **Lettura della zona con righe di colonne diverse mescolate** - `src/PuntaEAscolta.Logic/Reading/TextResolver.cs` / `src/PuntaEAscolta.Logic/Resolution/PointerTextSelector.cs` (composizione di OcrZone). Immagine 700x460 in una finestra al centro dello schermo e intorno la pagina di un browser: `--read-at X,Y --zone` restituisce le righe ordinate solo per Y, per esempio "ATTENZIONE:. [riga del browser]. [riga del browser]. È VIETATO. [riga del browser]... L'ACCESSO". Per chi ascolta il testo diventa incomprensibile. Servirebbe raggruppare prima per blocchi/colonne (i blocchi esistono già per OcrBlock) e leggere prima il blocco sotto il puntatore.

## Osservazioni minori

9. Esplora file, riquadro di spostamento: i nomi UIA contengono "(elemento aggiunto)" e, per il primo elemento, "Avvio dell'accesso rapido - " (gruppo). Si potrebbero togliere in `LabelCleaner.cs`, con una regola specifica per `explorer`.
10. Affinity, prima icona a destra della barra superiore (1363,74): `ToolBar` senza nome, OCR vuoto, "Nessun testo" in 824 ms (Windows OCR 86 ms, poi ONNX mirato 563 ms). Secondo `probe-affinity.md` 3.2 le barre di icone hanno figli `Text` fuori schermo con il nome ("Modalità anteprima", "Trasforma"...): da controllare in `UiaTextSource.cs` se questa barra ha nomi diversi fra loro (caso ambiguo della sonda) o se la ricerca dei figli non scatta.
11. Word, puntatore sulla barra di scorrimento orizzontale: l'app legge con l'OCR la riga della barra di stato sotto ("tAccessibiIità: conforme", con errori del motore). Una barra di scorrimento è un controllo noto: meglio il silenzio che la riga vicina (`TextResolver.cs`).
12. Da riga di comando il primo ripiego ONNX costa circa 1,1 s perché il motore parte a freddo; nella modalità icona è già riscaldato (272 ms di riscaldamento, all'avvio). Non è un bug.

## Cose rimaste aperte dopo le prove

- **Processo Word nascosto (PID 22520)**: l'ho creato io con una prima `New-Object -ComObject Word.Application` (senza documenti, invisibile). Non l'ho potuto chiudere: il riferimento COM era già rilasciato, la terminazione del processo è stata negata e la ROT restituisce solo l'istanza dell'utente. Si chiude da Gestione attività (processo "Microsoft Word" senza finestra) oppure riavviando. Il Word dell'utente (PID 18076) non è stato toccato. Le altre due istanze create per le prove (22424, 11116) sono state chiuse senza salvare.
- **Blocco note**: non era aperto prima delle prove e all'avvio ha ripristinato le schede della sessione dell'utente. Ho chiuso solo la mia scheda (`prova-notepad.txt`, non modificata) e ho ridotto la finestra a icona senza chiuderla, per non rischiare richieste di salvataggio sulle schede dell'utente.
- Affinity (avviato da me), la mia finestra di Esplora file, Excel e l'app (`--exit`) sono stati chiusi. `settings.json` della cartella di output è tornato con `AcceptInjectedEvents` disattivato.
- Prove e immagini sono in `scratchpad\live\` (`images.ps1`, `excel.ps1`, `word.ps1`, `tray.ps1`, `img-*.png`, catture). Il registro dell'app (`logs\punta-e-ascolta-20260922.log` nella cartella di output) contiene anche testo letto con `DebugLog` attivo.
