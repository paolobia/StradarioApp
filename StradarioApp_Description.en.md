🇮🇹 [Italiano](StradarioApp_Description.md) | 🇬🇧 **English**

# StradarioApp — Project description

## What it is
A portable Linux/Windows C# (.NET 8) desktop application for creating
cartographic street atlases (*stradari*) in PDF. The user draws the
quadrants to print on the map, and the app generates a PDF with an index,
an overview map, and one page per quadrant.

---

## Stack
| Component | Technology |
|---|---|
| GUI | Avalonia 11.2.0 (cross-platform, no WinForms/WPF) |
| Map rendering | SkiaSharp 2.88.8 + custom canvas (no Views packages) |
| OSM tiles | BruTile 4.0.0 (TileIndex only), download via HttpClient |
| PDF generation | PdfSharpCore 1.3.65 |
| Serialization | Newtonsoft.Json 13.0.3 |
| Linux PDF fonts | Custom FontResolver (IFontResolver) |
| City database | GeoNames cities500.csv (downloaded automatically if missing) |

---

## Features
1. **Settings**: two tabs. "Generale" (General): page format (A5/A4/A3), orientation,
   DPI (72/96/150/300), scale (from 1:1.000 to 1:1.000.000, 16 values — all the
   typical scales used by city/road street atlases), tile server (with or without
   an API key), auto-lock, PDF map contrast (None/Color/B-W/
   Emphasize roads). "Categorie POI" (POI categories): adding custom search
   categories (label + OSM tag `key=value`), appended after the
   built-in ones, persisted globally
2. **Interactive map**: pan by dragging, zoom with the scroll wheel centered on the cursor,
   OSM tiles with in-memory cache and automatic retry on failures, city
   search (GeoNames autocomplete)
3. **Page management**: right-click to add, drag to move, ✏ to
   edit (label, multi-line description, coordinates), ✕ to
   delete, 🔒/🔓 manual lock + auto-lock after inactivity
4. **Automatic description**: "📍 Città principali" (Main cities) button searches cities500.csv
   for the most populous cities within the page's bounding box
5. **POI groups**: markers with configurable icon/color (vector rendering
   shared across map/PDF/KMZ via `PoiIconRenderer`), added directly on the map
   with auto-label `POI<n>`, drag to reposition. Search by category, 43
   built-in ones (`key=value` list in `CategoriePOI.txt`; dropdown menu,
   text filter on the name, optional AI/Groq fallback if the
   literal filter finds nothing, extensible from
   Settings with custom categories), plus two special entries at the top
   of the menu: address search (Nominatim) and city search (GeoNames, name can be
   partial or empty for the ones currently visible). Every search shows a
   step-by-step log window with a Cancel button (`PoiSearchLogWindow`)
6. **Routes (Percorsi)**: point-by-point drawing on the map (click = point,
   shift+click = finish), auto-label `PATH<n>`, extension of existing
   routes, dragging of individual vertices
7. **Unified import/export**: a single toolbar button imports KMZ/KML/GPX
   (POI and routes in the same file, automatic merge); separate export for
   POI groups and routes in KMZ/KML/GPX depending on the extension chosen.
   Points in China are corrected GCJ-02→WGS84 on import and symmetrically
   WGS84→GCJ-02 on export (`GcjTransform`)
8. **PDF generation**: preview (temp file → system viewer → Save/Close
   dialog) before asking where to save; index + overview map +
   any POI gazetteer pages + map pages ordered with adjacent
   borders and a graphic scale bar
9. **Project saving**: `.stradario` file (JSON) with all
   settings/pages/POI/routes — the API keys (`TileServerApiKey`,
   `GroqApiKey`) are `[JsonIgnore]`: never written into the project, they only live
   in `AppPreferencesService` (global user preferences)

---

## Available tile servers (hardcoded in TileServers.All)
- OpenStreetMap Standard ← **default**
- OSM France
- OSM Deutschland
- OpenTopoMap
- CartoDB Light
- Thunderforest Atlas (requires an API key)
- Thunderforest Neighbourhood (requires an API key)
- Stadia Alidade Smooth (requires an API key)
- Stadia Stamen Toner Lite (requires an API key)

---

