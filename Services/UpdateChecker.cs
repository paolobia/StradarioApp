// =============================================================================
// Services/UpdateChecker.cs
//
// SINOSSI: Controllo silenzioso di una nuova versione all'avvio, confrontando
//   la versione corrente (StradarioApp.csproj <Version>, letta dall'assembly)
//   con l'ultimo tag pubblicato su GitHub Releases
//   (paolobia/StradarioApp, endpoint pubblico /releases/latest, nessuna
//   autenticazione necessaria). Qualunque errore (offline, rate limit,
//   risposta inattesa) deve degradare a "nessun aggiornamento", mai a
//   un'eccezione visibile: è un controllo opportunistico in background,
//   non una funzionalità critica.
// =============================================================================

using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace StradarioApp.Services
{
    public record UpdateInfo(string LatestVersion, string ReleaseUrl);

    public static class UpdateChecker
    {
        private const string LatestReleaseApiUrl =
            "https://api.github.com/repos/paolobia/StradarioApp/releases/latest";

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };

        static UpdateChecker()
        {
            // GitHub rifiuta richieste API senza User-Agent; stesso header
            // identificativo usato per Overpass/Nominatim/GeoNames dal resto
            // dell'app (vedi MapRenderer/CityDatabase/PoiSearchService).
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("StradarioApp/1.0 (educational use)");
            Http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        }

        // Versione corrente da <Version> nel .csproj, impostata da MSBuild come
        // AssemblyVersion (es. "1.0.4.0" -> "1.0.4", senza il trailing ".0"
        // aggiunto quando il terzo numero manca dall'AssemblyVersion a 4 campi).
        public static string CurrentVersion
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version;
                return v is null ? "0.0.0" : new Version(v.Major, v.Minor, v.Build).ToString();
            }
        }

        // Ritorna null se non è disponibile alcun aggiornamento (versione
        // corrente già la più recente) o se il controllo fallisce per
        // qualunque motivo (nessuna eccezione propagata al chiamante).
        public static async Task<UpdateInfo?> CheckForNewerVersionAsync()
        {
            try
            {
                var json = await Http.GetStringAsync(LatestReleaseApiUrl);
                var obj  = JObject.Parse(json);
                var tag  = (string?)obj["tag_name"];
                var url  = (string?)obj["html_url"];
                if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(url))
                    return null;

                var latest  = tag.TrimStart('v', 'V');
                if (!Version.TryParse(latest, out var latestVersion))
                    return null;
                if (!Version.TryParse(CurrentVersion, out var currentVersion))
                    return null;

                return latestVersion > currentVersion ? new UpdateInfo(latest, url) : null;
            }
            catch
            {
                // Offline, rate limit GitHub, risposta inattesa: nessun
                // aggiornamento segnalato, silenziosamente.
                return null;
            }
        }
    }
}
