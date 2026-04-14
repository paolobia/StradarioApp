using System;
using StradarioApp.Models;

namespace StradarioApp.Services
{
    public static class GeoUtils
    {
        private const double EarthRadiusKm = 6371.0;
        private const double EarthCircumM  = 2.0 * Math.PI * 6378137.0; // WGS84

        // ---- Degree / km conversions ----

        public static double LatDegToKm(double deg) => deg * (Math.PI / 180.0) * EarthRadiusKm;
        public static double KmToLatDeg(double km)  => km / EarthRadiusKm * (180.0 / Math.PI);

        public static double LonDegToKm(double deg, double lat)
        {
            double cosLat = Math.Cos(lat * Math.PI / 180.0);
            return deg * (Math.PI / 180.0) * EarthRadiusKm * cosLat;
        }

        public static double KmToLonDeg(double km, double lat)
        {
            double cosLat = Math.Cos(lat * Math.PI / 180.0);
            if (Math.Abs(cosLat) < 1e-10) return 0;
            return km / (EarthRadiusKm * cosLat) * (180.0 / Math.PI);
        }

        // ---- Page bounds ----

        public static GeoRect CalcPageBounds(double centerLon, double centerLat, StradarioSettings settings)
        {
            double halfWidthKm  = settings.GetPageWidthKm()  / 2.0;
            double halfHeightKm = settings.GetPageHeightKm() / 2.0;

            double dLon = KmToLonDeg(halfWidthKm, centerLat);
            double dLat = KmToLatDeg(halfHeightKm);

            return new GeoRect
            {
                MinLon = centerLon - dLon,
                MaxLon = centerLon + dLon,
                MinLat = centerLat - dLat,
                MaxLat = centerLat + dLat
            };
        }

        // ---- Optimal zoom (96 DPI fixed reference — OSM standard) ----

        public static int CalcOptimalZoom(StradarioSettings settings, double latitude)
        {
            double cosLat      = Math.Cos(latitude * Math.PI / 180.0);
            int    scaleDenom  = settings.GetScaleDenominator();
            // 96 DPI is the OSM reference — do NOT use settings.Dpi here
            double z = Math.Log2(cosLat * EarthCircumM * 96.0 / (0.0254 * scaleDenom * 256.0));
            int zoom = (int)Math.Floor(z);
            return Math.Clamp(zoom, 1, 19);
        }

        // ---- WebMercator pixel conversions ----

        public static (double x, double y) GeoToPixel(
            double lon, double lat,
            double centerLon, double centerLat,
            double zoom, double w, double h)
        {
            double scale = 256.0 * Math.Pow(2.0, zoom);

            double cx = LonToWorld(centerLon) * scale;
            double cy = LatToWorld(centerLat) * scale;

            double px = LonToWorld(lon) * scale;
            double py = LatToWorld(lat) * scale;

            return (w / 2.0 + (px - cx), h / 2.0 + (py - cy));
        }

        public static (double lon, double lat) PixelToGeo(
            double x, double y,
            double centerLon, double centerLat,
            double zoom, double w, double h)
        {
            double scale = 256.0 * Math.Pow(2.0, zoom);

            double cx = LonToWorld(centerLon) * scale;
            double cy = LatToWorld(centerLat) * scale;

            double wx = (x - w / 2.0 + cx) / scale;
            double wy = (y - h / 2.0 + cy) / scale;

            return (WorldToLon(wx), WorldToLat(wy));
        }

        // ---- Tile conversions ----

        public static double LonToTileX(double lon, double zoom)
        {
            double n = Math.Pow(2.0, zoom);
            return (lon + 180.0) / 360.0 * n;
        }

        public static double LatToTileY(double lat, double zoom)
        {
            double n = Math.Pow(2.0, zoom);
            double latRad = lat * Math.PI / 180.0;
            return (1.0 - Math.Log(Math.Tan(latRad) + 1.0 / Math.Cos(latRad)) / Math.PI) / 2.0 * n;
        }

        public static double TileXToLon(double tileX, double zoom)
        {
            double n = Math.Pow(2.0, zoom);
            return tileX / n * 360.0 - 180.0;
        }

        public static double TileYToLat(double tileY, double zoom)
        {
            double n  = Math.Pow(2.0, zoom);
            double r  = Math.Atan(Math.Sinh(Math.PI * (1.0 - 2.0 * tileY / n)));
            return r * 180.0 / Math.PI;
        }

        // ---- Haversine distance ----

        public static double DistanceKm(double lon1, double lat1, double lon2, double lat2)
        {
            double dLat = (lat2 - lat1) * Math.PI / 180.0;
            double dLon = (lon2 - lon1) * Math.PI / 180.0;
            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                     + Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0)
                     * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            return EarthRadiusKm * 2.0 * Math.Asin(Math.Sqrt(a));
        }

        // ---- Internal WebMercator helpers ----

        private static double LonToWorld(double lon) => (lon + 180.0) / 360.0;

        private static double LatToWorld(double lat)
        {
            double latRad = lat * Math.PI / 180.0;
            return (1.0 - Math.Log(Math.Tan(latRad) + 1.0 / Math.Cos(latRad)) / Math.PI) / 2.0;
        }

        private static double WorldToLon(double wx) => wx * 360.0 - 180.0;

        private static double WorldToLat(double wy)
        {
            double r = Math.PI * (1.0 - 2.0 * wy);
            return Math.Atan(Math.Sinh(r)) * 180.0 / Math.PI;
        }
    }
}
