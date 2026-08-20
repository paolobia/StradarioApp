// =============================================================================
// Services/WaterFeaturesService.cs
//
// SINOSSI: Laghi + fiumi (Natural Earth 1:10m "lakes" e
//   "rivers_lake_centerlines", dominio pubblico — la risoluzione PIÙ
//   dettagliata offerta da Natural Earth, non la stessa scala 1:110m usata
//   da WorldBordersService per i confini: alla scala dei confini mondiali
//   non compariva NESSUN lago/fiume italiano, nemmeno il Garda o il Po),
//   filtrati per "min_zoom" (<= 6.0, lo stesso criterio di visibilità
//   incrementale usato da Natural Earth per le proprie mappe web) per
//   restare a un livello "regionale" senza portarsi dietro l'intero
//   dataset (586 laghi + 800 fiumi selezionati, non i ~1300+1400 totali).
//   Bundlato come EmbeddedResource compresso
//   (Resources/WorldBorders/water_features.geojson.gz, ~1.4 MB — più
//   pesante del dataset 1:110m usato in precedenza, necessario per la
//   copertura regionale). Usato dalla mini-mappa schematica nella
//   copertina del PDF (PdfGenerator.DrawLocatorMap) per dare un
//   riferimento geografico oltre al solo contorno di terra.
//
//   Nota: anche a questa risoluzione un piccolo bacino artificiale come il
//   lago di Brasimone (Appennino bolognese, ~3 km²) resta assente — è
//   sotto la soglia di qualunque dataset cartografico generalista
//   (verificato: assente anche nel dataset 1:10m completo, non solo nel
//   sottoinsieme filtrato qui). Per un dettaglio di quel livello servirebbe
//   un dataset idrografico nazionale/regionale dedicato, fuori scopo per
//   una mini-mappa "colpo d'occhio" come questa.
//
//   Formato dati: JSON minimizzato ad-hoc (non il GeoJSON originale):
//   { "lakes":  [ { "n": "Superiore", "b": [minLon,minLat,maxLon,maxLat],
//                    "r": [ [[lon,lat], ...], ... ] }, ... ],
//     "rivers": [ { "n": "Danubio",   "b": [minLon,minLat,maxLon,maxLat],
//                    "l": [ [[lon,lat], ...], ... ] }, ... ] }
//   "r" (laghi) sono anelli di poligono (come i confini); "l" (fiumi) sono
//   polilinee aperte. Nomi in italiano quando disponibili nel dataset
//   Natural Earth (campo "name_it"), altrimenti il nome originale.
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
    public readonly struct RiverLine
    {
        public string Name { get; }
        public GeoRect Bbox { get; }
        public List<List<(double Lon, double Lat)>> Lines { get; }

        public RiverLine(string name, GeoRect bbox, List<List<(double Lon, double Lat)>> lines)
        {
            Name = name;
            Bbox = bbox;
            Lines = lines;
        }
    }

    public static class WaterFeaturesService
    {
        private const string ResourceName = "StradarioApp.WorldBorders.water.geojson.gz";

        private static List<BorderPolygon>? _lakes;
        private static List<RiverLine>? _rivers;
        private static readonly object _lock = new object();

        private sealed class RawLake
        {
            [JsonPropertyName("n")] public string N { get; set; } = "";
            [JsonPropertyName("b")] public double[] B { get; set; } = Array.Empty<double>();
            [JsonPropertyName("r")] public double[][][] R { get; set; } = Array.Empty<double[][]>();
        }

        private sealed class RawRiver
        {
            [JsonPropertyName("n")] public string N { get; set; } = "";
            [JsonPropertyName("b")] public double[] B { get; set; } = Array.Empty<double>();
            [JsonPropertyName("l")] public double[][][] L { get; set; } = Array.Empty<double[][]>();
        }

        private sealed class RawData
        {
            [JsonPropertyName("lakes")]  public List<RawLake>  Lakes  { get; set; } = new();
            [JsonPropertyName("rivers")] public List<RawRiver> Rivers { get; set; } = new();
        }

        private static void EnsureLoaded()
        {
            if (_lakes != null && _rivers != null) return;
            lock (_lock)
            {
                if (_lakes != null && _rivers != null) return;
                Load(out _lakes, out _rivers);
            }
        }

        public static IEnumerable<BorderPolygon> GetLakesInBounds(GeoRect bounds)
        {
            EnsureLoaded();
            foreach (var lake in _lakes!)
            {
                if (lake.Bbox.MaxLon < bounds.MinLon || lake.Bbox.MinLon > bounds.MaxLon) continue;
                if (lake.Bbox.MaxLat < bounds.MinLat || lake.Bbox.MinLat > bounds.MaxLat) continue;
                yield return lake;
            }
        }

        public static IEnumerable<RiverLine> GetRiversInBounds(GeoRect bounds)
        {
            EnsureLoaded();
            foreach (var river in _rivers!)
            {
                if (river.Bbox.MaxLon < bounds.MinLon || river.Bbox.MinLon > bounds.MaxLon) continue;
                if (river.Bbox.MaxLat < bounds.MinLat || river.Bbox.MinLat > bounds.MaxLat) continue;
                yield return river;
            }
        }

        private static GeoRect BboxOf(double[] b) => new GeoRect
        {
            MinLon = b[0],
            MinLat = b[1],
            MaxLon = b[2],
            MaxLat = b[3]
        };

        private static List<List<(double Lon, double Lat)>> ReadRings(double[][][] raw)
        {
            var rings = new List<List<(double Lon, double Lat)>>(raw.Length);
            foreach (var ring in raw)
            {
                var pts = new List<(double Lon, double Lat)>(ring.Length);
                foreach (var p in ring)
                {
                    if (p.Length < 2) continue;
                    pts.Add((p[0], p[1]));
                }
                if (pts.Count >= 2) rings.Add(pts);
            }
            return rings;
        }

        private static void Load(out List<BorderPolygon>? lakes, out List<RiverLine>? rivers)
        {
            lakes = new List<BorderPolygon>();
            rivers = new List<RiverLine>();

            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using var resStream = asm.GetManifestResourceStream(ResourceName);
                if (resStream == null) return;

                using var gz = new GZipStream(resStream, CompressionMode.Decompress);
                using var ms = new MemoryStream();
                gz.CopyTo(ms);
                ms.Position = 0;

                var raw = System.Text.Json.JsonSerializer.Deserialize<RawData>(ms);
                if (raw == null) return;

                foreach (var f in raw.Lakes)
                {
                    if (f.B.Length < 4) continue;
                    var rings = ReadRings(f.R);
                    if (rings.Count == 0) continue;
                    lakes.Add(new BorderPolygon(f.N, BboxOf(f.B), rings));
                }

                foreach (var f in raw.Rivers)
                {
                    if (f.B.Length < 4) continue;
                    var lines = ReadRings(f.L);
                    if (lines.Count == 0) continue;
                    rivers.Add(new RiverLine(f.N, BboxOf(f.B), lines));
                }
            }
            catch
            {
                // Best-effort, come WorldBordersService: se il resource manca/è
                // corrotto, la mini-mappa disegna semplicemente senza laghi/fiumi
                // invece di far fallire l'intera generazione del PDF.
                lakes = new List<BorderPolygon>();
                rivers = new List<RiverLine>();
            }
        }
    }
}
