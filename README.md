🇮🇹 **Italiano** | 🇬🇧 [English](README.en.md)

# StradarioApp

Applicazione desktop C# per la creazione di stradari cartografici (atlanti
stradali a pagine) a partire da OpenStreetMap. Portabile su Linux e Windows
grazie ad Avalonia UI.

🗺️ C'è anche **[StradarioViewer](https://paolobia.github.io/StradarioApp/)**, l'app compagna
da tenere in tasca per consultare i percorsi del progetto sul telefono.

---

## Funzionalità

- **Mappa interattiva**: pan con drag, zoom con rotella centrato sul cursore, ricerca città
- **Pagine**: click destro per aggiungere, drag per spostare, blocco manuale/automatico contro spostamenti accidentali, etichette automatiche (A1, B2…), **descrizione precompilata con le città più popolose dell'area** (database locale, GeoNames), riordino trascinabile nell'albero laterale, **orientamento e scala personalizzabili per singola pagina** (override rispetto alle impostazioni generali)
- **Gruppi POI**: marker con icona/colore personalizzabili (**l'icona si sceglie per singolo POI**, il gruppo definisce solo il colore condiviso), aggiunta diretta sulla mappa o via ricerca — 43 categorie predefinite più quelle personalizzate, ricerca dal vivo (Overpass, con punteggio locale offline sempre calcolato in aggiunta al filtro AI/Groq opzionale) o offline (database locale scaricabile per continente), ricerca indirizzo e città; spostamento di un POI fra gruppi con un gesto taglia/incolla; riordino trascinabile dei POI dentro il proprio gruppo, oppure automatico per data quando presente; un gruppo nascosto (occhio spento) è protetto da modifiche accidentali; il colore di un gruppo si sincronizza automaticamente con quello di un percorso coincidente
- **Percorsi**: disegno punto-per-punto sulla mappa (Invio o shift+clic per confermare, senza aggiungere punti spuri se si pan durante il disegno), estendibili ed editabili in seguito, instradabili su strada reale via OSRM (auto/bici/piedi, fino a 10 punti, alternative multiple per tratta scelte da un pannello dedicato ridimensionabile); l'etichetta si sposta automaticamente se troppo vicina a un POI; **qualunque punto del percorso può diventare un POI inline** (icona, etichetta, descrizione) direttamente dalla maschera di modifica, sempre col colore del percorso — l'icona viene anche suggerita automaticamente da parole chiave nel testo (italiano/inglese), senza dover creare un gruppo POI a parte; la maschera di modifica è ora organizzata in due tab (Percorso / Punti), con navigazione punto-per-punto tramite frecce e campi descrizione a piena altezza
- **Data/ora opzionali su POI e percorsi**: campo Da/A facoltativo su ogni POI e su ogni percorso — quando presente, l'albero di navigazione si riordina cronologicamente in automatico e il PDF genera una pagina "Piano di viaggio" dedicata (non datati in coda) subito dopo la copertina; senza alcuna data impostata il PDF resta identico a prima (nessuna pagina in più); nel PDF la data è preceduta dalla sigla del giorno della settimana (**Domenica in grassetto**), l'orario di inizio/fine viene omesso quando è 00:00/23:59, un evento che copre l'intera giornata (00:00 → 23:59 stesso giorno) non genera una riga "Fine" separata, e una sottile riga verticale continua separa la colonna data/ora dall'icona lungo tutta la tabella
- **Import/export universale**: un solo comando importa KMZ/KML/GPX (POI e percorsi insieme, con colori distinti per i gruppi POI importati, anche più file in un colpo solo); esportazione separata, combinata o di un singolo gruppo/percorso; anche in **CSV** (due file tabellari, uno per POI e uno per percorsi, apribili in Excel/LibreOffice); le date di POI/percorsi viaggiano nel KML/KMZ (ExtendedData) e si ritrovano intatte al reimport; un POI che coincide con un vertice di uno o più percorsi (es. un itinerario di viaggio con una base ripetuta ogni giorno) viene riconciliato come POI inline su quel/quei punto/i invece di restare duplicato — un gruppo POI rimasto vuoto dopo la riconciliazione non è né importato né esportato; nomi in script non latino ripuliti automaticamente (anche nel tooltip "Cosa c'è qui"); punti in Cina (possibile GCJ-02 anziché WGS84) gestiti con correzione automatica o manuale
- **Generazione PDF**: si apre subito nel visualizzatore di sistema (nessuna domanda di salvataggio — si salva da lì, se serve), **pagina di copertina con titolo** (più una mini-mappa schematica — confini/coste, laghi e fiumi principali, percorsi, città maggiori sopra una soglia di popolazione adattiva — quando il progetto ha percorsi), indice (omesso se non ci sono pagine mappa), mappa riassuntiva che inquadra e disegna anche POI e percorsi liberi, pagine con riferimenti alle adiacenti e scala grafica, descrizioni lunghe con word-wrap reale, gruppo POI mai separato dal proprio elenco da un'interruzione di pagina; un percorso ad anello (ultimo punto coincidente col primo) non ripete la descrizione già stampata all'inizio; **generabile anche con zero pagine mappa** (solo copertina, elenchi POI/percorsi, riassuntiva); 5 modalità di contrasto per la mappa stampata (nessuno, colore, bianco/nero, enfatizza strade, **adattivo/locale**), più **rinforzo contorni** e **retinatura per stampa B/N** (dithering) come opzioni indipendenti
- **Etichette leggibili e senza sovrapposizioni**: sia in stampa (pagine mappa e mappa riassuntiva) sia sulla mappa interattiva, ogni etichetta prova automaticamente più posizioni (destra/sinistra/sopra/sotto) scegliendo quella che si sovrappone meno alle altre — in stampa un'etichetta può restare nascosta se non c'è proprio spazio (priorità ai POI singoli sui POI dei percorsi), sulla mappa interattiva viene invece sempre mostrata comunque, nella posizione migliore disponibile; alone bianco pieno intorno al testo per restare leggibile su qualunque sfondo; POI diversi che coincidono nello stesso punto con la stessa etichetta la stampano una sola volta invece di sovrapporla; l'etichetta col nome di un percorso è ancorata al suo baricentro invece che al primo punto
- **Salvataggio progetto**: file `.stradario` (JSON) leggibile e modificabile a mano; le chiavi API restano solo nelle preferenze locali, mai nel progetto
- **Cancellazioni protette**: eliminare una pagina, un gruppo POI, un singolo POI, un percorso o un punto di un percorso chiede sempre conferma esplicita prima di procedere
- **Selezione dalla mappa**: cliccare un POI o un percorso bloccato sulla mappa (anche sulla linea o su un punto-POI inline, non solo su un vertice) lo rende corrente nell'albero di navigazione, espandendo il gruppo/ramo che lo contiene e scorrendo automaticamente fino a renderlo visibile
- **Impostazioni**: tre tab — Generale (formato pagina, DPI, scala di stampa da 1:1.000 a 1:1.000.000, tile server, contrasto mappa nel PDF), Categorie POI (aggiunta di categorie personalizzate), Database POI offline (download facoltativo per continente)
- **[StradarioViewer](https://paolobia.github.io/StradarioApp/)**, l'app compagna: StradarioApp è pensato per creare/modificare progetti su desktop, non per essere consultato in mobilità — da qui **StradarioViewer**, una app separata (Blazor WebAssembly, installabile come PWA sul telefono) che carica lo stesso file `.stradario` e mostra solo i percorsi/POI datati del giorno corrente (con navigazione al giorno prima/dopo) su una mappa, per avere l'itinerario di viaggio sotto mano senza portarsi dietro il progetto completo o un PC; funziona offline dopo il primo caricamento, i dati restano nel browser (`localStorage`), nessun server coinvolto

---

## Gallery

23 screenshot reali (dati di esempio, progetto "Firenze Demo" inventato),
organizzati per area funzionale — dalla mappa interattiva all'output PDF
finale, che è il vero prodotto dell'app.

### 🗺️ Mappa e pagine multiple

<table>
<tr>
<td width="50%">
<img src="docs/screenshots/01-multi-page-grid.png" width="100%" alt="Multi-page grid" />
<sub>Four A4 pages tiled in a 2×2 grid over the project area, with POI groups and a route overlaid.</sub>
</td>
<td width="50%">
<img src="docs/screenshots/08-misura-distanza.png" width="100%" alt="Ruler distance" />
<sub>Ruler tool: click-to-click distance measurement between two points on the map.</sub>
</td>
</tr>
</table>

### 🔍 Ricerca POI

<table>
<tr>
<td width="50%">
<img src="docs/screenshots/02-poi-search-results.png" width="100%" alt="Category search results" />
<sub>Category search (offline database): hundreds of matches shown as markers, click one to add it.</sub>
</td>
<td width="50%">
<img src="docs/screenshots/03-ricerca-indirizzo.png" width="100%" alt="Address search" />
<sub>Address search: type a place name to locate and jump to it on the map.</sub>
</td>
</tr>
</table>

### 📍 Gestione POI e percorsi

<table>
<tr>
<td width="50%">
<img src="docs/screenshots/04-poi-gruppi.png" width="100%" alt="POI groups" />
<sub>Multiple POI groups, each with its own icon and color, managed from the navigation tree.</sub>
</td>
<td width="50%">
<img src="docs/screenshots/05-poi-tree.png" width="100%" alt="POI tree" />
<sub>A POI group expanded in the tree, listing every point with its coordinates.</sub>
</td>
</tr>
<tr>
<td width="50%">
<img src="docs/screenshots/06-percorso-disegno.png" width="100%" alt="Freehand route drawing" />
<sub>Freehand route drawing, point by point, directly on the map.</sub>
</td>
<td width="50%">
<img src="docs/screenshots/07-percorso-osrm.png" width="100%" alt="OSRM routing" />
<sub>Road-accurate routing via OSRM, with per-leg distance and duration.</sub>
</td>
</tr>
<tr>
<td width="50%">
<img src="docs/screenshots/22-percorso-poi-inline-dialog.png" width="100%" alt="Inline route-point POI editor" />
<sub>Any route point can be marked as a POI right from the route editor — icon, label and description inline, always the route's own color.</sub>
</td>
<td width="50%">
<img src="docs/screenshots/23-percorso-poi-inline-mappa.png" width="100%" alt="Inline route-point POI on the map" />
<sub>The marked point renders as a real marker on the map (and in the PDF), sharing the route's color — no separate POI group needed.</sub>
</td>
</tr>
</table>

### 📄 Output PDF — l'atlante stampato

<table>
<tr>
<td width="50%">
<img src="docs/screenshots/09-pdf-copertina.png" width="100%" alt="PDF cover page" />
<sub>Cover page: project name, scale, page size and generation date, plus a schematic locator map (country borders/coastlines, lakes and major rivers, routes and major cities) when the project has routes.</sub>
</td>
<td width="50%">
<img src="docs/screenshots/12-pdf-indice.png" width="100%" alt="PDF index page" />
<sub>Index page: every page listed with its center coordinates and description.</sub>
</td>
</tr>
<tr>
<td width="50%">
<img src="docs/screenshots/13-pdf-overview.png" width="100%" alt="PDF overview page" />
<sub>Overview page: all pages plotted on one map, with page-number references.</sub>
</td>
<td width="50%">
<img src="docs/screenshots/10-pdf-gazetteer.png" width="100%" alt="PDF POI gazetteer" />
<sub>POI gazetteer page, grouped by category, auto-paginated when it doesn't fit on one page.</sub>
</td>
</tr>
<tr>
<td width="50%">
<img src="docs/screenshots/11-pdf-percorsi.png" width="100%" alt="PDF routes summary" />
<sub>Routes summary page: each route with its total distance and point count.</sub>
</td>
<td width="50%">
<img src="docs/screenshots/15-pdf-pagina-mappa.png" width="100%" alt="PDF map page" />
<sub>A full map page as it prints: POI, routes and labels inside the page's own borders.</sub>
</td>
</tr>
<tr>
<td width="50%">
<img src="docs/screenshots/14-pdf-pagina-bordi.png" width="100%" alt="PDF page borders" />
<sub>A page's border: neighboring-page references (north/west) and the graphic scale bar.</sub>
</td>
<td width="50%">
<img src="docs/screenshots/16-pdf-salva-dialog.png" width="100%" alt="PDF preview dialog" />
<sub>The generated PDF opens in the system viewer first for preview; "Save" writes it to a permanent location.</sub>
</td>
</tr>
</table>

### 🎨 Contrasto mappa nel PDF — stessa pagina, 5 modalità

<table>
<tr>
<td width="33%">
<img src="docs/screenshots/17-contrasto-nessuno.png" width="100%" alt="Contrast: none" />
<sub><b>Nessuno</b> — original OSM Carto colors.</sub>
</td>
<td width="33%">
<img src="docs/screenshots/18-contrasto-colore.png" width="100%" alt="Contrast: boost color" />
<sub><b>Contrasta colore</b> — boosted saturation and contrast, still full color.</sub>
</td>
<td width="33%">
<img src="docs/screenshots/19-contrasto-bn.png" width="100%" alt="Contrast: black & white" />
<sub><b>Contrasta B/N</b> — perceptual grayscale tuned for legibility on B/W printers.</sub>
</td>
</tr>
<tr>
<td width="33%">
<img src="docs/screenshots/20-contrasto-strade.png" width="100%" alt="Contrast: emphasize roads" />
<sub><b>Enfatizza strade</b> — road network highlighted, area fills desaturated.</sub>
</td>
<td width="33%">
<img src="docs/screenshots/21-contrasto-adattivo.png" width="100%" alt="Contrast: adaptive" />
<sub><b>Contrasto adattivo</b> (CLAHE) — local contrast stretching, still in color.</sub>
</td>
<td width="33%"></td>
</tr>
</table>

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
│   ├── PoiSearchService.cs         # Ricerca POI per categoria/indirizzo + filtro AI/Groq opzionale + punteggio locale
│   ├── RouteInstradationService.cs # Instradamento OSRM di un Percorso su strada reale (auto/bici/piedi)
│   ├── GroqClient.cs               # Client HTTP minimo per l'API Groq (filtro POI AI)
│   ├── KmlIo.cs                    # Caricamento XML KML/KMZ/GPX robusto a BOM/encoding
│   ├── CsvIo.cs                    # Lettura/scrittura CSV minimale (RFC 4180) per import/export POI/percorsi
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
    ├── RouteInstradationPanel.cs    # Pannello alternative/distanza/durata durante l'instradamento OSRM
    ├── PoiSearchLogWindow.cs        # Log passo-passo di ogni ricerca POI, con Annulla/OK
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
