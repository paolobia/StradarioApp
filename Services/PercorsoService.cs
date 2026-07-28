// =============================================================================
// Services/PercorsoService.cs
//
// SINOSSI: Gestione dei percorsi (creazione ID) e import/export KMZ.
//   - Export: un <Folder> "Percorsi" con uno <Style>/<Placemark><LineString>
//     per ciascun percorso (nessuna icona da incorporare: non serve un file
//     PNG come per i POI, solo colore/spessore della linea).
//   - Import: accetta sia .kmz (zip) che .kml grezzo; ogni <Placemark> con
//     <LineString> (dentro o fuori da una Folder) diventa un percorso. Il
//     colore viene letto dallo <Style> collegato se presente, altrimenti
//     assegnato di default.
//   - Nessun pacchetto NuGet aggiuntivo, come PoiService.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using SkiaSharp;
using StradarioApp.Models;

namespace StradarioApp.Services
{
    public class PercorsoService
    {
        private const string KmlNamespace = "http://www.opengis.net/kml/2.2";
        private const string GpxNamespace = "http://www.topografix.com/GPX/1/1";

        // Ritorna un nuovo ID univoco per un percorso (max esistente + 1)
        public int GetNextId(List<Percorso> routes)
        {
            int max = 0;
            foreach (var r in routes)
                if (r.Id > max) max = r.Id;
            return max + 1;
        }

        // ---------------------------------------------------------------
        // EXPORT
        // ---------------------------------------------------------------
        // internal (non private): riusato da MainWindow per l'export
        // combinato POI+percorsi in un unico KML/KMZ (v. MainWindow.OnExportAll).
        internal XDocument BuildKmlDocument(List<Percorso> routes)
        {
            XNamespace kml = KmlNamespace;

            var document = new XElement(kml + "Document",
                new XElement(kml + "name", "Stradario - Percorsi"));

            var folder = new XElement(kml + "Folder", new XElement(kml + "name", "Percorsi"));

            foreach (var r in routes)
            {
                document.Add(new XElement(kml + "Style",
                    new XAttribute("id", $"style_{r.Id}"),
                    new XElement(kml + "LineStyle",
                        new XElement(kml + "color", HexToKmlColor(r.ColorHex)),
                        new XElement(kml + "width", 3))));

                var placemark = new XElement(kml + "Placemark",
                    new XElement(kml + "name", r.Label));
                if (!string.IsNullOrWhiteSpace(r.Description))
                    placemark.Add(new XElement(kml + "description", r.Description));
                placemark.Add(new XElement(kml + "styleUrl", $"#style_{r.Id}"));

                // Simmetrico all'import (GcjTransform.Gcj02ToWgs84): i punti
                // che cadono in Cina vengono qui ri-corretti da WGS84 a
                // GCJ-02, vedi commento analogo in PoiService.BuildKmlDocument.
                string coords = string.Join(" ", r.Points.Select(p =>
                {
                    var (expLat, expLon) = GcjTransform.Wgs84ToGcj02ForExport(p.Lat, p.Lon);
                    return $"{expLon.ToString(CultureInfo.InvariantCulture)},{expLat.ToString(CultureInfo.InvariantCulture)},0";
                }));
                placemark.Add(new XElement(kml + "LineString",
                    new XElement(kml + "tessellate", 1),
                    new XElement(kml + "coordinates", coords)));

                folder.Add(placemark);
            }

            document.Add(folder);

            return new XDocument(
                new XDeclaration("1.0", "UTF-8", null),
                new XElement(kml + "kml", new XAttribute("xmlns", KmlNamespace), document));
        }

        public async Task ExportKmzAsync(List<Percorso> routes, string path)
        {
            var kmlDoc = BuildKmlDocument(routes);

            using var fs  = File.Create(path);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

            var kmlEntry = zip.CreateEntry("doc.kml");
            await using var es     = kmlEntry.Open();
            await using var writer = new StreamWriter(es, new UTF8Encoding(false));
            await writer.WriteAsync(kmlDoc.Declaration + Environment.NewLine + kmlDoc.Root);
        }

