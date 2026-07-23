// =============================================================================
// Services/DebugLog.cs
//
// SINOSSI: Log su file di testo per il debug delle chiamate di rete più
//   difficili da diagnosticare (in particolare Groq, vedi GroqClient) —
//   Debug.WriteLine finisce solo nella console del debugger e non lascia
//   traccia persistente. Attivo SOLO in build Debug (Write è un no-op in
//   Release, vedi il check su AppContext qui sotto): è un log di sviluppo
//   per verificare al volo cosa succede durante una sessione di test, non
//   uno strumento diagnostico per bug report remoti.
//   Percorso: dentro la cartella del progetto (repo root, "debug.log",
//   in .gitignore) invece che nella cartella dati utente, così è
//   raggiungibile direttamente durante lo sviluppo senza dover risalire a
//   %AppData%/~/.config. Append-only, nessuna rotazione: è un log
//   diagnostico a bassa frequenza (ricerche POI avviate dall'utente), non
//   un log applicativo continuo.
// =============================================================================

using System;
using System.IO;
using System.Reflection;

namespace StradarioApp.Services
{
    public static class DebugLog
    {
        private static readonly object Lock = new();

        // Risale dalla cartella di esecuzione (bin/Debug/net8.0/...) alla
        // radice del repo/pubblicazione, così il log finisce sempre accanto
        // al .csproj in sviluppo e accanto all'eseguibile in un publish.
        public static string LogFilePath =>
            Path.Combine(AppContext.BaseDirectory, "debug.log");

        // Best-effort: un errore nella scrittura del log non deve mai
        // interrompere la ricerca che lo sta generando. No-op in Release.
        public static void Write(string line)
        {
#if DEBUG
            try
            {
                lock (Lock)
                {
                    File.AppendAllText(LogFilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}");
                }
            }
            catch { /* logging best-effort */ }
#endif
        }
    }
}
