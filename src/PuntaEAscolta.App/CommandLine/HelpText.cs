namespace PuntaEAscolta.App.CommandLine;

internal static class HelpText
{
    public const string Text = """
Punta e Ascolta: modalità di prova a riga di comando (senza interfaccia).
Senza argomenti l'app parte normalmente, con l'icona vicino all'orologio.

Uso:  PuntaEAscolta.exe <comando> [opzioni]

Comandi
  --read-at X,Y [--zone] [--speak]
        Risolve che cosa verrebbe letto nel punto X,Y dello schermo (pixel fisici) e stampa l'esito.
        Senza coordinate (o con "puntatore") usa la posizione attuale del puntatore.
        --zone    legge tutta la zona attorno al punto (come la pressione prolungata)
        --speak   pronuncia anche il testo trovato
  --read-selection [--speak]
        Legge il testo selezionato nell'app in primo piano (accessibilità, poi appunti).
        Con --attendi 3000 si hanno 3 secondi per portare in primo piano la finestra giusta.
  --ocr-file <immagine.png> --point X,Y [--engine windows|onnx|entrambi] [--scale 1.25]
        OCR di un'immagine salvata: stampa tutte le righe riconosciute e la scelta attorno al punto
        (coordinate in pixel dell'immagine). --scale è la scala del monitor da cui viene l'immagine
        (predefinita: quella del monitor attuale).
  --speak "testo" [--provider windows|elevenlabs|auto]
        Pronuncia il testo con le impostazioni correnti; --provider vale solo per questa prova.
  --voices
        Elenca le voci di Windows installate e, se la chiave è configurata, quelle dell'account ElevenLabs.
  --selftest
        Autodiagnosi silenziosa: lingue OCR, voci, microfoni, installazione dell'hook del mouse per 1 s,
        accessibilità sotto il puntatore, chiave ElevenLabs. Esito JSON; codice 1 se qualcosa di essenziale non va.
  --set-key [--verifica]
        Legge la chiave API di ElevenLabs dallo standard input, la cifra (DPAPI, solo questo utente e questo PC)
        e la salva in settings.json. Con --verifica la controlla prima presso ElevenLabs.
        Esempio (PowerShell):  Get-Content chiave.txt | .\PuntaEAscolta.exe --set-key --verifica
        Chiudere l'app in esecuzione prima di usarlo, poi riavviarla.
  --help
        Questo aiuto.

Opzioni comuni
  --attendi <ms>   attende prima di leggere (per spostare il puntatore o cambiare finestra)
  --verbose        copia il registro su stderr (compreso il livello di dettaglio)

Uscita: JSON su stdout. Codice di uscita 0 = comando eseguito, 1 = errore o argomenti non validi.
Nota: l'eseguibile è un'app a finestre. Da cmd usare "start /wait PuntaEAscolta.exe ...", da PowerShell
aggiungere "| Out-String" per attendere la fine e vedere l'uscita in ordine.
""";
}
