// =============================================================================
// UI/SettingsWindow.cs
//
// SINOSSI: Finestra di dialogo per le impostazioni dello stradario.
//   - Selezione formato pagina (A4/A3) e orientamento (Portrait/Landscape)
//   - Impostazione DPI (72, 96, 150, 300)
//   - Selezione scala (1:100.000 / 1:200.000)
//   - Selezione tile server da elenco predefinito
//   - Preview live delle dimensioni km della pagina
//   - Proprietà Confirmed e ResultSettings per leggere il risultato
// =============================================================================

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using StradarioApp.Models;

namespace StradarioApp.UI
{
    public class SettingsWindow : Window
    {
        public bool              Confirmed      { get; private set; } = false;
        public StradarioSettings ResultSettings { get; private set; }

        private ComboBox?  _cbPageSize;
        private ComboBox?  _cbOrientation;
        private ComboBox?  _cbDpi;
        private ComboBox?  _cbScale;
        private ComboBox?  _cbTileServer;
        private TextBox?   _tbTileApiKey;
        private TextBlock? _lblTileApiKey;
        private TextBlock? _tbTileServerInfo;
        private ComboBox?  _cbPdfContrast;
        private TextBox?   _tbAutoLockSeconds;
        private TextBox?   _tbGroqApiKey;
        private TextBlock? _tbPreview;

        public SettingsWindow(StradarioSettings current)
        {
            ResultSettings = current;
            Title          = "Impostazioni stradario";
            Width          = 460;
            Height         = 560;
            CanResize      = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            BuildUI(current);
        }

