// =============================================================================
// Services/GeolocationService.cs
//
// SINOSSI: Localizzazione "dove sono" tramite il servizio di posizione del
//   sistema operativo (non IP-based: nessuna chiamata a servizi esterni).
//   Non esiste un'API .NET cross-platform per la posizione su desktop, quindi
//   ogni piattaforma delega a un processo esterno che parla con il servizio
//   nativo, e legge righe di testo dal suo stdout finché non viene fermato
//   (Stop) o l'utente chiude la finestra:
//     - Linux: GeoClue2 via D-Bus. Un piccolo script Python (scritto su un
//       file temporaneo ed eseguito con python3+PyGObject, entrambi tipicamente
//       già presenti su un desktop Linux moderno) mantiene UNA connessione
//       D-Bus aperta per l'intera sessione — necessario perché GeoClue2 lega
//       il Client al mittente D-Bus che lo ha creato (Manager.GetClient) e lo
//       rimuove alla disconnessione: non è quindi possibile spezzare i passi
//       "crea client / avvia / ascolta aggiornamenti" su invocazioni gdbus
//       separate (ognuna apre e chiude una connessione propria).
//     - Windows: Windows Location API (WinRT Geolocator) via un piccolo
//       script PowerShell, eseguito con Windows PowerShell 5.1 (sempre
//       presente), che sa caricare tipi WinRT con la sintassi
//       "[Tipo,Assembly,ContentType=WindowsRuntime]" senza bisogno di
//       compilare proiezioni CsWinRT nel progetto (che richiederebbero un
//       secondo TargetFramework "net8.0-windows10.0...", cambiando i comandi
//       "dotnet run/build" documentati). NOTA: non verificabile in questo
//       ambiente di sviluppo (Linux, nessuna macchina Windows disponibile) —
//       verificare su una macchina Windows reale prima di considerarlo
//       definitivo.
//   In entrambi i casi, un fix arrivato genera PositionUpdated; qualunque
//   impossibilità (servizio assente, permesso negato, nessun fix ricevuto)
//   genera ErrorOccurred con un messaggio in italiano pronto per la status bar.
// =============================================================================

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace StradarioApp.Services
{
    public sealed class GeoFix
    {
        public double  Lat             { get; }
        public double  Lon             { get; }
        public double? AccuracyMeters  { get; }

        public GeoFix(double lat, double lon, double? accuracyMeters)
        {
            Lat            = lat;
            Lon            = lon;
            AccuracyMeters = accuracyMeters;
        }
    }

    public sealed class GeolocationService : IDisposable
    {
        // Dopo quanti secondi senza alcun fix segnalare un errore in più
        // (il processo resta comunque attivo: un fix tardivo verrà comunque mostrato)
        private const int NoFixWarningSeconds = 20;

        // Dopo quanti secondi senza NEMMENO la riga "STARTED" (emessa dagli
        // script come primissima cosa, prima di qualunque chiamata al sistema)
        // segnalare che il processo stesso non è partito correttamente
        private const int StartupWarningSeconds = 6;

        // Il processo esterno (script Python/PowerShell) è partito ed è vivo:
        // non significa ancora "posizione trovata", solo "il servizio di
        // sistema è stato interpellato" — un passo intermedio utile da
        // mostrare nella status bar mentre si aspetta l'esito vero e proprio
        public event Action? Started;
        public event Action<GeoFix>? PositionUpdated;
        public event Action<string>? ErrorOccurred;

        private Process?            _process;
        private CancellationTokenSource? _cts;
        private string?             _tempScriptPath;

        public bool IsRunning => _process != null && !_process.HasExited;

        public void Start()
        {
            Stop();

            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            if (OperatingSystem.IsWindows())
                StartProcess("powershell.exe",
                    "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{0}\"",
                    WindowsLocationScript, ".ps1", token);
            else if (OperatingSystem.IsLinux())
                StartProcess("python3", "\"{0}\"", GeoCluePythonScript, ".py", token);
            else
                ErrorOccurred?.Invoke("Localizzazione non supportata su questo sistema operativo.");
        }

        public void Stop()
        {
            _cts?.Cancel();
            _cts = null;

            try
            {
                if (_process != null && !_process.HasExited)
                    _process.Kill(entireProcessTree: true);
            }
            catch { /* processo già terminato */ }

            _process?.Dispose();
            _process = null;

            if (_tempScriptPath != null)
            {
                try { File.Delete(_tempScriptPath); } catch { /* best-effort */ }
                _tempScriptPath = null;
            }
        }

        private void StartProcess(string exeName, string argsFormat, string scriptContent,
            string scriptExtension, CancellationToken token)
        {
            string scriptPath = Path.Combine(Path.GetTempPath(), $"stradario_geoloc_{Guid.NewGuid():N}{scriptExtension}");
            try
            {
                File.WriteAllText(scriptPath, scriptContent);
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"Impossibile preparare lo script di localizzazione: {ex.Message}");
                return;
            }
            _tempScriptPath = scriptPath;

            var psi = new ProcessStartInfo(exeName, string.Format(argsFormat, scriptPath))
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };

            try
            {
                _process = Process.Start(psi);
            }
            catch (Exception ex)
            {
                TryDeleteScript();
                ErrorOccurred?.Invoke(
                    $"Impossibile avviare il servizio di localizzazione ({exeName} non trovato o non eseguibile): {ex.Message}");
                return;
            }

            if (_process == null)
            {
                TryDeleteScript();
                ErrorOccurred?.Invoke("Impossibile avviare il servizio di localizzazione.");
                return;
            }

            _ = ReadOutputAsync(_process, token);
        }

        private void TryDeleteScript()
        {
            if (_tempScriptPath == null) return;
            try { File.Delete(_tempScriptPath); } catch { /* best-effort */ }
            _tempScriptPath = null;
        }

        private async Task ReadOutputAsync(Process process, CancellationToken token)
        {
            bool    gotStarted = false;
            bool    gotAnyFix  = false;
            string? lastError  = null;

            // Due avvisi separati, per distinguere "il processo non è nemmeno
            // partito" (python3/powershell mancanti, script rotto: sintomo di
            // un problema ambientale) da "è partito ma il sistema non ha ancora
            // dato un fix" (nessun GPS/permesso negato ma senza un errore
            // esplicito, es. una chiamata WinRT rimasta bloccata in attesa)
            var startTimer = new Timer(_ =>
            {
                if (!gotStarted && !gotAnyFix && !token.IsCancellationRequested)
                    ErrorOccurred?.Invoke(
                        "Localizzazione non disponibile: il processo del servizio di posizione non ha risposto (verifica che python3 con PyGObject/GeoClue2, su Linux, o PowerShell, su Windows, siano disponibili).");
            }, null, TimeSpan.FromSeconds(StartupWarningSeconds), Timeout.InfiniteTimeSpan);

            var noFixTimer = new Timer(_ =>
            {
                if (!gotAnyFix && !token.IsCancellationRequested)
                    ErrorOccurred?.Invoke("Posizione non disponibile: nessun fix ricevuto dal servizio di localizzazione del sistema.");
            }, null, TimeSpan.FromSeconds(NoFixWarningSeconds), Timeout.InfiniteTimeSpan);

            void MarkStarted()
            {
                if (gotStarted) return;
                gotStarted = true;
                Started?.Invoke();
            }

            try
            {
                while (!token.IsCancellationRequested)
                {
                    string? line = await process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false);
                    if (line == null) break;

                    if (line == "STARTED")
                    {
                        MarkStarted();
                    }
                    else if (line.StartsWith("FIX:", StringComparison.Ordinal))
                    {
                        MarkStarted();
                        var parts = line.Substring(4).Split(':');
                        if (parts.Length >= 2 &&
                            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double lat) &&
                            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double lon))
                        {
                            double? acc = parts.Length >= 3 &&
                                double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double a)
                                ? a : (double?)null;
                            gotAnyFix = true;
                            PositionUpdated?.Invoke(new GeoFix(lat, lon, acc));
                        }
                    }
                    else if (line.StartsWith("ERROR:", StringComparison.Ordinal))
                    {
                        MarkStarted();
                        lastError = line.Substring(6).Trim();
                    }
                }
            }
            catch (OperationCanceledException) { /* Stop() chiamato */ }
            catch { /* processo terminato/pipe chiusa */ }
            finally
            {
                startTimer.Dispose();
                noFixTimer.Dispose();
            }

            if (!gotAnyFix && !token.IsCancellationRequested)
            {
                string msg = lastError != null
                    ? $"Localizzazione non disponibile: {lastError}"
                    : "Localizzazione non disponibile: il servizio di posizione del sistema non ha risposto.";
                ErrorOccurred?.Invoke(msg);
            }
        }

        public void Dispose() => Stop();

        // -------------------------------------------------------------------
        // Linux: GeoClue2 via PyGObject. Ferma output "FIX:lat:lon:accuracy"
        // a ogni aggiornamento di posizione, "ERROR:messaggio" in caso di
        // fallimento (client negato, servizio assente, ecc.).
        // -------------------------------------------------------------------
        private const string GeoCluePythonScript = @"
