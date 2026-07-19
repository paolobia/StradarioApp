// =============================================================================
// Services/ProjectService.cs
//
// SINOSSI: Gestione della persistenza del progetto stradario su file.
//   - Salva il progetto come file JSON con estensione .stradario
//   - Carica un progetto da file, con validazione minimale
//   - Assegna ID univoci alle nuove pagine
// =============================================================================

using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using StradarioApp.Models;

namespace StradarioApp.Services
{
    public class ProjectService
    {
        // Estensione del file progetto
        public const string FileExtension = ".stradario";
        public const string FileFilter    = "Stradario (*.stradario)|*.stradario|Tutti i file (*.*)|*.*";

        // Salva il progetto su file JSON
        public async Task SaveAsync(StradarioProject project, string filePath)
        {
            project.LastModified = DateTime.Now;

            string json = JsonConvert.SerializeObject(project, Formatting.Indented);
            await File.WriteAllTextAsync(filePath, json);
        }

        // Carica un progetto da file JSON
        public async Task<StradarioProject> LoadAsync(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"File non trovato: {filePath}");

            string json    = await File.ReadAllTextAsync(filePath);
            var    project = JsonConvert.DeserializeObject<StradarioProject>(json);

            if (project == null)
                throw new InvalidDataException("File progetto non valido.");

            return project;
        }

        // Ritorna un nuovo ID univoco per una pagina (max esistente + 1)
        public int GetNextPageId(StradarioProject project)
        {
            int max = 0;
            foreach (var p in project.Pages)
                if (p.Id > max) max = p.Id;
            return max + 1;
        }

        // Genera un'etichetta automatica per la pagina (A1, A2, B1, ...)
        // Basata sull'ordine spaziale: righe per latitudine, colonne per longitudine
        public string GenerateAutoLabel(StradarioProject project, MapPage newPage)
        {
            // Raccoglie tutte le latitudini e longitudini centrali esistenti
            // e assegna riga/colonna in base all'ordinamento
            var pages = project.Pages;

            // Trova la riga (ordinamento per lat decrescente)
            var latsSorted = new System.Collections.Generic.List<double>();
            foreach (var p in pages)
                if (!latsSorted.Contains(Math.Round(p.GeoBounds.CenterLat, 3)))
                    latsSorted.Add(Math.Round(p.GeoBounds.CenterLat, 3));
            latsSorted.Add(Math.Round(newPage.GeoBounds.CenterLat, 3));
            latsSorted.Sort((a, b) => b.CompareTo(a)); // decrescente

            var lonsSorted = new System.Collections.Generic.List<double>();
            foreach (var p in pages)
                if (!lonsSorted.Contains(Math.Round(p.GeoBounds.CenterLon, 3)))
                    lonsSorted.Add(Math.Round(p.GeoBounds.CenterLon, 3));
            lonsSorted.Add(Math.Round(newPage.GeoBounds.CenterLon, 3));
            lonsSorted.Sort();

            int row = latsSorted.IndexOf(Math.Round(newPage.GeoBounds.CenterLat, 3));
            int col = lonsSorted.IndexOf(Math.Round(newPage.GeoBounds.CenterLon, 3));

            // Riga come lettera (A, B, C...), colonna come numero
            char rowChar = (char)('A' + (row % 26));
            return $"{rowChar}{col + 1}";
        }
    }
}
