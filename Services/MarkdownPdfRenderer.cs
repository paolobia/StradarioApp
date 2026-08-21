// =============================================================================
// Services/MarkdownPdfRenderer.cs
//
// SINOSSI: Rendering "ricco" di una Description Markdown nel PDF esportato
//   (Services/PdfGenerator.cs) — titoli, grassetto, corsivo, code inline,
//   liste (anche annidate), link cliccabili. PdfSharpCore/XGraphics non ha
//   un'API per disegnare testo con stili misti in un'unica chiamata (un solo
//   XFont per DrawString): questo file fa manualmente quello che un vero
//   motore di rich text farebbe — spezza l'AST Markdig in "righe" già
//   misurate (una lista di "run" text+XFont+url per riga), che il chiamante
//   disegna una alla volta dentro il proprio ciclo di paginazione ESISTENTE
//   (stesso schema di PdfGenerator.WrapText, di cui questo è il sostituto
//   per i soli campi Description — le etichette restano testo semplice).
//
//   Controparte "povera" per i tooltip mappa: Services/MarkdownPlainText.cs.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using PdfSharpCore.Drawing;

namespace StradarioApp.Services
{
    // Un segmento di testo con un font già risolto (stile derivato dal
    // Markdown: grassetto/corsivo/code) e, se è un link, l'URL di
    // destinazione — usato sia per disegnare sia per registrare l'area
    // cliccabile (page.AddWebLink) in PdfGenerator.
    public readonly struct MarkdownRun
    {
        public string Text { get; init; }
        public XFont  Font { get; init; }
        public string? Url { get; init; }
    }

    // Una riga di tabella: ogni cella porta le proprie righe già impaginate
    // (via LayoutBlock ricorsivo), con ColumnX/ColumnWidth assoluti rispetto
    // allo stesso `indent` usato dal resto del layout — disegnata come
    // un'unica unità (non spezzata fra pagine cella per cella: una
    // semplificazione accettabile per un campo descrizione, non un vero
    // motore di tabelle).
    public sealed class MarkdownTableRow
    {
        public List<List<MarkdownPdfLine>> Cells { get; init; } = new();
        public List<double> ColumnX      { get; init; } = new();
        public List<double> ColumnWidth  { get; init; } = new();
        public bool IsHeader { get; init; }
    }

    // Una riga già impaginata (word-wrap già risolto rispetto alla larghezza
    // massima passata a Layout): i Runs vanno disegnati in sequenza da
    // sinistra, IndentPt è lo scostamento orizzontale iniziale (liste
    // annidate), Height è l'altezza da usare per avanzare Y dopo averla
    // disegnata (varia: un titolo è più alto di un paragrafo normale).
    // TableRow non-null ⇒ riga di tabella: Runs resta vuoto, si dise gna
    // diversamente (v. DrawLine).
    public sealed class MarkdownPdfLine
    {
        public List<MarkdownRun> Runs { get; } = new();
        public double Height   { get; set; }
        public double IndentPt { get; set; }
        public MarkdownTableRow? TableRow { get; set; }
    }

    public static class MarkdownPdfRenderer
    {
        // UseSoftlineBreakAsHardlineBreak: stessa scelta di
        // MarkdownPlainText — un "a capo" nel TextBox resta una riga vera.
        // UsePipeTables: sintassi tabella GFM ("| a | b |"), altrimenti
        // Markdig la lascia come paragrafo di testo normale.
        private static readonly MarkdownPipeline Pipeline =
            new MarkdownPipelineBuilder().UseSoftlineBreakAsHardlineBreak().UsePipeTables().Build();

        private const double ListIndentPt   = 12;
        private const double LineHeightRatio = 1.35; // approssimazione riga/dimensione font, coerente con i rowH fissi già usati altrove in PdfGenerator

        private static readonly XPen    LinkPen   = new XPen(XColors.SteelBlue, 0.5);
        private static readonly XSolidBrush LinkBrush = new XSolidBrush(XColors.SteelBlue);
        private static readonly XPen    TableBorderPen  = new XPen(XColors.Gray, 0.5);
        private static readonly XSolidBrush TableHeaderFill = new XSolidBrush(XColor.FromArgb(30, 0, 0, 0));

