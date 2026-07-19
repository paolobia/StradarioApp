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
        public static void DrawWithLabel(SKCanvas canvas, PoiIconType type, SKColor color,
            string label, float x, float y, float size)
        {
            Draw(canvas, type, color, x, y, size);
            if (string.IsNullOrWhiteSpace(label)) return;

            float textSize = Math.Max(9f, size * 0.5f);
            using var shadow = new SKPaint { Color = new SKColor(255, 255, 255, 210), IsAntialias = true, TextSize = textSize, FakeBoldText = true };
            using var text   = new SKPaint { Color = SKColors.Black,                  IsAntialias = true, TextSize = textSize, FakeBoldText = true };

            float tx = x + size * 0.42f;
            float ty = y - size * 0.75f;
            canvas.DrawText(label, tx + 1, ty + 1, shadow);
            canvas.DrawText(label, tx,     ty,     text);
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
