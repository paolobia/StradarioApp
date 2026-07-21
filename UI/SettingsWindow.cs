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
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using StradarioApp.Models;
using StradarioApp.Services;

namespace StradarioApp.UI
{
    public class SettingsWindow : Window
    {
        public bool              Confirmed      { get; private set; } = false;
        public StradarioSettings ResultSettings { get; private set; }

        // Categorie POI personalizzate (tab "Categorie POI"): letta al posto
        // di PoiSearchService.CustomCategories dal chiamante (MainWindow), che
        // le persiste/riapplica alla conferma — vedi OnOpenSettings.
        public List<(string Key, string Value, string Label)> ResultCustomCategories { get; private set; } = new();
        private readonly List<(string Key, string Value, string Label)> _customCategories;
        private StackPanel? _customCategoriesPanel;
        private TextBox?    _tbCustomLabel;
        private TextBox?    _tbCustomKey;
        private TextBox?    _tbCustomValue;
        private TextBlock?  _tbCustomError;

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

        // Scale in ordine crescente per il ComboBox (l'ordine dell'enum MapScale
        // è invece fissato dalla persistenza in .stradario, vedi Models/StradarioModels.cs).
        private static readonly MapScale[] ScaleComboValues =
        {
            MapScale.Scale1K, MapScale.Scale5K, MapScale.Scale10K, MapScale.Scale15K,
            MapScale.Scale20K, MapScale.Scale25K, MapScale.Scale50K, MapScale.Scale100K,
            MapScale.Scale150K, MapScale.Scale200K, MapScale.Scale250K, MapScale.Scale300K,
            MapScale.Scale400K, MapScale.Scale500K, MapScale.Scale800K, MapScale.Scale1M
        };
        private static readonly string[] ScaleComboItems =
        {
            "1:1.000", "1:5.000", "1:10.000", "1:15.000",
            "1:20.000", "1:25.000", "1:50.000", "1:100.000",
            "1:150.000", "1:200.000", "1:250.000", "1:300.000",
            "1:400.000", "1:500.000", "1:800.000", "1:1.000.000"
        };

        public SettingsWindow(StradarioSettings current, IEnumerable<(string Key, string Value, string Label)>? customCategories = null)
        {
            ResultSettings   = current;
            _customCategories = (customCategories ?? Enumerable.Empty<(string, string, string)>()).ToList();
            Title          = "Impostazioni stradario";
            Width          = 460;
            Height         = 600;
            CanResize      = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var tabs = new TabControl();
            tabs.Items.Add(new TabItem { Header = "Generale",     Content = BuildGeneralTab(current) });
            tabs.Items.Add(new TabItem { Header = "Categorie POI", Content = BuildCategoriesTab() });

            // Pulsanti condivisi da entrambe le tab (OK conferma tutto,
            // impostazioni generali E categorie personalizzate insieme).
            var btnRow = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing             = 10,
                Margin              = new Thickness(16, 8, 16, 12)
            };
            var btnOk     = DialogUi.MakeDialogButton("OK", primary: true);
            var btnCancel = DialogUi.MakeDialogButton("Annulla");
            btnOk.Click     += OnOkClick;
            btnCancel.Click += (_, _) => Close();
            btnRow.Children.Add(btnOk);
            btnRow.Children.Add(btnCancel);

            var root = new DockPanel();
            DockPanel.SetDock(btnRow, Dock.Bottom);
            root.Children.Add(btnRow);
            root.Children.Add(tabs);

            Content = root;
        }

        private Control BuildGeneralTab(StradarioSettings s)
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
                ItemsSource   = ScaleComboItems,
                SelectedIndex = Array.IndexOf(ScaleComboValues, s.Scale) is var scaleSelIdx && scaleSelIdx >= 0 ? scaleSelIdx : 7,
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

