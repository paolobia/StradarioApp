// =============================================================================
// Services/PoiSearchService.cs
//
// SINOSSI: Ricerca di POI per categoria (Overpass API) nell'area attualmente
//   visualizzata sulla mappa. La categoria si sceglie SEMPRE dal combo della
//   toolbar (mai da parola chiave riconosciuta nel testo libero: nessuna
//   ambiguità) — vedi MainWindow.OnPoiSearchAsync/RunCategorySearchAsync.
//   Il testo digitato accanto al combo è un raffinamento all'interno della
//   categoria scelta: di norma un filtro letterale sul nome (regex su "name"
//   via SearchCategoryAsync), oppure — quando quel filtro letterale non
//   trova nulla ed è configurata una chiave Groq — un vincolo più ampio che
//   un'AI seleziona dentro l'elenco chiuso di candidati reali già trovati
//   nell'area (FilterCandidatesByQueryAsync, es. "della linea firenze
//   bologna" su categoria "stazioni ferroviarie": nessuna stazione si chiama
//   così, ma l'AI riconosce quali stazioni dell'elenco appartengono a quella
//   linea). L'AI non propone MAI luoghi di sua iniziativa in questo file:
//   sceglie solo dentro dati OSM reali già recuperati.
//   Un solo HttpClient/richiesta per ricerca interattiva avviata dall'utente
//   (Overpass/Nominatim): rispetta la policy d'uso dei server pubblici
//   (User-Agent identificativo — stesso header usato dal resto dell'app per
//   il fetch dei tile, vedi MapRenderer/PdfGenerator; throttling ~1
//   richiesta/secondo verso Nominatim, usata qui solo per geocodifica di
//   luoghi/indirizzi e ricerca inversa, non più per ricerca testuale diretta
//   di POI).
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

        // Limite di SICUREZZA (non un limite "normale") passato a Overpass
        // ("out center N"): per categorie molto dense (es. "caffetterie" in
        // una grande città) l'utente vuole vederle TUTTE, non un sottoinsieme
        // arbitrario troncato a poche centinaia — quindi questo valore è
        // volutamente alto, pensato solo per evitare risposte patologiche
        // (centinaia di migliaia di elementi) che bloccherebbero client e
        // server, non per limitare l'uso normale. Pubblico così il chiamante
        // può confrontarlo con Result.Count e avvisare nel raro caso in cui
        // anche questo tetto molto alto venga raggiunto.
        public const int CategoryResultCap = 5000;

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
        public record Result(string DisplayName, double Lon, double Lat, string? Category, string? Type, string? Address);

        // Geocodifica un nome di luogo (città/paese/regione, es. "Pechino" o
        // "Prato") nel suo riquadro geografico su Nominatim, SENZA restringere
        // a un'area: usata da MainWindow.OnPoiSearchAsync quando il testo
        // digitato accanto alla categoria contiene una preposizione di luogo
        // ("stazione centrale a Pechino" → cerca a Pechino, non nell'area
        // visualizzata) e da GetCoordinatesAsync/SearchByPlaceAndCategoryAsync
        // più sotto. Ritorna null se il luogo non viene trovato.
        public async Task<GeoRect?> GeocodePlaceAsync(string placeName, Action<string>? onProgress = null, CancellationToken ct = default)
        {
            onProgress?.Invoke($"Attendo il turno per interrogare Nominatim (max 1 richiesta/secondo)...");
            await ThrottleNominatimAsync(ct);

            string url =
                "https://nominatim.openstreetmap.org/search" +
                $"?q={Uri.EscapeDataString(placeName)}" +
                "&format=jsonv2&limit=1&accept-language=it,en";

            onProgress?.Invoke($"Interrogo Nominatim per il luogo \"{placeName}\"...");
            using var resp = await Http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();
            string json = await resp.Content.ReadAsStringAsync(ct);

            var arr = JArray.Parse(json);
            if (arr.Count == 0)
            {
                onProgress?.Invoke($"Nominatim non ha trovato nessun luogo per \"{placeName}\".");
                return null;
            }

            var bbox = arr[0]["boundingbox"] as JArray;
            if (bbox == null || bbox.Count < 4) return null;

            // Ordine Nominatim: [min_lat, max_lat, min_lon, max_lon] (stringhe)
            double minLat = double.Parse(bbox[0].Value<string>()!, CultureInfo.InvariantCulture);
            double maxLat = double.Parse(bbox[1].Value<string>()!, CultureInfo.InvariantCulture);
            double minLon = double.Parse(bbox[2].Value<string>()!, CultureInfo.InvariantCulture);
            double maxLon = double.Parse(bbox[3].Value<string>()!, CultureInfo.InvariantCulture);

            return new GeoRect { MinLon = minLon, MinLat = minLat, MaxLon = maxLon, MaxLat = maxLat };
        }

        // Ricerca libera di un indirizzo/via+città su Nominatim (voce "Ricerca
        // un indirizzo" del combo categoria, MainWindow.RunAddressSearchAsync):
        // a differenza di GeocodePlaceAsync (un solo luogo, ritorna il suo
        // bounding box) qui il testo è un indirizzo puntuale e il chiamante
        // vuole vedere TUTTI i candidati plausibili come marker sulla mappa
        // (stessa UX della ricerca per categoria), non un solo bbox su cui
        // ricentrare. `viewBounds`, se dato, passa a Nominatim come suggerimento
        // di area (parametro "viewbox", non "bounded": un indirizzo scritto per
        // esteso con la propria città può stare benissimo fuori dalla vista
        // attuale, non va scartato).
        public async Task<List<Result>> SearchAddressAsync(string query, GeoRect? viewBounds = null, int limit = 15,
            Action<string>? onProgress = null, CancellationToken ct = default)
        {
            onProgress?.Invoke("Attendo il turno per interrogare Nominatim (max 1 richiesta/secondo)...");
            await ThrottleNominatimAsync(ct);

            string url =
                "https://nominatim.openstreetmap.org/search" +
                $"?q={Uri.EscapeDataString(query)}" +
                $"&format=jsonv2&limit={limit}&addressdetails=1&accept-language=it,en";
            if (viewBounds != null)
                url += $"&viewbox={Fmt(viewBounds.MinLon)},{Fmt(viewBounds.MaxLat)},{Fmt(viewBounds.MaxLon)},{Fmt(viewBounds.MinLat)}";

            onProgress?.Invoke($"Interrogo Nominatim per l'indirizzo \"{query}\"...");
            using var resp = await Http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();
            string json = await resp.Content.ReadAsStringAsync(ct);

            var arr = JArray.Parse(json);
            var results = new List<Result>();
            foreach (var item in arr)
            {
                var r = ParseResult(item);
                if (r != null) results.Add(r);
            }

            onProgress?.Invoke($"Trovati {results.Count} indirizzi corrispondenti.");
            return results;
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
            (new[] { "distributore automatico", "distributori automatici", "vending machine" }, "amenity", "vending_machine", "distributori automatici"),
            (new[] { "edicola", "edicole", "newsagent" },                         "shop",    "newsagent",     "edicole"),
            (new[] { "fast food", "fastfood" },                                   "amenity", "fast_food",     "fast food"),
            (new[] { "tabaccheria", "tabaccherie", "tabacchi", "tobacco" },        "shop",    "tobacco",       "tabaccherie"),
            (new[] { "parrucchiere", "parrucchieri", "hairdresser" },              "shop",    "hairdresser",   "parrucchieri"),
            (new[] { "veterinario", "veterinari", "veterinary" },                  "amenity", "veterinary",    "veterinari"),
            (new[] { "colonnina elettrica", "colonnine elettriche", "ricarica elettrica", "charging station" }, "amenity", "charging_station", "colonnine ricarica elettrica"),
            (new[] { "fontanella", "fontanelle", "acqua potabile", "drinking water" }, "amenity", "drinking_water", "fontanelle (acqua potabile)"),
            (new[] { "lavanderia", "lavanderie", "laundry" },                     "shop",    "laundry",       "lavanderie"),

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

        // Le due voci "speciali" in testa al combo (vedi BuildToolbar in
        // MainWindow): non sono tag OSM da interrogare su Overpass come le
        // altre, ma modalità di ricerca diverse (geocoding indirizzo via
        // Nominatim, nome città anche parziale da CityDatabase/cities500.csv)
        // — riconosciute da Key == SentinelCategoryKey, con Value che
        // distingue le due (AddressSearchValue/CitySearchValue). Prependerle
        // qui invece che in una lista separata mantiene l'indice del combo
        // allineato 1:1 con AllCategories, così GetSelectedCategoryFilter
        // (MainWindow) resta invariato.
        public const string SentinelCategoryKey  = "__special__";
        public const string AddressSearchValue    = "address";
        public const string CitySearchValue       = "city";

        // Categorie personalizzate aggiunte dall'utente dalle Impostazioni
        // (tab "Categorie POI"), persistite in AppPreferencesService — a
        // differenza di Categories (fisse, compilate nel codice), queste
        // possono cambiare a runtime: MainWindow le carica all'avvio
        // (SetCustomCategories) e le aggiorna ogni volta che le Impostazioni
        // vengono confermate. La parola chiave di riconoscimento testuale
        // (TryMatchCategory) è semplicemente l'etichetta stessa: l'utente non
        // compila un elenco di sinonimi separato, solo etichetta+tag OSM.
        private static List<(string[] Keywords, string Key, string Value, string Label)> _customCategories = new();

        public static void SetCustomCategories(IEnumerable<(string Key, string Value, string Label)> customs)
        {
            _customCategories = (customs ?? Enumerable.Empty<(string, string, string)>())
                .Where(c => !string.IsNullOrWhiteSpace(c.Key) && !string.IsNullOrWhiteSpace(c.Value) && !string.IsNullOrWhiteSpace(c.Label))
                .Select(c => (new[] { c.Label.Trim().ToLowerInvariant() }, c.Key.Trim(), c.Value.Trim(), c.Label.Trim()))
                .ToList();
        }

        public static IReadOnlyList<(string Key, string Value, string Label)> CustomCategories =>
            _customCategories.Select(c => (c.Key, c.Value, c.Label)).ToList();

        // Vista pubblica di sola lettura di Categories, per il selettore a
        // combobox nella toolbar (MainWindow): solo Key/Value/Label, non le
        // parole chiave (quelle servono solo al riconoscimento testuale di
        // TryMatchCategory, non alla UI). Le categorie personalizzate vanno
        // sempre in coda, dopo quelle predefinite.
        public static IReadOnlyList<(string Key, string Value, string Label)> AllCategories =>
            new List<(string Key, string Value, string Label)>
            {
                (SentinelCategoryKey, AddressSearchValue, "Ricerca un indirizzo"),
                (SentinelCategoryKey, CitySearchValue,     "Ricerca una città (anche parziale)"),
            }
            .Concat(Categories.Select(c => (c.Key, c.Value, c.Label)))
            .Concat(_customCategories.Select(c => (c.Key, c.Value, c.Label)))
            .ToList();

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
            foreach (var cat in Categories.Concat(_customCategories))
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
            IEnumerable<string>? subFilters = null, string? nameFilter = null, Action<string>? onProgress = null, CancellationToken ct = default)
        {
            var subList = (subFilters ?? Enumerable.Empty<string>()).ToList();

            string cacheKey = BuildCategoryCacheKey(key, value, viewBounds, subList, nameFilter);
            if (_categoryCache.TryGetValue(cacheKey, out var cached) && cached.Expiry > DateTime.UtcNow)
            {
                onProgress?.Invoke($"Trovata in cache la stessa ricerca ({key}={value}) per quest'area: nessuna nuova richiesta a Overpass.");
                return cached.Results;
            }

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

            onProgress?.Invoke($"Costruita la query Overpass per \"{key}={value}\" nell'area {Fmt(viewBounds.MinLat)},{Fmt(viewBounds.MinLon)} – {Fmt(viewBounds.MaxLat)},{Fmt(viewBounds.MaxLon)}.");
            onProgress?.Invoke("Invio la richiesta al server pubblico Overpass (overpass-api.de)...");
            using var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("data", ql) });
            using var resp = await Http.PostAsync("https://overpass-api.de/api/interpreter", content, ct);
            resp.EnsureSuccessStatusCode();
            onProgress?.Invoke("Risposta ricevuta da Overpass, analizzo gli elementi trovati...");
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

            onProgress?.Invoke($"Trovati {results.Count} elementi validi (con coordinate) su Overpass.");

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
        // Filtro AI su candidati reali (alternativa a GenerateHypothesesAsync
        // per richieste che, DOPO aver scelto una categoria dal combo (unico
        // modo di scegliere una categoria: nessuna ambiguità), aggiungono nel
        // testo un vincolo che un filtro sul nome letterale non può
        // esprimere, es. categoria "stazioni ferroviarie" + testo "della
        // linea firenze bologna" — nessuna stazione si chiama letteralmente
        // così. A differenza della Fase A, l'LLM non propone luoghi dalla
        // propria conoscenza — sceglie SOLO dentro un elenco chiuso di
        // candidati già trovati su OpenStreetMap (SearchCategoryAsync, stessa
        // query della ricerca a categoria esatta, sull'area attualmente
        // visualizzata). Per questo
        // non serve una fase di verifica successiva: ogni elemento scelto è
        // per definizione un luogo OSM reale, come nella ricerca a categoria
        // pura. Nessun web search integrato (DefaultModel, non CompoundModel):
        // qui il compito è scegliere dentro un elenco dato, non cercare
        // informazioni nuove.
        // ---------------------------------------------------------------
        public async Task<List<Result>> FilterCandidatesByQueryAsync(string apiKey, string query, List<Result> candidates, Action<string>? onProgress = null, CancellationToken ct = default)
        {
            if (candidates.Count == 0) return new List<Result>();

            var indexed = new JArray(candidates.Select((c, i) => new JObject
            {
                ["index"] = i,
                ["name"]  = c.DisplayName
            }));

            string systemPrompt =
                "Ti viene fornito un elenco CHIUSO di luoghi reali (trovati su OpenStreetMap nell'area " +
                "attualmente visualizzata sulla mappa) e una richiesta dell'utente. Il tuo compito è SOLO scegliere " +
                "quali elementi dell'elenco soddisfano la richiesta, usando la tua conoscenza geografica — NON " +
                "proporre luoghi che non sono nell'elenco, anche se li conosci: se un luogo pertinente non è tra i " +
                "candidati forniti, va semplicemente omesso, non inventato. Se la richiesta menziona una " +
                "linea/tratta/percorso (es. una linea ferroviaria tra due città), scegli i luoghi dell'elenco che vi " +
                "appartengono in base al nome/alla tua conoscenza geografica; se nessun elemento dell'elenco è " +
                "pertinente, restituisci una lista vuota. " +
                "Rispondi SOLO con un oggetto JSON con esattamente questo campo: " +
                "{\"selected_indices\": [number, ...]}, usando i valori \"index\" esattamente come forniti " +
                "nell'elenco candidati.";

            string userPrompt = $"Richiesta: \"{query}\"\n\nElenco candidati:\n{indexed.ToString(Formatting.None)}";

            onProgress?.Invoke($"Invio a Groq/AI {candidates.Count} candidati OSM da filtrare per \"{query}\"...");
            string raw = await _groq.ChatJsonAsync(apiKey, systemPrompt, userPrompt, ct,
                model: GroqClient.DefaultModel, enableJsonMode: true).ConfigureAwait(false);
            onProgress?.Invoke("Risposta AI ricevuta, analizzo la selezione...");

            JObject obj;
            try { obj = ExtractJsonObject(raw); }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PoiSearchFilter] risposta non in formato JSON valido: {ex.Message}\n{raw}");
                throw new GroqException($"Risposta di selezione candidati non in formato JSON valido: {ex.Message}", ex);
            }

            var indices = (obj["selected_indices"] as JArray)?
                .Select(t => t.Type == JTokenType.Integer ? t.Value<int?>() : null)
                .Where(i => i.HasValue && i.Value >= 0 && i.Value < candidates.Count)
                .Select(i => i!.Value)
                .Distinct()
                .ToList() ?? new List<int>();

            onProgress?.Invoke($"L'AI ha selezionato {indices.Count} luoghi pertinenti su {candidates.Count} candidati.");
            return indices.Select(i => candidates[i]).ToList();
        }

        // Client Groq condiviso: usato da FilterCandidatesByQueryAsync sopra.
        private readonly GroqClient _groq = new();

        // ---------------------------------------------------------------
        // API di comodo (nessun uso ancora nella UI, aggiunte su richiesta
        // come primitive riutilizzabili): stessa infrastruttura di sopra
        // (Nominatim/Overpass/GeoUtils), solo con firme più semplici/dirette
        // per un chiamante che vuole "un punto", "cosa c'è vicino" o "quanto
        // dista", senza passare da bbox/viste mappa.
        // ---------------------------------------------------------------

        // Risultato di SearchNearbyAsync/SearchByPlaceAndCategoryAsync: più
        // semplice del Result sopra (niente Category/Type grezzi separati) —
        // qui Category è già la categoria richiesta dal chiamante, non un tag
        // OSM da interpretare.
        public record PoiResult(string Name, double Lat, double Lon, string Category, string? Address);

        // 1. Coordinate del centro di un luogo dato per nome (città, indirizzo,
        // punto di interesse...). Lancia se il luogo non viene trovato: a
        // differenza di GeocodePlaceAsync (che ritorna null, pensato per un
        // chiamante che vuole gestire il "non trovato" come caso normale),
        // qui la firma richiesta non è nullable, quindi il "non trovato" è
        // un'eccezione.
        public async Task<(double Lat, double Lon)> GetCoordinatesAsync(string place, CancellationToken ct = default)
        {
            var rect = await GeocodePlaceAsync(place, ct: ct).ConfigureAwait(false);
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

        // Estrae il primo oggetto JSON presente nel testo (o, se già JSON
        // puro, lo restituisce direttamente, provato per primo) — usata da
        // FilterCandidatesByQueryAsync come parsing tollerante della risposta.
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
