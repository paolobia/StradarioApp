// =============================================================================
// Services/WorldBordersService.cs
//
// SINOSSI: Confini nazionali + coste (Natural Earth 1:110m "admin 0
//   countries", dominio pubblico), bundlati come EmbeddedResource compresso
//   (Resources/WorldBorders/world_borders.geojson.gz, ~65 KB), usati dalla
//   mini-mappa schematica nella pagina indice del PDF
//   (PdfGenerator.DrawLocatorMap). Un poligono paese contiene già sia i
//   confini fra stati sia le coste (il lato di un poligono non condiviso con
//   un vicino è mare): un solo stroke uniforme sui bordi dei poligoni
//   disegna entrambi, non serve un dataset coste separato.
//
//   Formato dati: NON il GeoJSON originale (troppo verboso, pieno di campi
//   inutilizzati), ma un JSON minimizzato ad-hoc prodotto offline da
//   ne_110m_admin_0_countries.geojson (coordinate arrotondate a 3 decimali,
//   ~110m di precisione, coerente con la risoluzione del dataset 1:110m):
//   { "features": [ { "n": "Italy", "b": [minLon,minLat,maxLon,maxLat],
//                      "r": [ [[lon,lat], ...], ... ] }, ... ] }
//   "r" è la lista degli anelli (ring) del poligono/multipoligono — un paese
//   con isole ha più anelli, ciascuno disegnato come polilinea chiusa
//   indipendente (anelli interni/buchi inclusi: disegnati come contorno,
//   senza riempimento, restano visivamente corretti anche senza distinguerli
//   dagli anelli esterni).
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;
using StradarioApp.Models;

namespace StradarioApp.Services
{
    public readonly struct BorderPolygon
    {
        public string Name { get; }
        public GeoRect Bbox { get; }
        public List<List<(double Lon, double Lat)>> Rings { get; }

        public BorderPolygon(string name, GeoRect bbox, List<List<(double Lon, double Lat)>> rings)
        {
            Name = name;
            Bbox = bbox;
            Rings = rings;
        }
    }

    public static class WorldBordersService
    {
        private const string ResourceName = "StradarioApp.WorldBorders.geojson.gz";

        private static List<BorderPolygon>? _polygons;
        private static readonly object _lock = new object();

        private sealed class RawFeature
        {
            [JsonPropertyName("n")] public string N { get; set; } = "";
            [JsonPropertyName("b")] public double[] B { get; set; } = Array.Empty<double>();
            [JsonPropertyName("r")] public double[][][] R { get; set; } = Array.Empty<double[][]>();
        }

        private sealed class RawData
        {
            [JsonPropertyName("features")] public List<RawFeature> Features { get; set; } = new();
        }

        public static IReadOnlyList<BorderPolygon> GetPolygons()
        {
            if (_polygons != null) return _polygons;

            lock (_lock)
            {
                if (_polygons != null) return _polygons;
                _polygons = Load();
                return _polygons;
            }
        }

        // Solo i poligoni il cui bbox interseca bounds — evita di iterare
        // inutilmente i ~177 paesi del dataset quando la mini-mappa inquadra
        // un'area piccola.
        public static IEnumerable<BorderPolygon> GetPolygonsInBounds(GeoRect bounds)
        {
            foreach (var poly in GetPolygons())
            {
                if (poly.Bbox.MaxLon < bounds.MinLon || poly.Bbox.MinLon > bounds.MaxLon) continue;
                if (poly.Bbox.MaxLat < bounds.MinLat || poly.Bbox.MinLat > bounds.MaxLat) continue;
                yield return poly;
            }
        }

        private static List<BorderPolygon> Load()
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using var resStream = asm.GetManifestResourceStream(ResourceName);
                if (resStream == null) return new List<BorderPolygon>();

                using var gz = new GZipStream(resStream, CompressionMode.Decompress);
                using var ms = new MemoryStream();
                gz.CopyTo(ms);
                ms.Position = 0;

                var raw = System.Text.Json.JsonSerializer.Deserialize<RawData>(ms);
                if (raw == null) return new List<BorderPolygon>();

                var result = new List<BorderPolygon>(raw.Features.Count);
                foreach (var f in raw.Features)
                {
                    if (f.B.Length < 4) continue;
                    var bbox = new GeoRect
                    {
                        MinLon = f.B[0],
                        MinLat = f.B[1],
                        MaxLon = f.B[2],
                        MaxLat = f.B[3]
                    };

                    var rings = new List<List<(double Lon, double Lat)>>(f.R.Length);
                    foreach (var ring in f.R)
                    {
                        var pts = new List<(double Lon, double Lat)>(ring.Length);
                        foreach (var p in ring)
                        {
                            if (p.Length < 2) continue;
                            pts.Add((p[0], p[1]));
                        }
                        if (pts.Count >= 2) rings.Add(pts);
                    }

                    if (rings.Count == 0) continue;
                    result.Add(new BorderPolygon(f.N, bbox, rings));
                }

                return result;
            }
            catch
            {
                // Best-effort: se il resource manca/è corrotto, la mini-mappa
                // disegna semplicemente senza confini invece di far fallire
                // l'intera generazione del PDF.
                return new List<BorderPolygon>();
            }
        }
    }
}
