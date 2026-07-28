🇮🇹 [Italiano](README.md) | 🇬🇧 **English**

# StradarioApp

C# desktop application to create cartographic *stradari* (page-based street
atlases) from OpenStreetMap. Portable on Linux and Windows thanks to
Avalonia UI.

---

## Features

- **Interactive map**: pan by dragging, zoom with the scroll wheel centered on the cursor, city search
- **Pages**: right-click to add, drag to move, manual/automatic lock against accidental moves, automatic labels (A1, B2…)
- **POI groups**: markers with customizable icon/color, added directly on the map or via search — 43 built-in categories plus custom ones, live search (Overpass, with an offline local match score always computed alongside the optional AI/Groq filter) or offline (downloadable per-continent local database), address and city search; move a POI between groups with a cut/paste gesture; a hidden group (eye off) is protected from accidental changes
- **Routes**: point-by-point drawing on the map (Enter or shift+click to confirm, panning while drawing no longer adds a stray point), extendable and editable afterwards, routable onto real roads via OSRM (driving/cycling/walking, multiple alternatives selectable directly on the map)
- **Universal import/export**: a single command imports KMZ/KML/GPX (POI and routes together, with distinct colors for imported POI groups); export either separate, combined, or a single group/route; also **CSV** (two tabular files, one for POI and one for routes, that open cleanly in Excel/LibreOffice); names in a non-Latin script are cleaned up automatically; points in China (possibly GCJ-02 instead of WGS84) handled with automatic or manual correction
- **PDF generation**: preview before saving, index, overview map, pages with references to adjacent ones and a graphic scale bar; 5 print contrast modes for the map (none, color, black/white, road emphasis, **adaptive/local**), plus **edge reinforcement** and **B/W print dithering** as independent options
- **Project saving**: `.stradario` file (JSON), human-readable and editable by hand; API keys stay only in local preferences, never in the project
- **Settings**: three tabs — General (page format, DPI, print scale from 1:1,000 to 1:1,000,000, tile server, PDF map contrast), POI categories (add custom categories), Offline POI database (optional per-continent download)

---

## Available scales

1:1,000 · 1:5,000 · 1:10,000 · 1:15,000 · 1:20,000 · 1:25,000 · 1:50,000 ·
1:100,000 · 1:150,000 · 1:200,000 · 1:250,000 · 1:300,000 · 1:400,000 ·
1:500,000 · 1:800,000 · 1:1,000,000

The printed map scale is calculated exactly for the chosen DPI (not an
approximation tied to OSM tile zoom).

---

## NuGet dependencies

| Package                 | Use                                        |
|--------------------------|-------------------------------------------|
| Avalonia                | Cross-platform UI (Windows/Linux/macOS)    |
| Avalonia.Desktop        | Desktop lifecycle                          |
| Avalonia.Themes.Fluent  | Visual theme                                |
| Avalonia.Fonts.Inter    | Inter font                                  |
| Avalonia.Skia           | Custom Skia canvas on Avalonia 11           |
| SkiaSharp               | 2D rendering (map, POI icons, routes)       |
| BruTile                 | OSM tile scheme (TileIndex)                 |
| PdfSharpCore            | PDF generation                              |
| Newtonsoft.Json         | Project serialization                       |

---

## Build and run

