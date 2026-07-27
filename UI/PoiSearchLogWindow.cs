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
using StradarioApp.Resources;

namespace StradarioApp.UI
{
    public class PoiSearchLogWindow : Window
    {
        public event Action? CancelRequested;

        // Sollevato invece di CancelRequested quando il pulsante viene
        // premuto mentre siamo in attesa della risposta AI (Groq) — vedi
        // EnterAiWaitPhase: in quella fase il pulsante diventa "OK" e
        // smette di significare "annulla tutta la ricerca", significa
        // "non aspettare più l'AI, mostrami quello che hai già trovato col
        // solo punteggio locale" (MainWindow.RunCategorySearchAsync). La
        // finestra NON si chiude da sola in questo caso: la ricerca
        // prosegue e la chiude lei stessa a fine operazione, come al solito.
        public event Action? SkipAiRequested;

        private readonly StackPanel   _logPanel;
        private readonly ScrollViewer _scroll;
        private readonly Button       _actionBtn;
        private bool _closingProgrammatically = false;
        private bool _aiWaitPhase = false;

        // Impostato da LogError: MainWindow.OnPoiSearchAsync lo controlla
        // prima di chiudere la finestra da sé a fine ricerca — se c'è stato
        // un errore la finestra resta aperta (l'utente la chiude a mano con
        // "Annulla"/X), invece di sparire portandosi via il messaggio
        // d'errore prima che l'utente possa leggerlo.
        public bool HasError { get; private set; } = false;

        public PoiSearchLogWindow()
        {
            Title         = Strings.Get("PoiSearchLogWindow_Titolo");
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

            _actionBtn = new Button
            {
                Content = Strings.Get("PoiSearchLogWindow_Annulla"),
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin  = new Thickness(10)
            };
            _actionBtn.Click += (s, e) =>
            {
                if (_aiWaitPhase)
                {
                    // Non chiude la finestra: la ricerca prosegue e la
                    // chiuderà da sola a operazione conclusa.
                    SkipAiRequested?.Invoke();
                    return;
                }
                CancelRequested?.Invoke();
                CloseProgrammatically();
            };

            var root = new DockPanel();
            DockPanel.SetDock(_actionBtn, Dock.Bottom);
            root.Children.Add(_actionBtn);
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

        // Come Log, ma segna l'errore (rosso) e marca HasError = true così
        // la finestra non si chiude da sola a fine ricerca.
        public void LogError(string message)
        {
            HasError = true;
            _logPanel.Children.Add(new TextBlock
            {
                Text         = $"{DateTime.Now:HH:mm:ss}  ⚠ {message}",
                FontSize     = 12,
                FontWeight   = FontWeight.Bold,
                Foreground   = Brushes.Firebrick,
                TextWrapping = TextWrapping.Wrap
            });
            _scroll.ScrollToEnd();
        }

        // Segnala che si sta per attendere la risposta AI (Groq): il
        // pulsante cambia etichetta in "OK" e il suo significato cambia
        // (vedi SkipAiRequested sopra). ExitAiWaitPhase va chiamato sempre,
        // in un finally, non solo sul percorso felice — altrimenti il
        // pulsante resterebbe "OK" anche dopo, con la ricerca conclusa.
        public void EnterAiWaitPhase()
        {
            _aiWaitPhase = true;
            _actionBtn.Content = Strings.Get("PoiSearchLogWindow_OK");
        }

        public void ExitAiWaitPhase()
        {
            _aiWaitPhase = false;
            _actionBtn.Content = Strings.Get("PoiSearchLogWindow_Annulla");
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
