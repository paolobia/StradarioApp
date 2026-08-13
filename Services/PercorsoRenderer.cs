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
//
//   DrawLine/DrawVerticesOnly sono le stesse operazioni scomposte in fasi
//   separate (solo linea / soli pallini-icona, senza etichetta) — usate dal
//   PDF per garantire un ordine di disegno GLOBALE corretto su tutta la
//   pagina (tutte le linee di tutti i percorsi, poi tutte le icone, poi
//   tutte le etichette): prima, disegnando un percorso per volta con Draw
//   (linea+icone+etichetta insieme), la linea di un percorso disegnato DOPO
//   finiva sopra l'etichetta di un percorso disegnato prima, rendendola
//   illeggibile — bug reale segnalato dall'utente ("ho delle etichette
//   SOTTO a delle linee"). Draw resta invariato (usato solo dalla mappa
//   interattiva e dalle anteprime di instradamento, dove l'ordine per-
//   percorso non è un problema).
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

        // Disegna SOLO la traccia (alone bianco + linea colorata/tratteggiata),
        // senza vertici né etichetta — vedi sinossi sopra per il perché.
        public static void DrawLine(SKCanvas canvas, Percorso route,
            Func<double, double, (double x, double y)> project, bool dashed = false,
            SKColor? colorOverride = null, float strokeWidthMultiplier = 1f)
        {
            if (route.Points.Count < 2) return;

            var color = colorOverride ?? ParseColor(route.ColorHex);
            var pts = route.Points.Select(p => project(p.Lon, p.Lat)).ToList();

            using var path = new SKPath();
            path.MoveTo((float)pts[0].x, (float)pts[0].y);
            for (int i = 1; i < pts.Count; i++)
                path.LineTo((float)pts[i].x, (float)pts[i].y);

            if (!dashed)
            {
                using var halo = new SKPaint
                {
                    Color = new SKColor(255, 255, 255, 200), IsAntialias = true,
                    Style = SKPaintStyle.Stroke, StrokeWidth = 6.5f * strokeWidthMultiplier,
                    StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round
                };
                canvas.DrawPath(path, halo);
            }

            using var stroke = new SKPaint
            {
                Color = color, IsAntialias = true,
                Style = SKPaintStyle.Stroke, StrokeWidth = 4f * strokeWidthMultiplier,
                StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round
            };
            using var dashEffect = dashed ? SKPathEffect.CreateDash(new float[] { 10f, 6f }, 0) : null;
            if (dashed)
                stroke.PathEffect = dashEffect;
            canvas.DrawPath(path, stroke);
        }

        // Disegna SOLI i pallini/icone dei vertici (nessuna etichetta, anche
        // per i punti marcati IsPoi — solo l'icona) — vedi sinossi sopra.
        public static void DrawVerticesOnly(SKCanvas canvas, Percorso route,
            Func<double, double, (double x, double y)> project,
            SKColor? colorOverride = null, float strokeWidthMultiplier = 1f)
        {
            if (route.Points.Count == 0) return;

            var color = colorOverride ?? ParseColor(route.ColorHex);
            var pts = route.Points.Select(p => project(p.Lon, p.Lat)).ToList();

            using var vertexFill   = new SKPaint { Color = color,          IsAntialias = true };
            using var vertexBorder = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };
            float poiSize = 22f * strokeWidthMultiplier;
            for (int i = 0; i < pts.Count; i++)
            {
                var gp = route.Points[i];
                if (gp.IsPoi)
                {
                    PoiIconRenderer.Draw(canvas, gp.PoiIcon, color, (float)pts[i].x, (float)pts[i].y, poiSize);
                    continue;
                }
                float r = (i == 0 || i == pts.Count - 1) ? 5.5f : 3f;
                canvas.DrawCircle((float)pts[i].x, (float)pts[i].y, r, vertexFill);
                canvas.DrawCircle((float)pts[i].x, (float)pts[i].y, r, vertexBorder);
            }
        }

        // Dimensione (px) usata per i marker dei punti-POI inline disegnati
        // da DrawVerticesOnly — serve a chi calcola le etichette in una fase
        // separata (PdfGenerator) per restare coerente con le icone.
        public const float InlinePoiMarkerSizePx = 22f;

        // Disegna la traccia completa: alone bianco, linea colorata, pallini
        // ai vertici (più grandi su primo/ultimo punto) ed etichetta accanto
        // al primo punto. "project" converte lon/lat nel sistema di
        // coordinate del canvas di destinazione (schermo o bitmap). "dashed"
        // disegna la linea tratteggiata: usato per il percorso in corso di
        // disegno (non ancora confermato). "drawVertices=false" omette i
        // pallini: pensati per un percorso disegnato a mano (pochi punti),
        // su una geometria densa (es. un'alternativa OSRM con centinaia di
        // punti) diventerebbero una fila di pallini fitta e illeggibile
        // invece di una linea pulita — vedi MainWindow.DrawInstradaOverlay.
        // avoidLabelNear: coordinate (tipicamente dei marker POI nella stessa
        // vista) da cui tenere lontana l'etichetta del percorso. Serve quando
        // un percorso coincide (anche solo all'inizio) con un gruppo di POI:
        // l'etichetta di default (sopra-destra del primo punto) finirebbe
        // sovrapposta all'etichetta del POI nello stesso punto, illeggibile.
        // Usato solo quando `occupiedLabelRects` è null (vedi sotto).
        // occupiedLabelRects: se non null, condivisa fra TUTTI i percorsi/POI
        // dello stesso frame della mappa interattiva (MapRenderer) — sceglie
        // la posizione migliore fra le 4 candidate (destra/sinistra/sopra/
        // sotto, vedi PoiIconRenderer.ChooseLabelPosition) sia per l'etichetta
        // del percorso sia per quelle dei suoi punti-POI inline, senza mai
        // nasconderle. Sostituisce in quel caso la logica avoidLabelNear/
        // tooClose (troppo rozza: un solo raggio fisso, non teneva conto di
        // ALTRI percorsi/POI). BUG REALE segnalato dall'utente: due percorsi
        // diversi con l'estremo nello stesso punto (es. un cambio treno)
        // mostravano l'un sull'altro sia le rispettive etichette di percorso
        // sia quelle dei punti-POI, sovrapposte senza alcun tentativo di
        // separarle — solo i marker POI "singoli" (MapRenderer.DrawPois)
        // avevano già questo trattamento.
        public static void Draw(SKCanvas canvas, Percorso route,
            Func<double, double, (double x, double y)> project, bool dashed = false,
            SKColor? colorOverride = null, bool drawVertices = true,
            IReadOnlyList<(double Lon, double Lat)>? avoidLabelNear = null,
            float strokeWidthMultiplier = 1f,
            List<SKRect>? occupiedLabelRects = null)
        {
            if (route.Points.Count == 0) return;

            DrawLine(canvas, route, project, dashed, colorOverride, strokeWidthMultiplier);

            var color = colorOverride ?? ParseColor(route.ColorHex);
            var pts = route.Points.Select(p => project(p.Lon, p.Lat)).ToList();

            if (drawVertices)
            {
                using var vertexFill   = new SKPaint { Color = color,          IsAntialias = true };
                using var vertexBorder = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };
                float poiSize = 22f * strokeWidthMultiplier;
                for (int i = 0; i < pts.Count; i++)
                {
                    var gp = route.Points[i];
                    if (gp.IsPoi)
                    {
                        // Un punto marcato come POI si disegna come un vero
                        // marker (icona + etichetta), sempre col colore del
                        // percorso — mai il semplice pallino di vertice.
                        PoiIconRenderer.DrawWithLabel(canvas, gp.PoiIcon, color, gp.PoiLabel, (float)pts[i].x, (float)pts[i].y, poiSize,
                            occupiedLabelRects: occupiedLabelRects, alwaysShow: occupiedLabelRects != null);
                        continue;
                    }
                    float r = (i == 0 || i == pts.Count - 1) ? 5.5f : 3f;
                    canvas.DrawCircle((float)pts[i].x, (float)pts[i].y, r, vertexFill);
                    canvas.DrawCircle((float)pts[i].x, (float)pts[i].y, r, vertexBorder);
                }
            }

            if (!string.IsNullOrWhiteSpace(route.Label))
            {
                float textSize = 12f;

                if (occupiedLabelRects != null)
                {
                    var pos = PoiIconRenderer.ChooseLabelPosition(route.Label, (float)pts[0].x, (float)pts[0].y, 24f, occupiedLabelRects);
                    PoiIconRenderer.DrawHaloText(canvas, route.Label, pos.tx, pos.ty, pos.textSize);
                }
                else
                {
                    using var measure = new SKPaint { TextSize = textSize, FakeBoldText = true };

                    const float AvoidRadiusPx = 26f;
                    bool tooClose = avoidLabelNear != null && avoidLabelNear.Any(p =>
                    {
                        var (ax, ay) = project(p.Lon, p.Lat);
                        double dx = ax - pts[0].x, dy = ay - pts[0].y;
                        return dx * dx + dy * dy < AvoidRadiusPx * AvoidRadiusPx;
                    });

                    float lx, ly;
                    if (tooClose)
                    {
                        // Un marker (tipicamente un POI) è troppo vicino al primo
                        // punto: sposta l'etichetta sotto-sinistra invece che
                        // sopra-destra, più lontana dal punto.
                        float textWidth = measure.MeasureText(route.Label);
                        lx = (float)pts[0].x - textWidth - 9;
                        ly = (float)pts[0].y + 22;
                    }
                    else
                    {
                        lx = (float)pts[0].x + 9;
                        ly = (float)pts[0].y - 9;
                    }

                    PoiIconRenderer.DrawHaloText(canvas, route.Label, lx, ly, textSize);
                }
            }
        }
    }
}
