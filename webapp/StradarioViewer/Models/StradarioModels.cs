// Sottoinsieme dei modelli dati di StradarioApp (Models/StradarioModels.cs, progetto
// desktop principale) copiato qui a mano: StradarioViewer e' un progetto .NET
// indipendente (Blazor WebAssembly, niente reference al progetto Avalonia) che legge
// lo stesso file .stradario (JSON, System.Text.Json qui invece di Newtonsoft, ma
// compatibile: nessuna proprieta' usa [JsonProperty] lato desktop, quindi i nomi JSON
// coincidono con i nomi C# in entrambi i progetti). Solo i campi effettivamente letti
// dal viewer sono presenti (Pages/MapPage, StradarioSettings, ecc. sono omessi).
//
// IMPORTANTE: se il modello principale cambia forma (nuovi campi data-correlati,
// nuovi valori enum), va risincronizzato QUI a mano — nessun meccanismo automatico di
// condivisione tra i due progetti. Gli enum sono persistiti come interi posizionali:
// l'ordine dei membri deve restare identico a quello del progetto desktop.

using System.Text.Json.Serialization;

namespace StradarioViewer.Models;

public sealed class StradarioProject
{
    public string ProjectName { get; set; } = "";
    public List<PoiGroup> PoiGroups { get; set; } = new();
    public List<Percorso> Percorsi { get; set; } = new();
    public double ViewCenterLon { get; set; } = 12.4964;
    public double ViewCenterLat { get; set; } = 41.9028;
    public double ViewZoom { get; set; } = 10.0;
}

public sealed class PoiGroup
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string ColorHex { get; set; } = "#3388ff";
    public List<PoiItem> Items { get; set; } = new();
}

public sealed class PoiItem
{
    public int Id { get; set; }
    public string Label { get; set; } = "";
    public string Description { get; set; } = "";
    public double Lon { get; set; }
    public double Lat { get; set; }
    public DateTime? DateStart { get; set; }
    public DateTime? DateEnd { get; set; }
}

public sealed class Percorso
{
    public int Id { get; set; }
    public string Label { get; set; } = "";
    public string Description { get; set; } = "";
    public string ColorHex { get; set; } = "#3388ff";
    public DateTime? StartDateTime { get; set; }
    public DateTime? EndDateTime { get; set; }
    public List<GeoPoint> Points { get; set; } = new();
}

public sealed class GeoPoint
{
    public double Lon { get; set; }
    public double Lat { get; set; }
    public bool IsPoi { get; set; }
    public string PoiLabel { get; set; } = "";
    public string PoiDescription { get; set; } = "";
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(StradarioProject))]
public partial class StradarioJsonContext : JsonSerializerContext
{
}
