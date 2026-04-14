using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using SkiaSharp;
using StradarioApp.Models;

namespace StradarioApp.Services
{
    public class PdfGenerator
    {
        private static readonly HttpClient _http;
        private const double MmToPoint = 72.0 / 25.4;  // 1 mm = 2.8346 pt
        private const double MarginMm  = 15.0;

        static PdfGenerator()
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "StradarioApp/1.0");
        }

        // ---- Public entry point ----

        public async Task GenerateAsync(
            string outputPath,
            StradarioProject project,
            IProgress<(string message, double fraction)>? progress = null)
        {
            var sortedPages = SortPages(project.Pages);
            var doc = new PdfDocument();
            doc.Info.Title   = project.ProjectName;
            doc.Info.Creator = "StradarioApp";

            // Page 1 — index
            progress?.Report(("Creazione indice…", 0.05));
            DrawIndexPage(doc, sortedPages, project.Settings);

            // Page 2 — overview map
            progress?.Report(("Rendering mappa di overview…", 0.15));
            await RenderOverviewAsync(doc, project, sortedPages, progress);

            // Pages 3..N — individual map pages
            for (int i = 0; i < sortedPages.Count; i++)
            {
                double frac = 0.40 + 0.55 * (i / (double)Math.Max(sortedPages.Count, 1));
                progress?.Report(($"Rendering pagina {i + 1}/{sortedPages.Count}…", frac));
                await RenderSinglePageAsync(doc, sortedPages[i], project.Settings, i + 1, sortedPages.Count);
            }

            progress?.Report(("Salvataggio PDF…", 0.98));
            doc.Save(outputPath);
            progress?.Report(("Completato.", 1.0));
        }

        // ---- Page sorting ----

        public static List<MapPage> SortPages(IList<MapPage> pages)
        {
            if (pages.Count == 0) return new List<MapPage>();

            double avgHeight = pages.Average(p => p.GeoBounds.Height);
            double tolerance = avgHeight * 0.40;

            // Group into rows by latitude proximity
            var rows = new List<List<MapPage>>();
            foreach (var page in pages.OrderByDescending(p => p.GeoBounds.CenterLat))
            {
                var row = rows.FirstOrDefault(r =>
                    Math.Abs(r[0].GeoBounds.CenterLat - page.GeoBounds.CenterLat) <= tolerance);
                if (row != null)
                    row.Add(page);
                else
                    rows.Add(new List<MapPage> { page });
            }

            // Sort rows north→south, within each row west→east
            return rows
                .OrderByDescending(r => r[0].GeoBounds.CenterLat)
                .SelectMany(r => r.OrderBy(p => p.GeoBounds.CenterLon))
                .ToList();
        }

        // ---- Index page ----

        private static void DrawIndexPage(PdfDocument doc, List<MapPage> pages, StradarioSettings settings)
        {
            var (wMm, hMm) = settings.GetPageDimensionsMm();
            var pdfPage    = doc.AddPage();
            pdfPage.Width  = XUnit.FromMillimeter(wMm);
            pdfPage.Height = XUnit.FromMillimeter(hMm);

            using var gfx = XGraphics.FromPdfPage(pdfPage);

            double marginPt  = MarginMm * MmToPoint;
            double pageWPt   = wMm * MmToPoint;
            double pageHPt   = hMm * MmToPoint;
            double contentW  = pageWPt - marginPt * 2;
            double contentH  = pageHPt - marginPt * 2;

            var fontTitle  = new XFont("Helvetica", 16, XFontStyle.Bold);
            var fontHeader = new XFont("Helvetica",  9, XFontStyle.Bold);
            var fontBody   = new XFont("Helvetica",  8, XFontStyle.Regular);

            // Title
            gfx.DrawString("Indice pagine", fontTitle, XBrushes.Black,
                new XRect(marginPt, marginPt, contentW, 24), XStringFormats.CenterLeft);

            double y = marginPt + 28;

            // Column widths (% of content)
            double colLabel  = contentW * 0.10;
            double colCoords = contentW * 0.22;
            double colDesc   = contentW - colLabel - colCoords;

            // Header
            gfx.DrawString("Etichetta", fontHeader, XBrushes.Black,
                new XRect(marginPt, y, colLabel, 12), XStringFormats.CenterLeft);
            gfx.DrawString("Coordinate centro", fontHeader, XBrushes.Black,
                new XRect(marginPt + colLabel, y, colCoords, 12), XStringFormats.CenterLeft);
            gfx.DrawString("Descrizione", fontHeader, XBrushes.Black,
                new XRect(marginPt + colLabel + colCoords, y, colDesc, 12), XStringFormats.CenterLeft);
            y += 14;
            gfx.DrawLine(XPens.Black, marginPt, y, marginPt + contentW, y);
            y += 2;

            for (int i = 0; i < pages.Count; i++)
            {
                var page = pages[i];
                bool hasDesc = !string.IsNullOrWhiteSpace(page.Description);
                double rowH = hasDesc ? 22 : 13;

                if (y + rowH > pageHPt - marginPt) break; // stop if out of space

                // Alternate row background
                if (i % 2 == 0)
                {
                    gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(230, 240, 255)), marginPt, y, contentW, rowH);
                }

                gfx.DrawString(page.Label, fontBody, XBrushes.Black,
                    new XRect(marginPt, y, colLabel, rowH), XStringFormats.CenterLeft);

                string coords = $"Lon: {page.GeoBounds.CenterLon:F4}\nLat: {page.GeoBounds.CenterLat:F4}";
                gfx.DrawString($"Lon: {page.GeoBounds.CenterLon:F4}", fontBody, XBrushes.Black,
                    new XRect(marginPt + colLabel, y, colCoords, rowH / 2), XStringFormats.CenterLeft);
                gfx.DrawString($"Lat: {page.GeoBounds.CenterLat:F4}", fontBody, XBrushes.Black,
                    new XRect(marginPt + colLabel, y + rowH / 2, colCoords, rowH / 2), XStringFormats.CenterLeft);

                if (hasDesc)
                {
                    // Two lines max
                    string desc = page.Description;
                    if (desc.Length > 80) desc = desc[..80] + "…";
                    gfx.DrawString(desc, fontBody, XBrushes.Black,
                        new XRect(marginPt + colLabel + colCoords, y, colDesc, rowH), XStringFormats.TopLeft);
                }

                y += rowH;
            }
        }

        // ---- Overview page ----

        private async Task RenderOverviewAsync(
            PdfDocument doc, StradarioProject project, List<MapPage> sortedPages,
            IProgress<(string, double)>? progress)
        {
            if (sortedPages.Count == 0)
            {
                AddBlankPage(doc, project.Settings, "Nessuna pagina da mostrare");
                return;
            }

            var (wMm, hMm) = project.Settings.GetPageDimensionsMm();
            double marginMm   = MarginMm;
            double pixPerMm   = project.Settings.Dpi / 25.4;
            int    pixW       = (int)((wMm - marginMm * 2) * pixPerMm);
            int    pixH       = (int)((hMm - marginMm * 2) * pixPerMm);

            // Bounding box of all pages + 10% padding
            double minLon = sortedPages.Min(p => p.GeoBounds.MinLon);
            double maxLon = sortedPages.Max(p => p.GeoBounds.MaxLon);
            double minLat = sortedPages.Min(p => p.GeoBounds.MinLat);
            double maxLat = sortedPages.Max(p => p.GeoBounds.MaxLat);
            double padLon = (maxLon - minLon) * 0.10;
            double padLat = (maxLat - minLat) * 0.10;
            minLon -= padLon; maxLon += padLon;
            minLat -= padLat; maxLat += padLat;

            double lonExtent = maxLon - minLon;
            double latExtent = maxLat - minLat;
            double centerLon = (minLon + maxLon) / 2;
            double centerLat = (minLat + maxLat) / 2;
            double cosLat    = Math.Cos(centerLat * Math.PI / 180.0);

            // Zoom formula with latExtent * cosLat (NOT divided)
            double zLon  = Math.Log2(pixW * 360.0 / (256.0 * lonExtent));
            double zLat  = Math.Log2(pixH * 360.0 / (256.0 * latExtent * cosLat));
            int    zoom  = Math.Clamp((int)Math.Floor(Math.Min(zLon, zLat)), 1, 15);

            var tiles = await RenderTilesAsync(
                centerLon, centerLat, zoom, pixW, pixH, project.Settings.TileServerUrl, progress, 0.15, 0.35);

            // Draw on SKBitmap
            using var bmp    = new SKBitmap(pixW, pixH);
            using var canvas = new SKCanvas(bmp);
            DrawTilesToCanvas(canvas, pixW, pixH, centerLon, centerLat, zoom, tiles);

            // Draw page rectangles on overview
            foreach (var page in sortedPages)
            {
                var (x1, y1) = GeoUtils.GeoToPixel(page.GeoBounds.MinLon, page.GeoBounds.MaxLat, centerLon, centerLat, zoom, pixW, pixH);
                var (x2, y2) = GeoUtils.GeoToPixel(page.GeoBounds.MaxLon, page.GeoBounds.MinLat, centerLon, centerLat, zoom, pixW, pixH);

                var rect = new SKRect((float)x1, (float)y1, (float)x2, (float)y2);
                using var fillPaint   = new SKPaint { Color = new SKColor(0, 120, 255, 60), Style = SKPaintStyle.Fill };
                using var strokePaint = new SKPaint { Color = new SKColor(0, 80, 200, 220), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };
                canvas.DrawRect(rect, fillPaint);
                canvas.DrawRect(rect, strokePaint);

                if (!string.IsNullOrEmpty(page.Label))
                {
                    float cx = (float)((x1 + x2) / 2);
                    float cy = (float)((y1 + y2) / 2);
                    using var lp = new SKPaint
                    {
                        Color = SKColors.DarkBlue, TextSize = 10, TextAlign = SKTextAlign.Center, IsAntialias = true,
                        FakeBoldText = true
                    };
                    canvas.DrawText(page.Label, cx, cy + 4, lp);
                }
            }

            AddBitmapPage(doc, bmp, project.Settings, $"Mappa di Overview — {project.ProjectName}", wMm, hMm);
        }

        // ---- Single map page ----

        private async Task RenderSinglePageAsync(
            PdfDocument doc, MapPage page, StradarioSettings settings,
            int pageIndex, int totalPages)
        {
            var (wMm, hMm) = settings.GetPageDimensionsMm();
            double marginMm  = MarginMm;
            double stripMm   = 8.0;   // side strip width
            double innerWMm  = wMm - marginMm * 2 - stripMm * 2;
            double innerHMm  = hMm - marginMm * 2 - stripMm * 2;

            double pixPerMm  = settings.Dpi / 25.4;
            int    pixW      = (int)(innerWMm * pixPerMm);
            int    pixH      = (int)(innerHMm * pixPerMm);

            double centerLon = page.GeoBounds.CenterLon;
            double centerLat = page.GeoBounds.CenterLat;
            int    zoom      = GeoUtils.CalcOptimalZoom(settings, centerLat);

            var tiles = await RenderTilesAsync(
                centerLon, centerLat, zoom, pixW, pixH, settings.TileServerUrl, null, 0, 1);

            using var bmp    = new SKBitmap(pixW, pixH);
            using var canvas = new SKCanvas(bmp);
            DrawTilesToCanvas(canvas, pixW, pixH, centerLon, centerLat, zoom, tiles);

            // Add to PDF
            var pdfPage   = doc.AddPage();
            pdfPage.Width  = XUnit.FromMillimeter(wMm);
            pdfPage.Height = XUnit.FromMillimeter(hMm);
            using var gfx = XGraphics.FromPdfPage(pdfPage);

            double marginPt = marginMm * MmToPoint;
            double stripPt  = stripMm  * MmToPoint;
            double innerWPt = innerWMm * MmToPoint;
            double innerHPt = innerHMm * MmToPoint;

            // Draw map image
            using var ms = new MemoryStream();
            bmp.Encode(ms, SkiaSharp.SKEncodedImageFormat.Png, 100);
            ms.Position = 0;
            var ximg = XImage.FromStream(() => new MemoryStream(ms.ToArray()));
            gfx.DrawImage(ximg, marginPt + stripPt, marginPt + stripPt, innerWPt, innerHPt);

            // Border strips
            DrawPageBorderStrips(gfx, page, wMm, hMm, marginMm, stripMm, pageIndex, totalPages);
        }

        private static void DrawPageBorderStrips(
            XGraphics gfx, MapPage page,
            double wMm, double hMm, double marginMm, double stripMm,
            int pageIndex, int totalPages)
        {
            double mPt   = marginMm * MmToPoint;
            double sPt   = stripMm  * MmToPoint;
            double wPt   = wMm * MmToPoint;
            double hPt   = hMm * MmToPoint;
            double iWPt  = (wMm - marginMm * 2 - stripMm * 2) * MmToPoint;
            double iHPt  = (hMm - marginMm * 2 - stripMm * 2) * MmToPoint;

            var stripBrush  = new XSolidBrush(XColor.FromArgb(220, 235, 255));
            var borderPen   = XPens.SteelBlue;
            var fontStrip   = new XFont("Helvetica", 7, XFontStyle.Regular);

            // Top strip
            gfx.DrawRectangle(stripBrush, mPt + sPt, mPt, iWPt, sPt);
            gfx.DrawString($"N — {page.Label}", fontStrip, XBrushes.DarkBlue,
                new XRect(mPt + sPt, mPt, iWPt, sPt), XStringFormats.Center);

            // Bottom strip
            gfx.DrawRectangle(stripBrush, mPt + sPt, mPt + sPt + iHPt, iWPt, sPt);
            gfx.DrawString($"S — Pag. {pageIndex}/{totalPages}", fontStrip, XBrushes.DarkBlue,
                new XRect(mPt + sPt, mPt + sPt + iHPt, iWPt, sPt), XStringFormats.Center);

            // Left strip (rotated W)
            gfx.Save();
            gfx.TranslateTransform(mPt + sPt / 2, mPt + sPt + iHPt / 2);
            gfx.RotateTransform(-90);
            gfx.DrawRectangle(stripBrush, -iHPt / 2, -sPt / 2, iHPt, sPt);
            gfx.DrawString($"O — {page.GeoBounds.MinLon:F3}", fontStrip, XBrushes.DarkBlue,
                new XRect(-iHPt / 2, -sPt / 2, iHPt, sPt), XStringFormats.Center);
            gfx.Restore();

            // Right strip (rotated E)
            gfx.Save();
            gfx.TranslateTransform(mPt + sPt + iWPt + sPt / 2, mPt + sPt + iHPt / 2);
            gfx.RotateTransform(90);
            gfx.DrawRectangle(stripBrush, -iHPt / 2, -sPt / 2, iHPt, sPt);
            gfx.DrawString($"E — {page.GeoBounds.MaxLon:F3}", fontStrip, XBrushes.DarkBlue,
                new XRect(-iHPt / 2, -sPt / 2, iHPt, sPt), XStringFormats.Center);
            gfx.Restore();

            // Outer margin box
            gfx.DrawRectangle(borderPen, mPt, mPt, wPt - mPt * 2, hPt - mPt * 2);
        }

        // ---- Shared tile download ----

        private static async Task<ConcurrentDictionary<string, SKBitmap>> RenderTilesAsync(
            double centerLon, double centerLat, int zoom, int pixW, int pixH,
            string tileServerUrl,
            IProgress<(string, double)>? progress, double fracStart, double fracEnd)
        {
            var result    = new ConcurrentDictionary<string, SKBitmap>();
            var sem       = new SemaphoreSlim(4, 4);
            var tasks     = new List<Task>();

            double tilePx = 256.0;
            double centerTileX = GeoUtils.LonToTileX(centerLon, zoom);
            double centerTileY = GeoUtils.LatToTileY(centerLat, zoom);

            double offsetX = pixW / 2.0 - centerTileX * tilePx;
            double offsetY = pixH / 2.0 - centerTileY * tilePx;

            int tileCount = (int)Math.Pow(2, zoom);
            int minTX = (int)Math.Floor(-offsetX / tilePx) - 1;
            int maxTX = (int)Math.Ceiling((pixW - offsetX) / tilePx);
            int minTY = (int)Math.Floor(-offsetY / tilePx) - 1;
            int maxTY = (int)Math.Ceiling((pixH - offsetY) / tilePx);

            var needed = new List<(int tx, int ty)>();
            for (int ty = minTY; ty <= maxTY; ty++)
            {
                for (int tx = minTX; tx <= maxTX; tx++)
                {
                    int wtx = ((tx % tileCount) + tileCount) % tileCount;
                    int wty = ty;
                    if (wty < 0 || wty >= tileCount) continue;
                    needed.Add((wtx, wty));
                }
            }

            int total    = needed.Count;
            int done     = 0;

            foreach (var (wtx, wty) in needed)
            {
                int captTx = wtx, captTy = wty;
                var task = Task.Run(async () =>
                {
                    await sem.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        string url = tileServerUrl
                            .Replace("{z}", zoom.ToString())
                            .Replace("{x}", captTx.ToString())
                            .Replace("{y}", captTy.ToString());

                        var bytes = await _http.GetByteArrayAsync(url, cts.Token).ConfigureAwait(false);
                        var bmp = SKBitmap.Decode(bytes);
                        if (bmp != null)
                            result[$"{captTx}/{captTy}"] = bmp;
                    }
                    catch { /* ignore failed tiles */ }
                    finally
                    {
                        sem.Release();
                        int d = Interlocked.Increment(ref done);
                        if (progress != null && total > 0)
                        {
                            double frac = fracStart + (fracEnd - fracStart) * d / total;
                            progress.Report(($"Download tile {d}/{total}…", frac));
                        }
                    }
                });
                tasks.Add(task);
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
            return result;
        }

        private static void DrawTilesToCanvas(
            SKCanvas canvas, int pixW, int pixH,
            double centerLon, double centerLat, int zoom,
            ConcurrentDictionary<string, SKBitmap> tiles)
        {
            canvas.Clear(SKColors.LightGray);
            double tilePx    = 256.0;
            double centerTX  = GeoUtils.LonToTileX(centerLon, zoom);
            double centerTY  = GeoUtils.LatToTileY(centerLat, zoom);
            double offsetX   = pixW / 2.0 - centerTX * tilePx;
            double offsetY   = pixH / 2.0 - centerTY * tilePx;
            int    tileCount = (int)Math.Pow(2, zoom);

            int minTX = (int)Math.Floor(-offsetX / tilePx) - 1;
            int maxTX = (int)Math.Ceiling((pixW - offsetX) / tilePx);
            int minTY = (int)Math.Floor(-offsetY / tilePx) - 1;
            int maxTY = (int)Math.Ceiling((pixH - offsetY) / tilePx);

            for (int ty = minTY; ty <= maxTY; ty++)
            {
                for (int tx = minTX; tx <= maxTX; tx++)
                {
                    int wtx = ((tx % tileCount) + tileCount) % tileCount;
                    int wty = ty;
                    if (wty < 0 || wty >= tileCount) continue;

                    if (tiles.TryGetValue($"{wtx}/{wty}", out var bmp) && bmp != null)
                    {
                        float px = (float)(tx * tilePx + offsetX);
                        float py = (float)(ty * tilePx + offsetY);
                        canvas.DrawBitmap(bmp, new SKRect(px, py, px + (float)tilePx, py + (float)tilePx));
                    }
                }
            }
        }

        // ---- Helpers ----

        private static void AddBlankPage(PdfDocument doc, StradarioSettings settings, string message)
        {
            var (wMm, hMm) = settings.GetPageDimensionsMm();
            var page = doc.AddPage();
            page.Width  = XUnit.FromMillimeter(wMm);
            page.Height = XUnit.FromMillimeter(hMm);
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawString(message, new XFont("Helvetica", 14), XBrushes.Black,
                new XRect(0, 0, page.Width, page.Height), XStringFormats.Center);
        }

        private static void AddBitmapPage(
            PdfDocument doc, SKBitmap bmp, StradarioSettings settings,
            string title, double wMm, double hMm)
        {
            var pdfPage   = doc.AddPage();
            pdfPage.Width  = XUnit.FromMillimeter(wMm);
            pdfPage.Height = XUnit.FromMillimeter(hMm);
            using var gfx = XGraphics.FromPdfPage(pdfPage);

            double marginPt = MarginMm * MmToPoint;
            double contentW = wMm * MmToPoint - marginPt * 2;
            double contentH = hMm * MmToPoint - marginPt * 2;

            using var ms = new MemoryStream();
            bmp.Encode(ms, SkiaSharp.SKEncodedImageFormat.Png, 100);
            ms.Position = 0;
            var ximg = XImage.FromStream(() => new MemoryStream(ms.ToArray()));
            gfx.DrawImage(ximg, marginPt, marginPt, contentW, contentH);

            if (!string.IsNullOrEmpty(title))
            {
                gfx.DrawString(title, new XFont("Helvetica", 9, XFontStyle.Bold), XBrushes.DarkBlue,
                    new XRect(marginPt, 4, contentW, 12), XStringFormats.CenterLeft);
            }
        }
    }
}
