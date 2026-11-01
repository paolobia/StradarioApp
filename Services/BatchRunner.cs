// =============================================================================
// BatchRunner.cs
//
// SINOSSI: Modalita' batch da riga di comando (nessuna finestra): carica un
//   .stradario e genera PDF e/o file di scambio (KML/KMZ/GPX combinati, CSV).
//
//   StradarioApp --pdf out.pdf --kml out.kmz --csv out.csv progetto.stradario
//
//   Usa le impostazioni salvate nel progetto (nessun override da CLI) e le
//   chiavi API globali di AppPreferencesService, come ApplyGlobalPreferences.
//   Esporta SEMPRE tutto il progetto: i toggle di visibilita' dell'albero
//   sono stato di sessione della UI. Coordinate sempre WGS84 (nessuna
//   correzione GCJ-02, come per KML/KMZ/CSV nell'app).
//
//   Codici di uscita: 0 = ok, 1 = errore in esecuzione, 2 = uso errato.
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using StradarioApp.Models;

namespace StradarioApp.Services
{
    public static class BatchRunner
    {
        private static readonly string[] BatchOptions = { "--pdf", "--kml", "--csv", "--help", "-h", "-?" };

        // True se la riga di comando chiede la modalita' batch (altrimenti si
        // avvia la UI come sempre).
        public static bool IsBatch(string[] args) =>
            args.Any(a => BatchOptions.Contains(a, StringComparer.OrdinalIgnoreCase));

        public static int Run(string[] args)
        {
            AttachParentConsole();
            try { Console.OutputEncoding = Encoding.UTF8; } catch { /* console non regolabile */ }

            try { return RunAsync(args).GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Errore: {ex.Message}");
                return 1;
            }
        }

        private static async Task<int> RunAsync(string[] args)
        {
            string? projectPath = null, pdfPath = null;
            var kmlPaths = new List<string>();
            string? csvPath = null;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                switch (a.ToLowerInvariant())
                {
                    case "--help": case "-h": case "-?":
                        PrintUsage(Console.Out);
                        return 0;
                    case "--pdf": if (!TakeValue(args, ref i, out pdfPath)) return Usage("--pdf richiede un percorso"); break;
                    case "--csv": if (!TakeValue(args, ref i, out csvPath)) return Usage("--csv richiede un percorso"); break;
                    case "--kml":
                        if (!TakeValue(args, ref i, out var k)) return Usage("--kml richiede un percorso");
                        kmlPaths.Add(k!);
                        break;
                    default:
                        if (a.StartsWith("-")) return Usage($"opzione sconosciuta: {a}");
                        if (projectPath != null) return Usage("più di un file di progetto indicato");
                        projectPath = a;
                        break;
                }
            }

            if (projectPath == null) return Usage("manca il file di progetto .stradario");
            if (pdfPath == null && kmlPaths.Count == 0 && csvPath == null)
                return Usage("indica almeno una tra --pdf, --kml, --csv");

            foreach (var k in kmlPaths)
            {
                string e = Path.GetExtension(k).ToLowerInvariant();
                if (e != ".kml" && e != ".kmz" && e != ".gpx")
                    return Usage($"--kml accetta solo .kml, .kmz o .gpx: {k}");
            }

            var project = await new ProjectService().LoadAsync(projectPath);
            ApplyGlobalPreferences(project);
            Console.WriteLine($"Progetto: {projectPath} ({project.Pages.Count} pagine, " +
                              $"{project.PoiGroups.Count} gruppi POI, {project.Percorsi.Count} percorsi)");

