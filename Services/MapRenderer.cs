// =============================================================================
// Services/MapRenderer.cs
//
// SINOSSI: Servizio di rendering della mappa usando BruTile (tile OSM) e SkiaSharp.
//   - TileCache: cache in memoria dei tile scaricati (solo successi, mai i fallimenti)
//   - MapRenderer: scarica i tile OSM necessari e disegna la mappa su SKCanvas
//   - Timeout per singolo tile: 8 secondi, poi il tile rimane grigio e viene
//     ritentato al prossimo frame (non viene cachato come null)
//   - Disegna i rettangoli delle pagine dello stradario sopra la mappa
//   - ClearCache(): svuota la cache per forzare il refresh di tutti i tile
// =============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BruTile;
using SkiaSharp;
using StradarioApp.Models;

namespace StradarioApp.Services
{
    // Cache in memoria per i tile scaricati con successo.
    // La chiave include il server URL per non mescolare tile di server diversi.
    // I tile falliti NON vengono cachati: verranno ritentati al prossimo frame.
    public class TileCache
    {
        private readonly ConcurrentDictionary<string, SKBitmap> _cache    = new();
        private readonly ConcurrentDictionary<string, byte>     _inFlight = new();
        private const int MaxCacheSize = 512;

        public SKBitmap? GetKey(string key)
        {
            _cache.TryGetValue(key, out var bmp);
            return bmp;
        }

        public void SetKey(string key, SKBitmap bmp)
        {
            if (_cache.Count >= MaxCacheSize)
                _cache.Clear();
            _cache[key] = bmp;
        }

        public bool IsCachedKey(string key)   => _cache.ContainsKey(key);
        public bool IsInFlightKey(string key) => _inFlight.ContainsKey(key);

        public void MarkInFlightKey(string key)   => _inFlight[key] = 0;
        public void UnmarkInFlightKey(string key) => _inFlight.TryRemove(key, out _);

        // Svuota tutto per forzare un reload completo
        public void Clear()
        {
            _cache.Clear();
            _inFlight.Clear();
        }
    }

    public class MapRenderer
    {
        private readonly TileCache  _cache = new();
        private readonly HttpClient _http;

        private const string UserAgent   = "StradarioApp/1.0 (educational use)";
        private const int    TileTimeout = 8; // secondi per singolo tile

        // Colori rettangoli pagine
        private static readonly SKColor PageFillColor      = new(30,  144, 255, 60);
        private static readonly SKColor PageBorderColor    = new(30,  144, 255, 220);
        private static readonly SKColor PageLabelColor     = SKColors.White;
        private static readonly SKColor PageLabelShadow    = new(0, 0, 0, 180);
        private static readonly SKColor SelectedFillColor  = new(255, 165, 0, 80);
        private static readonly SKColor SelectedBorderColor= new(255, 140, 0, 230);

        public MapRenderer()
        {
            _http = new HttpClient();
            _http.DefaultRequestHeaders.Add("User-Agent", UserAgent);
            _http.Timeout = TimeSpan.FromSeconds(TileTimeout + 2);
        }

        // Svuota la cache per forzare il reload di tutti i tile
        public void ClearCache() => _cache.Clear();

        // Scarica un tile OSM. In caso di fallimento NON lo cacha (verrà ritentato).
        // La chiave di cache include l'URL base per non mescolare tile di server diversi.
        private async Task FetchTileAsync(TileIndex idx, string tileServerUrl, Action onLoaded)
        {
            string cacheKey = $"{tileServerUrl}|{idx.Level}/{idx.Col}/{idx.Row}";

            if (_cache.IsCachedKey(cacheKey) || _cache.IsInFlightKey(cacheKey)) return;

            _cache.MarkInFlightKey(cacheKey);

            // Sostituisce {z}/{x}/{y} nel template URL
            string url = tileServerUrl
                .Replace("{z}", idx.Level.ToString())
                .Replace("{x}", idx.Col.ToString())
                .Replace("{y}", idx.Row.ToString());
            try
            {
                using var cts  = new CancellationTokenSource(TimeSpan.FromSeconds(TileTimeout));
                byte[]    data = await _http.GetByteArrayAsync(url, cts.Token);
                var       bmp  = SKBitmap.Decode(data);
                if (bmp != null)
                {
                    _cache.SetKey(cacheKey, bmp);
                    onLoaded();
                }
            }
            catch { /* timeout o errore rete: si ritenta al prossimo frame */ }
            finally { _cache.UnmarkInFlightKey(cacheKey); }
        }

