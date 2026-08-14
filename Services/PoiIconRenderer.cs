// =============================================================================
// Services/PoiIconRenderer.cs
//
// SINOSSI: Disegno delle icone dei gruppi di POI, condiviso da:
//   - la mappa interattiva dell'app (MapRenderer.DrawPois)
//   - le mappe stampate nel PDF (PdfGenerator, disegnate direttamente sul
//     bitmap SkiaSharp della pagina prima di essere incorporate come immagine)
//   - l'elenco POI a inizio PDF (icona renderizzata su bitmap piccolo)
//   - il file KMZ esportato (icona incorporata come PNG)
//   - l'anteprima nel selettore icona della UI
//
//   Ogni icona è un pin (goccia) del colore del gruppo con un glifo bianco
//   disegnato con primitive vettoriali SkiaSharp (path, cerchi, rettangoli):
//   NON si usano glifi di testo/emoji, per garantire lo stesso aspetto identico
//   su ogni piattaforma e in ogni output (evita i problemi di font mancanti
//   già documentati per PdfSharpCore/FontResolver su Linux).
// =============================================================================

using System;
using System.Collections.Generic;
using SkiaSharp;
using StradarioApp.Models;

namespace StradarioApp.Services
{
    public static class PoiIconRenderer
    {
        // Palette di colori curati proposti nel selettore colore della UI
        public static readonly string[] Palette = new[]
        {
            "#E53935", "#1E88E5", "#43A047", "#FB8C00", "#8E24AA",
            "#00897B", "#6D4C41", "#3949AB", "#D81B60", "#546E7A"
        };

        public const string DefaultColorHex = "#1E88E5";

        public static SKColor ParseColor(string hex)
        {
            if (!string.IsNullOrWhiteSpace(hex) && SKColor.TryParse(hex, out var c))
                return c;
            return SKColor.Parse(DefaultColorHex);
        }

        // Disegna il pin del gruppo: (cx, cy) è la punta, cioè il punto
        // geografico esatto; "size" è l'altezza complessiva del pin in pixel.
        public static void Draw(SKCanvas canvas, PoiIconType type, SKColor color,
            float cx, float cy, float size)
        {
            float r      = size * 0.32f;
            float bodyCy = cy - r * 1.5f;

            using var fill   = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Fill };
            using var border = new SKPaint
            {
                Color = new SKColor(0, 0, 0, 130), IsAntialias = true,
                Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(0.8f, size * 0.035f)
            };
            using var glyph = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Fill };

            // Coda del pin, verso il punto geografico esatto
            using (var tail = new SKPath())
            {
                float tailHalf = r * 0.55f;
                tail.MoveTo(cx - tailHalf, bodyCy + r * 0.6f);
                tail.LineTo(cx + tailHalf, bodyCy + r * 0.6f);
                tail.LineTo(cx, cy);
                tail.Close();
                canvas.DrawPath(tail, fill);
            }

            // Corpo circolare del pin
            canvas.DrawCircle(cx, bodyCy, r, fill);
            canvas.DrawCircle(cx, bodyCy, r, border);

