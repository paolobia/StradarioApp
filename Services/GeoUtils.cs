// =============================================================================
// Services/GeoUtils.cs
//
// SINOSSI: Funzioni di utilità per conversioni geografiche e calcoli cartografici.
//   - Conversione tra coordinate geografiche (lat/lon WGS84) e coordinate pixel
//   - Calcolo dell'estensione geografica di una pagina dato il centro
//   - Conversione gradi decimali <-> metri (approssimazione WebMercator)
//   - Calcolo livello di zoom OSM ottimale per una data scala e DPI
// =============================================================================

using System;
using System.Collections.Generic;
using StradarioApp.Models;

namespace StradarioApp.Services
{
    public static class GeoUtils
    {
        // Raggio della Terra in km (sferoide WGS84 approssimato)
        private const double EarthRadiusKm = 6371.0;

        // Converte gradi di latitudine in km (costante)
        public static double LatDegToKm(double degrees)
        {
            return degrees * (Math.PI / 180.0) * EarthRadiusKm;
        }

        // Converte km in gradi di latitudine (costante)
        public static double KmToLatDeg(double km)
        {
            return km / EarthRadiusKm * (180.0 / Math.PI);
        }

        // Converte gradi di longitudine in km (dipende dalla latitudine)
        public static double LonDegToKm(double degrees, double atLatitude)
        {
            double cosLat = Math.Cos(atLatitude * Math.PI / 180.0);
            return degrees * (Math.PI / 180.0) * EarthRadiusKm * cosLat;
        }

        // Converte km in gradi di longitudine (dipende dalla latitudine)
        public static double KmToLonDeg(double km, double atLatitude)
        {
            double cosLat = Math.Cos(atLatitude * Math.PI / 180.0);
            if (Math.Abs(cosLat) < 1e-10) return 0;
            return km / (EarthRadiusKm * cosLat) * (180.0 / Math.PI);
        }

        // Calcola il GeoRect di una pagina dato il centro e le impostazioni
        public static GeoRect CalcPageBounds(double centerLon, double centerLat, StradarioSettings settings)
        {
            double halfWidthKm  = settings.GetPageWidthKm()  / 2.0;
            double halfHeightKm = settings.GetPageHeightKm() / 2.0;

            double halfWidthDeg  = KmToLonDeg(halfWidthKm,  centerLat);
            double halfHeightDeg = KmToLatDeg(halfHeightKm);

            return new GeoRect
            {
                MinLon = centerLon - halfWidthDeg,
                MaxLon = centerLon + halfWidthDeg,
                MinLat = centerLat - halfHeightDeg,
                MaxLat = centerLat + halfHeightDeg
            };
        }

        // Calcola il livello di zoom OSM ottimale per coprire l'area geografica
        // della pagina con i tile disponibili, indipendentemente dai DPI di stampa.
        //
        // Approccio: a zoom z, un tile 256px copre un'area di:
        //   larghezza = 360° / 2^z  gradi di longitudine (all'equatore)
        // Vogliamo che la larghezza della pagina (in gradi) sia coperta da
        // un numero ragionevole di tile. Usiamo la larghezza geografica della pagina
        // per trovare lo zoom che produce ~1 tile per estensione (poi aggiustiamo
        // verso l'alto in base alla scala desiderata).
        //
        // Formula derivata: al zoom z, la risoluzione è
        //   metres_per_pixel = cos(lat) * EarthCircumferenceM / (256 * 2^z)
        // La scala cartografica è: S = metres_per_pixel * DPI_schermo / 0.0254
        // dove DPI_schermo è convenzionalmente 96 per OSM (non il DPI di stampa).
        // Quindi:  2^z = cos(lat) * EarthCircumferenceM * 96 / (0.0254 * S * 256)
        public static int CalcOptimalZoom(StradarioSettings settings, double latitude)
        {
            double scaleDenom        = settings.GetScaleDenominator();
            double cosLat            = Math.Cos(latitude * Math.PI / 180.0);
            double earthCircumferenceM = 2.0 * Math.PI * EarthRadiusKm * 1000.0;

            // 96 DPI è la risoluzione di riferimento standard per i tile OSM
            const double osmScreenDpi = 96.0;

            double z = Math.Log2(cosLat * earthCircumferenceM * osmScreenDpi
                                 / (0.0254 * scaleDenom * 256.0));

            // Arrotondiamo per difetto: meglio un tile leggermente meno dettagliato
            // che tiles troppo zoomati che mostrano solo pochi isolati
            int zoom = (int)Math.Floor(z);
            return Math.Clamp(zoom, 1, 19);
        }

