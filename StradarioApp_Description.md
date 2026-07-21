# StradarioApp — Descrizione del progetto

## Cos'è
Applicazione desktop C# (.NET 8) portabile Linux/Windows per creare stradari
cartografici in PDF. L'utente disegna sulla mappa i quadranti da stampare,
l'app genera un PDF con indice, mappa riassuntiva e una pagina per quadrante.

---

## Stack
| Componente | Tecnologia |
|---|---|
| GUI | Avalonia 11.2.0 (cross-platform, no WinForms/WPF) |
| Rendering mappa | SkiaSharp 2.88.8 + canvas custom (no pacchetti Views) |
| Tile OSM | BruTile 4.0.0 (solo TileIndex), download via HttpClient |
| Generazione PDF | PdfSharpCore 1.3.65 |
| Serializzazione | Newtonsoft.Json 13.0.3 |
| Font PDF Linux | FontResolver custom (IFontResolver) |
| Database città | GeoNames cities500.csv (scaricato automaticamente se assente) |

---

## Funzionalità
1. **Impostazioni**: due tab. "Generale": formato pagina (A5/A4/A3), orientamento,
   DPI (72/96/150/300), scala (da 1:1.000 a 1:1.000.000, 16 valori — tutte le
   scale tipiche degli stradari cittadini e stradali), tile server (con o senza
   API key), blocco automatico, contrasto mappa nel PDF (Nessuno/Colore/B-N/
   Enfatizza strade). "Categorie POI": aggiunta di categorie di ricerca
   personalizzate (etichetta + tag OSM `chiave=valore`), in coda alle
   predefinite, persistite globalmente
2. **Mappa interattiva**: pan con drag, zoom con rotella centrato sul cursore,
   tile OSM con cache in memoria e retry automatico sui fallimenti, ricerca
   città (autocomplete GeoNames)
3. **Gestione pagine**: click destro per aggiungere, drag per spostare, ✏ per
   modificare (etichetta, descrizione multilinea, coordinate), ✕ per
   cancellare, 🔒/🔓 blocco manuale + auto-lock dopo inattività
4. **Descrizione automatica**: bottone "📍 Città principali" cerca in cities500.csv
   le città più popolose nel bounding box della pagina
5. **Gruppi POI**: marker con icona/colore configurabili (rendering vettoriale
   condiviso mappa/PDF/KMZ via `PoiIconRenderer`), aggiunta diretta sulla mappa
   con auto-label `POI<n>`, drag per riposizionare. Ricerca per categoria, 43
   predefinite (elenco `key=value` in `CategoriePOI.txt`; menu a tendina,
   filtro testuale sul nome, fallback AI/Groq opzionale se il filtro
   letterale non trova nulla, estendibile dalle
   Impostazioni con categorie personalizzate), più due voci speciali in cima al
   menu: ricerca indirizzo (Nominatim) e ricerca città (GeoNames, nome anche
   parziale o vuoto per quelle visibili). Ogni ricerca mostra una finestra
   di log passo-passo con pulsante Annulla (`PoiSearchLogWindow`)
6. **Percorsi**: disegno punto-per-punto sulla mappa (click = punto,
   shift+click = fine), auto-label `PATH<n>`, estensione di percorsi
   esistenti, drag dei singoli vertici
7. **Import/Export unificato**: un solo pulsante toolbar importa KMZ/KML/GPX
   (POI e percorsi nello stesso file, merge automatico); export separato per
   gruppi POI e percorsi in KMZ/KML/GPX a seconda dell'estensione scelta.
   Punti in Cina corretti GCJ-02→WGS84 in import e simmetricamente
   WGS84→GCJ-02 in export (`GcjTransform`)
8. **Generazione PDF**: anteprima (temp file → viewer di sistema → dialog
   Salva/Chiudi) prima di chiedere dove salvare; indice + mappa riassuntiva +
   eventuali pagine gazetteer POI + pagine mappa ordinate con bordi
   adiacenti e scala grafica
