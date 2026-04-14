using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StradarioApp.Models;

namespace StradarioApp.Services
{
    public class CityEntry
    {
        public string Name       { get; set; } = string.Empty;
        public double Latitude   { get; set; }
        public double Longitude  { get; set; }
        public long   Population { get; set; }
    }

    public static class CityDatabase
    {
        private static List<CityEntry>? _cities;
        private static readonly object _lock = new();
        private static bool _loading;
        private static string _loadStatus = "Non caricato";

        public static string LoadStatus => _loadStatus;

        public static void EnsureLoaded()
        {
            lock (_lock)
            {
                if (_cities != null || _loading) return;
                _loading = true;
            }
            DoLoad();
        }

        private static void DoLoad()
        {
            var candidates = new[]
            {
                "cities500.csv",
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cities500.csv"),
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "cities500.csv"),
            };

            foreach (var path in candidates)
            {
                if (!File.Exists(path)) continue;
                try
                {
                    _loadStatus = "Caricamento…";
                    var entries = ParseCsv(path);
                    lock (_lock)
                    {
                        _cities  = entries;
                        _loading = false;
                    }
                    _loadStatus = $"Caricate {entries.Count} città";
                    return;
                }
                catch (Exception ex)
                {
                    _loadStatus = $"Errore: {ex.Message}";
                }
            }

            lock (_lock) { _loading = false; }
            if (_cities == null)
                _loadStatus = "cities500.csv non trovato";
        }

        private static List<CityEntry> ParseCsv(string path)
        {
            var result = new List<CityEntry>();
            using var reader = new StreamReader(path);

            string? header = reader.ReadLine();
            if (header == null) return result;

            // Detect column indices from header
            var cols = ParseCsvLine(header);
            int nameIdx = FindIndex(cols, "name");
            int latIdx  = FindIndex(cols, "latitude");
            int lonIdx  = FindIndex(cols, "longitude");
            int popIdx  = FindIndex(cols, "population");

            if (nameIdx < 0 || latIdx < 0 || lonIdx < 0) return result;

            while (!reader.EndOfStream)
            {
                var line = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(line)) continue;

                var fields = ParseCsvLine(line);
                if (fields.Count <= Math.Max(Math.Max(nameIdx, latIdx), Math.Max(lonIdx, popIdx < 0 ? 0 : popIdx)))
                    continue;

                if (!double.TryParse(Unquote(fields[latIdx]).Replace(',', '.'),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double lat))
                    continue;

                if (!double.TryParse(Unquote(fields[lonIdx]).Replace(',', '.'),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double lon))
                    continue;

                long pop = 0;
                if (popIdx >= 0 && popIdx < fields.Count)
                    long.TryParse(Unquote(fields[popIdx]).Replace(",", ""), out pop);

                result.Add(new CityEntry
                {
                    Name       = Unquote(fields[nameIdx]),
                    Latitude   = lat,
                    Longitude  = lon,
                    Population = pop
                });
            }

            return result;
        }

        private static List<string> ParseCsvLine(string line)
        {
            var fields = new List<string>();
            int i = 0;
            while (i < line.Length)
            {
                if (line[i] == '"')
                {
                    i++;
                    int start = i;
                    while (i < line.Length && line[i] != '"') i++;
                    fields.Add(line.Substring(start, i - start));
                    if (i < line.Length) i++; // skip closing quote
                    if (i < line.Length && line[i] == ',') i++; // skip comma
                }
                else
                {
                    int start = i;
                    while (i < line.Length && line[i] != ',') i++;
                    fields.Add(line.Substring(start, i - start).Trim());
                    if (i < line.Length) i++; // skip comma
                }
            }
            return fields;
        }

        private static string Unquote(string s) =>
            s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1] : s;

        private static int FindIndex(List<string> headers, string name)
        {
            for (int i = 0; i < headers.Count; i++)
                if (string.Equals(Unquote(headers[i]).Trim(), name, StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }

        public static List<CityEntry> FindTopCities(GeoRect bounds, int n = 3)
        {
            List<CityEntry>? snapshot;
            lock (_lock) { snapshot = _cities; }
            if (snapshot == null) return new List<CityEntry>();

            return snapshot
                .Where(c => c.Longitude >= bounds.MinLon && c.Longitude <= bounds.MaxLon
                         && c.Latitude  >= bounds.MinLat && c.Latitude  <= bounds.MaxLat)
                .OrderByDescending(c => c.Population)
                .Take(n)
                .ToList();
        }

        public static string Describe(GeoRect bounds, int n = 3)
        {
            var cities = FindTopCities(bounds, n);
            if (cities.Count == 0) return string.Empty;
            return string.Join(", ", cities.Select(c => c.Name));
        }
    }
}