import sys

def emit(line):
    sys.stdout.write(line + ""\n"")
    sys.stdout.flush()

# Primissima riga emessa, PRIMA di qualunque import/chiamata al sistema: se
# non arriva nemmeno questa, il problema è l'avvio del processo stesso
# (python3 non trovato/non eseguibile), non GeoClue2 (vedi GeolocationService)
emit('STARTED')

try:
    import gi
    gi.require_version('Geoclue', '2.0')
    from gi.repository import Geoclue, GLib, Gio
except Exception as e:
    emit('ERROR:PyGObject/GeoClue2 non disponibili (%s)' % e)
    sys.exit(1)

def on_g_signal(proxy, sender_name, signal_name, params):
    if signal_name != 'LocationUpdated':
        return
    try:
        _old_path, new_path = params.unpack()
        loc = Geoclue.Location.new_for_bus_sync(
            Gio.BusType.SYSTEM, 0, 'org.freedesktop.GeoClue2', new_path, None)
        emit('FIX:%r:%r:%r' % (
            loc.get_property('latitude'),
            loc.get_property('longitude'),
            loc.get_property('accuracy')))
    except Exception as e:
        emit('ERROR:%s' % e)

def main():
    try:
        manager = Geoclue.ManagerProxy.new_for_bus_sync(
            Gio.BusType.SYSTEM, 0, 'org.freedesktop.GeoClue2',
            '/org/freedesktop/GeoClue2/Manager', None)
        client_path = manager.call_get_client_sync(None)
        client = Geoclue.ClientProxy.new_for_bus_sync(
            Gio.BusType.SYSTEM, 0, 'org.freedesktop.GeoClue2', client_path, None)
        client.set_property('desktop-id', 'stradarioapp')
        client.set_property('requested-accuracy-level', int(Geoclue.AccuracyLevel.EXACT))
        client.connect('g-signal', on_g_signal)
        client.call_start_sync(None)
    except Exception as e:
        emit('ERROR:%s' % e)
        sys.exit(1)

    GLib.MainLoop().run()

