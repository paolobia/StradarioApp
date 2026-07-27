// =============================================================================
// Services/PercorsoRenderer.cs
//
// SINOSSI: Disegno condiviso dei percorsi (tracce), usato da:
//   - la mappa interattiva dell'app (MapRenderer)
//   - le mappe stampate nel PDF (PdfGenerator, disegnate sul bitmap della
//     pagina prima di essere incorporate come immagine)
//   Il metodo Draw riceve una funzione di proiezione geo→pixel così da
//   restare identico sia sul canvas interattivo (coordinate schermo) sia sul
//   bitmap ad alta risoluzione del PDF (coordinate bitmap), stesso principio
//   già usato da PoiIconRenderer per i marker dei POI.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;
using StradarioApp.Models;

namespace StradarioApp.Services
{
    public static class PercorsoRenderer
    {
        public const string DefaultColorHex = "#E53935";

        public static SKColor ParseColor(string hex) => PoiIconRenderer.ParseColor(
            string.IsNullOrWhiteSpace(hex) ? DefaultColorHex : hex);

        // Lunghezza totale del percorso in km (somma delle distanze fra punti consecutivi)
        public static double LengthKm(Percorso route)
        {
            double total = 0;
            for (int i = 1; i < route.Points.Count; i++)
                total += GeoUtils.DistanceKm(
                    route.Points[i - 1].Lon, route.Points[i - 1].Lat,
                    route.Points[i].Lon,     route.Points[i].Lat);
            return total;
        }

        // Disegna la traccia: alone bianco (leggibilità sopra i tile), linea
        // colorata, pallini ai vertici (più grandi su primo/ultimo punto) ed
        // etichetta accanto al primo punto. "project" converte lon/lat nel
        // sistema di coordinate del canvas di destinazione (schermo o bitmap).
        // "dashed" disegna la linea tratteggiata: usato per il percorso in
        // corso di disegno (non ancora confermato). "drawVertices=false"
        // omette i pallini: pensati per un percorso disegnato a mano (pochi
        // punti), su una geometria densa (es. un'alternativa OSRM con
        // centinaia di punti) diventerebbero una fila di pallini fitta e
        // illeggibile invece di una linea pulita — vedi MainWindow.DrawInstradaOverlay.
        public static void Draw(SKCanvas canvas, Percorso route,
            Func<double, double, (double x, double y)> project, bool dashed = false,
            SKColor? colorOverride = null, bool drawVertices = true)
        {
            if (route.Points.Count == 0) return;

            var color = colorOverride ?? ParseColor(route.ColorHex);
            var pts = route.Points.Select(p => project(p.Lon, p.Lat)).ToList();

            if (pts.Count >= 2)
            {
                using var path = new SKPath();
                path.MoveTo((float)pts[0].x, (float)pts[0].y);
                for (int i = 1; i < pts.Count; i++)
                    path.LineTo((float)pts[i].x, (float)pts[i].y);

                if (!dashed)
                {
                    using var halo = new SKPaint
                    {
                        Color = new SKColor(255, 255, 255, 200), IsAntialias = true,
                        Style = SKPaintStyle.Stroke, StrokeWidth = 6.5f,
                        StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round
                    };
                    canvas.DrawPath(path, halo);
                }

                using var stroke = new SKPaint
                {
                    Color = color, IsAntialias = true,
                    Style = SKPaintStyle.Stroke, StrokeWidth = 4f,
                    StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round
                };
                using var dashEffect = dashed ? SKPathEffect.CreateDash(new float[] { 10f, 6f }, 0) : null;
                if (dashed)
                    stroke.PathEffect = dashEffect;
                canvas.DrawPath(path, stroke);
            }

            if (drawVertices)
            {
                using var vertexFill   = new SKPaint { Color = color,          IsAntialias = true };
                using var vertexBorder = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };
                for (int i = 0; i < pts.Count; i++)
                {
                    float r = (i == 0 || i == pts.Count - 1) ? 5.5f : 3f;
                    canvas.DrawCircle((float)pts[i].x, (float)pts[i].y, r, vertexFill);
                    canvas.DrawCircle((float)pts[i].x, (float)pts[i].y, r, vertexBorder);
                }
            }

            if (!string.IsNullOrWhiteSpace(route.Label))
            {
                float lx = (float)pts[0].x + 9;
                float ly = (float)pts[0].y - 9;
                float textSize = 12f;
                using var shadow = new SKPaint { Color = new SKColor(255, 255, 255, 210), IsAntialias = true, TextSize = textSize, FakeBoldText = true };
                using var text   = new SKPaint { Color = SKColors.Black,                  IsAntialias = true, TextSize = textSize, FakeBoldText = true };
                canvas.DrawText(route.Label, lx + 1, ly + 1, shadow);
                canvas.DrawText(route.Label, lx,     ly,     text);
            }
        }
    }
}