        // Calcola l'impaginazione di `markdown` dentro `maxWidth`, usando
        // `baseFont` (famiglia+dimensione) come stile di partenza per
        // paragrafi/liste — titoli/grassetto/corsivo/code derivano da
        // quello. Ritorna "" (lista vuota) se non c'è testo, esattamente
        // come WrapText per una stringa vuota.
        public static List<MarkdownPdfLine> Layout(XGraphics gfx, string? markdown, XFont baseFont, double maxWidth)
        {
            var lines = new List<MarkdownPdfLine>();
            if (string.IsNullOrWhiteSpace(markdown)) return lines;

            var doc = Markdown.Parse(MarkdownPreprocess.EnsureBlankLineBeforeTables(markdown), Pipeline);
            foreach (var block in doc)
                LayoutBlock(gfx, block, baseFont, maxWidth, indent: 0, lines);
            return lines;
        }

        public static double TotalHeight(List<MarkdownPdfLine> lines) => lines.Sum(l => l.Height);

        // Disegna una singola riga già impaginata a partire da (x, y) — x è
        // il bordo sinistro della colonna, IndentPt viene sommato qui. Per
        // ogni run che è un link, disegna testo colorato+sottolineato e
        // invoca `onLink` con il rettangolo disegnato e l'URL: il chiamante
        // (PdfGenerator) lo usa per registrare `page.AddWebLink`.
        public static void DrawLine(XGraphics gfx, MarkdownPdfLine line, double x, double y, XBrush brush, Action<XRect, string>? onLink)
        {
            if (line.TableRow != null)
            {
                DrawTableRow(gfx, line.TableRow, x, y, line.Height, brush, onLink);
                return;
            }

            double cx = x + line.IndentPt;
            foreach (var run in line.Runs)
            {
                double w = gfx.MeasureString(run.Text, run.Font).Width;
                bool isLink = run.Url != null;
                gfx.DrawString(run.Text, run.Font, isLink ? LinkBrush : brush,
                    new XRect(cx, y, w, line.Height), XStringFormats.TopLeft);
                if (isLink)
                {
                    double uy = y + line.Height - 2;
                    gfx.DrawLine(LinkPen, cx, uy, cx + w, uy);
                    onLink?.Invoke(new XRect(cx, y, w, line.Height), run.Url!);
                }
                cx += w;
            }
        }

        // Disegna una riga di tabella: contorno di ogni cella, sfondo
        // leggero se intestazione, e le righe di testo già impaginate di
        // ciascuna cella (ricorre in DrawLine per il contenuto testuale —
        // MAI in una tabella, un motore di tabelle nidificate non serve qui).
        private static void DrawTableRow(XGraphics gfx, MarkdownTableRow row, double x, double y, double rowHeight, XBrush brush, Action<XRect, string>? onLink)
        {
            for (int c = 0; c < row.Cells.Count; c++)
            {
                double cellX = x + row.ColumnX[c];
                double cellW = row.ColumnWidth[c];
                if (row.IsHeader)
                    gfx.DrawRectangle(TableHeaderFill, cellX, y, cellW, rowHeight);
                gfx.DrawRectangle(TableBorderPen, cellX, y, cellW, rowHeight);

                double cy = y + 2;
                foreach (var cellLine in row.Cells[c])
                {
                    DrawLine(gfx, cellLine, x, cy, brush, onLink);
                    cy += cellLine.Height;
                }
            }
        }

