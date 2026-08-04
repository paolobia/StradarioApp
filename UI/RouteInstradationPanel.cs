// =============================================================================
// UI/RouteInstradationPanel.cs
//
// SINOSSI: Pannello mostrato durante l'instradamento OSRM di un Percorso
//   (MainWindow.StartInstradaMode). Mostrato con ShowDialog: è quindi MODALE
//   rispetto alla finestra principale (input alla mappa sottostante bloccato
//   finché resta aperto) — per questo la scelta fra le alternative di ogni
//   tratta avviene interamente QUI dentro, con una riga di pulsanti "a
//   scheda" ORIZZONTALE per tratta (uno per alternativa generata da OSRM),
//   non più cliccando sulla geometria sulla mappa: quel meccanismo (rimosso,
//   vedi FindInstradaAlternativeAtPoint nella cronologia) non poteva mai
//   scattare proprio perché il dialog è modale — bug reale riscontrato
//   dall'utente con più tratte/alternative. Cliccare un'alternativa
//   aggiorna subito l'anteprima sulla mappa (tramite AlternativeSelected),
//   il bottone "Crea percorso" resta invariato. Finestra ridimensionabile:
//   il layout (DockPanel, ultimo figlio = ScrollViewer che riempie lo
//   spazio residuo) si adatta a qualunque dimensione scelta dall'utente.
// =============================================================================

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using StradarioApp.Resources;
using StradarioApp.Services;

namespace StradarioApp.UI
{
    public class RouteInstradationPanel : Window
    {
        public event Action<RouteInstradationService.Profile>? ProfileChanged;
        public event Action? CreateRequested;

        // (legIndex, altIndex) scelto dall'utente cambiando tab per quella tratta.
        public event Action<int, int>? AlternativeSelected;

        private readonly ComboBox    _cbProfile;
        private readonly TextBlock   _tbTotals;
        private readonly StackPanel  _legsPanel;
        private readonly Button      _btnCreate;

        public RouteInstradationPanel(string routeLabel)
        {
            Title         = string.Format(Strings.Get("RouteInstradationPanel_Titolo"), routeLabel);
            Width         = 620;
            Height        = 720;
            MinWidth      = 420;
            MinHeight     = 420;
            CanResize     = true;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var root = new DockPanel { Margin = new Thickness(14) };

            var lblProfile = new TextBlock { Text = Strings.Get("RouteInstradationPanel_Profilo"), Margin = new Thickness(0, 0, 0, 4) };
            DockPanel.SetDock(lblProfile, Dock.Top);
            root.Children.Add(lblProfile);

            _cbProfile = new ComboBox
            {
                ItemsSource = new[]
                {
                    Strings.Get("RouteInstradationPanel_ProfiloAuto"),
                    Strings.Get("RouteInstradationPanel_ProfiloBici"),
                    Strings.Get("RouteInstradationPanel_ProfiloPiedi"),
                },
                SelectedIndex = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 0, 12),
            };
            _cbProfile.SelectionChanged += (_, _) =>
            {
                var profile = _cbProfile.SelectedIndex switch
                {
                    1 => RouteInstradationService.Profile.Bici,
                    2 => RouteInstradationService.Profile.Piedi,
                    _ => RouteInstradationService.Profile.Auto,
                };
                ProfileChanged?.Invoke(profile);
            };
            DockPanel.SetDock(_cbProfile, Dock.Top);
            root.Children.Add(_cbProfile);

            _btnCreate = DialogUi.MakeDialogButton(Strings.Get("RouteInstradationPanel_CreaPercorso"), primary: true);
            _btnCreate.HorizontalAlignment = HorizontalAlignment.Stretch;
            _btnCreate.Margin = new Thickness(0, 12, 0, 0);
            _btnCreate.IsEnabled = false;
            _btnCreate.Click += (_, _) => CreateRequested?.Invoke();
            DockPanel.SetDock(_btnCreate, Dock.Bottom);
            root.Children.Add(_btnCreate);

            _tbTotals = new TextBlock
            {
                FontWeight   = FontWeight.Bold,
                Margin       = new Thickness(0, 0, 0, 10),
                TextWrapping = TextWrapping.Wrap,
            };
            DockPanel.SetDock(_tbTotals, Dock.Top);
            root.Children.Add(_tbTotals);

            _legsPanel = new StackPanel { Spacing = 10 };
            var scroll = new ScrollViewer { Content = _legsPanel };
            root.Children.Add(scroll); // ultimo figlio: riempie lo spazio restante

            Content = root;
        }

        // Distanza/durata totale del percorso instradato, sommando SOLO le
        // tratte con un'alternativa attualmente selezionata (una tratta
        // fallita non contribuisce).
        public void SetTotals(double km, double minutes)
        {
            _tbTotals.Text = string.Format(Strings.Get("RouteInstradationPanel_Totali"), km, minutes);
        }

