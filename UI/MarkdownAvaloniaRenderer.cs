// =============================================================================
// UI/MarkdownAvaloniaRenderer.cs
//
// SINOSSI: Rendering "ricco" di una Description Markdown come controlli
//   Avalonia veri (TextBlock con Inlines in grassetto/corsivo, liste, e una
//   vera Grid per le tabelle) — usato da UI/PoiDetailWindow, la finestra di
//   dettaglio apribile con un clic sulla mappa (a differenza del tooltip
//   hover, che resta "povero" via Services/MarkdownPlainText: qui c'è una
//   vera finestra ridimensionabile, non un riquadro fugace, quindi ha senso
//   mostrare anche le tabelle come griglia reale invece che testo
//   appiattito "a | b | c" — richiesta esplicita dell'utente dopo aver
//   visto che il PDF le rende ma il pannello no).
//
//   A differenza di Services/MarkdownPdfRenderer.cs (che deve fare da sé il
//   word-wrap perché XGraphics disegna testo con un solo font per chiamata),
//   qui non serve: Avalonia TextBlock avvolge da solo le righe di Inlines,
//   quindi non c'è bisogno di un motore di layout manuale — solo di
//   costruire gli Inline giusti (Run con FontWeight/FontStyle) e lasciare
//   che il framework faccia il resto.
// =============================================================================

using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using StradarioApp.Services;
using MdInline = Markdig.Syntax.Inlines.Inline;

namespace StradarioApp.UI
{
    internal static class MarkdownAvaloniaRenderer
    {
        // Stessa pipeline (softline→hardline, tabelle pipe GFM) usata dal
        // PDF e dal tooltip — v. Services/MarkdownPdfRenderer.cs.
        private static readonly MarkdownPipeline Pipeline =
            new MarkdownPipelineBuilder().UseSoftlineBreakAsHardlineBreak().UsePipeTables().Build();

        private const double ListIndent = 16;

        public static Control Render(string? markdown, double baseFontSize = 13)
        {
            var root = new StackPanel { Spacing = 6 };
            if (string.IsNullOrWhiteSpace(markdown)) return root;

            var doc = Markdown.Parse(MarkdownPreprocess.EnsureBlankLineBeforeTables(markdown), Pipeline);
            foreach (var block in doc)
                RenderBlock(block, root, baseFontSize, indent: 0);
            return root;
        }

        // ThematicBreakBlock/HtmlBlock: nessun case sotto, ignorati (stesso
        // scope del PDF — v. MarkdownPdfRenderer.LayoutBlock).
        private static void RenderBlock(Block block, StackPanel container, double baseFontSize, double indent)
        {
            switch (block)
            {
                case HeadingBlock heading:
                {
                    var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Avalonia.Thickness(indent, 0, 0, 0) };
                    AppendInlines(tb.Inlines!, heading.Inline, bold: true, italic: false, HeadingSize(baseFontSize, heading.Level));
                    container.Children.Add(tb);
                    break;
                }

                case ParagraphBlock paragraph:
                {
                    var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = baseFontSize, Margin = new Avalonia.Thickness(indent, 0, 0, 0) };
                    AppendInlines(tb.Inlines!, paragraph.Inline, bold: false, italic: false, baseFontSize);
                    container.Children.Add(tb);
                    break;
                }

