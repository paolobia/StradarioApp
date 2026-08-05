// =============================================================================
// Services/PoiIconSuggestion.cs
//
// SINOSSI: Suggerisce automaticamente un'icona POI (PoiIconType) a partire
//   da parole chiave (italiano + inglese) trovate in etichetta/descrizione.
//   Condiviso fra UI/RouteEditWindow (etichetta/descrizione digitate a mano)
//   e MainWindow.ReconcileImportedPoiWithRoutes (etichette che arrivano da
//   un import KML/GPX/CSV, dove non scatta alcun evento di UI) — un punto
//   importato deve ricevere l'icona suggerita SUBITO, non solo se l'utente
//   riapre il dialog e tocca i campi di testo.
// =============================================================================

using System;
using System.Linq;
using StradarioApp.Models;

namespace StradarioApp.Services
{
    public static class PoiIconSuggestion
    {
        // Parole chiave (italiano + inglese, minuscolo) associate a ciascuna
        // icona POI: la prima che compare come sottostringa di etichetta+
        // descrizione (in quest'ordine, dal più specifico al più generico)
        // vince. Copre solo le 16 icone disponibili in PoiIcons.All — per
        // concetti senza un'icona dedicata (es. moschea/tempio, museo/forte)
        // si sceglie l'icona disponibile concettualmente più vicina
        // (rispettivamente Church e Monument).
        private static readonly (PoiIconType Icon, string[] Keywords)[] Keywords =
        {
            (PoiIconType.Hospital,   new[] { "ospedale", "clinica", "pronto soccorso", "farmacia", "hospital", "clinic", "emergency", "pharmacy" }),
            (PoiIconType.Hotel,      new[] { "hotel", "albergo", "resort", "ostello", "lodge", "lodging", "hostel", "b&b", "bed and breakfast", "guesthouse" }),
            (PoiIconType.Restaurant, new[] { "ristorante", "trattoria", "osteria", "pizzeria", "restaurant", "dining", "diner" }),
            (PoiIconType.Cafe,       new[] { "caffè", "caffetteria", "bar", "cafe", "coffee", "bistro" }),
            (PoiIconType.Church,     new[] { "chiesa", "cattedrale", "basilica", "duomo", "abbazia", "santuario", "moschea", "tempio", "church", "cathedral", "mosque", "temple", "shrine", "abbey" }),
            (PoiIconType.Monument,   new[] { "monumento", "statua", "memoriale", "museo", "forte", "fortezza", "castello", "rovine", "monument", "museum", "memorial", "statue", "fort", "fortress", "castle", "ruins", "landmark" }),
            (PoiIconType.Viewpoint,  new[] { "belvedere", "panorama", "punto panoramico", "corniche", "viewpoint", "lookout", "scenic" }),
            (PoiIconType.Camping,    new[] { "campeggio", "camping", "campground" }),
            (PoiIconType.Fountain,   new[] { "fontana", "fountain" }),
            (PoiIconType.Parking,    new[] { "parcheggio", "garage", "parking" }),
            (PoiIconType.Shop,       new[] { "negozio", "mercato", "mercatino", "souk", "bazar", "centro commerciale", "shop", "store", "market", "mall", "boutique" }),
            (PoiIconType.Home,       new[] { "abitazione", "residenza", "casa", "villa", "home", "house", "residence" }),
            (PoiIconType.Flag,       new[] { "bandiera", "confine", "frontiera", "flag", "border" }),
            (PoiIconType.Info,       new[] { "informazioni", "ufficio turistico", "info point", "information", "tourist office" }),
        };

        // Ritorna true (con "icon" valorizzata) se una parola chiave compare
        // in label/description, false altrimenti (icon = Pin, il default).
        public static bool TrySuggest(string? label, string? description, out PoiIconType icon)
        {
            string haystack = $"{label} {description}".ToLowerInvariant();
            foreach (var (candidate, keywords) in Keywords)
            {
                if (!keywords.Any(k => haystack.Contains(k, StringComparison.Ordinal))) continue;
                icon = candidate;
                return true;
            }
            icon = PoiIconType.Pin;
            return false;
        }
    }
}