        private static readonly IBrush TabSelectedBg   = new SolidColorBrush(Color.Parse("#1E88E5"));
        private static readonly IBrush TabUnselectedBg = new SolidColorBrush(Color.Parse("#EEEEEE"));

        // Un blocco per tratta: se fallita mostra il motivo (nessun pulsante);
        // se ha una sola alternativa mostra la riga "Tratta N: X km · Y min"
        // come prima (una sola scelta non avrebbe senso come pulsante); se ne
        // ha più di una, una riga ORIZZONTALE (WrapPanel: va a capo da sola
        // se lo spazio non basta, invece di uscire dal bordo della finestra)
        // di pulsanti "a scheda", uno per alternativa — cliccarne uno
        // aggiorna subito l'anteprima sulla mappa tramite AlternativeSelected.
        public void SetLegs(IReadOnlyList<LegInfo> legs)
        {
            _legsPanel.Children.Clear();
            for (int li = 0; li < legs.Count; li++)
            {
                var leg = legs[li];
                var legLabel = new TextBlock
                {
                    Text       = string.Format(Strings.Get("RouteInstradationPanel_TrattaLabel"), li + 1),
                    FontWeight = FontWeight.SemiBold,
                    FontSize   = 12,
                };
                _legsPanel.Children.Add(legLabel);

                if (leg.Failed)
                {
                    var tb = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Firebrick };
                    tb.Text = string.IsNullOrWhiteSpace(leg.Error)
                        ? string.Format(Strings.Get("RouteInstradationPanel_TrattaFallita"), li + 1)
                        : string.Format(Strings.Get("RouteInstradationPanel_TrattaFallitaConErrore"), li + 1, leg.Error);
                    _legsPanel.Children.Add(tb);
                    continue;
                }

                if (leg.Alternatives.Count <= 1)
                {
                    var (km0, min0) = leg.Alternatives.Count == 1 ? leg.Alternatives[0] : (0.0, 0.0);
                    var tb = new TextBlock
                    {
                        FontSize = 12, TextWrapping = TextWrapping.Wrap,
                        Text = string.Format(Strings.Get("RouteInstradationPanel_Tratta"), li + 1, km0, min0),
                    };
                    _legsPanel.Children.Add(tb);
                    continue;
                }

                var row = new WrapPanel { Orientation = Orientation.Horizontal };
                int selectedIndex = Math.Clamp(leg.SelectedIndex, 0, leg.Alternatives.Count - 1);
                int capturedLi = li;
                for (int ai = 0; ai < leg.Alternatives.Count; ai++)
                {
                    var (km, min) = leg.Alternatives[ai];
                    bool isSelected = ai == selectedIndex;
                    int capturedAi = ai;

                    var btn = new Button
                    {
                        Content = string.Format(Strings.Get("RouteInstradationPanel_AlternativaHeader"), ai + 1, km, min),
                        FontSize = 12,
                        Padding  = new Thickness(10, 6),
                        Margin   = new Thickness(0, 0, 6, 6),
                        CornerRadius = new CornerRadius(4),
                        Background = isSelected ? TabSelectedBg : TabUnselectedBg,
                        Foreground = isSelected ? Brushes.White : Brushes.Black,
                        FontWeight = isSelected ? FontWeight.Bold : FontWeight.Normal,
                    };
                    btn.Click += (_, _) => AlternativeSelected?.Invoke(capturedLi, capturedAi);
                    row.Children.Add(btn);
                }

                _legsPanel.Children.Add(row);
            }
        }

        // Info necessarie a renderizzare una tratta: km/min di OGNI
        // alternativa disponibile (non solo quella selezionata, a differenza
        // della vecchia SetLegs) più quale è correntemente selezionata.
        public readonly record struct LegInfo(
            IReadOnlyList<(double km, double min)> Alternatives,
            int SelectedIndex,
            bool Failed,
            string? Error);

        private bool _canCreate;

        // Disabilita combo e bottone "Crea percorso" mentre una richiesta
        // OSRM e' in corso, per evitare di cambiare profilo/materializzare il
        // percorso a metà di un aggiornamento.
        public void SetBusy(bool busy)
        {
            _cbProfile.IsEnabled = !busy;
            _btnCreate.IsEnabled = !busy && _canCreate;
        }

        // Il bottone "Crea percorso" resta disabilitato finche' non c'e'
        // almeno una tratta con un'alternativa selezionabile (la decisione
        // se permettere un percorso parzialmente instradato spetta a
        // MainWindow, qui si riflette solo lo stato comunicato).
        public void SetCanCreate(bool canCreate)
        {
            _canCreate = canCreate;
            _btnCreate.IsEnabled = canCreate;
        }
    }
}
