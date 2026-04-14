using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using StradarioApp.Models;
using StradarioApp.Services;

namespace StradarioApp.UI
{
    public class MainWindow : Window
    {
        // ---- Application state ----
        private StradarioProject _project = new StradarioProject();
        private string?          _projectPath;
        private bool             _isDirty;

        // ---- Map view state ----
        private double _viewCenterLon = 12.4964;
        private double _viewCenterLat = 41.9028;
        private double _viewZoom      = 10.0;

        // ---- Mouse drag state ----
        private bool   _isDragging;
        private Point  _dragStart;
        private double _dragCenterLon, _dragCenterLat;

        // ---- Page drag state ----
        private bool   _isDraggingPage;
        private double _pageDragStartLon, _pageDragStartLat;
        private MapPage? _draggingPage;

        // ---- Mode ----
        private bool _addPageMode;
        private int? _selectedPageId;
        private int  _nextPageId = 1;

        // ---- Services ----
        private readonly MapRenderer _renderer = new MapRenderer();

        // ---- UI references ----
        private MapCanvas    _mapCanvas   = null!;
        private StackPanel   _pageListPanel = null!;
        private TextBlock    _statusBar   = null!;

        public MainWindow()
        {
            Title  = "StradarioApp";
            Width  = 1100;
            Height = 720;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            Closing += OnWindowClosing;

            BuildUI();
            UpdateTitle();
            RefreshPageList();
        }

        // ====================================================================
        // UI construction
        // ====================================================================

        private void BuildUI()
        {
            // ---- Toolbar ----
            var toolbar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing     = 4,
                Margin      = new Thickness(4),
            };

            Button MakeBtn(string label, Action handler)
            {
                var btn = new Button { Content = label, Padding = new Thickness(8, 4) };
                btn.Click += (_, _) => handler();
                return btn;
            }

            toolbar.Children.Add(MakeBtn("🗺️ Nuovo",         () => NewProject()));
            toolbar.Children.Add(MakeBtn("📂 Apri",           OnOpen));
            toolbar.Children.Add(MakeBtn("💾 Salva",          OnSave));
            toolbar.Children.Add(MakeBtn("💾 Salva come",     OnSaveAs));
            toolbar.Children.Add(new Separator { Width = 8 });
            toolbar.Children.Add(MakeBtn("➕ Aggiungi pagina", OnAddPage));
            toolbar.Children.Add(MakeBtn("📄 Genera PDF",     OnGeneratePdf));
            toolbar.Children.Add(new Separator { Width = 8 });
            toolbar.Children.Add(MakeBtn("🔄 Refresh mappa",  OnRefreshMap));
            toolbar.Children.Add(MakeBtn("⚙️ Impostazioni",   OnOpenSettings));

            // ---- Left panel ----
            var leftPanel = new DockPanel { Width = 280, LastChildFill = true };

            var leftHeader = new TextBlock
            {
                Text       = "Pagine",
                FontWeight = FontWeight.Bold,
                Margin     = new Thickness(8, 6, 8, 2)
            };
            DockPanel.SetDock(leftHeader, Dock.Top);

            var scrollViewer = new ScrollViewer { VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
            _pageListPanel = new StackPanel { Spacing = 2, Margin = new Thickness(4) };
            scrollViewer.Content = _pageListPanel;

            leftPanel.Children.Add(leftHeader);
            leftPanel.Children.Add(scrollViewer);

            // ---- Map canvas ----
            _mapCanvas = new MapCanvas
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment   = VerticalAlignment.Stretch,
            };
            _mapCanvas.PaintSkia += OnPaintSkia;
            _mapCanvas.PointerPressed  += OnPointerPressed;
            _mapCanvas.PointerMoved    += OnPointerMoved;
            _mapCanvas.PointerReleased += OnPointerReleased;
            _mapCanvas.PointerWheelChanged += OnWheelChanged;

            // ---- Status bar ----
            _statusBar = new TextBlock
            {
                Text   = "Pronto",
                Margin = new Thickness(8, 2)
            };