        // Calcola come piastrellare i tile OSM affinché la mappa STAMPATA rispetti
        // ESATTAMENTE la scala cartografica richiesta, a qualunque DPI.
        //
        // La scala su carta dipende solo dai metri reali per pixel di carta:
        //   requiredMpp = scaleDenominator * 0.0254 / dpi   (0.0254 m = 1 pollice)
        // Si sceglie lo zoom OSM intero più vicino e si disegna ogni tile a
        // `tileSizePx` px (invece dei 256 nativi) così che il metri-per-pixel
        // effettivo coincida con requiredMpp. Con questa correzione la scala è
        // esatta per QUALSIASI zoom: lo zoom influenza solo la nitidezza, non la scala.
        // Ritorna lo zoom intero e la dimensione (in px) a cui disegnare ogni tile.
        public static (int zoom, double tileSizePx) CalcScaleExactTiling(
            int scaleDenominator, int dpi, double latitude)
        {
            double cosLat     = Math.Cos(latitude * Math.PI / 180.0);
            double earthCircM = 2.0 * Math.PI * EarthRadiusKm * 1000.0;

            // Metri reali che ogni pixel di carta deve rappresentare per la scala.
            double requiredMpp = scaleDenominator * 0.0254 / dpi;

            // Zoom OSM intero più vicino (arrotondato, per minimizzare il resampling).
            double zIdeal = Math.Log2(cosLat * earthCircM / (256.0 * requiredMpp));
            int    zoom   = Math.Clamp((int)Math.Round(zIdeal), 1, 19);

            // Metri per pixel nativi a quello zoom, poi dimensione di disegno del tile.
            double mppZ       = cosLat * earthCircM / (256.0 * Math.Pow(2.0, zoom));
            double tileSizePx = 256.0 * mppZ / requiredMpp;

            return (zoom, tileSizePx);
        }

        // Converte coordinate geografiche in coordinate pixel sullo schermo,
        // data la vista corrente (centro, zoom, dimensioni canvas).
        public static (double x, double y) GeoToPixel(
            double lon, double lat,
            double viewCenterLon, double viewCenterLat,
            double zoom, double canvasWidth, double canvasHeight)
        {
            // Proiezione Web Mercator: coordinate in "tile units" al livello zoom
            double scale = 256.0 * Math.Pow(2.0, zoom);

            double cx = LonToTileX(viewCenterLon, zoom) * 256.0;
            double cy = LatToTileY(viewCenterLat, zoom) * 256.0;

            double px = LonToTileX(lon, zoom) * 256.0;
            double py = LatToTileY(lat, zoom) * 256.0;

            double screenX = canvasWidth  / 2.0 + (px - cx);
            double screenY = canvasHeight / 2.0 + (py - cy);

            return (screenX, screenY);
        }

        // Converte coordinate pixel sullo schermo in geografiche
        public static (double lon, double lat) PixelToGeo(
            double screenX, double screenY,
            double viewCenterLon, double viewCenterLat,
            double zoom, double canvasWidth, double canvasHeight)
        {
            double cx = LonToTileX(viewCenterLon, zoom) * 256.0;
            double cy = LatToTileY(viewCenterLat, zoom) * 256.0;

            double px = cx + (screenX - canvasWidth  / 2.0);
            double py = cy + (screenY - canvasHeight / 2.0);

            double tileX = px / 256.0;
            double tileY = py / 256.0;

            double lon = TileXToLon(tileX, zoom);
            double lat = TileYToLat(tileY, zoom);

            return (lon, lat);
        }

        // Numero di tile frazionario dalla longitudine (Web Mercator)
        public static double LonToTileX(double lon, double zoom)
        {
            double n = Math.Pow(2.0, zoom);
            return n * (lon + 180.0) / 360.0;
        }