        // Disegna la mappa sul canvas SkiaSharp.
        // I tile mancanti vengono scaricati in background;
        // onTileLoaded viene chiamato all'arrivo di ciascun tile.
        public void RenderMap(
            SKCanvas canvas,
            float canvasWidth, float canvasHeight,
            double centerLon, double centerLat,
            double zoom,
            string tileServerUrl,
            List<MapPage> pages,
            int? selectedPageId,
            Action? onTileLoaded,
            List<PoiGroup>? poiGroups = null,
            List<Percorso>? routes = null,
            Percorso? previewRoute = null,
            (int GroupId, int ItemId)? highlightedPoi = null,
            int? highlightedRouteId = null)
        {
            canvas.Clear(SKColors.LightGray);

            // I tile raster esistono solo a livelli di zoom interi: quando lo zoom
            // corrente ha una parte frazionaria (es. dopo lo zoom con la rotellina,
            // che avanza a step di 0.5), i tile vanno ingranditi/rimpiccioliti di
            // questo fattore per restare alla stessa scala pixel usata da
            // GeoUtils.GeoToPixel (che usa lo zoom frazionario esatto per
            // posizionare pagine e POI). Si arrotonda sempre PER ECCESSO
            // (Ceiling, non Floor) così i tile vengono sempre rimpiccioliti
            // (scaleFactor <= 1) e mai ingranditi: un downscale resta nitido,
            // mentre l'upscale di un tile a bassa risoluzione appare sgranato.
            int    zoomInt     = (int)Math.Ceiling(zoom);
            double scaleFactor = Math.Pow(2.0, zoom - zoomInt);
            double tileSize    = 256.0 * scaleFactor;

            double centerTileX = GeoUtils.LonToTileX(centerLon, zoomInt);
            double centerTileY = GeoUtils.LatToTileY(centerLat, zoomInt);

            int tilesX = (int)Math.Ceiling(canvasWidth  / tileSize) + 2;
            int tilesY = (int)Math.Ceiling(canvasHeight / tileSize) + 2;
            int startTileX = (int)Math.Floor(centerTileX - tilesX / 2.0);
            int startTileY = (int)Math.Floor(centerTileY - tilesY / 2.0);
            int maxTile    = (int)Math.Pow(2, zoomInt);

            using var placeholderPaint = new SKPaint { Color = new SKColor(210, 210, 210) };
            using var gridPaint        = new SKPaint { Color = new SKColor(190, 190, 190), IsStroke = true };
            // Qualità di ricampionamento più alta per il residuo scaling dei
            // tile (lo zoom frazionario li rimpicciolisce sempre leggermente,
            // vedi sopra): senza, il downscale di default può comunque apparire
            // grezzo/aliasato.
            using var tilePaint        = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };

            for (int tx = startTileX; tx <= startTileX + tilesX; tx++)
            {
                for (int ty = startTileY; ty <= startTileY + tilesY; ty++)
                {
                    int wrappedTx = ((tx % maxTile) + maxTile) % maxTile;
                    if (ty < 0 || ty >= maxTile) continue;

                    var    idx      = new TileIndex(wrappedTx, ty, zoomInt);
                    string cacheKey = $"{tileServerUrl}|{idx.Level}/{idx.Col}/{idx.Row}";

                    float px = (float)((tx - centerTileX) * tileSize + canvasWidth  / 2.0);
                    float py = (float)((ty - centerTileY) * tileSize + canvasHeight / 2.0);
                    var destRect = new SKRect(px, py, px + (float)tileSize, py + (float)tileSize);

                    var bmp = _cache.GetKey(cacheKey);
                    if (bmp != null)
                    {
                        canvas.DrawBitmap(bmp, destRect, tilePaint);
                    }
                    else
                    {
                        canvas.DrawRect(destRect, placeholderPaint);
                        canvas.DrawRect(destRect, gridPaint);

                        if (!_cache.IsInFlightKey(cacheKey))
                        {
                            var capturedIdx = idx;
                            var capturedUrl = tileServerUrl;
                            _ = Task.Run(() => FetchTileAsync(capturedIdx, capturedUrl,
                                () => onTileLoaded?.Invoke()));
                        }
                    }
                }
            }

            DrawPages(canvas, canvasWidth, canvasHeight, centerLon, centerLat, zoom, pages, selectedPageId);

            if (routes != null && routes.Count > 0)
                DrawRoutes(canvas, canvasWidth, canvasHeight, centerLon, centerLat, zoom, routes, poiGroups, highlightedRouteId);

            if (poiGroups != null && poiGroups.Count > 0)
                DrawPois(canvas, canvasWidth, canvasHeight, centerLon, centerLat, zoom, poiGroups, highlightedPoi);

            if (previewRoute != null)
            {
                (double x, double y) Project(double lon, double lat) =>
                    GeoUtils.GeoToPixel(lon, lat, centerLon, centerLat, zoom, canvasWidth, canvasHeight);
                var avoid = poiGroups?.SelectMany(g => g.Items).Select(it => (it.Lon, it.Lat)).ToList();
                PercorsoRenderer.Draw(canvas, previewRoute, Project, dashed: true, avoidLabelNear: avoid);
            }
        }

