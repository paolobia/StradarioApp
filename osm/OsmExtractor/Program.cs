using System.Globalization;
using System.Linq;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using OsmSharp.Streams;
using OsmSharp.Tags;
using OsmSharp;

class Program
{
    static HashSet<string> TargetTags = new();

    // Deriva il nome del continente dal nome del file .pbf Geofabrik, es.
    // "europe-260721.osm.pbf" -> "europe", "australia-oceania-260721.osm.pbf"
    // -> "australia-oceania" (mantiene il trattino per i nomi composti,
    // toglie solo il suffisso numerico finale con la data).
    static string GetContinentName(string pbfPath)
    {
        string fileName = Path.GetFileName(pbfPath);
        string baseName = fileName;
        if (baseName.EndsWith(".osm.pbf", StringComparison.OrdinalIgnoreCase))
            baseName = baseName.Substring(0, baseName.Length - ".osm.pbf".Length);

        int lastDash = baseName.LastIndexOf('-');
        if (lastDash > 0 && baseName.Substring(lastDash + 1).All(char.IsDigit))
            return baseName.Substring(0, lastDash);

        return baseName;
    }

    // Trova il primo tag dell'elemento che matcha una categoria in
    // TargetTags (stesso criterio "primo che matcha vince" per Node e Way).
    static bool TryFindMatchingTag(TagsCollectionBase tags, out string? matchingTag, out Tag matchingTagObj)
    {
        matchingTag = null;
        matchingTagObj = default;
        foreach (var tag in tags)
        {
            var candidate = $"{tag.Key}={tag.Value}";
            if (TargetTags.Contains(candidate))
            {
                matchingTag = candidate;
                matchingTagObj = tag;
                return true;
            }
        }
        return false;
    }

