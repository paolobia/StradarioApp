# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

StradarioApp is a cross-platform (Linux/Windows) C# desktop app that builds
cartographic *stradari* (page-based street atlases) from OpenStreetMap tiles.
The user pans/zooms an interactive OSM map, drops rectangular "pages" covering
areas of interest, and exports a multi-page PDF atlas. Projects are saved as
JSON `.stradario` files. Comments and UI strings are in Italian.

## Commands

```bash
dotnet restore
dotnet run                 # build + launch the Avalonia desktop app
dotnet build               # compile only

# Self-contained publish (bundles the .NET runtime, no install needed on target)
dotnet publish -c Release -r linux-x64 --self-contained true -o ./publish/linux
dotnet publish -c Release -r win-x64   --self-contained true -o ./publish/win

# Single-file framework-dependent publish (smaller, ~10-11 MB; target machine
# needs the .NET 8 Runtime installed) — used for the GitHub Release assets
dotnet publish -c Release -r linux-x64 --self-contained false \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./publish/linux
dotnet publish -c Release -r win-x64   --self-contained false \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./publish/win
```

- Requires the **.NET 8 SDK**.
- There is **no test suite** and no linter configured — verification is by
  running the app.
- `cities500.csv` (GeoNames, ~24 MB, at repo root) must sit next to the
  executable (or in `~`) at runtime; without it, city-name lookups degrade
  silently to empty results (see `CityDatabase.SearchPaths`). Deliberately
  excluded from the GitHub Release zips (size) — noted in the release
  description instead.
- GitHub Releases (`gh release create <tag> <zips> --repo paolobia/StradarioApp`)
  host the single-file builds as downloadable zips; `./publish/` is
  gitignored, so these binaries never go into the git history itself.

## Architecture

The app has no MVVM/binding framework — it's a code-behind Avalonia app where
`MainWindow` owns all state and wires everything together directly.

**Layers:**
- `Program.cs` — Avalonia entry point. Registers `FontResolver` and kicks off
  background `CityDatabase.EnsureLoaded()` *before* the UI starts. Forces the
  Light theme (dark mode makes white-on-gray text unreadable).
- `Models/StradarioModels.cs` — all data types. `StradarioProject` (the
  serialized root: settings + `List<MapPage>` + saved view center/zoom) →
  `StradarioSettings` (page size, orientation, DPI, scale, tile server URL) and
  `MapPage` (id, label, `GeoRect` bounds). `StradarioSettings` also computes the
  physical→geographic conversions (`GetPageWidthKm`, scale denominators).
  `MapScale` covers 16 values (1:1.000 up to 1:1.000.000 — the standard set
  used by printed street/road atlases); **new values must always be appended
  at the end of the enum, never inserted/reordered**, because Newtonsoft
  serializes enums as plain integers and `.stradario` files persist that
  numeric index — reordering would silently change the scale of previously
  saved projects. `StradarioSettings.TileServerApiKey`/`GroqApiKey` are
  `[JsonIgnore]`: they're user/account credentials, not document parameters,
  so they're never written into `.stradario` (which may be shared/versioned)
  — only into `AppPreferencesService`'s global preferences file, reapplied to
  every project via `MainWindow.ApplyGlobalPreferences`.
- `Services/` — stateless/utility logic (see below).
- `UI/` — Avalonia windows and the custom Skia canvas control.

**Key architectural facts worth knowing before editing:**

- **Skia-on-Avalonia rendering is custom.** There is no `SkiaSharp.Views.Avalonia`
  package. `UI/MapCanvas.cs` subclasses Avalonia `Control`, overrides `Render`,
  and reaches the native `SKCanvas` via `ISkiaSharpApiLeaseFeature` inside an
  `ICustomDrawOperation`. It re-posts `InvalidateVisual()` every frame so async
  tiles animate in. All map drawing goes through the `PaintSkia` event →
  `MapRenderer.RenderMap`.

