🇮🇹 [Italiano](StradarioApp_Description.md) | 🇬🇧 **English**

# StradarioApp — Project description

## What it is
A portable Linux/Windows C# (.NET 8) desktop application for creating
cartographic street atlases (*stradari*) in PDF from OpenStreetMap data. The
user draws the quadrants to print on the map; the app generates a PDF with
an index, an overview map, and one page per quadrant.

---

## Main features
- **Interactive map**: pan by dragging, zoom with the scroll wheel centered
  on the cursor, city search
- **Pages**: right-click to add, drag to move, manual/automatic lock against
  accidental moves, automatic labels (A1, B2…)
- **POI groups**: markers with customizable icon/color, added directly on
  the map or via search — 43 built-in categories plus custom ones, live
  search (Overpass) or offline (downloadable per-continent local database),
  address and city search
- **Routes**: point-by-point drawing on the map, extendable and editable
  afterwards
- **Universal import/export**: a single command imports KMZ/KML/GPX (POI
  and routes together); export either separate or combined into a single
  file. Names in a non-Latin script are cleaned up automatically; points in
  China, which may be in GCJ-02 rather than real WGS84 (a quirk also known
  from Google Maps), are handled with automatic or manual correction
  depending on the format
- **PDF generation**: preview before saving, index, overview map, pages
  ordered with references to adjacent pages and a graphic scale bar
- **Project saving**: `.stradario` file (JSON), human-readable and
  editable by hand; API keys stay only in local preferences, never in the
  project file

---

## Available tile servers
OpenStreetMap Standard (default), OSM France, OSM Deutschland, OpenTopoMap,
CartoDB Light, Thunderforest Atlas/Neighbourhood, Stadia Alidade
Smooth/Stamen Toner Lite (the last four require an API key).

---

## Requirements and build
Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download). The city
database (`cities500.csv`, GeoNames) downloads itself on first use if not
present — no manual step needed.

```bash
dotnet restore
dotnet run
# or, for a distributable executable
dotnet publish -c Release -r linux-x64 --self-contained true
dotnet publish -c Release -r win-x64  --self-contained true
```

---

## Settings
Three tabs in "⚙ Settings": **General** (page format, DPI, print scale from
1:1,000 to 1:1,000,000, tile server, PDF map contrast), **POI categories**
(add custom search categories), and **Offline POI database** (optional
per-continent download for network-free POI search).

---

## Learn more
This page is deliberately concise. For the internal architecture,
implementation choices, and technical development notes (file structure,
services, details on rendering/PDF/geographic coordinates...) see
[CLAUDE.md](CLAUDE.md) in the repository, kept up to date with every
substantial code change.
