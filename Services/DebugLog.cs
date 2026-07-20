// =============================================================================
// Services/DebugLog.cs
//
// SINOSSI: Log su file di testo per il debug delle chiamate di rete più
//   difficili da diagnosticare da remoto (in particolare Groq, vedi
//   GroqClient) — Debug.WriteLine finisce solo nella console del debugger e
//   sparisce del tutto in una build Release, quindi non è utilizzabile per
//   farsi incollare l'accaduto da chi esegue l'app fuori da un IDE. Stesso
//   pattern di cartella di AppPreferencesService/RecentFilesService
//   (Environment.SpecialFolder.ApplicationData/StradarioApp). Append-only,
//   nessuna rotazione: è un log diagnostico a bassa frequenza (ricerche POI
//   avviate dall'utente), non un log applicativo continuo.
// =============================================================================

using System;
using System.IO;

namespace StradarioApp.Services
{
    public static class DebugLog
    {
        private static readonly object Lock = new();

        public static string LogFilePath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "StradarioApp", "debug.log");

        // Best-effort: un errore nella scrittura del log non deve mai
        // interrompere la ricerca che lo sta generando.
        public static void Write(string line)
        {
            try
            {
                lock (Lock)
                {
                    string? dir = Path.GetDirectoryName(LogFilePath);
                    if (dir != null) Directory.CreateDirectory(dir);
                    File.AppendAllText(LogFilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}");
                }
            }
            catch { /* logging best-effort */ }
        }
    }
}
