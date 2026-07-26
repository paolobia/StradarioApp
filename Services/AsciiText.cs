// =============================================================================
// Services/AsciiText.cs
//
// SINOSSI: Helper condivisi per tenere solo testo ASCII nei nomi/descrizioni
//   di POI e percorsi. Fonti non occidentali (Cina in primis, ma in generale
//   Asia/Medio Oriente/Russia...) spesso hanno "name"/"description" nello
//   script locale (es. caratteri cinesi): l'app non ha un IME per editarli e
//   mostrano solo quadratini nei font di sistema usati per l'UI. Regola
//   concordata con l'utente: SEMPRE preferire una variante già in ASCII
//   quando disponibile (name:it/name:en/int_name, con priorità alla lingua
//   correntemente selezionata nell'interfaccia), altrimenti ripulire il testo
//   trovato togliendo ogni carattere non ASCII piuttosto che mostrare testo
//   illeggibile. Usato sia dalla ricerca POI live (PoiSearchService, tag OSM
//   strutturati via Overpass) sia dall'import KML/GPX (PoiService/
//   PercorsoService, dove i tag extra sono spesso infilati come testo libero
//   dentro <description>, non elementi XML separati).
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using StradarioApp.Resources;

namespace StradarioApp.Services
{
    internal static class AsciiText
    {
        public static bool IsAscii(string s) => s.All(c => c <= 127);

        public static string StripNonAscii(string s) => new string(s.Where(c => c <= 127).ToArray());

        // Ripulisce un blocco di testo multi-riga (es. <description> KML)
        // togliendo i caratteri non ASCII riga per riga. Scarta le righe che
        // restano vuote dopo la pulizia, e per righe in forma "chiave=valore"
        // (tipico export OSM->KML che infila i tag come testo libero) scarta
        // anche quelle il cui valore resta vuoto dopo la pulizia (es.
        // "alt_name=北京北站" -> non "alt_name=" penzolante, sparisce del tutto).
        public static string SanitizeMultilineAscii(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;

            var kept = new List<string>();
            foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                string line = IsAscii(raw) ? raw : StripNonAscii(raw).Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;

                int eq = line.IndexOf('=');
                if (eq >= 0 && string.IsNullOrWhiteSpace(line.Substring(eq + 1))) continue;

                kept.Add(line);
            }
            return string.Join("\n", kept);
        }

        // Priorità delle chiavi "name:xx"/"int_name" in base alla lingua UI
        // corrente (Resources.Strings.CurrentLanguage).
        public static string[] NamePriorityKeys() =>
            Strings.CurrentLanguage == "en"
                ? new[] { "name:en", "name:it", "int_name", "name" }
                : new[] { "name:it", "name:en", "int_name", "name" };

        // Cerca in un blob di testo multi-riga (tipico export OSM->KML: righe
        // "chiave=valore" infilate nella description) la prima riga ASCII per
        // una lista di chiavi, in ordine di priorità.
        public static string? FindTaggedValue(string? text, string[] priorityKeys)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var lines = text.Replace("\r\n", "\n").Split('\n');
            foreach (var key in priorityKeys)
            {
                foreach (var line in lines)
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim();
                    if (!string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) continue;
                    string v = line.Substring(eq + 1).Trim();
                    if (!string.IsNullOrWhiteSpace(v) && IsAscii(v)) return v;
                }
            }
            return null;
        }

        // Sceglie un'etichetta leggibile: se rawLabel è già ASCII la tiene
        // com'è; altrimenti cerca una variante ASCII dentro descriptionBlob
        // (v. FindTaggedValue); altrimenti ripulisce rawLabel dai caratteri
        // non ASCII; se non resta nulla di utile, usa fallback.
        public static string PickAsciiLabel(string rawLabel, string? descriptionBlob, string fallback)
        {
            if (string.IsNullOrWhiteSpace(rawLabel)) return fallback;
            if (IsAscii(rawLabel)) return rawLabel;

            string? alt = FindTaggedValue(descriptionBlob, NamePriorityKeys());
            if (alt != null) return alt;

            string stripped = StripNonAscii(rawLabel).Trim();
            return string.IsNullOrWhiteSpace(stripped) ? fallback : stripped;
        }
    }
}