## File structure
```
StradarioApp/
├── Program.cs                   Startup: FontResolver.Register() + CityDatabase.EnsureLoaded()
├── StradarioApp.csproj
├── CategoriePOI.txt              List (key=value) of the built-in POI categories, for reference
├── Models/
│   └── StradarioModels.cs       All data types (Settings, MapPage, GeoRect, Project,
│                                 TileServers, PoiGroup/PoiItem, Percorso)
├── Services/
│   ├── GeoUtils.cs              Geo↔pixel conversions, CalcOptimalZoom, CalcScaleExactTiling
│   ├── MapRenderer.cs           TileCache + map rendering (tiles + POI + routes) on SKCanvas
│   ├── PdfGenerator.cs          PDF generation (index, overview, pages, gazetteer, scale bar)
│   ├── MapContrastFilter.cs     Map contrast filters for printing (Color/B-W/Emphasize roads)
│   ├── PercorsoRenderer.cs      Route drawing shared between map/PDF
│   ├── PoiIconRenderer.cs       Vector POI icons shared between map/PDF/KMZ
│   ├── PoiService.cs            Import/export KMZ/KML/GPX for POI groups
│   ├── PercorsoService.cs       Import/export KMZ/KML/GPX for routes
│   ├── PoiSearchService.cs      POI search (keywords + Nominatim + optional AI/Groq)
│   ├── GroqClient.cs            Minimal Groq chat completions client (OpenAI-compatible)
│   ├── KmlIo.cs                 KML/KMZ/GPX XML loading, robust to BOM/encoding, SanitizeName
│   ├── GeolocationService.cs    Fallback IP geolocation
│   ├── ProjectService.cs        Save/load .stradario (JSON)
│   ├── AppPreferencesService.cs Global preferences (API keys), NOT in the project
│   ├── FontResolver.cs          Fonts for PdfSharpCore on Linux (IFontResolver)
│   ├── CityDatabase.cs          Loads cities500.csv (automatic download if missing),
│   │                             FindTopCities/SearchByName + IT→GeoNames aliases
│   └── RecentFilesService.cs    List of recent projects
└── UI/
    ├── MapCanvas.cs             Custom Avalonia Control exposing an SKCanvas (via Avalonia.Skia)
    ├── MainWindow.cs            Main window (all code-behind, no AXAML)
    ├── SettingsWindow.cs        Settings dialog
    ├── EditPageWindow.cs        Page edit dialog + city button
    ├── PoiGroupEditWindow.cs / PoiItemEditWindow.cs   Group/POI dialogs
    ├── RouteEditWindow.cs       Route edit dialog
    ├── PoiManagerWindow.cs      Leftover, NOT used as an entry point (see CLAUDE.md)
    ├── PoiSearchLogWindow.cs    Step-by-step log of every POI search, with Cancel
    └── ProgressWindow.cs        PDF generation progress dialog
```

---

## Critical notes to avoid mistakes

**Packages**: `SkiaSharp.Views.Avalonia` does not exist. The SkiaSharp canvas
is implemented with `MapCanvas : Control` using `ICustomDrawOperation` and
`ISkiaSharpApiLeaseFeature` from the `Avalonia.Skia 11.2.0` package.

**OSM zoom**: `CalcOptimalZoom` always uses a **fixed 96 DPI** (OSM standard),
never `settings.Dpi`. Print DPI only enters into `CalcTileSizePx = 256 * Dpi/96`,
which scales the tile drawing size in the PDF.

**Correct PDF scale**: tiles are 256px at 96 DPI. Printing at 150 DPI means every
tile must be drawn at 400px (`256 * 150/96`) to cover the same geographic area.
`RenderTilesAsync` receives `tileSizePx` as a parameter — map pages use
`CalcTileSizePx`, the overview map uses a fixed 256.

**Zoom on the cursor**: in `OnMapWheelChanged` — compute the geo coordinates under
the cursor before zooming, apply the zoom, recompute where that point ended up,
translate the center to cancel out the offset. For latitude, use Mercator tile
coordinates (`LatToTileY` → shift → `TileYToLat`), not linear degrees.

**FontResolver**: `IFontResolver` requires the `DefaultFontName`
property (→ `"dejavusans|false|false"`). Without it: CS0535 error.

**Theme**: `RequestedThemeVariant = ThemeVariant.Light` in `App.Initialize()`
to avoid unreadable white text on Windows dark mode.

**File dialogs**: use `StorageProvider.OpenFilePickerAsync` and
`StorageProvider.SaveFilePickerAsync` (Avalonia 11 API). `OpenFileDialog`
and `SaveFileDialog` are obsolete (CS0618).

**ToolTip on Button**: not a constructor property. Use
`ToolTip.SetTip(btn, "text")` after creation.

**BruTile 4.x TileIndex**: the third parameter is an `int`, not a `string`.
`new TileIndex(x, y, zoomInt)` — without `.ToString()`.

**PDF page ordering**: a plain `OrderBy(lat).ThenBy(lon)` doesn't work
for adjacent pages with slight misalignments. Use `SortPages()`, which groups
pages into rows with a tolerance of 40% of the average geographic height.

**Overview rectangles**: the geo→PDF conversion must use the same WebMercator
projection as the tiles (via `LonToTileX`/`LatToTileY`), not a linear
degree-based projection. Otherwise the rectangles won't line up with the background map.

**zLat in the overview**: `zLat = log2(pixH * 360 / (256 * latExtent * cosLat))`
— multiply by `cosLat`, don't divide. Dividing distorts the rectangles' height.

**Window closing**: `Closing` with `e.Cancel = true` blocks closing in order to
show the "save?" dialog. Before the final `Close()`, do
`Closing -= handler` to avoid a recursive loop.

**Tile cache**: cache only successes, never failures. Failed tiles
are retried on the next frame. The key includes the server URL:
`"serverUrl|z/x/y"` — so switching server doesn't mix up different tiles.

**cities500.csv**: if not found in the executable's folder/`~`,
`CityDatabase.DownloadAndExtract` downloads it on its own from
https://download.geonames.org/export/dump/cities500.zip and caches it
in `%AppData%/StradarioApp` (raw GeoNames TSV format, different from
the CSV-with-header expected if provided by hand — two separate parsers
selected by extension, `.txt` vs `.csv`). Loading starts in the
background at startup (`Program.cs` → `CityDatabase.EnsureLoaded()`).

---

## Required external file
`cities500.csv`: no longer requires manual intervention, the app downloads it on
its own on first use if not found (see above). If the download fails
(no network), the "📍 Città principali" (Main cities) button and the "Ricerca
una città" (Search a city) POI search show a warning but everything else works normally.

---

## Build
```bash
dotnet restore
dotnet run
# or
dotnet publish -c Release -r linux-x64 --self-contained true
dotnet publish -c Release -r win-x64  --self-contained true
```