### Prerequisites
- [.NET 8 SDK](https://dotnet.microsoft.com/download)
- `cities500.csv` (GeoNames, ~24 MB) for city search: **no need to fetch it
  by hand**, the app downloads it on its own on first run if not found —
  see below

```bash
dotnet restore
dotnet run          # build + launch
dotnet build         # compile only
```

### Publishing

```bash
# Self-contained (bundles the .NET runtime, no dependency to install)
dotnet publish -c Release -r linux-x64 --self-contained true -o ./publish/linux
dotnet publish -c Release -r win-x64   --self-contained true -o ./publish/win

# Single-file framework-dependent executable (lighter, requires the .NET 8
# Runtime installed on the target machine)
dotnet publish -c Release -r linux-x64 --self-contained false \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./publish/linux
dotnet publish -c Release -r win-x64   --self-contained false \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./publish/win
```

### Download a pre-built executable

The [Releases](https://github.com/paolobia/StradarioApp/releases) of the
GitHub repo contain ready-to-use single-file, framework-dependent
executables for Linux and Windows (require the [.NET 8 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
installed). `cities500.csv` isn't included for size reasons, but the app
downloads it on its own on first run (see below) — no need to fetch it
separately.

---

## Project structure

```
StradarioApp/
├── Program.cs                     # Avalonia entry point
├── StradarioApp.csproj
├── CategoriePOI.txt                # List (key=value) of built-in POI categories, for reference
├── Models/
│   └── StradarioModels.cs         # Project, settings, pages, POI, routes, tile servers
├── Services/
│   ├── GeoUtils.cs                 # Geographic conversions, exact zoom/scale
│   ├── MapRenderer.cs              # Interactive map rendering (tiles + POI + routes)
│   ├── PdfGenerator.cs             # PDF generation (index, overview, pages, gazetteer)
│   ├── MapContrastFilter.cs        # Map contrast filters for printing
│   ├── PercorsoRenderer.cs         # Route drawing shared between map/PDF
│   ├── PoiIconRenderer.cs          # Vector POI icons shared between map/PDF/KMZ
│   ├── PoiService.cs / PercorsoService.cs   # KMZ/KML/GPX import/export
│   ├── GcjTransform.cs             # GCJ-02 -> WGS84 correction for imports in China
│   ├── PoiSearchService.cs         # POI search by category/address + optional AI/Groq filter + local match score
│   ├── RouteInstradationService.cs # OSRM routing of a Route onto real roads (driving/cycling/walking)
│   ├── GroqClient.cs               # Minimal HTTP client for the Groq API (AI POI filter)
│   ├── KmlIo.cs                    # KML/KMZ/GPX XML loading, robust to BOM/encoding
│   ├── CsvIo.cs                    # Minimal RFC 4180 CSV read/write for POI/route import/export
│   ├── CityDatabase.cs             # GeoNames city database, automatic download if missing
│   ├── ProjectService.cs           # Save/load .stradario project
│   ├── AppPreferencesService.cs    # Global preferences (API keys, last POI category), not in the project
│   ├── DebugLog.cs                 # Diagnostic file log (Groq calls)
│   ├── FontResolver.cs             # Fonts for PdfSharpCore on Linux
│   └── RecentFilesService.cs       # Recent projects list
└── UI/
    ├── MainWindow.cs                # Main window (all code-behind, no AXAML)
    ├── MapCanvas.cs                 # Custom Avalonia control with a Skia canvas
    ├── SettingsWindow.cs            # Settings dialog
    ├── EditPageWindow.cs            # Page edit dialog
    ├── PoiGroupEditWindow.cs / PoiItemEditWindow.cs
    ├── RouteEditWindow.cs
    ├── RouteInstradationPanel.cs    # Alternatives/distance/duration panel during OSRM routing
    ├── PoiSearchLogWindow.cs        # Step-by-step log of every POI search, with Cancel/OK
    └── ProgressWindow.cs            # PDF generation progress dialog
```

---

## Quick start

1. Launch the app
2. *(Optional)* Click **⚙️ Settings** to choose page size, DPI, scale and
   tile server
3. Navigate the map with **drag** (pan) and **scroll wheel** (zoom)
4. **Right-click** on the map to add a page
5. From the side panel, create POI groups and routes (or import them from
   KMZ/KML/GPX)
6. Click **📄 Generate PDF**: the app shows a preview, then you can save or
   discard it
7. Click **💾 Save** to keep the project as a `.stradario` file

---

## Technical notes

- OSM tiles are downloaded from the chosen tile server and kept in an
  in-memory cache
- The PDF includes: optional POI gazetteer pages, index, overview map, one
  page per defined page
- Pages in the PDF are ordered by rows (north→south), columns (west→east)
- Every PDF page shows references to adjacent pages (N/S/E/W) and a
  graphic scale bar
- The `.stradario` file is human-readable, editable JSON
- API keys (tile server, Groq) are saved only in the application
  preferences, never in the `.stradario` file

---

## City database (cities500.csv)

Used by the "📍 Main cities" button and the "Search a city" POI search.
Looked for in the executable's folder or in `~`; **if not found, the app
downloads it on its own** from [GeoNames](https://download.geonames.org/export/dump/cities500.zip)
on first use and caches it (`%AppData%/StradarioApp` on Windows,
`~/.config/StradarioApp` on Linux) — no need to fetch it by hand, not even
for the Release executables. Requires a network connection on first run;
if the download fails (no network), the features that depend on the
database degrade silently (no crash) until it becomes available.

---

## License

Distributed under the [GNU GPL v3.0](LICENSE) license or later: you can
use, modify and redistribute the code freely, including for commercial
purposes, as long as derivative/distributed versions remain open source
under the same license. Map data is © OpenStreetMap contributors (ODbL),
subject to the separate [OpenStreetMap](https://www.openstreetmap.org/copyright)
terms.
