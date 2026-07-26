// =============================================================================
// Services/PoiOfflineDatabase.cs
//
// SINOSSI: Database POI offline generato dal tool separato osm/OsmExtractor
//   (estratti Geofabrik .osm.pbf -> CSV per continente+categoria, vedi
//   osm/CLAUDE.md) e pubblicato come zip per continente in una release
//   GitHub dedicata (tag "osm-data-<data>", separato dai tag versione v1.0.x
//   dell'app — vedi UpdateChecker). Ogni zip contiene SEMPRE tutte le 43
//   categorie di quel continente insieme: non esiste un download parziale
//   per singola categoria, solo "scarica/aggiorna questo continente".
//
//   Ricerca: PoiSearchService (Overpass, dal vivo) resta il fallback, ma se
//   l'utente ha scaricato almeno un continente da Impostazioni, SearchCategory
//   qui sotto trova gli stessi risultati istantaneamente e offline, senza
//   bisogno di restringere l'area (nessun server pubblico da proteggere,
//   a differenza di Overpass — vedi MainWindow.RunCategorySearchAsync).
//
//   Cartelle di ricerca: le stesse "famiglia" di CityDatabase (cities500.csv,
//   vedi Services/CityDatabase.cs) — cartella corrente, cartella eseguibile,
//   ~ — sotto "osm_data/csv/<continente>/", più la cache del download
//   automatico in AppData. A differenza di cities500.csv, qui NON c'è un
//   download automatico silenzioso al primo uso: l'azione è sempre esplicita
//   da Impostazioni (vedi UI/SettingsWindow), perché i pacchetti sono grossi
//   (fino a ~320 MB per l'Europa) e l'utente deve scegliere quale
//   continente scaricare.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using StradarioApp.Models;

namespace StradarioApp.Services
{
    // Una riga del CSV locale generato da OsmExtractor: id, lat, lon, name,
    // tags ("chiave=valore;chiave=valore;...", stessa serializzazione
    // scritta da osm/OsmExtractor/Program.cs).
    public record PoiOfflineEntry(long Id, double Lat, double Lon, string Name, string Tags)
    {
        public string? GetTag(string key)
        {
            foreach (var part in Tags.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                if (part.Substring(0, eq) == key) return part.Substring(eq + 1);
            }
            return null;
        }
    }

    public record PoiDataRelease(string Tag, Dictionary<string, string> AssetUrlsByContinent);

    public static class PoiOfflineDatabase
    {
        // Stessi 8 continenti di osm/download_continenti.bat/Geofabrik,
        // stessi nomi di cartella prodotti da OsmExtractor.GetContinentName
        // e stessi nomi di asset (<continente>.zip) nella release dati.
        public static readonly string[] Continents =
        {
            "africa", "antarctica", "asia", "australia-oceania",
            "central-america", "europe", "north-america", "south-america",
        };

        private const string GithubRepo      = "paolobia/StradarioApp";
        private const string ReleasesApiUrl  = "https://api.github.com/repos/" + GithubRepo + "/releases";
        // Prefisso dei tag "dati" (osm-data-<data>): distingue queste
        // release da quelle versione app (v1.0.x) sullo stesso repo, così
        // non si può confondere l'una con l'altra scorrendo l'elenco.
        private const string DataTagPrefix   = "osm-data-";

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };

        static PoiOfflineDatabase()
        {
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("StradarioApp/1.0 (educational use)");
        }

        // Stesse cartelle "base" di CityDatabase.SearchPaths (cities500.csv):
        // l'utente mette osm_data/csv accanto a cities500.csv, non in un
        // percorso configurabile a parte.
        private static readonly string[] BaseSearchDirs =
        {
            ".",
            AppDomain.CurrentDomain.BaseDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };

        private static readonly string DownloadCacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StradarioApp", "osm_data", "csv");

        private static string? FindContinentDir(string continent)
        {
            foreach (var baseDir in BaseSearchDirs)
            {
                var candidate = Path.Combine(baseDir, "osm_data", "csv", continent);
                if (Directory.Exists(candidate) && Directory.EnumerateFiles(candidate, "*.csv").Any())
                    return candidate;
            }

            var cached = Path.Combine(DownloadCacheDir, continent);
            if (Directory.Exists(cached) && Directory.EnumerateFiles(cached, "*.csv").Any())
                return cached;

            return null;
        }

        public static bool IsContinentDownloaded(string continent) => FindContinentDir(continent) != null;

        public static bool HasAnyLocalData() => Continents.Any(IsContinentDownloaded);

        // Tag della release con cui il continente è stato scaricato (scritto
        // da DownloadContinentAsync in _version.txt dentro la cartella del
        // continente); null se non scaricato, o se i CSV sono stati messi lì
        // a mano dall'utente senza passare dal download (nessun manifest).
        public static string? GetContinentVersion(string continent)
        {
            var dir = FindContinentDir(continent);
            if (dir == null) return null;
            var versionFile = Path.Combine(dir, "_version.txt");
            return File.Exists(versionFile) ? File.ReadAllText(versionFile).Trim() : null;
        }

