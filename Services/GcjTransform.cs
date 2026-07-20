// =============================================================================
// Services/GcjTransform.cs
//
// SINOSSI: Correzione GCJ-02 -> WGS84 per POI/percorsi importati da fonti
//   cinesi (Amap/Gaode, Baidu-derivati, molte app di navigazione cinesi).
//   Per obbligo normativo, le mappe pubblicate in Cina applicano un offset
//   crittografico ("coordinate Mars", GCJ-02) rispetto alle coordinate
//   geografiche reali (WGS84). I tile OSM usati da StradarioApp sono invece
//   in WGS84 vero: un punto GCJ-02 non corretto appare quindi spostato di
//   50-700 m rispetto alla posizione reale sulla mappa. La correzione va
//   applicata solo ai punti che cadono dentro il bounding box della Cina
//   (fuori da quell'area le coordinate sono già WGS84 corrette, correggerle
//   comunque le sposterebbe erroneamente) — Gcj02ToWgs84 verifica da sé
//   IsInChina e restituisce il punto invariato se fuori.
//   Algoritmo standard (v. progetto "eviltransform"): la trasformazione
//   WGS84->GCJ-02 è nota in forma chiusa; l'inversa non ha soluzione
//   analitica esatta, quindi si usa una bisezione iterativa che converge
//   sotto il metro in poche iterazioni.
// =============================================================================

using System;

namespace StradarioApp.Services
{
    public static class GcjTransform
    {
        private const double A  = 6378245.0;               // semiasse maggiore ellissoide di Krasovsky
        private const double Ee = 0.00669342162296594323;   // eccentricità al quadrato

        // Bounding box approssimativo della Cina continentale (incluse Hong Kong/Macao):
        // fuori da quest'area le mappe non applicano l'offset GCJ-02.
        public static bool IsInChina(double lat, double lon)
        {
            return lon >= 72.004 && lon <= 137.8347 && lat >= 0.8293 && lat <= 55.8271;
        }

        // Corregge un punto GCJ-02 (letto da un KML/GPX di origine cinese) nel
        // corrispondente punto WGS84 reale. No-op se il punto è fuori dalla Cina.
        public static (double Lat, double Lon) Gcj02ToWgs84(double gcjLat, double gcjLon)
        {
            if (!IsInChina(gcjLat, gcjLon))
                return (gcjLat, gcjLon);

            const double initDelta  = 0.01;
            const double threshold  = 0.000001;

            double mLat = gcjLat - initDelta, mLon = gcjLon - initDelta;
            double pLat = gcjLat + initDelta, pLon = gcjLon + initDelta;
            double wgLat = gcjLat, wgLon = gcjLon;

            for (int i = 0; i < 30; i++)
            {
                wgLat = (mLat + pLat) / 2;
                wgLon = (mLon + pLon) / 2;
                var (tmpLat, tmpLon) = Wgs84ToGcj02(wgLat, wgLon);
                double dLat = tmpLat - gcjLat;
                double dLon = tmpLon - gcjLon;
                if (Math.Abs(dLat) < threshold && Math.Abs(dLon) < threshold)
                    break;

                if (dLat > 0) pLat = wgLat; else mLat = wgLat;
                if (dLon > 0) pLon = wgLon; else mLon = wgLon;
            }

            return (wgLat, wgLon);
        }

        private static (double Lat, double Lon) Wgs84ToGcj02(double wgLat, double wgLon)
        {
            double dLat = TransformLat(wgLon - 105.0, wgLat - 35.0);
            double dLon = TransformLon(wgLon - 105.0, wgLat - 35.0);
            double radLat = wgLat / 180.0 * Math.PI;
            double magic = Math.Sin(radLat);
            magic = 1 - Ee * magic * magic;
            double sqrtMagic = Math.Sqrt(magic);
            dLat = (dLat * 180.0) / ((A * (1 - Ee)) / (magic * sqrtMagic) * Math.PI);
            dLon = (dLon * 180.0) / (A / sqrtMagic * Math.Cos(radLat) * Math.PI);
            return (wgLat + dLat, wgLon + dLon);
        }

        private static double TransformLat(double x, double y)
        {
            double ret = -100.0 + 2.0 * x + 3.0 * y + 0.2 * y * y + 0.1 * x * y + 0.2 * Math.Sqrt(Math.Abs(x));
            ret += (20.0 * Math.Sin(6.0 * x * Math.PI) + 20.0 * Math.Sin(2.0 * x * Math.PI)) * 2.0 / 3.0;
            ret += (20.0 * Math.Sin(y * Math.PI) + 40.0 * Math.Sin(y / 3.0 * Math.PI)) * 2.0 / 3.0;
            ret += (160.0 * Math.Sin(y / 12.0 * Math.PI) + 320 * Math.Sin(y * Math.PI / 30.0)) * 2.0 / 3.0;
            return ret;
        }

        private static double TransformLon(double x, double y)
        {
            double ret = 300.0 + x + 2.0 * y + 0.1 * x * x + 0.1 * x * y + 0.1 * Math.Sqrt(Math.Abs(x));
            ret += (20.0 * Math.Sin(6.0 * x * Math.PI) + 20.0 * Math.Sin(2.0 * x * Math.PI)) * 2.0 / 3.0;
            ret += (20.0 * Math.Sin(x * Math.PI) + 40.0 * Math.Sin(x / 3.0 * Math.PI)) * 2.0 / 3.0;
            ret += (150.0 * Math.Sin(x / 12.0 * Math.PI) + 300.0 * Math.Sin(x / 30.0 * Math.PI)) * 2.0 / 3.0;
            return ret;
        }
    }
}
