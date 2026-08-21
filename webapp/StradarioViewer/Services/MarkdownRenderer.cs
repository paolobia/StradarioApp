// =============================================================================
// Services/MarkdownRenderer.cs
//
// SINOSSI: Rendering Markdown→HTML delle descrizioni per StradarioViewer —
//   qui il "ricco" è quasi gratis: il browser fa il lavoro (grassetto,
//   liste, titoli), basta produrre HTML sicuro. Controparte lato desktop
//   (StradarioApp, progetto NON referenziato da qui — vedi CLAUDE.md di
//   questa cartella, pipeline duplicata a mano): rendering ricco nel PDF
//   (Services/MarkdownPdfRenderer.cs) ed essenziale nei tooltip mappa
//   (Services/MarkdownPlainText.cs).
// =============================================================================

using System.Collections.Generic;
using System.Text.RegularExpressions;
using Markdig;

namespace StradarioViewer.Services
{
    public static class MarkdownRenderer
    {
        // UseSoftlineBreakAsHardlineBreak: stessa scelta del progetto
        // desktop — un "a capo" nella textarea di StradarioApp resta una
        // riga vera anche qui.
        // DisableHtml(): un file .stradario può arrivare da terzi — l'HTML
        // grezzo eventualmente presente nel testo Markdown non va MAI
        // eseguito nel DOM del browser (rischio XSS reale, non teorico).
        // UsePipeTables: stessa sintassi tabella riconosciuta dal PDF
        // (Services/MarkdownPdfRenderer.cs) — qui il rendering <table> è
        // gratis, lo fa il browser, basta lo stile in wwwroot/css/app.css.
        private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
            .UseSoftlineBreakAsHardlineBreak()
            .UsePipeTables()
            .DisableHtml()
            .Build();

        private static readonly Regex AnchorHrefPattern = new(@"<a href=""", RegexOptions.Compiled);

        // Markdig richiede una riga vuota prima di una tabella pipe se
        // questa segue direttamente un paragrafo di testo, altrimenti la
        // inghiotte nel paragrafo precedente e la mostra come testo grezzo
        // "| a | b |" — bug reale, molto facile da dimenticare scrivendo a
        // mano una descrizione. Inserisce la riga vuota mancante solo
        // davanti alla riga di INTESTAZIONE di una tabella (riconosciuta
        // dalla riga di separatori "|---|---|" subito dopo), MAI fra le
        // righe dati di una tabella già correttamente riconosciuta.
        // Stessa logica duplicata a mano in StradarioApp
        // (Services/MarkdownPreprocess.cs — progetto indipendente, nessuna
        // project reference fra i due).
        private static readonly Regex RowLike = new(@"^\s*\|?.*\|.*\|?\s*$", RegexOptions.Compiled);
        private static readonly Regex SeparatorRowLike =
            new(@"^\s*\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)+\|?\s*$", RegexOptions.Compiled);

        private static string EnsureBlankLineBeforeTables(string markdown)
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

        // HTML pronto per essere iniettato con @((MarkupString)...) — i link
        // aprono sempre in una nuova scheda: l'app è una PWA installata,
        // navigare via dalla pagina corrente perderebbe lo stato caricato.
        public static string ToHtml(string? markdown)
        {
            if (string.IsNullOrWhiteSpace(markdown)) return "";
            string html = Markdown.ToHtml(EnsureBlankLineBeforeTables(markdown), Pipeline);
            return AnchorHrefPattern.Replace(html, "<a target=\"_blank\" rel=\"noopener noreferrer\" href=\"");
        }
    }
}