        // ---- Ricerca locale ----

        // Cache in memoria per continente+categoria, popolata al primo uso
        // (lazy, come CityDatabase._cities, ma qui una entry per
        // combinazione invece di un unico database mondiale: caricare tutti
        // i continenti x tutte le categorie insieme sprecherebbe RAM per
        // dati mai richiesti).
        private static readonly Dictionary<string, List<PoiOfflineEntry>> _cache = new();
        private static readonly object _cacheLock = new();

        private static List<PoiOfflineEntry> LoadCategory(string continent, string dir, string key, string value)
        {
            string cacheKey = $"{continent}:{key}={value}";
            lock (_cacheLock)
            {
                if (_cache.TryGetValue(cacheKey, out var cached)) return cached;

                var path = Path.Combine(dir, $"{key}_{value}.csv");
                var list = File.Exists(path) ? ParseCsv(path) : new List<PoiOfflineEntry>();
                _cache[cacheKey] = list;
                return list;
            }
        }

        private static List<PoiOfflineEntry> ParseCsv(string path)
        {
            var result = new List<PoiOfflineEntry>();
            // StreamReader rileva/scarta da sé un eventuale BOM UTF-8
            // iniziale (default detectEncodingFromByteOrderMarks: true).
            using var reader = new StreamReader(path);

            // Niente riga di intestazione da saltare: osm/OsmExtractor/Program.cs
            // scrive i CSV riga per riga con WriteField/NextRecord manuali,
            // senza mai chiamare WriteHeader — la prima riga è già un dato
            // reale (verificato: saltarla perdeva sistematicamente la prima
            // POI di ogni categoria/continente).
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var f = SplitCsvLine(line);
                if (f.Count < 4) continue;
                if (!long.TryParse(f[0], out long id)) continue;
                if (!double.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double lat)) continue;
                if (!double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double lon)) continue;

                string name = f[3];
                string tags = f.Count > 4 ? f[4] : "";
                result.Add(new PoiOfflineEntry(id, lat, lon, name, tags));
            }

            return result;
        }

        // Split CSV minimale con supporto virgolette (stesso approccio di
        // CityDatabase.SplitCsv, duplicato qui invece che condiviso: sono
        // formati diversi e non vale un'astrazione comune per poche righe).
        private static List<string> SplitCsvLine(string line)
        {
            var fields = new List<string>();
            bool inQuote = false;
            var current = new StringBuilder();

            foreach (char c in line)
            {
                if (c == '"') inQuote = !inQuote;
                else if (c == ',' && !inQuote) { fields.Add(current.ToString()); current.Clear(); }
                else current.Append(c);
            }
            fields.Add(current.ToString());
            return fields;
        }

        // Cerca key=value in tutti i continenti scaricati localmente,
        // filtrando per bbox e per gli eventuali subFilters (stessa sintassi
        // "chiave=valore"/"chiave!=valore" di
        // PoiSearchService.SearchCategoryAsync/GetCategoryExcludeFilters,
        // che MainWindow.RunCategorySearchAsync passa identici a entrambe).
        // A differenza della ricerca Overpass, qui non c'è alcun tetto
        // d'area da rispettare: la vista viene sempre cercata per intero.
        public static List<PoiSearchService.Result> SearchCategory(
            string key, string value, GeoRect bounds, IEnumerable<string>? subFilters = null)
        {
            var filters = (subFilters ?? Enumerable.Empty<string>()).ToList();
            var result  = new List<PoiSearchService.Result>();

            foreach (var continent in Continents)
            {
                var dir = FindContinentDir(continent);
                if (dir == null) continue;

                var entries = LoadCategory(continent, dir, key, value);
                foreach (var e in entries)
                {
                    if (e.Lon < bounds.MinLon || e.Lon > bounds.MaxLon ||
                        e.Lat < bounds.MinLat || e.Lat > bounds.MaxLat)
                        continue;
                    if (!MatchesSubFilters(e, filters)) continue;

                    result.Add(new PoiSearchService.Result(
                        string.IsNullOrWhiteSpace(e.Name) ? $"{value} (senza nome)" : e.Name,
                        e.Lon, e.Lat, key, value, null, e.Tags));
                }
            }

            return result;
        }

        private static bool MatchesSubFilters(PoiOfflineEntry e, List<string> subFilters)
        {
            foreach (var sf in subFilters)
            {
                int negIdx = sf.IndexOf("!=", StringComparison.Ordinal);
                bool negate = negIdx > 0;
                int eq = negate ? negIdx : sf.IndexOf('=');
                int valueStart = negate ? negIdx + 2 : eq + 1;
                if (eq <= 0 || valueStart >= sf.Length) continue;

                string sk = sf.Substring(0, eq).Trim();
                string sv = sf.Substring(valueStart).Trim();
                string? actual = e.GetTag(sk);

                if (negate) { if (actual == sv) return false; }
                else        { if (actual != sv) return false; }
            }
            return true;
        }

        // ---- Download / aggiornamento ----

        private static PoiDataRelease? _latestReleaseCache;
        private static DateTime _latestReleaseCacheUtc = DateTime.MinValue;

        // Elenco release del repo (non /releases/latest, che tornerebbe
        // l'ultima release IN ASSOLUTO, comprese le v1.0.x dell'app — vedi
        // UpdateChecker): si cerca la più recente con tag "osm-data-*".
        // Cache in memoria di 5 minuti: la finestra Impostazioni può
        // controllare più continenti nella stessa apertura senza richiamare
        // l'API una volta a continente.
        public static async Task<PoiDataRelease?> GetLatestDataReleaseAsync(CancellationToken ct = default)
        {
            if (_latestReleaseCache != null && (DateTime.UtcNow - _latestReleaseCacheUtc) < TimeSpan.FromMinutes(5))
                return _latestReleaseCache;

            try
            {
                string json = await Http.GetStringAsync(ReleasesApiUrl, ct);
                var arr = JArray.Parse(json);
                var release = arr.FirstOrDefault(r =>
                    ((string?)r["tag_name"])?.StartsWith(DataTagPrefix, StringComparison.OrdinalIgnoreCase) == true);
                if (release == null) return null;

                string tag = (string)release["tag_name"]!;
                var assetUrls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var asset in (release["assets"] as JArray) ?? new JArray())
                {
                    string? name = (string?)asset["name"];
                    string? url  = (string?)asset["browser_download_url"];
                    if (name != null && url != null && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        assetUrls[Path.GetFileNameWithoutExtension(name)] = url;
                }

                _latestReleaseCache    = new PoiDataRelease(tag, assetUrls);
                _latestReleaseCacheUtc = DateTime.UtcNow;
                return _latestReleaseCache;
            }
            catch
            {
                // Offline, rate limit GitHub, risposta inattesa: nessun
                // controllo possibile ora, mai un'eccezione visibile (stesso
                // comportamento di UpdateChecker/CityDatabase).
                return null;
            }
        }

        // Scarica ed estrae lo zip del continente dalla release dati più
        // recente, sovrascrivendo l'eventuale versione già in cache locale.
        // onProgress riceve una stringa già pronta da mostrare (percentuale
        // di download), niente calcoli lato chiamante.
        public static async Task<(bool Success, string Message)> DownloadContinentAsync(
            string continent, IProgress<string>? onProgress = null, CancellationToken ct = default)
        {
            var release = await GetLatestDataReleaseAsync(ct);
            if (release == null)
                return (false, "Impossibile contattare GitHub per scaricare i dati (rete assente o servizio non raggiungibile).");

            if (!release.AssetUrlsByContinent.TryGetValue(continent, out var url))
                return (false, $"Nessun pacchetto dati trovato per '{continent}' nella release {release.Tag}.");

            string tempZip = Path.GetTempFileName();
            try
            {
                onProgress?.Report($"Scarico {continent}.zip...");
                using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    resp.EnsureSuccessStatusCode();
                    long? totalBytes = resp.Content.Headers.ContentLength;

                    using var httpStream = await resp.Content.ReadAsStreamAsync(ct);
                    using var fileStream = File.Create(tempZip);

                    var buffer = new byte[81920];
                    long readTotal = 0;
                    int lastPercent = -1;
                    int read;
                    while ((read = await httpStream.ReadAsync(buffer, ct)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                        readTotal += read;
                        if (totalBytes is > 0)
                        {
                            int percent = (int)(readTotal * 100 / totalBytes.Value);
                            if (percent != lastPercent)
                            {
                                lastPercent = percent;
                                onProgress?.Report($"Scarico {continent}.zip... {percent}% ({readTotal / (1024 * 1024)} MB)");
                            }
                        }
                    }
                }

                string destDir = Path.Combine(DownloadCacheDir, continent);
                onProgress?.Report($"Estraggo {continent}.zip...");
                if (Directory.Exists(destDir)) Directory.Delete(destDir, recursive: true);
                Directory.CreateDirectory(destDir);
                ZipFile.ExtractToDirectory(tempZip, destDir);

                File.WriteAllText(Path.Combine(destDir, "_version.txt"), release.Tag);

                // Invalida la cache in memoria di eventuali categorie già
                // caricate per questo continente da una versione precedente.
                lock (_cacheLock)
                {
                    foreach (var k in _cache.Keys.Where(k => k.StartsWith(continent + ":", StringComparison.Ordinal)).ToList())
                        _cache.Remove(k);
                }

                return (true, $"Dati '{continent}' scaricati e aggiornati alla versione {release.Tag}.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return (false, $"Download di '{continent}' fallito: {ex.Message}");
            }
            finally
            {
                if (File.Exists(tempZip)) File.Delete(tempZip);
            }
        }
    }
}
