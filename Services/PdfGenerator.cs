// =============================================================================
// Services/PdfGenerator.cs
//
// SINOSSI: Generazione del PDF dello stradario usando PdfSharpCore e SkiaSharp.
//   - Le pagine vengono ordinate da sinistra a destra, dall'alto al basso
//   - Ogni pagina PDF contiene: la mappa del quadrante, il numero di pagina,
//     e i riferimenti alle pagine adiacenti (N, S, E, O) nei bordi
//   - La prima pagina è un indice con griglia delle pagine
//   - Scarica i tile OSM per ciascuna pagina al DPI e scala specificati
// =============================================================================

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
        private readonly HttpClient _http;
        private const string UserAgent = "StradarioApp/1.0 (educational use)";

        // Margine per i bordi con indicazioni adiacenti (mm)
        private const double BorderMarginMm = 15.0;

        public PdfGenerator()
        {
            _http = new HttpClient();
            _http.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        }

        // Genera il PDF dello stradario sul path specificato.
        // progress: callback con (paginaCorrente, totalePageine, messaggio)
        public async Task GenerateAsync(
            StradarioProject project,
            string outputPath,
            Action<int, int, string>? progress = null)
        {
            var settings = project.Settings;
            var pages    = project.Pages;

            if (pages.Count == 0)
                throw new InvalidOperationException("Nessuna pagina definita.");

            // Ordina le pagine da sinistra a destra, dall'alto al basso,
            // rispettando gli allineamenti orizzontali con tolleranza.
            // Algoritmo:
            //   1. Raggruppa le pagine in "righe" orizzontali: due pagine sono
            //      nella stessa riga se i loro centri di latitudine differiscono
            //      meno del 40% dell'altezza media di una pagina (tolleranza generosa
            //      per catturare pagine adiacenti con piccole sovrapposizioni).
            //   2. Ordina le righe per lat decrescente (nord → sud).
            //   3. All'interno di ogni riga ordina per lon crescente (ovest → est).
            var sorted    = SortPages(pages);
            var poiGroups = project.PoiGroups ?? new List<PoiGroup>();
            var percorsi  = project.Percorsi  ?? new List<Percorso>();

            // Calcola dimensioni pagina PDF
            var (pageWidthMm, pageHeightMm) = settings.GetPageDimensionsMm();
            var pdfDoc = new PdfDocument();
            pdfDoc.Info.Title   = project.ProjectName;
            pdfDoc.Info.Creator = "StradarioApp";

            // ---------------------------------------------------------------
            // Pagine iniziali: elenco dei gruppi di POI (se presenti), prima
            // dell'indice. Il numero di pagine è variabile (paginazione
            // automatica), perciò la numerazione delle pagine mappa successive
            // dipende da quante pagine POI sono state generate.
            // ---------------------------------------------------------------
            int poiPageCount = 0;
            if (poiGroups.Count > 0)
            {
                progress?.Invoke(0, sorted.Count + 2, "Generazione elenco punti di interesse...");
                poiPageCount = DrawPoiListPages(pdfDoc, poiGroups, pageWidthMm, pageHeightMm);
            }

            int percorsiPageCount = 0;
            if (percorsi.Count > 0)
            {
                progress?.Invoke(poiPageCount, sorted.Count + 2 + poiPageCount, "Generazione elenco percorsi...");
                percorsiPageCount = DrawPercorsiListPages(pdfDoc, percorsi, pageWidthMm, pageHeightMm);
            }

            // Le pagine mappa partono dopo: pagine POI, pagine percorsi, poi indice, poi overview
            int frontMatterPages = poiPageCount + percorsiPageCount + 2; // +2: indice + overview
            for (int i = 0; i < sorted.Count; i++)
                sorted[i].PageNumber = i + 1 + frontMatterPages;

            int totalSteps = sorted.Count + 2 + poiPageCount + percorsiPageCount;

            // ---------------------------------------------------------------
            // Pagina indice testuale
            // ---------------------------------------------------------------
            progress?.Invoke(poiPageCount + percorsiPageCount, totalSteps, "Generazione indice...");
            var indexPage = pdfDoc.AddPage();
            SetPageSize(indexPage, pageWidthMm, pageHeightMm);
            DrawIndexPage(indexPage, sorted, project.ProjectName, settings);

            // ---------------------------------------------------------------
            // Pagina mappa riassuntiva con tutti i rettangoli
            // ---------------------------------------------------------------
            progress?.Invoke(poiPageCount + percorsiPageCount + 1, totalSteps, "Generazione mappa riassuntiva...");
            var overviewPage = pdfDoc.AddPage();
            SetPageSize(overviewPage, pageWidthMm, pageHeightMm);
            var overviewBitmap = await RenderOverviewAsync(sorted, settings);
            DrawOverviewPage(overviewPage, overviewBitmap, sorted, percorsi, project.ProjectName, settings);
            overviewBitmap?.Dispose();

            // ---------------------------------------------------------------
            // Pagine successive: una per ogni quadrante
            // ---------------------------------------------------------------
            for (int i = 0; i < sorted.Count; i++)
            {
                var mapPage = sorted[i];
                progress?.Invoke(poiPageCount + percorsiPageCount + 2 + i, totalSteps, $"Pagina {mapPage.PageNumber}: {mapPage.Label}...");

                var pdfPage = pdfDoc.AddPage();
                SetPageSize(pdfPage, pageWidthMm, pageHeightMm);

                var adjacent  = FindAdjacentPages(mapPage, sorted);
                var mapBitmap = await RenderMapPageAsync(mapPage, settings, poiGroups, percorsi);
                DrawMapPage(pdfPage, mapBitmap, mapPage, adjacent, settings);
                mapBitmap?.Dispose();
            }

            pdfDoc.Save(outputPath);
        }

        // Imposta le dimensioni della pagina PDF in millimetri
        private void SetPageSize(PdfPage page, double widthMm, double heightMm)
        {
            page.Width  = XUnit.FromMillimeter(widthMm);
            page.Height = XUnit.FromMillimeter(heightMm);
        }

        // Ordina le pagine rispettando gli allineamenti orizzontali con tolleranza.
        // Raggruppa in "righe" le pagine i cui centri lat differiscono meno del
        // 40% dell'altezza geografica media — cattura pagine affiancate anche se
        // non perfettamente allineate. Poi ordina righe nord→sud, pagine ovest→est.
        private static List<MapPage> SortPages(List<MapPage> pages)
        {
            if (pages.Count == 0) return new List<MapPage>();

            // Altezza geografica media delle pagine (in gradi lat)
            double avgHeight = pages.Average(p => p.GeoBounds.Height);
            double tolerance = avgHeight * 0.40;

            // Raggruppa in righe: ogni riga contiene pagine con lat simile
            var rows      = new List<List<MapPage>>();
            var remaining = pages.OrderByDescending(p => p.GeoBounds.CenterLat).ToList();

            while (remaining.Count > 0)
            {
                var first  = remaining[0];
                var row    = remaining
                    .Where(p => Math.Abs(p.GeoBounds.CenterLat - first.GeoBounds.CenterLat) <= tolerance)
                    .OrderBy(p => p.GeoBounds.CenterLon)
                    .ToList();
                rows.Add(row);
                foreach (var p in row) remaining.Remove(p);
            }

            // Ordina le righe per lat del primo elemento (nord→sud)
            rows.Sort((a, b) => b[0].GeoBounds.CenterLat.CompareTo(a[0].GeoBounds.CenterLat));

            return rows.SelectMany(r => r).ToList();
        }

        // Disegna la pagina indice.
        // Colonne: Etichetta (stretta) | Centro (stretta) | Descrizione (ampia, 2 righe)
        // Senza colonna N° come da richiesta.
        private void DrawIndexPage(PdfPage page, List<MapPage> sortedPages,
            string title, StradarioSettings settings)
        {
            using var gfx = XGraphics.FromPdfPage(page);
            double w = page.Width.Point;
            double h = page.Height.Point;

            gfx.DrawRectangle(XBrushes.White, 0, 0, w, h);

            // Titolo
            var titleFont = new XFont("Arial", 16, XFontStyle.Bold);
            gfx.DrawString(title, titleFont, XBrushes.Black,
                new XRect(0, 18, w, 36), XStringFormats.TopCenter);

            // Sottotitolo
            var subFont   = new XFont("Arial", 9);
            string sub    = $"Scala {settings.GetScaleLabel()}  |  {settings.PageSize} {settings.Orientation}  |  {settings.Dpi} DPI";
            gfx.DrawString(sub, subFont, XBrushes.DarkGray,
                new XRect(0, 46, w, 16), XStringFormats.TopCenter);

            var cellFont  = new XFont("Arial", 8);
            var boldFont  = new XFont("Arial", 8, XFontStyle.Bold);

            double margin     = 24;
            double tableTop   = 72;
            double tableWidth = w - 2 * margin;

            // Larghezze colonne: Etichetta 10%, Centro 22%, Descrizione resto
            double colLabel  = tableWidth * 0.10;
            double colCenter = tableWidth * 0.22;
            double colDesc   = tableWidth - colLabel - colCenter;

            double xLabel  = margin;
            double xCenter = margin + colLabel;
            double xDesc   = margin + colLabel + colCenter;

            double rowH1 = 13; // riga singola (intestazione)
            double rowH2 = 22; // riga doppia (descrizione su 2 righe)

            // Intestazione
            gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(220, 230, 245)), margin, tableTop, tableWidth, rowH1);
            gfx.DrawString("Etichetta", boldFont, XBrushes.Black,
                new XRect(xLabel + 2,  tableTop + 2, colLabel  - 4, rowH1), XStringFormats.TopLeft);
            gfx.DrawString("Centro",   boldFont, XBrushes.Black,
                new XRect(xCenter + 2, tableTop + 2, colCenter - 4, rowH1), XStringFormats.TopLeft);
            gfx.DrawString("Descrizione", boldFont, XBrushes.Black,
                new XRect(xDesc + 2,   tableTop + 2, colDesc   - 4, rowH1), XStringFormats.TopLeft);
            gfx.DrawLine(XPens.DarkGray, margin, tableTop + rowH1, margin + tableWidth, tableTop + rowH1);

            double y = tableTop + rowH1;

            for (int i = 0; i < sortedPages.Count; i++)
            {
                var p = sortedPages[i];

                // Riga ha sempre altezza doppia: la colonna Centro disegna
                // lon/lat su due righe corte indipendentemente dalla presenza
                // di una descrizione, quindi rowH1 non basterebbe mai a contenerle.
                bool hasDesc  = !string.IsNullOrWhiteSpace(p.Description);
                double rh     = rowH2;

                // Controlla se entra nella pagina
                if (y + rh > h - margin) break;

                // Sfondo alternato
                if (i % 2 == 0)
                    gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(248, 248, 252)),
                        margin, y, tableWidth, rh);

                // Etichetta (centrata verticalmente)
                gfx.DrawString(p.Label, cellFont, XBrushes.Black,
                    new XRect(xLabel + 2, y + 2, colLabel - 4, rh - 4), XStringFormats.TopLeft);

                // Centro: lon/lat su due righe corte
                gfx.DrawString($"{p.GeoBounds.CenterLon:F3}°", cellFont, XBrushes.DimGray,
                    new XRect(xCenter + 2, y + 1,     colCenter - 4, 10), XStringFormats.TopLeft);
                gfx.DrawString($"{p.GeoBounds.CenterLat:F3}°", cellFont, XBrushes.DimGray,
                    new XRect(xCenter + 2, y + 1 + 11, colCenter - 4, 10), XStringFormats.TopLeft);

                // Descrizione: fino a 2 righe
                if (hasDesc)
                {
                    // Riga 1: primi ~60 caratteri
                    string desc  = p.Description;
                    string line1 = desc.Length <= 60 ? desc : desc[..60];
                    string line2 = desc.Length > 60  ? desc[60..Math.Min(desc.Length, 120)] : "";
                    gfx.DrawString(line1, cellFont, XBrushes.Black,
                        new XRect(xDesc + 2, y + 1,      colDesc - 4, 10), XStringFormats.TopLeft);
                    if (line2.Length > 0)
                        gfx.DrawString(line2, cellFont, XBrushes.Black,
                            new XRect(xDesc + 2, y + 1 + 11, colDesc - 4, 10), XStringFormats.TopLeft);
                }

                // Separatore riga
                gfx.DrawLine(new XPen(XColor.FromArgb(220, 220, 220)),
                    margin, y + rh, margin + tableWidth, y + rh);

                y += rh;
            }

            // Bordo tabella
            gfx.DrawRectangle(XPens.Gray, margin, tableTop, tableWidth, y - tableTop);

            // Separatori colonne verticali
            var colPen = new XPen(XColor.FromArgb(180, 180, 180));
            gfx.DrawLine(colPen, xCenter, tableTop, xCenter, y);
            gfx.DrawLine(colPen, xDesc,   tableTop, xDesc,   y);
        }

        // Disegna l'elenco dei gruppi di POI (icona + nome + descrizione, poi
        // una riga per ogni POI con icona, etichetta e coordinate), in testa
        // al documento prima dell'indice. Crea automaticamente tutte le
        // pagine necessarie (nessun troncamento). Ritorna il numero di
        // pagine create.
        private int DrawPoiListPages(PdfDocument pdfDoc, List<PoiGroup> poiGroups,
            double pageWidthMm, double pageHeightMm)
        {
            const double margin      = 24;
            const double groupRowH   = 20;
            const double descRowH    = 12;
            const double itemRowH    = 16;
            const double iconSize    = 14;

            var titleFont = new XFont("Arial", 16, XFontStyle.Bold);
            var groupFont = new XFont("Arial", 11, XFontStyle.Bold);
            var descFont  = new XFont("Arial", 8, XFontStyle.Italic);
            var itemFont  = new XFont("Arial", 9);
            var coordFont = new XFont("Arial", 8);

            var iconCache = new Dictionary<int, XImage>();
            XImage GetIcon(PoiGroup g)
            {
                if (iconCache.TryGetValue(g.Id, out var cached)) return cached;
                using var bmp = PoiIconRenderer.RenderToBitmap(g.Icon, PoiIconRenderer.ParseColor(g.ColorHex), 48);
                using var ms  = new MemoryStream();
                bmp.Encode(ms, SKEncodedImageFormat.Png, 100);
                ms.Position = 0;
                var xImg = XImage.FromStream(() => new MemoryStream(ms.ToArray()));
                iconCache[g.Id] = xImg;
                return xImg;
            }

            int pageCount = 0;
            XGraphics? gfx = null;
            double w = 0, h = 0, tableWidth = 0, y = 0;

            void NewPage()
            {
                var page = pdfDoc.AddPage();
                SetPageSize(page, pageWidthMm, pageHeightMm);
                gfx = XGraphics.FromPdfPage(page);
                w   = page.Width.Point;
                h   = page.Height.Point;
                gfx.DrawRectangle(XBrushes.White, 0, 0, w, h);
                tableWidth = w - 2 * margin;
                pageCount++;

                string title = pageCount == 1 ? "Punti di interesse" : "Punti di interesse (segue)";
                gfx.DrawString(title, titleFont, XBrushes.Black,
                    new XRect(0, 18, w, 26), XStringFormats.TopCenter);

                y = 18 + 40;
            }

            NewPage();

            foreach (var group in poiGroups)
            {
                if (y + groupRowH + itemRowH > h - margin)
                    NewPage();

                gfx!.DrawRectangle(new XSolidBrush(XColor.FromArgb(230, 238, 250)), margin, y, tableWidth, groupRowH);
                gfx.DrawImage(GetIcon(group), margin + 3, y + 2, iconSize, iconSize);
                gfx.DrawString(group.Name, groupFont, XBrushes.Black,
                    new XRect(margin + iconSize + 8, y, tableWidth - iconSize - 16, groupRowH), XStringFormats.CenterLeft);
                y += groupRowH;

                if (!string.IsNullOrWhiteSpace(group.Description))
                {
                    if (y + descRowH > h - margin) NewPage();
                    gfx!.DrawString(group.Description, descFont, XBrushes.DimGray,
                        new XRect(margin + iconSize + 8, y, tableWidth - iconSize - 16, descRowH), XStringFormats.TopLeft);
                    y += descRowH;
                }

                foreach (var item in group.Items)
                {
                    if (y + itemRowH > h - margin)
                        NewPage();

                    double iconY = y + (itemRowH - iconSize * 0.85) / 2.0;
                    gfx!.DrawImage(GetIcon(group), margin + 10, iconY, iconSize * 0.85, iconSize * 0.85);
                    gfx.DrawString(item.Label, itemFont, XBrushes.Black,
                        new XRect(margin + iconSize + 14, y, tableWidth * 0.55, itemRowH), XStringFormats.CenterLeft);
                    string coords = $"{item.Lon:F4}°E  {item.Lat:F4}°N";
                    gfx.DrawString(coords, coordFont, XBrushes.DimGray,
                        new XRect(margin + tableWidth * 0.62, y, tableWidth * 0.38 - 4, itemRowH), XStringFormats.CenterLeft);
                    y += itemRowH;
                }

                y += 6; // spazio tra gruppi
            }

            return pageCount;
        }

        // Disegna l'elenco dei percorsi (swatch colore + nome + descrizione,
        // lunghezza e numero di punti), in testa al documento, dopo l'elenco
        // POI e prima dell'indice. Crea automaticamente tutte le pagine
        // necessarie (nessun troncamento). Ritorna il numero di pagine create.
        private int DrawPercorsiListPages(PdfDocument pdfDoc, List<Percorso> percorsi,
            double pageWidthMm, double pageHeightMm)
        {
            const double margin    = 24;
            const double rowH      = 20;
            const double descRowH  = 12;
            const double swatchSz  = 12;

            var titleFont = new XFont("Arial", 16, XFontStyle.Bold);
            var nameFont  = new XFont("Arial", 10, XFontStyle.Bold);
            var descFont  = new XFont("Arial", 8, XFontStyle.Italic);
            var metaFont  = new XFont("Arial", 8);

            int pageCount = 0;
            XGraphics? gfx = null;
            double w = 0, h = 0, tableWidth = 0, y = 0;

            void NewPage()
            {
                var page = pdfDoc.AddPage();
                SetPageSize(page, pageWidthMm, pageHeightMm);
                gfx = XGraphics.FromPdfPage(page);
                w   = page.Width.Point;
                h   = page.Height.Point;
                gfx.DrawRectangle(XBrushes.White, 0, 0, w, h);
                tableWidth = w - 2 * margin;
                pageCount++;

                string title = pageCount == 1 ? "Percorsi" : "Percorsi (segue)";
                gfx.DrawString(title, titleFont, XBrushes.Black,
                    new XRect(0, 18, w, 26), XStringFormats.TopCenter);

                y = 18 + 40;
            }

            NewPage();

            foreach (var r in percorsi)
            {
                bool hasDesc = !string.IsNullOrWhiteSpace(r.Description);
                double neededH = rowH + (hasDesc ? descRowH : 0);
                if (y + neededH > h - margin)
                    NewPage();

                gfx!.DrawRectangle(new XSolidBrush(XColor.FromArgb(230, 238, 250)), margin, y, tableWidth, rowH);

                var color = PercorsoRenderer.ParseColor(r.ColorHex);
                var swatchBrush = new XSolidBrush(XColor.FromArgb(color.Alpha, color.Red, color.Green, color.Blue));
                double swatchY = y + (rowH - swatchSz) / 2.0;
                gfx.DrawRectangle(swatchBrush, margin + 6, swatchY, swatchSz, swatchSz);
                gfx.DrawRectangle(XPens.DimGray, margin + 6, swatchY, swatchSz, swatchSz);

                gfx.DrawString(r.Label, nameFont, XBrushes.Black,
                    new XRect(margin + swatchSz + 14, y, tableWidth * 0.55 - swatchSz - 14, rowH), XStringFormats.CenterLeft);

                double lengthKm = PercorsoRenderer.LengthKm(r);
                string meta = $"{lengthKm:0.##} km  ·  {r.Points.Count} punti";
                gfx.DrawString(meta, metaFont, XBrushes.DimGray,
                    new XRect(margin + tableWidth * 0.62, y, tableWidth * 0.38 - 6, rowH), XStringFormats.CenterLeft);

                y += rowH;

                if (hasDesc)
                {
                    gfx.DrawString(r.Description, descFont, XBrushes.DimGray,
                        new XRect(margin + swatchSz + 14, y, tableWidth - swatchSz - 18, descRowH), XStringFormats.TopLeft);
                    y += descRowH;
                }

                y += 4; // spazio tra percorsi
            }

            return pageCount;
        }

        // Struttura per le pagine adiacenti
        private record AdjacentPages(
            MapPage? North, MapPage? South, MapPage? East, MapPage? West);

        // Trova le pagine adiacenti (che si sovrappongono sui bordi)
        private AdjacentPages FindAdjacentPages(MapPage page, List<MapPage> all)
        {
            MapPage? north = null, south = null, east = null, west = null;

            foreach (var other in all)
            {
                if (other.Id == page.Id) continue;

                // Controlla sovrapposizione in longitudine (stessa fascia orizzontale)
                bool overlapLon = other.GeoBounds.MinLon < page.GeoBounds.MaxLon &&
                                  other.GeoBounds.MaxLon > page.GeoBounds.MinLon;
                // Controlla sovrapposizione in latitudine
                bool overlapLat = other.GeoBounds.MinLat < page.GeoBounds.MaxLat &&
                                  other.GeoBounds.MaxLat > page.GeoBounds.MinLat;

                if (overlapLon)
                {
                    // Pagina a nord: il suo minLat è vicino al mio maxLat
                    if (other.GeoBounds.MinLat >= page.GeoBounds.MaxLat - 0.01 && north == null)
                        north = other;
                    // Pagina a sud
                    if (other.GeoBounds.MaxLat <= page.GeoBounds.MinLat + 0.01 && south == null)
                        south = other;
                }
                if (overlapLat)
                {
                    // Pagina a est
                    if (other.GeoBounds.MinLon >= page.GeoBounds.MaxLon - 0.001 && east == null)
                        east = other;
                    // Pagina a ovest
                    if (other.GeoBounds.MaxLon <= page.GeoBounds.MinLon + 0.001 && west == null)
                        west = other;
                }
            }

            return new AdjacentPages(north, south, east, west);
        }

        // ---------------------------------------------------------------
        // MAPPA RIASSUNTIVA
        // ---------------------------------------------------------------

        // Calcola il GeoRect che racchiude tutte le pagine con un margine del 10%
        private static GeoRect CalcOverallBounds(List<MapPage> pages)
        {
            double minLon = pages.Min(p => p.GeoBounds.MinLon);
            double maxLon = pages.Max(p => p.GeoBounds.MaxLon);
            double minLat = pages.Min(p => p.GeoBounds.MinLat);
            double maxLat = pages.Max(p => p.GeoBounds.MaxLat);

            // Padding 10% su ogni lato
            double padLon = (maxLon - minLon) * 0.10;
            double padLat = (maxLat - minLat) * 0.10;

            return new GeoRect
            {
                MinLon = minLon - padLon,
                MaxLon = maxLon + padLon,
                MinLat = minLat - padLat,
                MaxLat = maxLat + padLat
            };
        }

        // Scarica i tile per la mappa riassuntiva al livello di zoom corretto
        // per far vedere l'intero insieme delle pagine in una sola immagine.
        private async Task<SKBitmap?> RenderOverviewAsync(List<MapPage> pages, StradarioSettings settings)
        {
            var (pageWidthMm, pageHeightMm) = settings.GetPageDimensionsMm();
            double mapWidthMm  = pageWidthMm  - 2 * BorderMarginMm;
            double mapHeightMm = pageHeightMm - 2 * BorderMarginMm;

            int pixW = (int)(mapWidthMm  / 25.4 * settings.Dpi);
            int pixH = (int)(mapHeightMm / 25.4 * settings.Dpi);

            var    bounds    = CalcOverallBounds(pages);
            double centerLon = bounds.CenterLon;
            double centerLat = bounds.CenterLat;
            double cosLat    = Math.Cos(centerLat * Math.PI / 180.0);

            // Estensione in gradi che deve entrare nel canvas
            double lonExtent = bounds.Width;
            double latExtent = bounds.Height;

            // Quanti pixel occupa un grado di longitudine: pixPerDegLon = 256 * 2^z / 360
            // Quanti pixel occupa un grado di latitudine (Mercatore):
            //   pixPerDegLat ≈ pixPerDegLon * cos(lat)  →  gradi lat "pesano" meno pixel
            // Quindi per coprire latExtent gradi in pixH pixel:
            //   pixH = latExtent * pixPerDegLon * cosLat  →  zLat = log2(pixH*360/(256*latExtent*cosLat))
            double zLon = Math.Log2(pixW * 360.0 / (256.0 * lonExtent));
            double zLat = Math.Log2(pixH * 360.0 / (256.0 * latExtent * cosLat));

            int zoom = Math.Clamp((int)Math.Floor(Math.Min(zLon, zLat)), 1, 15);

            var bitmap = await RenderTilesAsync(centerLon, centerLat, zoom, pixW, pixH,
                settings.GetEffectiveTileServerUrl());

            bitmap = ApplyContrastPipeline(bitmap, settings);

            return bitmap;
        }

        // Applica in sequenza le tre trasformazioni raster opzionali
        // (PdfContrastMode, rinforzo contorni, retinatura B/N) nello stesso
        // ordine verificato su tile reali in Services/MapContrastFilter.cs:
        // modalità di contrasto → contorni → retinatura (solo se la
        // modalità è BlackWhite). Condiviso dai due punti che compongono il
        // raster di sfondo (pagina mappa e overview), così restano sempre
        // sincronizzati.
        private static SKBitmap? ApplyContrastPipeline(SKBitmap? bitmap, StradarioSettings settings)
        {
            if (bitmap == null) return null;

            if (settings.PdfContrastMode != PdfContrastMode.None)
                bitmap = MapContrastFilter.Apply(bitmap, settings.PdfContrastMode);

            if (settings.PdfEdgeReinforcement)
                bitmap = MapContrastFilter.ApplyEdgeReinforcement(bitmap);

            if (settings.PdfContrastMode == PdfContrastMode.BlackWhite && settings.PdfDitherBlackWhite)
                bitmap = MapContrastFilter.ApplyFloydSteinbergDither(bitmap);

            return bitmap;
        }

        // Disegna la pagina PDF riassuntiva: mappa + rettangoli + etichette.
        // La conversione geo→pixel PDF usa la stessa proiezione WebMercator
        // usata da RenderTilesAsync, così i rettangoli si sovrappongono correttamente.
        private void DrawOverviewPage(PdfPage pdfPage, SKBitmap? mapBitmap,
            List<MapPage> pages, List<Percorso> percorsi, string projectName, StradarioSettings settings)
        {
            using var gfx = XGraphics.FromPdfPage(pdfPage);
            double w = pdfPage.Width.Point;
            double h = pdfPage.Height.Point;

            double marginPt = BorderMarginMm * 2.8346;
            double mapX = marginPt;
            double mapY = marginPt + 28; // spazio per il titolo
            double mapW = w - 2 * marginPt;
            double mapH = h - 2 * marginPt - 28;

            // Titolo
            var titleFont = new XFont("Arial", 13, XFontStyle.Bold);
            gfx.DrawString($"{projectName}  –  Mappa riassuntiva",
                titleFont, XBrushes.Black,
                new XRect(mapX, marginPt, mapW, 24), XStringFormats.CenterLeft);

            // Mappa di sfondo
            if (mapBitmap != null)
            {
                using var ms = new MemoryStream();
                mapBitmap.Encode(ms, SKEncodedImageFormat.Png, 90);
                ms.Position = 0;
                var xImg = XImage.FromStream(() => new MemoryStream(ms.ToArray()));
                gfx.DrawImage(xImg, mapX, mapY, mapW, mapH);
            }
            else
            {
                gfx.DrawRectangle(XBrushes.LightGray, mapX, mapY, mapW, mapH);
            }
            gfx.DrawRectangle(XPens.Black, mapX, mapY, mapW, mapH);

            // Ricalcola zoom e centro identici a RenderOverviewAsync
            var (pageWidthMm, pageHeightMm) = settings.GetPageDimensionsMm();
            double mapWidthMm  = pageWidthMm  - 2 * BorderMarginMm;
            double mapHeightMm = pageHeightMm - 2 * BorderMarginMm;
            int pixW = (int)(mapWidthMm  / 25.4 * settings.Dpi);
            int pixH = (int)(mapHeightMm / 25.4 * settings.Dpi);

            var    bounds    = CalcOverallBounds(pages);
            double centerLon = bounds.CenterLon;
            double centerLat = bounds.CenterLat;
            double cosLat    = Math.Cos(centerLat * Math.PI / 180.0);

            double zLon = Math.Log2(pixW * 360.0 / (256.0 * bounds.Width));
            double zLat = Math.Log2(pixH * 360.0 / (256.0 * bounds.Height * cosLat));
            int zoom = Math.Clamp((int)Math.Floor(Math.Min(zLon, zLat)), 1, 15);

            // Conversione geo → pixel canvas (stesso sistema di RenderTilesAsync)
            // poi scala da pixel canvas a punti PDF
            double scaleX = mapW / pixW;
            double scaleY = mapH / pixH;

            double centerTileX = GeoUtils.LonToTileX(centerLon, zoom);
            double centerTileY = GeoUtils.LatToTileY(centerLat, zoom);

            (double px, double py) GeoToPdf(double lon, double lat)
            {
                double tileX   = GeoUtils.LonToTileX(lon, zoom);
                double tileY   = GeoUtils.LatToTileY(lat, zoom);
                double canvasX = (tileX - centerTileX) * 256.0 + pixW / 2.0;
                double canvasY = (tileY - centerTileY) * 256.0 + pixH / 2.0;
                return (mapX + canvasX * scaleX, mapY + canvasY * scaleY);
            }

            // Stili
            var rectPen     = new XPen(XColor.FromArgb(30, 100, 220), 1.2);
            var fillBrush   = new XSolidBrush(XColor.FromArgb(50, 30, 120, 220));
            var labelFont   = new XFont("Arial", 7, XFontStyle.Bold);
            var numFont     = new XFont("Arial", 6);
            var shadowBrush = new XSolidBrush(XColor.FromArgb(180, 255, 255, 255));

            foreach (var page in pages)
            {
                var (x1, y1) = GeoToPdf(page.GeoBounds.MinLon, page.GeoBounds.MaxLat);
                var (x2, y2) = GeoToPdf(page.GeoBounds.MaxLon, page.GeoBounds.MinLat);

                // Clamp ai bordi della mappa visibile
                x1 = Math.Max(x1, mapX); y1 = Math.Max(y1, mapY);
                x2 = Math.Min(x2, mapX + mapW); y2 = Math.Min(y2, mapY + mapH);

                double rw = x2 - x1;
                double rh = y2 - y1;
                if (rw <= 0 || rh <= 0) continue;

                gfx.DrawRectangle(fillBrush, x1, y1, rw, rh);
                gfx.DrawRectangle(rectPen,   x1, y1, rw, rh);

                // Label centrata con ombra
                var labelRect = new XRect(x1 + 1, y1 + 1, rw - 2, rh - 2);
                gfx.DrawString(page.Label, labelFont, shadowBrush, labelRect, XStringFormats.Center);
                gfx.DrawString(page.Label, labelFont, XBrushes.DarkBlue, labelRect, XStringFormats.Center);

                // Numero di pagina in basso a destra (se il rettangolo è abbastanza grande)
                if (rw > 22 && rh > 14)
                    gfx.DrawString($"p.{page.PageNumber}", numFont, XBrushes.Gray,
                        new XRect(x1 + 1, y2 - 10, rw - 2, 9), XStringFormats.BottomRight);
            }

            // Percorsi: disegnati come polilinee vettoriali (stesso backend
            // XGraphics dei rettangoli pagina, non SkiaSharp: la mappa
            // riassuntiva è già un bitmap di sfondo, i rettangoli/percorsi
            // sono overlay vettoriali sopra di esso).
            var routeLabelFont = new XFont("Arial", 6.5, XFontStyle.Bold);
            foreach (var route in percorsi)
            {
                if (route.Points.Count == 0) continue;

                var color   = settings.PdfContrastMode == PdfContrastMode.BlackWhite
                    ? SKColors.Black : PercorsoRenderer.ParseColor(route.ColorHex);
                var pen     = new XPen(XColor.FromArgb(color.Alpha, color.Red, color.Green, color.Blue), 1.6);
                var ptsPdf  = route.Points.Select(p => GeoToPdf(p.Lon, p.Lat)).ToList();

                if (ptsPdf.Count >= 2)
                {
                    for (int i = 1; i < ptsPdf.Count; i++)
                        gfx.DrawLine(pen, ptsPdf[i - 1].px, ptsPdf[i - 1].py, ptsPdf[i].px, ptsPdf[i].py);
                }

                if (!string.IsNullOrWhiteSpace(route.Label))
                    gfx.DrawString(route.Label, routeLabelFont, shadowBrush,
                        new XRect(ptsPdf[0].px + 2, ptsPdf[0].py - 8, 80, 9), XStringFormats.TopLeft);
            }
        }

        // ---------------------------------------------------------------
        // Metodo generico di download tile: scarica una zona di mappa
        // centrata su (centerLon, centerLat) al livello zoom dato.
        // ---------------------------------------------------------------
        private async Task<SKBitmap?> RenderTilesAsync(
            double centerLon, double centerLat, int zoom, int pixW, int pixH,
            string tileServerUrl, double tileSizePx = 256.0)
        {
            var bitmap  = new SKBitmap(pixW, pixH);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(new SKColor(220, 220, 220));

            // Dimensione a cui disegnare ogni tile (256 nativi = scala non corretta;
            // valore frazionario = scala cartografica esatta, vedi CalcScaleExactTiling).
            double tileSize = tileSizePx;
            double cx = GeoUtils.LonToTileX(centerLon, zoom);
            double cy = GeoUtils.LatToTileY(centerLat, zoom);

            int tilesX  = (int)Math.Ceiling(pixW / tileSize) + 2;
            int tilesY  = (int)Math.Ceiling(pixH / tileSize) + 2;
            int startX  = (int)Math.Floor(cx - tilesX / 2.0);
            int startY  = (int)Math.Floor(cy - tilesY / 2.0);
            int maxTile = (int)Math.Pow(2, zoom);

            var tileJobs = new List<(int tx, int ty, int wrapped, float px, float py)>();
            for (int tx = startX; tx <= startX + tilesX; tx++)
            {
                for (int ty = startY; ty <= startY + tilesY; ty++)
                {
                    if (ty < 0 || ty >= maxTile) continue;
                    int   wrapped = ((tx % maxTile) + maxTile) % maxTile;
                    float px      = (float)((tx - cx) * tileSize + pixW / 2.0);
                    float py      = (float)((ty - cy) * tileSize + pixH / 2.0);
                    tileJobs.Add((tx, ty, wrapped, px, py));
                }
            }

            var semaphore = new SemaphoreSlim(4, 4);
            var results   = new ConcurrentDictionary<(int, int), (SKBitmap bmp, float px, float py)>();

            var tasks = tileJobs.Select(async job =>
            {
                await semaphore.WaitAsync();
                try
                {
                    // Sostituisce {z}/{x}/{y} nel template URL
                    string url = tileServerUrl
                        .Replace("{z}", zoom.ToString())
                        .Replace("{x}", job.wrapped.ToString())
                        .Replace("{y}", job.ty.ToString());

                    using var cts  = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    byte[]    data = await _http.GetByteArrayAsync(url, cts.Token);
                    var       bmp  = SKBitmap.Decode(data);
                    if (bmp != null)
                        results[(job.tx, job.ty)] = (bmp, job.px, job.py);
                }
                catch { }
                finally { semaphore.Release(); }
            });

            await Task.WhenAll(tasks);

            // Interpolazione di qualità perché i tile a 256px vengono ridimensionati
            // a tileSize (frazionario); +0.75px di sovrapposizione evita fessure.
            using var tilePaint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
            float ts = (float)tileSize;
            foreach (var kv in results)
            {
                var (bmp, px, py) = kv.Value;
                canvas.DrawBitmap(bmp, new SKRect(px, py, px + ts + 0.75f, py + ts + 0.75f), tilePaint);
                bmp.Dispose();
            }

            return bitmap;
        }

        // Scarica i tile OSM e costruisce la bitmap della mappa per una pagina,
        // disegnandovi sopra anche i marker dei POI ricadenti nell'area.
        // Delega a RenderTilesAsync dopo aver calcolato zoom e dimensioni.
        private async Task<SKBitmap?> RenderMapPageAsync(MapPage page, StradarioSettings settings,
            List<PoiGroup> poiGroups, List<Percorso> percorsi)
        {
            var (pageWidthMm, pageHeightMm) = settings.GetPageDimensionsMm();
            double mapWidthMm  = pageWidthMm  - 2 * BorderMarginMm;
            double mapHeightMm = pageHeightMm - 2 * BorderMarginMm;

            int pixW = (int)(mapWidthMm  / 25.4 * settings.Dpi);
            int pixH = (int)(mapHeightMm / 25.4 * settings.Dpi);

            // Zoom + dimensione tile che rendono la scala cartografica ESATTA a questo DPI.
            var (zoom, tileSizePx) = GeoUtils.CalcScaleExactTiling(
                settings.GetScaleDenominator(), settings.Dpi, page.GeoBounds.CenterLat);

            var bitmap = await RenderTilesAsync(
                page.GeoBounds.CenterLon, page.GeoBounds.CenterLat, zoom, pixW, pixH,
                settings.GetEffectiveTileServerUrl(), tileSizePx);

            // Contrasto (solo PDF, su richiesta): applicato al raster dei tile
            // PRIMA di percorsi/POI, così le sovrapposizioni vettoriali restano nitide.
            bitmap = ApplyContrastPipeline(bitmap, settings);

            bool forceBlackWhite = settings.PdfContrastMode == PdfContrastMode.BlackWhite;

            if (bitmap != null && percorsi.Count > 0)
                DrawRoutesOnBitmap(bitmap, page.GeoBounds.CenterLon, page.GeoBounds.CenterLat,
                    zoom, tileSizePx, pixW, pixH, percorsi, forceBlackWhite);

            if (bitmap != null && poiGroups.Count > 0)
                DrawPoisOnBitmap(bitmap, page.GeoBounds.CenterLon, page.GeoBounds.CenterLat,
                    zoom, tileSizePx, pixW, pixH, poiGroups, forceBlackWhite);

            return bitmap;
        }

        // Disegna i percorsi ricadenti nell'area della pagina direttamente sul
        // bitmap ad alta risoluzione, usando la stessa proiezione di RenderTilesAsync.
        // Disegnati prima dei POI così i marker restano sempre in primo piano.
        // In modalità "Contrasta B/N" il colore del percorso viene ignorato e
        // forzato a nero puro: sulla mappa desaturata i colori dei gruppi non
        // sono più distinguibili tra loro, mentre il nero massimizza la resa.
        private void DrawRoutesOnBitmap(
            SKBitmap bitmap,
            double centerLon, double centerLat, int zoom, double tileSizePx,
            int pixW, int pixH,
            List<Percorso> percorsi, bool forceBlackWhite = false)
        {
            using var canvas = new SKCanvas(bitmap);

            (double x, double y) Project(double lon, double lat) =>
                GeoUtils.GeoToBitmapPixel(lon, lat, centerLon, centerLat, zoom, tileSizePx, pixW, pixH);

            foreach (var route in percorsi)
                PercorsoRenderer.Draw(canvas, route, Project,
                    colorOverride: forceBlackWhite ? SKColors.Black : (SKColor?)null);
        }

        // Disegna i marker dei POI ricadenti nell'area della pagina (con un
        // piccolo margine) direttamente sul bitmap ad alta risoluzione, usando
        // la stessa proiezione impiegata da RenderTilesAsync per i tile.
        // Stesso discorso di DrawRoutesOnBitmap per forceBlackWhite.
        private void DrawPoisOnBitmap(
            SKBitmap bitmap,
            double centerLon, double centerLat, int zoom, double tileSizePx,
            int pixW, int pixH,
            List<PoiGroup> poiGroups, bool forceBlackWhite = false)
        {
            const float markerSize = 26f;
            using var canvas = new SKCanvas(bitmap);

            foreach (var group in poiGroups)
            {
                var color = forceBlackWhite ? SKColors.Black : PoiIconRenderer.ParseColor(group.ColorHex);
                foreach (var item in group.Items)
                {
                    var (x, y) = GeoUtils.GeoToBitmapPixel(item.Lon, item.Lat,
                        centerLon, centerLat, zoom, tileSizePx, pixW, pixH);

                    if (x < -markerSize || x > pixW + markerSize ||
                        y < -markerSize || y > pixH + markerSize)
                        continue;

                    PoiIconRenderer.DrawWithLabel(canvas, group.Icon, color, item.Label,
                        (float)x, (float)y, markerSize);
                }
            }
        }

        // Disegna la pagina PDF: mappa + bordi con pagine adiacenti
        private void DrawMapPage(
            PdfPage pdfPage,
            SKBitmap? mapBitmap,
            MapPage page,
            AdjacentPages adjacent,
            StradarioSettings settings)
        {
            using var gfx = XGraphics.FromPdfPage(pdfPage);
            double w = pdfPage.Width.Point;
            double h = pdfPage.Height.Point;

            // Converti margine da mm a punti PDF (1 mm = 2.8346 pt)
            double marginPt = BorderMarginMm * 2.8346;

            // Area mappa in punti PDF
            double mapX = marginPt;
            double mapY = marginPt;
            double mapW = w - 2 * marginPt;
            double mapH = h - 2 * marginPt;

            // Disegna la bitmap della mappa
            if (mapBitmap != null)
            {
                using var ms  = new MemoryStream();
                mapBitmap.Encode(ms, SKEncodedImageFormat.Png, 90);
                ms.Position = 0;
                var xImg = XImage.FromStream(() => new MemoryStream(ms.ToArray()));
                gfx.DrawImage(xImg, mapX, mapY, mapW, mapH);
            }
            else
            {
                gfx.DrawRectangle(XBrushes.LightGray, mapX, mapY, mapW, mapH);
            }

            // Bordo nero attorno all'area mappa
            gfx.DrawRectangle(XPens.Black, mapX, mapY, mapW, mapH);

            // Font per i bordi
            var borderFont = new XFont("Arial", 8, XFontStyle.Bold);
            var numFont    = new XFont("Arial", 11, XFontStyle.Bold);

            // ---- Bordo NORD ----
            DrawBorderStrip(gfx, 0, 0, w, marginPt,
                adjacent.North != null ? $"▲ pag. {adjacent.North.PageNumber}  {adjacent.North.Label}" : "▲",
                borderFont, isVertical: false);

            // ---- Bordo SUD ----
            DrawBorderStrip(gfx, 0, h - marginPt, w, marginPt,
                adjacent.South != null ? $"▼ pag. {adjacent.South.PageNumber}  {adjacent.South.Label}" : "▼",
                borderFont, isVertical: false);

            // ---- Bordo OVEST ----
            DrawBorderStrip(gfx, 0, 0, marginPt, h,
                adjacent.West != null ? $"◄ pag. {adjacent.West.PageNumber}  {adjacent.West.Label}" : "◄",
                borderFont, isVertical: true);

            // ---- Bordo EST ----
            DrawBorderStrip(gfx, w - marginPt, 0, marginPt, h,
                adjacent.East != null ? $"pag. {adjacent.East.PageNumber}  {adjacent.East.Label} ►" : "►",
                borderFont, isVertical: true);

            // Numero di pagina e label in alto a sinistra (nell'area mappa)
            string header = $"  {page.PageNumber} - {page.Label}";
            gfx.DrawString(header, numFont, XBrushes.Black,
                new XRect(mapX + 4, mapY + 4, mapW, 20), XStringFormats.TopLeft);

            // Coordinate centro in basso a destra
            var coordFont = new XFont("Arial", 7);
            string coords = $"{page.GeoBounds.CenterLon:F4}°E  {page.GeoBounds.CenterLat:F4}°N";
            gfx.DrawString(coords, coordFont, XBrushes.DarkGray,
                new XRect(mapX, mapY + mapH - 14, mapW - 4, 14), XStringFormats.BottomRight);

            // Righello / barra di scala grafica: nella fascia di margine SUD,
            // sotto l'area mappa (non più sovrapposto alla mappa stessa).
            DrawScaleBar(gfx, settings, mapX, h - marginPt, mapW, marginPt);
        }

        // Disegna un righello graduato ("10 cm = X km") nella fascia di margine
        // sotto la mappa, allineato a sinistra (il centro/destra della fascia
        // restano liberi per il riferimento "▼ pag. N" già centrato da
        // DrawBorderStrip). La lunghezza reale è calcolata sulla scala richiesta:
        // poiché il rendering rispetta la scala esatta, la lunghezza scelta di
        // carta corrisponde davvero a questa distanza.
        private void DrawScaleBar(XGraphics gfx, StradarioSettings settings,
            double stripX, double stripY, double stripW, double stripH)
        {
            const double MmToPt = 2.8346; // 1 mm in punti PDF
            int scaleDenom = settings.GetScaleDenominator();

            // Lunghezza del righello sulla carta: prova 10/5/2 cm finché non entra
            // nello spazio disponibile a sinistra della fascia (lascia libero il
            // resto della fascia per il testo centrato dei bordi).
            double maxBarLenPt = stripW * 0.42 - 16;
            double barCm = 10.0;
            foreach (double candidate in new[] { 10.0, 5.0, 2.0, 1.0 })
            {
                if (candidate * 10.0 * MmToPt <= maxBarLenPt)
                {
                    barCm = candidate;
                    break;
                }
                barCm = candidate; // ultima spiaggia: usa comunque la più piccola
            }
            double barLenPt = barCm * 10.0 * MmToPt;

            // Distanza reale corrispondente: barCm/100 metri * denominatore scala.
            double realMeters = barCm / 100.0 * scaleDenom;
            string realLabel  = realMeters >= 1000.0
                ? $"{realMeters / 1000.0:0.###} km"
                : $"{realMeters:0} m";

            double barH = 1.5 * MmToPt;   // 1.5 mm: metà dell'altezza precedente (3 mm)

            var tickFont = new XFont("Arial", 5.0);

            // Blocco (tacche + barra + didascalia) centrato verticalmente nella fascia.
            double tickRowH    = 7.0;
            double captionRowH = 7.0;
            double blockH      = tickRowH + barH + captionRowH + 2.0;
            double x0 = stripX + 8;
            double y0 = stripY + (stripH - blockH) / 2.0 + tickRowH;

            // Barra suddivisa in segmenti da 1 cm, alternati nero/bianco.
            int    segs   = Math.Max(1, (int)Math.Round(barCm));
            double segLen = barLenPt / segs;
            for (int i = 0; i < segs; i++)
                gfx.DrawRectangle(i % 2 == 0 ? XBrushes.Black : XBrushes.White,
                    x0 + i * segLen, y0, segLen, barH);
            gfx.DrawRectangle(XPens.Black, x0, y0, barLenPt, barH);

            // Estremi: 0 a sinistra, distanza reale a destra della barra.
            gfx.DrawString("0", tickFont, XBrushes.Black,
                new XRect(x0 - 8, y0 - tickRowH, 16, tickRowH), XStringFormats.TopCenter);
            gfx.DrawString(realLabel, tickFont, XBrushes.Black,
                new XRect(x0 + barLenPt - 40, y0 - tickRowH, 44, tickRowH), XStringFormats.TopRight);

            // Didascalia sotto la barra.
            gfx.DrawString($"{barCm:0} cm = {realLabel}  (scala {settings.GetScaleLabel()})",
                tickFont, XBrushes.DimGray,
                new XRect(x0, y0 + barH + 1, barLenPt + 70, captionRowH), XStringFormats.TopLeft);
        }

        // Disegna una striscia di bordo con sfondo grigio chiaro e testo centrato
        private void DrawBorderStrip(XGraphics gfx, double x, double y, double w, double h,
            string text, XFont font, bool isVertical)
        {
            gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(230, 230, 230)), x, y, w, h);
            gfx.DrawRectangle(XPens.Gray, x, y, w, h);

            if (isVertical)
            {
                // Testo ruotato 90° per i bordi laterali
                gfx.Save();
                gfx.TranslateTransform(x + w / 2, y + h / 2);
                gfx.RotateTransform(-90);
                gfx.DrawString(text, font, XBrushes.Black,
                    new XRect(-h / 2, -w / 2, h, w), XStringFormats.Center);
                gfx.Restore();
            }
            else
            {
                gfx.DrawString(text, font, XBrushes.Black,
                    new XRect(x, y, w, h), XStringFormats.Center);
            }
        }
    }
}