            // ---- Layout ----
            var splitGrid = new Grid();
            splitGrid.ColumnDefinitions.Add(new ColumnDefinition(280, GridUnitType.Pixel));
            splitGrid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));

            var leftBorder = new Border
            {
                BorderBrush     = Brushes.LightGray,
                BorderThickness = new Thickness(0, 0, 1, 0),
                Child           = leftPanel
            };

            Grid.SetColumn(leftBorder, 0);
            Grid.SetColumn(_mapCanvas, 1);
            splitGrid.Children.Add(leftBorder);
            splitGrid.Children.Add(_mapCanvas);

            var mainLayout = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(toolbar,    Dock.Top);
            DockPanel.SetDock(_statusBar, Dock.Bottom);
            mainLayout.Children.Add(toolbar);
            mainLayout.Children.Add(_statusBar);
            mainLayout.Children.Add(splitGrid);

            Content = mainLayout;
        }

        // ====================================================================
        // Paint
        // ====================================================================

        private void OnPaintSkia(object? sender, SkiaPaintEventArgs e)
        {
            _renderer.RenderMap(
                e.Canvas, e.Width, e.Height,
                _viewCenterLon, _viewCenterLat, _viewZoom,
                _project.Settings.TileServerUrl,
                _project.Pages, _selectedPageId,
                () => Dispatcher.UIThread.Post(_mapCanvas.InvalidateVisual, DispatcherPriority.Background));
        }

        // ====================================================================
        // Mouse interaction
        // ====================================================================

        private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            var pt     = e.GetPosition(_mapCanvas);
            var props  = e.GetCurrentPoint(_mapCanvas).Properties;

            if (props.IsRightButtonPressed)
            {
                _addPageMode = false;
                SetStatus("Modalità aggiunta pagina annullata.");
                return;
            }

            if (!props.IsLeftButtonPressed) return;

            if (_addPageMode)
            {
                AddPageAtLocation(pt.X, pt.Y);
                _addPageMode = false;
                SetStatus("Pagina aggiunta.");
                return;
            }

            // Check if clicking on a page
            var hit = HitTestPage(pt.X, pt.Y);

            if (hit != null && hit.Id == _selectedPageId)
            {
                // Start dragging the selected page
                _isDraggingPage = true;
                var (lon, lat)  = PixelToGeo(pt.X, pt.Y);
                _pageDragStartLon = lon;
                _pageDragStartLat = lat;
                _draggingPage   = hit;
                e.Pointer.Capture(_mapCanvas);
                return;
            }

            if (hit != null)
            {
                _selectedPageId = hit.Id;
                CenterMapOnPage(hit);
                RefreshPageList();
                _mapCanvas.InvalidateVisual();
            }

            // Start panning
            _isDragging    = true;
            _dragStart     = pt;
            _dragCenterLon = _viewCenterLon;
            _dragCenterLat = _viewCenterLat;
            e.Pointer.Capture(_mapCanvas);
        }

        private void OnPointerMoved(object? sender, PointerEventArgs e)
        {
            var pt = e.GetPosition(_mapCanvas);

            if (_isDraggingPage && _draggingPage != null)
            {
                var (lon, lat) = PixelToGeo(pt.X, pt.Y);
                double dLon = lon - _pageDragStartLon;
                double dLat = lat - _pageDragStartLat;

                var b = _draggingPage.GeoBounds;
                _draggingPage.GeoBounds = new GeoRect
                {
                    MinLon = b.MinLon + dLon,
                    MaxLon = b.MaxLon + dLon,
                    MinLat = b.MinLat + dLat,
                    MaxLat = b.MaxLat + dLat
                };
                _pageDragStartLon = lon;
                _pageDragStartLat = lat;
                _mapCanvas.InvalidateVisual();
                return;
            }

            if (_isDragging)
            {
                double w  = _mapCanvas.Bounds.Width;
                double h  = _mapCanvas.Bounds.Height;
                double dx = pt.X - _dragStart.X;
                double dy = pt.Y - _dragStart.Y;

                // Longitude: linear
                double degPerPxLon = 360.0 / (256.0 * Math.Pow(2.0, _viewZoom));
                _viewCenterLon = _dragCenterLon - dx * degPerPxLon;

                // Latitude: non-linear (Mercator)
                double centerTileY0 = GeoUtils.LatToTileY(_dragCenterLat, _viewZoom);
                double pxPerTile    = 256.0;
                double newTileY     = centerTileY0 - dy / pxPerTile;
                _viewCenterLat = GeoUtils.TileYToLat(newTileY, _viewZoom);

                _mapCanvas.InvalidateVisual();
            }
        }

        private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (_isDraggingPage)
            {
                _isDraggingPage = false;
                _draggingPage   = null;
                MarkDirty();
            }
            _isDragging = false;
            e.Pointer.Capture(null);
        }

        private void OnWheelChanged(object? sender, PointerWheelEventArgs e)
        {
            double delta = e.Delta.Y > 0 ? 0.5 : -0.5;
            _viewZoom = Math.Clamp(_viewZoom + delta, 1.0, 19.0);
            _mapCanvas.InvalidateVisual();
        }

        // ====================================================================
        // Helpers
        // ====================================================================

        private (double lon, double lat) PixelToGeo(double x, double y)
        {
            return GeoUtils.PixelToGeo(x, y, _viewCenterLon, _viewCenterLat, _viewZoom,
                _mapCanvas.Bounds.Width, _mapCanvas.Bounds.Height);
        }

        private MapPage? HitTestPage(double px, double py)
        {
            double w = _mapCanvas.Bounds.Width;
            double h = _mapCanvas.Bounds.Height;

            foreach (var page in _project.Pages)
            {
                var (x1, y1) = GeoUtils.GeoToPixel(page.GeoBounds.MinLon, page.GeoBounds.MaxLat,
                    _viewCenterLon, _viewCenterLat, _viewZoom, w, h);
                var (x2, y2) = GeoUtils.GeoToPixel(page.GeoBounds.MaxLon, page.GeoBounds.MinLat,
                    _viewCenterLon, _viewCenterLat, _viewZoom, w, h);

                double minX = Math.Min(x1, x2), maxX = Math.Max(x1, x2);
                double minY = Math.Min(y1, y2), maxY = Math.Max(y1, y2);

                if (px >= minX && px <= maxX && py >= minY && py <= maxY)
                    return page;
            }
            return null;
        }

        private void AddPageAtLocation(double px, double py)
        {
            var (lon, lat) = PixelToGeo(px, py);
            var bounds = GeoUtils.CalcPageBounds(lon, lat, _project.Settings);

            var page = new MapPage
            {
                Id         = _nextPageId++,
                Label      = $"P{_project.Pages.Count + 1}",
                GeoBounds  = bounds,
                PageNumber = _project.Pages.Count + 1
            };
            _project.Pages.Add(page);
            _selectedPageId = page.Id;
            MarkDirty();
            RefreshPageList();
            _mapCanvas.InvalidateVisual();
        }

        private void CenterMapOnPage(MapPage page)
        {
            _viewCenterLon = page.GeoBounds.CenterLon;
            _viewCenterLat = page.GeoBounds.CenterLat;
        }

        // ====================================================================
        // Page list
        // ====================================================================

        private void RefreshPageList()
        {
            _pageListPanel.Children.Clear();

            foreach (var page in _project.Pages)
            {
                var pageRef = page; // capture

                var rowBorder = new Border
                {
                    Padding         = new Thickness(4, 2),
                    CornerRadius    = new CornerRadius(3),
                    Background      = page.Id == _selectedPageId
                        ? new SolidColorBrush(Color.FromRgb(200, 220, 255))
                        : Brushes.Transparent
                };

                var label = new TextBlock
                {
                    Text               = $"{page.Label}  ({page.GeoBounds.CenterLon:F2}, {page.GeoBounds.CenterLat:F2})",
                    VerticalAlignment  = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };

                var editBtn = new Button { Content = "✏", Padding = new Thickness(4, 1), FontSize = 11 };
                ToolTip.SetTip(editBtn, "Modifica pagina");
                editBtn.Click += async (_, _) => await EditPage(pageRef);

                var deleteBtn = new Button { Content = "✕", Padding = new Thickness(4, 1), FontSize = 11 };
                ToolTip.SetTip(deleteBtn, "Elimina pagina");
                deleteBtn.Click += (_, _) => DeletePage(pageRef);

                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
                row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

                Grid.SetColumn(label,     0);
                Grid.SetColumn(editBtn,   1);
                Grid.SetColumn(deleteBtn, 2);
                row.Children.Add(label);
                row.Children.Add(editBtn);
                row.Children.Add(deleteBtn);

                rowBorder.Child = row;

                // Click: select + center
                rowBorder.PointerPressed += (_, _) =>
                {
                    _selectedPageId = pageRef.Id;
                    CenterMapOnPage(pageRef);
                    _viewZoom = GeoUtils.CalcOptimalZoom(_project.Settings, pageRef.GeoBounds.CenterLat) + 1.0;
                    RefreshPageList();
                    _mapCanvas.InvalidateVisual();
                };

                // Double-click: edit
                rowBorder.DoubleTapped += async (_, _) => await EditPage(pageRef);

                _pageListPanel.Children.Add(rowBorder);
            }
        }

        private async Task EditPage(MapPage page)
        {
            var dlg = new EditPageWindow(page, _project.Settings) { ShowInTaskbar = false };
            var result = await dlg.ShowDialog<bool?>(this);
            if (result == true)
            {
                MarkDirty();
                RefreshPageList();
                _mapCanvas.InvalidateVisual();
            }
        }

        private void DeletePage(MapPage page)
        {
            _project.Pages.Remove(page);
            if (_selectedPageId == page.Id) _selectedPageId = null;
            MarkDirty();
            RefreshPageList();
            _mapCanvas.InvalidateVisual();
        }

        // ====================================================================
        // Toolbar actions
        // ====================================================================

        private void NewProject()
        {
            if (_isDirty && !ConfirmDiscardSync()) return;
            _project       = new StradarioProject();
            _projectPath   = null;
            _selectedPageId = null;
            _nextPageId    = 1;
            _viewCenterLon = _project.ViewCenterLon;
            _viewCenterLat = _project.ViewCenterLat;
            _viewZoom      = _project.ViewZoom;
            _isDirty = false;
            UpdateTitle();
            RefreshPageList();
            _mapCanvas.InvalidateVisual();
        }

        private void OnNew() => NewProject();

        private async void OnOpen()
        {
            if (_isDirty && !ConfirmDiscardSync()) return;

            var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "Apri progetto",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType("Progetto Stradario")
                    {
                        Patterns = new[] { "*.stradario" }
                    }
                }
            });

            if (files.Count == 0) return;
            string path = files[0].Path.LocalPath;

            try
            {
                _project     = ProjectService.Load(path);
                _projectPath = path;
                _selectedPageId = null;
                _nextPageId  = (_project.Pages.Count > 0 ? _project.Pages.Max(p => p.Id) + 1 : 1);
                _viewCenterLon = _project.ViewCenterLon;
                _viewCenterLat = _project.ViewCenterLat;
                _viewZoom      = _project.ViewZoom;
                _isDirty = false;
                UpdateTitle();
                RefreshPageList();
                _mapCanvas.InvalidateVisual();
            }
            catch (Exception ex)
            {
                await ShowError($"Errore di apertura: {ex.Message}");
            }
        }

        private async void OnSave()
        {
            if (_projectPath == null) { OnSaveAs(); return; }
            await DoSave(_projectPath);
        }

        private async void OnSaveAs()
        {
            var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
            {
                Title           = "Salva progetto",
                DefaultExtension = "stradario",
                FileTypeChoices = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType("Progetto Stradario")
                    {
                        Patterns = new[] { "*.stradario" }
                    }
                }
            });

            if (file == null) return;
            await DoSave(file.Path.LocalPath);
        }

        private async Task DoSave(string path)
        {
            try
            {
                ProjectService.UpdateViewState(_project, _viewCenterLon, _viewCenterLat, _viewZoom);
                ProjectService.Save(_project, path);
                _projectPath = path;
                _isDirty     = false;
                UpdateTitle();
            }
            catch (Exception ex)
            {
                await ShowError($"Errore di salvataggio: {ex.Message}");
            }
        }

        private void OnAddPage()
        {
            _addPageMode = true;
            SetStatus("Clicca sulla mappa per aggiungere una pagina. Tasto destro per annullare.");
        }

        private async void OnGeneratePdf()
        {
            if (_project.Pages.Count == 0)
            {
                await ShowError("Nessuna pagina da esportare.");
                return;
            }

            var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
            {
                Title            = "Salva PDF",
                DefaultExtension = "pdf",
                FileTypeChoices  = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType("PDF") { Patterns = new[] { "*.pdf" } }
                }
            });
            if (file == null) return;
            string pdfPath = file.Path.LocalPath;

            var progressWin = new ProgressWindow("Generazione PDF") { ShowInTaskbar = false };
            progressWin.Show(this);

            var progress = new Progress<(string message, double fraction)>(report =>
                progressWin.Report(report.message, report.fraction));

            try
            {
                await Task.Run(() => new PdfGenerator().GenerateAsync(pdfPath, _project, progress));
                progressWin.SafeClose();
                SetStatus($"PDF salvato: {pdfPath}");
            }
            catch (Exception ex)
            {
                progressWin.SafeClose();
                await ShowError($"Errore nella generazione PDF: {ex.Message}");
            }
        }

        private void OnRefreshMap()
        {
            _renderer.ClearCache();
            _mapCanvas.InvalidateVisual();
        }

        private async void OnOpenSettings()
        {
            var dlg = new SettingsWindow(_project.Settings) { ShowInTaskbar = false };
            var result = await dlg.ShowDialog<bool?>(this);
            if (result == true && dlg.ResultSettings != null)
            {
                bool serverChanged = _project.Settings.TileServerUrl != dlg.ResultSettings.TileServerUrl;
                _project.Settings = dlg.ResultSettings;
                if (serverChanged) _renderer.ClearCache();
                MarkDirty();
                _mapCanvas.InvalidateVisual();
            }
        }

        // ====================================================================
        // State helpers
        // ====================================================================

        private void MarkDirty()
        {
            _isDirty = true;
            UpdateTitle();
        }

        private void UpdateTitle()
        {
            string name  = _project.ProjectName;
            string dirty = _isDirty ? " •" : string.Empty;
            string path  = _projectPath != null ? $" [{Path.GetFileName(_projectPath)}]" : string.Empty;
            Title = $"StradarioApp — {name}{path}{dirty}";
        }

        private void SetStatus(string msg) => _statusBar.Text = msg;

        private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
        {
            if (!_isDirty) return;

            e.Cancel = true;

            var dlg = new Window
            {
                Title  = "Modifiche non salvate",
                Width  = 360,
                Height = 150,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            bool? choice = null;

            var saveBtn   = new Button { Content = "💾 Salva ed esci",       Margin = new Thickness(4) };
            var discardBtn= new Button { Content = "🗑 Esci senza salvare",   Margin = new Thickness(4) };
            var cancelBtn = new Button { Content = "Annulla",                 Margin = new Thickness(4) };

            saveBtn.Click   += (_, _) => { choice = true;  dlg.Close(); };
            discardBtn.Click+= (_, _) => { choice = false; dlg.Close(); };
            cancelBtn.Click += (_, _) => { choice = null;  dlg.Close(); };

            dlg.Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "Ci sono modifiche non salvate. Cosa vuoi fare?", TextWrapping = TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Children = { saveBtn, discardBtn, cancelBtn }
                    }
                }
            };

            await dlg.ShowDialog(this);

            if (choice == true)
            {
                if (_projectPath != null)
                    await DoSave(_projectPath);
                else
                {
                    OnSaveAs();
                    // If still dirty (save was cancelled), don't close
                    if (_isDirty) return;
                }
            }
            else if (choice == null)
            {
                // Cancelled
                return;
            }

            // Allow close
            Closing -= OnWindowClosing;
            Close();
        }

        private bool ConfirmDiscardSync()
        {
            // Simplified sync check — in real use we'd show a dialog
            // For now just return true (discard changes). Full dialog is async above.
            return true;
        }

        private async Task ShowError(string message)
        {
            var dlg = new Window
            {
                Title  = "Errore",
                Width  = 380,
                Height = 140,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            var ok = new Button { Content = "OK", HorizontalAlignment = HorizontalAlignment.Center };
            ok.Click += (_, _) => dlg.Close();
            dlg.Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    ok
                }
            };
            await dlg.ShowDialog(this);
        }
    }
}
