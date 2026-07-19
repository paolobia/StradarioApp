// =============================================================================
// Services/PoiSearchService.cs
//
// SINOSSI: Ricerca testuale di POI via Nominatim (OpenStreetMap), limitata
//   all'area attualmente visualizzata sulla mappa (bounded=1: la ricerca
//   NON esce dal riquadro passato, coerente con "tenendo conto della zona
//   visualizzata"). Un solo HttpClient/richiesta per ricerca interattiva
//   avviata dall'utente: rispetta la policy d'uso di Nominatim (max ~1
//   richiesta/secondo, User-Agent identificativo — stesso header usato dal
//   resto dell'app per il fetch dei tile, vedi MapRenderer/PdfGenerator).
//   addressdetails=1 su entrambe le chiamate: serve a costruire un indirizzo
//   compatto (via, civico, CAP, città) per il tooltip dei risultati sulla
//   mappa, più ricco del solo display_name troncato. accept-language=it,en:
//   nomi/indirizzi restituiti in italiano quando OSM ha quella traduzione
//   (name:it), altrimenti inglese, altrimenti il nome locale originale (es.
//   in cinese) — Nominatim continua comunque a cercare per corrispondenza
//   testuale nei dati OSM, quindi non "traduce" la query stessa: un termine
//   generico in italiano non troverà negozi che hanno solo un nome locale
//   non tradotto — per quello c'è TryMatchCategory + SearchCategoryAsync:
//   se la query digitata è (esattamente) una delle parole chiave note
//   ("stazione"/"station", "farmacia"/"pharmacy", ecc.) la ricerca non passa
//   più da Nominatim ma da Overpass API, cercando il TAG OSM corrispondente
//   (es. railway=station) in tutta l'area visualizzata: trova quindi anche
//   una stazione taggata solo "北京站" a Pechino, perché non sta confrontando
//   testo ma la classificazione semantica del punto.
//
//   Ricerca in linguaggio naturale (Groq, in fondo al file): 3 fasi separate
//   — A) GenerateHypothesesAsync, l'LLM propone luoghi reali dalla propria
//   conoscenza del mondo (non una tassonomia di tag: può proporre un luogo
//   specifico per nome); B) VerifyHypothesisAsync, ogni ipotesi va riscontrata
//   su OSM (reverse geocoding + fuzzy match sul nome, poi fallback Overpass
//   per nome entro un raggio) prima di fidarsene — un'ipotesi non confermata
//   resta comunque nel risultato con Verified=false, non viene scartata;
//   C) FilterAndRank, filtro geografico + ordinamento, puramente locale.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Newtonsoft.Json;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using StradarioApp.Models;

namespace StradarioApp.Services
{
    public class PoiSearchService
    {
        // Overpass (usato da SearchCategoryAsync) dichiara un timeout server-side
        // di 15s nella query stessa ("[timeout:15]"): il client deve concedergliene
        // di più, altrimenti va in timeout lato client prima ancora di ricevere
        // la risposta, specie su viste ampie (interi paesi) o quando il server
        // pubblico è sotto carico
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };

        // Limite risultati di SearchCategoryAsync per singola query Overpass:
        // sufficiente per la stragrande maggioranza delle ricerche, ma una
        // categoria molto densa in un'area vasta (es. "stazioni ferroviarie"
        // su una grande metropoli, dove spesso anche le fermate della
        // metropolitana condividono lo stesso tag) può comunque saturarlo.
        // Pubblico così il chiamante può confrontarlo con Result.Count e
        // avvisare che l'elenco potrebbe non essere completo, invece di far
        // credere che lo sia.
        public const int CategoryResultCap = 200;

        static PoiSearchService()
        {
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("StradarioApp/1.0 (educational use)");
        }

        // Throttling lato client per rispettare la policy d'uso di Nominatim
        // (max ~1 richiesta/secondo): la Fase B della ricerca in linguaggio
        // naturale può fare una reverse-geocode per OGNI ipotesi in sequenza,
        // quindi qui (a differenza delle singole ricerche interattive di prima,
        // dove bastava il fatto che l'utente non può cliccare più veloce di
        // così) serve un limite esplicito. Un SemaphoreSlim(1,1) invece di un
        // semplice campo perché più chiamate potrebbero sovrapporsi (nessuna
        // in pratica qui, essendo la Fase B sequenziale, ma è comunque il modo
        // corretto di serializzare un accesso condiviso in async).
        private DateTime _lastNominatimCallUtc = DateTime.MinValue;
        private readonly SemaphoreSlim _nominatimThrottle = new(1, 1);