9. **Salvataggio progetto**: file `.stradario` (JSON) con tutte le
   impostazioni/pagine/POI/percorsi — le chiavi API (`TileServerApiKey`,
   `GroqApiKey`) sono `[JsonIgnore]`: mai scritte nel progetto, vivono solo
   in `AppPreferencesService` (preferenze utente globali)

---

## Tile server disponibili (hardcoded in TileServers.All)
- OpenStreetMap Standard ← **default**
- OSM France
- OSM Deutschland
- OpenTopoMap
- CartoDB Light
- Thunderforest Atlas (richiede API key)
- Thunderforest Neighbourhood (richiede API key)
- Stadia Alidade Smooth (richiede API key)
- Stadia Stamen Toner Lite (richiede API key)

---

## Struttura file
```
StradarioApp/
├── Program.cs                   Avvio: FontResolver.Register() + CityDatabase.EnsureLoaded()
├── StradarioApp.csproj
├── CategoriePOI.txt              Elenco (key=value) delle categorie POI predefinite, per riferimento
├── Models/
│   └── StradarioModels.cs       Tutti i tipi dati (Settings, MapPage, GeoRect, Project,
│                                 TileServers, PoiGroup/PoiItem, Percorso)
├── Services/
│   ├── GeoUtils.cs              Conversioni geo↔pixel, CalcOptimalZoom, CalcScaleExactTiling
│   ├── MapRenderer.cs           TileCache + rendering mappa (tile + POI + percorsi) su SKCanvas
│   ├── PdfGenerator.cs          Generazione PDF (indice, overview, pagine, gazetteer, scala grafica)
│   ├── MapContrastFilter.cs     Filtri contrasto mappa per stampa (Colore/B-N/Enfatizza strade)
│   ├── PercorsoRenderer.cs      Disegno percorsi condiviso mappa/PDF
│   ├── PoiIconRenderer.cs       Icone POI vettoriali condivise mappa/PDF/KMZ
│   ├── PoiService.cs            Import/export KMZ/KML/GPX gruppi POI
│   ├── PercorsoService.cs       Import/export KMZ/KML/GPX percorsi
│   ├── PoiSearchService.cs      Ricerca POI (parole chiave + Nominatim + AI/Groq opzionale)
│   ├── GroqClient.cs            Client minimo chat completions Groq (compatibile OpenAI)
│   ├── KmlIo.cs                 Caricamento XML KML/KMZ/GPX robusto a BOM/encoding, SanitizeName
│   ├── GeolocationService.cs    Geolocalizzazione IP di fallback
│   ├── ProjectService.cs        Salva/carica .stradario (JSON)
│   ├── AppPreferencesService.cs Preferenze globali (chiavi API), NON nel progetto
│   ├── FontResolver.cs          Font per PdfSharpCore su Linux (IFontResolver)
│   ├── CityDatabase.cs          Carica cities500.csv (download automatico se assente),
│   │                             FindTopCities/SearchByName + alias IT→GeoNames
│   └── RecentFilesService.cs    Elenco progetti recenti
└── UI/
    ├── MapCanvas.cs             Control Avalonia custom che espone SKCanvas (via Avalonia.Skia)
    ├── MainWindow.cs            Finestra principale (tutto a codice, no AXAML)
    ├── SettingsWindow.cs        Dialog impostazioni
    ├── EditPageWindow.cs        Dialog modifica pagina + bottone città
    ├── PoiGroupEditWindow.cs / PoiItemEditWindow.cs   Dialog gruppi/POI
    ├── RouteEditWindow.cs       Dialog modifica percorso
    ├── PoiManagerWindow.cs      Leftover, NON usato come entry point (vedi CLAUDE.md)
    ├── PoiSearchLogWindow.cs    Log passo-passo di ogni ricerca POI, con Annulla
    └── ProgressWindow.cs        Dialog avanzamento generazione PDF
```

---

## Note critiche per non sbagliare

**Pacchetti**: `SkiaSharp.Views.Avalonia` non esiste. Il canvas SkiaSharp
si implementa con `MapCanvas : Control` che usa `ICustomDrawOperation` e
`ISkiaSharpApiLeaseFeature` dal pacchetto `Avalonia.Skia 11.2.0`.

