🇮🇹 [Italiano](README.md) | 🇬🇧 **English**

# StradarioApp

C# desktop application to create cartographic *stradari* (page-based street
atlases) from OpenStreetMap. Portable on **Linux** and **Windows** thanks to
Avalonia UI.

---

## Features

1. **Settings** – Three tabs: "General" (page size A5/A4/A3, orientation,
   DPI 72/96/150/300, map scale from 1:1,000 up to 1:1,000,000 — see the
   list below, tile server with or without an API key, auto-lock for
   inactive objects, map contrast in the PDF), "POI categories" (add
   custom search categories, alongside the built-in ones, by specifying a
   label and an OSM tag `key=value`), and "Offline POI database" (optional,
   per-continent download of a local POI database for instant, network-free,
   area-unrestricted search instead of live Overpass)
2. **Interactive map** – Pan (drag), zoom (scroll wheel) centered on the
   cursor
3. **Pages** – Right-click to add a page; drag to move it; automatic
   labels (A1, B2…); lock/unlock to prevent accidental moves; "📍 Main
   cities" button in the page edit dialog to fill the description with the
   most populous cities in the area (GeoNames `cities500.csv` database,
   optional)
4. **POI groups** – Markers with customizable icon/color, added directly
   on the map, drag to reposition, POI search across 43 built-in
   categories (full list of OSM tags in `CategoriePOI.txt`, dropdown menu,
   remembered across sessions, extendable from Settings — see above) with
   a text filter on the name and, optionally (requires a free Groq API
   key), a broader AI filter when the literal filter finds nothing. At the
   top of the menu, two special entries: **"Search an address"** (free
   geocoding via Nominatim) and **"Search a city"** (name, even partial, or
   empty for the cities already visible in the area — GeoNames database).
   Every search shows a step-by-step log window with a "Cancel" button: it
   closes itself on a successful search, but stays open on error (so the
   message doesn't disappear before the user can read it)
5. **Routes** – Point-by-point route drawing directly on the map,
   extendable afterwards, with drag on individual vertices
6. **KMZ/KML/GPX Import/Export** – Unified import (POIs and routes in the
   same file); export either separate (POI groups, routes) or combined
   into a single file ("Export all"). Names/descriptions in a non-Latin
   script (e.g. Chinese characters) are replaced with an ASCII variant when
   available in the tags, or stripped otherwise. Points in China may be in
   GCJ-02 rather than real WGS84 (a deterministic offset used by Chinese
   maps, including Google Maps for locations inside China): for KML/KMZ
   (always WGS84 by specification) nothing is asked, for GPX you can choose
   to correct or leave as-is (auto-detected from the file name if it
   contains "wgs84"/"gcj02"); "C→W"/"W→C" icons in the tree let you manually
   correct/convert any single POI or route point that falls in China, at
   any time
7. **Hybrid POI search** – Live Overpass by default; if at least one
   continent has been downloaded from the offline POI database (see
   Settings), the same category search answers instantly and offline
8. **PDF generation** – Preview before saving, complete stradario with
   index, overview map, optional POI gazetteer pages, map pages with
   references to adjacent pages (N/S/E/W) and a graphic scale bar;
   optional contrast tuned for black-and-white printing
9. **Project saving** – `.stradario` file (JSON), readable and editable by
   hand; API keys (tile server, Groq) are **never** saved in the project,
   only in the application preferences. Every file picker (open/save
   project, import, export, PDF) starts from the last folder actually
   used, not the system's "Recent" list

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
│   ├── PoiSearchService.cs         # POI search by category/address + optional AI/Groq filter
│   ├── GroqClient.cs               # Minimal HTTP client for the Groq API (AI POI filter)
│   ├── KmlIo.cs                    # KML/KMZ/GPX XML loading, robust to BOM/encoding
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
    ├── PoiSearchLogWindow.cs        # Step-by-step log of every POI search, with Cancel
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
