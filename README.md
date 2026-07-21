# StradarioApp

Applicazione desktop C# per la creazione di stradari cartografici (atlanti
stradali a pagine) a partire da OpenStreetMap. Portabile su **Linux** e
**Windows** grazie ad Avalonia UI.

---

## Funzionalità

1. **Impostazioni** – Formato pagina (A5/A4/A3), orientamento, DPI
   (72/96/150/300), scala cartografica (da 1:1.000 fino a 1:1.000.000, tutte
   le scale tipiche degli stradari — vedi elenco sotto), tile server (con o
   senza API key), blocco automatico oggetti inattivi, contrasto mappa nel PDF
2. **Mappa interattiva** – Pan (drag), zoom (rotella) centrato sul cursore
3. **Pagine** – Click destro per aggiungere una pagina; drag per spostarla;
   etichette automatiche (A1, B2…); blocco/sblocco per evitare spostamenti
   accidentali; bottone "📍 Città principali" nel dialog di modifica pagina
   per compilare la descrizione con le città più popolose dell'area
   (database GeoNames `cities500.csv`, opzionale)
4. **Gruppi POI** – Marker con icona/colore personalizzabili, aggiunta diretta
   sulla mappa, drag per riposizionare, ricerca POI per categoria (menu a
   tendina, ricordata tra le sessioni) con filtro testuale sul nome e, in
   opzione (richiede una chiave API Groq gratuita), un filtro AI più ampio
   quando il filtro letterale non trova nulla. In cima al menu, due voci
   speciali: **"Ricerca un indirizzo"** (geocoding libero via Nominatim) e
   **"Ricerca una città"** (nome anche parziale, o vuoto per le città già
   visibili nell'area — database GeoNames). Ogni ricerca mostra una
   finestra di log passo-passo con un pulsante "Annulla", che si chiude da
   sola a risultati ottenuti
5. **Percorsi** – Disegno di percorsi punto-per-punto direttamente sulla
   mappa, estendibili in seguito, con drag dei singoli vertici
6. **Import/Export KMZ/KML/GPX** – Importazione unificata (POI e percorsi
   nello stesso file), esportazione separata per gruppi POI e percorsi; i
   punti che cadono in Cina vengono corretti automaticamente da GCJ-02 a
   WGS84 in importazione e viceversa in esportazione (le mappe pubbliche
   cinesi offuscano le coordinate reali con un offset deterministico)
7. **Generazione PDF** – Anteprima prima del salvataggio, stradario completo
   con indice, mappa riassuntiva, eventuali pagine gazetteer POI, pagine
   mappa con riferimenti alle pagine adiacenti (N/S/E/O) e scala grafica;
   contrasto opzionale ottimizzato per la stampa in bianco e nero
8. **Salvataggio progetto** – File `.stradario` (JSON), leggibile e
   modificabile manualmente; le chiavi API (tile server, Groq) **non**
   vengono mai salvate nel progetto, solo nelle preferenze dell'applicazione

---

## Scale disponibili

1:1.000 · 1:5.000 · 1:10.000 · 1:15.000 · 1:20.000 · 1:25.000 · 1:50.000 ·
1:100.000 · 1:150.000 · 1:200.000 · 1:250.000 · 1:300.000 · 1:400.000 ·
1:500.000 · 1:800.000 · 1:1.000.000

La scala della mappa stampata è calcolata esattamente per il DPI scelto
(non è un'approssimazione legata allo zoom dei tile OSM).

---

## Dipendenze NuGet

| Pacchetto               | Uso                                       |
|--------------------------|-------------------------------------------|
| Avalonia                | UI cross-platform (Windows/Linux/macOS)    |
| Avalonia.Desktop        | Lifecycle desktop                          |
| Avalonia.Themes.Fluent  | Tema visuale                                |
| Avalonia.Fonts.Inter    | Font Inter                                  |
| Avalonia.Skia           | Canvas Skia custom su Avalonia 11           |
| SkiaSharp               | Rendering 2D (mappa, icone POI, percorsi)   |
| BruTile                 | Schema tile OSM (TileIndex)                 |
| PdfSharpCore            | Generazione PDF                             |
| Newtonsoft.Json         | Serializzazione progetto                    |

---

## Build e avvio

### Prerequisiti
- [.NET 8 SDK](https://dotnet.microsoft.com/download)
- `cities500.csv` (GeoNames, ~24 MB) per la ricerca città: **non serve
  procurarselo manualmente**, l'app lo scarica da sola al primo avvio se
  non lo trova — vedi sotto

```bash
dotnet restore
dotnet run          # build + avvio
dotnet build        # solo compilazione
```

### Pubblicazione

```bash
# Self-contained (include il runtime .NET, nessuna dipendenza da installare)
dotnet publish -c Release -r linux-x64 --self-contained true -o ./publish/linux
dotnet publish -c Release -r win-x64   --self-contained true -o ./publish/win

# Eseguibile singolo framework-dependent (più leggero, richiede .NET 8
# Runtime installato sulla macchina di destinazione)
dotnet publish -c Release -r linux-x64 --self-contained false \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./publish/linux
dotnet publish -c Release -r win-x64   --self-contained false \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./publish/win
```

### Download eseguibile già compilato

Le [Release](https://github.com/paolobia/StradarioApp/releases) del repo
GitHub contengono eseguibili singoli framework-dependent già pronti per
Linux e Windows (richiedono il [.NET 8 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
installato). `cities500.csv` non è incluso per motivi di dimensione, ma
l'app lo scarica da sola al primo avvio (vedi sotto) — non va procurato a parte.

---

## Struttura del progetto

```
StradarioApp/
├── Program.cs                     # Entry point Avalonia
├── StradarioApp.csproj
├── Models/
│   └── StradarioModels.cs         # Progetto, impostazioni, pagine, POI, percorsi, tile server
├── Services/
│   ├── GeoUtils.cs                 # Conversioni geografiche, zoom/scala esatta
│   ├── MapRenderer.cs              # Rendering mappa interattiva (tile + POI + percorsi)
│   ├── PdfGenerator.cs             # Generazione PDF (indice, overview, pagine, gazetteer)
│   ├── MapContrastFilter.cs        # Filtri di contrasto mappa per la stampa
│   ├── PercorsoRenderer.cs         # Disegno percorsi condiviso mappa/PDF
│   ├── PoiIconRenderer.cs          # Icone POI vettoriali condivise mappa/PDF/KMZ
│   ├── PoiService.cs / PercorsoService.cs   # Import/export KMZ/KML/GPX
│   ├── GcjTransform.cs             # Correzione GCJ-02 -> WGS84 per import in Cina
│   ├── PoiSearchService.cs         # Ricerca POI per categoria/indirizzo + filtro AI/Groq opzionale
│   ├── GroqClient.cs               # Client HTTP minimo per l'API Groq (filtro POI AI)
│   ├── KmlIo.cs                    # Caricamento XML KML/KMZ/GPX robusto a BOM/encoding
│   ├── CityDatabase.cs             # Database città GeoNames, download automatico se assente
│   ├── ProjectService.cs           # Salvataggio/caricamento progetto .stradario
│   ├── AppPreferencesService.cs    # Preferenze globali (chiavi API, ultima categoria POI), non nel progetto
│   ├── DebugLog.cs                 # Log diagnostico su file (chiamate Groq)
│   ├── FontResolver.cs             # Font per PdfSharpCore su Linux
│   └── RecentFilesService.cs       # Elenco progetti recenti
└── UI/
    ├── MainWindow.cs                # Finestra principale (tutto a codice, no AXAML)
    ├── MapCanvas.cs                 # Control Avalonia custom con canvas Skia
    ├── SettingsWindow.cs            # Dialog impostazioni
    ├── EditPageWindow.cs            # Dialog modifica pagina
    ├── PoiGroupEditWindow.cs / PoiItemEditWindow.cs
    ├── RouteEditWindow.cs
    ├── PoiSearchLogWindow.cs        # Log passo-passo di ogni ricerca POI, con Annulla
    └── ProgressWindow.cs            # Dialog avanzamento generazione PDF
```

---

## Uso rapido

1. Avvia l'app
2. *(Opzionale)* Clicca **⚙️ Impostazioni** per scegliere formato, DPI, scala e tile server
3. Naviga la mappa con **drag** (pan) e **rotella** (zoom)
4. **Click destro** sulla mappa per aggiungere una pagina
5. Dal pannello laterale, crea gruppi POI e percorsi (o importali da KMZ/KML/GPX)
6. Clicca **📄 Genera PDF**: l'app mostra un'anteprima, poi puoi salvarla o scartarla
7. Clicca **💾 Salva** per conservare il progetto come file `.stradario`

---

## Note tecniche

- I tile OSM vengono scaricati dal tile server scelto e tenuti in cache in memoria
- Il PDF include: eventuali pagine gazetteer POI, indice, mappa riassuntiva,
  una pagina per ogni pagina definita
- Le pagine nel PDF sono ordinate per righe (nord→sud), colonne (ovest→est)
- Ogni pagina PDF mostra i riferimenti alle pagine adiacenti (N/S/E/O) e la scala grafica
- Il file `.stradario` è JSON leggibile e modificabile manualmente
- Le chiavi API (tile server, Groq) sono salvate solo nelle preferenze
  dell'applicazione, mai nel file `.stradario`

---

## Database città (cities500.csv)

Usato dal bottone "📍 Città principali" e dalla ricerca POI "Ricerca una
città". Cercato nella cartella dell'eseguibile o in `~`; **se non trovato,
l'app lo scarica da sola** da [GeoNames](https://download.geonames.org/export/dump/cities500.zip)
al primo utilizzo e lo tiene in cache (`%AppData%/StradarioApp` su Windows,
`~/.config/StradarioApp` su Linux) — non serve procurarselo manualmente, né
per gli eseguibili delle Release. Richiede una connessione di rete al primo
avvio; se il download fallisce (rete assente), le funzioni che dipendono
dal database degradano silenziosamente (nessun crash) finché non è
disponibile.

---

## Licenza

Distribuito sotto licenza [GNU GPL v3.0](LICENSE) o successiva: puoi usare,
modificare e ridistribuire il codice liberamente, anche a fini commerciali,
a patto che le versioni derivate/distribuite restino open source con la
stessa licenza. I dati cartografici sono © OpenStreetMap contributors
(ODbL), soggetti ai termini separati di [OpenStreetMap](https://www.openstreetmap.org/copyright).
