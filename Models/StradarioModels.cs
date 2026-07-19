// =============================================================================
// Models/StradarioModels.cs
// 
// SINOSSI: Definizione di tutti i modelli dati dell'applicazione stradario.
//   - PageSize: dimensioni fisiche della pagina (A4, ecc.)
//   - MapScale: scala cartografica (1:100.000, 1:200.000)
//   - StradarioSettings: impostazioni globali (formato, DPI, scala)
//   - MapPage: singola pagina dello stradario con posizione geografica
//   - StradarioProject: struttura completa del progetto (impostazioni + pagine)
//   - GeoRect: rettangolo geografico in coordinate lat/lon
//   - PixelRect: rettangolo in coordinate pixel sullo schermo
// =============================================================================

using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace StradarioApp.Models
{
    // Formato fisico della pagina
    public enum PageSize
    {
        A5,
        A4,
        A3
    }

    // Orientamento della pagina
    public enum PageOrientation
    {
        Portrait,
        Landscape
    }

    // Scale supportate
    public enum MapScale
    {
        Scale1K,     // 1:1.000
        Scale5K,     // 1:5.000
        Scale10K,    // 1:10.000
        Scale100K,   // 1:100.000
        Scale200K    // 1:200.000
    }

    // Livello di contrasto applicato alla mappa raster in fase di export PDF
    // (mai sulla mappa interattiva a schermo). None = tile OSM originali;
    // Color = contrasto/saturazione potenziati mantenendo i colori; BlackWhite
    // = conversione in scala di grigi con curva a "S" pensata per la stampa
    // B/N, dove i tenui riempimenti pastello di OSM Carto altrimenti
    // collassano tutti sullo stesso grigio; RoadEmphasis = filtro selettivo
    // per tonalità (non solo luminanza, vedi Services/MapContrastFilter.cs):
    // schiarisce verso il bianco edifici/verde/acqua e accentua saturazione e
    // contrasto di strade principali/secondarie e testo, così la rete
    // stradale e i nomi restano leggibili a scapito degli sfondi.
    public enum PdfContrastMode
    {
        None,
        Color,
        BlackWhite,
        RoadEmphasis
    }

    // Elenco dei tile server disponibili (nome visualizzato → URL template)
    // {z} = zoom, {x} = colonna tile, {y} = riga tile. Un template che
    // contiene anche {apikey} richiede una chiave personale gratuita
    // (Thunderforest/Stadia Maps: nessun tile server anonimo raggiunge la
    // stessa leggibilità in stampa di questi, pensati apposta per basemap
    // "outdoor/print" con strade e testo in risalto — vedi
    // StradarioSettings.GetEffectiveTileServerUrl per la sostituzione).
    public static class TileServers
    {
        // Coverage = estensione geografica dei dati/tile disponibili.
        // Characteristics = tratti distintivi dello stile grafico/rendering.
        // Suggestion = quando conviene scegliere questo server rispetto agli altri.
        public record Entry(string Name, string UrlTemplate, string Coverage, string Characteristics, string Suggestion, int MaxZoom = 19)
        {
            public bool RequiresApiKey => UrlTemplate.Contains("{apikey}");
        }

        public static readonly Entry[] All = new[]
        {
            new Entry("OpenStreetMap Standard",              "https://tile.openstreetmap.org/{z}/{x}/{y}.png",
                "Mondiale (dati OSM completi)",
                "Stile \"Standard\" OSM Carto: colori vivaci, alta densità di dettaglio in area urbana, aggiornamento frequente dalla community",
                "Uso generale: miglior compromesso leggibilità/dettaglio per la maggior parte degli stradari",
                MaxZoom: 19),
            new Entry("OSM France",                          "https://a.tile.openstreetmap.fr/osmfr/{z}/{x}/{y}.png",
                "Mondiale (mirror OSM, dati globali)",
                "Rendering simile a OSM Standard ma servito da infrastruttura indipendente (tiles.openstreetmap.fr)",
                "Alternativa di backup quando il server OSM principale è lento/sovraccarico, o per zone in Francia",
                MaxZoom: 19),
            new Entry("OSM Deutschland",                     "https://tile.openstreetmap.de/{z}/{x}/{y}.png",
                "Mondiale (dati OSM globali), enfasi su Germania/Europa centrale",
                "Stile \"osm.de\": strade e confini amministrativi molto leggibili, buona resa dei toponimi in Mitteleuropa",
                "Stradari in area germanica/centroeuropea, oppure come ulteriore alternativa a OSM Standard",
                MaxZoom: 18),
            new Entry("OpenTopoMap",                         "https://tile.opentopomap.org/{z}/{x}/{y}.png",
                "Mondiale",
                "Stile topografico con curve di livello, hillshading, sentieri e rifugi evidenziati; meno enfasi su indirizzario urbano denso",
                "Stradari escursionistici/montani o percorsi outdoor dove il rilievo conta più della numerazione civica",
                MaxZoom: 17),
            new Entry("CartoDB Light",                       "https://cartodb-basemaps-a.global.ssl.fastly.net/light_all/{z}/{x}/{y}.png",
                "Mondiale",
                "Stile minimale chiaro (\"Positron\"), pochissimo rumore visivo, ottimo contrasto di sfondo",
                "Quando POI e percorsi disegnati devono risaltare al massimo, o per stampe a bassa risoluzione",
                MaxZoom: 20),
            new Entry("Thunderforest Atlas (richiede API key)",
                "https://tile.thunderforest.com/atlas/{z}/{x}/{y}.png?apikey={apikey}",
                "Mondiale",
                "Stile \"outdoor\" pensato per la stampa: strade e nomi ad alto contrasto, leggibile anche rimpicciolito; richiede API key gratuita (thunderforest.com)",
                "Stradari stampati destinati a lettura su carta, specialmente in bianco e nero",
                MaxZoom: 20),
            new Entry("Thunderforest Neighbourhood (richiede API key)",
                "https://tile.thunderforest.com/neighbourhood/{z}/{x}/{y}.png?apikey={apikey}",
                "Mondiale",
                "Variante Thunderforest orientata al dettaglio di quartiere: edifici e numerazione civica più marcati rispetto ad Atlas; richiede API key gratuita",
                "Stradari cittadini a scala molto dettagliata (1:1.000/1:5.000) dove serve distinguere edifici e vie strette",
                MaxZoom: 20),
            new Entry("Stadia Alidade Smooth (richiede API key)",
                "https://tiles.stadiamaps.com/tiles/alidade_smooth/{z}/{x}/{y}.png?api_key={apikey}",
                "Mondiale",
                "Stile pulito e uniforme, colori tenui, alta leggibilità dei nomi delle strade; richiede API key gratuita (stadiamaps.com)",
                "Stradari generali con estetica moderna e minimalista",
                MaxZoom: 20),
            new Entry("Stadia Stamen Toner Lite (richiede API key)",
                "https://tiles.stadiamaps.com/tiles/stamen_toner_lite/{z}/{x}/{y}.png?api_key={apikey}",
                "Mondiale",
                "Stile bianco e nero ad altissimo contrasto (derivato da Stamen Toner), pensato esplicitamente per la stampa; richiede API key",
                "Stampa B/N pura, quando serve il massimo contrasto senza applicare i filtri PdfContrastMode in post-produzione",
                MaxZoom: 20),
        };

        // Default: OpenStreetMap Standard (indice 0)
        public static readonly string DefaultUrl = All[0].UrlTemplate;

        // Zoom massimo realmente servito dal tile server con questo URL template
        // (i template non elencati, es. legacy/custom, usano il fallback 19 di OSM Standard).
        // Serve a evitare di richiedere zoom che il server non serve (tile 404 → mappa vuota
        // che non si aggiorna più, perché i fetch falliti non vengono ricaricati oltre il retry
        // del frame successivo).
        public static int GetMaxZoom(string urlTemplate)
        {
            foreach (var entry in All)
                if (entry.UrlTemplate == urlTemplate)
                    return entry.MaxZoom;
            return 19;
        }
    }

    // Impostazioni globali dello stradario
    public class StradarioSettings
    {
        public PageSize PageSize { get; set; } = PageSize.A4;
        public PageOrientation Orientation { get; set; } = PageOrientation.Portrait;
        public int Dpi { get; set; } = 150;
        public MapScale Scale { get; set; } = MapScale.Scale100K;

        // URL template del tile server scelto ({z}/{x}/{y} sostituiti a runtime)
        public string TileServerUrl { get; set; } = TileServers.DefaultUrl;

        // Chiave API personale per i tile server che la richiedono
        // (TileServers.Entry.RequiresApiKey). Ignorata dai server anonimi.
        public string TileServerApiKey { get; set; } = "";

        // Sostituisce {apikey} nel template con la chiave impostata, lasciando
        // {z}/{x}/{y} intatti per il fetch dei singoli tile. Se il template
        // non richiede una chiave ritorna l'URL invariato.
        public string GetEffectiveTileServerUrl() =>
            TileServerUrl.Contains("{apikey}")
                ? TileServerUrl.Replace("{apikey}", Uri.EscapeDataString(TileServerApiKey ?? ""))
                : TileServerUrl;

        // Chiave API Groq (console.groq.com), facoltativa: se impostata abilita
        // la ricerca POI in linguaggio naturale (interpretazione query + ranking/
        // descrizione risultati, vedi PoiSearchService.InterpretQueryAsync/
        // RankAndDescribeAsync). Se vuota, la ricerca POI resta quella storica
        // basata su parole chiave note + Nominatim.
        public string GroqApiKey { get; set; } = "";

        // Dopo quanti secondi di inattività un oggetto sbloccato (pagina,
        // gruppo POI, percorso) viene bloccato automaticamente per evitare
        // spostamenti accidentali. 0 = disabilitato.
        public int AutoLockSeconds { get; set; } = 60;

        // Contrasto applicato alle mappe raster solo nel PDF esportato (vedi PdfContrastMode)
        public PdfContrastMode PdfContrastMode { get; set; } = PdfContrastMode.None;

        // Ritorna le dimensioni in mm per il formato scelto
        public (double WidthMm, double HeightMm) GetPageDimensionsMm()
        {
            (double w, double h) = PageSize switch
            {
                PageSize.A5 => (148.0, 210.0),
                PageSize.A4 => (210.0, 297.0),
                PageSize.A3 => (297.0, 420.0),
                _           => (210.0, 297.0)
            };
            if (Orientation == PageOrientation.Landscape)
                return (h, w);
            return (w, h);
        }

        // Ritorna il denominatore della scala (es. 100000)
        public int GetScaleDenominator()
        {
            return Scale switch
            {
                MapScale.Scale1K   =>   1000,
                MapScale.Scale5K   =>   5000,
                MapScale.Scale10K  =>  10000,
                MapScale.Scale100K => 100000,
                MapScale.Scale200K => 200000,
                _                  => 100000
            };
        }

        // Etichetta leggibile della scala corrente
        public string GetScaleLabel() => Scale switch
        {
            MapScale.Scale1K   => "1:1.000",
            MapScale.Scale5K   => "1:5.000",
            MapScale.Scale10K  => "1:10.000",
            MapScale.Scale100K => "1:100.000",
            MapScale.Scale200K => "1:200.000",
            _                  => "1:100.000"
        };

        // Calcola quanti km coprono la pagina in larghezza
        public double GetPageWidthKm()
        {
            var (widthMm, _) = GetPageDimensionsMm();
            // larghezza_mm * scala / 1.000.000 = km
            return widthMm * GetScaleDenominator() / 1_000_000.0;
        }

        // Calcola quanti km coprono la pagina in altezza
        public double GetPageHeightKm()
        {
            var (_, heightMm) = GetPageDimensionsMm();
            return heightMm * GetScaleDenominator() / 1_000_000.0;
        }
    }

    // Tipi di icona disponibili per i gruppi di POI. Il set è chiuso di proposito:
    // ogni valore ha un disegno vettoriale dedicato in Services/PoiIconRenderer.cs,
    // così da poter essere renderizzato in modo identico su mappa interattiva,
    // PDF e KMZ esportato (nessun upload di immagini custom).
    public enum PoiIconType
    {
        Pin, Star, Flag, Home, Church, Monument, Restaurant,
        Cafe, Shop, Parking, Hospital, Hotel, Viewpoint, Camping,
        Fountain, Info
    }

    // Registro delle icone POI con etichetta leggibile in italiano, nell'ordine
    // in cui vanno proposte nei selettori della UI.
    public static class PoiIcons
    {
        public record Entry(string Name, PoiIconType Type);

        public static readonly Entry[] All = new[]
        {
            new Entry("Segnaposto",     PoiIconType.Pin),
            new Entry("Stella",         PoiIconType.Star),
            new Entry("Bandiera",       PoiIconType.Flag),
            new Entry("Abitazione",     PoiIconType.Home),
            new Entry("Chiesa",         PoiIconType.Church),
            new Entry("Monumento",      PoiIconType.Monument),
            new Entry("Ristorante",     PoiIconType.Restaurant),
            new Entry("Bar / Caffè",    PoiIconType.Cafe),
            new Entry("Negozio",        PoiIconType.Shop),
            new Entry("Parcheggio",     PoiIconType.Parking),
            new Entry("Ospedale",       PoiIconType.Hospital),
            new Entry("Hotel",          PoiIconType.Hotel),
            new Entry("Punto panoramico", PoiIconType.Viewpoint),
            new Entry("Campeggio",      PoiIconType.Camping),
            new Entry("Fontana",        PoiIconType.Fountain),
            new Entry("Informazioni",   PoiIconType.Info),
        };
    }

    // Singolo punto di interesse all'interno di un gruppo
    public class PoiItem
    {
        public int Id { get; set; }

        // Etichetta visualizzata sulla mappa e negli elenchi
        public string Label { get; set; } = "";

        // Descrizione opzionale
        public string Description { get; set; } = "";

        public double Lon { get; set; }
        public double Lat { get; set; }
    }

    // Gruppo di POI: etichetta, icona e colore comuni a tutti i suoi punti
    public class PoiGroup
    {
        public int Id { get; set; }

        public string Name { get; set; } = "Nuovo gruppo";
        public string Description { get; set; } = "";

        public PoiIconType Icon { get; set; } = PoiIconType.Pin;

        // Colore del gruppo in formato esadecimale "#RRGGBB"
        public string ColorHex { get; set; } = "#1E88E5";

        // Se true, i POI del gruppo non possono essere trascinati sulla
        // mappa (protezione da spostamenti accidentali)
        public bool IsLocked { get; set; } = false;

        public List<PoiItem> Items { get; set; } = new List<PoiItem>();
    }

    // Singolo punto geografico (usato per le sequenze di punti dei percorsi)
    public class GeoPoint
    {
        public double Lon { get; set; }
        public double Lat { get; set; }
    }

    // Percorso: sequenza ordinata di punti geografici (traccia/itinerario),
    // con etichetta, descrizione e colore propri. Analogo per struttura ai
    // gruppi di POI, ma un percorso è un'unica entità (non un contenitore):
    // ogni percorso ha la propria lista di punti, non ci sono "sotto-elementi".
    public class Percorso
    {
        public int Id { get; set; }

        public string Label { get; set; } = "Nuovo percorso";
        public string Description { get; set; } = "";

        // Colore del percorso in formato esadecimale "#RRGGBB"
        public string ColorHex { get; set; } = "#E53935";

        // Se true, i punti del percorso non possono essere trascinati sulla
        // mappa (protezione da spostamenti accidentali)
        public bool IsLocked { get; set; } = false;

        public List<GeoPoint> Points { get; set; } = new List<GeoPoint>();
    }

    // Rettangolo geografico (coordinate WGS84)
    public class GeoRect
    {
        public double MinLon { get; set; }
        public double MinLat { get; set; }
        public double MaxLon { get; set; }
        public double MaxLat { get; set; }

        [JsonIgnore]
        public double CenterLon => (MinLon + MaxLon) / 2.0;

        [JsonIgnore]
        public double CenterLat => (MinLat + MaxLat) / 2.0;

        [JsonIgnore]
        public double Width => MaxLon - MinLon;

        [JsonIgnore]
        public double Height => MaxLat - MinLat;
    }

    // Singola pagina dello stradario
    public class MapPage
    {
        public int Id { get; set; }

        // Etichetta visualizzata (es. "A1", "B3", oppure personalizzata)
        public string Label { get; set; } = "";

        // Area geografica coperta dalla pagina
        public GeoRect GeoBounds { get; set; } = new GeoRect();

        // Numero di pagina nell'indice finale (assegnato automaticamente)
        public int PageNumber { get; set; }

        // Stringa descrittiva opzionale (es. nome del quartiere)
        public string Description { get; set; } = "";

        // Se true, la pagina non può essere trascinata sulla mappa
        // (protezione da spostamenti accidentali)
        public bool IsLocked { get; set; } = false;
    }

    // Struttura completa del progetto stradario (salvata su file)
    public class StradarioProject
    {
        public string ProjectName { get; set; } = "Nuovo Stradario";
        public StradarioSettings Settings { get; set; } = new StradarioSettings();
        public List<MapPage> Pages { get; set; } = new List<MapPage>();

        // Gruppi di punti di interesse (POI), caricabili/salvabili anche come KMZ
        public List<PoiGroup> PoiGroups { get; set; } = new List<PoiGroup>();

        // Percorsi (tracce/itinerari), caricabili/salvabili anche come KMZ
        public List<Percorso> Percorsi { get; set; } = new List<Percorso>();

        public DateTime LastModified { get; set; } = DateTime.Now;

        // Vista corrente della mappa (centro e zoom)
        public double ViewCenterLon { get; set; } = 12.4964;  // Default: Roma
        public double ViewCenterLat { get; set; } = 41.9028;
        public double ViewZoom { get; set; } = 10.0;
    }
}
