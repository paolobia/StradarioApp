// =============================================================================
// UI/ProgressWindow.cs
//
// SINOSSI: Finestra modale di avanzamento per la generazione del PDF.
//   - Mostra una barra di avanzamento e un messaggio testuale
//   - Aggiornata tramite il metodo Update(current, total, message)
//   - Non ha pulsante di chiusura (si chiude programmaticamente)
// =============================================================================

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using StradarioApp.Resources;

namespace StradarioApp.UI
{
    public class ProgressWindow : Window
    {
        private ProgressBar _bar;
        private TextBlock   _msg;

        public ProgressWindow(string title)
        {
            Title         = title;
            Width         = 400;
            Height        = 130;
            CanResize     = false;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            // Non permette la chiusura manuale
            Closing += (s, e) => { /* bloccato finché non chiamato .Close() da codice */ };

            var panel = new StackPanel
            {
                Margin  = new Thickness(20),
                Spacing = 10
            };

            _msg = new TextBlock
            {
                Text      = Strings.Get("ProgressWindow_Avvio"),
                FontSize  = 12,
                Foreground = Brushes.DimGray
            };

            _bar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value   = 0,
                Height  = 20
            };

            panel.Children.Add(_msg);
            panel.Children.Add(_bar);
            Content = panel;
        }

        // Aggiorna lo stato di avanzamento
        public void Update(int current, int total, string message)
        {
            _msg.Text  = message;
            _bar.Value = total > 0 ? (double)current / total * 100.0 : 0;
        }

        // Override per permettere la chiusura programmatica
        public new void Close()
        {
            Closing -= (s, e) => { };
            base.Close();
        }
    }
}