            // Mai correzione GCJ-02/WGS84 in batch: nessuno puo' rispondere
            // alla domanda, e il default dell'app per GPX e' "gia' WGS84".
            GcjTransform.CorrectionEnabled = false;
            try
            {
                if (kmlPaths.Count > 0)
                {
                    var combined = new CombinedExportService();
                    foreach (var path in kmlPaths)
                    {
                        EnsureDirectory(path);
                        switch (Path.GetExtension(path).ToLowerInvariant())
                        {
                            case ".kml": await combined.ExportKmlAsync(path, false, project.PoiGroups, project.Percorsi); break;
                            case ".gpx": await combined.ExportGpxAsync(path, project.PoiGroups, project.Percorsi); break;
                            default:     await combined.ExportKmzAsync(path, project.PoiGroups, project.Percorsi); break;
                        }
                        Console.WriteLine($"Scritto: {path}");
                    }
                }

                if (csvPath != null)
                {
                    EnsureDirectory(csvPath);
                    // Due CSV tabellari separati (POI e percorsi hanno colonne
                    // diverse): "x.csv" -> "x_poi.csv" e "x_percorsi.csv".
                    string dir  = Path.GetDirectoryName(Path.GetFullPath(csvPath))!;
                    string stem = Path.GetFileNameWithoutExtension(csvPath);
                    if (project.PoiGroups.Any(g => g.Items.Count > 0))
                    {
                        string p = Path.Combine(dir, stem + "_poi.csv");
                        await new PoiService().ExportCsvAsync(project.PoiGroups, p);
                        Console.WriteLine($"Scritto: {p}");
                    }
                    if (project.Percorsi.Count > 0)
                    {
                        string p = Path.Combine(dir, stem + "_percorsi.csv");
                        await new PercorsoService().ExportCsvAsync(project.Percorsi, p);
                        Console.WriteLine($"Scritto: {p}");
                    }
                }
            }
            finally { GcjTransform.CorrectionEnabled = true; }

            if (pdfPath != null)
            {
                bool hasAnyPoi = project.PoiGroups.Any(g => g.Items.Count > 0);
                if (project.Pages.Count == 0 && !hasAnyPoi && project.Percorsi.Count == 0)
                    throw new InvalidOperationException("il progetto non ha pagine, POI né percorsi: nulla da stampare");

                EnsureDirectory(pdfPath);
                int lastPct = -1;
                await new PdfGenerator().GenerateAsync(project, pdfPath,
                    (current, total, msg) =>
                    {
                        int pct = total > 0 ? current * 100 / total : 0;
                        if (pct / 10 != lastPct / 10) { lastPct = pct; Console.WriteLine($"PDF {pct}% - {msg}"); }
                    },
                    Path.GetFileNameWithoutExtension(projectPath));
                Console.WriteLine($"Scritto: {pdfPath}");
            }

            return 0;
        }

        // Come MainWindow.ApplyGlobalPreferences: le chiavi API non stanno nel
        // .stradario, si prendono dalle preferenze globali dell'utente.
        private static void ApplyGlobalPreferences(StradarioProject project)
        {
            var (groqKey, tileKey) = new AppPreferencesService().Load();
            if (string.IsNullOrWhiteSpace(project.Settings.GroqApiKey))
                project.Settings.GroqApiKey = groqKey;
            if (string.IsNullOrWhiteSpace(project.Settings.TileServerApiKey))
                project.Settings.TileServerApiKey = tileKey;
        }

        private static bool TakeValue(string[] args, ref int i, out string? value)
        {
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) { value = args[++i]; return true; }
            value = null;
            return false;
        }

        private static void EnsureDirectory(string filePath)
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }

        private static int Usage(string error)
        {
            Console.Error.WriteLine($"Errore: {error}");
            Console.Error.WriteLine();
            PrintUsage(Console.Error);
            return 2;
        }

        private static void PrintUsage(TextWriter w)
        {
            w.WriteLine("Uso: StradarioApp [--pdf <file.pdf>] [--kml <file.kml|.kmz|.gpx>]... [--csv <file.csv>] <progetto.stradario>");
            w.WriteLine();
            w.WriteLine("  --pdf   genera l'atlante PDF");
            w.WriteLine("  --kml   esporta POI e percorsi insieme; formato dall'estensione (.kmz, .kml, .gpx); ripetibile");
            w.WriteLine("  --csv   esporta <nome>_poi.csv e <nome>_percorsi.csv");
            w.WriteLine();
            w.WriteLine("Usa le impostazioni salvate nel progetto ed esporta sempre tutto il contenuto.");
            w.WriteLine("Senza queste opzioni l'app si avvia normalmente con la finestra.");
            w.WriteLine("Uscita: 0 ok, 1 errore, 2 uso errato.");
        }

        // StradarioApp e' un eseguibile WinExe (senza console): da cmd/PowerShell
        // l'output andrebbe perso. Ci si aggancia alla console del processo
        // padre, se esiste. No-op su Linux/macOS.
        [DllImport("kernel32.dll")] private static extern bool AttachConsole(int dwProcessId);
        private static void AttachParentConsole()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
            try { AttachConsole(-1); } catch { /* nessuna console da agganciare */ }
        }
    }
}