        // Disegna tutti i percorsi visibili sopra la mappa e le pagine
        private void DrawRoutes(
            SKCanvas canvas,
            float canvasWidth, float canvasHeight,
            double centerLon, double centerLat,
            double zoom,
            List<Percorso> routes,
            List<PoiGroup>? poiGroups,
            int? highlightedRouteId = null)
        {
            (double x, double y) Project(double lon, double lat) =>
                GeoUtils.GeoToPixel(lon, lat, centerLon, centerLat, zoom, canvasWidth, canvasHeight);

            // Punti da evitare per l'etichetta del percorso: tutti i POI
            // visibili nella stessa vista (vedi PercorsoRenderer.Draw).
            var avoid = poiGroups?.SelectMany(g => g.Items).Select(it => (it.Lon, it.Lat)).ToList();

            foreach (var route in routes)
            {
                // Percorso appena cliccato nell'albero: spessore raddoppiato
                // per un secondo, per individuarlo subito dopo il centraggio.
                float strokeMultiplier = route.Id == highlightedRouteId ? 2f : 1f;
                PercorsoRenderer.Draw(canvas, route, Project, avoidLabelNear: avoid,
                    strokeWidthMultiplier: strokeMultiplier);
            }
        }

        // Disegna i marker di tutti i gruppi di POI sopra la mappa e le pagine
        private void DrawPois(
            SKCanvas canvas,
            float canvasWidth, float canvasHeight,
            double centerLon, double centerLat,
            double zoom,
            List<PoiGroup> poiGroups,
            (int GroupId, int ItemId)? highlightedPoi = null)
        {
            const float markerSize = 22f;

            foreach (var group in poiGroups)
            {
                var color = PoiIconRenderer.ParseColor(group.ColorHex);
                foreach (var item in group.Items)
                {
                    var (x, y) = GeoUtils.GeoToPixel(item.Lon, item.Lat,
                        centerLon, centerLat, zoom, canvasWidth, canvasHeight);

                    if (x < -markerSize || x > canvasWidth + markerSize ||
                        y < -markerSize || y > canvasHeight + markerSize)
                        continue;

                    // POI appena cliccato nell'albero: icona raddoppiata per
                    // un secondo, per individuarlo subito dopo il centraggio.
                    bool isHighlighted = highlightedPoi is { } h && h.GroupId == group.Id && h.ItemId == item.Id;
                    float size = isHighlighted ? markerSize * 2f : markerSize;

                    PoiIconRenderer.DrawWithLabel(canvas, item.Icon ?? PoiIconType.Pin, color, item.Label,
                        (float)x, (float)y, size);
                }
            }
        }

        // Disegna i rettangoli di tutte le pagine dello stradario
        private void DrawPages(
            SKCanvas canvas,
            float canvasWidth, float canvasHeight,
            double centerLon, double centerLat,
            double zoom,
            List<MapPage> pages,
            int? selectedPageId)
        {
            using var fillPaint      = new SKPaint { Color = PageFillColor,       IsAntialias = true };
            using var borderPaint    = new SKPaint { Color = PageBorderColor,     IsAntialias = true, IsStroke = true, StrokeWidth = 2.0f };
            using var selFill        = new SKPaint { Color = SelectedFillColor,   IsAntialias = true };
            using var selBorder      = new SKPaint { Color = SelectedBorderColor, IsAntialias = true, IsStroke = true, StrokeWidth = 2.5f };
            using var labelPaint     = new SKPaint { Color = PageLabelColor,      IsAntialias = true, TextSize = 14f, FakeBoldText = true };
            using var shadowPaint    = new SKPaint { Color = PageLabelShadow,     IsAntialias = true, TextSize = 14f, FakeBoldText = true };

            foreach (var page in pages)
            {
                bool isSelected = page.Id == selectedPageId;

                var (x1, y1) = GeoUtils.GeoToPixel(page.GeoBounds.MinLon, page.GeoBounds.MaxLat,
                    centerLon, centerLat, zoom, canvasWidth, canvasHeight);
                var (x2, y2) = GeoUtils.GeoToPixel(page.GeoBounds.MaxLon, page.GeoBounds.MinLat,
                    centerLon, centerLat, zoom, canvasWidth, canvasHeight);

                var rect = new SKRect((float)x1, (float)y1, (float)x2, (float)y2);

                canvas.DrawRect(rect, isSelected ? selFill   : fillPaint);
                canvas.DrawRect(rect, isSelected ? selBorder : borderPaint);

                float cx = (rect.Left + rect.Right)  / 2f;
                float cy = (rect.Top  + rect.Bottom) / 2f;
                canvas.DrawCircle(cx, cy, isSelected ? 6f : 4f, isSelected ? selBorder : borderPaint);

                // Se selezionata: mostra icona "sposta" al centro
                if (isSelected)
                {
                    using var movePaint = new SKPaint { Color = SelectedBorderColor, IsAntialias = true, TextSize = 18f };
                    canvas.DrawText("✥", cx - 9, cy + 7, movePaint);
                }

                float labelX = rect.Left + 4;
                float labelY = rect.Top  + 16;
                canvas.DrawText(page.Label, labelX + 1, labelY + 1, shadowPaint);
                canvas.DrawText(page.Label, labelX,     labelY,     labelPaint);
            }
        }
    }
}
