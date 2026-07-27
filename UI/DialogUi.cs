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
        // svgPathData è uno dei path in UI/BootstrapIcons.cs. "enabled" = false
        // disabilita davvero il click (IsEnabled) E lo segnala visivamente con
        // opacità ridotta — usato per le azioni di un gruppo POI/percorso
        // nascosto (vedi BuildPoiGroupNavHeader/BuildPercorsoNavItem: "se non
        // lo vedi non lo puoi toccare"), dove il tema di base non applicherebbe
        // da solo uno stile "disabilitato" abbastanza evidente.
        public static Button MakeTreeIconButton(string svgPathData, string tooltip, IBrush foreground, Action onClick, double size = 24, bool enabled = true)
        {
            var icon = new Path
            {
                Data    = Geometry.Parse(svgPathData),
                Fill    = foreground,
                Width   = size * 0.62,
                Height  = size * 0.62,
                Stretch = Stretch.Uniform,
                Opacity = enabled ? 1.0 : 0.35
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
                Cursor     = new Cursor(enabled ? StandardCursorType.Hand : StandardCursorType.No),
                IsEnabled  = enabled
            };
            ToolTip.SetTip(btn, tooltip);
            btn.Click += (_, _) => onClick();
            return btn;
        }

        // Icona vettoriale puramente decorativa (non cliccabile), usata al
        // posto di un glifo emoji davanti a un'etichetta di testo (es. titoli
        // dei rami nell'albero di navigazione).
        public static Path MakeIconGlyph(string svgPathData, IBrush foreground, double size = 16)
        {
            return new Path
            {
                Data    = Geometry.Parse(svgPathData),
                Fill    = foreground,
                Width   = size,
                Height  = size,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        // Badge "C→W" / "W→C" per la correzione GCJ-02/WGS84 dei punti che
        // ricadono in Cina (v. Services/GcjTransform): stessa identica
        // dimensione/sfondo trasparente/stile di MakeTreeIconButton (niente
        // box colorato) — solo testo al posto di un Path, non essendoci un
        // glifo SVG sensato per "conversione di sistema di coordinate".
        public static Button MakeGcjBadgeButton(string text, string tooltip, IBrush foreground, Action onClick, double size = 24)
        {
            var label = new TextBlock
            {
                Text                = text,
                FontSize            = size * 0.34,
                FontWeight          = FontWeight.Bold,
                Foreground          = foreground,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center
            };
            var btn = new Button
            {
                Content    = label,
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

        // Bottone con icona vettoriale + etichetta di testo, stesso trattamento
        // (Path da BootstrapIcons) delle altre icone dell'app al posto
        // dell'emoji usata in precedenza per bottoni tipo "Usa centro vista mappa".
        public static Button MakeIconTextButton(string svgPathData, string text)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
            panel.Children.Add(MakeIconGlyph(svgPathData, Brushes.SteelBlue, 15));
            panel.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            return new Button
            {
                Content = panel,
                HorizontalContentAlignment = HorizontalAlignment.Center
            };
        }
    }
}
