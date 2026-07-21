// =============================================================================
// Services/CityDatabase.cs
//
// SINOSSI: Carica e interroga il database cities500.csv (GeoNames).
//   - Caricamento lazy al primo utilizzo (singleton thread-safe)
//   - Parsing CSV con supporto valori quoted e separatore virgola
//   - FindTopCities(bounds, n): trova le n città più popolose nell'area
//   - Il file cities500.csv deve essere nella stessa cartella dell'eseguibile
//     oppure nella cartella corrente; se non trovato, prova a scaricarlo da
//     GeoNames automaticamente (DownloadAndExtract) prima di arrendersi:
//     l'utente non deve andarselo a cercare/scaricare a mano. Solo se anche
//     il download fallisce (rete assente, server irraggiungibile) la
//     funzionalità degrada a lista vuota senza eccezioni/crash, come prima.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using StradarioApp.Models;

namespace StradarioApp.Services
{
    public record CityEntry(string Name, double Lat, double Lon, long Population);

    public static class CityDatabase
    {
        // Caricamento lazy: i dati vengono letti solo alla prima chiamata
        private static List<CityEntry>? _cities;
        private static readonly object  _lock = new();

        // Percorso dove viene salvato il file scaricato automaticamente da
        // GeoNames (vedi DownloadAndExtract): in AppData, non nella cartella
        // dell'eseguibile, che su Windows può essere sotto Program Files
        // (non scrivibile senza privilegi elevati) — stessa cartella già
        // usata da DebugLog per lo stesso motivo.
        private static readonly string DownloadCachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StradarioApp", "cities500.txt");

        private const string DownloadUrl = "https://download.geonames.org/export/dump/cities500.zip";