        private async Task ThrottleNominatimAsync(CancellationToken ct)
        {
            await _nominatimThrottle.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var minInterval = TimeSpan.FromSeconds(1.1);
                var elapsed     = DateTime.UtcNow - _lastNominatimCallUtc;
                if (elapsed < minInterval)
                    await Task.Delay(minInterval - elapsed, ct).ConfigureAwait(false);
                _lastNominatimCallUtc = DateTime.UtcNow;
            }
            finally { _nominatimThrottle.Release(); }
        }

        // Category/Type = tag OSM grezzi (es. "shop"/"butcher", "amenity"/"restaurant"),
        // usati per una riga descrittiva nel tooltip. Address = via+civico,
        // CAP+città formattati su una riga, quando Nominatim li fornisce.
        // Verified: true per tutte le ricerche "classiche" (Nominatim/Overpass
        // dirette, comportamento storico — un elemento trovato così è per
        // definizione reale). false SOLO per un risultato della ricerca in
        // linguaggio naturale (vedi VerifyHypothesisAsync) che l'LLM ha
        // proposto ma nessun riscontro su OpenStreetMap ha confermato.
        public record Result(string DisplayName, double Lon, double Lat, string? Category, string? Type, string? Address, string? Description = null, bool Verified = true);

        // Cerca `query` su Nominatim, limitando i risultati al rettangolo
        // geografico `viewBounds` (tipicamente l'area attualmente visualizzata
        // sulla mappa interattiva)
        public async Task<List<Result>> SearchAsync(string query, GeoRect viewBounds, CancellationToken ct = default)
        {
            await ThrottleNominatimAsync(ct);

            string url =
                "https://nominatim.openstreetmap.org/search" +
                $"?q={Uri.EscapeDataString(query)}" +
                "&format=jsonv2&limit=15&bounded=1&addressdetails=1&accept-language=it,en" +
                $"&viewbox={Fmt(viewBounds.MinLon)},{Fmt(viewBounds.MaxLat)},{Fmt(viewBounds.MaxLon)},{Fmt(viewBounds.MinLat)}";

            using var resp = await Http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();
            string json = await resp.Content.ReadAsStringAsync(ct);

            var results = new List<Result>();
            foreach (var item in JArray.Parse(json))
            {
                var result = ParseResult(item);
                if (result != null) results.Add(result);
            }
            return results;
        }

        // Geocodifica un nome di luogo (città/paese/regione, es. "Pechino" o
        // "Prato") nel suo riquadro geografico su Nominatim, SENZA restringere
        // a un'area (a differenza di SearchAsync): usata dalla ricerca in
        // linguaggio naturale (Fase A → LocationHints) quando la query nomina
        // esplicitamente un luogo diverso dall'area visualizzata sulla mappa,
        // per cercare (Fase B) lì invece che nel riquadro visibile corrente.
        // Ritorna null se il luogo non viene trovato.
        public async Task<GeoRect?> GeocodePlaceAsync(string placeName, CancellationToken ct = default)
        {
            await ThrottleNominatimAsync(ct);

            string url =
                "https://nominatim.openstreetmap.org/search" +
                $"?q={Uri.EscapeDataString(placeName)}" +
                "&format=jsonv2&limit=1&accept-language=it,en";

            using var resp = await Http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();
            string json = await resp.Content.ReadAsStringAsync(ct);

            var arr = JArray.Parse(json);
            if (arr.Count == 0) return null;

            var bbox = arr[0]["boundingbox"] as JArray;
            if (bbox == null || bbox.Count < 4) return null;

            // Ordine Nominatim: [min_lat, max_lat, min_lon, max_lon] (stringhe)
            double minLat = double.Parse(bbox[0].Value<string>()!, CultureInfo.InvariantCulture);
            double maxLat = double.Parse(bbox[1].Value<string>()!, CultureInfo.InvariantCulture);
            double minLon = double.Parse(bbox[2].Value<string>()!, CultureInfo.InvariantCulture);
            double maxLon = double.Parse(bbox[3].Value<string>()!, CultureInfo.InvariantCulture);

            return new GeoRect { MinLon = minLon, MinLat = minLat, MaxLon = maxLon, MaxLat = maxLat };
        }

        // Ricerca inversa: cosa si trova nel punto geografico (lon, lat)
        // indicato. Nominatim ritorna al più un singolo luogo/indirizzo (il
        // più vicino), non un elenco
        public async Task<Result?> ReverseAsync(double lon, double lat, CancellationToken ct = default)
        {
            await ThrottleNominatimAsync(ct);

            string url =
                "https://nominatim.openstreetmap.org/reverse" +
                $"?lat={Fmt(lat)}&lon={Fmt(lon)}&format=jsonv2&zoom=18&addressdetails=1&accept-language=it,en";

            using var resp = await Http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();
            string json = await resp.Content.ReadAsStringAsync(ct);

            var obj = JObject.Parse(json);
            if (obj["error"] != null) return null;

            return ParseResult(obj);
        }

        private static Result? ParseResult(JToken item)
        {
            double? lon = item["lon"]?.Value<string>() is string lonStr ? double.Parse(lonStr, CultureInfo.InvariantCulture) : null;
            double? lat = item["lat"]?.Value<string>() is string latStr ? double.Parse(latStr, CultureInfo.InvariantCulture) : null;
            string? name = item["display_name"]?.Value<string>();
            if (lon == null || lat == null || string.IsNullOrWhiteSpace(name)) return null;

            return new Result(
                name!, lon.Value, lat.Value,
                item["class"]?.Value<string>(),
                item["type"]?.Value<string>(),
                FormatAddress(item["address"] as JObject));
        }

        // Compatta i campi strutturati di "address" in una o due righe leggibili:
        // "Via Roma 12" e "00100 Roma" (solo i pezzi effettivamente presenti)
        private static string? FormatAddress(JObject? addr)
        {
            if (addr == null) return null;

            string? road  = addr["road"]?.Value<string>();
            string? house = addr["house_number"]?.Value<string>();
            string? postcode = addr["postcode"]?.Value<string>();
            string? city = addr["city"]?.Value<string>()
                ?? addr["town"]?.Value<string>()
                ?? addr["village"]?.Value<string>()
                ?? addr["municipality"]?.Value<string>();

            string line1 = string.Join(" ", new[] { road, house }.Where(s => !string.IsNullOrWhiteSpace(s)));
            string line2 = string.Join(" ", new[] { postcode, city }.Where(s => !string.IsNullOrWhiteSpace(s)));
            string joined = string.Join(", ", new[] { line1, line2 }.Where(s => !string.IsNullOrWhiteSpace(s)));

            return string.IsNullOrWhiteSpace(joined) ? null : joined;
        }

        private static string Fmt(double v) => v.ToString("F7", CultureInfo.InvariantCulture);

        // ---------------------------------------------------------------
        // Ricerca per categoria (Overpass API): riconosce parole chiave
        // generiche IT/EN e le traduce nel tag OSM corrispondente, invece di
        // cercare quella parola come testo nei nomi (vedi nota in testa al file)
        // ---------------------------------------------------------------
        private static readonly (string[] Keywords, string Key, string Value, string Label)[] Categories =
        {
            (new[] { "stazione", "stazioni", "station" },                          "railway", "station",       "stazioni ferroviarie"),
            (new[] { "metro", "metropolitana", "subway" },                         "station", "subway",        "stazioni della metropolitana"),
            (new[] { "farmacia", "farmacie", "pharmacy" },                         "amenity", "pharmacy",      "farmacie"),
            (new[] { "ristorante", "ristoranti", "restaurant" },                   "amenity", "restaurant",    "ristoranti"),
            (new[] { "bar", "pub" },                                               "amenity", "bar",           "bar"),
            (new[] { "caffe", "caffè", "cafe", "coffee" },                         "amenity", "cafe",          "caffetterie"),
            (new[] { "parcheggio", "parcheggi", "parking" },                       "amenity", "parking",       "parcheggi"),
            (new[] { "ospedale", "ospedali", "hospital" },                         "amenity", "hospital",      "ospedali"),
            (new[] { "scuola", "scuole", "school" },                               "amenity", "school",        "scuole"),
            (new[] { "hotel", "albergo", "alberghi" },                             "tourism", "hotel",         "hotel"),
            (new[] { "supermercato", "supermercati", "supermarket" },              "shop",    "supermarket",   "supermercati"),
            (new[] { "macelleria", "macellerie", "butcher" },                      "shop",    "butcher",       "macellerie"),
            (new[] { "panetteria", "panificio", "bakery" },                        "shop",    "bakery",        "panetterie"),
            (new[] { "banca", "banche", "bank" },                                  "amenity", "bank",          "banche"),
            (new[] { "bancomat", "atm" },                                          "amenity", "atm",           "bancomat"),
            (new[] { "chiesa", "chiese", "church" },                               "amenity", "place_of_worship", "luoghi di culto"),
            (new[] { "museo", "musei", "museum" },                                 "tourism", "museum",        "musei"),
            (new[] { "aeroporto", "aeroporti", "airport" },                        "aeroway", "aerodrome",     "aeroporti"),
            (new[] { "fermata", "bus stop" },                                      "highway", "bus_stop",      "fermate bus"),
            (new[] { "bagno", "bagni", "toilette", "toilet", "restroom" },         "amenity", "toilets",       "servizi igienici"),
            (new[] { "benzina", "distributore", "distributori", "fuel", "petrol" },"amenity", "fuel",          "distributori di benzina"),
            (new[] { "posta", "ufficio postale", "post office" },                  "amenity", "post_office",   "uffici postali"),
            (new[] { "biblioteca", "biblioteche", "library" },                     "amenity", "library",       "biblioteche"),
            (new[] { "cinema" },                                                   "amenity", "cinema",        "cinema"),

            // Aggiunte per il selettore a categoria (combobox toolbar, vedi
            // AllCategories/OnCategorySearchSelected in MainWindow) — utili
            // soprattutto "da turista"
            (new[] { "taxi" },                                                     "amenity", "taxi",          "posteggi taxi"),
            (new[] { "capolinea", "capolinea bus", "bus station" },                "amenity", "bus_station",   "capolinea bus"),
            (new[] { "terminal", "terminal aeroporto", "airport terminal" },       "aeroway", "terminal",      "terminal aeroportuali"),
            (new[] { "attrazione", "attrazioni", "attraction" },                   "tourism", "attraction",    "attrazioni turistiche"),
            (new[] { "panorama", "punto panoramico", "viewpoint" },                "tourism", "viewpoint",     "punti panoramici"),
            (new[] { "monumento", "monumenti", "monument" },                       "historic","monument",      "monumenti"),
            (new[] { "noleggio bici", "bike rental", "bicycle rental" },           "amenity", "bicycle_rental","noleggio bici"),
            (new[] { "parcheggio bici", "bike parking", "bicycle parking" },       "amenity", "bicycle_parking","parcheggi bici"),
            (new[] { "ufficio turistico", "informazioni turistiche", "tourist information" }, "tourism", "information", "uffici turistici"),
            (new[] { "cambio", "cambio valuta", "currency exchange" },             "amenity", "bureau_de_change","uffici cambio"),
        };

        // Vista pubblica di sola lettura di Categories, per il selettore a
        // combobox nella toolbar (MainWindow): solo Key/Value/Label, non le
        // parole chiave (quelle servono solo al riconoscimento testuale di
        // TryMatchCategory, non alla UI).
        public static IReadOnlyList<(string Key, string Value, string Label)> AllCategories { get; } =
            Categories.Select(c => (c.Key, c.Value, c.Label)).ToList();

        // Sottofiltri di ESCLUSIONE applicati sempre a una categoria (oltre a
        // quelli passati dal chiamante), a prescindere da come vi si arriva
        // (parola chiave digitata o selettore a combobox) — vedi
        // RunCategorySearchAsync in MainWindow, che li unisce automaticamente.
        // Caso reale che li ha resi necessari: a Pechino (e in molte altre
        // grandi città) le stazioni della metropolitana sono anch'esse taggate
        // railway=station (con station=subway in aggiunta), quindi una ricerca
        // "stazioni ferroviarie" senza questa esclusione le mescola — in una
        // città con moltissime fermate di metro, queste riempiono da sole il
        // limite di risultati della query, lasciando fuori le stazioni
        // ferroviarie vere e proprie.
        private static readonly Dictionary<(string Key, string Value), string[]> CategoryExcludeFilters = new()
        {
            [("railway", "station")] = new[] { "station!=subway" },
        };

        public static string[] GetCategoryExcludeFilters(string key, string value) =>
            CategoryExcludeFilters.TryGetValue((key, value), out var f) ? f : Array.Empty<string>();

        // Riconosce se `query` è (per intero, non come sottostringa — evita
        // falsi positivi tipo "via del bar vecchio") una delle parole chiave di
        // una categoria nota
        public static bool TryMatchCategory(string query, out string key, out string value, out string label)
        {
            string q = (query ?? "").Trim().ToLowerInvariant();
            foreach (var cat in Categories)
            {
                if (cat.Keywords.Any(k => k.Equals(q, StringComparison.OrdinalIgnoreCase)))
                {
                    key = cat.Key; value = cat.Value; label = cat.Label;
                    return true;
                }
            }
            key = value = label = "";
            return false;
        }

        // Cerca tramite Overpass API tutti gli elementi con tag key=value
        // nell'area geografica `viewBounds`. A differenza di SearchAsync non
        // cerca per nome: trova quindi anche elementi il cui nome è scritto in
        // una lingua/alfabeto diverso da quello della parola chiave digitata.
        // subFilters (opzionale): altri tag "chiave=valore" in AND, usati dalla
        // ricerca in linguaggio naturale (Fase A) per restringere il tag
        // principale (es. amenity=restaurant + cuisine=pizza). nameFilter
        // (opzionale): sottostringa (case-insensitive) da cercare nel tag
        // "name", per richieste che identificano un luogo specifico per nome
        // oltre che per categoria (es. "stazione CENTRALE" → railway=station
        // + nameFilter="centrale": senza questo, "stazioni a Prato e Pistoia"
        // troverebbe TUTTE le stazioni/fermate nell'area, non solo quelle
        // centrali — un tag OSM da solo non basta a distinguerle, serve il
        // nome). Cache in memoria per bbox+tag+sottofiltri+nameFilter, per non
        // richiamare Overpass più volte per la stessa identica ricerca (es.
        // l'utente che riprova o pan minimi).
        public async Task<List<Result>> SearchCategoryAsync(string key, string value, GeoRect viewBounds,
            IEnumerable<string>? subFilters = null, string? nameFilter = null, CancellationToken ct = default)
        {
            var subList = (subFilters ?? Enumerable.Empty<string>()).ToList();

            string cacheKey = BuildCategoryCacheKey(key, value, viewBounds, subList, nameFilter);
            if (_categoryCache.TryGetValue(cacheKey, out var cached) && cached.Expiry > DateTime.UtcNow)
                return cached.Results;

            string extraFilters = "";
            foreach (string sf in subList)
            {
                // "chiave!=valore" = esclusione (es. "station!=subway" per
                // togliere le fermate di metropolitana da una ricerca di
                // stazioni ferroviarie, vedi CategoryExcludeFilters sopra);
                // "chiave=valore" = filtro positivo in AND, come prima.
                int negIdx = sf.IndexOf("!=", StringComparison.Ordinal);
                bool negate = negIdx > 0;
                int eq = negate ? negIdx : sf.IndexOf('=');
                int valueStart = negate ? negIdx + 2 : eq + 1;
                if (eq <= 0 || valueStart >= sf.Length) continue;

                string sk = EscapeOverpassValue(sf.Substring(0, eq).Trim());
                string sv = EscapeOverpassValue(sf.Substring(valueStart).Trim());
                if (sk.Length == 0 || sv.Length == 0) continue;
                extraFilters += negate ? $"[\"{sk}\"!=\"{sv}\"]" : $"[\"{sk}\"=\"{sv}\"]";
            }
            if (!string.IsNullOrWhiteSpace(nameFilter))
                extraFilters += $"[\"name\"~\"{EscapeOverpassRegex(nameFilter.Trim())}\",i]";

            string bbox = $"{Fmt(viewBounds.MinLat)},{Fmt(viewBounds.MinLon)},{Fmt(viewBounds.MaxLat)},{Fmt(viewBounds.MaxLon)}";
            string ql =
                "[out:json][timeout:20];" +
                "(" +
                $"node[\"{key}\"=\"{value}\"]{extraFilters}({bbox});" +
                $"way[\"{key}\"=\"{value}\"]{extraFilters}({bbox});" +
                ");" +
                $"out center {CategoryResultCap};";

            using var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("data", ql) });
            using var resp = await Http.PostAsync("https://overpass-api.de/api/interpreter", content, ct);
            resp.EnsureSuccessStatusCode();
            string json = await resp.Content.ReadAsStringAsync(ct);

            var root = JObject.Parse(json);
            var results = new List<Result>();
            foreach (var el in (root["elements"] as JArray) ?? new JArray())
            {
                // I "node" hanno lat/lon diretti; i "way" (es. un edificio) solo
                // col "center" richiesto da "out center" nella query
                double? lat = el["lat"]?.Value<double?>() ?? el["center"]?["lat"]?.Value<double?>();
                double? lon = el["lon"]?.Value<double?>() ?? el["center"]?["lon"]?.Value<double?>();
                if (lat == null || lon == null) continue;

                var tags = el["tags"] as JObject;
                string? name = tags?["name:it"]?.Value<string>()
                    ?? tags?["name:en"]?.Value<string>()
                    ?? tags?["name"]?.Value<string>();

                results.Add(new Result(
                    string.IsNullOrWhiteSpace(name) ? $"{value} (senza nome)" : name!,
                    lon.Value, lat.Value,
                    key, value, null));
            }

            if (_categoryCache.Count >= CategoryCacheMaxEntries) _categoryCache.Clear();
            _categoryCache[cacheKey] = (DateTime.UtcNow.AddSeconds(CategoryCacheTtlSeconds), results);

            return results;
        }

        private readonly Dictionary<string, (DateTime Expiry, List<Result> Results)> _categoryCache = new();
        private const int CategoryCacheTtlSeconds = 300;
        private const int CategoryCacheMaxEntries = 200;

        private static string BuildCategoryCacheKey(string key, string value, GeoRect b, List<string> subFilters, string? nameFilter)
        {
            string sf = string.Join("|", subFilters.OrderBy(s => s, StringComparer.Ordinal));
            // Arrotonda il bbox (~100 m) così piccoli pan/richieste quasi
            // identiche ricadono sulla stessa voce di cache
            string bbox = $"{Math.Round(b.MinLat, 3)},{Math.Round(b.MinLon, 3)},{Math.Round(b.MaxLat, 3)},{Math.Round(b.MaxLon, 3)}";
            return $"{key}={value}|{sf}|{nameFilter ?? ""}|{bbox}";
        }

        private static string EscapeOverpassValue(string s) => s.Replace("\"", "").Replace("\\", "");

        // Come sopra ma per un valore che finirà dentro un pattern regex
        // Overpass ("~"): oltre alle virgolette, va neutralizzato ogni
        // metacarattere regex (il testo viene da un nome di luogo scritto
        // dall'utente/estratto dall'LLM, non da un pattern scritto apposta)
        private static string EscapeOverpassRegex(string s) =>
            System.Text.RegularExpressions.Regex.Escape(s.Replace("\"", "").Replace("\\", ""));

        // ---------------------------------------------------------------
        // Ricerca in linguaggio naturale (Groq + OpenStreetMap), a 3 fasi:
        //   A) GenerateHypothesesAsync — l'LLM propone luoghi reali usando la
        //      propria conoscenza del mondo (NON una tassonomia di tag chiusa
        //      come il vecchio design: qui può proporre un luogo per nome,
        //      es. "Prato Centrale", non solo una categoria generica)
        //   B) VerifyHypothesisAsync — ogni ipotesi va riscontrata su OSM
        //      prima di essere mostrata, per non mostrare allucinazioni
        //      dell'LLM come se fossero luoghi reali
        //   C) FilterAndRank — filtro geografico + ordinamento, puramente
        //      locale (nessuna chiamata di rete, nessun LLM: solo haversine
        //      già usata altrove in questa classe)
        // ---------------------------------------------------------------
        private readonly GroqClient _groq = new();

        // Fase A: proposta grezza dell'LLM, NON ancora riscontrata su OSM —
        // Lat/Lon sono una stima del modello, possono essere imprecise o
        // (raramente, la regola nel prompt lo vieta esplicitamente) inventate:
        // per questo la Fase B la verifica prima di fidarsene.
        // Confidence: "high"/"low", auto-dichiarata dal modello, non usata per
        // filtrare (la verifica su OSM in Fase B è il controllo che conta) ma
        // utile nei log per capire quanto l'LLM stesso si fidava della proposta.
        public record PlaceHypothesis(string Name, double Lat, double Lon, string? Address, string Confidence);

        // Esito della Fase A completa: le ipotesi più gli eventuali nomi di
        // luogo (città/zona) menzionati esplicitamente nella richiesta, che la
        // Fase C userà con priorità sul bbox della mappa visibile
        public record HypothesesResult(List<string> LocationHints, List<PlaceHypothesis> Hypotheses);

        // Esito della Fase B per una singola ipotesi. Se Verified, Lat/Lon
        // sono quelle (più precise) di OpenStreetMap, non la stima dell'LLM.
        // SourceTags: tag OSM grezzi dell'elemento trovato (solo dal fallback
        // Overpass, che li legge comunque; il reverse geocoding Nominatim non
        // li espone in forma di dizionario tag→valore) — utili per debug e
        // per un'eventuale Fase C più ricca in futuro.
        public record VerifiedPlace(string Name, double Lat, double Lon, string? Address, bool Verified, Dictionary<string, string>? SourceTags);

        public record RankedVerifiedPlace(VerifiedPlace Place, double DistanceMeters);

        private const double FuzzyMatchThreshold        = 0.75;
        private const double FallbackSearchRadiusMeters  = 750;

        // Fase A: chiede all'LLM di proporre luoghi reali plausibili. Usa
        // CompoundModel (web search integrata, la stessa della Fase D) invece
        // del modello "puro" (solo conoscenza parametrica): per richieste su
        // luoghi meno centrali nel training del modello (es. stazioni di una
        // città non anglofona) la sola memoria del modello base si è rivelata
        // troppo debole e restituiva zero ipotesi — una ricerca web vera e
        // propria, fatta PRIMA di proporre le ipotesi anziché solo dopo in
        // Fase D per verificarle, dà risultati sensibilmente migliori. Come
        // la Fase D, quindi enableJsonMode:false + estrazione JSON tollerante
        // (ExtractJsonObject) perché CompoundModel non garantisce json_object.
        public async Task<HypothesesResult> GenerateHypothesesAsync(string apiKey, string query, CancellationToken ct = default)
        {
            string systemPrompt =
                "Sei un assistente che propone luoghi reali in risposta a una richiesta in linguaggio naturale " +
                "(italiano o inglese). USA LA RICERCA WEB per trovare luoghi reali pertinenti — non affidarti solo " +
                "alla tua conoscenza pregressa, che può essere debole o incompleta per luoghi fuori dalle aree/lingue " +
                "più comuni nei tuoi dati di addestramento (es. stazioni ferroviarie di una città non anglofona): " +
                "cerca attivamente prima di rispondere. Per ciascun luogo pertinente trovato, indica nome, coordinate " +
                "geografiche (lat/lon), un indirizzo se disponibile, e un livello di confidenza \"high\" (una fonte " +
                "della ricerca conferma nome e posizione) o \"low\" (plausibile ma senza conferma diretta dalla " +
                "ricerca). " +
                "REGOLA: proponi un'ipotesi (anche a bassa confidenza) ogni volta che hai una base ragionevole per " +
                "farlo — NON limitarti a quello che conosci con certezza assoluta: ogni ipotesi verrà comunque " +
                "verificata contro dati reali in un passo successivo, e se non verificata resterà comunque visibile " +
                "all'utente marcata come tale, quindi proporre un candidato plausibile è sempre meglio che ometterlo. " +
                "Ometti SOLO se la ricerca non restituisce alcuna base concreta (non inventare nomi o coordinate di " +
                "sana pianta). Per richieste su una categoria in una città/zona specifica (es. \"stazioni ferroviarie " +
                "a Pechino\"), elenca TUTTI i luoghi rilevanti di quella categoria che trovi in quella zona, non solo " +
                "il più famoso. " +
                "Rispondi SOLO con un oggetto JSON con esattamente questi campi: " +
                "{\"location_hints\": [string], \"hypotheses\": [{\"name\": string, \"lat\": number, \"lon\": number, " +
                "\"address\": string|null, \"confidence\": \"high\"|\"low\"}, ...]}. " +
                "\"location_hints\" è l'elenco dei nomi di città/zone menzionati ESPLICITAMENTE nella richiesta come " +
                "area di ricerca (es. \"stazione centrale a Pechino\" → [\"Pechino\"]), lista vuota se non ne nomina " +
                "nessuno (in quel caso si cerca nell'area già visualizzata sulla mappa, non inventare un luogo).";

            string raw = await _groq.ChatJsonAsync(apiKey, systemPrompt, query, ct,
                model: GroqClient.CompoundModel, enableJsonMode: false);

            JObject obj;
            try { obj = ExtractJsonObject(raw); }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PoiSearchA] risposta non in formato JSON valido: {ex.Message}\n{raw}");
                throw new GroqException($"Risposta di generazione ipotesi non in formato JSON valido: {ex.Message}", ex);
            }

            var locationHints = (obj["location_hints"] as JArray)?
                .Select(t => (t.Value<string>() ?? "").Trim())
                .Where(s => s.Length > 0)
                .ToList() ?? new List<string>();

            var hypotheses = new List<PlaceHypothesis>();
            foreach (var h in (obj["hypotheses"] as JArray) ?? new JArray())
            {
                string? name = h["name"]?.Value<string>();
                double? lat  = TryGetDouble(h["lat"]);
                double? lon  = TryGetDouble(h["lon"]);
                if (string.IsNullOrWhiteSpace(name) || lat == null || lon == null)
                {
                    Debug.WriteLine($"[PoiSearchA] ipotesi scartata (dati incompleti): {h}");
                    continue;
                }
                string confidence = h["confidence"]?.Value<string>() ?? "low";
                hypotheses.Add(new PlaceHypothesis(name!, lat.Value, lon.Value, h["address"]?.Value<string>(), confidence));
            }

            return new HypothesesResult(locationHints, hypotheses);
        }

        private static double? TryGetDouble(JToken? t) =>
            t != null && (t.Type == JTokenType.Float || t.Type == JTokenType.Integer) ? t.Value<double>() : (double?)null;

        // Fase B: verifica una singola ipotesi su OpenStreetMap.
        //   1) reverse geocoding Nominatim sulle coordinate stimate dall'LLM
        //   2) confronto fuzzy (Levenshtein normalizzato) tra il nome
        //      proposto e quello restituito da OSM; sopra soglia → verificato,
        //      con le coordinate (più precise) di OSM al posto di quelle stimate
        //   3) altrimenti, fallback: ricerca Overpass per nome entro un raggio
        //      ristretto attorno al punto stimato (non sull'intero bbox mappa)
        //   4) altrimenti, non verificato — ma NON scartato: torna comunque
        //      con Verified=false, così l'utente può ancora vederlo (con uno
        //      stile diverso sulla mappa) e decidere se fidarsene
        public async Task<VerifiedPlace> VerifyHypothesisAsync(PlaceHypothesis hypothesis, CancellationToken ct = default)
        {
            Result? reverse = null;
            try
            {
                reverse = await ReverseAsync(hypothesis.Lon, hypothesis.Lat, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PoiSearchB] reverse geocoding fallito per \"{hypothesis.Name}\": {ex.Message}");
            }

            if (reverse != null)
            {
                string osmName = FirstNamePart(reverse.DisplayName);
                double sim     = NameSimilarity(hypothesis.Name, osmName);
                if (sim >= FuzzyMatchThreshold)
                {
                    Debug.WriteLine($"[PoiSearchB] \"{hypothesis.Name}\" verificato via reverse geocoding (similarità {sim:F2}, OSM: \"{osmName}\")");
                    return new VerifiedPlace(reverse.DisplayName, reverse.Lat, reverse.Lon, reverse.Address, true, null);
                }
                Debug.WriteLine($"[PoiSearchB] \"{hypothesis.Name}\" non confermato via reverse geocoding (similarità {sim:F2} < {FuzzyMatchThreshold}, OSM più vicino: \"{osmName}\"): provo il fallback Overpass");
            }
            else
            {
                Debug.WriteLine($"[PoiSearchB] \"{hypothesis.Name}\": Nominatim non trova nulla su quel punto, provo il fallback Overpass");
            }

            try
            {
                var found = await SearchByNameNearAsync(hypothesis.Name, hypothesis.Lon, hypothesis.Lat, FallbackSearchRadiusMeters, ct).ConfigureAwait(false);
                if (found != null)
                {
                    Debug.WriteLine($"[PoiSearchB] \"{hypothesis.Name}\" verificato via Overpass di fallback (raggio {FallbackSearchRadiusMeters:F0} m)");
                    return found;
                }
                Debug.WriteLine($"[PoiSearchB] \"{hypothesis.Name}\": nessun elemento con nome simile entro {FallbackSearchRadiusMeters:F0} m su Overpass");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PoiSearchB] fallback Overpass fallito per \"{hypothesis.Name}\": {ex.Message}");
            }

            Debug.WriteLine($"[PoiSearchB] \"{hypothesis.Name}\" NON verificato (nessun riscontro su OpenStreetMap)");
            return new VerifiedPlace(hypothesis.Name, hypothesis.Lat, hypothesis.Lon, hypothesis.Address, false, null);
        }

        private static string FirstNamePart(string displayName) => (displayName ?? "").Split(',')[0].Trim();

        // Fallback della Fase B: cerca su Overpass elementi con nome simile
        // (regex case-insensitive sul tag "name") entro un raggio ristretto
        // attorno al punto stimato dall'LLM — "around:raggio,lat,lon", non un
        // bbox: la ricerca resta mirata anche se il punto stimato dall'LLM è
        // solo approssimativamente giusto. Tra più corrispondenze nel raggio,
        // ritorna la più vicina al punto stimato.
        public async Task<VerifiedPlace?> SearchByNameNearAsync(string name, double lon, double lat, double radiusMeters, CancellationToken ct = default)
        {
            string ql =
                "[out:json][timeout:15];" +
                "(" +
                $"node[\"name\"~\"{EscapeOverpassRegex(name)}\",i](around:{Fmt(radiusMeters)},{Fmt(lat)},{Fmt(lon)});" +
                $"way[\"name\"~\"{EscapeOverpassRegex(name)}\",i](around:{Fmt(radiusMeters)},{Fmt(lat)},{Fmt(lon)});" +
                ");" +
                "out center 5;";

            using var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("data", ql) });
            using var resp = await Http.PostAsync("https://overpass-api.de/api/interpreter", content, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            var root     = JObject.Parse(json);
            var elements = (root["elements"] as JArray) ?? new JArray();
            if (elements.Count == 0) return null;

            JToken? best     = null;
            double  bestDist = double.MaxValue;
            foreach (var el in elements)
            {
                double? elat = el["lat"]?.Value<double?>() ?? el["center"]?["lat"]?.Value<double?>();
                double? elon = el["lon"]?.Value<double?>() ?? el["center"]?["lon"]?.Value<double?>();
                if (elat == null || elon == null) continue;
                double d = GeoUtils.DistanceKm(lon, lat, elon.Value, elat.Value);
                if (d < bestDist) { bestDist = d; best = el; }
            }
            if (best == null) return null;

            double bLat = best["lat"]?.Value<double?>() ?? best["center"]!["lat"]!.Value<double>();
            double bLon = best["lon"]?.Value<double?>() ?? best["center"]!["lon"]!.Value<double>();
            var tags     = best["tags"] as JObject;
            string bestName = tags?["name"]?.Value<string>() ?? name;
            var sourceTags  = tags?.Properties().ToDictionary(p => p.Name, p => p.Value.Value<string>() ?? "")
                ?? new Dictionary<string, string>();

            return new VerifiedPlace(bestName, bLat, bLon, null, true, sourceTags);
        }

        // Similarità normalizzata (0..1) tra due stringhe, su distanza di
        // Levenshtein: 1 = identiche, 0 = nessuna somiglianza. Confronto
        // case-insensitive e su soli lettere/cifre (Nominatim e l'LLM possono
        // variare leggermente la forma del nome pur indicando lo stesso luogo,
        // es. "Prato Centrale" vs "Stazione di Prato Centrale, Prato").
        // → Implementata a mano invece di una libreria tipo FuzzySharp: è un
        // algoritmo di poche righe, coerente con la scelta già fatta altrove
        // in questo progetto (vedi KmlIo) di non aggiungere pacchetti quando
        // il codice necessario è minimo.
        private static double NameSimilarity(string a, string b)
        {
            string na = NormalizeForCompare(a);
            string nb = NormalizeForCompare(b);
            if (na.Length == 0 || nb.Length == 0) return 0;
            int dist   = LevenshteinDistance(na, nb);
            int maxLen = Math.Max(na.Length, nb.Length);
            return 1.0 - (double)dist / maxLen;
        }

        private static string NormalizeForCompare(string s) =>
            new string((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        private static int LevenshteinDistance(string a, string b)
        {
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                }
            return d[a.Length, b.Length];
        }

        // Fase C: filtro geografico (il bbox passato da chi chiama ha già la
        // priorità corretta: quello geocodificato dai LocationHints se
        // presenti, altrimenti il bbox della mappa visibile — decisione presa
        // dal chiamante, che è l'unico a conoscere sia i LocationHints sia la
        // vista corrente) + ordinamento verificati-prima poi per distanza.
        // Nessuna chiamata di rete né LLM: solo haversine (GeoUtils.DistanceKm).
        public List<RankedVerifiedPlace> FilterAndRank(List<VerifiedPlace> places, GeoRect bounds, GeoPoint userLocation) =>
            RankByDistance(
                places.Where(p => p.Lon >= bounds.MinLon && p.Lon <= bounds.MaxLon && p.Lat >= bounds.MinLat && p.Lat <= bounds.MaxLat),
                userLocation);

        // Come FilterAndRank ma SENZA il filtro geografico: usato come
        // ripiego quando il filtro toglierebbe TUTTO (es. le coordinate
        // stimate dall'LLM cadono fuori dall'area attesa) — mostrare comunque
        // i luoghi trovati, per quanto fuori area, è più utile di zero
        // risultati; il chiamante marca chiaramente il messaggio di stato in
        // questo caso, così l'utente sa che l'area potrebbe non essere esatta.
        public List<RankedVerifiedPlace> RankByDistance(IEnumerable<VerifiedPlace> places, GeoPoint userLocation) =>
            places
                .Select(p => new RankedVerifiedPlace(p, GeoUtils.DistanceKm(userLocation.Lon, userLocation.Lat, p.Lon, p.Lat) * 1000.0))
                .OrderByDescending(rp => rp.Place.Verified)
                .ThenBy(rp => rp.DistanceMeters)
                .ToList();

        // ---------------------------------------------------------------
        // API di comodo (nessun uso ancora nella UI, aggiunte su richiesta
        // come primitive riutilizzabili): stessa infrastruttura di sopra
        // (Nominatim/Overpass/GeoUtils), solo con firme più semplici/dirette
        // per un chiamante che vuole "un punto", "cosa c'è vicino" o "quanto
        // dista", senza passare da bbox/viste mappa.
        // ---------------------------------------------------------------

        // Risultato di SearchNearbyAsync/SearchByPlaceAndCategoryAsync: più
        // semplice del Result "storico" sopra (niente Category/Type grezzi
        // separati, niente Description/Verified) — qui Category è già la
        // categoria richiesta dal chiamante, non un tag OSM da interpretare.
        public record PoiResult(string Name, double Lat, double Lon, string Category, string? Address);

        // 1. Coordinate del centro di un luogo dato per nome (città, indirizzo,
        // punto di interesse...). Lancia se il luogo non viene trovato: a
        // differenza di GeocodePlaceAsync (che ritorna null, pensato per un
        // chiamante che vuole gestire il "non trovato" come caso normale),
        // qui la firma richiesta non è nullable, quindi il "non trovato" è
        // un'eccezione.
        public async Task<(double Lat, double Lon)> GetCoordinatesAsync(string place, CancellationToken ct = default)
        {
            var rect = await GeocodePlaceAsync(place, ct).ConfigureAwait(false);
            if (rect == null)
                throw new InvalidOperationException($"Luogo \"{place}\" non trovato.");
            return (rect.CenterLat, rect.CenterLon);
        }

        // 2. POI di una categoria nota (stesse parole chiave/tag di
        // TryMatchCategory/Categories, non un tag OSM grezzo) entro un raggio
        // (km) da un punto — Overpass "around", non un bbox: a differenza di
        // SearchCategoryAsync (pensato per l'area visualizzata sulla mappa)
        // qui il chiamante dà solo un centro e un raggio.
        public async Task<List<PoiResult>> SearchNearbyAsync(double lat, double lon, string category, double radiusKm = 5, CancellationToken ct = default)
        {
            if (!TryMatchCategory(category, out string key, out string value, out _))
                throw new InvalidOperationException($"Categoria \"{category}\" non riconosciuta.");

            double radiusMeters = radiusKm * 1000.0;
            string ql =
                "[out:json][timeout:15];" +
                "(" +
                $"node[\"{key}\"=\"{value}\"](around:{Fmt(radiusMeters)},{Fmt(lat)},{Fmt(lon)});" +
                $"way[\"{key}\"=\"{value}\"](around:{Fmt(radiusMeters)},{Fmt(lat)},{Fmt(lon)});" +
                ");" +
                "out center 100;";

            using var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("data", ql) });
            using var resp = await Http.PostAsync("https://overpass-api.de/api/interpreter", content, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            var root = JObject.Parse(json);
            var results = new List<PoiResult>();
            foreach (var el in (root["elements"] as JArray) ?? new JArray())
            {
                double? elat = el["lat"]?.Value<double?>() ?? el["center"]?["lat"]?.Value<double?>();
                double? elon = el["lon"]?.Value<double?>() ?? el["center"]?["lon"]?.Value<double?>();
                if (elat == null || elon == null) continue;

                var tags = el["tags"] as JObject;
                string? name = tags?["name:it"]?.Value<string>()
                    ?? tags?["name:en"]?.Value<string>()
                    ?? tags?["name"]?.Value<string>();

                results.Add(new PoiResult(
                    string.IsNullOrWhiteSpace(name) ? $"{value} (senza nome)" : name!,
                    elat.Value, elon.Value, category, null));
            }
            return results;
        }

        // 3. Distanza (km, haversine) tra due coordinate — wrapper async su
        // GeoUtils.DistanceKm (sincrona: nessuna rete coinvolta) solo per
        // uniformità di firma con gli altri metodi di questa sezione.
        public Task<double> GetDistanceAsync(double lat1, double lon1, double lat2, double lon2) =>
            Task.FromResult(GeoUtils.DistanceKm(lon1, lat1, lon2, lat2));

        // 4. Combina 1+2: geocodifica il luogo, poi cerca la categoria entro
        // il raggio dal punto trovato.
        public async Task<List<PoiResult>> SearchByPlaceAndCategoryAsync(string place, string category, double radiusKm = 5, CancellationToken ct = default)
        {
            var (lat, lon) = await GetCoordinatesAsync(place, ct).ConfigureAwait(false);
            return await SearchNearbyAsync(lat, lon, category, radiusKm, ct).ConfigureAwait(false);
        }

        // ---------------------------------------------------------------
        // Fase D: ultima verifica, SOLO per le ipotesi che la Fase B non è
        // riuscita a confermare su OpenStreetMap. Usa un modello Groq
        // "compound" (GroqClient.CompoundModel): stessa API/chiave già in
        // uso per le Fasi A, ma con web search integrata lato Groq nella
        // stessa chiamata — nessun servizio di ricerca esterno da configurare
        // a parte. Se la fonte trovata riporta solo un indirizzo testuale
        // (non coordinate dirette), lo si geocodifica via Nominatim (forward
        // geocoding, GeocodePlaceAsync) per ottenere lat/lon verificate. Se
        // anche questo fallisce, il luogo resta Verified=false: la Fase D è
        // un ULTIMO tentativo, non sostituisce la Fase B, e come la Fase B
        // non scarta mai un'ipotesi non confermata, la marca solo come tale.
        // ---------------------------------------------------------------

        // locationHint: città/zona nota dalla richiesta originale (Fase A →
        // LocationHints), se presente — aiuta la ricerca web a non confondere
        // luoghi omonimi in città diverse (es. molte città hanno una via/piazza
        // con lo stesso nome)
        public async Task<VerifiedPlace> WebSearchVerifyAsync(string apiKey, PlaceHypothesis hypothesis, string? locationHint, CancellationToken ct = default)
        {
            string systemPrompt =
                "Cerca sul web informazioni su questo luogo per determinare se esiste realmente. Usa la ricerca web " +
                "per trovare pagine ufficiali, TripAdvisor, Google Maps o altre fonti affidabili — non fidarti solo " +
                "della tua conoscenza pregressa, verifica con una ricerca vera. " +
                "Rispondi SOLO con un oggetto JSON con esattamente questi campi: {\"found\": true|false, " +
                "\"name\": string|null, \"address\": string|null, \"lat\": number|null, \"lon\": number|null}. " +
                "\"found\"=true SOLO se una fonte concreta trovata con la ricerca conferma che il luogo esiste " +
                "davvero con quel nome, in quella zona. \"address\" è l'indirizzo testuale trovato, se presente. " +
                "\"lat\"/\"lon\" SOLO se una fonte riporta coordinate dirette (es. dati strutturati della pagina o " +
                "un link Google Maps con coordinate); se hai solo un indirizzo testuale, lasciali null — verranno " +
                "geocodificati separatamente. Se non trovi alcun riscontro concreto, \"found\": false e tutti gli " +
                "altri campi null: NON inventare un indirizzo o delle coordinate plausibili.";

            string userPrompt =
                $"Luogo da verificare: \"{hypothesis.Name}\"" +
                (locationHint != null ? $", presumibilmente a/in \"{locationHint}\"" : "") +
                (!string.IsNullOrWhiteSpace(hypothesis.Address) ? $". Indirizzo stimato in precedenza: {hypothesis.Address}" : "") +
                ".";

            string raw;
            try
            {
                raw = await _groq.ChatJsonAsync(apiKey, systemPrompt, userPrompt, ct,
                    model: GroqClient.CompoundModel, enableJsonMode: false).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PoiSearchD] ricerca web fallita per \"{hypothesis.Name}\": {ex.Message}");
                return NotVerified(hypothesis);
            }

            JObject obj;
            try { obj = ExtractJsonObject(raw); }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PoiSearchD] risposta ricerca web non in formato JSON valido per \"{hypothesis.Name}\": {ex.Message}\n{raw}");
                return NotVerified(hypothesis);
            }

            bool found = obj["found"]?.Value<bool?>() ?? false;
            if (!found)
            {
                Debug.WriteLine($"[PoiSearchD] \"{hypothesis.Name}\": nessun riscontro concreto trovato sul web");
                return NotVerified(hypothesis);
            }

            string  name    = obj["name"]?.Value<string>() ?? hypothesis.Name;
            string? address = obj["address"]?.Value<string>();
            double? lat     = TryGetDouble(obj["lat"]);
            double? lon     = TryGetDouble(obj["lon"]);

            if (lat != null && lon != null)
            {
                Debug.WriteLine($"[PoiSearchD] \"{hypothesis.Name}\" verificato via ricerca web (coordinate dirette dalla fonte)");
                return new VerifiedPlace(name, lat.Value, lon.Value, address, true, null);
            }

            if (!string.IsNullOrWhiteSpace(address))
            {
                GeoRect? rect = null;
                try { rect = await GeocodePlaceAsync(address!, ct).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[PoiSearchD] geocodifica dell'indirizzo \"{address}\" fallita per \"{hypothesis.Name}\": {ex.Message}");
                }

                if (rect != null)
                {
                    Debug.WriteLine($"[PoiSearchD] \"{hypothesis.Name}\" verificato via ricerca web + geocodifica indirizzo (\"{address}\")");
                    return new VerifiedPlace(name, rect.CenterLat, rect.CenterLon, address, true, null);
                }
                Debug.WriteLine($"[PoiSearchD] \"{hypothesis.Name}\": indirizzo trovato (\"{address}\") ma nessun riscontro Nominatim per geocodificarlo");
            }

            Debug.WriteLine($"[PoiSearchD] \"{hypothesis.Name}\" NON verificato (ricerca web senza indirizzo/coordinate utilizzabili)");
            return NotVerified(hypothesis);
        }

        private static VerifiedPlace NotVerified(PlaceHypothesis h) =>
            new VerifiedPlace(h.Name, h.Lat, h.Lon, h.Address, false, null);

        // Estrae il primo oggetto JSON presente nel testo: i modelli
        // "compound" (Fase D) non garantiscono response_format=json_object,
        // quindi la risposta può includere testo di contorno attorno al JSON
        // vero e proprio (o raramente essere già JSON puro, provato per primo)
        private static JObject ExtractJsonObject(string text)
        {
            try { return JObject.Parse(text); }
            catch { /* non è JSON puro: prova a estrarlo dal testo circostante */ }

            int start = text.IndexOf('{');
            int end   = text.LastIndexOf('}');
            if (start < 0 || end <= start)
                throw new FormatException("Nessun oggetto JSON individuabile nella risposta.");
            return JObject.Parse(text.Substring(start, end - start + 1));
        }
    }
}