        // Numero di tile frazionario dalla latitudine (Web Mercator)
        public static double LatToTileY(double lat, double zoom)
        {
            double n    = Math.Pow(2.0, zoom);
            double latR = lat * Math.PI / 180.0;
            return n * (1.0 - Math.Log(Math.Tan(latR) + 1.0 / Math.Cos(latR)) / Math.PI) / 2.0;
        }

        // Longitudine dal numero di tile frazionario
        public static double TileXToLon(double tileX, double zoom)
        {
            double n = Math.Pow(2.0, zoom);
            return tileX / n * 360.0 - 180.0;
        }

        // Latitudine dal numero di tile frazionario
        public static double TileYToLat(double tileY, double zoom)
        {
            double n = Math.Pow(2.0, zoom);
            double latR = Math.Atan(Math.Sinh(Math.PI * (1.0 - 2.0 * tileY / n)));
            return latR * 180.0 / Math.PI;
        }

        // Converte coordinate geografiche in pixel all'interno di un bitmap
        // renderizzato da RenderTilesAsync (PdfGenerator), dove ogni tile è
        // disegnato a "tileSizePx" invece dei 256 nativi per rispettare la
        // scala esatta di stampa. Usata per posizionare i marker dei POI
        // sulle mappe stampate delle pagine PDF.
        public static (double x, double y) GeoToBitmapPixel(
            double lon, double lat,
            double centerLon, double centerLat,
            int zoom, double tileSizePx, double pixW, double pixH)
        {
            double cx = LonToTileX(centerLon, zoom);
            double cy = LatToTileY(centerLat, zoom);

            double tileX = LonToTileX(lon, zoom);
            double tileY = LatToTileY(lat, zoom);

            double px = (tileX - cx) * tileSizePx + pixW / 2.0;
            double py = (tileY - cy) * tileSizePx + pixH / 2.0;

            return (px, py);
        }

        // Distanza in km tra due punti geografici (formula haversine)
        public static double DistanceKm(double lon1, double lat1, double lon2, double lat2)
        {
            double dLat = (lat2 - lat1) * Math.PI / 180.0;
            double dLon = (lon2 - lon1) * Math.PI / 180.0;
            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                     + Math.Cos(lat1 * Math.PI / 180.0)
                     * Math.Cos(lat2 * Math.PI / 180.0)
                     * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            return 2.0 * EarthRadiusKm * Math.Asin(Math.Sqrt(a));
        }

        // Baricentro di una spezzata, pesato per la lunghezza di ogni
        // segmento (non la semplice media dei vertici, che sovrapeserebbe
        // i tratti con molti punti ravvicinati rispetto a un lungo tratto
        // rettilineo con pochi vertici) — usato sia per centrare la mappa
        // su un percorso cliccato in albero, sia come ancora dell'etichetta
        // del nome percorso (mappa interattiva e PDF).
        public static (double lon, double lat) PolylineCentroid(IReadOnlyList<GeoPoint> points)
        {
            if (points.Count == 1) return (points[0].Lon, points[0].Lat);

            double sumLon = 0, sumLat = 0, sumLen = 0;
            for (int i = 0; i < points.Count - 1; i++)
            {
                var a = points[i];
                var b = points[i + 1];
                double len = DistanceKm(a.Lon, a.Lat, b.Lon, b.Lat);
                sumLon += (a.Lon + b.Lon) / 2.0 * len;
                sumLat += (a.Lat + b.Lat) / 2.0 * len;
                sumLen += len;
            }

            if (sumLen <= 0) return (points[0].Lon, points[0].Lat);
            return (sumLon / sumLen, sumLat / sumLen);
        }

        // Arrotonda una coordinata (lon o lat) a una chiave intera con
        // precisione ~0.1m, per riconoscere due punti come "lo stesso posto"
        // pur con minime differenze di arrotondamento in virgola mobile —
        // usato per deduplicare l'etichetta testuale quando più POI (inline
        // su percorsi diversi, o un gruppo POI + un inline) coincidono nello
        // stesso punto con lo stesso testo.
        public static long LabelDedupKey(double coord) => (long)Math.Round(coord * 1_000_000.0);
    }
}
