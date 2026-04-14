using Newtonsoft.Json;
using System.Collections.Generic;

namespace StradarioApp.Models
{
    public enum PageSize { A5, A4, A3 }
    public enum PageOrientation { Portrait, Landscape }
    public enum MapScale { Scale1K, Scale5K, Scale10K, Scale100K, Scale200K }

    public static class TileServers
    {
        public static readonly List<(string Name, string Url)> All = new()
        {
            ("OpenStreetMap Standard",  "https://tile.openstreetmap.org/{z}/{x}/{y}.png"),
            ("OSM France",              "https://a.tile.openstreetmap.fr/osmfr/{z}/{x}/{y}.png"),
            ("OSM Deutschland",         "https://tile.openstreetmap.de/{z}/{x}/{y}.png"),
            ("OpenTopoMap",             "https://tile.opentopomap.org/{z}/{x}/{y}.png"),
            ("CartoDB Light",           "https://cartodb-basemaps-a.global.ssl.fastly.net/light_all/{z}/{x}/{y}.png"),
        };

        public static string Default => All[0].Url;
    }

    public class StradarioSettings
    {
        public PageSize PageSize { get; set; } = PageSize.A4;
        public PageOrientation Orientation { get; set; } = PageOrientation.Portrait;
        public int Dpi { get; set; } = 150;
        public MapScale Scale { get; set; } = MapScale.Scale100K;
        public string TileServerUrl { get; set; } = TileServers.Default;

        public (double WidthMm, double HeightMm) GetPageDimensionsMm()
        {
            (double w, double h) = PageSize switch
            {
                PageSize.A5 => (148.0, 210.0),
                PageSize.A4 => (210.0, 297.0),
                PageSize.A3 => (297.0, 420.0),
                _ => (210.0, 297.0)
            };
            return Orientation == PageOrientation.Landscape ? (h, w) : (w, h);
        }

        public int GetScaleDenominator() => Scale switch
        {
            MapScale.Scale1K   => 1000,
            MapScale.Scale5K   => 5000,
            MapScale.Scale10K  => 10000,
            MapScale.Scale100K => 100000,
            MapScale.Scale200K => 200000,
            _ => 100000
        };

        public string GetScaleLabel() => Scale switch
        {
            MapScale.Scale1K   => "1:1.000",
            MapScale.Scale5K   => "1:5.000",
            MapScale.Scale10K  => "1:10.000",
            MapScale.Scale100K => "1:100.000",
            MapScale.Scale200K => "1:200.000",
            _ => "1:100.000"
        };

        public double GetPageWidthKm()
        {
            var (w, _) = GetPageDimensionsMm();
            return w * GetScaleDenominator() / 1_000_000.0;
        }

        public double GetPageHeightKm()
        {
            var (_, h) = GetPageDimensionsMm();
            return h * GetScaleDenominator() / 1_000_000.0;
        }
    }

    public class GeoRect
    {
        public double MinLon { get; set; }
        public double MinLat { get; set; }
        public double MaxLon { get; set; }
        public double MaxLat { get; set; }

        [JsonIgnore] public double CenterLon => (MinLon + MaxLon) / 2.0;
        [JsonIgnore] public double CenterLat => (MinLat + MaxLat) / 2.0;
        [JsonIgnore] public double Width  => MaxLon - MinLon;
        [JsonIgnore] public double Height => MaxLat - MinLat;
    }

    public class MapPage
    {
        public int Id { get; set; }
        public string Label { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public GeoRect GeoBounds { get; set; } = new GeoRect();
        public int PageNumber { get; set; }
    }

    public class StradarioProject
    {
        public string ProjectName { get; set; } = "Nuovo Progetto";
        public StradarioSettings Settings { get; set; } = new StradarioSettings();
        public List<MapPage> Pages { get; set; } = new List<MapPage>();
        public System.DateTime LastModified { get; set; } = System.DateTime.Now;

        // View state
        public double ViewCenterLon { get; set; } = 12.4964;
        public double ViewCenterLat { get; set; } = 41.9028;
        public double ViewZoom { get; set; } = 10.0;
    }
}
