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
    // Una riga del "piano di viaggio" unificato stampato in PDF: un POI o
    // un estremo (partenza/arrivo) di un percorso.
    public record ItineraryEntry(
        DateTime? Date,
        string    Label,
        string?   SubLabel,
        double    Lon,
        double    Lat,
        string    ColorHex,
        PoiIconType Icon,
        string?   Description);

    public static class ItineraryOrdering
    {
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
        // un entry per ogni PoiItem (icona/colore del proprio gruppo) più,
        // per ogni percorso con almeno un estremo datato, fino a due entry
        // ("Partenza"/"Arrivo") sul primo/ultimo punto del percorso. Stessa
        // regola dell'albero: i non datati vanno in coda (DateTime.MaxValue).
        public static List<ItineraryEntry> BuildItineraryEntries(List<PoiGroup> poiGroups, List<Percorso> percorsi)
        {
            var entries = new List<ItineraryEntry>();

            foreach (var group in poiGroups)
                foreach (var item in group.Items)
                    entries.Add(new ItineraryEntry(
                        item.DateStart, item.Label, null, item.Lon, item.Lat,
                        group.ColorHex, item.Icon ?? PoiIconType.Pin, item.Description));

            foreach (var route in percorsi)
            {
                if (route.Points.Count == 0) continue;

                if (route.StartDateTime.HasValue)
                {
                    var p = route.Points[0];
                    entries.Add(new ItineraryEntry(
                        route.StartDateTime, route.Label, "Partenza", p.Lon, p.Lat,
                        route.ColorHex, PoiIconType.Pin, route.Description));
                }
                if (route.EndDateTime.HasValue)
                {
                    var p = route.Points[^1];
                    entries.Add(new ItineraryEntry(
                        route.EndDateTime, route.Label, "Arrivo", p.Lon, p.Lat,
                        route.ColorHex, PoiIconType.Pin, route.Description));
                }
            }

            return entries.OrderBy(e => e.Date ?? DateTime.MaxValue).ToList();
        }
    }
}
