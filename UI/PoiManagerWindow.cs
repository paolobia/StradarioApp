// =============================================================================
// UI/PoiManagerWindow.cs
//
// SINOSSI: Finestra principale di gestione dei gruppi di POI.
//   - Lista scorrevole: ogni gruppo è un'intestazione espandibile (swatch
//     colore + anteprima icona + nome + bottoni modifica/elimina/aggiungi POI);
//     se espanso, sotto vengono elencati i suoi POI con bottoni modifica/elimina.
//   - Toolbar: nuovo gruppo, importa KMZ, esporta KMZ.
//   - Lavora su una copia (deep clone) della lista passata: le modifiche
//     diventano definitive solo confermando con OK (proprietà Confirmed +
//     ResultGroups), come EditPageWindow/SettingsWindow.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Newtonsoft.Json;
using StradarioApp.Models;
using StradarioApp.Resources;
using StradarioApp.Services;

namespace StradarioApp.UI
{
    public class PoiManagerWindow : Window
    {
        public bool            Confirmed    { get; private set; } = false;
        public List<PoiGroup>  ResultGroups { get; private set; }

        private readonly List<PoiGroup> _workingGroups;
        private readonly double         _currentViewLon;
        private readonly double         _currentViewLat;
        private readonly PoiService     _poiSvc = new PoiService();

        private readonly HashSet<int> _expandedGroupIds = new();

        private StackPanel? _listPanel;
        private TextBlock?  _statusText;

        public PoiManagerWindow(List<PoiGroup> groups, double currentViewLon, double currentViewLat)
        {
            _workingGroups  = CloneGroups(groups);
            ResultGroups    = _workingGroups;
            _currentViewLon = currentViewLon;
            _currentViewLat = currentViewLat;

            Title  = Strings.Get("PoiManagerWindow_Titolo");
            Width  = 560;
            Height = 620;
            MinWidth  = 420;
            MinHeight = 400;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            BuildUI();
            RefreshList();
        }

        private static List<PoiGroup> CloneGroups(List<PoiGroup> groups)
        {
            string json = JsonConvert.SerializeObject(groups);
            return JsonConvert.DeserializeObject<List<PoiGroup>>(json) ?? new List<PoiGroup>();
        }

        // ---------------------------------------------------------------
        // UI
        // ---------------------------------------------------------------
        private void BuildUI()
        {
            var root = new DockPanel { LastChildFill = true };

            // ---- Toolbar ----
            var toolbar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing     = 6,
                Margin      = new Thickness(10, 8)
            };
            toolbar.Children.Add(MakeButton(Strings.Get("PoiManagerWindow_NuovoGruppo"), OnNewGroup));
            toolbar.Children.Add(new Separator { Width = 1, Background = Brushes.LightGray, Margin = new Thickness(4, 0) });
            toolbar.Children.Add(MakeButton(Strings.Get("PoiManagerWindow_ImportaKmz"), OnImportKmz));
            toolbar.Children.Add(MakeButton(Strings.Get("PoiManagerWindow_EsportaKmz"), OnExportKmz));
            var toolbarBorder = new Border
            {
                Background      = new SolidColorBrush(Color.Parse("#F0F0F0")),
                BorderBrush     = Brushes.LightGray,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Child           = toolbar
            };
            DockPanel.SetDock(toolbarBorder, Dock.Top);
            root.Children.Add(toolbarBorder);

            // ---- Stato/errore ----
            _statusText = new TextBlock
            {
                Margin       = new Thickness(10, 4),
                FontSize     = 11,
                Foreground   = Brushes.DimGray,
                TextWrapping = TextWrapping.Wrap
            };
            DockPanel.SetDock(_statusText, Dock.Top);
            root.Children.Add(_statusText);

