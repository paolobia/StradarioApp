// =============================================================================
// Services/MarkdownPlainText.cs
//
// SINOSSI: Appiattisce una Description Markdown in testo semplice per i
//   tooltip sulla mappa (UI/MainWindow.cs) — rendering volutamente "povero":
//   nessun grassetto/corsivo/titolo reso graficamente, solo testo pulito,
//   MAI i marcatori Markdown letterali (**, *, #, [..](..)) in vista. Il
//   tooltip disegna già le sue righe con un solo SKPaint per chiamata; un
//   vero layout ricco lì non è stato richiesto — quello vive nel PDF, vedi
//   Services/MarkdownPdfRenderer.cs.
// =============================================================================

using System.Collections.Generic;
using System.Linq;
using System.Text;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace StradarioApp.Services
{
    public static class MarkdownPlainText
    {
        // UseSoftlineBreakAsHardlineBreak: un singolo "a capo" premuto
        // dall'utente nel TextBox multilinea (Invio) resta una riga vera
        // invece di essere unito alla successiva come da spec CommonMark
        // (dove un solo \n dentro un paragrafo è solo uno spazio) — coerente
        // con l'aspettativa "Invio = vai a capo" che l'app aveva già prima
        // di introdurre Markdown. UsePipeTables: stessa sintassi tabella
        // riconosciuta dal PDF (Services/MarkdownPdfRenderer.cs).
        private static readonly MarkdownPipeline Pipeline =
            new MarkdownPipelineBuilder().UseSoftlineBreakAsHardlineBreak().UsePipeTables().Build();

        // Un tooltip è un riquadro piccolo sopra la mappa, non un pannello a
        // scorrimento: una descrizione enorme (un Markdown "cattivo" può
        // essere lunghissimo) lo farebbe crescere a dismisura, coprendo la
        // mappa stessa — troncata con un indicatore esplicito.
        private const int MaxLines = 14;

        // Righe di testo semplice, una per riga logica (titoli/voci di lista/
        // interruzioni di riga) — NON ancora spezzate per larghezza: il
        // chiamante applica ancora il proprio WrapText(line, maxChars) come
        // faceva prima su ogni riga di ".Split('\n')".
        // `cap`: tronca a MaxLines righe con un indicatore esplicito — pensato
        // per il tooltip hover (riquadro piccolo sopra la mappa, v. sopra);
        // `false` per contesti con più spazio reale (es. UI/PoiDetailWindow,
        // una finestra ridimensionabile pensata apposta per leggere tutto).
        public static List<string> Flatten(string? markdown, bool cap = true)
        {
            var lines = new List<string>();
            if (string.IsNullOrWhiteSpace(markdown)) return lines;

            var doc = Markdown.Parse(MarkdownPreprocess.EnsureBlankLineBeforeTables(markdown), Pipeline);
            foreach (var block in doc)
                FlattenBlock(block, lines);

            if (cap && lines.Count > MaxLines)
            {
                lines = lines.Take(MaxLines).ToList();
                lines.Add("… (continua nel PDF)");
            }
            return lines;
        }

        // CodeBlock/FencedCodeBlock/ThematicBreakBlock/HtmlBlock non compaiono
        // in nessun case sotto e sono quindi ignorati volutamente nel
        // tooltip "povero" — codice multi-riga e HTML grezzo non hanno senso
        // in un riquadro di poche righe. Table: una riga di testo per riga
        // di tabella, celle unite con " | " (nessuna griglia vera, coerente
        // col resto — "pessimo" va bene qui).
        private static void FlattenBlock(Block block, List<string> lines, string prefix = "")
        {
            switch (block)
            {
                case HeadingBlock heading:
                    lines.Add(prefix + FlattenInlineText(heading.Inline));
                    break;

                case ParagraphBlock paragraph:
                {
                    var parts = SplitOnBreaks(paragraph.Inline);
                    for (int i = 0; i < parts.Count; i++)
                        lines.Add((i == 0 ? prefix : "") + parts[i]);
                    break;
                }

                case ListBlock list:
                    foreach (var item in list)
                        if (item is ListItemBlock listItem)
                        {
                            bool first = true;
                            foreach (var itemBlock in listItem)
                            {
                                FlattenBlock(itemBlock, lines, first ? "• " : "");
                                first = false;
                            }
                        }
                    break;

                case QuoteBlock quote:
                    foreach (var quoteBlock in quote)
                        FlattenBlock(quoteBlock, lines, prefix);
                    break;

                case Table table:
                    foreach (var rowObj in table)
                        if (rowObj is TableRow row)
                        {
                            var cellTexts = new List<string>();
                            foreach (var cellObj in row)
                                if (cellObj is TableCell cell)
                                {
                                    var cellLines = new List<string>();
                                    foreach (var cellBlock in cell)
                                        FlattenBlock(cellBlock, cellLines);
                                    cellTexts.Add(string.Join(" ", cellLines));
                                }
                            lines.Add(prefix + string.Join(" | ", cellTexts));
                        }
                    break;

                case ContainerBlock container:
                    foreach (var child in container)
                        FlattenBlock(child, lines, prefix);
                    break;
            }
        }

        private static string FlattenInlineText(Inline? inline)
        {
            var sb = new StringBuilder();
            AppendInlineText(inline, sb);
            return sb.ToString();
        }

        private static void AppendInlineText(Inline? inline, StringBuilder sb)
        {
            for (var current = inline; current != null; current = current.NextSibling)
            {
                switch (current)
                {
                    case LiteralInline literal:
                        sb.Append(literal.Content.ToString());
                        break;
                    case CodeInline code:
                        sb.Append(code.Content);
                        break;
                    case LineBreakInline:
                        sb.Append(' ');
                        break;
                    case LinkInline link:
                        AppendInlineText(link.FirstChild, sb); // solo il testo del link, mai l'URL
                        break;
                    case ContainerInline container:
                        AppendInlineText(container.FirstChild, sb);
                        break;
                }
            }
        }

        // Come AppendInlineText, ma spezza una nuova riga a ogni
        // LineBreakInline (paragrafo con "a capo" interni) invece di
        // appiattire tutto in una singola stringa.
        private static List<string> SplitOnBreaks(Inline? inline)
        {
            var result = new List<string>();
            var sb = new StringBuilder();

            void Flush()
            {
                result.Add(sb.ToString());
                sb.Clear();
            }

            void Walk(Inline? node)
            {
                for (var current = node; current != null; current = current.NextSibling)
                {
                    switch (current)
                    {
                        case LiteralInline literal:
                            sb.Append(literal.Content.ToString());
                            break;
                        case CodeInline code:
                            sb.Append(code.Content);
                            break;
                        case LineBreakInline:
                            Flush();
                            break;
                        case LinkInline link:
                            Walk(link.FirstChild);
                            break;
                        case ContainerInline container:
                            Walk(container.FirstChild);
                            break;
                    }
                }
            }

            Walk(inline);
            Flush();
            return result;
        }
    }
}