**Zoom OSM**: `CalcOptimalZoom` usa sempre **96 DPI fissi** (standard OSM),
mai `settings.Dpi`. Il DPI di stampa entra solo in `CalcTileSizePx = 256 * Dpi/96`
che scala la dimensione di disegno dei tile nel PDF.

**Scala PDF corretta**: i tile sono 256px a 96 DPI. Stampando a 150 DPI ogni
tile va disegnato a 400px (`256 * 150/96`) per coprire la stessa area geografica.
`RenderTilesAsync` riceve `tileSizePx` come parametro — pagine mappa usano
`CalcTileSizePx`, la mappa overview usa 256 fisso.

**Zoom sul cursore**: in `OnMapWheelChanged` — calcola le coordinate geo sotto
il cursore prima dello zoom, applica lo zoom, ricalcola dove è finito quel punto,
trasla il centro per annullare lo scarto. Per lat usa coordinate tile Mercatore
(`LatToTileY` → sposta → `TileYToLat`), non gradi lineari.

**FontResolver**: `IFontResolver` richiede la proprietà `DefaultFontName`
(→ `"dejavusans|false|false"`). Senza di essa: errore CS0535.

**Tema**: `RequestedThemeVariant = ThemeVariant.Light` in `App.Initialize()`
per evitare testi bianchi illeggibili su Windows dark mode.

**Dialog file**: usare `StorageProvider.OpenFilePickerAsync` e
`StorageProvider.SaveFilePickerAsync` (API Avalonia 11). `OpenFileDialog`
e `SaveFileDialog` sono obsoleti (CS0618).

**ToolTip su Button**: non è una proprietà del costruttore. Usare
`ToolTip.SetTip(btn, "testo")` dopo la creazione.

**TileIndex BruTile 4.x**: il terzo parametro è `int`, non `string`.
`new TileIndex(x, y, zoomInt)` — senza `.ToString()`.

**Ordinamento pagine PDF**: semplice `OrderBy(lat).ThenBy(lon)` non funziona
per pagine adiacenti con lievi disallineamenti. Usare `SortPages()` che raggruppa
in righe con tolleranza del 40% dell'altezza geografica media.

**Rettangoli overview**: la conversione geo→PDF deve usare la stessa proiezione
WebMercator dei tile (via `LonToTileX`/`LatToTileY`), non una proiezione lineare
in gradi. Altrimenti i rettangoli non si sovrappongono alla mappa di sfondo.

**zLat nell'overview**: `zLat = log2(pixH * 360 / (256 * latExtent * cosLat))`
— moltiplicare per `cosLat`, non dividere. Dividere deforma i rettangoli in altezza.

**Chiusura finestra**: `Closing` con `e.Cancel = true` blocca la chiusura per
mostrare il dialog "salva?". Prima di `Close()` finale fare
`Closing -= handler` per evitare loop ricorsivo.

**Cache tile**: cachare solo i successi, mai i fallimenti. I tile falliti
vengono ritentati al prossimo frame. La chiave include il server URL:
`"serverUrl|z/x/y"` — così cambiare server non mescola tile diversi.

**cities500.csv**: se non trovato nella cartella dell'eseguibile/`~`,
`CityDatabase.DownloadAndExtract` lo scarica da sola da
https://download.geonames.org/export/dump/cities500.zip e lo mette in
cache in `%AppData%/StradarioApp` (formato TSV grezzo GeoNames, diverso dal
CSV con intestazione atteso se fornito a mano — due parser distinti
selezionati per estensione, `.txt` vs `.csv`). Il caricamento parte in
background all'avvio (`Program.cs` → `CityDatabase.EnsureLoaded()`).

---

## File esterno necessario
`cities500.csv`: non richiede più intervento manuale, l'app lo scarica da
sola al primo utilizzo se non lo trova (vedi sopra). Se il download fallisce
(rete assente), il bottone "📍 Città principali" e la ricerca POI "Ricerca
una città" mostrano un avviso ma tutto il resto funziona normalmente.

---

## Build
```bash
dotnet restore
dotnet run
# oppure
dotnet publish -c Release -r linux-x64 --self-contained true
dotnet publish -c Release -r win-x64  --self-contained true
```
