# StradarioViewer

App compagna di [StradarioApp](../../README.md) da tenere in tasca: carica
un file `.stradario` e mostra i percorsi/POI datati del giorno corrente
(con navigazione al giorno prima/dopo) su una mappa, con etichetta e
descrizione di ogni elemento — pensata per consultare l'itinerario di
viaggio dal telefono, non per crearlo o modificarlo.

**👉 Usala qui: https://paolobia.github.io/StradarioApp/**

Blazor WebAssembly (.NET 8), 100% client-side: nessun backend, nessun
database. I dati restano nel `localStorage` del browser — il file va
caricato una sola volta, resta disponibile alle visite successive. Funziona
offline dopo il primo caricamento e si può installare come PWA ("Aggiungi a
schermata Home"/icona di installazione nella barra indirizzi).

## Come si usa

1. Apri il link sopra (dal telefono o dal computer).
2. Carica il file `.stradario` del progetto che vuoi consultare (lo stesso
   file salvato da StradarioApp).
3. Naviga i giorni con le frecce ◀/▶: la mappa e la lista a fianco mostrano
   solo i percorsi/POI di quel giorno.
4. Clicca un percorso/POI (in lista o sulla mappa) per leggerne etichetta e
   descrizione.
5. *(Facoltativo)* Installa l'app: dal browser, "Installa app" / "Aggiungi a
   schermata Home" — da quel momento si apre come un'app a sé, anche senza
   connessione (i tile della mappa restano da rete).

Per caricare un progetto diverso in seguito: pulsante "Carica un altro
file" in alto a destra.

## Sviluppo

```bash
dotnet run          # dev server locale
dotnet build         # solo compilazione
dotnet publish -c Release -o ./publish   # output statico
```

Il deploy sulla URL sopra è **automatico**: ogni push su `main` che tocca
questa cartella pubblica una nuova versione su GitHub Pages via GitHub
Actions (`.github/workflows/deploy-viewer.yml`), nessun passo manuale.

Per i dettagli tecnici (formato dati, architettura, limiti noti) vedi
[`../CLAUDE.md`](../CLAUDE.md).
