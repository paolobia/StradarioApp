🇮🇹 **Italiano** | 🇬🇧 [English](README.en.md)

# StradarioApp

Applicazione desktop C# per la creazione di stradari cartografici (atlanti
stradali a pagine) a partire da OpenStreetMap. Portabile su Linux e Windows
grazie ad Avalonia UI.

---

## Funzionalità

- **Mappa interattiva**: pan con drag, zoom con rotella centrato sul cursore, ricerca città
- **Pagine**: click destro per aggiungere, drag per spostare, blocco manuale/automatico contro spostamenti accidentali, etichette automatiche (A1, B2…)
- **Gruppi POI**: marker con icona/colore personalizzabili, aggiunta diretta sulla mappa o via ricerca — 43 categorie predefinite più quelle personalizzate, ricerca dal vivo (Overpass) o offline (database locale scaricabile per continente), ricerca indirizzo e città
- **Percorsi**: disegno punto-per-punto sulla mappa, estendibili ed editabili in seguito
- **Import/export universale**: un solo comando importa KMZ/KML/GPX (POI e percorsi insieme); esportazione separata o combinata in un unico file; nomi in script non latino ripuliti automaticamente; punti in Cina (possibile GCJ-02 anziché WGS84) gestiti con correzione automatica o manuale
- **Generazione PDF**: anteprima prima di salvare, indice, mappa riassuntiva, pagine con riferimenti alle adiacenti e scala grafica
- **Salvataggio progetto**: file `.stradario` (JSON) leggibile e modificabile a mano; le chiavi API restano solo nelle preferenze locali, mai nel progetto
- **Impostazioni**: tre tab — Generale (formato pagina, DPI, scala di stampa da 1:1.000 a 1:1.000.000, tile server, contrasto mappa nel PDF), Categorie POI (aggiunta di categorie personalizzate), Database POI offline (download facoltativo per continente)

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
├── CategoriePOI.txt                # Elenco (key=value) delle categorie POI predefinite, per riferimento
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
