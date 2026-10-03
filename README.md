# Punta e Ascolta

Punta e Ascolta legge a voce ciò che si trova sotto il puntatore del mouse, solo quando glielo chiedi.
Funziona in qualsiasi programma di Windows: menu, pulsanti, celle di Excel, frasi di Word, testo dentro le fotografie.

Questo è il manuale breve. La guida completa è in [docs/LEGGIMI.txt](docs/LEGGIMI.txt).

## Avvio

1. Scegli la cartella giusta per il tuo PC:
   - `PuntaEAscolta-win-arm64` per i PC con processore Snapdragon (ARM);
   - `PuntaEAscolta-win-x64` per i PC con processore Intel o AMD.
2. Fai doppio clic su `PuntaEAscolta.exe`. Compare un'icona vicino all'orologio.
3. Se Windows chiede conferma perché il programma non è firmato, scegli "Esegui comunque".

Per farla partire da sola con Windows: Impostazioni > Generale > "Avvia con Windows".

## Come si usa

1. Porta il puntatore sopra una voce di menu, un pulsante, una frase o un cartello in una foto.
2. Premi la **rotellina** del mouse (clic centrale). L'app legge il testo.
3. Per fermare la voce premi di nuovo la rotellina, oppure **Esc**.

| Per fare questo | Premi |
| --- | --- |
| Leggere ciò che è sotto il puntatore | Rotellina, oppure Ctrl+Maiusc+Spazio |
| Leggere il testo selezionato | Rotellina dentro la selezione, oppure Win+Maiusc+F9 |
| Fermare la voce | Rotellina, oppure Esc |
| Dettare (se attivata) | Win+Maiusc+D, parla, poi di nuovo Win+Maiusc+D |

Le scorciatoie e il pulsante del mouse si cambiano in Impostazioni > Attivazione.

Con il tasto destro sull'icona vicino all'orologio trovi: Pausa/Riprendi, Impostazioni, Apri cartella dei log, Esci.

## Portatile senza mouse

Sul touchpad non c'è la rotellina. Due possibilità:

- usa Ctrl+Maiusc+Spazio;
- oppure fai diventare il tocco con tre dita un clic centrale: Impostazioni di Windows > Bluetooth e dispositivi > Touchpad > Gesti con tre dita > "Pulsante centrale del mouse". Nell'app lascia attiva la casella "Accetta i clic generati da software" (scheda Attivazione).

## La voce

Senza configurazione l'app usa la voce di Windows. Per una voce più naturale serve una chiave API di ElevenLabs:

1. Apri Impostazioni dall'icona vicino all'orologio, scheda Voce.
2. Incolla la chiave, premi "Verifica" e poi "Salva chiave".
3. Scegli la voce dall'elenco, premi "Salva" e poi "Prova voce ElevenLabs".

La chiave deve avere almeno il permesso "Text to Speech". Se manca qualche permesso, "Verifica" dice quale.
La chiave è cifrata e vale solo su quel PC e per quell'utente di Windows: su un altro PC va reinserita.
Senza internet l'app torna da sola alla voce di Windows.

## Limiti noti

- Nelle finestre eseguite come amministratore il clic della rotellina non viene intercettato.
- Il testo molto piccolo o quasi invisibile può essere letto male.
- Mentre l'app è attiva, la rotellina non fa più lo scorrimento automatico negli altri programmi e Ctrl+Maiusc+Spazio non arriva agli altri programmi. Entrambi si possono cambiare in Impostazioni > Attivazione.

## Se qualcosa non va

Nella scheda Diagnostica delle Impostazioni ci sono gli ultimi messaggi del registro e l'esito dell'ultima lettura.
La cartella dei log si apre dal menu dell'icona vicino all'orologio.

## Per chi sviluppa

Serve .NET 10 SDK su Windows. Per creare le due cartelle portatili in `publish\`:

```powershell
powershell -ExecutionPolicy Bypass -File tools\publish.ps1
```

Lo script compila, esegue i test e pubblica per `win-x64` e `win-arm64`. Il progetto è descritto in [docs/DESIGN.md](docs/DESIGN.md).
