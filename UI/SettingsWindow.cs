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
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using StradarioApp.Models;
using StradarioApp.Resources;
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

        // Tab "Database POI offline" (vedi Services/PoiOfflineDatabase): una
        // riga per continente, con stato (non scaricato / versione locale /
        // aggiornamento disponibile) e un bottone scarica/aggiorna. Nessuna
        // selezione per singola categoria: ogni zip contiene sempre tutte le
        // 43 categorie del continente insieme.
        private StackPanel? _offlineDataPanel;
        private TextBlock?  _offlineDataStatusText;
        // Tag dell'ultima release dati vista da CheckOfflineDataUpdatesAsync
        // in questa apertura della finestra: null finché non si è controllato
        // almeno una volta. Riusato da OnDownloadContinentAsync a fine
        // download per rietichettare correttamente il pulsante (altrimenti
        // tornerebbe sempre a "Scarica" anche se il continente è ora alla
        // versione più recente, che dovrebbe invece mostrare "Riscarica").
        private string? _lastKnownDataTag;
        private readonly Dictionary<string, TextBlock> _offlineContinentStatusLabels = new();
        private readonly Dictionary<string, Button>    _offlineContinentButtons      = new();

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
        private ComboBox?  _cbLanguage;

        // "it"/"en", nello stesso ordine del ComboBox _cbLanguage — i nomi
        // restano nella propria lingua nativa (non tradotti), come nella
        // maggior parte dei selettori di lingua.
        private static readonly string[] LanguageCodes = { "it", "en" };
        private static readonly string[] LanguageNames = { "Italiano", "English" };

        // Lingua corrente al momento dell'apertura (Strings.CurrentLanguage,
        // già allineata alla preferenza salvata/rilevata da Program.cs):
        // usata per preselezionare il combo, non richiede un parametro
        // separato nel costruttore.
        public string ResultLanguage { get; private set; } = Strings.CurrentLanguage;

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
            Title          = Strings.Get("SettingsWindow_Titolo");
            // Larghezza aumentata rispetto alle 2 tab originali (460 px):
            // con l'aggiunta della tab "Database POI offline" le tre
            // intestazioni (Generale / Categorie POI / Database POI
            // offline) non stavano più su una sola riga.
            Width          = 620;
            Height         = 650;
            CanResize      = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var tabs = new TabControl();
            tabs.Items.Add(new TabItem { Header = Strings.Get("SettingsWindow_TabGenerale"),     Content = BuildGeneralTab(current) });
            tabs.Items.Add(new TabItem { Header = Strings.Get("SettingsWindow_TabCategoriePoi"), Content = BuildCategoriesTab() });
            tabs.Items.Add(new TabItem { Header = Strings.Get("SettingsWindow_TabDatiOffline"),  Content = BuildOfflineDataTab() });

            // Pulsanti condivisi da entrambe le tab (OK conferma tutto,
            // impostazioni generali E categorie personalizzate insieme).
            var btnRow = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing             = 10,
                Margin              = new Thickness(16, 8, 16, 12)
            };
            var btnOk     = DialogUi.MakeDialogButton(Strings.Get("SettingsWindow_Ok"), primary: true);
            var btnCancel = DialogUi.MakeDialogButton(Strings.Get("SettingsWindow_Annulla"));
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
                RowDefinitions    = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,*,Auto,Auto,Auto,Auto"),
                ColumnDefinitions = new ColumnDefinitions("140,*")
            };

            // ---- Formato pagina ----
            AddLabel(grid, Strings.Get("SettingsWindow_FormatoPagina"), 0);
            _cbPageSize = new ComboBox
            {
                ItemsSource   = new[] { "A5", "A4", "A3" },
                SelectedIndex = s.PageSize switch { PageSize.A5 => 0, PageSize.A4 => 1, PageSize.A3 => 2, _ => 1 },
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            _cbPageSize.SelectionChanged += (_, _) => UpdatePreview();
            AddControl(grid, _cbPageSize, 0);

            // ---- Orientamento ----
            AddLabel(grid, Strings.Get("SettingsWindow_Orientamento"), 1);
            _cbOrientation = new ComboBox
            {
                ItemsSource   = new[] { Strings.Get("SettingsWindow_Portrait"), Strings.Get("SettingsWindow_Landscape") },
                SelectedIndex = s.Orientation == PageOrientation.Portrait ? 0 : 1,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            _cbOrientation.SelectionChanged += (_, _) => UpdatePreview();
            AddControl(grid, _cbOrientation, 1);

            // ---- DPI ----
            AddLabel(grid, Strings.Get("SettingsWindow_Dpi"), 2);
            _cbDpi = new ComboBox
            {
                ItemsSource   = new[] { "72", "96", "150", "300" },
                SelectedIndex = s.Dpi switch { 72 => 0, 96 => 1, 150 => 2, 300 => 3, _ => 2 },
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            AddControl(grid, _cbDpi, 2);

            // ---- Scala ----
            AddLabel(grid, Strings.Get("SettingsWindow_Scala"), 3);
            _cbScale = new ComboBox
            {
                ItemsSource   = ScaleComboItems,
                SelectedIndex = Array.IndexOf(ScaleComboValues, s.Scale) is var scaleSelIdx && scaleSelIdx >= 0 ? scaleSelIdx : 7,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            _cbScale.SelectionChanged += (_, _) => UpdatePreview();
            AddControl(grid, _cbScale, 3);

            // ---- Blocco automatico ----
            AddLabel(grid, Strings.Get("SettingsWindow_BloccoAutomatico"), 4);
            _tbAutoLockSeconds = new TextBox
            {
                Text = s.AutoLockSeconds.ToString(),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            AddControl(grid, _tbAutoLockSeconds, 4);

            // ---- Contrasto mappe nel PDF ----
            AddLabel(grid, Strings.Get("SettingsWindow_ContrastoMappe"), 5);
            _cbPdfContrast = new ComboBox
            {
                ItemsSource   = new[]
                {
                    Strings.Get("SettingsWindow_ContrastoNessuno"),
                    Strings.Get("SettingsWindow_ContrastoColore"),
                    Strings.Get("SettingsWindow_ContrastoBN"),
                    Strings.Get("SettingsWindow_ContrastoStrade")
                },
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
            AddLabel(grid, Strings.Get("SettingsWindow_MappaTileServer"), 7);
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
                Text              = Strings.Get("SettingsWindow_ApiKeyTileServer"),
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
                Watermark           = Strings.Get("SettingsWindow_ChiaveTileServerWatermark"),
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
                _tbTileServerInfo.Text = string.Format(
                    Strings.Get("SettingsWindow_InfoTileServer"),
                    entry.Coverage, entry.Characteristics, entry.Suggestion);
            }
            _cbTileServer.SelectionChanged += (_, _) => UpdateTileServerInfo();
            UpdateTileServerInfo();

            // ---- Chiave API Groq (ricerca luoghi in linguaggio naturale, facoltativa) ----
            AddLabel(grid, Strings.Get("SettingsWindow_ChiaveGroq"), 10);
            _tbGroqApiKey = new TextBox
            {
                Text                = s.GroqApiKey,
                Watermark           = Strings.Get("SettingsWindow_ChiaveGroqWatermark"),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            AddControl(grid, _tbGroqApiKey, 10);

            // ---- Lingua interfaccia ----
            AddLabel(grid, Strings.Get("SettingsWindow_Lingua"), 11);
            int langIdx = Array.IndexOf(LanguageCodes, Strings.CurrentLanguage);
            _cbLanguage = new ComboBox
            {
                ItemsSource   = LanguageNames,
                SelectedIndex = langIdx >= 0 ? langIdx : 0,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            AddControl(grid, _cbLanguage, 11);

            var lblLanguageNote = new TextBlock
            {
                Text         = Strings.Get("SettingsWindow_RiavvioLingua"),
                Margin       = new Thickness(0, 0, 0, 0),
                FontSize     = 11,
                Foreground   = Brushes.DimGray,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            };
            Grid.SetRow(lblLanguageNote, 12);
            Grid.SetColumnSpan(lblLanguageNote, 2);
            grid.Children.Add(lblLanguageNote);

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
                Text = Strings.Get("SettingsWindow_IntroCategoriePoi"),
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
            _tbCustomLabel = new TextBox { Watermark = Strings.Get("SettingsWindow_EtichettaWatermark"), Margin = new Thickness(0, 0, 4, 0) };
            _tbCustomKey   = new TextBox { Watermark = Strings.Get("SettingsWindow_ChiaveOsmWatermark"),            Margin = new Thickness(0, 0, 4, 0) };
            _tbCustomValue = new TextBox { Watermark = Strings.Get("SettingsWindow_ValoreOsmWatermark"),     Margin = new Thickness(0, 0, 4, 0) };
            var btnAdd = DialogUi.MakeDialogButton(Strings.Get("SettingsWindow_AggiungiCategoria"));
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
                SetCustomError(Strings.Get("SettingsWindow_CompilaTuttiICampi"));
                return;
            }

            bool alreadyExists =
                _customCategories.Any(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && c.Value.Equals(value, StringComparison.OrdinalIgnoreCase)) ||
                PoiSearchService.AllCategories.Any(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && c.Value.Equals(value, StringComparison.OrdinalIgnoreCase));
            if (alreadyExists)
            {
                SetCustomError(string.Format(Strings.Get("SettingsWindow_CategoriaGiaEsistente"), key, value));
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
                    Text       = Strings.Get("SettingsWindow_NessunaCategoriaPersonalizzata"),
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
                    Text              = string.Format(Strings.Get("SettingsWindow_CategoriaRiga"), cat.Label, cat.Key, cat.Value),
                    VerticalAlignment = VerticalAlignment.Center
                };
                var btnRemove = new Button { Content = "🗑", Padding = new Thickness(6, 2) };
                ToolTip.SetTip(btnRemove, Strings.Get("SettingsWindow_RimuoviCategoriaTooltip"));
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

        // Nomi visualizzati per gli 8 continenti (stessi slug di
        // PoiOfflineDatabase.Continents/osm/OsmExtractor.GetContinentName).
        private static string ContinentDisplayName(string continent) =>
            Strings.Get("Continent_" + continent.Replace("-", "_"));

        private Control BuildOfflineDataTab()
        {
            var root = new DockPanel { Margin = new Thickness(16) };

            var intro = new TextBlock
            {
                Text         = Strings.Get("SettingsWindow_IntroDatiOffline"),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                FontSize     = 11,
                Foreground   = Brushes.DimGray,
                Margin       = new Thickness(0, 0, 0, 10)
            };
            DockPanel.SetDock(intro, Dock.Top);
            root.Children.Add(intro);

            var btnCheckAll = DialogUi.MakeDialogButton(Strings.Get("SettingsWindow_ControllaAggiornamentiDati"));
            btnCheckAll.HorizontalAlignment = HorizontalAlignment.Left;
            btnCheckAll.Margin = new Thickness(0, 0, 0, 8);
            btnCheckAll.Click += async (_, _) => await CheckOfflineDataUpdatesAsync();
            DockPanel.SetDock(btnCheckAll, Dock.Top);
            root.Children.Add(btnCheckAll);

            _offlineDataStatusText = new TextBlock
            {
                FontSize     = 11,
                Foreground   = Brushes.DimGray,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 8),
                IsVisible    = false
            };
            DockPanel.SetDock(_offlineDataStatusText, Dock.Top);
            root.Children.Add(_offlineDataStatusText);

            _offlineDataPanel = new StackPanel { Spacing = 6 };
            var scroll = new ScrollViewer { Content = _offlineDataPanel };
            root.Children.Add(scroll); // ultimo figlio del DockPanel: riempie lo spazio restante

            RefreshOfflineDataList();

            return root;
        }

        private void RefreshOfflineDataList()
        {
            if (_offlineDataPanel == null) return;
            _offlineDataPanel.Children.Clear();
            _offlineContinentStatusLabels.Clear();
            _offlineContinentButtons.Clear();

            foreach (var continent in PoiOfflineDatabase.Continents)
            {
                var row = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("120,*,Auto"),
                    Margin            = new Thickness(0, 0, 0, 2)
                };

                var nameLbl = new TextBlock
                {
                    Text              = ContinentDisplayName(continent),
                    VerticalAlignment = VerticalAlignment.Center,
                    FontWeight        = Avalonia.Media.FontWeight.SemiBold
                };

                var statusLbl = new TextBlock
                {
                    FontSize          = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping      = Avalonia.Media.TextWrapping.Wrap,
                    Margin            = new Thickness(8, 0)
                };

                var btn = new Button { Padding = new Thickness(8, 2) };
                btn.Click += async (_, _) => await OnDownloadContinentAsync(continent);

                Grid.SetColumn(nameLbl, 0);
                Grid.SetColumn(statusLbl, 1);
                Grid.SetColumn(btn, 2);
                row.Children.Add(nameLbl);
                row.Children.Add(statusLbl);
                row.Children.Add(btn);

                _offlineContinentStatusLabels[continent] = statusLbl;
                _offlineContinentButtons[continent]      = btn;
                UpdateContinentRow(continent, latestTag: null);

                _offlineDataPanel.Children.Add(row);
            }
        }

        // Aggiorna etichetta di stato e testo del bottone per un continente.
        // latestTag = null quando non è ancora stato fatto un controllo
        // aggiornamenti in questa apertura della finestra (mostra solo se
        // scaricato o no, senza sapere se è la versione più recente).
        private void UpdateContinentRow(string continent, string? latestTag)
        {
            if (!_offlineContinentStatusLabels.TryGetValue(continent, out var label)) return;
            if (!_offlineContinentButtons.TryGetValue(continent, out var btn)) return;

            bool    downloaded   = PoiOfflineDatabase.IsContinentDownloaded(continent);
            string? localVersion = PoiOfflineDatabase.GetContinentVersion(continent);

            if (!downloaded)
            {
                label.Text       = Strings.Get("SettingsWindow_DatiNonScaricati");
                label.Foreground = Brushes.DimGray;
                btn.Content      = Strings.Get("SettingsWindow_Scarica");
            }
            else if (latestTag != null && localVersion != null &&
                     !localVersion.Equals(latestTag, StringComparison.OrdinalIgnoreCase))
            {
                label.Text       = string.Format(Strings.Get("SettingsWindow_AggiornamentoDatiDisponibile"), localVersion, latestTag);
                label.Foreground = Brushes.DarkOrange;
                btn.Content      = Strings.Get("SettingsWindow_Aggiorna");
            }
            else
            {
                label.Text       = string.Format(Strings.Get("SettingsWindow_DatiVersioneScaricata"), localVersion ?? "?");
                label.Foreground = Brushes.SeaGreen;
                // Etichetta diversa da "Scarica"/"Aggiorna": qui non c'è una
                // versione più recente, ma il pulsante resta comunque attivo
                // e forza un nuovo download/estrazione anche a versione
                // invariata (DownloadContinentAsync non ha alcun controllo
                // "già alla versione più recente, non fare nulla") — utile
                // per un CSV corrotto/incompleto o riscaricare dopo una
                // ri-generazione dei dati con lo stesso tag di release.
                btn.Content      = Strings.Get("SettingsWindow_Riscarica");
            }
        }

        private async Task CheckOfflineDataUpdatesAsync()
        {
            if (_offlineDataStatusText == null) return;
            _offlineDataStatusText.IsVisible  = true;
            _offlineDataStatusText.Foreground = Brushes.DimGray;
            _offlineDataStatusText.Text       = Strings.Get("SettingsWindow_ControlloAggiornamentiDatiInCorso");

            var release = await PoiOfflineDatabase.GetLatestDataReleaseAsync();
            if (release == null)
            {
                _offlineDataStatusText.Text       = Strings.Get("SettingsWindow_ControlloAggiornamentiDatiFallito");
                _offlineDataStatusText.Foreground = Brushes.Firebrick;
                return;
            }

            _offlineDataStatusText.Text = string.Format(Strings.Get("SettingsWindow_UltimaVersioneDatiDisponibile"), release.Tag);
            _lastKnownDataTag = release.Tag;
            foreach (var continent in PoiOfflineDatabase.Continents)
                UpdateContinentRow(continent, release.Tag);
        }

        private async Task OnDownloadContinentAsync(string continent)
        {
            if (!_offlineContinentButtons.TryGetValue(continent, out var btn)) return;
            if (!_offlineContinentStatusLabels.TryGetValue(continent, out var label)) return;

            btn.IsEnabled = false;
            var progress = new Progress<string>(msg =>
            {
                label.Text       = msg;
                label.Foreground = Brushes.DimGray;
            });

            var (success, message) = await PoiOfflineDatabase.DownloadContinentAsync(continent, progress);

            label.Foreground = success ? Brushes.SeaGreen : Brushes.Firebrick;
            btn.IsEnabled    = true;
            if (success)
                // Rietichetta correttamente ("Riscarica" se ora alla versione
                // più recente nota, altrimenti "Scarica"/"Aggiorna" come da
                // logica normale) invece di tornare sempre al generico
                // "Scarica" indipendentemente dallo stato risultante.
                UpdateContinentRow(continent, _lastKnownDataTag);
            else
                label.Text = message;
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
                _tbPreview.Text = string.Format(
                    Strings.Get("SettingsWindow_PreviewPagina"),
                    tmp.GetPageWidthKm().ToString("F1"), tmp.GetPageHeightKm().ToString("F1"));
        }

        private void OnOkClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            ResultSettings         = BuildSettingsFromControls();
            ResultCustomCategories = _customCategories.ToList();
            int langIdx            = _cbLanguage?.SelectedIndex ?? 0;
            ResultLanguage          = LanguageCodes[Math.Clamp(langIdx, 0, LanguageCodes.Length - 1)];
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

