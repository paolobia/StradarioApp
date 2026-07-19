// =============================================================================
// Services/RecentFilesService.cs
//
// SINOSSI: Elenco degli ultimi progetti .stradario aperti/salvati, persistito
//   in un piccolo file JSON nella cartella dati utente (così sopravvive tra
//   sessioni). Percorsi non più esistenti sul disco vengono scartati alla
//   lettura, non memorizzati esplicitamente come "mancanti".
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace StradarioApp.Services
{
    public class RecentFilesService
    {
        private const int MaxEntries = 8;

        private static string StorageFilePath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "StradarioApp", "recent_files.json");

        // Ritorna gli ultimi progetti aperti/salvati (più recente per primo),
        // filtrando quelli non più presenti sul disco
        public List<string> GetRecent()
        {
            try
            {
                if (!File.Exists(StorageFilePath)) return new List<string>();
                string json = File.ReadAllText(StorageFilePath);
                var list = JsonConvert.DeserializeObject<List<string>>(json) ?? new List<string>();
                return list.Where(File.Exists).Take(MaxEntries).ToList();
            }
            catch
            {
                // File corrotto/illeggibile: tratta come lista vuota, non bloccare l'avvio
                return new List<string>();
            }
        }

        // Aggiunge (o riporta in cima se già presente) un percorso alla lista
        public void Add(string path)
        {
            try
            {
                var list = GetRecent();
                list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                list.Insert(0, path);
                if (list.Count > MaxEntries) list = list.Take(MaxEntries).ToList();

                string? dir = Path.GetDirectoryName(StorageFilePath);
                if (dir != null) Directory.CreateDirectory(dir);
                File.WriteAllText(StorageFilePath, JsonConvert.SerializeObject(list));
            }
            catch
            {
                // Persistenza best-effort: un errore qui non deve interrompere il salvataggio del progetto
            }
        }
    }
}
