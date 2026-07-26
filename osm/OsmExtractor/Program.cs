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

                // Trova il tag match - versione senza Tag? problematici
                string? matchingTag = null;
                Tag matchingTagObj = default;
                bool foundMatch = false;
                foreach (var tag in element.Tags)
                {
                    var candidate = $"{tag.Key}={tag.Value}";
                    if (TargetTags.Contains(candidate))
                    {
                        matchingTag = candidate;
                        matchingTagObj = tag;
                        foundMatch = true;
                        break;
                    }
                }

                if (!foundMatch || matchingTag == null)
                    continue;

                matched++;

                double lat = 0, lon = 0;
                if (element is Node node)
                {
                    lat = node.Latitude ?? 0;
                    lon = node.Longitude ?? 0;
                }
                else
                {
                    continue;
                }

                if (!writers.TryGetValue(matchingTag, out var writer))
                    continue;

                // Prepara i dati per il CSV
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
        }

        Console.WriteLine($"\r100% ({processed:N0} oggetti) | Completato in {(DateTime.Now - startTime).ToString(@"hh\:mm\:ss")}    ");
        Console.WriteLine();

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
}