        private void BuildUI(StradarioSettings s)
        {
            var grid = new Grid
            {
                Margin            = new Thickness(16),
                RowDefinitions    = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,*,Auto,Auto"),
                ColumnDefinitions = new ColumnDefinitions("140,*")
            };

            // ---- Formato pagina ----
            AddLabel(grid, "Formato pagina:", 0);
            _cbPageSize = new ComboBox
            {
                ItemsSource   = new[] { "A5", "A4", "A3" },
                SelectedIndex = s.PageSize switch { PageSize.A5 => 0, PageSize.A4 => 1, PageSize.A3 => 2, _ => 1 },
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            _cbPageSize.SelectionChanged += (_, _) => UpdatePreview();
            AddControl(grid, _cbPageSize, 0);

            // ---- Orientamento ----
            AddLabel(grid, "Orientamento:", 1);
            _cbOrientation = new ComboBox
            {
                ItemsSource   = new[] { "Portrait", "Landscape" },
                SelectedIndex = s.Orientation == PageOrientation.Portrait ? 0 : 1,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            _cbOrientation.SelectionChanged += (_, _) => UpdatePreview();
            AddControl(grid, _cbOrientation, 1);

            // ---- DPI ----
            AddLabel(grid, "DPI:", 2);
            _cbDpi = new ComboBox
            {
                ItemsSource   = new[] { "72", "96", "150", "300" },
                SelectedIndex = s.Dpi switch { 72 => 0, 96 => 1, 150 => 2, 300 => 3, _ => 2 },
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            AddControl(grid, _cbDpi, 2);

            // ---- Scala ----
            AddLabel(grid, "Scala:", 3);
            _cbScale = new ComboBox
            {
                ItemsSource   = new[] { "1:1.000", "1:5.000", "1:10.000", "1:100.000", "1:200.000" },
                SelectedIndex = s.Scale switch
                {
                    MapScale.Scale1K   => 0,
                    MapScale.Scale5K   => 1,
                    MapScale.Scale10K  => 2,
                    MapScale.Scale100K => 3,
                    MapScale.Scale200K => 4,
                    _                  => 3
                },
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            _cbScale.SelectionChanged += (_, _) => UpdatePreview();
            AddControl(grid, _cbScale, 3);

            // ---- Blocco automatico ----
            AddLabel(grid, "Blocco automatico dopo (secondi, 0=mai):", 4);
            _tbAutoLockSeconds = new TextBox
            {
                Text = s.AutoLockSeconds.ToString(),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            AddControl(grid, _tbAutoLockSeconds, 4);

            // ---- Contrasto mappe nel PDF ----
            AddLabel(grid, "Contrasto mappe (solo PDF):", 5);
            _cbPdfContrast = new ComboBox
            {
                ItemsSource   = new[] { "Nessuno", "Contrasta colore", "Contrasta B/N", "Enfatizza strade" },
                SelectedIndex = s.PdfContrastMode switch
                {
                    PdfContrastMode.None         => 0,
                    PdfContrastMode.Color        => 1,
                    PdfContrastMode.BlackWhite   => 2,
                    PdfContrastMode.RoadEmphasis => 3,
                    _                            => 0
                },
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            AddControl(grid, _cbPdfContrast, 5);

            // ---- Preview ----
            _tbPreview = new TextBlock
            {
                Margin     = new Thickness(0, 10, 0, 0),
                FontSize   = 11,
                Foreground = Brushes.DimGray
            };
            Grid.SetRow(_tbPreview, 6);
            Grid.SetColumnSpan(_tbPreview, 2);
            grid.Children.Add(_tbPreview);
            UpdatePreview();

            // ---- Tile server (ultima impostazione: subito seguita dalla sua descrizione) ----
            AddLabel(grid, "Mappa (tile server):", 7);
            var serverNames = new string[TileServers.All.Length];
            int selectedServerIdx = TileServers.All.Length - 1; // default: ultimo
            for (int i = 0; i < TileServers.All.Length; i++)
            {
                serverNames[i] = TileServers.All[i].Name;
                if (TileServers.All[i].UrlTemplate == s.TileServerUrl)
                    selectedServerIdx = i;
            }
            _cbTileServer = new ComboBox
            {
                ItemsSource   = serverNames,
                SelectedIndex = selectedServerIdx,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            AddControl(grid, _cbTileServer, 7);

            // ---- API key tile server (solo se il server scelto la richiede) ----
            _lblTileApiKey = new TextBlock
            {
                Text              = "API key tile server:",
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 4),
                TextWrapping      = Avalonia.Media.TextWrapping.Wrap
            };
            Grid.SetRow(_lblTileApiKey, 8);
            Grid.SetColumn(_lblTileApiKey, 0);
            grid.Children.Add(_lblTileApiKey);

            _tbTileApiKey = new TextBox
            {
                Text                = s.TileServerApiKey,
                Watermark           = "Chiave gratuita da thunderforest.com o stadiamaps.com",
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            AddControl(grid, _tbTileApiKey, 8);

            void UpdateApiKeyRowVisibility()
            {
                bool needed = TileServers.All[_cbTileServer.SelectedIndex].RequiresApiKey;
                _lblTileApiKey.IsVisible = needed;
                _tbTileApiKey.IsVisible  = needed;
            }
            _cbTileServer.SelectionChanged += (_, _) => UpdateApiKeyRowVisibility();
            UpdateApiKeyRowVisibility();

            // ---- Info descrittiva sul tile server scelto (copertura/caratteristiche/uso) ----
            _tbTileServerInfo = new TextBlock
            {
                Margin       = new Thickness(0, 4, 0, 0),
                FontSize     = 11,
                Foreground   = Brushes.DimGray,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            };
            Grid.SetRow(_tbTileServerInfo, 9);
            Grid.SetColumnSpan(_tbTileServerInfo, 2);
            grid.Children.Add(_tbTileServerInfo);

            void UpdateTileServerInfo()
            {
                var entry = TileServers.All[_cbTileServer.SelectedIndex];
                _tbTileServerInfo.Text =
                    $"Copertura: {entry.Coverage}\n" +
                    $"Caratteristiche: {entry.Characteristics}\n" +
                    $"Consigliato per: {entry.Suggestion}";
            }
            _cbTileServer.SelectionChanged += (_, _) => UpdateTileServerInfo();
            UpdateTileServerInfo();

            // ---- Chiave API Groq (ricerca luoghi in linguaggio naturale, facoltativa) ----
            AddLabel(grid, "Chiave API Groq (ricerca luoghi AI):", 10);
            _tbGroqApiKey = new TextBox
            {
                Text                = s.GroqApiKey,
                Watermark           = "Facoltativa, da console.groq.com — abilita la ricerca POI in linguaggio naturale",
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            AddControl(grid, _tbGroqApiKey, 10);

            // ---- Pulsanti ----
            var btnRow = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing             = 10,
                Margin              = new Thickness(0, 12, 0, 0)
            };

            var btnOk     = DialogUi.MakeDialogButton("OK", primary: true);
            var btnCancel = DialogUi.MakeDialogButton("Annulla");
            btnOk.Click     += OnOkClick;
            btnCancel.Click += (_, _) => Close();

            btnRow.Children.Add(btnOk);
            btnRow.Children.Add(btnCancel);

            Grid.SetRow(btnRow, 11);
            Grid.SetColumnSpan(btnRow, 2);
            grid.Children.Add(btnRow);

            Content = grid;
        }

        private void AddLabel(Grid grid, string text, int row)
        {
            var lbl = new TextBlock
            {
                Text              = text,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 4),
                TextWrapping      = Avalonia.Media.TextWrapping.Wrap
            };
            Grid.SetRow(lbl, row);
            Grid.SetColumn(lbl, 0);
            grid.Children.Add(lbl);
        }

        private void AddControl(Grid grid, Control ctrl, int row)
        {
            ctrl.Margin = new Thickness(0, 4);
            Grid.SetRow(ctrl, row);
            Grid.SetColumn(ctrl, 1);
            grid.Children.Add(ctrl);
        }

        private void UpdatePreview()
        {
            var tmp = BuildSettingsFromControls();
            if (_tbPreview != null)
                _tbPreview.Text =
                    $"Ogni pagina copre circa {tmp.GetPageWidthKm():F1} × {tmp.GetPageHeightKm():F1} km";
        }

        private void OnOkClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            ResultSettings = BuildSettingsFromControls();
            Confirmed      = true;
            Close();
        }

        private StradarioSettings BuildSettingsFromControls()
        {
            var s = new StradarioSettings();

            s.PageSize    = (_cbPageSize?.SelectedIndex ?? 1) switch
                            { 0 => PageSize.A5, 1 => PageSize.A4, 2 => PageSize.A3, _ => PageSize.A4 };
            s.Orientation = (_cbOrientation?.SelectedIndex ?? 0) == 0 ? PageOrientation.Portrait : PageOrientation.Landscape;
            s.Dpi         = (_cbDpi?.SelectedIndex ?? 2) switch { 0 => 72, 1 => 96, 2 => 150, 3 => 300, _ => 150 };
            s.Scale       = (_cbScale?.SelectedIndex ?? 3) switch
            {
                0 => MapScale.Scale1K,  1 => MapScale.Scale5K,  2 => MapScale.Scale10K,
                3 => MapScale.Scale100K, 4 => MapScale.Scale200K, _ => MapScale.Scale100K
            };

            // Tile server: prende l'URL dal dizionario in base all'indice selezionato
            int idx = _cbTileServer?.SelectedIndex ?? (TileServers.All.Length - 1);
            idx = Math.Clamp(idx, 0, TileServers.All.Length - 1);
            s.TileServerUrl = TileServers.All[idx].UrlTemplate;
            s.TileServerApiKey = _tbTileApiKey?.Text ?? "";

            s.AutoLockSeconds = int.TryParse(_tbAutoLockSeconds?.Text, out int autoLock)
                ? Math.Max(0, autoLock) : 60;

            s.GroqApiKey = _tbGroqApiKey?.Text?.Trim() ?? "";

            s.PdfContrastMode = (_cbPdfContrast?.SelectedIndex ?? 0) switch
            {
                1 => PdfContrastMode.Color,
                2 => PdfContrastMode.BlackWhite,
                3 => PdfContrastMode.RoadEmphasis,
                _ => PdfContrastMode.None
            };

            return s;
        }
    }
}