        // Percorsi in cui cercare il file (in ordine di priorità). L'ultimo è
        // la cache del download automatico: se già scaricato in una sessione
        // precedente, viene riusato senza rifare la richiesta di rete.
        private static readonly string[] SearchPaths = new[]
        {
            "cities500.csv",
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cities500.csv"),
            Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile), "cities500.csv"),
            DownloadCachePath,
        };

        // Stato del caricamento (per mostrare nella UI se necessario)
        public static string LoadStatus { get; private set; } = "Non caricato";
        public static int    CityCount  => _cities?.Count ?? 0;

        // Forza il caricamento del file. Chiamare all'avvio per evitare
        // il ritardo al primo utilizzo. Non fa nulla se già caricato.
        public static void EnsureLoaded()
        {
            if (_cities != null) return;
            lock (_lock)
            {
                if (_cities != null) return;
                _cities = Load();
            }
        }

        // Trova le n città più popolose nell'area geografica specificata
        public static List<CityEntry> FindTopCities(GeoRect bounds, int n = 3)
        {
            EnsureLoaded();
            if (_cities == null || _cities.Count == 0)
                return new List<CityEntry>();

            return _cities
                .Where(c => c.Lon >= bounds.MinLon && c.Lon <= bounds.MaxLon &&
                            c.Lat >= bounds.MinLat && c.Lat <= bounds.MaxLat)
                .OrderByDescending(c => c.Population)
                .Take(n)
                .ToList();
        }

        // Cerca città per nome (anche parziale, case-insensitive, in
        // qualunque punto del nome — non solo "inizia con"), su tutto il
        // database mondiale, ordinate per popolazione decrescente. Usata
        // dalla voce "Ricerca una città" del combo categoria POI: a
        // differenza di FindTopCities (che filtra per area già visualizzata)
        // qui il chiamante non ha ancora un'area, è il nome digitato a
        // determinare dove andare a parare.
        // Alias italiano -> nome usato da GeoNames, per le città (poche, le
        // più note) che GeoNames registra con l'esonimo inglese/internazionale
        // invece del nome italiano (es. "Firenze" è in GeoNames "Florence":
        // cercare "firen" da solo non la troverebbe mai). Lista curata a
        // mano, non un database di traduzioni: copre i casi reali più comuni
        // (grandi città italiane + capitali europee/mondiali più note),
        // non ogni possibile corrispondenza. TODO (annotato su richiesta,
        // non ancora pianificato): se un domani si deciderà di rendere
        // multilingua l'INTERFACCIA dell'app (stringhe/menu, non solo questa
        // ricerca), questa lista andrà ripensata in quell'ottica più ampia
        // (probabilmente con i dati "alternateNames" di GeoNames invece di
        // una lista scritta a mano) invece di continuare ad allungarla qui.
        private static readonly (string Italian, string Geonames)[] CityAliases = new[]
        {
            ("Firenze", "Florence"),
            ("Roma", "Rome"),
            ("Milano", "Milan"),
            ("Napoli", "Naples"),
            ("Torino", "Turin"),
            ("Genova", "Genoa"),
            ("Venezia", "Venice"),
            ("Padova", "Padua"),
            ("Mantova", "Mantua"),
            ("Siracusa", "Syracuse"),
            ("Londra", "London"),
            ("Parigi", "Paris"),
            ("Monaco di Baviera", "Munich"),
            ("Praga", "Prague"),
            ("Varsavia", "Warsaw"),
            ("Mosca", "Moscow"),
            ("Pechino", "Beijing"),
            ("Il Cairo", "Cairo"),
            ("Atene", "Athens"),
            ("Lisbona", "Lisbon"),
            ("Bruxelles", "Brussels"),
            ("Copenaghen", "Copenhagen"),
            ("Cracovia", "Krakow"),
            ("Danzica", "Gdansk"),
            ("Zurigo", "Zurich"),
            ("Ginevra", "Geneva"),
            ("L'Aia", "The Hague"),
            ("Anversa", "Antwerp"),
        };

        public static List<CityEntry> SearchByName(string query, int maxResults = 50)
        {
            EnsureLoaded();
            if (_cities == null || _cities.Count == 0 || string.IsNullOrWhiteSpace(query))
                return new List<CityEntry>();

            string q = query.Trim();

            // Corrispondenza diretta sul nome così com'è in GeoNames (vale
            // per la stragrande maggioranza delle città, il cui nome coincide
            // in italiano e su GeoNames).
            var matches = _cities.Where(c => c.Name.Contains(q, StringComparison.OrdinalIgnoreCase));

            // Più il nome GeoNames delle città il cui alias italiano
            // corrisponde (anche parziale) alla query — vedi CityAliases.
            foreach (var (italian, geonames) in CityAliases)
            {
                if (italian.Contains(q, StringComparison.OrdinalIgnoreCase))
                    matches = matches.Concat(_cities.Where(c => c.Name.Equals(geonames, StringComparison.OrdinalIgnoreCase)));
            }

            return matches
                .Distinct()
                .OrderByDescending(c => c.Population)
                .Take(maxResults)
                .ToList();
        }

        // Genera la stringa descrizione: "Roma, Milano, Napoli" (o meno se non trovate)
        public static string Describe(GeoRect bounds, int n = 3)
        {
            var cities = FindTopCities(bounds, n);
            if (cities.Count == 0) return "";
            return string.Join(", ", cities.Select(c => c.Name));
        }

        // Carica il CSV (o il dump grezzo GeoNames scaricato automaticamente)
        // e restituisce la lista delle città
        private static List<CityEntry> Load()
        {
            string? filePath = SearchPaths.FirstOrDefault(File.Exists);

            // Non trovato in nessuno dei percorsi noti: prova a scaricarlo da
            // GeoNames invece di arrendersi subito, così l'utente non deve
            // andare a cercarselo/scaricarlo a mano (vedi sinossi in testa
            // al file). Bloccante ma innocuo: EnsureLoaded gira già su un
            // thread in background (Program.cs), o comunque una tantum al
            // primo avvio.
            if (filePath == null)
                filePath = DownloadAndExtract();

            if (filePath == null)
            {
                LoadStatus = "File cities500.csv non trovato e download automatico non riuscito";
                return new List<CityEntry>();
            }

            var result = Path.GetExtension(filePath).Equals(".txt", StringComparison.OrdinalIgnoreCase)
                ? ParseGeonamesRawDump(filePath)
                : ParseLocalCsv(filePath);

            LoadStatus = $"Caricate {result.Count:N0} città da {Path.GetFileName(filePath)}";
            return result;
        }

        // Scarica lo zip ufficiale GeoNames (cities500.zip, ~11 MB) ed
        // estrae cities500.txt nella cache locale (DownloadCachePath).
        // Ritorna null (senza eccezioni) se rete assente o server
        // irraggiungibile: il chiamante (Load) degrada a lista vuota,
        // esattamente come nel caso "file non trovato" di prima.
        private static string? DownloadAndExtract()
        {
            try
            {
                LoadStatus = "cities500.csv non trovato: scarico il database città da GeoNames...";

                string? dir = Path.GetDirectoryName(DownloadCachePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("StradarioApp/1.0 (educational use)");
                byte[] zipBytes = http.GetByteArrayAsync(DownloadUrl).GetAwaiter().GetResult();

                using var zipStream = new MemoryStream(zipBytes);
                using var zip   = new ZipArchive(zipStream, ZipArchiveMode.Read);
                var       entry = zip.Entries.FirstOrDefault(e =>
                    e.Name.Equals("cities500.txt", StringComparison.OrdinalIgnoreCase));
                if (entry == null)
                {
                    LoadStatus = "Download riuscito ma cities500.txt non trovato nello zip GeoNames";
                    return null;
                }

                using (var es = entry.Open())
                using (var fs = File.Create(DownloadCachePath))
                    es.CopyTo(fs);

                return DownloadCachePath;
            }
            catch (Exception ex)
            {
                LoadStatus = $"Download automatico di cities500.csv fallito: {ex.Message}";
                return null;
            }
        }

        // Parsing del dump GeoNames grezzo (cities500.txt: TSV senza
        // intestazione, colonne fisse) scaricato da DownloadAndExtract —
        // formato ufficiale documentato su geonames.org/export/dump/,
        // diverso dal cities500.csv "semplificato" con intestazione atteso
        // da ParseLocalCsv. Colonne rilevanti: 1=name, 4=latitude,
        // 5=longitude, 14=population.
        private static List<CityEntry> ParseGeonamesRawDump(string filePath)
        {
            var result = new List<CityEntry>(500_000);
            try
            {
                using var reader = new StreamReader(filePath);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var f = line.Split('\t');
                    if (f.Length < 15) continue;

                    if (!double.TryParse(f[4], NumberStyles.Float, CultureInfo.InvariantCulture, out double lat)) continue;
                    if (!double.TryParse(f[5], NumberStyles.Float, CultureInfo.InvariantCulture, out double lon)) continue;
                    long.TryParse(f[14], out long pop);

                    result.Add(new CityEntry(f[1], lat, lon, pop));
                }
            }
            catch (Exception ex)
            {
                LoadStatus = $"Errore lettura {Path.GetFileName(filePath)}: {ex.Message}";
            }

            return result;
        }

        // Parsing del cities500.csv "semplificato" (con intestazione,
        // separatore virgola) — formato storico atteso quando il file è
        // fornito manualmente dall'utente invece che scaricato in automatico.
        private static List<CityEntry> ParseLocalCsv(string filePath)
        {
            var result = new List<CityEntry>(500_000);
            int errors = 0;

            try
            {
                using var reader = new StreamReader(filePath);

                // Prima riga: intestazione — individua gli indici delle colonne
                string? header = reader.ReadLine();
                if (header == null)
                {
                    LoadStatus = "File vuoto";
                    return result;
                }

                var cols     = SplitCsv(header);
                int iName    = IndexOf(cols, "name");
                int iLat     = IndexOf(cols, "latitude");
                int iLon     = IndexOf(cols, "longitude");
                int iPop     = IndexOf(cols, "population");

                if (iName < 0 || iLat < 0 || iLon < 0 || iPop < 0)
                {
                    LoadStatus = "Colonne obbligatorie non trovate nell'intestazione";
                    return result;
                }

                // Leggi tutte le righe dati
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var f = SplitCsv(line);

                    // Riga potenzialmente incompleta: skip silenzioso
                    int needed = Math.Max(Math.Max(iName, iLat), Math.Max(iLon, iPop));
                    if (f.Count <= needed) { errors++; continue; }

                    if (!double.TryParse(f[iLat], NumberStyles.Float, CultureInfo.InvariantCulture, out double lat)) continue;
                    if (!double.TryParse(f[iLon], NumberStyles.Float, CultureInfo.InvariantCulture, out double lon)) continue;

                    long.TryParse(f[iPop], out long pop);

                    result.Add(new CityEntry(f[iName], lat, lon, pop));
                }
            }
            catch (Exception ex)
            {
                LoadStatus = $"Errore lettura: {ex.Message}";
            }

            return result;
        }

        // Trova l'indice di una colonna nel header (case-insensitive)
        private static int IndexOf(List<string> cols, string name)
        {
            for (int i = 0; i < cols.Count; i++)
                if (cols[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }

        // Split CSV che gestisce campi tra virgolette (es. "48.07556")
        private static List<string> SplitCsv(string line)
        {
            var fields = new List<string>();
            bool inQuote = false;
            var  current = new System.Text.StringBuilder();

            foreach (char c in line)
            {
                if (c == '"')
                {
                    inQuote = !inQuote;
                }
                else if (c == ',' && !inQuote)
                {
                    fields.Add(current.ToString().Trim());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }
            fields.Add(current.ToString().Trim());
            return fields;
        }
    }
}
