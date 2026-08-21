// =============================================================================
// UI/PoiDetailWindow.cs
//
// SINOSSI: Pannello di dettaglio (etichetta + descrizione + coordinate) per
//   un POI/punto di percorso/linea di percorso, aperto con un CLIC sulla
//   mappa (MainWindow.OnMapPointerPressed/Released) — a differenza del
//   tooltip a comparsa-su-hover (DrawTooltipBox e affini, invariato, resta
//   per uno sguardo rapido), questo resta aperto finché l'utente non lo
//   chiude esplicitamente, ed essendo una vera Window è ridimensionabile
//   trascinando il bordo (nativo del sistema operativo — nessun codice di
//   drag-resize custom, stesso schema non modale di ProgressWindow e
//   ridimensionabile di RouteInstradationPanel). Descrizione mostrata con
//   rendering "ricco" (MarkdownAvaloniaRenderer: grassetto/corsivo/liste/
//   tabelle vere) — a differenza del tooltip hover, qui c'è spazio vero per
//   farlo, ed è quello che l'utente si aspetta vedendo che il PDF già
//   renderizza le tabelle mentre il pannello no.
// =============================================================================

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using StradarioApp.Services;

namespace StradarioApp.UI
{
    public class PoiDetailWindow : Window
    {
        public PoiDetailWindow(string title, string? description, string colorHex, string? coordsText)
        {
            Title         = title;
            Width         = 380;
            Height        = 320;
            MinWidth      = 280;
            MinHeight     = 200;
            CanResize     = true;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var color = PoiIconRenderer.ParseColor(colorHex);
            var accentBrush = new SolidColorBrush(Color.FromArgb(color.Alpha, color.Red, color.Green, color.Blue));

            var root = new Border
            {
                BorderBrush     = accentBrush,
                BorderThickness = new Thickness(6, 0, 0, 0),
                Padding         = new Thickness(14, 12, 14, 12),
            };

            var content = new DockPanel();

            var titleBlock = new TextBlock
            {
                Text         = title,
                FontSize     = 15,
                FontWeight   = FontWeight.Bold,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 8),
            };
            DockPanel.SetDock(titleBlock, Dock.Top);
            content.Children.Add(titleBlock);

            if (!string.IsNullOrWhiteSpace(coordsText))
            {
                var coordsBlock = new TextBlock
                {
                    Text       = coordsText,
                    FontSize   = 11,
                    Foreground = Brushes.DimGray,
                    Margin     = new Thickness(0, 8, 0, 0),
                };
                DockPanel.SetDock(coordsBlock, Dock.Bottom);
                content.Children.Add(coordsBlock);
            }

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = MarkdownAvaloniaRenderer.Render(description, baseFontSize: 13),
            };
            content.Children.Add(scroll);

            root.Child = content;
            Content    = root;
        }
    }
}
