// =============================================================================
// Resources/Strings.cs
//
// SINOSSI: Punto di accesso unico alle stringhe UI. Fase 2 dell'i18n:
//   supporta "it" (ItStrings.Map, default) e "en" (EnStrings.Map).
//   CurrentLanguage riflette sempre l'ultima lingua impostata con
//   SetLanguage — usato da SettingsWindow per preselezionare il combo lingua
//   senza dover rileggere le preferenze salvate.
// =============================================================================

using System.Collections.Generic;

namespace StradarioApp.Resources
{
    internal static class Strings
    {
        private static Dictionary<string, string> _current = ItStrings.Map;

        public static string CurrentLanguage { get; private set; } = "it";

        /// <summary>
        /// Restituisce il testo associato alla chiave nella lingua corrente,
        /// oppure la chiave stessa se non trovata (fallback visibile per
        /// individuare rapidamente chiavi mancanti/errate).
        /// </summary>
        public static string Get(string key)
        {
            return _current.TryGetValue(key, out var value) ? value : key;
        }

        // Lingue supportate: "it" (default) e "en". Un valore sconosciuto
        // ricade su "it" invece di lasciare la lingua precedente, così il
        // comportamento è deterministico anche con una preferenza corrotta.
        public static void SetLanguage(string lang)
        {
            switch (lang)
            {
                case "en":
                    _current = EnStrings.Map;
                    CurrentLanguage = "en";
                    break;
                case "it":
                default:
                    _current = ItStrings.Map;
                    CurrentLanguage = "it";
                    break;
            }
        }
    }
}