        // Export come .kml grezzo (non zippato): stesso documento del KMZ,
        // scritto direttamente su file invece che dentro uno zip (i percorsi
        // non hanno assets da incorporare, quindi il contenuto è identico).
        public async Task ExportKmlAsync(List<Percorso> routes, string path)
        {
            var kmlDoc = BuildKmlDocument(routes);
            await using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
            await writer.WriteAsync(kmlDoc.Declaration + Environment.NewLine + kmlDoc.Root);
        }

        // Export come .gpx: ogni percorso diventa un <trk> con un solo
        // <trkseg>. GPX non ha un campo colore standard per le tracce, quindi
        // il colore del percorso non viene preservato.
        public async Task ExportGpxAsync(List<Percorso> routes, string path)
        {
            XNamespace gpx = GpxNamespace;
            var root = new XElement(gpx + "gpx",
                new XAttribute("version", "1.1"),
                new XAttribute("creator", "StradarioApp"),
                new XAttribute("xmlns", GpxNamespace));

            foreach (var trk in BuildGpxTracks(routes))
                root.Add(trk);

            var gpxDoc = new XDocument(new XDeclaration("1.0", "UTF-8", null), root);
            await using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
            await writer.WriteAsync(gpxDoc.Declaration + Environment.NewLine + gpxDoc.Root);
        }

        // Estratto da ExportGpxAsync: riusato dall'export combinato
        // POI+percorsi in un unico GPX (v. MainWindow.OnExportAll).
        internal List<XElement> BuildGpxTracks(List<Percorso> routes)
        {
            XNamespace gpx = GpxNamespace;
            var result = new List<XElement>();

            foreach (var r in routes)
            {
                var trk = new XElement(gpx + "trk");
                if (!string.IsNullOrWhiteSpace(r.Label))
                    trk.Add(new XElement(gpx + "name", r.Label));
                if (!string.IsNullOrWhiteSpace(r.Description))
                    trk.Add(new XElement(gpx + "desc", r.Description));

                var trkseg = new XElement(gpx + "trkseg");
                foreach (var p in r.Points)
                {
                    var (expLat, expLon) = GcjTransform.Wgs84ToGcj02ForExport(p.Lat, p.Lon);
                    trkseg.Add(new XElement(gpx + "trkpt",
                        new XAttribute("lat", expLat.ToString(CultureInfo.InvariantCulture)),
                        new XAttribute("lon", expLon.ToString(CultureInfo.InvariantCulture))));
                }
                trk.Add(trkseg);

                result.Add(trk);
            }

            return result;
        }

        // Export come .csv: una riga per PUNTO del percorso (etichetta/colore/
        // descrizione ripetuti su ogni riga dello stesso percorso) — formato
        // piatto tabellare, non annidato come KML. Sempre WGS84, stesso
        // motivo di PoiService.ExportCsvAsync: il CSV è un formato nativo
        // dell'app, nessuna correzione/domanda GCJ-02 in export o import.
        public async Task ExportCsvAsync(List<Percorso> routes, string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine(CsvIo.BuildLine(new[] { "Percorso", "Colore", "Descrizione", "IndicePunto", "Lat", "Lon" }));
            foreach (var r in routes)
                for (int i = 0; i < r.Points.Count; i++)
                    sb.AppendLine(CsvIo.BuildLine(new[]
                    {
                        r.Label, r.ColorHex, r.Description, i.ToString(CultureInfo.InvariantCulture),
                        r.Points[i].Lat.ToString(CultureInfo.InvariantCulture),
                        r.Points[i].Lon.ToString(CultureInfo.InvariantCulture)
                    }));

            await using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
            await writer.WriteAsync(sb.ToString());
        }