            return grid;
        }

        // Tab "Categorie POI": elenco delle categorie personalizzate aggiunte
        // dall'utente (oltre a quelle predefinite, vedi PoiSearchService.
        // Categories), con un modulo per aggiungerne di nuove specificando
        // direttamente il tag OSM (chiave=valore) e l'etichetta da mostrare
        // nel combo di ricerca POI.
        private Control BuildCategoriesTab()
        {
            var root = new DockPanel { Margin = new Thickness(16) };

            var intro = new TextBlock
            {
                Text = "Categorie aggiuntive per la ricerca POI per categoria, oltre a quelle predefinite " +
                       "(ristoranti, farmacie, ecc.). Specifica il tag OSM (chiave e valore, es. amenity / vending_machine) " +
                       "e l'etichetta da mostrare nel menu di ricerca.",
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                FontSize     = 11,
                Foreground   = Brushes.DimGray,
                Margin       = new Thickness(0, 0, 0, 10)
            };
            DockPanel.SetDock(intro, Dock.Top);
            root.Children.Add(intro);

            var addGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,*,*,Auto"),
                Margin            = new Thickness(0, 0, 0, 4)
            };
            _tbCustomLabel = new TextBox { Watermark = "Etichetta (es. distributori acqua)", Margin = new Thickness(0, 0, 4, 0) };
            _tbCustomKey   = new TextBox { Watermark = "Chiave OSM (es. amenity)",            Margin = new Thickness(0, 0, 4, 0) };
            _tbCustomValue = new TextBox { Watermark = "Valore OSM (es. vending_machine)",     Margin = new Thickness(0, 0, 4, 0) };
            var btnAdd = DialogUi.MakeDialogButton("➕ Aggiungi");
            btnAdd.Click += (_, _) => OnAddCustomCategory();

            Grid.SetColumn(_tbCustomLabel, 0);
            Grid.SetColumn(_tbCustomKey, 1);
            Grid.SetColumn(_tbCustomValue, 2);
            Grid.SetColumn(btnAdd, 3);
            addGrid.Children.Add(_tbCustomLabel);
            addGrid.Children.Add(_tbCustomKey);
            addGrid.Children.Add(_tbCustomValue);
            addGrid.Children.Add(btnAdd);
            DockPanel.SetDock(addGrid, Dock.Top);
            root.Children.Add(addGrid);

            _tbCustomError = new TextBlock
            {
                Foreground   = Brushes.Firebrick,
                FontSize     = 11,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 8),
                IsVisible    = false
            };
            DockPanel.SetDock(_tbCustomError, Dock.Top);
            root.Children.Add(_tbCustomError);

            _customCategoriesPanel = new StackPanel { Spacing = 4 };
            var scroll = new ScrollViewer { Content = _customCategoriesPanel };
            root.Children.Add(scroll); // ultimo figlio del DockPanel: riempie lo spazio restante

            RefreshCustomCategoriesList();

            return root;
        }

        private void SetCustomError(string? message)
        {
            if (_tbCustomError == null) return;
            _tbCustomError.Text      = message ?? "";
            _tbCustomError.IsVisible = !string.IsNullOrEmpty(message);
        }

        private void OnAddCustomCategory()
        {
            string label = _tbCustomLabel?.Text?.Trim() ?? "";
            string key   = _tbCustomKey?.Text?.Trim() ?? "";
            string value = _tbCustomValue?.Text?.Trim() ?? "";

            if (label.Length == 0 || key.Length == 0 || value.Length == 0)
            {
                SetCustomError("Compila etichetta, chiave e valore prima di aggiungere.");
                return;
            }

            bool alreadyExists =
                _customCategories.Any(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && c.Value.Equals(value, StringComparison.OrdinalIgnoreCase)) ||
                PoiSearchService.AllCategories.Any(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && c.Value.Equals(value, StringComparison.OrdinalIgnoreCase));
            if (alreadyExists)
            {
                SetCustomError($"Esiste già una categoria con il tag \"{key}={value}\".");
                return;
            }

            _customCategories.Add((key, value, label));
            _tbCustomLabel!.Text = "";
            _tbCustomKey!.Text   = "";
            _tbCustomValue!.Text = "";
            SetCustomError(null);
            RefreshCustomCategoriesList();
        }

        private void RefreshCustomCategoriesList()
        {
            if (_customCategoriesPanel == null) return;
            _customCategoriesPanel.Children.Clear();

            if (_customCategories.Count == 0)
            {
                _customCategoriesPanel.Children.Add(new TextBlock
                {
                    Text       = "Nessuna categoria personalizzata.",
                    FontSize   = 11,
                    Foreground = Brushes.DimGray
                });
                return;
            }

            foreach (var cat in _customCategories.ToList())
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                var lbl = new TextBlock
                {
                    Text              = $"{cat.Label}  ({cat.Key}={cat.Value})",
                    VerticalAlignment = VerticalAlignment.Center
                };
                var btnRemove = new Button { Content = "🗑", Padding = new Thickness(6, 2) };
                ToolTip.SetTip(btnRemove, "Rimuovi questa categoria personalizzata");
                btnRemove.Click += (_, _) =>
                {
                    _customCategories.Remove(cat);
                    RefreshCustomCategoriesList();
                };

                Grid.SetColumn(lbl, 0);
                Grid.SetColumn(btnRemove, 1);
                row.Children.Add(lbl);
                row.Children.Add(btnRemove);
                _customCategoriesPanel.Children.Add(row);
            }
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
            ResultSettings         = BuildSettingsFromControls();
            ResultCustomCategories = _customCategories.ToList();
            Confirmed              = true;
            Close();
        }

        private StradarioSettings BuildSettingsFromControls()
        {
            var s = new StradarioSettings();

            s.PageSize    = (_cbPageSize?.SelectedIndex ?? 1) switch
                            { 0 => PageSize.A5, 1 => PageSize.A4, 2 => PageSize.A3, _ => PageSize.A4 };
            s.Orientation = (_cbOrientation?.SelectedIndex ?? 0) == 0 ? PageOrientation.Portrait : PageOrientation.Landscape;
            s.Dpi         = (_cbDpi?.SelectedIndex ?? 2) switch { 0 => 72, 1 => 96, 2 => 150, 3 => 300, _ => 150 };
            int scaleIdx = _cbScale?.SelectedIndex ?? 7;
            s.Scale       = (scaleIdx >= 0 && scaleIdx < ScaleComboValues.Length)
                ? ScaleComboValues[scaleIdx] : MapScale.Scale100K;

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

