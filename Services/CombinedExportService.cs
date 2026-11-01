// =============================================================================
// CombinedExportService.cs
//
// SINOSSI: Export combinato POI + percorsi in un unico file KML/KMZ/GPX.
//   Estratto da MainWindow per essere condiviso con la modalita' batch da
//   riga di comando (BatchRunner), senza dipendere dalla UI.
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using StradarioApp.Models;

namespace StradarioApp.Services
{
    public class CombinedExportService
    {
        private readonly PoiService      _poiSvc      = new PoiService();
        private readonly PercorsoService _percorsoSvc = new PercorsoService();

        // Unisce i <Style>/<Folder> di PoiService/PercorsoService.BuildKmlDocument
        // sotto un solo <Document> con un unico <name> combinato (Add() clona i
        // nodi già "parentati" in un altro XDocument, non serve clonarli a mano).
        internal XDocument BuildCombinedKmlDocument(bool embedIcons, List<PoiGroup> poiGroups, List<Percorso> routes)
        {
            XNamespace kml = "http://www.opengis.net/kml/2.2";
            var document = new XElement(kml + "Document",
                new XElement(kml + "name", "Stradario - POI e percorsi"));

            if (poiGroups.Count > 0)
            {
                var poiDoc = _poiSvc.BuildKmlDocument(poiGroups, embedIcons);
                var poiDocEl = poiDoc.Root!.Element(kml + "Document")!;
                RenameKmlStyleIds(poiDocEl, kml, "poi_");
                foreach (var child in poiDocEl.Elements())
                    if (child.Name != kml + "name") document.Add(child);
            }

            if (routes.Count > 0)
            {
                var routeDoc = _percorsoSvc.BuildKmlDocument(routes, embedIcons);
                var routeDocEl = routeDoc.Root!.Element(kml + "Document")!;
                RenameKmlStyleIds(routeDocEl, kml, "route_");
                foreach (var child in routeDocEl.Elements())
                    if (child.Name != kml + "name") document.Add(child);
            }

            return new XDocument(
                new XDeclaration("1.0", "UTF-8", null),
                new XElement(kml + "kml", new XAttribute("xmlns", kml.NamespaceName), document));
        }

        // I due servizi numerano gli id di <Style> ("style_N") in modo
        // indipendente (Id di PoiGroup e Id di Percorso partono entrambi da 1
        // per progetto): uniti sotto lo stesso <Document> potrebbero collidere
        // (due <Style id="style_1"> diversi). Rinomina con un prefisso per
        // tipo sia l'attributo id di <Style> sia i <styleUrl> dei Placemark
        // che li referenziano, prima di unire i frammenti.
        private static void RenameKmlStyleIds(XElement documentEl, XNamespace kml, string prefix)
        {
            foreach (var style in documentEl.Descendants(kml + "Style"))
            {
                var idAttr = style.Attribute("id");
                if (idAttr != null) idAttr.Value = prefix + idAttr.Value;
            }
            foreach (var styleUrl in documentEl.Descendants(kml + "styleUrl"))
            {
                if (styleUrl.Value.StartsWith("#"))
                    styleUrl.Value = "#" + prefix + styleUrl.Value.Substring(1);
            }
        }

        public async Task ExportKmlAsync(string path, bool embedIcons, List<PoiGroup> poiGroups, List<Percorso> routes)
        {
            var kmlDoc = BuildCombinedKmlDocument(embedIcons, poiGroups, routes);
            await using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
            await writer.WriteAsync(kmlDoc.Declaration + Environment.NewLine + kmlDoc.Root);
        }

        public async Task ExportKmzAsync(string path, List<PoiGroup> poiGroups, List<Percorso> routes)
        {
            var kmlDoc = BuildCombinedKmlDocument(embedIcons: true, poiGroups, routes);

            using var fs  = File.Create(path);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

            var kmlEntry = zip.CreateEntry("doc.kml");
            await using (var es = kmlEntry.Open())
            await using (var writer = new StreamWriter(es, new UTF8Encoding(false)))
                await writer.WriteAsync(kmlDoc.Declaration + Environment.NewLine + kmlDoc.Root);

            if (poiGroups.Count > 0)
                await _poiSvc.WriteIconEntriesAsync(zip, poiGroups);
            if (routes.Count > 0)
                await _percorsoSvc.WriteRoutePoiIconEntriesAsync(zip, routes);
        }

        public async Task ExportGpxAsync(string path, List<PoiGroup> poiGroups, List<Percorso> routes)
        {
            XNamespace gpx = "http://www.topografix.com/GPX/1/1";
            var root = new XElement(gpx + "gpx",
                new XAttribute("version", "1.1"),
                new XAttribute("creator", "StradarioApp"),
                new XAttribute("xmlns", gpx.NamespaceName));

            if (poiGroups.Count > 0)
                foreach (var wpt in _poiSvc.BuildGpxWaypoints(poiGroups))
                    root.Add(wpt);
            if (routes.Count > 0)
                foreach (var trk in _percorsoSvc.BuildGpxTracks(routes))
                    root.Add(trk);

            var gpxDoc = new XDocument(new XDeclaration("1.0", "UTF-8", null), root);
            await using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
            await writer.WriteAsync(gpxDoc.Declaration + Environment.NewLine + gpxDoc.Root);
        }
    }
}