- **Coordinate math lives entirely in `Services/GeoUtils.cs`** and uses the OSM
  Web Mercator "tile units" convention (256 px tiles, `2^zoom`). `GeoToPixel` /
  `PixelToGeo` convert screen↔geo against the current view. `CalcOptimalZoom`
  picks the OSM zoom for a print scale using a **fixed 96 DPI** reference (the
  print DPI does not affect tile zoom). `CalcPageBounds` turns a click center +
  settings into a page's `GeoRect`. Any new geographic calculation belongs here.

- **Two independent tile fetchers.** `MapRenderer` fetches tiles for the
  on-screen map (in-memory `TileCache`, 512-tile cap that clears wholesale,
  8 s per-tile timeout, failures never cached so they retry next frame).
  `PdfGenerator` has its *own* HttpClient and tile logic for high-res export —
  changes to tile handling usually need to be made in both places. Both send a
  `StradarioApp/1.0 (educational use)` User-Agent (required by OSM policy).

- **Fractional zoom always rounds up (`Ceiling`), never down, when picking
  which raster tile level to fetch** (`MapRenderer.RenderMap`). Tiles only
  exist at integer OSM zoom levels; the on-screen zoom moves in 0.5 steps
  (scroll wheel) and can land on a fractional value. Rounding down and
  upscaling the tile bitmap to match looks pixelated/grainy; rounding up and
  downscaling stays sharp. A high-`SKFilterQuality` paint is also used for
  `DrawBitmap` to smooth the (now always ≤1×) residual scale factor. Page/POI
  positioning math is unaffected — `GeoUtils.GeoToPixel` still uses the exact
  fractional `zoom`, only the tile-fetch level changed.

- **PDF layout ordering.** `PdfGenerator.SortPages` groups pages into rows by
  latitude (40% page-height tolerance), rows north→south, columns west→east.
  Page numbering is dynamic: if the project has POI groups, `DrawPoiListPages`
  emits one or more gazetteer pages first (paginated automatically), followed
  by the index page, then the overview page, then the map pages — so map page
  numbers start at `poiPageCount + 3`. Each map page draws N/S/E/O neighbor
  references in its borders, and the scale bar is drawn in the south border
  strip (below the map, not overlaid on it).

- **"Genera PDF" previews before asking where to save.** `OnGeneratePdf` in
  `UI/MainWindow` no longer asks for a destination path upfront: it renders
  straight to a temp file (`Path.GetTempPath()`), opens it in the OS default
  PDF viewer (`Process.Start(..., UseShellExecute = true)`), then shows a
  small "💾 Salva / ✕ Chiudi" dialog (`ShowPdfPreviewDialog`) — Salva prompts
  for a destination and copies the temp file there, Chiudi just discards it.
  The temp file is deleted in both cases once the dialog closes.

