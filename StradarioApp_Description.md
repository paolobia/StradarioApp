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
| Database città | GeoNames cities500.csv (file esterno) |

---

## Funzionalità
1. **Impostazioni**: formato pagina (A5/A4/A3), orientamento, DPI (72/96/150/300),
   scala (1:1.000 / 1:5.000 / 1:10.000 / 1:100.000 / 1:200.000), tile server
2. **Mappa interattiva**: pan con drag, zoom con rotella centrato sul cursore,
   tile OSM con cache in memoria e retry automatico sui fallimenti
3. **Gestione pagine**: click per aggiungere, drag per spostare, ✏ per modificare
   (etichetta, descrizione multilinea, coordinate), ✕ per cancellare
4. **Descrizione automatica**: bottone "📍 Città principali" cerca in cities500.csv
   le 3 città più popolose nel bounding box della pagina
5. **Generazione PDF**: indice + mappa riassuntiva + pagine ordinate con bordi
   adiacenti e scala grafica (barra km/cm)
6. **Salvataggio progetto**: file `.stradario` (JSON) con tutte le impostazioni

---

## Tile server disponibili (hardcoded in TileServers.All)
- OpenStreetMap Standard ← **default**
- OSM France
- OSM Deutschland
- OpenTopoMap
- CartoDB Light

---

## Struttura file
```
StradarioApp/
├── Program.cs                   Avvio: FontResolver.Register() + CityDatabase.EnsureLoaded()
├── StradarioApp.csproj
├── Models/
│   └── StradarioModels.cs       Tutti i tipi dati (Settings, MapPage, GeoRect, Project, TileServers)
├── Services/
│   ├── GeoUtils.cs              Conversioni geo↔pixel, CalcOptimalZoom, CalcTileSizePx, CalcScaleBarKm
│   ├── MapRenderer.cs           TileCache + rendering mappa su SKCanvas
│   ├── PdfGenerator.cs          Generazione PDF (indice, overview, pagine, scala grafica)
│   ├── ProjectService.cs        Salva/carica .stradario (JSON)
│   ├── FontResolver.cs          Font per PdfSharpCore su Linux (IFontResolver)
│   └── CityDatabase.cs          Carica cities500.csv, FindTopCities(bounds, n)
└── UI/
    ├── MapCanvas.cs             Control Avalonia custom che espone SKCanvas (via Avalonia.Skia)
    ├── MainWindow.cs            Finestra principale (tutto a codice, no AXAML)
    ├── SettingsWindow.cs        Dialog impostazioni
    ├── EditPageWindow.cs        Dialog modifica pagina + bottone città
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

**cities500.csv**: va scaricato da https://download.geonames.org/export/dump/cities500.zip
e messo nella stessa cartella dell'eseguibile. Il parser CSV gestisce campi
tra virgolette (formato GeoNames). Il caricamento parte in background all'avvio.

---

## File esterno necessario
`cities500.csv` nella stessa cartella dell'eseguibile (o di `dotnet run`).
Senza di esso il bottone "📍 Città principali" mostra un avviso ma tutto
il resto funziona normalmente.

---

## Build
```bash
dotnet restore
dotnet run
# oppure
dotnet publish -c Release -r linux-x64 --self-contained true
dotnet publish -c Release -r win-x64  --self-contained true
```