main()
";

        // -------------------------------------------------------------------
        // Windows: Windows Location API (WinRT Geolocator) via PowerShell.
        // NON VERIFICATO su una macchina Windows reale (ambiente di sviluppo
        // Linux): stesso formato di output "FIX:lat:lon:accuracy" / "ERROR:msg".
        // -------------------------------------------------------------------
        private const string WindowsLocationScript = @"
Write-Output 'STARTED'

# Attesa non bloccante di un'operazione WinRT asincrona (IAsyncOperation),
# con timeout esplicito: usare .GetAwaiter().GetResult()/.Wait() direttamente
# rischia un blocco indefinito e silenzioso se la marshalling COM/WinRT non
# completa mai in un processo console senza message pump (nessun output,
# nessun errore: il sintomo esatto di 'non dice niente' da diagnosticare).
# Il polling sullo Status evita questo rischio a costo di una latenza minima.
function Wait-WinRtOp($op, [int]$timeoutMs) {
    $elapsed = 0
    while ($op.Status -eq [Windows.Foundation.AsyncStatus]::Started -and $elapsed -lt $timeoutMs) {
        Start-Sleep -Milliseconds 200
        $elapsed += 200
    }
    return $op
}

$ErrorActionPreference = 'Stop'
try {
    [void][Windows.Devices.Geolocation.Geolocator,Windows.Devices.Geolocation,ContentType=WindowsRuntime]

    $accessOp = [Windows.Devices.Geolocation.Geolocator]::RequestAccessAsync()
    $accessOp = Wait-WinRtOp $accessOp 10000
    if ($accessOp.Status -ne [Windows.Foundation.AsyncStatus]::Completed) {
        Write-Output (""ERROR:Richiesta di accesso alla posizione scaduta (stato: $($accessOp.Status)) - il sistema non ha risposto entro 10s"")
        exit 1
    }
    $access = $accessOp.GetResults()
    if ($access -ne [Windows.Devices.Geolocation.GeolocationAccessStatus]::Allowed) {
        Write-Output (""ERROR:Accesso alla posizione negato dal sistema (stato: $access). Verifica Impostazioni > Privacy e sicurezza > Posizione."")
        exit 1
    }

    $geolocator = New-Object Windows.Devices.Geolocation.Geolocator
    $geolocator.DesiredAccuracy = [Windows.Devices.Geolocation.PositionAccuracy]::High

    $action = {
        $c = $Event.SourceEventArgs.Position.Coordinate
        Write-Output (""FIX:{0}:{1}:{2}"" -f $c.Point.Position.Latitude, $c.Point.Position.Longitude, $c.Accuracy)
    }
    Register-ObjectEvent -InputObject $geolocator -EventName PositionChanged -Action $action | Out-Null

    try {
        $posOp = $geolocator.GetGeopositionAsync()
        $posOp = Wait-WinRtOp $posOp 15000
        if ($posOp.Status -ne [Windows.Foundation.AsyncStatus]::Completed) {
            Write-Output (""ERROR:Richiesta della posizione scaduta (stato: $($posOp.Status)) - resto in ascolto di aggiornamenti"")
        } else {
            $pos = $posOp.GetResults()
            $c = $pos.Coordinate
            Write-Output (""FIX:{0}:{1}:{2}"" -f $c.Point.Position.Latitude, $c.Point.Position.Longitude, $c.Accuracy)
        }
    } catch {
        Write-Output (""ERROR:"" + $_.Exception.Message)
    }

    while ($true) { Start-Sleep -Seconds 1 }
}
catch {
    Write-Output (""ERROR:"" + $_.Exception.Message)
    exit 1
}
";
    }
}