        // Import da .csv (stesso formato di ExportCsvAsync): le righe con lo
        // stesso nome percorso vengono raggruppate, nell'ordine di prima
        // comparsa, e i punti riordinati per IndicePunto (se assente/non
        // numerico, si tiene semplicemente l'ordine delle righe nel file —
        // corretto comunque per un file scritto da ExportCsvAsync, che le
        // scrive già in ordine). Colore/descrizione presi dalla prima riga
        // del percorso. Righe con Lat/Lon mancanti o non numerici saltate.
        public List<Percorso> ImportCsv(byte[] raw, StradarioProject project)
        {
            if (raw.Length == 0)
                throw new InvalidDataException("Il file selezionato risulta vuoto (0 byte letti).");

            string text = CsvIo.DecodeText(raw);
            var rows = CsvIo.ParseAll(text);
            if (rows.Count == 0) return new List<Percorso>();

            var header = rows[0];
            int idxPercorso = Array.IndexOf(header, "Percorso");
            int idxColore   = Array.IndexOf(header, "Colore");
            int idxDesc     = Array.IndexOf(header, "Descrizione");
            int idxIndice   = Array.IndexOf(header, "IndicePunto");
            int idxLat      = Array.IndexOf(header, "Lat");
            int idxLon      = Array.IndexOf(header, "Lon");
            if (idxLat < 0 || idxLon < 0)
                throw new InvalidDataException("CSV non riconosciuto come export Percorsi di StradarioApp (mancano le colonne Lat/Lon).");

            var routesByName = new Dictionary<string, (Percorso Route, List<(int Index, GeoPoint Point)> Points)>(StringComparer.OrdinalIgnoreCase);
            var orderedNames = new List<string>();
            int fallbackCursor = 1;

            for (int i = 1; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row.Length == 1 && row[0].Length == 0) continue; // riga vuota finale

                string Get(int idx) => idx >= 0 && idx < row.Length ? row[idx] : "";

                if (!double.TryParse(Get(idxLat), NumberStyles.Float, CultureInfo.InvariantCulture, out double lat)) continue;
                if (!double.TryParse(Get(idxLon), NumberStyles.Float, CultureInfo.InvariantCulture, out double lon)) continue;

                string label = idxPercorso >= 0 && !string.IsNullOrWhiteSpace(Get(idxPercorso)) ? Get(idxPercorso) : $"Percorso {fallbackCursor}";
                if (!routesByName.TryGetValue(label, out var entry))
                {
                    string color = idxColore >= 0 && SKColor.TryParse(Get(idxColore), out _) ? Get(idxColore) : PercorsoRenderer.DefaultColorHex;
                    var route = new Percorso { Label = label, ColorHex = color, Description = idxDesc >= 0 ? Get(idxDesc) : "" };
                    entry = (route, new List<(int, GeoPoint)>());
                    routesByName[label] = entry;
                    orderedNames.Add(label);
                    if (idxPercorso < 0 || string.IsNullOrWhiteSpace(Get(idxPercorso))) fallbackCursor++;
                }

                int pointIndex = idxIndice >= 0 && int.TryParse(Get(idxIndice), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedIdx)
                    ? parsedIdx : entry.Points.Count;
                entry.Points.Add((pointIndex, new GeoPoint { Lat = lat, Lon = lon }));
            }

            var imported = new List<Percorso>();
            foreach (var name in orderedNames)
            {
                var (route, points) = routesByName[name];
                route.Points = points.OrderBy(p => p.Index).Select(p => p.Point).ToList();
                if (route.Points.Count < 2) continue; // un percorso con meno di 2 punti non è disegnabile
                imported.Add(route);
            }

            int idCursor = GetNextId(project.Percorsi);
            foreach (var r in imported)
                r.Id = idCursor++;

            return imported;
        }

        // ---------------------------------------------------------------
        // IMPORT
        // ---------------------------------------------------------------
        // Ritorna i percorsi letti dal file, con ID già liberi rispetto a
        // "project" (pronti per essere aggiunti/appesi al progetto corrente).
        public async Task<List<Percorso>> ImportKmzAsync(string path, StradarioProject project)
        {
            byte[] raw = await File.ReadAllBytesAsync(path);
            if (raw.Length == 0)
                throw new InvalidDataException($"Il file \"{path}\" risulta vuoto (0 byte letti).");
            return ImportKmz(raw, project);
        }