            // Glifo bianco specifico per tipo
            float gr = r * 0.55f;
            switch (type)
            {
                case PoiIconType.Star:       DrawStar(canvas, cx, bodyCy, gr, glyph);        break;
                case PoiIconType.Flag:       DrawFlag(canvas, cx, bodyCy, gr, glyph);        break;
                case PoiIconType.Home:       DrawHouse(canvas, cx, bodyCy, gr, glyph);       break;
                case PoiIconType.Church:     DrawLatinCross(canvas, cx, bodyCy, gr, glyph);  break;
                case PoiIconType.Monument:   DrawMonument(canvas, cx, bodyCy, gr, glyph);    break;
                case PoiIconType.Restaurant: DrawCutlery(canvas, cx, bodyCy, gr, glyph);     break;
                case PoiIconType.Cafe:       DrawCup(canvas, cx, bodyCy, gr, glyph);         break;
                case PoiIconType.Shop:       DrawBag(canvas, cx, bodyCy, gr, glyph);         break;
                case PoiIconType.Parking:    DrawParkingBadge(canvas, cx, bodyCy, gr, glyph);break;
                case PoiIconType.Hospital:   DrawGreekCross(canvas, cx, bodyCy, gr, glyph);  break;
                case PoiIconType.Hotel:      DrawBed(canvas, cx, bodyCy, gr, glyph);         break;
                case PoiIconType.Viewpoint:  DrawViewpoint(canvas, cx, bodyCy, gr, glyph);   break;
                case PoiIconType.Camping:    DrawTent(canvas, cx, bodyCy, gr, glyph);        break;
                case PoiIconType.Fountain:   DrawDroplet(canvas, cx, bodyCy, gr, glyph);     break;
                case PoiIconType.Info:       DrawInfo(canvas, cx, bodyCy, gr, glyph);        break;
                case PoiIconType.Pin:
                default:
                    canvas.DrawCircle(cx, bodyCy, gr * 0.5f, glyph);
                    break;
            }
        }

        // Icona + etichetta con ombra, usata sia sulla mappa interattiva che
        // sui bitmap delle pagine mappa del PDF.
        // `occupiedLabelRects`/`useForcedPosition`+`forcedPosition` abilitano
        // il decluttering automatico usato solo in stampa (PdfGenerator): se
        // `useForcedPosition` è true, la posizione (o l'assenza di etichetta,
        // se null) è già stata decisa altrove (usato per i POI di gruppo, la
        // cui etichetta va sempre piazzata PRIMA di quelle dei percorsi —
        // vedi PdfGenerator.RenderMapPageAsync); altrimenti, se
        // `occupiedLabelRects` non è null, la posizione viene scelta ora fra
        // più candidate (vedi TryPlaceLabel) rispetto a quelle già occupate
        // nella lista condivisa. In tutti i casi l'icona resta sempre
        // disegnata: solo l'etichetta può essere omessa (mai su richiesta
        // esplicita: vedi `alwaysShow`).
        // `alwaysShow`: usato dalla mappa INTERATTIVA (MapRenderer.DrawPois,
        // insieme a `occupiedLabelRects` condiviso fra tutti i POI dello
        // stesso frame) — sceglie comunque la migliore fra le 4 posizioni
        // candidate (vedi ChooseLabelPosition) ma non nasconde mai
        // l'etichetta anche se nessuna è libera: richiesta esplicita
        // dell'utente ("la regola del destra/sinistra... facendole tutte
        // comunque le etichette") dopo aver visto il decluttering funzionare
        // bene in stampa — sulla mappa interattiva, dove si può comunque
        // sempre zoomare per separare i marker, nascondere un'etichetta
        // sarebbe più fastidioso che utile.
        public static void DrawWithLabel(SKCanvas canvas, PoiIconType type, SKColor color,
            string label, float x, float y, float size,
            List<SKRect>? occupiedLabelRects = null,
            bool useForcedPosition = false, (float tx, float ty, float textSize)? forcedPosition = null,
            IReadOnlyList<(float x1, float y1, float x2, float y2)>? avoidLines = null,
            bool alwaysShow = false, bool skipLabel = false)
        {
            Draw(canvas, type, color, x, y, size);
            // L'icona si disegna comunque (sono comunque marker distinti,
            // spesso di colore diverso perché appartengono a percorsi/gruppi
            // diversi) — solo il testo va evitato quando un altro POI nello
            // STESSO punto ha già stampato la STESSA etichetta (v. chiamanti,
            // che calcolano skipLabel confrontando lon/lat/testo).
            if (skipLabel || string.IsNullOrWhiteSpace(label)) return;

            (float tx, float ty, float textSize)? pos = useForcedPosition ? forcedPosition
                : occupiedLabelRects != null && alwaysShow ? ChooseLabelPosition(label, x, y, size, occupiedLabelRects)
                : occupiedLabelRects != null ? TryPlaceLabel(label, x, y, size, occupiedLabelRects, avoidLines)
                : (x + size * 0.42f, y - size * 0.75f, Math.Max(9f, size * 0.5f));
            if (pos is not { } p) return;

            DrawHaloText(canvas, label, p.tx, p.ty, p.textSize);
        }

        // Disegna testo con un vero contorno bianco tutt'intorno (non una
        // singola ombra sfalsata di 1px, come in una versione precedente):
        // segnalato dall'utente come illeggibile sopra una mappa OSM densa,
        // dove il lato del glifo OPPOSTO alla direzione dell'ombra non aveva
        // alcun alone e si confondeva con lo sfondo. Un vero stroke bianco
        // (Style=Stroke, dietro al riempimento nero) resta leggibile in
        // qualunque direzione. Condiviso da PoiIconRenderer (marker) e
        // PercorsoRenderer (etichetta del percorso).
        public static void DrawHaloText(SKCanvas canvas, string text, float x, float y, float textSize)
        {
            using var outline = new SKPaint
            {
                Color = SKColors.White, IsAntialias = true, TextSize = textSize, FakeBoldText = true,
                Style = SKPaintStyle.Stroke, StrokeWidth = textSize * 0.30f,
                StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round
            };
            using var fill = new SKPaint
            {
                Color = SKColors.Black, IsAntialias = true, TextSize = textSize, FakeBoldText = true,
                Style = SKPaintStyle.Fill
            };
            canvas.DrawText(text, x, y, outline);
            canvas.DrawText(text, x, y, fill);
        }

        private static bool RectsOverlap(SKRect a, SKRect b) =>
            a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;

        // Vero se il segmento (x1,y1)-(x2,y2) attraversa (anche solo di
        // striscio) il rettangolo r — usato per preferire, fra le posizioni
        // candidate di un'etichetta, quelle che non attraversano la linea di
        // un percorso ("se possibile", richiesta esplicita dell'utente).
        private static bool SegmentIntersectsRect(float x1, float y1, float x2, float y2, SKRect r)
        {
            if (Math.Max(x1, x2) < r.Left || Math.Min(x1, x2) > r.Right ||
                Math.Max(y1, y2) < r.Top  || Math.Min(y1, y2) > r.Bottom)
                return false;
            if (r.Contains(x1, y1) || r.Contains(x2, y2)) return true;
            return SegmentsIntersect(x1, y1, x2, y2, r.Left,  r.Top,    r.Right, r.Top)
                || SegmentsIntersect(x1, y1, x2, y2, r.Right, r.Top,    r.Right, r.Bottom)
                || SegmentsIntersect(x1, y1, x2, y2, r.Right, r.Bottom, r.Left,  r.Bottom)
                || SegmentsIntersect(x1, y1, x2, y2, r.Left,  r.Bottom, r.Left,  r.Top);
        }

        private static bool SegmentsIntersect(
            float ax1, float ay1, float ax2, float ay2,
            float bx1, float by1, float bx2, float by2)
        {
            float d1 = Cross(bx2 - bx1, by2 - by1, ax1 - bx1, ay1 - by1);
            float d2 = Cross(bx2 - bx1, by2 - by1, ax2 - bx1, ay2 - by1);
            float d3 = Cross(ax2 - ax1, ay2 - ay1, bx1 - ax1, by1 - ay1);
            float d4 = Cross(ax2 - ax1, ay2 - ay1, bx2 - ax1, by2 - ay1);
            return (d1 > 0) != (d2 > 0) && (d3 > 0) != (d4 > 0);
        }

        private static float Cross(float ax, float ay, float bx, float by) => ax * by - ay * bx;

        // Prova PIÙ posizioni candidate per l'etichetta di un marker (destra,
        // sinistra, sopra, sotto — in quest'ordine di preferenza estetica,
        // "destra" è la posizione storica/di default) rispetto a quelle già
        // occupate in `occupied`: la prima che non si sovrappone viene
        // riservata (aggiunta alla lista) e la sua posizione ritornata;
        // "meglio un'etichetta spostata che nessuna", richiesta esplicita
        // dell'utente dopo aver visto troppe etichette sparire del tutto su
        // un progetto molto denso. Ritorna null se nessuna delle 4 posizioni
        // è libera. I chiamanti con priorità più alta vanno invocati per
        // primi (vedi PdfGenerator.RenderMapPageAsync).
        // `avoidLines`: segmenti (in pixel) delle linee dei percorsi già
        // disegnate — fra le posizioni libere da altre etichette, si
        // preferisce quella che NON attraversa una linea, se ce n'è una;
        // altrimenti si accetta comunque la prima libera da altre etichette
        // (un'etichetta sopra una linea resta più leggibile — ha comunque il
        // proprio alone bianco — di nessuna etichetta affatto).
        public static (float tx, float ty, float textSize)? TryPlaceLabel(string label, float x, float y, float size,
            List<SKRect> occupied, IReadOnlyList<(float x1, float y1, float x2, float y2)>? avoidLines = null)
        {
            if (string.IsNullOrWhiteSpace(label)) return null;
            float textSize = Math.Max(9f, size * 0.5f);
            using var measure = new SKPaint { TextSize = textSize, FakeBoldText = true };
            float textWidth = measure.MeasureText(label);
            var candidates = LabelCandidates(x, y, size, textWidth);

            (float tx, float ty, SKRect rect)? firstFreeOfLabels = null;
            foreach (var (tx, ty) in candidates)
            {
                var rect = new SKRect(tx, ty - textSize, tx + textWidth, ty + textSize * 0.3f);
                bool overlapsLabel = false;
                foreach (var r in occupied)
                    if (RectsOverlap(r, rect)) { overlapsLabel = true; break; }
                if (overlapsLabel) continue;

                firstFreeOfLabels ??= (tx, ty, rect);

                bool overlapsLine = false;
                if (avoidLines != null)
                    foreach (var l in avoidLines)
                        if (SegmentIntersectsRect(l.x1, l.y1, l.x2, l.y2, rect)) { overlapsLine = true; break; }

                if (!overlapsLine)
                {
                    occupied.Add(rect);
                    return (tx, ty, textSize);
                }
            }
            if (firstFreeOfLabels is { } f)
            {
                occupied.Add(f.rect);
                return (f.tx, f.ty, textSize);
            }
            return null;
        }

        private static (float tx, float ty)[] LabelCandidates(float x, float y, float size, float textWidth) => new[]
        {
            (x + size * 0.42f,             y - size * 0.75f), // destra, sopra (default storico)
            (x - size * 0.42f - textWidth, y - size * 0.75f), // sinistra, sopra
            (x - textWidth / 2f,           y - size * 0.9f),  // sopra, centrata
            (x - textWidth / 2f,           y + size * 0.55f), // sotto, centrata
        };

        // Come TryPlaceLabel, ma non nasconde MAI l'etichetta: se nessuna
        // delle 4 posizioni candidate è del tutto libera dalle altre già
        // occupate, sceglie quella con la sovrapposizione MINORE (area totale
        // di sovrapposizione più piccola) — non semplicemente la prima
        // (destra) a prescindere, come una versione precedente. BUG REALE
        // trovato dall'utente rivedendo la mappa interattiva ("due etichette
        // a destra che si sovrappongono per metà altezza... una poteva
        // andare a sinistra e si leggeva tutto"): con 4 candidate tutte
        // parzialmente occupate, il vecchio codice tornava comunque sempre a
        // destra anche quando sinistra/sopra/sotto si sovrapponevano molto
        // meno (o niente affatto con lo stesso vicino) — verificato passo
        // passo con un cluster sintetico densissimo, dove "destra" si
        // sovrapponeva a un vicino mentre "sinistra" si sovrapponeva SOLO a
        // un altro vicino diverso, ma con area minore.
        // Usata dalla mappa interattiva (MapRenderer.DrawPois): "la regola
        // del destra/sinistra... facendole tutte comunque le etichette",
        // richiesta esplicita dell'utente — a differenza della stampa, qui
        // non ha senso far sparire un'etichetta (si può sempre zoomare per
        // separare i marker), basta sceglierle la posizione meno peggio.
        public static (float tx, float ty, float textSize) ChooseLabelPosition(string label, float x, float y, float size,
            List<SKRect> occupied)
        {
            float textSize = Math.Max(9f, size * 0.5f);
            using var measure = new SKPaint { TextSize = textSize, FakeBoldText = true };
            float textWidth = measure.MeasureText(label);
            var candidates = LabelCandidates(x, y, size, textWidth);

            (float tx, float ty, SKRect rect)? bestOverlapping = null;
            float bestOverlapArea = float.MaxValue;

            foreach (var (tx, ty) in candidates)
            {
                var rect = new SKRect(tx, ty - textSize, tx + textWidth, ty + textSize * 0.3f);
                float overlapArea = 0f;
                foreach (var r in occupied)
                    overlapArea += OverlapArea(r, rect);

                if (overlapArea == 0f)
                {
                    occupied.Add(rect);
                    return (tx, ty, textSize);
                }
                if (overlapArea < bestOverlapArea)
                {
                    bestOverlapArea = overlapArea;
                    bestOverlapping = (tx, ty, rect);
                }
            }

            var best = bestOverlapping!.Value;
            occupied.Add(best.rect);
            return (best.tx, best.ty, textSize);
        }

        // Area (px²) di sovrapposizione fra due rettangoli — 0 se non si
        // toccano. Usata da ChooseLabelPosition per scegliere la posizione
        // "meno peggio" quando nessuna candidata è del tutto libera.
        private static float OverlapArea(SKRect a, SKRect b)
        {
            float left   = Math.Max(a.Left, b.Left);
            float right  = Math.Min(a.Right, b.Right);
            float top    = Math.Max(a.Top, b.Top);
            float bottom = Math.Min(a.Bottom, b.Bottom);
            if (right <= left || bottom <= top) return 0f;
            return (right - left) * (bottom - top);
        }

        // Renderizza la sola icona (senza etichetta) su un bitmap trasparente
        // quadrato: usato per il PNG incorporato nel KMZ, le righe dell'elenco
        // POI nel PDF e le anteprime nel selettore icona della UI.
        public static SKBitmap RenderToBitmap(PoiIconType type, SKColor color, int pixelSize)
        {
            var bmp = new SKBitmap(pixelSize, pixelSize, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bmp);
            canvas.Clear(SKColors.Transparent);
            float size = pixelSize * 0.82f;
            Draw(canvas, type, color, pixelSize / 2f, pixelSize * 0.66f, size);
            return bmp;
        }

        // -----------------------------------------------------------------
        // Glifi vettoriali per tipo. Nessun testo/emoji: solo path, cerchi e
        // rettangoli SkiaSharp, per un aspetto identico su ogni piattaforma.
        // -----------------------------------------------------------------

        private static void DrawStar(SKCanvas canvas, float cx, float cy, float R, SKPaint paint)
        {
            using var path = new SKPath();
            float innerR = R * 0.42f;
            for (int i = 0; i < 10; i++)
            {
                double angle = Math.PI / 5 * i - Math.PI / 2;
                float rad = (i % 2 == 0) ? R : innerR;
                float x = cx + (float)(rad * Math.Cos(angle));
                float y = cy + (float)(rad * Math.Sin(angle));
                if (i == 0) path.MoveTo(x, y); else path.LineTo(x, y);
            }
            path.Close();
            canvas.DrawPath(path, paint);
        }

        private static void DrawFlag(SKCanvas canvas, float cx, float cy, float R, SKPaint paint)
        {
            canvas.DrawRect(SKRect.Create(cx - R * 0.7f, cy - R, R * 0.18f, R * 2f), paint);
            using var path = new SKPath();
            path.MoveTo(cx - R * 0.6f, cy - R);
            path.LineTo(cx + R,        cy - R * 0.45f);
            path.LineTo(cx - R * 0.6f, cy + R * 0.05f);
            path.Close();
            canvas.DrawPath(path, paint);
        }

        private static void DrawHouse(SKCanvas canvas, float cx, float cy, float R, SKPaint paint)
        {
            using var path = new SKPath();
            path.MoveTo(cx - R,       cy);
            path.LineTo(cx - R,       cy + R * 0.9f);
            path.LineTo(cx + R,       cy + R * 0.9f);
            path.LineTo(cx + R,       cy);
            path.LineTo(cx,           cy - R * 0.9f);
            path.Close();
            canvas.DrawPath(path, paint);
        }

        private static void DrawLatinCross(SKCanvas canvas, float cx, float cy, float R, SKPaint paint)
        {
            float armW = R * 0.34f;
            canvas.DrawRect(SKRect.Create(cx - armW / 2, cy - R,          armW,       R * 2.0f), paint);
            canvas.DrawRect(SKRect.Create(cx - R * 0.75f, cy - R * 0.35f, R * 1.5f,   armW),      paint);
        }

        private static void DrawGreekCross(SKCanvas canvas, float cx, float cy, float R, SKPaint paint)
        {
            float armW = R * 0.55f;
            canvas.DrawRect(SKRect.Create(cx - armW / 2, cy - R,        armW,   R * 2f), paint);
            canvas.DrawRect(SKRect.Create(cx - R,        cy - armW / 2, R * 2f, armW),   paint);
        }

        private static void DrawMonument(SKCanvas canvas, float cx, float cy, float R, SKPaint paint)
        {
            float baseW = R * 0.5f;
            canvas.DrawRect(SKRect.Create(cx - baseW / 2, cy - R * 0.2f, baseW, R * 1.3f), paint);
            using var top = new SKPath();
            top.MoveTo(cx - baseW / 2, cy - R * 0.2f);
            top.LineTo(cx + baseW / 2, cy - R * 0.2f);
            top.LineTo(cx,             cy - R);
            top.Close();
            canvas.DrawPath(top, paint);
        }

        private static void DrawCutlery(SKCanvas canvas, float cx, float cy, float R, SKPaint fillPaint)
        {
            using var stroke = new SKPaint
            {
                Color = fillPaint.Color, IsAntialias = true,
                Style = SKPaintStyle.Stroke, StrokeWidth = R * 0.28f, StrokeCap = SKStrokeCap.Round
            };
            canvas.DrawLine(cx - R * 0.7f, cy - R * 0.7f, cx + R * 0.7f, cy + R * 0.7f, stroke);
            canvas.DrawLine(cx + R * 0.7f, cy - R * 0.7f, cx - R * 0.7f, cy + R * 0.7f, stroke);
        }

        private static void DrawCup(SKCanvas canvas, float cx, float cy, float R, SKPaint fillPaint)
        {
            var body = SKRect.Create(cx - R * 0.55f, cy - R * 0.3f, R * 1.1f, R * 1.1f);
            canvas.DrawRoundRect(body, R * 0.15f, R * 0.15f, fillPaint);
            using var handleStroke = new SKPaint
            {
                Color = fillPaint.Color, IsAntialias = true,
                Style = SKPaintStyle.Stroke, StrokeWidth = R * 0.18f
            };
            var handleRect = SKRect.Create(cx + R * 0.3f, cy - R * 0.1f, R * 0.6f, R * 0.7f);
            canvas.DrawArc(handleRect, -90, 180, false, handleStroke);
        }

        private static void DrawBag(SKCanvas canvas, float cx, float cy, float R, SKPaint fillPaint)
        {
            using var path = new SKPath();
            path.MoveTo(cx - R * 0.65f, cy - R * 0.1f);
            path.LineTo(cx + R * 0.65f, cy - R * 0.1f);
            path.LineTo(cx + R * 0.5f,  cy + R * 0.9f);
            path.LineTo(cx - R * 0.5f,  cy + R * 0.9f);
            path.Close();
            canvas.DrawPath(path, fillPaint);

            using var handleStroke = new SKPaint
            {
                Color = fillPaint.Color, IsAntialias = true,
                Style = SKPaintStyle.Stroke, StrokeWidth = R * 0.14f
            };
            var handleRect = SKRect.Create(cx - R * 0.35f, cy - R * 0.75f, R * 0.7f, R * 0.7f);
            canvas.DrawArc(handleRect, 180, 180, false, handleStroke);
        }

        private static void DrawParkingBadge(SKCanvas canvas, float cx, float cy, float R, SKPaint paint)
        {
            canvas.DrawRect(SKRect.Create(cx - R * 0.45f, cy - R * 0.85f, R * 0.32f, R * 1.7f), paint);
            var bump = SKRect.Create(cx - R * 0.45f, cy - R * 0.85f, R * 0.85f, R * 0.85f);
            canvas.DrawRoundRect(bump, R * 0.3f, R * 0.3f, paint);
        }

        private static void DrawBed(SKCanvas canvas, float cx, float cy, float R, SKPaint paint)
        {
            var frame  = SKRect.Create(cx - R * 0.85f, cy - R * 0.1f,  R * 1.7f, R * 0.75f);
            var pillow = SKRect.Create(cx - R * 0.85f, cy - R * 0.55f, R * 0.55f, R * 0.5f);
            canvas.DrawRoundRect(frame,  R * 0.12f, R * 0.12f, paint);
            canvas.DrawRoundRect(pillow, R * 0.12f, R * 0.12f, paint);
        }

        private static void DrawViewpoint(SKCanvas canvas, float cx, float cy, float R, SKPaint paint)
        {
            canvas.DrawCircle(cx + R * 0.5f, cy - R * 0.75f, R * 0.22f, paint);
            using var m1 = new SKPath();
            m1.MoveTo(cx - R,          cy + R * 0.7f);
            m1.LineTo(cx - R * 0.15f,  cy - R * 0.55f);
            m1.LineTo(cx + R * 0.5f,   cy + R * 0.7f);
            m1.Close();
            canvas.DrawPath(m1, paint);
            using var m2 = new SKPath();
            m2.MoveTo(cx - R * 0.35f, cy + R * 0.7f);
            m2.LineTo(cx + R * 0.35f, cy - R * 0.15f);
            m2.LineTo(cx + R,         cy + R * 0.7f);
            m2.Close();
            canvas.DrawPath(m2, paint);
        }

        private static void DrawTent(SKCanvas canvas, float cx, float cy, float R, SKPaint paint)
        {
            using var path = new SKPath();
            path.MoveTo(cx - R,        cy + R * 0.8f);
            path.LineTo(cx,            cy - R);
            path.LineTo(cx + R,        cy + R * 0.8f);
            path.LineTo(cx + R * 0.4f, cy + R * 0.8f);
            path.LineTo(cx,            cy);
            path.LineTo(cx - R * 0.4f, cy + R * 0.8f);
            path.Close();
            canvas.DrawPath(path, paint);
        }

        private static void DrawDroplet(SKCanvas canvas, float cx, float cy, float R, SKPaint paint)
        {
            canvas.DrawCircle(cx, cy + R * 0.15f, R * 0.6f, paint);
            using var top = new SKPath();
            top.MoveTo(cx - R * 0.42f, cy - R * 0.05f);
            top.LineTo(cx + R * 0.42f, cy - R * 0.05f);
            top.LineTo(cx,             cy - R);
            top.Close();
            canvas.DrawPath(top, paint);
        }

        private static void DrawInfo(SKCanvas canvas, float cx, float cy, float R, SKPaint paint)
        {
            canvas.DrawCircle(cx, cy - R * 0.55f, R * 0.16f, paint);
            var stem = SKRect.Create(cx - R * 0.14f, cy - R * 0.15f, R * 0.28f, R * 1.0f);
            canvas.DrawRoundRect(stem, R * 0.1f, R * 0.1f, paint);
        }
    }
}