    static void Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("Uso: dotnet run <file_pbf>");
            return;
        }

        string pbfPath = args[0];

        if (!File.Exists(pbfPath))
        {
            Console.WriteLine($"Errore: File {pbfPath} non trovato");
            return;
        }

        if (!File.Exists("CategoriePOI.txt"))
        {
            Console.WriteLine("Errore: CategoriePOI.txt non trovato");
            return;
        }

        // Carica le categorie
        var lines = File.ReadAllLines("CategoriePOI.txt");
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (!string.IsNullOrEmpty(trimmed) && !trimmed.StartsWith("#"))
                TargetTags.Add(trimmed);
        }

        if (TargetTags.Count == 0)
        {
            Console.WriteLine("Errore: Nessuna categoria caricata");
            return;
        }

        // Percorso CSV: osm_data/csv/{continente}/{key}_{value}.csv — il
        // continente si ricava dal nome del file .pbf (es.
        // "europe-260721.osm.pbf" -> "europe", "australia-oceania-260721.osm.pbf"
        // -> "australia-oceania") così ogni continente resta in file separati
        // invece di finire mescolato nello stesso CSV di categoria.
        string? pbfDir = Path.GetDirectoryName(pbfPath);
        string? parentDir = Path.GetDirectoryName(pbfDir);
        string continent = GetContinentName(pbfPath);
        string outputDir = Path.Combine(parentDir ?? ".", "csv", continent);

        // Stampa info iniziali
        long fileSize = new FileInfo(pbfPath).Length;
        Console.WriteLine($"File PBF: {pbfPath}");
        Console.WriteLine($"Continente: {continent}");
        Console.WriteLine($"Dimensione: {fileSize / (1024.0 * 1024 * 1024):F2} GB");
        Console.WriteLine($"Categorie caricate: {TargetTags.Count}");
        Console.WriteLine($"Output: {outputDir}");
        Console.WriteLine();

        // Crea la cartella CSV
        Directory.CreateDirectory(outputDir);

        // Apre un writer per ogni categoria (con CsvHelper)
        var writers = new Dictionary<string, CsvWriter>();
        foreach (var tag in TargetTags)
        {
            var fileName = tag.Replace("=", "_") + ".csv";
            var path = Path.Combine(outputDir, fileName);

            var streamWriter = new StreamWriter(path, append: true, Encoding.UTF8);

            var config = new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                Delimiter = ",",
                HasHeaderRecord = !File.Exists(path) || new FileInfo(path).Length == 0
            };

            var csvWriter = new CsvWriter(streamWriter, config);
            writers[tag] = csvWriter;
        }

        // Processa il file
        Console.WriteLine("Elaborazione in corso...");
        Console.WriteLine();

        long processed = 0;
        long matched = 0;
        long bytesRead = 0;
        int lastPercent = -1;
        DateTime startTime = DateTime.Now;

        // Way (poligoni: edifici, aree...) che matchano una categoria:
        // niente lat/lon dirette come i Node, serve calcolare un centroide
        // dai nodi che compongono il perimetro — raccolti QUI, nella stessa
        // unica passata che gestisce già i Node (le Way arrivano comunque
        // più avanti nello stesso file, non serve una passata dedicata solo
        // per trovarle). Le coordinate dei loro nodi però non sono ancora
        // note a questo punto (i Node già passati non vengono tenuti in
        // memoria: per un continente intero sarebbero miliardi, troppa RAM)
        // — richiedono un'unica passata aggiuntiva dopo questa, mirata solo
        // a risolvere gli id-nodo qui raccolti (ResolveWayCentroids sotto).
        // Relation non gestite (multipolygon con "outer"/"inner", geometria
        // già più complessa da assemblare correttamente).
        var matchedWays = new List<(string tag, long id, string name, string tags, long[] nodeIds)>();
        var neededNodeIds = new HashSet<long>();

        using (var fileStream = File.OpenRead(pbfPath))
        {
            var source = new PBFOsmStreamSource(fileStream);

            foreach (var element in source)
            {
                processed++;
                bytesRead = fileStream.Position;

                int percent = (int)((bytesRead * 100) / fileSize);

                bool shouldShow = false;
                if (percent != lastPercent)
                {
                    if (percent <= 10)
                    {
                        shouldShow = true;
                    }
                    else if (percent % 5 == 0)
                    {
                        shouldShow = true;
                    }
                }

                if (shouldShow)
                {
                    lastPercent = percent;

                    TimeSpan elapsed = DateTime.Now - startTime;
                    double totalTimeSec = elapsed.TotalSeconds;
                    double percentDone = percent / 100.0;

                    string etaStr = "calcolo...";
                    if (percent > 0)
                    {
                        double etaSec = (totalTimeSec / percentDone) - totalTimeSec;
                        if (etaSec > 0)
                        {
                            if (etaSec < 60)
                                etaStr = $"~{(int)etaSec}s";
                            else if (etaSec < 3600)
                                etaStr = $"~{(int)(etaSec / 60)}m {(int)(etaSec % 60)}s";
                            else
                                etaStr = $"~{(int)(etaSec / 3600)}h {(int)((etaSec % 3600) / 60)}m";
                        }
                    }

                    double mbPerSec = (bytesRead / (1024.0 * 1024)) / totalTimeSec;

                    Console.Write($"\r{percent}% ({processed:N0} oggetti) | {mbPerSec:F1} MB/s | ETA: {etaStr}    ");
                }

                if (element.Tags == null || element.Tags.Count == 0)
                    continue;

                if (!TryFindMatchingTag(element.Tags, out string? matchingTag, out Tag matchingTagObj) || matchingTag == null)
                    continue;

                matched++;

                if (element is Node node)
                {
                    double lat = node.Latitude ?? 0, lon = node.Longitude ?? 0;

                    if (!writers.TryGetValue(matchingTag, out var writer))
                        continue;

                    string name = element.Tags.GetValue("name") ?? "";

                    // Costruisce la stringa dei tags ESCLUDENDO il tag che ha fatto match e il tag "name" (già in colonna dedicata)
                    var allTags = element.Tags.Where(t => !t.Equals(matchingTagObj) && t.Key != "name");
                    string tags = string.Join(";", allTags.Select(t => $"{t.Key}={t.Value}"));

                    // Scrive la riga usando CsvHelper
                    writer.WriteField(element.Id);
                    writer.WriteField(lat.ToString("0.000000", CultureInfo.InvariantCulture));
                    writer.WriteField(lon.ToString("0.000000", CultureInfo.InvariantCulture));
                    writer.WriteField(name);
                    writer.WriteField(tags);
                    writer.NextRecord();
                }
                else if (element is Way way)
                {
                    var nodeIds = way.Nodes ?? Array.Empty<long>();
                    if (nodeIds.Length == 0) continue;

                    string name = element.Tags.GetValue("name") ?? "";
                    var allTags = element.Tags.Where(t => !t.Equals(matchingTagObj) && t.Key != "name");
                    string tagsStr = string.Join(";", allTags.Select(t => $"{t.Key}={t.Value}"));

                    matchedWays.Add((matchingTag, way.Id ?? 0, name, tagsStr, nodeIds));
                    foreach (var nid in nodeIds) neededNodeIds.Add(nid);
                }
                // Relation: non gestite, si scarta.
            }
        }

        Console.WriteLine($"\r100% ({processed:N0} oggetti) | Completato in {(DateTime.Now - startTime).ToString(@"hh\:mm\:ss")}    ");
        Console.WriteLine($"Way di categoria trovate: {matchedWays.Count:N0} ({neededNodeIds.Count:N0} nodi da risolvere)");
        Console.WriteLine();

        // Unica passata aggiuntiva: rilegge il file solo per risolvere le
        // coordinate dei nodi richiesti dalle way appena raccolte (mai
        // l'intero file di Node in un dizionario — qui solo la piccola
        // frazione che appartiene a una way di categoria).
        ResolveWayCentroids(pbfPath, fileSize, writers, matchedWays, neededNodeIds);

        foreach (var w in writers.Values)
        {
            w.Dispose();
        }

        // Riepilogo finale
        Console.WriteLine();
        Console.WriteLine($"Elaborazione completata!");
        Console.WriteLine($"Oggetti letti: {processed:N0}");
        Console.WriteLine($"Match trovati: {matched:N0}");
        Console.WriteLine($"Tempo impiegato: {(DateTime.Now - startTime).ToString(@"hh\:mm\:ss")}");
        Console.WriteLine($"File CSV in: {outputDir}");
    }

    static void ResolveWayCentroids(
        string pbfPath, long fileSize, Dictionary<string, CsvWriter> writers,
        List<(string tag, long id, string name, string tags, long[] nodeIds)> matchedWays,
        HashSet<long> neededNodeIds)
    {
        if (matchedWays.Count == 0) return;

        Console.WriteLine("Way (poligoni): risoluzione coordinate nodi...");
        DateTime start = DateTime.Now;

        var nodeCoords = new Dictionary<long, (float lat, float lon)>(neededNodeIds.Count);
        using (var fs = File.OpenRead(pbfPath))
        {
            var source = new PBFOsmStreamSource(fs);
            bool sawAnyNode = false;
            int lastPercent = -1;

            foreach (var element in source)
            {
                if (element is not Node node)
                {
                    // I blocchi Geofabrik sono sempre Node, poi Way, poi
                    // Relation: appena finiscono i Node non c'è più nulla da
                    // cercare in questa passata, si può fermare subito invece
                    // di continuare a scandire way/relation fino in fondo.
                    if (sawAnyNode) break;
                    continue;
                }
                sawAnyNode = true;

                int percent = (int)((fs.Position * 100) / fileSize);
                if (percent != lastPercent && (percent <= 10 || percent % 5 == 0))
                {
                    lastPercent = percent;
                    Console.Write($"\r{percent}%    ");
                }

                if (node.Id.HasValue && neededNodeIds.Contains(node.Id.Value))
                    nodeCoords[node.Id.Value] = ((float)(node.Latitude ?? 0), (float)(node.Longitude ?? 0));
            }
        }

        Console.WriteLine($"\r100% ({nodeCoords.Count:N0}/{neededNodeIds.Count:N0} nodi risolti) | {(DateTime.Now - start).ToString(@"hh\:mm\:ss")}    ");
        Console.WriteLine();

        // Centroide = centro del bounding box dei nodi risolti (stessa
        // convenzione di Overpass "out center", per restare coerenti con
        // quello che la ricerca dal vivo mostra per lo stesso elemento, e
        // con tutti i dati già estratti/pubblicati con questa stessa
        // formula — vedi CLAUDE.md/osm/CLAUDE.md).
        int written = 0, skippedUnresolved = 0;
        foreach (var w in matchedWays)
        {
            double minLat = 90, maxLat = -90, minLon = 180, maxLon = -180;
            int resolved = 0;
            foreach (var nid in w.nodeIds)
            {
                if (!nodeCoords.TryGetValue(nid, out var c)) continue;
                resolved++;
                if (c.lat < minLat) minLat = c.lat;
                if (c.lat > maxLat) maxLat = c.lat;
                if (c.lon < minLon) minLon = c.lon;
                if (c.lon > maxLon) maxLon = c.lon;
            }
            // Way al bordo dell'estratto continentale: alcuni nodi possono
            // riferirsi a un file adiacente non incluso qui. Si scarta solo
            // se NESSUN nodo è stato risolto (centroide impossibile);
            // altrimenti il bbox parziale resta comunque una posizione
            // ragionevole.
            if (resolved == 0) { skippedUnresolved++; continue; }

            if (!writers.TryGetValue(w.tag, out var writer)) continue;
            double lat = (minLat + maxLat) / 2.0, lon = (minLon + maxLon) / 2.0;

            writer.WriteField(w.id);
            writer.WriteField(lat.ToString("0.000000", CultureInfo.InvariantCulture));
            writer.WriteField(lon.ToString("0.000000", CultureInfo.InvariantCulture));
            writer.WriteField(w.name);
            writer.WriteField(w.tags);
            writer.NextRecord();
            written++;
        }

        Console.WriteLine($"Way scritte: {written:N0} (scartate per nodi irrisolvibili: {skippedUnresolved:N0})");
    }
}