        // Variante che parte da byte già letti (usata dall'import unificato in
        // MainWindow, che legge il file tramite lo Stream fornito da Avalonia
        // StorageProvider invece che da un path locale: vedi commento
        // analogo in PoiService.ImportKmz). Accetta .kmz/.kml (Placemark con
        // LineString) e .gpx (<trk>/<trkseg>/<trkpt> e <rte>/<rtept>).
        public List<Percorso> ImportKmz(byte[] raw, StradarioProject project)
        {
            if (raw.Length == 0)
                throw new InvalidDataException("Il file selezionato risulta vuoto (0 byte letti).");

            var xdoc = KmlIo.LoadDocument(raw);
            var root = xdoc.Root ?? throw new InvalidDataException("File KML/KMZ non valido.");

            var imported = string.Equals(root.Name.LocalName, "gpx", StringComparison.OrdinalIgnoreCase)
                ? ParseGpxRoutes(root)
                : ParseKmlRoutes(xdoc, root);

            // Rinumera per non collidere con i percorsi già presenti nel progetto
            int idCursor = GetNextId(project.Percorsi);
            foreach (var r in imported)
                r.Id = idCursor++;

            return imported;
        }

        private static List<Percorso> ParseKmlRoutes(XDocument xdoc, XElement root)
        {
            XNamespace ns = root.Name.Namespace;

            var documentEl = xdoc.Descendants(ns + "Document").FirstOrDefault() ?? root;

            // Mappa stile → colore, per risolvere styleUrl dei Placemark
            var styleColors = new Dictionary<string, string>();
            foreach (var style in documentEl.Descendants(ns + "Style"))
            {
                string? id = style.Attribute("id")?.Value;
                if (id == null) continue;
                var colorText = style.Element(ns + "LineStyle")?.Element(ns + "color")?.Value?.Trim();
                if (!string.IsNullOrEmpty(colorText))
                    styleColors[id] = KmlColorToHex(colorText);
            }

            var imported = new List<Percorso>();
            int cursor = 1;

            foreach (var pm in documentEl.Descendants(ns + "Placemark"))
            {
                var lineString = pm.Element(ns + "LineString");
                if (lineString == null) continue;

                var coordText = lineString.Element(ns + "coordinates")?.Value?.Trim();
                if (string.IsNullOrEmpty(coordText)) continue;

                var points = new List<GeoPoint>();
                foreach (var tuple in coordText.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = tuple.Split(',');
                    if (parts.Length < 2) continue;
                    if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double lon)) continue;
                    if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double lat)) continue;
                    (lat, lon) = GcjTransform.Gcj02ToWgs84(lat, lon);
                    points.Add(new GeoPoint { Lon = lon, Lat = lat });
                }
                if (points.Count < 2) continue;

                string? styleUrl = pm.Element(ns + "styleUrl")?.Value?.Trim().TrimStart('#');
                string colorHex = (styleUrl != null && styleColors.TryGetValue(styleUrl, out var c))
                    ? c : PercorsoRenderer.DefaultColorHex;
                string rawDesc = pm.Element(ns + "description")?.Value?.Trim() ?? "";

                imported.Add(new Percorso
                {
                    Id          = cursor,
                    Label       = ResolveLabel(pm.Element(ns + "name")?.Value, rawDesc, $"Percorso {cursor}"),
                    Description = AsciiText.SanitizeMultilineAscii(rawDesc),
                    ColorHex    = colorHex,
                    Points      = points
                });
                cursor++;
            }

            return imported;
        }

        // Ritorna il nome KML/GPX sanificato (vedi KmlIo.SanitizeName), o il
        // fallback se assente/vuoto (anche dopo la sanificazione). Applica
        // anche la preferenza ASCII (v. Services/AsciiText): un'etichetta in
        // script non latino viene sostituita con una variante ASCII trovata
        // nella description, o ripulita, non lasciata passare illeggibile.
        private static string ResolveLabel(string? raw, string? description, string fallback)
        {
            if (string.IsNullOrWhiteSpace(raw)) return fallback;
            string sanitized = KmlIo.SanitizeName(raw);
            if (sanitized.Length == 0) return fallback;
            return AsciiText.PickAsciiLabel(sanitized, description, fallback);
        }

        // GPX: ogni <trk> (con uno o più <trkseg>) e ogni <rte> diventano un
        // percorso; GPX non ha un concetto di colore per traccia, quindi si
        // usa sempre il colore di default.
        private static List<Percorso> ParseGpxRoutes(XElement root)
        {
            XNamespace ns = root.Name.Namespace;
            var imported = new List<Percorso>();
            int cursor = 1;

            static bool TryReadPoint(XElement pt, out GeoPoint point)
            {
                point = new GeoPoint();
                var latAttr = pt.Attribute("lat")?.Value;
                var lonAttr = pt.Attribute("lon")?.Value;
                if (string.IsNullOrEmpty(latAttr) || string.IsNullOrEmpty(lonAttr)) return false;
                if (!double.TryParse(lonAttr, NumberStyles.Float, CultureInfo.InvariantCulture, out double lon)) return false;
                if (!double.TryParse(latAttr, NumberStyles.Float, CultureInfo.InvariantCulture, out double lat)) return false;
                (lat, lon) = GcjTransform.Gcj02ToWgs84(lat, lon);
                point = new GeoPoint { Lon = lon, Lat = lat };
                return true;
            }

            foreach (var trk in root.Elements(ns + "trk"))
            {
                var points = new List<GeoPoint>();
                foreach (var trkpt in trk.Descendants(ns + "trkpt"))
                    if (TryReadPoint(trkpt, out var p)) points.Add(p);
                if (points.Count < 2) continue;

                string rawTrkDesc = trk.Element(ns + "desc")?.Value?.Trim() ?? "";
                imported.Add(new Percorso
                {
                    Id          = cursor,
                    Label       = ResolveLabel(trk.Element(ns + "name")?.Value, rawTrkDesc, $"Percorso {cursor}"),
                    Description = AsciiText.SanitizeMultilineAscii(rawTrkDesc),
                    ColorHex    = PercorsoRenderer.DefaultColorHex,
                    Points      = points
                });
                cursor++;
            }

            foreach (var rte in root.Elements(ns + "rte"))
            {
                var points = new List<GeoPoint>();
                foreach (var rtept in rte.Elements(ns + "rtept"))
                    if (TryReadPoint(rtept, out var p)) points.Add(p);
                if (points.Count < 2) continue;

                string rawRteDesc = rte.Element(ns + "desc")?.Value?.Trim() ?? "";
                imported.Add(new Percorso
                {
                    Id          = cursor,
                    Label       = ResolveLabel(rte.Element(ns + "name")?.Value, rawRteDesc, $"Percorso {cursor}"),
                    Description = AsciiText.SanitizeMultilineAscii(rawRteDesc),
                    ColorHex    = PercorsoRenderer.DefaultColorHex,
                    Points      = points
                });
                cursor++;
            }

            return imported;
        }

        // KML usa il formato colore "aabbggrr" (alpha, poi BGR invertito),
        // mentre l'app usa "#RRGGBB": conversione nei due sensi.
        private static string HexToKmlColor(string hex)
        {
            var c = PercorsoRenderer.ParseColor(hex);
            return $"ff{c.Blue:x2}{c.Green:x2}{c.Red:x2}";
        }

        private static string KmlColorToHex(string kmlColor)
        {
            kmlColor = kmlColor.Trim();
            if (kmlColor.Length != 8) return PercorsoRenderer.DefaultColorHex;
            try
            {
                string bb = kmlColor.Substring(2, 2);
                string gg = kmlColor.Substring(4, 2);
                string rr = kmlColor.Substring(6, 2);
                return $"#{rr}{gg}{bb}".ToUpperInvariant();
            }
            catch
            {
                return PercorsoRenderer.DefaultColorHex;
            }
        }
    }
}
