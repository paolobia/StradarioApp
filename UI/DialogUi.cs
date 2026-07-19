// =============================================================================
// UI/DialogUi.cs
//
// SINOSSI: Helper condivisi per uno stile coerente dei controlli della UI.
//   - MakeDialogButton: bottoni OK/Annulla/azione dei dialog, tutti della
//     stessa altezza e con spazio sufficiente per il testo (MinWidth, non
//     Width fissa, così le etichette più lunghe non vengono tagliate).
//   - MakeTreeIconButton: icone di azione compatte ma ben leggibili usate
//     nell'albero di navigazione (aggiungi/modifica/elimina/mostra-nascondi),
//     disegnate come Path vettoriale da un path SVG (BootstrapIcons) invece
//     che come glifo emoji: stesso trattamento della toolbar, per coerenza.
// =============================================================================

using System;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace StradarioApp.UI
{
    internal static class DialogUi
    {
        // Altezza e larghezza minima uniformi per tutti i bottoni dei dialog
        public const double ButtonHeight   = 34;
        public const double ButtonMinWidth = 96;

        public static Button MakeDialogButton(string text, bool primary = false)
        {
            return new Button
            {
                Content    = text,
                Height     = ButtonHeight,
                MinWidth   = ButtonMinWidth,
                Padding    = new Avalonia.Thickness(16, 0),
                FontSize   = 13,
                FontWeight = primary ? FontWeight.Bold : FontWeight.Normal,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment   = VerticalAlignment.Center
            };
        }

        // Icona di azione quadrata usata nell'albero di navigazione (pannello
        // sinistro): dimensione fissa, ben visibile, con tooltip esplicativo.
        // svgPathData è uno dei path in UI/BootstrapIcons.cs.
        public static Button MakeTreeIconButton(string svgPathData, string tooltip, IBrush foreground, Action onClick, double size = 24)
        {
            var icon = new Path
            {
                Data    = Geometry.Parse(svgPathData),
                Fill    = foreground,
                Width   = size * 0.62,
                Height  = size * 0.62,
                Stretch = Stretch.Uniform
            };
            var btn = new Button
            {
                Content    = icon,
                Width      = size,
                Height     = size,
                Padding    = new Avalonia.Thickness(0),
                Background = Brushes.Transparent,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment   = VerticalAlignment.Center,
                Cursor     = new Cursor(StandardCursorType.Hand)
            };
            ToolTip.SetTip(btn, tooltip);
            btn.Click += (_, _) => onClick();
            return btn;
        }
    }
}
