🇮🇹 **Italiano** | 🇬🇧 [English](StradarioApp_Description.en.md)

# StradarioApp — Descrizione del progetto

## Cos'è
Applicazione desktop C# (.NET 8) portabile Linux/Windows per creare stradari
cartografici in PDF a partire da OpenStreetMap. L'utente disegna sulla mappa
i quadranti da stampare; l'app genera un PDF con indice, mappa riassuntiva e
una pagina per quadrante.

---

## Funzionalità principali
- **Mappa interattiva**: pan con drag, zoom con rotella centrato sul cursore,
  ricerca città
- **Pagine**: click destro per aggiungere, drag per spostare, blocco
  manuale/automatico contro spostamenti accidentali, etichette automatiche
  (A1, B2…)
- **Gruppi POI**: marker con icona/colore personalizzabili, aggiunta diretta
  sulla mappa o via ricerca — 43 categorie predefinite più quelle
  personalizzate, ricerca dal vivo (Overpass) o offline (database locale
  scaricabile per continente), ricerca indirizzo e città
- **Percorsi**: disegno punto-per-punto sulla mappa, estendibili ed
  editabili in seguito
- **Import/export universale**: un solo comando importa KMZ/KML/GPX (POI e
  percorsi insieme); esportazione separata o combinata in un unico file.
  Nomi in script non latino ripuliti automaticamente; i punti in Cina, che
  possono essere in GCJ-02 invece di WGS84 reale (una particolarità nota
  anche di Google Maps), vengono gestiti con correzione automatica o
  manuale a seconda del formato
- **Generazione PDF**: anteprima prima di salvare, indice, mappa
  riassuntiva, pagine ordinate con riferimenti alle pagine adiacenti e scala
  grafica
- **Salvataggio progetto**: file `.stradario` (JSON) leggibile e
  modificabile a mano; le chiavi API restano solo nelle preferenze locali,
  mai nel file di progetto

---

## Tile server disponibili
OpenStreetMap Standard (default), OSM France, OSM Deutschland, OpenTopoMap,
CartoDB Light, Thunderforest Atlas/Neighbourhood, Stadia Alidade
Smooth/Stamen Toner Lite (gli ultimi quattro richiedono una API key).

---

## Requisiti e build
Richiede il [.NET 8 SDK](https://dotnet.microsoft.com/download). Il database
città (`cities500.csv`, GeoNames) si scarica da solo al primo utilizzo se
non presente — nessun intervento manuale necessario.

```bash
dotnet restore
dotnet run
# oppure, per un eseguibile distribuibile
dotnet publish -c Release -r linux-x64 --self-contained true
dotnet publish -c Release -r win-x64  --self-contained true
```

---

## Impostazioni
Tre tab in "⚙ Impostazioni": **Generale** (formato pagina, DPI, scala di
stampa da 1:1.000 a 1:1.000.000, tile server, contrasto mappa nel PDF),
**Categorie POI** (aggiunta di categorie di ricerca personalizzate) e
**Database POI offline** (download facoltativo per continente per la
ricerca POI senza rete).

---

## Per approfondire
Questa pagina è volutamente sintetica. Per l'architettura interna, le scelte
implementative e le note tecniche di sviluppo (struttura dei file, servizi,
dettagli su rendering/PDF/coordinate geografiche...) vedi
[CLAUDE.md](CLAUDE.md) nel repository, mantenuto aggiornato ad ogni
modifica sostanziale del codice.