        // CodeBlock/FencedCodeBlock: rese come testo monospace, non perse —
        // ThematicBreakBlock/HtmlBlock (nessun case sotto, ContainerBlock
        // generico non li intercetta) sono gli unici blocchi ignorati
        // (fuori scope esplicito, v. piano: HTML inline mai eseguito, una
        // riga orizzontale non aggiunge nulla in un campo descrizione).
        private static void LayoutBlock(XGraphics gfx, Block block, XFont baseFont, double maxWidth, double indent, List<MarkdownPdfLine> lines)
        {
            switch (block)
            {
                case HeadingBlock heading:
                {
                    double size = HeadingSize(baseFont.Size, heading.Level);
                    LayoutInline(gfx, heading.Inline, baseFont.FontFamily.Name, size, blockBold: true, maxWidth - indent, indent, lines, leadingMarker: null);
                    break;
                }

                case ParagraphBlock paragraph:
                    LayoutInline(gfx, paragraph.Inline, baseFont.FontFamily.Name, baseFont.Size, blockBold: false, maxWidth - indent, indent, lines, leadingMarker: null);
                    break;

                case ListBlock list:
                {
                    bool ordered = list.IsOrdered;
                    int ordinal = ordered && list.OrderedStart != null && int.TryParse(list.OrderedStart, out var start) ? start : 1;
                    foreach (var item in list)
                    {
                        if (item is not ListItemBlock listItem) continue;
                        string marker = ordered ? $"{ordinal}." : "•";
                        ordinal++;
                        double itemIndent = indent + ListIndentPt;
                        bool first = true;
                        foreach (var itemBlock in listItem)
                        {
                            if (first && itemBlock is ParagraphBlock p)
                                LayoutInline(gfx, p.Inline, baseFont.FontFamily.Name, baseFont.Size, blockBold: false, maxWidth - itemIndent, itemIndent, lines, leadingMarker: marker + " ");
                            else
                                LayoutBlock(gfx, itemBlock, baseFont, maxWidth, itemIndent, lines);
                            first = false;
                        }
                    }
                    break;
                }

                case Table table:
                    LayoutTable(gfx, table, baseFont, maxWidth, indent, lines);
                    break;

                case FencedCodeBlock or CodeBlock:
                {
                    var codeBlock = (CodeBlock)block;
                    var codeFont = new XFont("Courier New", baseFont.Size, XFontStyle.Regular);
                    foreach (var codeLine in codeBlock.Lines.Lines)
                    {
                        string text = codeLine.Slice.ToString();
                        LayoutPlainText(gfx, text, codeFont, maxWidth - indent, indent, lines);
                    }
                    break;
                }

                case QuoteBlock quote:
                    foreach (var qb in quote)
                        LayoutBlock(gfx, qb, baseFont, maxWidth, indent + ListIndentPt, lines);
                    break;

                case ContainerBlock container:
                    foreach (var child in container)
                        LayoutBlock(gfx, child, baseFont, maxWidth, indent, lines);
                    break;
            }
        }

        // Tabella "basic": colonne a larghezza uguale (non calcolata sul
        // contenuto, un vero motore di tabelle è fuori scope per un campo
        // descrizione), ogni riga disegnata come unità unica non spezzabile
        // fra pagine (v. MarkdownTableRow). L'intestazione (TableRow.
        // IsHeader) usa il font in grassetto.
        private static void LayoutTable(XGraphics gfx, Table table, XFont baseFont, double maxWidth, double indent, List<MarkdownPdfLine> lines)
        {
            // Il conteggio celle REALE delle righe, non table.ColumnDefinitions
            // — verificato che quest'ultimo può contare una colonna fantasma
            // in più (es. 4 invece di 3 per "| A | B | C |"), che riservava
            // spazio per una colonna mai disegnata: la tabella non arrivava
            // mai al 100% della larghezza disponibile.
            int colCount = 0;
            foreach (var rowObj in table)
                if (rowObj is TableRow r)
                    colCount = Math.Max(colCount, r.Count);
            if (colCount == 0) return;

            double available = maxWidth - indent;
            double colW = available / colCount;
            var colX = new List<double>();
            var colWs = new List<double>();
            for (int c = 0; c < colCount; c++)
            {
                colX.Add(indent + c * colW);
                colWs.Add(colW);
            }

            foreach (var rowObj in table)
            {
                if (rowObj is not TableRow row) continue;
                var headerFont = row.IsHeader
                    ? new XFont(baseFont.FontFamily.Name, baseFont.Size, XFontStyle.Bold)
                    : baseFont;

                var cellsLines = new List<List<MarkdownPdfLine>>();
                double rowHeight = 0;
                int ci = 0;
                foreach (var cellObj in row)
                {
                    if (ci >= colCount) break;
                    var cellLines = new List<MarkdownPdfLine>();
                    if (cellObj is TableCell cell)
                        foreach (var cellBlock in cell)
                            LayoutBlock(gfx, cellBlock, headerFont, colX[ci] + colWs[ci] - 4, colX[ci] + 2, cellLines);
                    rowHeight = Math.Max(rowHeight, TotalHeight(cellLines));
                    cellsLines.Add(cellLines);
                    ci++;
                }
                rowHeight = Math.Max(rowHeight, baseFont.Size * LineHeightRatio) + 4;

                lines.Add(new MarkdownPdfLine
                {
                    Height = rowHeight,
                    TableRow = new MarkdownTableRow
                    {
                        Cells = cellsLines,
                        ColumnX = colX,
                        ColumnWidth = colWs,
                        IsHeader = row.IsHeader,
                    },
                });
            }
        }

