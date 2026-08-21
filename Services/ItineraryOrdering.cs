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
    // viaggio (i punti del percorso marcati IsPoi) — di norma non ha una
    // propria data (solo il percorso nel suo complesso ha inizio/fine).
    // ECCEZIONE: se inizio e fine del percorso hanno data/ora diverse E il
    // percorso ha almeno un punto POI, l'ULTIMO di questi punti assorbe la
    // data di fine (v. BuildItineraryEntries) — niente riga "(Fine)"
    // generica separata, l'arrivo è quel punto reale.
    public record ItineraryNestedPoint(
        string    Label,
        double    Lon,
        double    Lat,
        PoiIconType Icon,
        string?   Description,
        DateTime? Date = null);

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
        // Sigle italiane del giorno della settimana, indicizzate da
        // DateTime.DayOfWeek (Sunday=0..Saturday=6) — hardcoded (non da
        // CultureInfo) per coerenza con le altre stringhe UI dell'app, tutte
        // letterali in italiano.
        private static readonly string[] DayAbbreviations =
            { "Dom", "Lun", "Mar", "Mer", "Gio", "Ven", "Sab" };

        public static string GetDayAbbreviation(DateTime d) => DayAbbreviations[(int)d.DayOfWeek];

        // Nomi completi italiani del giorno della settimana, stesso indice di
        // DayAbbreviations — usati per l'intestazione di giornata del "Piano
        // di viaggio" PDF (raggruppato per giorno), a differenza della sigla
        // breve usata altrove.
        private static readonly string[] DayNames =
            { "Domenica", "Lunedì", "Martedì", "Mercoledì", "Giovedì", "Venerdì", "Sabato" };

        public static string GetDayName(DateTime d) => DayNames[(int)d.DayOfWeek];

        // Intestazione di una giornata nel "Piano di viaggio": "Martedì 01/09/2026".
        public static string FormatDayHeader(DateTime d) => $"{GetDayName(d)} {d:dd/MM/yyyy}";

        // Orario "di chiusura giornata" (23:59), usato SOLO da IsFullDaySpan
        // per riconoscere un range 00:00→23:59 come "tutto il giorno" (una
        // sola voce nel piano di viaggio, non due). Non nasconde più alcun
        // orario in fase di formattazione: un DateTime? è null oppure ha un
        // valore, e quel valore — orario incluso, anche 00:00 — va sempre
        // mostrato (richiesta esplicita dell'utente: niente ambiguità
        // "nessun orario vs. mezzanotte scelta apposta" nel testo mostrato,
        // solo nel dato — che non può comunque distinguerle, v. CLAUDE.md).
        private static readonly TimeSpan EndOfDayTime = new TimeSpan(23, 59, 0);

        // Formatta una singola data, SEMPRE con l'orario incluso (anche
        // 00:00) — usata da albero, tooltip, dialog e PDF, ovunque si mostri
        // un DateTime già impostato.
        // `includeDayAbbrev` antepone la sigla del giorno della settimana
        // (es. "Lun 10/03/2026 00:00") — usato solo dal PDF, l'albero di
        // navigazione resta invariato.
        public static string FormatSingleDate(DateTime d, bool includeDayAbbrev = false)
        {
            string date = d.ToString("dd/MM/yyyy HH:mm");
            return includeDayAbbrev ? $"{GetDayAbbreviation(d)} {date}" : date;
        }

        // Solo l'orario "HH:mm", SEMPRE (anche 00:00 esatta) — usato dalla
        // colonna ora del "Piano di viaggio" PDF, dove la data vive
        // nell'intestazione di giornata e non più per-riga.
        public static string FormatTimeOnly(DateTime d) => d.ToString("HH:mm");

        // Formatta un intervallo inizio/fine (POI: DateStart/DateEnd, percorso:
        // StartDateTime/EndDateTime): "" se nessuno dei due è impostato, la sola
        // data se solo uno dei due lo è o se sono uguali (non ripetere due volte
        // la stessa data), altrimenti "inizio - fine". Condivisa da albero e PDF
        // così le due viste restano sempre coerenti.
        public static string FormatDateRange(DateTime? start, DateTime? end, bool includeDayAbbrev = false)
        {
            if (!start.HasValue && !end.HasValue) return "";
            if (start.HasValue && end.HasValue && start.Value != end.Value)
                return $"{FormatSingleDate(start.Value, includeDayAbbrev)} - {FormatSingleDate(end.Value, includeDayAbbrev)}";
            return FormatSingleDate((start ?? end)!.Value, includeDayAbbrev);
        }

        // Un intervallo inizio/fine copre "l'intera giornata" (stesso giorno,
        // da 00:00 a 23:59): nel "Piano di viaggio" del PDF questo va trattato
        // come una singola voce (nessuna riga "Fine" separata), non diversamente
        // da inizio == fine — richiesta esplicita dell'utente dopo aver notato
        // che un evento 00:00→23:59 nello stesso giorno veniva comunque
        // spezzato in due righe.
        private static bool IsFullDaySpan(DateTime start, DateTime end) =>
            start.Date == end.Date && start.TimeOfDay == TimeSpan.Zero && end.TimeOfDay == EndOfDayTime;

        // Ordina in place gli item di un gruppo per DateStart crescente;
        // i non datati (null) vanno in coda — stabile: a parità di chiave
        // mantiene l'ordine relativo originale.
        public static void SortItemsByDate(List<PoiItem> items)
        {
            var sorted = items.OrderBy(i => i.DateStart ?? DateTime.MaxValue).ToList();
            items.Clear();
            items.AddRange(sorted);
        }

        // Data minima di un singolo POI: il minore tra DateStart e DateEnd
        // (ignorando quello non impostato), o DateTime.MaxValue se nessuno
        // dei due lo è — così un POI con solo DateEnd impostato (senza
        // DateStart) conta comunque la sua data reale invece di finire tra
        // i "senza data".
        private static DateTime GetItemMinDate(PoiItem item)
        {
            DateTime? min = null;
            if (item.DateStart.HasValue) min = item.DateStart.Value;
            if (item.DateEnd.HasValue && (!min.HasValue || item.DateEnd.Value < min.Value)) min = item.DateEnd.Value;
            return min ?? DateTime.MaxValue;
        }

        // Data minima tra i POI del gruppo, o DateTime.MaxValue se nessuno
        // ha una data (coerente col sentinel usato per l'ordinamento: i
        // gruppi senza alcuna data vanno in coda).
        public static DateTime GetGroupMinDate(PoiGroup group)
        {
            var dated = group.Items.Select(GetItemMinDate).Where(d => d != DateTime.MaxValue);
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
                    if (!item.DateStart.HasValue && !item.DateEnd.HasValue) continue;

                    var icon = item.Icon ?? PoiIconType.Pin;
                    bool distinctBoth = item.DateStart.HasValue && item.DateEnd.HasValue
                        && item.DateStart.Value != item.DateEnd.Value
                        && !IsFullDaySpan(item.DateStart.Value, item.DateEnd.Value);

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

                bool distinctBoth = hasStart && hasEnd && route.StartDateTime!.Value != route.EndDateTime!.Value
                    && !IsFullDaySpan(route.StartDateTime!.Value, route.EndDateTime!.Value);

                if (distinctBoth)
                {
                    var pStart = route.Points[0];

                    // Il merge "ultimo POI assorbe la fine" vale SOLO se
                    // inizio e fine cadono nello stesso giorno: su giorni
                    // diversi la fine deve comunque comparire sotto
                    // l'intestazione del proprio giorno nel "Piano di
                    // viaggio" (raggruppato per giorno) — impossibile se
                    // resta annidata sotto l'entry di Inizio, che appartiene
                    // al giorno di partenza. In quel caso si ripristina la
                    // riga "(Fine)" separata, esattamente come quando il
                    // percorso non ha nessun punto marcato.
                    bool sameDay = nestedPoints != null
                        && route.StartDateTime!.Value.Date == route.EndDateTime!.Value.Date;

                    if (sameDay)
                    {
                        // Il percorso ha già un punto reale (l'ultimo POI
                        // incontrato): l'arrivo è quello, non serve una riga
                        // "(Fine)" generica separata — gli si assegna la
                        // data/ora di fine del percorso.
                        int lastIdx = nestedPoints!.Count - 1;
                        nestedPoints[lastIdx] = nestedPoints[lastIdx] with { Date = route.EndDateTime };

                        entries.Add(new ItineraryEntry(
                            route.StartDateTime, route.Label, true, pStart.Lon, pStart.Lat,
                            route.ColorHex, PoiIconType.Pin, route.Description, nestedPoints));
                    }
                    else if (nestedPoints != null)
                    {
                        // Giorni diversi: l'ultimo POI marcato non può
                        // restare annidato sotto Inizio (finirebbe sotto
                        // l'intestazione del giorno sbagliato) — diventa
                        // lui stesso un'entry di primo livello, col proprio
                        // nome/icona/posizione, datata alla fine del
                        // percorso: compare così sotto l'intestazione del
                        // proprio giorno, restando comunque "l'ultimo POI"
                        // e non una riga generica "(Fine) NomePercorso".
                        int lastIdx = nestedPoints.Count - 1;
                        var lastPoint = nestedPoints[lastIdx];
                        var remainingNested = lastIdx > 0 ? nestedPoints.GetRange(0, lastIdx) : null;

                        entries.Add(new ItineraryEntry(
                            route.StartDateTime, route.Label, true, pStart.Lon, pStart.Lat,
                            route.ColorHex, PoiIconType.Pin, route.Description, remainingNested));

                        entries.Add(new ItineraryEntry(
                            route.EndDateTime, lastPoint.Label, null, lastPoint.Lon, lastPoint.Lat,
                            route.ColorHex, lastPoint.Icon, lastPoint.Description));
                    }
                    else
                    {
                        entries.Add(new ItineraryEntry(
                            route.StartDateTime, route.Label, true, pStart.Lon, pStart.Lat,
                            route.ColorHex, PoiIconType.Pin, route.Description, null));

                        var pEnd = route.Points[^1];
                        entries.Add(new ItineraryEntry(
                            route.EndDateTime, route.Label, false, pEnd.Lon, pEnd.Lat,
                            route.ColorHex, PoiIconType.Pin, null, null));
                    }
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
