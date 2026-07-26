// =============================================================================
// Services/PoiService.cs
//
// SINOSSI: Gestione dei gruppi di POI (creazione ID) e import/export KMZ.
//   - Un file .kmz è uno zip contenente un file .kml (XML) più eventuali
//     risorse (qui: le icone dei gruppi, incorporate come PNG).
//   - Export: un <Folder> KML per gruppo, con <Style> che referenzia l'icona
//     del gruppo incorporata nello zip, e un <Placemark> per ogni POI.
//   - Import: accetta sia .kmz (zip) che .kml grezzo; i Placemark dentro un
//     Folder diventano un gruppo, quelli fuori da qualunque Folder finiscono
//     in un gruppo di fallback "Importati". Non essendo garantito che il KMZ
//     esterno usi la nostra libreria icone, l'icona/colore assegnati sono
//     quelli di default (Pin, colore di palette).
//   - Nessun pacchetto NuGet aggiuntivo: System.IO.Compression e System.Xml.Linq
//     fanno parte dell'SDK .NET.
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
    public class PoiService
    {
        private const string KmlNamespace = "http://www.opengis.net/kml/2.2";
        private const string GpxNamespace = "http://www.topografix.com/GPX/1/1";
        private const int    IconPixelSize = 64;

        // Ritorna un nuovo ID univoco per un gruppo (max esistente + 1)
        public int GetNextGroupId(List<PoiGroup> groups)
        {
            int max = 0;
            foreach (var g in groups)
                if (g.Id > max) max = g.Id;
            return max + 1;
        }

        // Ritorna un nuovo ID univoco per un POI all'interno del gruppo (max esistente + 1)
        public int GetNextItemId(PoiGroup group)
        {
            int max = 0;
            foreach (var it in group.Items)
                if (it.Id > max) max = it.Id;
            return max + 1;
        }

        // ---------------------------------------------------------------
        // EXPORT
        // ---------------------------------------------------------------

        // Costruisce il documento KML condiviso da KMZ e KML: "embedIcons"
        // controlla se lo stile referenzia un'icona PNG incorporata (solo
        // possibile dentro un contenitore zip) oppure solo il colore del
        // gruppo (per l'export .kml grezzo, che non ha dove incorporare i PNG).
        // internal (non private): riusato da MainWindow per l'export combinato
        // POI+percorsi in un unico KML/KMZ (OnExportAll), che assembla i
        // <Style>/<Folder> di questo documento insieme a quelli prodotti da
        // PercorsoService.BuildKmlDocument sotto un solo <Document>.
        internal XDocument BuildKmlDocument(List<PoiGroup> groups, bool embedIcons)
        {
            XNamespace kml = KmlNamespace;

            var document = new XElement(kml + "Document",
                new XElement(kml + "name", "Stradario - Punti di interesse"));

            foreach (var g in groups)
            {
                var iconStyle = embedIcons
                    ? new XElement(kml + "IconStyle",
                        new XElement(kml + "Icon", new XElement(kml + "href", $"files/icon_{g.Id}.png")))
                    : new XElement(kml + "IconStyle",
                        new XElement(kml + "color", HexToKmlColor(g.ColorHex)));

                document.Add(new XElement(kml + "Style",
                    new XAttribute("id", $"style_{g.Id}"),
                    iconStyle));

                var folder = new XElement(kml + "Folder",
                    new XElement(kml + "name", g.Name));
                if (!string.IsNullOrWhiteSpace(g.Description))
                    folder.Add(new XElement(kml + "description", g.Description));

                foreach (var item in g.Items)
                {
                    var placemark = new XElement(kml + "Placemark",
                        new XElement(kml + "name", item.Label));
                    if (!string.IsNullOrWhiteSpace(item.Description))
                        placemark.Add(new XElement(kml + "description", item.Description));
                    placemark.Add(new XElement(kml + "styleUrl", $"#style_{g.Id}"));
                    // Simmetrico all'import (GcjTransform.Gcj02ToWgs84): un
                    // punto che cade in Cina viene qui ri-corretto da WGS84
                    // (usato internamente) a GCJ-02, così riaperto in un'app
                    // di mappe cinese torna a coincidere con la posizione
                    // mostrata da quell'app invece di apparire spostato.
                    var (expLat, expLon) = GcjTransform.Wgs84ToGcj02ForExport(item.Lat, item.Lon);
                    placemark.Add(new XElement(kml + "Point",
                        new XElement(kml + "coordinates",
                            $"{expLon.ToString(CultureInfo.InvariantCulture)},{expLat.ToString(CultureInfo.InvariantCulture)},0")));
                    folder.Add(placemark);
                }

                document.Add(folder);
            }

            return new XDocument(
                new XDeclaration("1.0", "UTF-8", null),
                new XElement(kml + "kml", new XAttribute("xmlns", KmlNamespace), document));
        }

        // KML usa il formato colore "aabbggrr" (alpha, poi BGR invertito)
        private static string HexToKmlColor(string hex)
        {
            var c = PoiIconRenderer.ParseColor(hex);
            return $"ff{c.Blue:x2}{c.Green:x2}{c.Red:x2}";
        }

        public async Task ExportKmzAsync(List<PoiGroup> groups, string path)
        {
            var kmlDoc = BuildKmlDocument(groups, embedIcons: true);

            using var fs  = File.Create(path);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

            var kmlEntry = zip.CreateEntry("doc.kml");
            await using (var es = kmlEntry.Open())
            await using (var writer = new StreamWriter(es, new UTF8Encoding(false)))
            {
                await writer.WriteAsync(kmlDoc.Declaration + Environment.NewLine + kmlDoc.Root);
            }

            await WriteIconEntriesAsync(zip, groups);
        }

        // Scrive le icone PNG dei gruppi ("files/icon_{id}.png") in uno zip
        // già aperto: estratto da ExportKmzAsync per essere riusato anche
        // dall'export combinato POI+percorsi in un unico KMZ (v. MainWindow.OnExportAll).
        internal async Task WriteIconEntriesAsync(ZipArchive zip, List<PoiGroup> groups)
        {
            foreach (var g in groups)
            {
                using var bmp   = PoiIconRenderer.RenderToBitmap(g.Icon, PoiIconRenderer.ParseColor(g.ColorHex), IconPixelSize);
                using var data  = bmp.Encode(SKEncodedImageFormat.Png, 100);
                var entry = zip.CreateEntry($"files/icon_{g.Id}.png");
                await using var entryStream = entry.Open();
                data.SaveTo(entryStream);
            }
        }

        // Export come .kml grezzo (non zippato): stessa struttura del KMZ ma
        // senza icone PNG incorporate (non c'è un contenitore per ospitarle),
        // lo stile usa solo il colore del gruppo.
        public async Task ExportKmlAsync(List<PoiGroup> groups, string path)
        {
            var kmlDoc = BuildKmlDocument(groups, embedIcons: false);
            await using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
            await writer.WriteAsync(kmlDoc.Declaration + Environment.NewLine + kmlDoc.Root);
        }

        // Export come .gpx: ogni POI diventa un <wpt>. Il formato GPX non ha
        // un concetto di cartella/gruppo, quindi il raggruppamento si perde
        // (tutti i POI di tutti i gruppi finiscono in una lista piatta di
        // waypoint) e non c'è modo di preservare icona/colore del gruppo.
        public async Task ExportGpxAsync(List<PoiGroup> groups, string path)
        {
            XNamespace gpx = GpxNamespace;
            var root = new XElement(gpx + "gpx",
                new XAttribute("version", "1.1"),
                new XAttribute("creator", "StradarioApp"),
                new XAttribute("xmlns", GpxNamespace));

            foreach (var wpt in BuildGpxWaypoints(groups))
                root.Add(wpt);

            var gpxDoc = new XDocument(new XDeclaration("1.0", "UTF-8", null), root);
            await using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
            await writer.WriteAsync(gpxDoc.Declaration + Environment.NewLine + gpxDoc.Root);
        }

        // Estratto da ExportGpxAsync: riusato dall'export combinato
        // POI+percorsi in un unico GPX (v. MainWindow.OnExportAll), dove
        // waypoint e tracce finiscono nello stesso <gpx> root.
        internal List<XElement> BuildGpxWaypoints(List<PoiGroup> groups)
        {
            XNamespace gpx = GpxNamespace;
            var result = new List<XElement>();

            foreach (var g in groups)
            {
                foreach (var item in g.Items)
                {
                    var (expLat, expLon) = GcjTransform.Wgs84ToGcj02ForExport(item.Lat, item.Lon);
                    var wpt = new XElement(gpx + "wpt",
                        new XAttribute("lat", expLat.ToString(CultureInfo.InvariantCulture)),
                        new XAttribute("lon", expLon.ToString(CultureInfo.InvariantCulture)));
                    if (!string.IsNullOrWhiteSpace(item.Label))
                        wpt.Add(new XElement(gpx + "name", item.Label));
                    if (!string.IsNullOrWhiteSpace(item.Description))
                        wpt.Add(new XElement(gpx + "desc", item.Description));
                    result.Add(wpt);
                }
            }

            return result;
        }

        // ---------------------------------------------------------------
        // IMPORT
        // ---------------------------------------------------------------
        // Ritorna i gruppi letti dal file, con ID già liberi rispetto a
        // "project" (pronti per essere aggiunti/appesi al progetto corrente).
        // Accetta .kmz/.kml (Placemark) e .gpx (waypoint <wpt>).
        public async Task<List<PoiGroup>> ImportKmzAsync(string path, StradarioProject project)
        {
            byte[] raw = await File.ReadAllBytesAsync(path);
            if (raw.Length == 0)
                throw new InvalidDataException($"Il file \"{path}\" risulta vuoto (0 byte letti).");
            return ImportKmz(raw, project, Path.GetFileNameWithoutExtension(path));
        }

        // Variante che parte da byte già letti (usata dall'import unificato in
        // MainWindow, che legge il file tramite lo Stream fornito da Avalonia
        // StorageProvider invece che da un path locale: alcuni backend dei
        // file picker su Linux — es. xdg-desktop-portal — possono restituire
        // un percorso che non è ancora garantito leggibile con File.ReadAllBytes
        // nello stesso istante, mentre lo Stream del picker è sempre affidabile).
        // "fileNameHint" (senza estensione) viene usato come nome del gruppo
        // quando il file non ne fornisce uno (Folder senza <name>, o POI/wpt
        // non raggruppati in nessuna Folder).
        public List<PoiGroup> ImportKmz(byte[] raw, StradarioProject project, string? fileNameHint = null)
        {
            if (raw.Length == 0)
                throw new InvalidDataException("Il file selezionato risulta vuoto (0 byte letti).");

            string fallbackName = string.IsNullOrWhiteSpace(fileNameHint) ? "Importati" : fileNameHint!;

            var xdoc = KmlIo.LoadDocument(raw);
            var root = xdoc.Root ?? throw new InvalidDataException("File KML/KMZ non valido.");

            var importedGroups = string.Equals(root.Name.LocalName, "gpx", StringComparison.OrdinalIgnoreCase)
                ? ParseGpxWaypoints(root, fallbackName)
                : ParseKmlGroups(xdoc, root, fallbackName);

            // Rinumera per non collidere con i gruppi/POI già presenti nel progetto
            int groupIdCursor = GetNextGroupId(project.PoiGroups);
            foreach (var g in importedGroups)
            {
                g.Id = groupIdCursor++;
                int itemIdCursor = 1;
                foreach (var it in g.Items)
                    it.Id = itemIdCursor++;
            }

            return importedGroups;
        }

        // Ritorna il nome KML/GPX sanificato (vedi KmlIo.SanitizeName), o il
        // fallback se assente/vuoto (anche dopo la sanificazione). Applica
        // anche la preferenza ASCII (v. Services/AsciiText): un nome di
        // cartella in script non latino viene sostituito con una variante
        // ASCII trovata nella description, o ripulito, non lasciato passare
        // illeggibile.
        private static string ResolveName(string? raw, string? description, string fallback)
        {
            if (string.IsNullOrWhiteSpace(raw)) return fallback;
            string sanitized = KmlIo.SanitizeName(raw);
            if (sanitized.Length == 0) return fallback;
            return AsciiText.PickAsciiLabel(sanitized, description, fallback);
        }

        private static List<PoiGroup> ParseKmlGroups(XDocument xdoc, XElement root, string fallbackName)
        {
            XNamespace ns = root.Name.Namespace;
            var documentEl = xdoc.Descendants(ns + "Document").FirstOrDefault() ?? root;

            var importedGroups = new List<PoiGroup>();
            int nextGroupId = 1;

            foreach (var folder in documentEl.Elements(ns + "Folder"))
            {
                string folderDesc = folder.Element(ns + "description")?.Value?.Trim() ?? "";
                var group = new PoiGroup
                {
                    Id          = nextGroupId++,
                    Name        = ResolveName(folder.Element(ns + "name")?.Value, folderDesc, fallbackName),
                    Description = AsciiText.SanitizeMultilineAscii(folderDesc),
                    Icon        = PoiIconType.Pin,
                    ColorHex    = PoiIconRenderer.DefaultColorHex
                };
                ParsePlacemarks(folder, ns, group);
                if (group.Items.Count > 0)
                    importedGroups.Add(group);
            }

            // Placemark direttamente sotto <Document>/<kml>, non in una Folder
            var looseGroup = new PoiGroup
            {
                Id       = nextGroupId++,
                Name     = fallbackName,
                Icon     = PoiIconType.Pin,
                ColorHex = PoiIconRenderer.DefaultColorHex
            };
            ParsePlacemarks(documentEl, ns, looseGroup);
            if (looseGroup.Items.Count > 0)
                importedGroups.Add(looseGroup);

            return importedGroups;
        }

        private static void ParsePlacemarks(XElement container, XNamespace ns, PoiGroup group)
        {
            foreach (var pm in container.Elements(ns + "Placemark"))
            {
                var coordText = pm.Element(ns + "Point")?.Element(ns + "coordinates")?.Value?.Trim();
                if (string.IsNullOrEmpty(coordText)) continue;

                var parts = coordText.Split(',');
                if (parts.Length < 2) continue;
                if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double lon)) continue;
                if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double lat)) continue;
                (lat, lon) = GcjTransform.Gcj02ToWgs84(lat, lon);

                string rawLabel = KmlIo.SanitizeName(pm.Element(ns + "name")?.Value ?? "");
                string rawDesc  = pm.Element(ns + "description")?.Value?.Trim() ?? "";

                group.Items.Add(new PoiItem
                {
                    Label       = AsciiText.PickAsciiLabel(rawLabel, rawDesc, ""),
                    Description = AsciiText.SanitizeMultilineAscii(rawDesc),
                    Lon         = lon,
                    Lat         = lat
                });
            }
        }

        // GPX non raggruppa i waypoint in cartelle: tutti i <wpt> del file
        // finiscono in un unico gruppo, con il nome del file come fallback.
        private static List<PoiGroup> ParseGpxWaypoints(XElement root, string fallbackName)
        {
            XNamespace ns = root.Name.Namespace;

            var group = new PoiGroup
            {
                Id       = 1,
                Name     = fallbackName,
                Icon     = PoiIconType.Pin,
                ColorHex = PoiIconRenderer.DefaultColorHex
            };

            foreach (var wpt in root.Elements(ns + "wpt"))
            {
                var latAttr = wpt.Attribute("lat")?.Value;
                var lonAttr = wpt.Attribute("lon")?.Value;
                if (string.IsNullOrEmpty(latAttr) || string.IsNullOrEmpty(lonAttr)) continue;
                if (!double.TryParse(lonAttr, NumberStyles.Float, CultureInfo.InvariantCulture, out double lon)) continue;
                if (!double.TryParse(latAttr, NumberStyles.Float, CultureInfo.InvariantCulture, out double lat)) continue;
                (lat, lon) = GcjTransform.Gcj02ToWgs84(lat, lon);

                string rawLabel = KmlIo.SanitizeName(wpt.Element(ns + "name")?.Value ?? "");
                string rawDesc  = wpt.Element(ns + "desc")?.Value?.Trim()
                                  ?? wpt.Element(ns + "cmt")?.Value?.Trim() ?? "";

                group.Items.Add(new PoiItem
                {
                    Label       = AsciiText.PickAsciiLabel(rawLabel, rawDesc, ""),
                    Description = AsciiText.SanitizeMultilineAscii(rawDesc),
                    Lon         = lon,
                    Lat         = lat
                });
            }

            return group.Items.Count > 0 ? new List<PoiGroup> { group } : new List<PoiGroup>();
        }
    }
}
