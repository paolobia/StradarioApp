// =============================================================================
// Services/ItineraryOrdering.cs
//
// SINOSSI: Ordinamento cronologico di POI/gruppi/percorsi datati, usato sia
//   dall'albero di navigazione (UI/MainWindow) sia dal PDF (PdfGenerator,
//   sezione "Piano di viaggio").
//
//   Un elemento senza data va SEMPRE in coda, dopo tutti quelli datati
//   (sentinel DateTime.MaxValue) — sia nell'albero sia nel PDF. Una prima
//   versione metteva i non datati in testa nell'albero ("passato remoto")
//   e in coda nel PDF: due regole diverse per lo stesso concetto, che
//   l'utente ha segnalato come comportamento sbagliato/confuso una volta
//   provato — unificate qui su un'unica regola coerente ovunque.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using StradarioApp.Models;

namespace StradarioApp.Services
{
    // Un punto annidato mostrato sotto una entry di percorso nel piano di
    // viaggio (i punti del percorso marcati IsPoi) — non ha una propria
    // data (solo il percorso nel suo complesso ha inizio/fine).
    public record ItineraryNestedPoint(
        string    Label,
        double    Lon,
        double    Lat,
        PoiIconType Icon,
        string?   Description);

    // Una riga del "piano di viaggio" unificato stampato in PDF: un POI (o
    // un suo estremo inizio/fine, se ha date diverse) oppure un estremo
    // (inizio/fine) di un percorso. IsStart è null per un punto "semplice"
    // senza distinzione inizio/fine (data unica, o nessuna data), true per
    // l'estremo di inizio, false per l'estremo di fine.
    public record ItineraryEntry(
        DateTime? Date,
        string    Label,
        bool?     IsStart,
        double    Lon,
        double    Lat,
        string    ColorHex,
        PoiIconType Icon,
        string?   Description,
        List<ItineraryNestedPoint>? NestedPoints = null);

    public static class ItineraryOrdering
    {
        // Formatta una singola data: solo il giorno se l'orario è mezzanotte
        // esatta ("nessun orario impostato"), altrimenti data+ora — usata
        // sia dall'albero (MainWindow) sia dal PDF (PdfGenerator).
        public static string FormatSingleDate(DateTime d) =>
            d.TimeOfDay == TimeSpan.Zero ? d.ToString("dd/MM/yyyy") : d.ToString("dd/MM/yyyy HH:mm");

        // Formatta un intervallo inizio/fine (POI: DateStart/DateEnd, percorso:
        // StartDateTime/EndDateTime): "" se nessuno dei due è impostato, la sola
        // data se solo uno dei due lo è o se sono uguali (non ripetere due volte
        // la stessa data), altrimenti "inizio - fine". Condivisa da albero e PDF
        // così le due viste restano sempre coerenti.
        public static string FormatDateRange(DateTime? start, DateTime? end)
        {
            if (!start.HasValue && !end.HasValue) return "";
            if (start.HasValue && end.HasValue && start.Value != end.Value)
                return $"{FormatSingleDate(start.Value)} - {FormatSingleDate(end.Value)}";
            return FormatSingleDate((start ?? end)!.Value);
        }

        // Ordina in place gli item di un gruppo per DateStart crescente;
        // i non datati (null) vanno in coda — stabile: a parità di chiave
        // mantiene l'ordine relativo originale.
        public static void SortItemsByDate(List<PoiItem> items)
        {
            var sorted = items.OrderBy(i => i.DateStart ?? DateTime.MaxValue).ToList();
            items.Clear();
            items.AddRange(sorted);
        }

        // Data minima tra i POI del gruppo, o DateTime.MaxValue se nessuno
        // ha una data (coerente col sentinel usato per l'ordinamento: i
        // gruppi senza alcuna data vanno in coda).
        public static DateTime GetGroupMinDate(PoiGroup group)
        {
            var dated = group.Items.Where(i => i.DateStart.HasValue).Select(i => i.DateStart!.Value);
            return dated.Any() ? dated.Min() : DateTime.MaxValue;
        }

        // Data minima tra gli estremi (partenza/arrivo) del percorso, o
        // DateTime.MaxValue se nessuno dei due è impostato.
        public static DateTime GetPercorsoMinDate(Percorso r)
        {
            DateTime? min = null;
            if (r.StartDateTime.HasValue) min = r.StartDateTime.Value;
            if (r.EndDateTime.HasValue && (!min.HasValue || r.EndDateTime.Value < min.Value)) min = r.EndDateTime.Value;
            return min ?? DateTime.MaxValue;
        }