- **POI groups.** `Models/StradarioModels.cs` defines `PoiGroup`/`PoiItem`
  (label, description, icon, color), stored in `StradarioProject.PoiGroups`
  and managed inline from the left-panel navigation tree in `UI/MainWindow`
  (`UI/PoiManagerWindow` is a leftover standalone dialog, currently unused —
  don't trust it as the entry point). All icon rendering goes through
  `Services/PoiIconRenderer` (pure SkiaSharp vector shapes, no text/emoji
  glyphs) so the same pin renders identically on the interactive map
  (`MapRenderer.DrawPois`), on the printed page bitmaps
  (`PdfGenerator.DrawPoisOnBitmap`), and as an embedded PNG in exported KMZ
  files. `Services/PoiService` handles KMZ import/export (KML `<Folder>` =
  group, `<Placemark>` = POI) using only the .NET SDK's
  `System.IO.Compression`/`System.Xml.Linq` — no extra NuGet package. Both
  `ImportKmzAsync` methods (`PoiService`/`PercorsoService`) accept a plain
  uncompressed `.kml` as well as a zipped `.kmz`, via the shared
  `Services/KmlIo.LoadDocument` (zip "PK" / gzip `1F 8B` / raw XML
  detection). It parses with `XDocument.Load` over a `Stream` rather than a
  pre-decoded string, so BOM and non-UTF-8 encoding declarations are handled
  by the XML reader itself instead of assuming UTF-8 — the previous
  hand-rolled `Encoding.UTF8.GetString` + `XDocument.Parse(string)` approach
  broke ("Root element is missing"/"Data at the root level is invalid") on
  real-world KML exports with a UTF-8 BOM. A lenient fallback (reparse from
  the first `<` in the text) covers stray preambles that even `Load` rejects.
  New POI can also be dropped directly on the interactive map: the group's
  "➕" icon enters `_addPoiMode` (`_addPoiTargetGroup` holds the target
  group), and the next map click places the POI immediately at the clicked
  lon/lat with no dialog — it gets an auto-generated label `POI<n>`
  (`GetNextPoiLabelNumber` in `MainWindow`, based on the highest `POI<n>`
  label already used in the project, so deleted numbers aren't reused);
  right-click or Escape cancels the mode. Use the group's "✏" edit action
  on the item afterwards (`UI/PoiItemEditWindow`) to rename/describe it.
  Existing POI can be repositioned by dragging their marker directly on the
  map — no prior selection needed, just press-drag-release on the pin
  (hit-tested by pixel radius, `FindPoiAtPoint`/`PoiHitRadiusPx` in
  `MainWindow`).

- **Percorsi (routes).** `Models/StradarioModels.cs` defines `Percorso`
  (label, description, color, ordered `List<GeoPoint>`), stored flat in
  `StradarioProject.Percorsi` (no grouping — each route is its own entity)
  and managed from the same navigation tree as POI groups (branch
  "🥾 Percorsi" in `UI/MainWindow`). Creating one enters a draw mode
  (`_addRouteMode`/`_drawingRoute` in `MainWindow`): left-click appends a
  point, shift+click appends a final point and finishes immediately with no
  dialog — the route is added with an auto-generated label `PATH<n>`
  (`GetNextPercorsoLabelNumber`, same highest-number-used approach as POI
  labels); right-click undoes the last point (or exits the mode if empty),
  Escape cancels outright. Use the route's "✏" edit action afterwards
  (`UI/RouteEditWindow`) to rename/recolor/describe it or edit points
  manually. Existing route vertices can be repositioned by dragging them
  directly on the map, same pattern as POI
  (`FindRoutePointAtPoint`/`RoutePointHitRadiusPx`). Drawing/rendering is
  shared between the interactive map and the
  PDF via `Services/PercorsoRenderer.Draw`, which takes a geo→pixel
  projection delegate so the exact same code draws on both the on-screen
  `SKCanvas` (`MapRenderer`) and the high-res page bitmaps
  (`PdfGenerator.DrawRoutesOnBitmap`) — same pattern as `PoiIconRenderer`.
  The PDF overview page draws routes as vector `XGraphics` lines instead
  (it's compositing over a raster background, like the page rectangles are).
  `Services/PercorsoService` handles KMZ import/export (`<Folder>` +
  `<Placemark><LineString>`, with `<LineStyle><color>` in KML's `aabbggrr`
  order — converted to/from the app's `#RRGGBB`).

- **Import is a single toolbar action, not per-branch.** The nav tree's
  "Gruppi POI"/"Percorsi" branches only expose export (💾); import lives in
  the main toolbar ("📥 Importa KMZ/KML" → `OnImportKmzUnified` in
  `UI/MainWindow`), which reads the picked file's bytes once (via
  `ReadPickedFileBytesAsync` — see below) then runs both
  `PoiService.ImportKmz` and `PercorsoService.ImportKmz` against those same
  bytes and merges whatever each finds — a single file can legitimately
  contain POI Folders/waypoints, route LineStrings/tracks, or both, and each
  parser already ignores the shapes it doesn't understand.

- **Import also accepts GPX**, not just KML/KMZ. Both `PoiService.ImportKmz`
  and `PercorsoService.ImportKmz` branch on `root.Name.LocalName == "gpx"`
  after `KmlIo.LoadDocument` (GPX is XML too, so the same BOM/encoding-safe
  loader applies): `PoiService.ParseGpxWaypoints` reads `<wpt lat lon>` into
  one flat POI group (GPX has no folder concept), and
  `PercorsoService.ParseGpxRoutes` reads each `<trk>` (all its `<trkseg>`
  merged) and each `<rte>` as a separate route. The toolbar file picker
  filter includes `*.gpx` alongside `*.kmz`/`*.kml`.

- **Fallback POI group naming uses the file name.** When a KML has no
  `<Folder><name>` (or the Placemark/`<wpt>` isn't inside any Folder), the
  imported group is named after the source file (`Path.GetFileNameWithoutExtension`,
  threaded through as `fileNameHint` into `PoiService.ImportKmz`) instead of
  a generic "Importati"/"Gruppo N" — only when the file itself doesn't
  provide a real name. Routes without a `<name>` still fall back to
  "Percorso N" (not file-based) — this wasn't part of the same request and
  is inconsistent on purpose until someone asks for it too.

- **`KmlIo.SanitizeName` strips path-like `<name>` values down to their last
  segment.** Seen in practice: a GPS/photo-tracking export that names every
  track `<name>` after its source album folder, e.g.
  `"/mnt/nas/.../2026.03.11"` — quotes and all, as literal text. Applied via
  `PoiService.ResolveName`/`PercorsoService.ResolveLabel` (fallback-aware:
  used for Folder/group names and route labels, where empty-after-sanitizing
  still falls back to the generic name) and directly for POI item/GPX `<wpt>`
  labels (no fallback needed there — empty label is fine). NOT applied to
  `<description>` fields — a path there is plausibly real free-text content,
  not a mislabeled name.

- **`ReadPickedFileBytesAsync` (in `UI/MainWindow`) reads picked files
  defensively.** It tries the Avalonia `IStorageFile` stream first, falls
  back to `TryGetLocalPath()` + `File.ReadAllBytesAsync`, and retries a
  couple of times with a short delay — some Linux file-picker backends
  (xdg-desktop-portal document mounts, network shares) can transiently
  report/return an empty file right after selection. If every attempt still
  comes back empty, it throws with diagnostic detail (name, resolved local
  path, stream length, FileInfo length) rather than silently failing —
  useful to distinguish "the source file is genuinely 0 bytes" (seen in
  practice: a corrupted GPS export) from an actual read-timing bug.

- **Nav tree icon ordering is a fixed convention**: wherever a branch/item
  shows more than one action icon, they always appear in this order (only
  the applicable ones are shown) — importa, esporta, aggiungi, modifica,
  cancella, nascondi (👁), blocca (🔒/🔓). Keep new action icons consistent
  with this order when touching `BuildNavigationTree`/`BuildPoiGroupNavHeader`/
  `BuildPercorsoNavItem`/`BuildPageListItem` etc. in `UI/MainWindow`.

- **Locking (`IsLocked`) prevents accidental map-drag moves, and — unlike
  visibility — is persisted in the project file.** `MapPage`, `PoiGroup`, and
  `Percorso` each carry an `IsLocked` bool (default false). Toggled via the
  🔒/🔓 icon in the nav tree (per-page in `BuildPageListItem`, per-group in
  `BuildPoiGroupNavHeader`, per-route in `BuildPercorsoNavItem`). Enforced at
  the hit-test stage in `MainWindow`: a locked page is skipped by the
  page-drag branch in `OnMapPointerPressed` (falls through to map pan
  instead), and locked groups/routes are skipped entirely by
  `FindPoiAtPoint`/`FindRoutePointAtPoint` so their POI/vertices can't be
  picked up for dragging. Locking does *not* block edit/delete via their
  explicit nav-tree buttons — only accidental drag.

- **Three things auto-lock, on top of the manual 🔒 toggle:**
  1. Anything brought in via `OnImportKmzUnified` (KMZ/KML/GPX import) is
     `IsLocked = true` immediately — imported data is treated as "finalized,
     don't nudge it" until the user explicitly unlocks it.
  2. `OnOpenProject` force-locks every page/group/route right after
     `_projSvc.LoadAsync` — reopening a `.stradario` file assumes you're
     mostly there to view/print it, not rearrange it; saved `IsLocked`
     values from a previous session are overwritten to `true` on load (not
     preserved/toggled), by design.
  3. **Idle auto-lock**: an unlocked page/group/route with no interaction for
     `StradarioSettings.AutoLockSeconds` (default 60, editable in
     `SettingsWindow`, `0` disables it) gets locked automatically. Tracked via
     three session-only `Dictionary<int, DateTime>` last-touch maps in
     `MainWindow` (`_pageLastTouchUtc`/`_poiGroupLastTouchUtc`/
     `_percorsoLastTouchUtc`, cleared on new/open project) updated by
     `TouchPage`/`TouchPoiGroup`/`TouchPercorso` at every create/edit/drag-end/
     manual-unlock call site, and checked every 5s by a `DispatcherTimer`
     (`_autoLockTimer`, started in the constructor,
     `OnAutoLockTimerTick`). An item not yet in its map is treated as
     "touched now" the first time the timer sees it, so pre-existing
     never-touched-this-session items don't insta-lock.

- **Existing routes can be extended graphically, not just drawn from
  scratch.** The route's "➕" nav-tree icon (`StartAddRoutePointsMode`) enters
  `_addRoutePointsMode`: clicks append/prepend points directly to the live
  `Percorso.Points` list (no scratch/preview object — the normal route
  render already reflects it live). Which end gets extended is decided once,
  from the first click of the session, by proximity to the route's current
  first vs. last point (`_addRoutePointsPrepend`); shift+click ends the
  session, right-click undoes the last point *added this session* (falls
  through to canceling the mode once the session-added count reaches zero,
  never eating pre-existing points), Escape cancels outright — same
  interaction language as `_addRouteMode`'s from-scratch drawing.

- **Export offers KMZ/KML/GPX, chosen by the extension picked/typed in the
  save dialog** (`OnExportKmz`/`OnExportPercorsiKmz` in `UI/MainWindow`
  switch on `Path.GetExtension`). `PoiService`/`PercorsoService` each expose
  `ExportKmzAsync` (zipped, POI icons embedded as PNG), `ExportKmlAsync`
  (same KML document written raw, no zip — POI style falls back to
  color-only `IconStyle` since there's no container for the PNGs), and
  `ExportGpxAsync` (`<wpt>` per POI, `<trk>` per route; GPX has no
  folder/group concept so POI grouping is flattened, and no standard color
  field so route colors aren't preserved in GPX).

- Per-item/per-group **visibility toggles** (the 👁 icons in the nav tree) are
  session-only UI state (`_hiddenPoiGroupIds`, `_hiddenPercorsoIds`,
  `_poiVisible`, `_percorsiVisible`, `_pagesVisible` in `MainWindow`) — not
  persisted to the project file (contrast with `IsLocked` above, which is).
  They filter what's passed to
  `MapRenderer.RenderMap` on the interactive map only; PDF export always
  renders everything regardless of these toggles.

- **`FontResolver`** implements `IFontResolver` because PdfSharpCore can't find
  system fonts on Linux; it scans standard font dirs on both OSes. Must be
  registered before any `XFont` is created.

- **Tile servers** are a fixed list in `TileServers.All` (`Models`), selectable
  in Settings; the chosen URL template (`{z}/{x}/{y}`) is stored per-project.

- **PDF map contrast (`StradarioSettings.PdfContrastMode`)** — opt-in,
  PDF-export-only (never applied to the interactive on-screen map), selectable
  in `SettingsWindow` as "Nessuno" / "Contrasta colore" / "Contrasta B/N" /
  "Enfatizza strade".
  Applied by `Services/MapContrastFilter.cs` as an `SKColorFilter` on the
  composited tile bitmap in `PdfGenerator.RenderMapPageAsync`/
  `RenderOverviewAsync`, *before* routes/POI are drawn on top, so vector
  overlays stay crisp. Motivation: OSM Carto's standard style differentiates
  polygons (buildings, landuse, park, primary roads...) mostly by *hue* at
  nearly identical *lightness* (pastel fills cluster around L≈0.70–0.96,
  confirmed by sampling a real urban tile) — a naive grayscale conversion
  collapses them all into the same light grey, unreadable on a B/N printer.
  "Contrasta colore" boosts saturation then applies a linear contrast matrix
  (stays in color). "Contrasta B/N" converts to grayscale by perceptual
  luminance then reshapes it through a sigmoid ("S-curve") LUT
  (`SKColorFilter.CreateTable`) tuned with `pivot=0.78, steepness=6.0` —
  centered in the middle of that 0.70–0.96 pastel band so it's the *fills
  themselves* that get pulled apart from each other, not just separated from
  the already-dark casings/text. A steeper/higher pivot (tried at 0.85/9.0)
  looked correct on paper but actually made buildings collapse toward the
  same dark grey as roads, because it put a narrow cliff *inside* the
  building↔background gap instead of spreading the whole cluster — verified
  by rendering a real Rome-center OSM tile through both curves and comparing
  pixel histograms/luma before trusting the tuning. **Skia's `SKColorMatrix`
  operates on 0..1-normalized components (bias included), not 0..255 like
  Android's `ColorMatrix`** — an initial `t = 128*(1-contrast)` bias
  (0..255-scale thinking) blew every pixel to solid black; the fix is
  `t = 0.5*(1-contrast)`. In "Contrasta B/N" mode, `DrawRoutesOnBitmap`/
  `DrawPoisOnBitmap` (and the overview page's vector route lines) also force
  routes/POI to solid black instead of their stored color — on a desaturated
  map the group colors are no longer distinguishable from each other or from
  the basemap, so black-on-white (they already draw a white halo/shadow
  behind lines and text) is more legible than a mid-grey.

- **`PdfContrastMode.RoadEmphasis`** ("Enfatizza strade") is not a linear
  `SKColorFilter` — it needs a per-pixel HSL decision, so `MapContrastFilter.
  ApplyRoadEmphasis` walks `SKBitmap.Pixels` directly (bulk buffer copy, far
  faster than per-pixel `GetPixel`/`SetPixel`). Classification: pixels with
  saturation < `AchromaticSatMax` (15) are treated as road casings/borders/text
  and left untouched; warm-hued (orange/red/yellow) pixels above
  `RoadMinSaturation` (50) are treated as road fill and saturated+darkened;
  everything else (area fills — buildings, landuse, water, parks) is pushed
  toward white. **Do not add a lightness cutoff to the "achromatic" branch**
  — an earlier version also required lightness < 55 to count as linework,
  which wiped out minor/residential road casings: sampling a real OSM tile
  (zoom 16, Rome) showed genuine casing grays at many lightness levels (29%,
  45%, 60%, 73%, 80%...), while *every* real area fill sampled had
  saturation ≥15% (residential fill ≈17%, park ≈86%, etc.) — so saturation
  alone already separates roads/borders from fills; gating on lightness too
  only ever discards legitimate light-gray road casings. Verified by
  rendering the filter over a downloaded tile before/after the fix.

## Interaction model (MainWindow)

Left click = recenter view; right click = add a page centered on the clicked
point; drag = pan; scroll = zoom; a selected page can be dragged to move it.
Auto-labels (A1, B2…) come from `ProjectService.GenerateAutoLabel`, derived from
each page center's spatial rank in lat (rows) and lon (columns). Existing POI
markers and route vertices can be dragged directly, without prior selection
(checked before the page-drag/pan hit-test in `OnMapPointerPressed`).

Adding a page (`_addPageMode`), drawing a route (`_addRouteMode`), and adding
a POI (`_addPoiMode`) are mutually exclusive: starting one cancels any other
in progress (`CancelAllAddModes` in `MainWindow`). While drawing a route,
left-click and shift+click are repurposed as described above under
"Percorsi"; while in add-POI mode, left-click places the new POI and
right-click/Escape cancels — page add/drag, POI/vertex drag, and map pan all
work normally once no add-mode is active.