        private static double HeadingSize(double baseSize, int level) => level switch
        {
            1 => baseSize + 6,
            2 => baseSize + 4,
            3 => baseSize + 3,
            4 => baseSize + 2,
            5 => baseSize + 1,
            _ => baseSize,
        };

        // AttachToPrevious: vero se nel Markdown sorgente non c'era uno
        // spazio prima di questo token (es. una virgola subito dopo un
        // *corsivo*, che Markdig separa in due Inline distinti pur non
        // essendoci spazio fra loro) — senza questo flag ogni confine fra
        // due Inline riceveva uno spazio spurio, anche quando non c'era nel
        // testo originale (visto renderizzando un PDF di prova: "corsivo ,"
        // invece di "corsivo,").
        private readonly record struct InlineToken(string Text, bool Bold, bool Italic, bool Code, string? Url, bool ForceBreakBefore, bool AttachToPrevious = false);

        // Cammina l'albero inline Markdig (grassetto/corsivo annidabili,
        // link, code) e lo appiattisce in una sequenza di "parole" con lo
        // stile cumulativo attivo in quel punto — un LineBreakInline (a capo
        // manuale, hardline per via della pipeline) marca la parola
        // successiva come inizio di una nuova riga forzata.
        private static List<InlineToken> Tokenize(Inline? inline, bool bold, bool italic)
        {
            var tokens = new List<InlineToken>();
            bool forceBreak = false;
            // Vero se l'ultimo carattere emesso finora era spazio (o non
            // c'è ancora nulla, inizio testo) — i nodi Inline di Markdig
            // sono slice contigue del sorgente: se NÉ la fine del nodo
            // precedente NÉ l'inizio di questo hanno uno spazio, nel
            // sorgente non c'era alcuno spazio fra loro (es. "*corsivo*,"
            // — la virgola va attaccata, non staccata da uno spazio spurio).
            bool prevEndedWithSpace = true;

            void Walk(Inline? node, bool b, bool i, string? url)
            {
                for (var cur = node; cur != null; cur = cur.NextSibling)
                {
                    switch (cur)
                    {
                        case LiteralInline lit:
                        {
                            string litText = lit.Content.ToString();
                            if (litText.Length == 0) break;
                            bool startsWithSpace = char.IsWhiteSpace(litText[0]);
                            bool firstWord = true;
                            foreach (var word in litText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                            {
                                bool attach = firstWord && !prevEndedWithSpace && !startsWithSpace;
                                tokens.Add(new InlineToken(word, b, i, false, url, forceBreak, attach));
                                forceBreak = false;
                                firstWord = false;
                            }
                            prevEndedWithSpace = char.IsWhiteSpace(litText[^1]);
                            break;
                        }
                        case CodeInline code:
                        {
                            bool attach = !prevEndedWithSpace;
                            tokens.Add(new InlineToken(code.Content, b, i, true, url, forceBreak, attach));
                            forceBreak = false;
                            prevEndedWithSpace = false;
                            break;
                        }
                        case LineBreakInline:
                            forceBreak = true;
                            prevEndedWithSpace = true;
                            break;
                        case EmphasisInline emphasis:
                            bool strong = emphasis.DelimiterCount >= 2;
                            Walk(emphasis.FirstChild, b || strong, i || !strong, url);
                            break;
                        case LinkInline link:
                            Walk(link.FirstChild, b, i, link.Url);
                            break;
                        case ContainerInline container:
                            Walk(container.FirstChild, b, i, url);
                            break;
                    }
                }
            }

            Walk(inline, bold, italic, null);
            return tokens;
        }

        private static XFont GetFont(string fontFamily, double size, bool bold, bool italic, bool code)
        {
            if (code) return new XFont("Courier New", size, XFontStyle.Regular);
            var style = XFontStyle.Regular;
            if (bold)   style |= XFontStyle.Bold;
            if (italic) style |= XFontStyle.Italic;
            return new XFont(fontFamily, size, style);
        }

        // Word-wrap greedy sui token già tokenizzati, un run per parola (con
        // lo spazio iniziale già incluso nel testo tranne che a inizio riga)
        // — più semplice e robusto che provare a fondere parole consecutive
        // dello stesso stile in un unico run, al costo di qualche run in
        // più: irrilevante per testo lungo quanto una Description.
        private static void LayoutInline(XGraphics gfx, Inline? inline, string fontFamily, double size, bool blockBold, double maxWidth, double indent, List<MarkdownPdfLine> lines, string? leadingMarker)
        {
            var tokens = new List<InlineToken>();
            if (leadingMarker != null)
                tokens.Add(new InlineToken(leadingMarker, false, false, false, null, false));
            tokens.AddRange(Tokenize(inline, blockBold, false));
            if (tokens.Count == 0) return;

            var current = new MarkdownPdfLine { IndentPt = indent };
            double currentWidth = 0;
            bool lineHasContent = false;

            void FlushLine()
            {
                current.Height = current.Runs.Count == 0
                    ? size * LineHeightRatio
                    : current.Runs.Max(r => r.Font.Size) * LineHeightRatio;
                lines.Add(current);
                current = new MarkdownPdfLine { IndentPt = indent };
                currentWidth = 0;
                lineHasContent = false;
            }

            foreach (var token in tokens)
            {
                if (token.ForceBreakBefore && lineHasContent)
                    FlushLine();

                var font = GetFont(fontFamily, size, token.Bold, token.Italic, token.Code);
                bool needsSpace = lineHasContent && !token.AttachToPrevious;
                string text = needsSpace ? " " + token.Text : token.Text;
                double w = gfx.MeasureString(text, font).Width;

                if (lineHasContent && currentWidth + w > maxWidth)
                {
                    FlushLine();
                    text = token.Text;
                    w = gfx.MeasureString(text, font).Width;
                }

                current.Runs.Add(new MarkdownRun { Text = text, Font = font, Url = token.Url });
                currentWidth += w;
                lineHasContent = true;
            }
            FlushLine();
        }

        // Come LayoutInline, ma per una stringa semplice con un solo stile
        // fisso — usato per le righe di un blocco di codice.
        private static void LayoutPlainText(XGraphics gfx, string text, XFont font, double maxWidth, double indent, List<MarkdownPdfLine> lines)
        {
            double lineHeight = font.Size * LineHeightRatio;
            if (text.Length == 0)
            {
                lines.Add(new MarkdownPdfLine { IndentPt = indent, Height = lineHeight });
                return;
            }

            var current = new MarkdownPdfLine { IndentPt = indent };
            double currentWidth = 0;
            bool hasContent = false;

            void Flush()
            {
                current.Height = lineHeight;
                lines.Add(current);
                current = new MarkdownPdfLine { IndentPt = indent };
                currentWidth = 0;
                hasContent = false;
            }

            foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string piece = hasContent ? " " + word : word;
                double w = gfx.MeasureString(piece, font).Width;
                if (hasContent && currentWidth + w > maxWidth)
                {
                    Flush();
                    piece = word;
                    w = gfx.MeasureString(piece, font).Width;
                }
                current.Runs.Add(new MarkdownRun { Text = piece, Font = font, Url = null });
                currentWidth += w;
                hasContent = true;
            }
            Flush();
        }
    }
}
