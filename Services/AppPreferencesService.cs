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
using System.IO;
using Newtonsoft.Json;

namespace StradarioApp.Services
{
    public class AppPreferencesService
    {
        private class Data
        {
            public string GroqApiKey       = "";
            public string TileServerApiKey = "";
        }

        private static string StorageFilePath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "StradarioApp", "preferences.json");

        public (string GroqApiKey, string TileServerApiKey) Load()
        {
            try
            {
                if (!File.Exists(StorageFilePath)) return ("", "");
                var data = JsonConvert.DeserializeObject<Data>(File.ReadAllText(StorageFilePath)) ?? new Data();
                return (data.GroqApiKey ?? "", data.TileServerApiKey ?? "");
            }
            catch
            {
                // File corrotto/illeggibile: tratta come "nessuna preferenza salvata"
                return ("", "");
            }
        }

        public void Save(string groqApiKey, string tileServerApiKey)
        {
            try
            {
                string? dir = Path.GetDirectoryName(StorageFilePath);
                if (dir != null) Directory.CreateDirectory(dir);
                var data = new Data { GroqApiKey = groqApiKey ?? "", TileServerApiKey = tileServerApiKey ?? "" };
                File.WriteAllText(StorageFilePath, JsonConvert.SerializeObject(data));
            }
            catch
            {
                // Persistenza best-effort: un errore qui non deve bloccare il salvataggio delle impostazioni
            }
        }
    }
}