        // Ordina in place i gruppi POI per data minima crescente (i gruppi
        // senza alcun POI datato vanno in coda) — stabile.
        public static void SortGroupsByMinDate(List<PoiGroup> groups)
        {
            var sorted = groups.OrderBy(GetGroupMinDate).ToList();
            groups.Clear();
            groups.AddRange(sorted);
        }

        // Ordina in place i percorsi per data minima crescente (i percorsi
        // senza data vanno in coda) — stabile.
        public static void SortPercorsiByMinDate(List<Percorso> percorsi)
        {
            var sorted = percorsi.OrderBy(GetPercorsoMinDate).ToList();
            percorsi.Clear();
            percorsi.AddRange(sorted);
        }

        // Costruisce la sequenza unificata "piano di viaggio" per la stampa:
        // un entry per ogni PoiItem (icona/colore del proprio gruppo) — due
        // entry (Inizio/Fine) se DateStart e DateEnd sono entrambi impostati
        // e DIVERSI, una sola altrimenti (anche se entrambi impostati ma
        // uguali: mai due righe per lo stesso istante) — più, per ogni
        // percorso con almeno un estremo datato, fino a due entry (Inizio/
        // Fine) sul primo/ultimo punto del percorso (stessa regola: un solo
        // estremo, o entrambi ma uguali, dà una sola voce), con i suoi punti
        // marcati IsPoi annidati sotto l'unica entry (o quella di Inizio, se
        // sono due). La descrizione va su una sola delle due entry quando
        // sono due, mai ripetuta. Stessa regola dell'albero: i non datati
        // vanno in coda (DateTime.MaxValue).
        public static List<ItineraryEntry> BuildItineraryEntries(List<PoiGroup> poiGroups, List<Percorso> percorsi)
        {
            var entries = new List<ItineraryEntry>();

            foreach (var group in poiGroups)
                foreach (var item in group.Items)
                {
                    var icon = item.Icon ?? PoiIconType.Pin;
                    bool distinctBoth = item.DateStart.HasValue && item.DateEnd.HasValue
                        && item.DateStart.Value != item.DateEnd.Value;

                    if (distinctBoth)
                    {
                        entries.Add(new ItineraryEntry(
                            item.DateStart, item.Label, true, item.Lon, item.Lat,
                            group.ColorHex, icon, item.Description));
                        entries.Add(new ItineraryEntry(
                            item.DateEnd, item.Label, false, item.Lon, item.Lat,
                            group.ColorHex, icon, null));
                    }
                    else
                    {
                        entries.Add(new ItineraryEntry(
                            item.DateStart ?? item.DateEnd, item.Label, null, item.Lon, item.Lat,
                            group.ColorHex, icon, item.Description));
                    }
                }

            foreach (var route in percorsi)
            {
                if (route.Points.Count == 0) continue;
                bool hasStart = route.StartDateTime.HasValue;
                bool hasEnd   = route.EndDateTime.HasValue;
                if (!hasStart && !hasEnd) continue;

                var nestedPoints = route.Points.Where(p => p.IsPoi)
                    .Select(p => new ItineraryNestedPoint(p.PoiLabel, p.Lon, p.Lat, p.PoiIcon, p.PoiDescription))
                    .ToList();
                if (nestedPoints.Count == 0) nestedPoints = null!;

                bool distinctBoth = hasStart && hasEnd && route.StartDateTime!.Value != route.EndDateTime!.Value;

                if (distinctBoth)
                {
                    var pStart = route.Points[0];
                    entries.Add(new ItineraryEntry(
                        route.StartDateTime, route.Label, true, pStart.Lon, pStart.Lat,
                        route.ColorHex, PoiIconType.Pin, route.Description, nestedPoints));

                    var pEnd = route.Points[^1];
                    entries.Add(new ItineraryEntry(
                        route.EndDateTime, route.Label, false, pEnd.Lon, pEnd.Lat,
                        route.ColorHex, PoiIconType.Pin, null, null));
                }
                else
                {
                    // Un solo estremo impostato, oppure entrambi ma uguali:
                    // una sola voce, senza distinzione Inizio/Fine.
                    var p = hasStart ? route.Points[0] : route.Points[^1];
                    entries.Add(new ItineraryEntry(
                        route.StartDateTime ?? route.EndDateTime, route.Label, null, p.Lon, p.Lat,
                        route.ColorHex, PoiIconType.Pin, route.Description, nestedPoints));
                }
            }

            return entries.OrderBy(e => e.Date ?? DateTime.MaxValue).ToList();
        }
    }
}
