using StradarioViewer.Models;

namespace StradarioViewer.Services;

// Un percorso o un POI datato che ricade in un giorno specifico, con abbastanza
// informazione (colore, punti, etichetta/descrizione) per disegnarlo su mappa e
// mostrarlo in una lista di dettaglio.
public sealed class DayEntry
{
    public required string Kind { get; init; } // "route" oppure "poi"
    public required string Label { get; init; }
    public required string Description { get; init; }
    public required string ColorHex { get; init; }
    public required List<(double Lon, double Lat)> Points { get; init; }
    public DateTime? Start { get; init; }
    public DateTime? End { get; init; }
}

public sealed class TravelPlanService
{
    private const string StorageKey = "stradario.project";
    private const string StorageFileNameKey = "stradario.fileName";

    private readonly LocalStorageService _storage;

    public StradarioProject? Project { get; private set; }
    public string? FileName { get; private set; }

    public TravelPlanService(LocalStorageService storage)
    {
        _storage = storage;
    }

    public async Task<bool> TryLoadFromStorageAsync()
    {
        var json = await _storage.GetItemAsync(StorageKey);
        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            Project = System.Text.Json.JsonSerializer.Deserialize(json, StradarioJsonContext.Default.StradarioProject);
            FileName = await _storage.GetItemAsync(StorageFileNameKey);
            return Project != null;
        }
        catch
        {
            // File corrotto/formato inatteso: meglio ripartire da zero (chiedere di
            // ricaricare) che mostrare un errore bloccante.
            Project = null;
            return false;
        }
    }

    public async Task<bool> LoadFromJsonAsync(string json, string fileName)
    {
        StradarioProject? project;
        try
        {
            project = System.Text.Json.JsonSerializer.Deserialize(json, StradarioJsonContext.Default.StradarioProject);
        }
        catch
        {
            return false;
        }

        if (project == null) return false;

        Project = project;
        FileName = fileName;
        await _storage.SetItemAsync(StorageKey, json);
        await _storage.SetItemAsync(StorageFileNameKey, fileName);
        return true;
    }

    public async Task ClearAsync()
    {
        Project = null;
        FileName = null;
        await _storage.RemoveItemAsync(StorageKey);
        await _storage.RemoveItemAsync(StorageFileNameKey);
    }

    // Tutti i giorni (solo data, senza ora) toccati da almeno un percorso o POI
    // datato, in ordine cronologico. Vuota se il progetto non ha alcuna data.
    public List<DateOnly> GetAvailableDays()
    {
        if (Project == null) return new List<DateOnly>();

        var days = new SortedSet<DateOnly>();

        foreach (var p in Project.Percorsi)
        {
            foreach (var d in DaysInRange(p.StartDateTime, p.EndDateTime))
                days.Add(d);
        }

        foreach (var g in Project.PoiGroups)
        foreach (var item in g.Items)
        {
            foreach (var d in DaysInRange(item.DateStart, item.DateEnd))
                days.Add(d);
        }

        return days.ToList();
    }

    // Percorsi/POI del giorno indicato. Se il progetto non ha NESSUNA data
    // impostata da nessuna parte (GetAvailableDays() vuoto), il filtro non ha senso:
    // si ritorna tutto senza filtrare, invece di una schermata vuota.
    public List<DayEntry> GetForDay(DateOnly day)
    {
        if (Project == null) return new List<DayEntry>();

        bool noDatesAtAll = GetAvailableDays().Count == 0;
        var result = new List<DayEntry>();

        foreach (var p in Project.Percorsi)
        {
            if (p.Points.Count < 2) continue;
            if (!noDatesAtAll && !IntersectsDay(p.StartDateTime, p.EndDateTime, day)) continue;

            result.Add(new DayEntry
            {
                Kind = "route",
                Label = p.Label,
                Description = p.Description,
                ColorHex = p.ColorHex,
                Points = p.Points.Select(pt => (pt.Lon, pt.Lat)).ToList(),
                Start = p.StartDateTime,
                End = p.EndDateTime,
            });

            // POI inline sui punti del percorso: nessuna data propria, quindi
            // seguono la finestra del percorso a cui appartengono.
            foreach (var pt in p.Points)
            {
                if (!pt.IsPoi) continue;
                result.Add(new DayEntry
                {
                    Kind = "poi",
                    Label = pt.PoiLabel,
                    Description = pt.PoiDescription,
                    ColorHex = p.ColorHex,
                    Points = new List<(double, double)> { (pt.Lon, pt.Lat) },
                    Start = p.StartDateTime,
                    End = p.EndDateTime,
                });
            }
        }

        foreach (var g in Project.PoiGroups)
        foreach (var item in g.Items)
        {
            if (!noDatesAtAll && !IntersectsDay(item.DateStart, item.DateEnd, day)) continue;

            result.Add(new DayEntry
            {
                Kind = "poi",
                Label = item.Label,
                Description = item.Description,
                ColorHex = g.ColorHex,
                Points = new List<(double, double)> { (item.Lon, item.Lat) },
                Start = item.DateStart,
                End = item.DateEnd,
            });
        }

        return result;
    }

    private static IEnumerable<DateOnly> DaysInRange(DateTime? start, DateTime? end)
    {
        if (start == null && end == null) yield break;

        var from = DateOnly.FromDateTime(start ?? end!.Value);
        var to = DateOnly.FromDateTime(end ?? start!.Value);
        if (to < from) (from, to) = (to, from);

        for (var d = from; d <= to; d = d.AddDays(1))
            yield return d;
    }

    private static bool IntersectsDay(DateTime? start, DateTime? end, DateOnly day)
    {
        if (start == null && end == null) return false;

        var from = DateOnly.FromDateTime(start ?? end!.Value);
        var to = DateOnly.FromDateTime(end ?? start!.Value);
        if (to < from) (from, to) = (to, from);

        return day >= from && day <= to;
    }
}
