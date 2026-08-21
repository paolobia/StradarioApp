// =============================================================================
// Services/MarkdownPreprocess.cs
//
// SINOSSI: Normalizzazione del sorgente Markdown prima del parsing Markdig,
//   condivisa da MarkdownPdfRenderer e MarkdownPlainText — StradarioViewer
//   (progetto indipendente, nessuna project reference) ha la propria copia
//   in Services/MarkdownRenderer.cs.
// =============================================================================

using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace StradarioApp.Services
{
    internal static class MarkdownPreprocess
    {
        // Markdig richiede una riga vuota prima di una tabella pipe se
        // questa segue direttamente un paragrafo di testo (verificato:
        // senza riga vuota l'intera tabella viene inghiottita nel paragrafo
        // precedente e mostrata come testo grezzo "| a | b |" invece che
        // renderizzata — bug reale segnalato dall'utente, capito da lui
        // stesso: "forse la tabella non viene perché va a capo?"). Molto
        // facile da dimenticare scrivendo a mano una descrizione. Inserisce
        // automaticamente la riga vuota mancante solo davanti alla riga di
        // INTESTAZIONE di una tabella (riconosciuta dalla riga di separatori
        // "|---|---|" subito dopo), MAI fra le righe dati di una tabella già
        // correttamente riconosciuta — altrimenti la spezzerebbe in tante
        // tabelle di una riga.
        private static readonly Regex RowLike = new(@"^\s*\|?.*\|.*\|?\s*$", RegexOptions.Compiled);
        private static readonly Regex SeparatorRowLike =
            new(@"^\s*\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)+\|?\s*$", RegexOptions.Compiled);

        public static string EnsureBlankLineBeforeTables(string markdown)
        {
            var lines = markdown.Replace("\r\n", "\n").Split('\n');
            var result = new List<string>(lines.Length + 4);
            for (int i = 0; i < lines.Length; i++)
            {
                bool isHeaderStart = i + 1 < lines.Length
                    && RowLike.IsMatch(lines[i])
                    && SeparatorRowLike.IsMatch(lines[i + 1])
                    && result.Count > 0
                    && result[^1].Trim().Length > 0;
                if (isHeaderStart)
                    result.Add("");
                result.Add(lines[i]);
            }
            return string.Join("\n", result);
        }
    }
}