                case ListBlock list:
                {
                    bool ordered = list.IsOrdered;
                    int ordinal = ordered && list.OrderedStart != null && int.TryParse(list.OrderedStart, out var start) ? start : 1;
                    foreach (var itemObj in list)
                    {
                        if (itemObj is not ListItemBlock listItem) continue;
                        string marker = ordered ? $"{ordinal}." : "•";
                        ordinal++;

                        var row = new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 6,
                            Margin = new Avalonia.Thickness(indent + ListIndent, 0, 0, 0),
                        };
                        row.Children.Add(new TextBlock { Text = marker, FontSize = baseFontSize });

                        var itemPanel = new StackPanel { Spacing = 4 };
                        bool first = true;
                        foreach (var itemBlock in listItem)
                        {
                            if (first && itemBlock is ParagraphBlock p)
                            {
                                var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = baseFontSize };
                                AppendInlines(tb.Inlines!, p.Inline, bold: false, italic: false, baseFontSize);
                                itemPanel.Children.Add(tb);
                            }
                            else
                            {
                                RenderBlock(itemBlock, itemPanel, baseFontSize, indent: 0);
                            }
                            first = false;
                        }
                        row.Children.Add(itemPanel);
                        container.Children.Add(row);
                    }
                    break;
                }

                case Table table:
                    container.Children.Add(RenderTable(table, baseFontSize, indent));
                    break;

                case FencedCodeBlock or CodeBlock:
                {
                    var codeBlock = (CodeBlock)block;
                    string text = string.Join("\n", codeBlock.Lines.Lines.Select(l => l.Slice.ToString()));
                    container.Children.Add(new Border
                    {
                        Background       = new SolidColorBrush(Color.FromArgb(20, 0, 0, 0)),
                        Padding          = new Avalonia.Thickness(6),
                        Margin           = new Avalonia.Thickness(indent, 0, 0, 0),
                        CornerRadius     = new Avalonia.CornerRadius(3),
                        Child = new TextBlock
                        {
                            Text         = text,
                            FontFamily   = new FontFamily("Courier New"),
                            FontSize     = baseFontSize,
                            TextWrapping = TextWrapping.Wrap,
                        },
                    });
                    break;
                }

                case QuoteBlock quote:
                    foreach (var qb in quote)
                        RenderBlock(qb, container, baseFontSize, indent + ListIndent);
                    break;

                case ContainerBlock cb:
                    foreach (var child in cb)
                        RenderBlock(child, container, baseFontSize, indent);
                    break;
            }
        }

        // Colonne a larghezza uguale (Star), stesso approccio "basic" del
        // PDF (v. MarkdownPdfRenderer.LayoutTable) — qui però ogni cella è
        // un vero Control Avalonia dentro una Grid reale, con bordo e
        // sfondo leggero sull'intestazione.
        private static Control RenderTable(Table table, double baseFontSize, double indent)
        {
            int colCount = 0;
            foreach (var rowObj in table)
                if (rowObj is TableRow r) colCount = Math.Max(colCount, r.Count);
            if (colCount == 0) return new TextBlock();

            var grid = new Grid { Margin = new Avalonia.Thickness(indent, 0, 0, 0) };
            for (int c = 0; c < colCount; c++)
                grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

            int rowIndex = 0;
            foreach (var rowObj in table)
            {
                if (rowObj is not TableRow row) continue;
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

                int colIndex = 0;
                foreach (var cellObj in row)
                {
                    if (colIndex >= colCount) break;

                    var cellPanel = new StackPanel { Spacing = 2 };
                    if (cellObj is TableCell cell)
                        foreach (var cellBlock in cell)
                            RenderBlock(cellBlock, cellPanel, baseFontSize, indent: 0);

                    var cellBorder = new Border
                    {
                        BorderBrush     = Brushes.Gray,
                        BorderThickness = new Avalonia.Thickness(0.5),
                        Padding         = new Avalonia.Thickness(6, 3),
                        Background      = row.IsHeader ? new SolidColorBrush(Color.FromArgb(25, 0, 0, 0)) : Brushes.Transparent,
                        Child           = cellPanel,
                    };
                    Grid.SetRow(cellBorder, rowIndex);
                    Grid.SetColumn(cellBorder, colIndex);
                    grid.Children.Add(cellBorder);
                    colIndex++;
                }
                rowIndex++;
            }
            return grid;
        }

        // Cammina l'albero inline Markdig aggiungendo Run (e LineBreak per
        // gli a-capo manuali) direttamente a InlineCollection — nessun
        // word-wrap manuale: TextBlock lo fa da sé. I link sono resi in
        // blu sottolineato ma non cliccabili (fuori scope: Avalonia non ha
        // un Inline "Hyperlink" nativo pronto all'uso).
        private static void AppendInlines(InlineCollection inlines, MdInline? inline, bool bold, bool italic, double size)
        {
            for (var cur = inline; cur != null; cur = cur.NextSibling)
            {
                switch (cur)
                {
                    case LiteralInline lit:
                        inlines.Add(MakeRun(lit.Content.ToString(), bold, italic, code: false, size));
                        break;
                    case CodeInline code:
                        inlines.Add(MakeRun(code.Content, bold, italic, code: true, size));
                        break;
                    case LineBreakInline:
                        inlines.Add(new LineBreak());
                        break;
                    case EmphasisInline emphasis:
                    {
                        bool strong = emphasis.DelimiterCount >= 2;
                        AppendInlines(inlines, emphasis.FirstChild, bold || strong, italic || !strong, size);
                        break;
                    }
                    case LinkInline link:
                    {
                        int before = inlines.Count;
                        AppendInlines(inlines, link.FirstChild, bold, italic, size);
                        for (int i = before; i < inlines.Count; i++)
                            if (inlines[i] is Run r)
                            {
                                r.Foreground      = Brushes.SteelBlue;
                                r.TextDecorations = Avalonia.Media.TextDecorations.Underline;
                            }
                        break;
                    }
                    case ContainerInline container:
                        AppendInlines(inlines, container.FirstChild, bold, italic, size);
                        break;
                }
            }
        }

        private static Run MakeRun(string text, bool bold, bool italic, bool code, double size)
        {
            var run = new Run(text) { FontSize = size };
            if (code)   run.FontFamily = new FontFamily("Courier New");
            if (bold)   run.FontWeight = FontWeight.Bold;
            if (italic) run.FontStyle  = FontStyle.Italic;
            return run;
        }

        private static double HeadingSize(double baseSize, int level) => level switch
        {
            1 => baseSize + 8,
            2 => baseSize + 5,
            3 => baseSize + 3,
            4 => baseSize + 2,
            5 => baseSize + 1,
            _ => baseSize,
        };
    }
}
