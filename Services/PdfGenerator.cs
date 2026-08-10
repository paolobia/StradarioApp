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

        // Margine per i bordi con indicazioni adiacenti (mm): definito in
        // StradarioSettings.BorderMarginMm, non qui, perché anche
        // GetPageWidthKm/GetPageHeightKm (quindi GeoUtils.CalcPageBounds) ne
        // hanno bisogno per calcolare GeoBounds sulla stessa area che viene
        // davvero stampata — vedi il commento lì per il bug reale che questo
        // ha causato quando le due costanti erano disallineate.
        private const double BorderMarginMm = StradarioSettings.BorderMarginMm;

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
            Action<int, int, string>? progress = null,
            string? coverTitle = null)
        {
            var settings = project.Settings;
            var pages    = project.Pages;

            // Nessun blocco su "0 pagine": un progetto può avere solo
            // POI/percorsi senza ancora alcuna pagina mappa definita, e il
            // PDF deve comunque generarsi (copertina, eventuali elenchi
            // POI/percorsi, indice, mappa riassuntiva) — richiesta esplicita
            // dell'utente. La mappa riassuntiva ricava comunque i suoi
            // confini da POI/percorsi quando non ci sono pagine (v.
            // CalcOverallBounds), quindi resta utile anche in quel caso.

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
            string title = string.IsNullOrWhiteSpace(coverTitle) ? project.ProjectName : coverTitle!;

            var pdfDoc = new PdfDocument();
            pdfDoc.Info.Title   = title;
            pdfDoc.Info.Creator = "StradarioApp";

            // ---------------------------------------------------------------
            // Copertina: titolo grande, prima di tutto il resto.
            // ---------------------------------------------------------------
            var coverPage = pdfDoc.AddPage();
            SetPageSize(coverPage, pageWidthMm, pageHeightMm);
            DrawCoverPage(coverPage, title, settings);

            // ---------------------------------------------------------------
            // Pagine iniziali: "Piano di viaggio" (se il progetto ha almeno
            // una data impostata, su POI o estremi di percorso), poi l'elenco
            // dei gruppi di POI e dei percorsi (se presenti), prima
            // dell'indice. Il numero di pagine è variabile (paginazione
            // automatica), perciò la numerazione delle pagine mappa successive
            // dipende da quante pagine sono state generate.
            //
            // POI e percorsi SENZA data sono sempre elencati per intero, non
            // più filtrati per contenimento geografico nelle pagine mappa
            // (prima un POI/percorso fuori da ogni pagina spariva dal
            // gazetteer) — richiesta esplicita dell'utente, anche perché ora
            // il PDF si genera pure senza alcuna pagina definita (v. sopra),
            // nel qual caso il vecchio filtro avrebbe sempre svuotato tutto.
            // Un POI/percorso CON data è invece già per intero nel "Piano di
            // viaggio" sopra: va escluso da questi elenchi "normali", altrimenti
            // comparirebbe due volte nel PDF.
            // ---------------------------------------------------------------
            bool anyDateSet = poiGroups.Any(g => g.Items.Any(i => i.DateStart.HasValue || i.DateEnd.HasValue)) ||
                               percorsi.Any(r => r.StartDateTime.HasValue || r.EndDateTime.HasValue);

            int itineraryPageCount = 0;
            if (anyDateSet)
            {
                progress?.Invoke(0, sorted.Count + 2, "Generazione piano di viaggio...");
                itineraryPageCount = DrawItineraryPages(pdfDoc, poiGroups, percorsi, pageWidthMm, pageHeightMm);
            }

            static bool PoiIsUndated(PoiItem i) => !i.DateStart.HasValue && !i.DateEnd.HasValue;
            static bool PercorsoIsUndated(Percorso r) => !r.StartDateTime.HasValue && !r.EndDateTime.HasValue;

            bool anyUndatedPoi = poiGroups.Any(g => g.Items.Any(PoiIsUndated));
            int poiPageCount = 0;
            if (anyUndatedPoi)
            {
                progress?.Invoke(itineraryPageCount, sorted.Count + 2 + itineraryPageCount, "Generazione elenco punti di interesse...");
                poiPageCount = DrawPoiListPages(pdfDoc, poiGroups, pageWidthMm, pageHeightMm, PoiIsUndated);
            }

            bool anyUndatedPercorso = percorsi.Any(PercorsoIsUndated);
            int percorsiPageCount = 0;
            if (anyUndatedPercorso)
            {
                progress?.Invoke(itineraryPageCount + poiPageCount, sorted.Count + 2 + itineraryPageCount + poiPageCount, "Generazione elenco percorsi...");
                percorsiPageCount = DrawPercorsiListPages(pdfDoc, percorsi, pageWidthMm, pageHeightMm, PercorsoIsUndated);
            }

            // La pagina Indice elenca le pagine mappa: senza alcuna pagina
            // sarebbe solo titolo+sottotitolo e una tabella vuota — inutile,
            // richiesta esplicita dell'utente di saltarla in quel caso (il
            // resto del front-matter, POI/percorsi/overview, resta invariato).
            bool hasPages = sorted.Count > 0;
            int  indexPageCount = hasPages ? 1 : 0;

            // Le pagine mappa partono dopo: copertina, piano di viaggio, pagine POI, pagine percorsi, indice (se presente), overview
            int frontMatterPages = itineraryPageCount + poiPageCount + percorsiPageCount + indexPageCount + 2; // +2: copertina + overview
            for (int i = 0; i < sorted.Count; i++)
                sorted[i].PageNumber = i + 1 + frontMatterPages;

            int totalSteps = sorted.Count + 1 + indexPageCount + itineraryPageCount + poiPageCount + percorsiPageCount;

            // ---------------------------------------------------------------
            // Pagina indice testuale (solo se ci sono pagine mappa)
            // ---------------------------------------------------------------
            if (hasPages)
            {
                progress?.Invoke(itineraryPageCount + poiPageCount + percorsiPageCount, totalSteps, "Generazione indice...");
                var indexPage = pdfDoc.AddPage();
                SetPageSize(indexPage, pageWidthMm, pageHeightMm);
                DrawIndexPage(indexPage, sorted, project.ProjectName, settings);
            }

            // ---------------------------------------------------------------
            // Pagina mappa riassuntiva con tutti i rettangoli
            // ---------------------------------------------------------------
            progress?.Invoke(itineraryPageCount + poiPageCount + percorsiPageCount + indexPageCount, totalSteps, "Generazione mappa riassuntiva...");
            var overviewPage = pdfDoc.AddPage();
            SetPageSize(overviewPage, pageWidthMm, pageHeightMm);
            var overviewBitmap = await RenderOverviewAsync(sorted, poiGroups, percorsi, settings);
            DrawOverviewPage(overviewPage, overviewBitmap, sorted, poiGroups, percorsi, project.ProjectName, settings);
            overviewBitmap?.Dispose();

            // ---------------------------------------------------------------
            // Pagine successive: una per ogni quadrante
            // ---------------------------------------------------------------
            for (int i = 0; i < sorted.Count; i++)
            {
                var mapPage = sorted[i];
                progress?.Invoke(itineraryPageCount + poiPageCount + percorsiPageCount + indexPageCount + 1 + i, totalSteps, $"Pagina {mapPage.PageNumber}: {mapPage.Label}...");

                // Ogni pagina può avere un proprio orientamento/scala (override
                // rispetto alle impostazioni globali) — vedi MapPage.GetEffectiveSettings.
                var pageSettings = mapPage.GetEffectiveSettings(settings);
                var (pageMmW, pageMmH) = pageSettings.GetPageDimensionsMm();

                var pdfPage = pdfDoc.AddPage();
                SetPageSize(pdfPage, pageMmW, pageMmH);

                var adjacent  = FindAdjacentPages(mapPage, sorted);
                var mapBitmap = await RenderMapPageAsync(mapPage, pageSettings, poiGroups, percorsi);
                DrawMapPage(pdfPage, mapBitmap, mapPage, adjacent, pageSettings);
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

        // Disegna la copertina: titolo grande centrato (di default il nome
        // del file .stradario senza estensione, vedi MainWindow.OnGeneratePdf),
        // una riga sottile di separazione e un sottotitolo con scala/formato
        // e data di generazione. Stile professionale: nero, nessun corsivo,
        // nessun colore decorativo.
        private void DrawCoverPage(PdfPage page, string title, StradarioSettings settings)
        {
            using var gfx = XGraphics.FromPdfPage(page);
            double w = page.Width.Point;
            double h = page.Height.Point;

            gfx.DrawRectangle(XBrushes.White, 0, 0, w, h);

            var titleFont    = new XFont("Arial", 28, XFontStyle.Bold);
            var subtitleFont = new XFont("Arial", 11);

            double centerY = h * 0.42;

            gfx.DrawString(title, titleFont, XBrushes.Black,
                new XRect(40, centerY - 40, w - 80, 60), XStringFormats.Center);

            double ruleY = centerY + 30;
            gfx.DrawLine(XPens.Black, w * 0.28, ruleY, w * 0.72, ruleY);

            string subtitle = $"Scala {settings.GetScaleLabel()}  |  {settings.PageSize} {settings.Orientation}  |  {DateTime.Now:d MMMM yyyy}";
            gfx.DrawString(subtitle, subtitleFont, XBrushes.Black,
                new XRect(40, ruleY + 14, w - 80, 20), XStringFormats.Center);

            var footerFont = new XFont("Arial", 8);
            gfx.DrawString("StradarioApp", footerFont, XBrushes.Black,
                new XRect(0, h - 40, w, 16), XStringFormats.BottomCenter);
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
            gfx.DrawString(sub, subFont, XBrushes.Black,
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

                // Etichetta: fino a 2 righe (colLabel è stretta, 10% della
                // tabella — a riga singola fissa un'etichetta anche solo
                // moderatamente lunga usciva dalla colonna senza alcun
                // adattamento). Stesso schema "wrap reale su 2 righe + ellissi"
                // già usato sotto per la descrizione.
                DrawWrappedTwoLines(gfx, p.Label, cellFont, xLabel + 2, y + 1, colLabel - 4);

                // Centro: lon/lat su due righe corte
                gfx.DrawString($"{p.GeoBounds.CenterLon:F3}°", cellFont, XBrushes.Black,
                    new XRect(xCenter + 2, y + 1,     colCenter - 4, 10), XStringFormats.TopLeft);
                gfx.DrawString($"{p.GeoBounds.CenterLat:F3}°", cellFont, XBrushes.Black,
                    new XRect(xCenter + 2, y + 1 + 11, colCenter - 4, 10), XStringFormats.TopLeft);

                // Descrizione: fino a 2 righe, con wrap reale (basato sulla
                // larghezza effettiva della colonna, non un taglio a
                // conteggio di caratteri fisso come prima — che poteva
                // tagliare a metà parola o, con un font/colonna più larghi
                // del previsto, sprecare spazio senza motivo).
                if (hasDesc)
                    DrawWrappedTwoLines(gfx, p.Description, cellFont, xDesc + 2, y + 1, colDesc - 4);

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

        // Spezza un testo in più righe che entrano in maxWidth (word-wrap
        // greedy: aggiunge parole finché stanno nella larghezza, altrimenti
        // va a capo). Usata per le descrizioni di gruppi POI/percorsi/singoli
        // POI nelle pagine elenco: senza wrap una descrizione lunga finiva
        // tagliata a bordo pagina invece di andare a capo.
        private static List<string> WrapText(XGraphics gfx, string text, XFont font, double maxWidth)
        {
            // I newline letterali (es. tag OSM multipli uniti con "\n" in
            // BuildOsmTagsString) non sono interpretati da XGraphics.DrawString
            // come "a capo": il carattere finiva disegnato come glifo (un
            // quadratino "tofu", Arial non lo mappa) in mezzo al testo. Li
            // normalizziamo a "; " prima dello split-by-space che segue.
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\r\n|\r|\n", "; ");
            var lines = new List<string>();
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var current = "";
            foreach (var word in words)
            {
                string candidate = current.Length == 0 ? word : current + " " + word;
                if (gfx.MeasureString(candidate, font).Width > maxWidth && current.Length > 0)
                {
                    lines.Add(current);
                    current = word;
                }
                else
                {
                    current = candidate;
                }
            }
            if (current.Length > 0) lines.Add(current);
            return lines;
        }

        // Disegna un testo su un massimo di 2 righe (wrap reale via WrapText,
        // non un taglio a conteggio di caratteri fisso) dentro una cella di
        // altezza fissa a due righe da 11pt — usata nella tabella indice
        // pagine per etichetta e descrizione. Se il testo eccede le 2 righe,
        // la seconda viene troncata con "…" invece di sparire silenziosamente.
        private static void DrawWrappedTwoLines(XGraphics gfx, string text, XFont font, double x, double y, double maxWidth)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var lines = WrapText(gfx, text, font, maxWidth);
            for (int i = 0; i < Math.Min(2, lines.Count); i++)
            {
                string line = lines[i];
                if (i == 1 && lines.Count > 2)
                    line = TruncateToWidth(gfx, line + "…", font, maxWidth);
                gfx.DrawString(line, font, XBrushes.Black,
                    new XRect(x, y + i * 11, maxWidth, 10), XStringFormats.TopLeft);
            }
        }

        // Accorcia un testo su UNA riga per farlo entrare in maxWidth,
        // troncando carattere per carattere e aggiungendo "…" — usata dove
        // il layout richiede una riga singola (non c'è spazio per andare a
        // capo, es. celle strette o etichette su mappa) invece del wrap
        // multi-riga di WrapText sopra.
        private static string TruncateToWidth(XGraphics gfx, string text, XFont font, double maxWidth)
        {
            if (string.IsNullOrEmpty(text) || gfx.MeasureString(text, font).Width <= maxWidth)
                return text;
            const string ellipsis = "…";
            string result = text;
            while (result.Length > 0 && gfx.MeasureString(result + ellipsis, font).Width > maxWidth)
                result = result[..^1];
            return result.Length > 0 ? result + ellipsis : ellipsis;
        }

        // Riduce la dimensione di un font (fino a minSize) finché `text` entra
        // su una riga in maxWidth — usata per etichette a riga singola in aree
        // strette (bordi pagina, mappa riassuntiva) dove prima un font fisso
        // poteva uscire dai bordi senza alcun adattamento. Se anche a minSize
        // il testo non entra, il chiamante deve troncare col risultato (v.
        // FitTextSingleLine sotto, che combina le due cose).
        private static XFont ShrinkFontToFit(XGraphics gfx, string text, XFont font, double maxWidth, double minSize)
        {
            double size = font.Size;
            var fitted = font;
            while (size > minSize && gfx.MeasureString(text, fitted).Width > maxWidth)
            {
                size = Math.Max(minSize, size - 0.5);
                fitted = new XFont(font.Name, size, font.Style);
            }
            return fitted;
        }

        // Combina ShrinkFontToFit + TruncateToWidth: prima prova a rimpicciolire
        // il font fino a minSize, poi — se il testo ancora non entra — lo
        // tronca con l'ellissi alla dimensione minima raggiunta. Garantisce
        // che il testo restituito entri sempre in maxWidth su una riga.
        private static (XFont Font, string Text) FitTextSingleLine(XGraphics gfx, string text, XFont font, double maxWidth, double minSize)
        {
            var fitted = ShrinkFontToFit(gfx, text, font, maxWidth, minSize);
            string fittedText = gfx.MeasureString(text, fitted).Width > maxWidth
                ? TruncateToWidth(gfx, text, fitted, maxWidth)
                : text;
            return (fitted, fittedText);
        }

        // Disegna il "Piano di viaggio": la sequenza cronologica unificata di
        // tutti i POI e gli estremi di percorso (partenza/arrivo) che hanno
        // una data impostata, più — in coda, per esplicita scelta di design
        // (a differenza dell'albero di navigazione, dove i non datati vanno
        // in testa) — tutti gli elementi senza data. Chiamata solo se il
        // progetto ha almeno una data impostata da qualche parte (v.
        // GenerateAsync): un progetto senza date non genera questa sezione,
        // zero impatto sul PDF prodotto prima di questa funzionalità.
        private int DrawItineraryPages(PdfDocument pdfDoc, List<PoiGroup> poiGroups, List<Percorso> percorsi,
            double pageWidthMm, double pageHeightMm)
        {
            const double margin      = 24;
            const double rowH        = 18;
            const double descRowH    = 10;
            const double iconSize    = 14;
            const double markerSize  = 10;
            const double nestedIconSize = 12;
            const double nestedRowH     = 14;
            const double nestedDescRowH = 9;

            var titleFont     = new XFont("Arial", 16, XFontStyle.Bold);
            var descFont      = new XFont("Arial", 6.5, XFontStyle.Regular);
            var dateFont      = descFont;
            var labelFont     = descFont;
            var nestedFont    = new XFont("Arial", 6.5, XFontStyle.Regular);
            var nestedDescFont = new XFont("Arial", 6, XFontStyle.Italic);

            // Colori fissi (indipendenti dal colore del gruppo/percorso) per
            // le due frecce Inizio/Fine, così restano riconoscibili ovunque.
            var startColor = XColor.FromArgb(46, 125, 50);
            var endColor   = XColor.FromArgb(198, 40, 40);

            var iconCache = new Dictionary<string, XImage>();
            XImage GetIcon(PoiIconType icon, string colorHex)
            {
                string key = $"{icon}_{colorHex}";
                if (iconCache.TryGetValue(key, out var cached)) return cached;
                using var bmp = PoiIconRenderer.RenderToBitmap(icon, PoiIconRenderer.ParseColor(colorHex), 48);
                using var ms  = new MemoryStream();
                bmp.Encode(ms, SKEncodedImageFormat.Png, 100);
                ms.Position = 0;
                var xImg = XImage.FromStream(() => new MemoryStream(ms.ToArray()));
                iconCache[key] = xImg;
                return xImg;
            }

            var entries = ItineraryOrdering.BuildItineraryEntries(poiGroups, percorsi);
            if (entries.Count == 0) return 0;

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

                string title = pageCount == 1 ? "Piano di viaggio" : "Piano di viaggio (segue)";
                gfx.DrawString(title, titleFont, XBrushes.Black,
                    new XRect(0, 18, w, 26), XStringFormats.TopCenter);

                y = 18 + 40;
            }

            // Disegna l'icona "Inizio" (freccia verso il basso, come un pin
            // che atterra sul punto) o "Fine" (stessa freccia, più corta, che
            // tocca una linea di base — il punto in cui il viaggio si ferma).
            void DrawStartEndMarker(double x, double y0, double size, bool isStart)
            {
                var color = isStart ? startColor : endColor;
                var brush = new XSolidBrush(color);
                double stemW = size * 0.22;
                double headW = size * 0.75;
                double headH = size * 0.55;
                double cx    = x + size / 2.0;
                double arrowH = isStart ? size : size * 0.8;
                double shaftH = arrowH - headH;

                gfx!.DrawRectangle(brush, cx - stemW / 2, y0, stemW, shaftH);
                var head = new[]
                {
                    new XPoint(cx - headW / 2, y0 + shaftH),
                    new XPoint(cx + headW / 2, y0 + shaftH),
                    new XPoint(cx, y0 + arrowH)
                };
                gfx.DrawPolygon(brush, head, XFillMode.Winding);

                if (!isStart)
                {
                    var groundPen = new XPen(color, size * 0.12);
                    gfx.DrawLine(groundPen, x, y0 + size, x + size, y0 + size);
                }
            }

            NewPage();

            double dateColWidth = 110;
            foreach (var entry in entries)
            {
                bool hasMarker = entry.IsStart.HasValue;
                double markerExtra = hasMarker ? markerSize + 4 : 0;

                bool hasDesc = !string.IsNullOrWhiteSpace(entry.Description);
                double descWidth = tableWidth - iconSize - dateColWidth - 18 - markerExtra;
                var descLines = hasDesc ? WrapText(gfx!, entry.Description!, descFont, descWidth) : new List<string>();
                double neededH = rowH + descLines.Count * descRowH;
                if (y + neededH > h - margin)
                    NewPage();

                string dateText = entry.Date.HasValue ? ItineraryOrdering.FormatSingleDate(entry.Date.Value) : "—";
                gfx!.DrawString(dateText, dateFont, XBrushes.Black,
                    new XRect(margin, y, dateColWidth, rowH), XStringFormats.CenterLeft);

                double iconX = margin + dateColWidth;
                double iconY = y + (rowH - iconSize * 0.85) / 2.0;
                gfx.DrawImage(GetIcon(entry.Icon, entry.ColorHex), iconX, iconY, iconSize * 0.85, iconSize * 0.85);

                double contentX = iconX + iconSize + 4;
                if (hasMarker)
                {
                    double markerY = y + (rowH - markerSize) / 2.0;
                    DrawStartEndMarker(contentX, markerY, markerSize, entry.IsStart!.Value);
                    contentX += markerSize + 4;
                }

                double labelWidth = margin + tableWidth - contentX - 2;
                var (labelFitFont, labelFitText) = FitTextSingleLine(gfx, entry.Label, labelFont, labelWidth - 2, 6.5);
                gfx.DrawString(labelFitText, labelFitFont, XBrushes.Black,
                    new XRect(contentX, y, labelWidth, rowH), XStringFormats.CenterLeft);
                y += rowH;

                foreach (var line in descLines)
                {
                    gfx.DrawString(line, descFont, XBrushes.Black,
                        new XRect(iconX + iconSize + 4, y, descWidth, descRowH), XStringFormats.TopLeft);
                    y += descRowH;
                }

                // Punti del percorso marcati POI, annidati sotto l'entry di
                // Inizio (o di Fine, se manca l'Inizio) — stesso layout di
                // DrawPercorsiListPages, ma senza colonna data (i punti non
                // hanno una data propria).
                if (entry.NestedPoints != null)
                {
                    double nestedX = iconX + iconSize + 12;
                    foreach (var np in entry.NestedPoints)
                    {
                        bool hasNpDesc = !string.IsNullOrWhiteSpace(np.Description);
                        double npDescWidth = margin + tableWidth - nestedX - nestedIconSize - 6;
                        var npDescLines = hasNpDesc ? WrapText(gfx!, np.Description!, nestedDescFont, npDescWidth) : new List<string>();
                        double neededNpH = nestedRowH + npDescLines.Count * nestedDescRowH;
                        if (y + neededNpH > h - margin)
                            NewPage();

                        double npIconY = y + (nestedRowH - nestedIconSize * 0.85) / 2.0;
                        gfx!.DrawImage(GetIcon(np.Icon, entry.ColorHex), nestedX, npIconY, nestedIconSize * 0.85, nestedIconSize * 0.85);
                        double npLabelWidth = (margin + tableWidth) * 0.55 - nestedX;
                        var (npLabelFont, npLabelText) = FitTextSingleLine(gfx, np.Label, nestedFont, npLabelWidth - 2, 6);
                        gfx.DrawString(npLabelText, npLabelFont, XBrushes.Black,
                            new XRect(nestedX + nestedIconSize + 4, y, npLabelWidth, nestedRowH), XStringFormats.CenterLeft);
                        string npCoords = $"{np.Lon:F4}°E  {np.Lat:F4}°N";
                        gfx.DrawString(npCoords, dateFont,
                            XBrushes.Black, new XRect(margin + tableWidth * 0.62, y, tableWidth * 0.38 - 4, nestedRowH), XStringFormats.CenterLeft);
                        y += nestedRowH;

                        foreach (var line in npDescLines)
                        {
                            gfx.DrawString(line, nestedDescFont, XBrushes.Black,
                                new XRect(nestedX + nestedIconSize + 4, y, npDescWidth, nestedDescRowH), XStringFormats.TopLeft);
                            y += nestedDescRowH;
                        }
                    }
                }
            }

            return pageCount;
        }

        // Disegna l'elenco dei gruppi di POI (icona + nome + descrizione, poi
        // una riga per ogni POI con icona, etichetta e coordinate), in testa
        // al documento prima dell'indice. Crea automaticamente tutte le
        // pagine necessarie (nessun troncamento). Ritorna il numero di
        // pagine create. `itemFilter` (se presente) esclude i POI già
        // mostrati per intero nel "Piano di viaggio" — un gruppo che non ha
        // più nessun item dopo il filtro non compare affatto (niente
        // intestazione vuota).
        private int DrawPoiListPages(PdfDocument pdfDoc, List<PoiGroup> poiGroups,
            double pageWidthMm, double pageHeightMm, Func<PoiItem, bool>? itemFilter = null)
        {
            const double margin      = 24;
            const double groupRowH    = 20;
            const double descRowH     = 12;
            const double itemRowH     = 16;
            const double itemDescRowH = 10;
            const double iconSize     = 14;

            var titleFont    = new XFont("Arial", 16, XFontStyle.Bold);
            var groupFont    = new XFont("Arial", 11, XFontStyle.Bold);
            var descFont     = new XFont("Arial", 8);
            var itemFont     = new XFont("Arial", 9);
            var itemDescFont = new XFont("Arial", 6.5);
            var coordFont    = new XFont("Arial", 8);

            // Cache keyed su (icona, colore) invece che sul gruppo: l'icona
            // è ora una proprietà del singolo item, il colore resta del
            // gruppo — più item/gruppi diversi possono condividere la
            // stessa combinazione e riusare la stessa XImage.
            var iconCache = new Dictionary<string, XImage>();
            XImage GetIcon(PoiIconType icon, string colorHex)
            {
                string key = $"{icon}_{colorHex}";
                if (iconCache.TryGetValue(key, out var cached)) return cached;
                using var bmp = PoiIconRenderer.RenderToBitmap(icon, PoiIconRenderer.ParseColor(colorHex), 48);
                using var ms  = new MemoryStream();
                bmp.Encode(ms, SKEncodedImageFormat.Png, 100);
                ms.Position = 0;
                var xImg = XImage.FromStream(() => new MemoryStream(ms.ToArray()));
                iconCache[key] = xImg;
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
                var items = itemFilter == null ? group.Items : group.Items.Where(itemFilter).ToList();
                if (items.Count == 0) continue;

                double groupDescWidth = tableWidth - iconSize - 16;
                bool hasGroupDesc = !string.IsNullOrWhiteSpace(group.Description);
                var groupDescLines = hasGroupDesc ? WrapText(gfx!, group.Description, descFont, groupDescWidth) : new List<string>();

                // Il primo POI del gruppo deve stare sulla stessa pagina
                // dell'intestazione (mai un'intestazione "orfana" a fondo
                // pagina con i suoi POI rimandati alla pagina successiva).
                bool firstItemHasDesc = !string.IsNullOrWhiteSpace(items[0].Description);
                double firstItemDescWidth = tableWidth - iconSize - 18;
                int firstItemDescLines = firstItemHasDesc
                    ? WrapText(gfx!, items[0].Description, itemDescFont, firstItemDescWidth).Count : 0;
                double neededFirstItemH = itemRowH + firstItemDescLines * itemDescRowH;

                double neededGroupStartH = groupRowH + groupDescLines.Count * descRowH + neededFirstItemH;
                if (y + neededGroupStartH > h - margin)
                    NewPage();

                gfx!.DrawRectangle(new XSolidBrush(XColor.FromArgb(230, 238, 250)), margin, y, tableWidth, groupRowH);
                // Il gruppo non ha più un'icona propria (icona = proprietà
                // del singolo POI): qui mostriamo solo lo swatch colore
                // condiviso da tutti i suoi POI, stesso schema di
                // DrawPercorsiListPages per i percorsi.
                var groupColor = PoiIconRenderer.ParseColor(group.ColorHex);
                var groupSwatchBrush = new XSolidBrush(XColor.FromArgb(groupColor.Alpha, groupColor.Red, groupColor.Green, groupColor.Blue));
                double groupSwatchY = y + (groupRowH - iconSize) / 2.0;
                gfx.DrawRectangle(groupSwatchBrush, margin + 3, groupSwatchY, iconSize, iconSize);
                gfx.DrawRectangle(XPens.DimGray, margin + 3, groupSwatchY, iconSize, iconSize);
                double groupNameWidth = tableWidth - iconSize - 16;
                var (groupNameFont, groupNameText) = FitTextSingleLine(gfx, group.Name, groupFont, groupNameWidth - 2, 7);
                gfx.DrawString(groupNameText, groupNameFont, XBrushes.Black,
                    new XRect(margin + iconSize + 8, y, groupNameWidth, groupRowH), XStringFormats.CenterLeft);
                y += groupRowH;

                foreach (var line in groupDescLines)
                {
                    gfx.DrawString(line, descFont, XBrushes.Black,
                        new XRect(margin + iconSize + 8, y, groupDescWidth, descRowH), XStringFormats.TopLeft);
                    y += descRowH;
                }

                foreach (var item in items)
                {
                    bool hasItemDesc = !string.IsNullOrWhiteSpace(item.Description);
                    double descWidth = tableWidth - iconSize - 18;
                    var descLines = hasItemDesc ? WrapText(gfx!, item.Description, itemDescFont, descWidth) : new List<string>();
                    double neededItemH = itemRowH + descLines.Count * itemDescRowH;
                    if (y + neededItemH > h - margin)
                        NewPage();

                    double iconY = y + (itemRowH - iconSize * 0.85) / 2.0;
                    gfx!.DrawImage(GetIcon(item.Icon ?? PoiIconType.Pin, group.ColorHex), margin + 10, iconY, iconSize * 0.85, iconSize * 0.85);
                    double itemLabelWidth = tableWidth * 0.55;
                    var (itemLabelFont, itemLabelText) = FitTextSingleLine(gfx, item.Label, itemFont, itemLabelWidth - 2, 6.5);
                    gfx.DrawString(itemLabelText, itemLabelFont, XBrushes.Black,
                        new XRect(margin + iconSize + 14, y, itemLabelWidth, itemRowH), XStringFormats.CenterLeft);
                    string coords = $"{item.Lon:F4}°E  {item.Lat:F4}°N";
                    gfx.DrawString(coords, coordFont, XBrushes.Black,
                        new XRect(margin + tableWidth * 0.62, y, tableWidth * 0.38 - 4, itemRowH), XStringFormats.CenterLeft);
                    y += itemRowH;

                    foreach (var line in descLines)
                    {
                        gfx.DrawString(line, itemDescFont, XBrushes.Black,
                            new XRect(margin + iconSize + 14, y, descWidth, itemDescRowH), XStringFormats.TopLeft);
                        y += itemDescRowH;
                    }
                }

                y += 6; // spazio tra gruppi
            }

            return pageCount;
        }

        // Formatta una singola data: solo il giorno se l'orario è mezzanotte
        // esatta ("nessun orario"), altrimenti data+ora — delega alla
        // versione condivisa con l'albero (ItineraryOrdering).
        private static string FormatSingleDate(DateTime d) => ItineraryOrdering.FormatSingleDate(d);

        // Formatta l'intervallo Da/A di un percorso per il titolo nell'elenco
        // percorsi: "" se nessuna data, la sola data se solo una delle due è
        // impostata (o sono uguali), altrimenti "Da - A" — delega alla stessa
        // versione condivisa usata dal Piano di viaggio.
        private static string FormatRouteDateRange(DateTime? start, DateTime? end) =>
            ItineraryOrdering.FormatDateRange(start, end);

        // Disegna l'elenco dei percorsi (swatch colore + nome + descrizione,
        // lunghezza e numero di punti), in testa al documento, dopo l'elenco
        // POI e prima dell'indice. Crea automaticamente tutte le pagine
        // necessarie (nessun troncamento). Ritorna il numero di pagine create.
        // `routeFilter` (se presente) esclude i percorsi già mostrati per
        // intero nel "Piano di viaggio".
        private int DrawPercorsiListPages(PdfDocument pdfDoc, List<Percorso> percorsi,
            double pageWidthMm, double pageHeightMm, Func<Percorso, bool>? routeFilter = null)
        {
            const double margin       = 24;
            const double rowH         = 20;
            const double descRowH     = 12;
            const double swatchSz     = 12;
            const double poiItemRowH  = 16;
            const double poiDescRowH  = 10;
            const double poiIconSize  = 14;

            var titleFont    = new XFont("Arial", 16, XFontStyle.Bold);
            var nameFont     = new XFont("Arial", 10, XFontStyle.Bold);
            var descFont     = new XFont("Arial", 8);
            var metaFont     = new XFont("Arial", 8);
            var poiItemFont  = new XFont("Arial", 9);
            var poiDescFont  = new XFont("Arial", 6.5);
            var poiCoordFont = new XFont("Arial", 8);

            // Icone dei punti-POI dei percorsi: cache per (routeId, pointIndex)
            // dato che colore/icona possono differire punto per punto (icona
            // scelta dall'utente) pur condividendo sempre il colore del
            // percorso — stesso meccanismo di cache XImage di DrawPoiListPages.
            var poiIconCache = new Dictionary<string, XImage>();
            XImage GetRoutePoiIcon(Percorso route, GeoPoint point)
            {
                string key = $"{route.Id}_{point.PoiIcon}";
                if (poiIconCache.TryGetValue(key, out var cached)) return cached;
                using var bmp = PoiIconRenderer.RenderToBitmap(point.PoiIcon, PercorsoRenderer.ParseColor(route.ColorHex), 48);
                using var ms  = new MemoryStream();
                bmp.Encode(ms, SKEncodedImageFormat.Png, 100);
                ms.Position = 0;
                var xImg = XImage.FromStream(() => new MemoryStream(ms.ToArray()));
                poiIconCache[key] = xImg;
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

                string title = pageCount == 1 ? "Percorsi" : "Percorsi (segue)";
                gfx.DrawString(title, titleFont, XBrushes.Black,
                    new XRect(0, 18, w, 26), XStringFormats.TopCenter);

                y = 18 + 40;
            }

            NewPage();

            foreach (var r in percorsi)
            {
                if (routeFilter != null && !routeFilter(r)) continue;

                bool hasDesc = !string.IsNullOrWhiteSpace(r.Description);
                double descWidth = tableWidth - swatchSz - 18;
                var descLines = hasDesc ? WrapText(gfx!, r.Description, descFont, descWidth) : new List<string>();
                double neededH = rowH + descLines.Count * descRowH;
                if (y + neededH > h - margin)
                    NewPage();

                gfx!.DrawRectangle(new XSolidBrush(XColor.FromArgb(230, 238, 250)), margin, y, tableWidth, rowH);

                var color = PercorsoRenderer.ParseColor(r.ColorHex);
                var swatchBrush = new XSolidBrush(XColor.FromArgb(color.Alpha, color.Red, color.Green, color.Blue));
                double swatchY = y + (rowH - swatchSz) / 2.0;
                gfx.DrawRectangle(swatchBrush, margin + 6, swatchY, swatchSz, swatchSz);
                gfx.DrawRectangle(XPens.DimGray, margin + 6, swatchY, swatchSz, swatchSz);

                // Se il percorso ha una data (Da e/o A), va prima del nome —
                // stessa convenzione già usata nell'albero di navigazione
                // (MainWindow.FormatItineraryDate) per POI/percorsi datati.
                string routeTitle = FormatRouteDateRange(r.StartDateTime, r.EndDateTime) is { Length: > 0 } dateRange
                    ? $"{dateRange}  {r.Label}" : r.Label;
                double routeTitleWidth = tableWidth * 0.55 - swatchSz - 14;
                var (routeTitleFont, routeTitleText) = FitTextSingleLine(gfx, routeTitle, nameFont, routeTitleWidth - 2, 7);
                gfx.DrawString(routeTitleText, routeTitleFont, XBrushes.Black,
                    new XRect(margin + swatchSz + 14, y, routeTitleWidth, rowH), XStringFormats.CenterLeft);

                double lengthKm = PercorsoRenderer.LengthKm(r);
                string meta = $"{lengthKm:0.##} km  ·  {r.Points.Count} punti";
                gfx.DrawString(meta, metaFont, XBrushes.Black,
                    new XRect(margin + tableWidth * 0.62, y, tableWidth * 0.38 - 6, rowH), XStringFormats.CenterLeft);

                y += rowH;

                foreach (var line in descLines)
                {
                    gfx.DrawString(line, descFont, XBrushes.Black,
                        new XRect(margin + swatchSz + 14, y, descWidth, descRowH), XStringFormats.TopLeft);
                    y += descRowH;
                }

                // Punti del percorso marcati come POI: elencati annidati sotto
                // il percorso stesso (non nella pagina POI separata, restano
                // "dentro la definizione del percorso" come richiesto) — stesso
                // layout icona/etichetta/coordinate/descrizione di DrawPoiListPages.
                foreach (var point in r.Points)
                {
                    if (!point.IsPoi) continue;

                    bool hasPointDesc = !string.IsNullOrWhiteSpace(point.PoiDescription);
                    double pointDescWidth = tableWidth - poiIconSize - 20;
                    var pointDescLines = hasPointDesc ? WrapText(gfx!, point.PoiDescription, poiDescFont, pointDescWidth) : new List<string>();
                    double neededPointH = poiItemRowH + pointDescLines.Count * poiDescRowH;
                    if (y + neededPointH > h - margin)
                        NewPage();

                    double iconY = y + (poiItemRowH - poiIconSize * 0.85) / 2.0;
                    gfx!.DrawImage(GetRoutePoiIcon(r, point), margin + swatchSz + 16, iconY, poiIconSize * 0.85, poiIconSize * 0.85);
                    double pointLabelWidth = tableWidth * 0.55;
                    var (pointLabelFont, pointLabelText) = FitTextSingleLine(gfx, point.PoiLabel, poiItemFont, pointLabelWidth - 2, 6.5);
                    gfx.DrawString(pointLabelText, pointLabelFont, XBrushes.Black,
                        new XRect(margin + swatchSz + poiIconSize + 20, y, pointLabelWidth, poiItemRowH), XStringFormats.CenterLeft);
                    string pointCoords = $"{point.Lon:F4}°E  {point.Lat:F4}°N";
                    gfx.DrawString(pointCoords, poiCoordFont, XBrushes.Black,
                        new XRect(margin + tableWidth * 0.62, y, tableWidth * 0.38 - 4, poiItemRowH), XStringFormats.CenterLeft);
                    y += poiItemRowH;

                    foreach (var line in pointDescLines)
                    {
                        gfx.DrawString(line, poiDescFont, XBrushes.Black,
                            new XRect(margin + swatchSz + poiIconSize + 20, y, pointDescWidth, poiDescRowH), XStringFormats.TopLeft);
                        y += poiDescRowH;
                    }
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

        // Calcola il GeoRect che racchiude pagine, POI e percorsi (con un
        // margine del 10%) — la mappa riassuntiva deve includere anche
        // POI/percorsi nel suo inquadramento, non solo le pagine mappa,
        // richiesta esplicita dell'utente: prima un POI/percorso fuori
        // dall'area coperta dalle pagine restava fuori campo nella mappa
        // riassuntiva anche se compariva nel gazetteer. Serve anche per il
        // caso "0 pagine ma POI/percorsi presenti" (v. GenerateAsync): senza
        // pagine, i confini derivano interamente da POI/percorsi. Ritorna
        // null solo se non c'è proprio nulla da inquadrare (pagine, POI e
        // percorsi tutti vuoti).
        private static GeoRect? CalcOverallBounds(List<MapPage> pages, List<PoiGroup> poiGroups, List<Percorso> percorsi)
        {
            var lons = new List<double>();
            var lats = new List<double>();

            foreach (var p in pages)
            {
                lons.Add(p.GeoBounds.MinLon); lons.Add(p.GeoBounds.MaxLon);
                lats.Add(p.GeoBounds.MinLat); lats.Add(p.GeoBounds.MaxLat);
            }
            foreach (var g in poiGroups)
                foreach (var item in g.Items)
                {
                    lons.Add(item.Lon);
                    lats.Add(item.Lat);
                }
            foreach (var r in percorsi)
                foreach (var pt in r.Points)
                {
                    lons.Add(pt.Lon);
                    lats.Add(pt.Lat);
                }

            if (lons.Count == 0) return null;

            double minLon = lons.Min(), maxLon = lons.Max();
            double minLat = lats.Min(), maxLat = lats.Max();

            // Padding 10% su ogni lato; con un solo punto (span zero, es. un
            // singolo POI senza pagine/percorsi) un padding proporzionale
            // resterebbe zero — margine minimo fisso (~1km) per evitare un
            // rettangolo degenere che manderebbe in errore il calcolo dello zoom.
            double padLon = Math.Max((maxLon - minLon) * 0.10, 0.01);
            double padLat = Math.Max((maxLat - minLat) * 0.10, 0.01);

            return new GeoRect
            {
                MinLon = minLon - padLon,
                MaxLon = maxLon + padLon,
                MinLat = minLat - padLat,
                MaxLat = maxLat + padLat
            };
        }

        // Scarica i tile per la mappa riassuntiva al livello di zoom corretto
        // per far vedere l'intero insieme di pagine/POI/percorsi in una sola immagine.
        private async Task<SKBitmap?> RenderOverviewAsync(List<MapPage> pages, List<PoiGroup> poiGroups, List<Percorso> percorsi, StradarioSettings settings)
        {
            var (pageWidthMm, pageHeightMm) = settings.GetPageDimensionsMm();
            double mapWidthMm  = pageWidthMm  - 2 * BorderMarginMm;
            double mapHeightMm = pageHeightMm - 2 * BorderMarginMm;

            int pixW = (int)(mapWidthMm  / 25.4 * settings.Dpi);
            int pixH = (int)(mapHeightMm / 25.4 * settings.Dpi);

            // Nessun dato affatto (0 pagine, 0 POI, 0 percorsi): nessun
            // inquadramento possibile, niente da scaricare.
            if (CalcOverallBounds(pages, poiGroups, percorsi) is not { } bounds)
                return null;

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
            List<MapPage> pages, List<PoiGroup> poiGroups, List<Percorso> percorsi, string projectName, StradarioSettings settings)
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

            // Nessun dato affatto: niente rettangoli/percorsi da disegnare
            // sopra il placeholder grigio già tracciato sopra.
            if (CalcOverallBounds(pages, poiGroups, percorsi) is not { } bounds)
                return;

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

                // Label centrata con ombra — rimpicciolita/troncata per
                // entrare nel rettangolo pagina (spesso piccolo sulla mappa
                // riassuntiva): a font fisso un'etichetta lunga usciva dal
                // rettangolo senza alcun adattamento.
                if (rw > 10 && rh > 8)
                {
                    var labelRect = new XRect(x1 + 1, y1 + 1, rw - 2, rh - 2);
                    var (pageLabelFont, pageLabelText) = FitTextSingleLine(gfx, page.Label, labelFont, rw - 2, 4.5);
                    gfx.DrawString(pageLabelText, pageLabelFont, shadowBrush, labelRect, XStringFormats.Center);
                    gfx.DrawString(pageLabelText, pageLabelFont, XBrushes.Black, labelRect, XStringFormats.Center);
                }

                // Numero di pagina in basso a destra (se il rettangolo è abbastanza grande)
                if (rw > 22 && rh > 14)
                    gfx.DrawString($"p.{page.PageNumber}", numFont, XBrushes.Black,
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
                {
                    var (rlFont, rlText) = FitTextSingleLine(gfx, route.Label, routeLabelFont, 78, 5.5);
                    gfx.DrawString(rlText, rlFont, shadowBrush,
                        new XRect(ptsPdf[0].px + 2, ptsPdf[0].py - 8, 80, 9), XStringFormats.TopLeft);
                }

                // Punti marcati come POI: la pagina riassuntiva è puramente
                // vettoriale (nessun PoiIconRenderer/bitmap qui), quindi un
                // cerchietto pieno + etichetta breve, stesso stile minimale
                // già usato per route.Label sopra.
                var poiBrush = new XSolidBrush(XColor.FromArgb(color.Alpha, color.Red, color.Green, color.Blue));
                for (int i = 0; i < route.Points.Count; i++)
                {
                    if (!route.Points[i].IsPoi) continue;
                    var (px, py) = ptsPdf[i];
                    gfx.DrawEllipse(poiBrush, px - 2.2, py - 2.2, 4.4, 4.4);
                    gfx.DrawEllipse(XPens.White, px - 2.2, py - 2.2, 4.4, 4.4);
                    if (!string.IsNullOrWhiteSpace(route.Points[i].PoiLabel))
                    {
                        var (plFont, plText) = FitTextSingleLine(gfx, route.Points[i].PoiLabel, routeLabelFont, 78, 5.5);
                        gfx.DrawString(plText, plFont, shadowBrush,
                            new XRect(px + 3, py - 8, 80, 9), XStringFormats.TopLeft);
                    }
                }
            }

            // POI "liberi" (gruppi standalone, non i punti-POI inline dei
            // percorsi già disegnati sopra) — prima erano inclusi solo
            // nell'inquadramento (v. CalcOverallBounds) ma non disegnati:
            // richiesta esplicita dell'utente. Stesso stile minimale
            // (cerchietto pieno + etichetta) essendo la riassuntiva
            // puramente vettoriale, nessun PoiIconRenderer/bitmap qui.
            foreach (var group in poiGroups)
            {
                var color = settings.PdfContrastMode == PdfContrastMode.BlackWhite
                    ? SKColors.Black : PoiIconRenderer.ParseColor(group.ColorHex);
                var poiBrush = new XSolidBrush(XColor.FromArgb(color.Alpha, color.Red, color.Green, color.Blue));
                foreach (var item in group.Items)
                {
                    var (px, py) = GeoToPdf(item.Lon, item.Lat);
                    gfx.DrawEllipse(poiBrush, px - 2.5, py - 2.5, 5, 5);
                    gfx.DrawEllipse(XPens.White, px - 2.5, py - 2.5, 5, 5);
                    if (!string.IsNullOrWhiteSpace(item.Label))
                    {
                        var (ilFont, ilText) = FitTextSingleLine(gfx, item.Label, routeLabelFont, 78, 5.5);
                        gfx.DrawString(ilText, ilFont, shadowBrush,
                            new XRect(px + 3, py - 8, 80, 9), XStringFormats.TopLeft);
                    }
                }
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
                    zoom, tileSizePx, pixW, pixH, percorsi, poiGroups, forceBlackWhite);

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
            List<Percorso> percorsi, List<PoiGroup> poiGroups, bool forceBlackWhite = false)
        {
            using var canvas = new SKCanvas(bitmap);

            (double x, double y) Project(double lon, double lat) =>
                GeoUtils.GeoToBitmapPixel(lon, lat, centerLon, centerLat, zoom, tileSizePx, pixW, pixH);

            // Punti da evitare per l'etichetta del percorso: tutti i POI di
            // questa pagina (vedi PercorsoRenderer.Draw) — coprono anche il
            // caso di un percorso che coincide con un gruppo di POI.
            var avoid = poiGroups.SelectMany(g => g.Items).Select(it => (it.Lon, it.Lat)).ToList();

            foreach (var route in percorsi)
                PercorsoRenderer.Draw(canvas, route, Project,
                    colorOverride: forceBlackWhite ? SKColors.Black : (SKColor?)null,
                    avoidLabelNear: avoid);
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

                    PoiIconRenderer.DrawWithLabel(canvas, item.Icon ?? PoiIconType.Pin, color, item.Label,
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
            var borderFont = new XFont("Arial", 7, XFontStyle.Bold);
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
            gfx.DrawString(coords, coordFont, XBrushes.Black,
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
                tickFont, XBrushes.Black,
                new XRect(x0, y0 + barH + 1, barLenPt + 70, captionRowH), XStringFormats.TopLeft);
        }

        // Disegna una striscia di bordo con sfondo grigio chiaro e testo centrato.
        // Il riferimento pagina adiacente ("▲ pag. N  Etichetta") è testo
        // arbitrario dell'utente: a font fisso poteva uscire dai bordi della
        // striscia con etichette lunghe (nessun controllo prima) — ora il
        // font si rimpicciolisce (fino a MinBorderFontSize) per entrare su
        // una riga, e se anche così non basta viene troncato con "…".
        private const double MinBorderFontSize = 5.5;

        private void DrawBorderStrip(XGraphics gfx, double x, double y, double w, double h,
            string text, XFont font, bool isVertical)
        {
            gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(230, 230, 230)), x, y, w, h);
            gfx.DrawRectangle(XPens.Gray, x, y, w, h);

            // Dopo la rotazione di 90° per i bordi laterali, il testo scorre
            // lungo `h` (l'altezza della pagina, ampia) — la larghezza
            // disponibile per il fit è quindi h per i bordi verticali, w per
            // quelli orizzontali (dove il testo non ruota).
            double availableWidth = (isVertical ? h : w) - 12;
            var (fitFont, fitText) = FitTextSingleLine(gfx, text, font, availableWidth, MinBorderFontSize);

            if (isVertical)
            {
                // Testo ruotato 90° per i bordi laterali
                gfx.Save();
                gfx.TranslateTransform(x + w / 2, y + h / 2);
                gfx.RotateTransform(-90);
                gfx.DrawString(fitText, fitFont, XBrushes.Black,
                    new XRect(-h / 2, -w / 2, h, w), XStringFormats.Center);
                gfx.Restore();
            }
            else
            {
                gfx.DrawString(fitText, fitFont, XBrushes.Black,
                    new XRect(x, y, w, h), XStringFormats.Center);
            }
        }
    }
}
