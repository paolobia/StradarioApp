// =============================================================================
// Services/CityDatabase.cs
//
// SINOSSI: Carica e interroga il database cities500.csv (GeoNames).
//   - Caricamento lazy al primo utilizzo (singleton thread-safe)
//   - Parsing CSV con supporto valori quoted e separatore virgola
//   - FindTopCities(bounds, n): trova le n città più popolose nell'area
//   - Il file cities500.csv deve essere nella stessa cartella dell'eseguibile
//     oppure nella cartella corrente; se non trovato restituisce lista vuota
//     senza eccezioni (funzionalità degradata senza crash)
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        // Percorsi in cui cercare il file (in ordine di priorità)
        private static readonly string[] SearchPaths = new[]
        {
            "cities500.csv",
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cities500.csv"),
            Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile), "cities500.csv"),
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

        // Genera la stringa descrizione: "Roma, Milano, Napoli" (o meno se non trovate)
        public static string Describe(GeoRect bounds, int n = 3)
        {
            var cities = FindTopCities(bounds, n);
            if (cities.Count == 0) return "";
            return string.Join(", ", cities.Select(c => c.Name));
        }

        // Carica il CSV e restituisce la lista delle città
        private static List<CityEntry> Load()
        {
            string? filePath = null;
            foreach (var path in SearchPaths)
            {
                if (File.Exists(path)) { filePath = path; break; }
            }

            if (filePath == null)
            {
                LoadStatus = "File cities500.csv non trovato";
                return new List<CityEntry>();
            }

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

                    if (!double.TryParse(f[iLat], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double lat)) continue;
                    if (!double.TryParse(f[iLon], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double lon)) continue;

                    long.TryParse(f[iPop], out long pop);

                    result.Add(new CityEntry(f[iName], lat, lon, pop));
                }

                LoadStatus = $"Caricate {result.Count:N0} città da {Path.GetFileName(filePath)}";
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
