using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using StradarioApp.Models;
using Avalonia.Threading;

namespace StradarioApp.Services
{
    public class TileCache
    {
        private readonly ConcurrentDictionary<string, SKBitmap> _tiles   = new();
        private readonly ConcurrentDictionary<string, byte>     _inFlight = new();

        public bool TryGet(string key, out SKBitmap? bitmap) => _tiles.TryGetValue(key, out bitmap);
        public bool IsInFlight(string key)                   => _inFlight.ContainsKey(key);
        public void MarkInFlight(string key)                 => _inFlight[key] = 1;
        public void CompleteInFlight(string key)             => _inFlight.TryRemove(key, out _);

        /// <summary>Cache only successes — failed tiles are not cached so they get retried.</summary>
        public void Store(string key, SKBitmap bitmap) => _tiles[key] = bitmap;

        public void Clear()
        {
            foreach (var bmp in _tiles.Values) bmp.Dispose();
            _tiles.Clear();
            _inFlight.Clear();
        }
    }

    public class MapRenderer
    {
        private static readonly HttpClient _http;
        private readonly TileCache _cache = new TileCache();

        static MapRenderer()
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "StradarioApp/1.0");
        }

        public void ClearCache() => _cache.Clear();

        public void FetchTileAsync(BruTile.TileIndex idx, string tileServerUrl, Action onLoaded)
        {
            string key = $"{tileServerUrl}|{idx.Level}/{idx.Col}/{idx.Row}";
            if (_cache.TryGet(key, out _) || _cache.IsInFlight(key)) return;

            _cache.MarkInFlight(key);
            Task.Run(async () =>
            {
                try
                {
                    string url = tileServerUrl
                        .Replace("{z}", idx.Level.ToString())
                        .Replace("{x}", idx.Col.ToString())
                        .Replace("{y}", idx.Row.ToString());

                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    var bytes = await _http.GetByteArrayAsync(url, cts.Token).ConfigureAwait(false);
                    var bmp = SKBitmap.Decode(bytes);
                    if (bmp != null)
                    {
                        _cache.Store(key, bmp);
                        Dispatcher.UIThread.Post(onLoaded, DispatcherPriority.Background);
                    }
                    // Do NOT cache failures — they will be retried next frame
                }
                catch
                {
                    // silently ignore; tile will be retried next render
                }
                finally
                {
                    _cache.CompleteInFlight(key);
                }
            });
        }

        /// <summary>
        /// Synchronous render — SKCanvas is not thread-safe.
        /// Draws cached tiles and fires background downloads for missing ones.
        /// </summary>
        public void RenderMap(
            SKCanvas canvas, int w, int h,
            double centerLon, double centerLat, double zoom,
            string tileServerUrl,
            IList<MapPage> pages, int? selectedPageId,
            Action onTileLoaded)
        {
            canvas.Clear(SKColors.LightGray);

            int zoomInt = (int)Math.Floor(zoom);
            zoomInt = Math.Clamp(zoomInt, 1, 19);

            double tileSize = 256.0;
            double scale    = Math.Pow(2.0, zoom - zoomInt); // sub-tile zoom fraction
            double tilePx   = tileSize * scale;              // pixels per tile at this zoom

            double centerTileX = GeoUtils.LonToTileX(centerLon, zoomInt);
            double centerTileY = GeoUtils.LatToTileY(centerLat, zoomInt);

            double offsetX = w / 2.0 - centerTileX * tilePx;
            double offsetY = h / 2.0 - centerTileY * tilePx;

            int tileCountX = (int)Math.Pow(2, zoomInt);
            int tileCountY = tileCountX;

            int minTileX = (int)Math.Floor((0         - offsetX) / tilePx) - 1;
            int maxTileX = (int)Math.Ceiling((w        - offsetX) / tilePx);
            int minTileY = (int)Math.Floor((0         - offsetY) / tilePx) - 1;
            int maxTileY = (int)Math.Ceiling((h        - offsetY) / tilePx);

            for (int ty = minTileY; ty <= maxTileY; ty++)
            {
                for (int tx = minTileX; tx <= maxTileX; tx++)
                {
                    int wtx = ((tx % tileCountX) + tileCountX) % tileCountX;
                    int wty = ty;
                    if (wty < 0 || wty >= tileCountY) continue;

                    var idx = new BruTile.TileIndex(wtx, wty, zoomInt);
                    string key = $"{tileServerUrl}|{zoomInt}/{wtx}/{wty}";

                    float px = (float)(tx * tilePx + offsetX);
                    float py = (float)(ty * tilePx + offsetY);

                    if (_cache.TryGet(key, out var bmp) && bmp != null)
                    {
                        var destRect = new SKRect(px, py, px + (float)tilePx, py + (float)tilePx);
                        canvas.DrawBitmap(bmp, destRect);
                    }
                    else
                    {
                        FetchTileAsync(idx, tileServerUrl, onTileLoaded);
                    }
                }
            }

            DrawPages(canvas, w, h, centerLon, centerLat, zoom, pages, selectedPageId);
        }

        private void DrawPages(
            SKCanvas canvas, int w, int h,
            double centerLon, double centerLat, double zoom,
            IList<MapPage> pages, int? selectedPageId)
        {
            foreach (var page in pages)
            {
                var bounds = page.GeoBounds;

                var (x1, y1) = GeoUtils.GeoToPixel(bounds.MinLon, bounds.MaxLat, centerLon, centerLat, zoom, w, h);
                var (x2, y2) = GeoUtils.GeoToPixel(bounds.MaxLon, bounds.MinLat, centerLon, centerLat, zoom, w, h);

                bool selected = page.Id == selectedPageId;

                var fillColor   = selected ? new SKColor(255, 165, 0, 80) : new SKColor(0, 120, 255, 60);
                var strokeColor = selected ? new SKColor(255, 140, 0, 220) : new SKColor(0, 100, 210, 220);

                using var fillPaint = new SKPaint { Color = fillColor, Style = SKPaintStyle.Fill };
                using var strokePaint = new SKPaint { Color = strokeColor, Style = SKPaintStyle.Stroke, StrokeWidth = selected ? 2f : 1.5f };

                var rect = new SKRect((float)x1, (float)y1, (float)x2, (float)y2);
                canvas.DrawRect(rect, fillPaint);
                canvas.DrawRect(rect, strokePaint);

                if (selected)
                {
                    float cx = (float)((x1 + x2) / 2);
                    float cy = (float)((y1 + y2) / 2);
                    using var iconPaint = new SKPaint
                    {
                        Color       = new SKColor(255, 100, 0, 220),
                        TextSize    = 18,
                        TextAlign   = SKTextAlign.Center,
                        IsAntialias = true
                    };
                    canvas.DrawText("✥", cx, cy + 6, iconPaint);
                }

                // Label with shadow
                string label = page.Label;
                if (!string.IsNullOrEmpty(label))
                {
                    float lx = (float)((x1 + x2) / 2);
                    float ly = (float)(y1 + 16);

                    using var shadowPaint = new SKPaint
                    {
                        Color       = SKColors.Black.WithAlpha(160),
                        TextSize    = 12,
                        TextAlign   = SKTextAlign.Center,
                        IsAntialias = true
                    };
                    using var labelPaint = new SKPaint
                    {
                        Color       = SKColors.White,
                        TextSize    = 12,
                        TextAlign   = SKTextAlign.Center,
                        IsAntialias = true
                    };

                    canvas.DrawText(label, lx + 1, ly + 1, shadowPaint);
                    canvas.DrawText(label, lx,     ly,     labelPaint);
                }
            }
        }
    }
}