            // ---- Bottoni OK/Annulla ----
            var btnRow = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing             = 8,
                Margin              = new Thickness(10)
            };
            var btnOk     = new Button { Content = Strings.Get("PoiManagerWindow_Ok"),      Width = 90 };
            var btnCancel = new Button { Content = Strings.Get("PoiManagerWindow_Annulla"), Width = 90 };
            btnOk.Click     += (_, _) => { Confirmed = true; ResultGroups = _workingGroups; Close(); };
            btnCancel.Click += (_, _) => Close();
            btnRow.Children.Add(btnOk);
            btnRow.Children.Add(btnCancel);
            DockPanel.SetDock(btnRow, Dock.Bottom);
            root.Children.Add(btnRow);

            // ---- Lista gruppi/POI ----
            _listPanel = new StackPanel { Spacing = 3, Margin = new Thickness(10, 4) };
            var scroll = new ScrollViewer { Content = _listPanel };
            root.Children.Add(scroll);

            Content = root;
        }

        private Button MakeButton(string text, EventHandler<RoutedEventArgs> handler)
        {
            var btn = new Button { Content = text, Padding = new Thickness(8, 4), FontSize = 12 };
            btn.Click += handler;
            return btn;
        }

        private void RefreshList()
        {
            if (_listPanel == null) return;
            _listPanel.Children.Clear();

            if (_workingGroups.Count == 0)
            {
                _listPanel.Children.Add(new TextBlock
                {
                    Text       = Strings.Get("PoiManagerWindow_NessunGruppo"),
                    FontSize   = 12,
                    Foreground = Brushes.Gray,
                    Margin     = new Thickness(6, 12),
                    TextWrapping = TextWrapping.Wrap
                });
                return;
            }

            foreach (var group in _workingGroups)
            {
                _listPanel.Children.Add(BuildGroupHeader(group));

                if (_expandedGroupIds.Contains(group.Id))
                {
                    if (group.Items.Count == 0)
                    {
                        _listPanel.Children.Add(new TextBlock
                        {
                            Text       = Strings.Get("PoiManagerWindow_NessunPoiNelGruppo"),
                            FontSize   = 11,
                            Foreground = Brushes.Gray,
                            Margin     = new Thickness(30, 1, 4, 3)
                        });
                    }
                    foreach (var item in group.Items)
                        _listPanel.Children.Add(BuildItemRow(group, item));
                }
            }
        }

        private Control BuildGroupHeader(PoiGroup group)
        {
            bool isExpanded = _expandedGroupIds.Contains(group.Id);

            var border = new Border
            {
                Background      = new SolidColorBrush(Color.Parse("#EEF3FA")),
                BorderBrush     = Brushes.LightGray,
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(4),
                Padding         = new Thickness(6, 4),
                Cursor          = new Cursor(StandardCursorType.Hand)
            };

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto,Auto") };

            var expandGlyph = new TextBlock
            {
                Text = isExpanded ? "▾" : "▸",
                Width = 16,
                FontWeight = FontWeight.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(expandGlyph, 0);
            row.Children.Add(expandGlyph);

            var iconImg = new Image
            {
                Width = 22, Height = 22,
                Margin = new Thickness(4, 0, 8, 0),
                Stretch = Stretch.Uniform
            };
            using (var bmp = PoiIconRenderer.RenderToBitmap(group.Icon, PoiIconRenderer.ParseColor(group.ColorHex), 40))
                iconImg.Source = SkiaImageHelper.ToAvaloniaBitmap(bmp);
            Grid.SetColumn(iconImg, 1);
            row.Children.Add(iconImg);

            var info = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock { Text = group.Name, FontWeight = FontWeight.Bold, FontSize = 13 });
            info.Children.Add(new TextBlock
            {
                Text       = string.IsNullOrWhiteSpace(group.Description)
                    ? string.Format(Strings.Get("PoiManagerWindow_ConteggioPoi"), group.Items.Count)
                    : string.Format(Strings.Get("PoiManagerWindow_ConteggioPoiConDescrizione"), group.Items.Count, group.Description),
                FontSize   = 10,
                Foreground = Brushes.DimGray
            });
            Grid.SetColumn(info, 2);
            row.Children.Add(info);

            var btnAddItem = new Button { Content = Strings.Get("PoiManagerWindow_AggiungiPoiBtn"), Padding = new Thickness(4, 2), FontSize = 11 };
            ToolTip.SetTip(btnAddItem, Strings.Get("PoiManagerWindow_AggiungiPoiTooltip"));
            btnAddItem.Click += async (_, _) => await AddPoiToGroup(group);
            Grid.SetColumn(btnAddItem, 3);
            row.Children.Add(btnAddItem);

            var btnEdit = new Button { Content = "✏", Padding = new Thickness(4, 2), FontSize = 11, Foreground = Brushes.SteelBlue, Background = Brushes.Transparent };
            ToolTip.SetTip(btnEdit, Strings.Get("PoiManagerWindow_ModificaGruppoTooltip"));
            btnEdit.Click += async (_, _) => await EditGroup(group);
            Grid.SetColumn(btnEdit, 4);
            row.Children.Add(btnEdit);

            var btnDelete = new Button { Content = "✕", Padding = new Thickness(4, 2), FontSize = 11, Foreground = Brushes.Crimson, Background = Brushes.Transparent };
            ToolTip.SetTip(btnDelete, Strings.Get("PoiManagerWindow_EliminaGruppoTooltip"));
            btnDelete.Click += (_, _) =>
            {
                _workingGroups.Remove(group);
                _expandedGroupIds.Remove(group.Id);
                RefreshList();
            };
            Grid.SetColumn(btnDelete, 5);
            row.Children.Add(btnDelete);

            border.Child = row;
            border.PointerPressed += (_, e) =>
            {
                // Non attivare il toggle se il click è su uno dei bottoni della riga
                if (e.Source is Button || (e.Source is Control c && c.FindAncestorOfType<Button>() != null)) return;

                if (isExpanded) _expandedGroupIds.Remove(group.Id);
                else            _expandedGroupIds.Add(group.Id);
                RefreshList();
            };

            return border;
        }

        private Control BuildItemRow(PoiGroup group, PoiItem item)
        {
            var border = new Border
            {
                Background      = Brushes.White,
                BorderBrush     = Brushes.Gainsboro,
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(3),
                Margin          = new Thickness(30, 1, 4, 1),
                Padding         = new Thickness(6, 3)
            };

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };

            var info = new StackPanel { Spacing = 1 };
            info.Children.Add(new TextBlock { Text = item.Label, FontSize = 12 });
            info.Children.Add(new TextBlock
            {
                Text       = $"{item.Lon:F4}°E, {item.Lat:F4}°N",
                FontSize   = 10,
                Foreground = Brushes.DimGray
            });
            Grid.SetColumn(info, 0);
            row.Children.Add(info);

            var btnEdit = new Button { Content = "✏", Padding = new Thickness(4, 2), FontSize = 10, Foreground = Brushes.SteelBlue, Background = Brushes.Transparent };
            btnEdit.Click += async (_, _) => await EditPoi(group, item);
            Grid.SetColumn(btnEdit, 1);
            row.Children.Add(btnEdit);

            var btnDelete = new Button { Content = "✕", Padding = new Thickness(4, 2), FontSize = 10, Foreground = Brushes.Crimson, Background = Brushes.Transparent };
            btnDelete.Click += (_, _) =>
            {
                group.Items.Remove(item);
                RefreshList();
            };
            Grid.SetColumn(btnDelete, 2);
            row.Children.Add(btnDelete);

            border.Child = row;
            return border;
        }

        // ---------------------------------------------------------------
        // Azioni gruppo
        // ---------------------------------------------------------------
        private async void OnNewGroup(object? sender, RoutedEventArgs e)
        {
            var newGroup = new PoiGroup { Id = _poiSvc.GetNextGroupId(_workingGroups) };
            var win = new PoiGroupEditWindow(newGroup);
            await win.ShowDialog(this);
            if (!win.Confirmed) return;

            _workingGroups.Add(win.ResultGroup);
            _expandedGroupIds.Add(win.ResultGroup.Id);
            RefreshList();
        }

        private async Task EditGroup(PoiGroup group)
        {
            var win = new PoiGroupEditWindow(group);
            await win.ShowDialog(this);
            if (!win.Confirmed) return;

            int idx = _workingGroups.FindIndex(g => g.Id == group.Id);
            if (idx >= 0) _workingGroups[idx] = win.ResultGroup;
            RefreshList();
        }

        // ---------------------------------------------------------------
        // Azioni POI
        // ---------------------------------------------------------------
        private async Task AddPoiToGroup(PoiGroup group)
        {
            var newItem = new PoiItem { Id = 0 };
            var win = new PoiItemEditWindow(newItem, _currentViewLon, _currentViewLat, group.ColorHex);
            await win.ShowDialog(this);
            if (!win.Confirmed) return;

            win.ResultItem.Id = _poiSvc.GetNextItemId(group);
            group.Items.Add(win.ResultItem);
            _expandedGroupIds.Add(group.Id);
            RefreshList();
        }

        private async Task EditPoi(PoiGroup group, PoiItem item)
        {
            var win = new PoiItemEditWindow(item, _currentViewLon, _currentViewLat, group.ColorHex);
            await win.ShowDialog(this);
            if (!win.Confirmed) return;

            int idx = group.Items.FindIndex(it => it.Id == item.Id);
            if (idx >= 0) group.Items[idx] = win.ResultItem;
            RefreshList();
        }

        // ---------------------------------------------------------------
        // KMZ import/export
        // ---------------------------------------------------------------
        private async void OnImportKmz(object? sender, RoutedEventArgs e)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title          = Strings.Get("PoiManagerWindow_ImportaTitolo"),
                AllowMultiple  = false,
                FileTypeFilter = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType(Strings.Get("PoiManagerWindow_FiltroKmzKml")) { Patterns = new[] { "*.kmz", "*.kml" } },
                    new Avalonia.Platform.Storage.FilePickerFileType(Strings.Get("PoiManagerWindow_FiltroTuttiFile")) { Patterns = new[] { "*.*" } }
                }
            });
            if (files.Count == 0) return;

            try
            {
                var tempProject = new StradarioProject { PoiGroups = _workingGroups };
                var imported = await _poiSvc.ImportKmzAsync(files[0].Path.LocalPath, tempProject);

                if (imported.Count == 0)
                {
                    SetStatus(Strings.Get("PoiManagerWindow_NessunGruppoPoiNelFile"), true);
                    return;
                }

                _workingGroups.AddRange(imported);
                foreach (var g in imported) _expandedGroupIds.Add(g.Id);
                RefreshList();
                SetStatus(string.Format(Strings.Get("PoiManagerWindow_ImportatiGruppi"), imported.Count, imported.Sum(g => g.Items.Count)), false);
            }
            catch (Exception ex)
            {
                SetStatus(string.Format(Strings.Get("PoiManagerWindow_ErroreImportazione"), ex.Message), true);
            }
        }

        private async void OnExportKmz(object? sender, RoutedEventArgs e)
        {
            if (_workingGroups.Count == 0)
            {
                SetStatus(Strings.Get("PoiManagerWindow_NessunGruppoDaEsportare"), true);
                return;
            }

            var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
            {
                Title             = Strings.Get("PoiManagerWindow_EsportaTitolo"),
                DefaultExtension  = "kmz",
                SuggestedFileName = "poi",
                FileTypeChoices   = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType(Strings.Get("PoiManagerWindow_FiltroKmz")) { Patterns = new[] { "*.kmz" } }
                }
            });
            if (file == null) return;

            try
            {
                await _poiSvc.ExportKmzAsync(_workingGroups, file.Path.LocalPath);
                SetStatus(string.Format(Strings.Get("PoiManagerWindow_Esportato"), file.Path.LocalPath), false);
            }
            catch (Exception ex)
            {
                SetStatus(string.Format(Strings.Get("PoiManagerWindow_ErroreEsportazione"), ex.Message), true);
            }
        }

        private void SetStatus(string msg, bool isError)
        {
            if (_statusText == null) return;
            _statusText.Text       = msg;
            _statusText.Foreground = isError ? Brushes.Crimson : Brushes.DimGray;
        }
    }
}
