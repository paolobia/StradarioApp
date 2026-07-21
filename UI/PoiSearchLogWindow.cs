// =============================================================================
// UI/PoiSearchLogWindow.cs
//
// SINOSSI: Finestra di log per la ricerca POI per categoria (Overpass/
//   Nominatim/AI), avviata da MainWindow.OnPoiSearchAsync a ogni ricerca
//   (Invio o secondo clic sulla lente). Mostra passo-passo cosa sta
//   succedendo (Log) invece di far apparire una sola riga di stato che
//   sparisce dopo pochi secondi. Un solo pulsante "Annulla" (anche la X
//   della finestra si comporta come Annulla, non c'è un secondo modo di
//   chiuderla): richiede l'annullamento dell'operazione in corso tramite
//   CancelRequested. Si chiude DA SOLA (RequestCloseOnCompletion) solo a
//   operazione conclusa — mai prima, indipendentemente dall'esito.
// =============================================================================

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace StradarioApp.UI
{
    public class PoiSearchLogWindow : Window
    {
        public event Action? CancelRequested;

        private readonly StackPanel   _logPanel;
        private readonly ScrollViewer _scroll;
        private bool _closingProgrammatically = false;

        public PoiSearchLogWindow()
        {
            Title         = "Ricerca POI in corso...";
            Width         = 560;
            Height        = 380;
            CanResize     = true;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            // La X della finestra equivale ad Annulla: un solo modo di
            // chiuderla manualmente, coerente col pulsante in basso.
            Closing += (s, e) =>
            {
                if (_closingProgrammatically) return;
                CancelRequested?.Invoke();
            };

            _logPanel = new StackPanel { Margin = new Thickness(10), Spacing = 3 };
            _scroll = new ScrollViewer
            {
                Content = _logPanel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            var cancelBtn = new Button
            {
                Content = "Annulla",
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin  = new Thickness(10)
            };
            cancelBtn.Click += (s, e) =>
            {
                CancelRequested?.Invoke();
                CloseProgrammatically();
            };

            var root = new DockPanel();
            DockPanel.SetDock(cancelBtn, Dock.Bottom);
            root.Children.Add(cancelBtn);
            root.Children.Add(_scroll);

            Content = root;
        }

        // Aggiunge una riga di log col relativo orario e scrolla in fondo.
        // Va chiamato dal thread UI (i chiamanti sono tutti in metodi async
        // di MainWindow, già sul thread UI grazie ad await/ConfigureAwait di
        // default).
        public void Log(string message)
        {
            _logPanel.Children.Add(new TextBlock
            {
                Text         = $"{DateTime.Now:HH:mm:ss}  {message}",
                FontSize     = 12,
                TextWrapping = TextWrapping.Wrap
            });
            _scroll.ScrollToEnd();
        }

        // Chiusura "legittima": a operazione conclusa (successo, errore o
        // annullamento) — non passa dal gestore di Closing sopra, che
        // altrimenti la scambierebbe per una richiesta di annullamento.
        public void CloseProgrammatically()
        {
            _closingProgrammatically = true;
            Close();
        }
    }
}
