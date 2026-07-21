// =============================================================================
// Services/AppPreferencesService.cs
//
// SINOSSI: Preferenze utente globali (non legate al singolo progetto),
//   persistite in un piccolo file JSON nella cartella dati utente — stesso
//   pattern di RecentFilesService. A differenza di StradarioSettings (che
//   viaggia dentro il file .stradario e quindi riparte dai default per ogni
//   nuovo progetto: formato pagina, scala, DPI... è corretto che siano per
//   progetto), la chiave API Groq e la chiave API del tile server sono
//   credenziali dell'utente/account, non parametri del documento: vanno
//   riproposte automaticamente su ogni progetto (nuovo o aperto), non
//   richieste di nuovo ogni volta. Vedi MainWindow.ApplyGlobalPreferences.
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace StradarioApp.Services
{
    public class AppPreferencesService
    {
        // Una categoria POI personalizzata aggiunta dall'utente dalle
        // Impostazioni (tab "Categorie POI") — vedi PoiSearchService.
        // SetCustomCategories/CustomCategories.
        public class CustomPoiCategoryPref
        {
            public string Key   = "";
            public string Value = "";
            public string Label = "";
        }

        private class Data
        {
            public string GroqApiKey         = "";
            public string TileServerApiKey   = "";
            // Ultima categoria usata nella ricerca POI per categoria (combo
            // toolbar): riproposta come default ad ogni riapertura dell'app,
            // così l'utente non deve riselezionarla ogni volta — vedi
            // MainWindow (combo sempre valorizzato, "ristoranti" è il
            // default solo quando non è ancora stata usata nessuna categoria).
            public string LastPoiCategoryKey   = "";
            public string LastPoiCategoryValue = "";
            // Categorie POI personalizzate (oltre a quelle predefinite in
            // PoiSearchService.Categories), gestite dalla tab "Categorie POI"
            // delle Impostazioni.
            public List<CustomPoiCategoryPref> CustomPoiCategories = new();
        }

        private static string StorageFilePath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "StradarioApp", "preferences.json");

        private Data LoadData()
        {
            try
            {
                if (!File.Exists(StorageFilePath)) return new Data();
                return JsonConvert.DeserializeObject<Data>(File.ReadAllText(StorageFilePath)) ?? new Data();
            }
            catch
            {
                // File corrotto/illeggibile: tratta come "nessuna preferenza salvata"
                return new Data();
            }
        }

        private void SaveData(Data data)
        {
            try
            {
                string? dir = Path.GetDirectoryName(StorageFilePath);
                if (dir != null) Directory.CreateDirectory(dir);
                File.WriteAllText(StorageFilePath, JsonConvert.SerializeObject(data));
            }
            catch
            {
                // Persistenza best-effort: un errore qui non deve bloccare il chiamante
            }
        }

        public (string GroqApiKey, string TileServerApiKey) Load()
        {
            var data = LoadData();
            return (data.GroqApiKey ?? "", data.TileServerApiKey ?? "");
        }

        // Legge/scrive solo i campi di propria competenza: entrambi passano
        // da LoadData/SaveData (leggono l'intero file e lo riscrivono per
        // intero), così Save() e SaveLastPoiCategory() non si cancellano a
        // vicenda anche se chiamati in momenti diversi.
        public void Save(string groqApiKey, string tileServerApiKey)
        {
            var data = LoadData();
            data.GroqApiKey       = groqApiKey ?? "";
            data.TileServerApiKey = tileServerApiKey ?? "";
            SaveData(data);
        }

        public (string Key, string Value)? LoadLastPoiCategory()
        {
            var data = LoadData();
            if (string.IsNullOrWhiteSpace(data.LastPoiCategoryKey) || string.IsNullOrWhiteSpace(data.LastPoiCategoryValue))
                return null;
            return (data.LastPoiCategoryKey, data.LastPoiCategoryValue);
        }

        public void SaveLastPoiCategory(string key, string value)
        {
            var data = LoadData();
            data.LastPoiCategoryKey   = key ?? "";
            data.LastPoiCategoryValue = value ?? "";
            SaveData(data);
        }

        public List<(string Key, string Value, string Label)> LoadCustomPoiCategories()
        {
            var data = LoadData();
            return (data.CustomPoiCategories ?? new())
                .Select(c => (c.Key ?? "", c.Value ?? "", c.Label ?? ""))
                .ToList();
        }

        public void SaveCustomPoiCategories(IEnumerable<(string Key, string Value, string Label)> categories)
        {
            var data = LoadData();
            data.CustomPoiCategories = categories
                .Select(c => new CustomPoiCategoryPref { Key = c.Key, Value = c.Value, Label = c.Label })
                .ToList();
            SaveData(data);
        }
    }
}
