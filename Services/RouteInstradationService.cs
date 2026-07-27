// =============================================================================
// Services/RouteInstradationService.cs
//
// SINOSSI: Instrada un Percorso esistente (disegnato a mano o importato, MAI
//   da zero) sulla rete stradale reale tramite il server pubblico OSRM
//   (router.project-osrm.org — nessuna chiave, nessun self-hosting, soggetto
//   a rate-limit/fair-use, vedi MainWindow.StartInstradaMode per il limite di
//   5 vertici). OSRM calcola alternative vere solo per query O-D a due punti,
//   non con via multipli: per bypassare questo limite si fa una richiesta
//   SEPARATA per ogni coppia di vertici consecutivi del percorso (max 4
//   tratte per 5 vertici), mai in parallelo — throttling identico nello
//   spirito a quello già usato per Nominatim in PoiSearchService
//   (ThrottleNominatimAsync), ma con il proprio semaforo/orario perché è un
//   host pubblico diverso con la propria policy d'uso.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using StradarioApp.Models;

namespace StradarioApp.Services
{
    public class RouteInstradationService
    {
        // EnabledSslProtocols esplicito invece di affidarsi al default di
        // sistema: su alcune configurazioni/versioni Windows TLS 1.2 non è
        // abilitato di default a livello di sistema operativo, causando
        // "The SSL connection could not be established" con HttpClient —
        // riscontrato davvero da un utente in beta test su Windows.
        private static readonly HttpClient Http = new HttpClient(new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            }
        })
        { Timeout = TimeSpan.FromSeconds(25) };

        // Client di fallback, solo TLS 1.2: riscontrato in un secondo test
        // (PC domestico, nessun antivirus/proxy di mezzo — quindi non
        // interferenza di rete) un vero TLS alert "HandshakeFailure" con
        // Http sopra, sintomo tipico di un supporto TLS 1.3 instabile nello
        // stack SChannel di quella specifica installazione Windows. Invece
        // di indovinare la versione di Windows dell'utente, si ritenta UNA
        // volta con solo Tls12 (protocollo maturo, supportato ovunque) solo
        // quando il primo tentativo fallisce con un errore riconducibile a
        // SSL/handshake.
        private static readonly HttpClient HttpTls12Only = new HttpClient(new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls12
            }
        })
        { Timeout = TimeSpan.FromSeconds(25) };

        static RouteInstradationService()
        {
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("StradarioApp/1.0 (educational use)");
            HttpTls12Only.DefaultRequestHeaders.UserAgent.ParseAdd("StradarioApp/1.0 (educational use)");
        }

        private static bool IsSslRelated(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is AuthenticationException) return true;
                if (e.GetType().Name.Contains("Win32Exception")) return true;
            }
            return false;
        }

        private DateTime _lastCallUtc = DateTime.MinValue;
        private readonly SemaphoreSlim _throttle = new(1, 1);

        private async Task ThrottleAsync(CancellationToken ct)
        {
            await _throttle.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var minInterval = TimeSpan.FromSeconds(1.1);
                var elapsed     = DateTime.UtcNow - _lastCallUtc;
                if (elapsed < minInterval)
                    await Task.Delay(minInterval - elapsed, ct).ConfigureAwait(false);
                _lastCallUtc = DateTime.UtcNow;
            }
            finally { _throttle.Release(); }
        }

        public enum Profile { Auto, Bici, Piedi }

        private static string ProfileSegment(Profile p) => p switch
        {
            Profile.Auto  => "driving",
            Profile.Bici  => "cycling",
            Profile.Piedi => "foot",
            _             => "driving",
        };

        public record RouteAlternative(List<GeoPoint> Geometry, double DistanceMeters, double DurationSeconds);

        // SelectedIndex = -1 quando Failed (nessuna alternativa disponibile).
        // Essendo un record, per cambiare la selezione si sostituisce l'intero
        // elemento nella lista con "leg with { SelectedIndex = nuovoIndice }",
        // non lo si muta in place. ErrorMessage (solo quando Failed) riporta
        // il motivo REALE del fallimento (eccezione .NET o "code" OSRM non
        // "Ok") — DebugLog.Write è un no-op nelle build Release (vedi
        // Services/DebugLog.cs), quindi per un utente remoto su un
        // eseguibile pubblicato l'unico modo di capire un fallimento di rete
        // (firewall, proxy, DNS...) è mostrarglielo direttamente in app,
        // vedi RouteInstradationPanel.SetLegs.
        public record LegResult(GeoPoint From, GeoPoint To, List<RouteAlternative> Alternatives, int SelectedIndex, bool Failed, string? ErrorMessage = null);

        private static string FmtCoord(double v) => v.ToString(CultureInfo.InvariantCulture);

        // Una singola tratta O-D: richiede alternatives=true (OSRM la onora
        // davvero solo per query a due soli punti, esattamente questo caso),
        // geometria completa in GeoJSON. Le alternative vengono riordinate
        // ESPLICITAMENTE per distanza crescente — l'ordine restituito da OSRM
        // non è garantito essere per lunghezza — e la più corta è la
        // selezione di default (SelectedIndex = 0).
        public async Task<LegResult> RouteLegAsync(GeoPoint from, GeoPoint to, Profile profile, CancellationToken ct)
        {
            await ThrottleAsync(ct).ConfigureAwait(false);

            string url = "https://router.project-osrm.org/route/v1/" +
                $"{ProfileSegment(profile)}/{FmtCoord(from.Lon)},{FmtCoord(from.Lat)};{FmtCoord(to.Lon)},{FmtCoord(to.Lat)}" +
                "?alternatives=true&overview=full&geometries=geojson&steps=false";

            string json;
            try
            {
                json = await Http.GetStringAsync(url, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception firstEx) when (IsSslRelated(firstEx))
            {
                try
                {
                    json = await HttpTls12Only.GetStringAsync(url, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    return new LegResult(from, to, new List<RouteAlternative>(), -1, Failed: true, ErrorMessage: DescribeException(ex));
                }
            }
            catch (Exception ex)
            {
                return new LegResult(from, to, new List<RouteAlternative>(), -1, Failed: true, ErrorMessage: DescribeException(ex));
            }

            try
            {
                var root = JObject.Parse(json);

                string? code = (string?)root["code"];
                var routesArr = root["routes"] as JArray;
                if (!string.Equals(code, "Ok", StringComparison.OrdinalIgnoreCase) || routesArr == null || routesArr.Count == 0)
                {
                    string? osrmMessage = (string?)root["message"];
                    string reason = string.IsNullOrWhiteSpace(osrmMessage) ? (code ?? "risposta OSRM senza \"routes\"") : $"{code}: {osrmMessage}";
                    return new LegResult(from, to, new List<RouteAlternative>(), -1, Failed: true, ErrorMessage: reason);
                }

                var alternatives = new List<RouteAlternative>();
                foreach (var r in routesArr)
                {
                    double distance = (double?)r["distance"] ?? 0;
                    double duration = (double?)r["duration"] ?? 0;
                    var coords = r["geometry"]?["coordinates"] as JArray;
                    if (coords == null || coords.Count == 0) continue;

                    var geometry = new List<GeoPoint>(coords.Count);
                    foreach (var c in coords)
                    {
                        double lon = (double)c[0]!;
                        double lat = (double)c[1]!;
                        geometry.Add(new GeoPoint { Lon = lon, Lat = lat });
                    }
                    alternatives.Add(new RouteAlternative(geometry, distance, duration));
                }

                if (alternatives.Count == 0)
                    return new LegResult(from, to, new List<RouteAlternative>(), -1, Failed: true, ErrorMessage: "nessuna geometria valida nella risposta OSRM");

                alternatives = alternatives.OrderBy(a => a.DistanceMeters).ToList();
                return new LegResult(from, to, alternatives, 0, Failed: false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Tipicamente qui: HttpRequestException (rete/DNS/firewall/
                // certificato), TaskCanceledException per timeout (25s, vedi
                // Http sopra), o JsonException se OSRM risponde con qualcosa
                // di inatteso. Il messaggio arriva fino all'utente tramite
                // RouteInstradationPanel — è l'unico modo di diagnosticare un
                // fallimento di rete su un eseguibile pubblicato da remoto.
                // Fondamentale scendere fino alle InnerException: per una
                // HttpRequestException di tipo TLS, ex.Message da solo è
                // spesso solo "The SSL connection could not be established,
                // see inner exception" — il motivo VERO (es. protocollo TLS
                // non supportato, certificato non attendibile) sta annidato
                // più sotto (riscontrato davvero in un test remoto).
                return new LegResult(from, to, new List<RouteAlternative>(), -1, Failed: true, ErrorMessage: DescribeException(ex));
            }
        }

        private static string DescribeException(Exception ex)
        {
            var sb = new StringBuilder();
            sb.Append(ex.GetType().Name).Append(": ").Append(ex.Message);
            var inner = ex.InnerException;
            while (inner != null)
            {
                sb.Append(" -> ").Append(inner.GetType().Name).Append(": ").Append(inner.Message);
                inner = inner.InnerException;
            }
            return sb.ToString();
        }

        // Una tratta per ogni coppia di vertici consecutivi, sempre in
        // sequenza (mai Task.WhenAll: rispetta il throttling verso il server
        // pubblico). onLegDone(indiceTratta, totaleTratte) per il feedback
        // progressivo nel pannello mentre le richieste procedono.
        public async Task<List<LegResult>> RouteAllLegsAsync(
            List<GeoPoint> vertices, Profile profile, Action<int, int>? onLegDone, CancellationToken ct)
        {
            var results = new List<LegResult>();
            int totalLegs = Math.Max(0, vertices.Count - 1);
            for (int i = 0; i < totalLegs; i++)
            {
                ct.ThrowIfCancellationRequested();
                var leg = await RouteLegAsync(vertices[i], vertices[i + 1], profile, ct).ConfigureAwait(false);
                results.Add(leg);
                onLegDone?.Invoke(i, totalLegs);
            }
            return results;
        }
    }
}
