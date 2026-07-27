// =============================================================================
// UI/RouteInstradationPanel.cs
//
// SINOSSI: Pannello non modale mostrato durante l'instradamento OSRM di un
//   Percorso (MainWindow.StartInstradaMode). Stesso schema non modale di
//   PoiSearchLogWindow (ShowDialog senza await immediato, cosi' MainWindow
//   continua a guidare le chiamate asincrone mentre resta aperto) ma senza
//   la sua semantica "X = Annulla operazione in corso": qui chiudere la
//   finestra (X, o programmaticamente da CancelAllAddModes) significa
//   semplicemente uscire dalla modalita' instradamento, gestito da
//   MainWindow tramite l'evento nativo Closed.
//
//   Contenuto volutamente limitato (deciso con l'utente): solo distanza/
//   durata totale e per tratta. NESSUN elenco di alternative (si selezionano
//   cliccando direttamente sulla loro geometria sulla mappa) e NESSUN elenco
//   di vie percorse.
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

        private readonly ComboBox    _cbProfile;
        private readonly TextBlock   _tbTotals;
        private readonly StackPanel  _legsPanel;
        private readonly Button      _btnCreate;

        public RouteInstradationPanel(string routeLabel)
        {
            Title         = string.Format(Strings.Get("RouteInstradationPanel_Titolo"), routeLabel);
            Width         = 380;
            Height        = 460;
            CanResize     = false;
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

            _legsPanel = new StackPanel { Spacing = 4 };
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

        // Una riga per tratta: "Tratta N: X km · Y min", oppure il motivo
        // reale del fallimento per le tratte senza alternative disponibili
        // (errore .NET o "code" OSRM — vedi RouteInstradationService.LegResult
        // .ErrorMessage: unico modo di diagnosticare un problema di rete su
        // un eseguibile pubblicato, dato che DebugLog non scrive nulla nelle
        // build Release).
        public void SetLegs(IReadOnlyList<(double km, double min, bool failed, string? error)> legs)
        {
            _legsPanel.Children.Clear();
            for (int i = 0; i < legs.Count; i++)
            {
                var (km, min, failed, error) = legs[i];
                var tb = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
                if (failed)
                {
                    tb.Text = string.IsNullOrWhiteSpace(error)
                        ? string.Format(Strings.Get("RouteInstradationPanel_TrattaFallita"), i + 1)
                        : string.Format(Strings.Get("RouteInstradationPanel_TrattaFallitaConErrore"), i + 1, error);
                    tb.Foreground = Brushes.Firebrick;
                }
                else
                {
                    tb.Text = string.Format(Strings.Get("RouteInstradationPanel_Tratta"), i + 1, km, min);
                }
                _legsPanel.Children.Add(tb);
            }
        }

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
