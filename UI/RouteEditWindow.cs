// =============================================================================
// UI/RouteEditWindow.cs
//
// SINOSSI: Dialog di modifica di un percorso, a due tab (stesso pattern di
//   SettingsWindow): "Percorso" (etichetta, descrizione, date, colore) e
//   "Punti" (una pagina per punto — lon/lat, POI e relativo pannello — con
//   frecce ◀/▶ per scorrere avanti/indietro). Prima era tutto su una sola
//   colonna con l'elenco punti compresso in righe strette; separare in tab
//   dà alla descrizione del percorso spazio reale a destra di Da/A, e a ogni
//   punto un'intera pagina invece di una riga sottile.
//   ANTEPRIMA LIVE: ogni modifica viene applicata SUBITO all'oggetto
//   `Percorso` reale passato dal chiamante (route), non a una copia — così
//   MainWindow può ridisegnare la mappa/l'albero ad ogni cambio tramite il
//   callback `onLiveChange`. "OK" rende le modifiche definitive (nessun'altra
//   azione necessaria, l'oggetto è già aggiornato); "Annulla"/chiusura con la
//   X ripristinano `route` allo stato catturato all'apertura del dialog
//   (v. RestoreBackup) — prima le modifiche venivano accumulate in una copia
//   e applicate solo a "OK" (nessuna anteprima, e "Annulla" non riportava
//   davvero indietro nulla perché non c'era nulla da riportare).
// =============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using StradarioApp.Models;
using StradarioApp.Resources;
using StradarioApp.Services;

namespace StradarioApp.UI
{
    public class RouteEditWindow : Window
    {
        public bool Confirmed { get; private set; } = false;

        private readonly Percorso  _route;
        private readonly double    _currentViewLon;
        private readonly double    _currentViewLat;
        private readonly Action?   _onLiveChange;
        private readonly Action<GeoPoint, Percorso>? _onKeepDeletedPoiAsStandalone;
        private readonly List<GeoPoint> _workingPoints;
        private string _selectedColor;

        // Snapshot preso all'apertura, per poter ripristinare `route` se
        // l'utente preme Annulla/chiude la finestra senza confermare.
        private readonly string    _backupLabel;
        private readonly string    _backupDescription;
        private readonly string    _backupColorHex;
        private readonly DateTime? _backupStart;
        private readonly DateTime? _backupEnd;
        private readonly List<GeoPoint> _backupPoints;

        private TextBox?   _tbLabel;
        private TextBox?   _tbDescription;
        private WrapPanel?  _colorPanel;
        private TextBlock?  _statusText;
        private TextBlock?  _summaryText;
        private DateTimeFieldPair? _daField;
        private DateTimeFieldPair? _aField;

        // Tab "Punti": indice della pagina correntemente mostrata.
        private int _pointIndex = 0;
        private ContentControl? _pointPageHost;
        private TextBlock? _pointNavTitle;
        private Button? _btnPointPrev;
        private Button? _btnPointNext;

        public RouteEditWindow(Percorso route, double currentViewLon, double currentViewLat, Action? onLiveChange = null,
            Action<GeoPoint, Percorso>? onKeepDeletedPoiAsStandalone = null)
        {
            _route          = route;
            _currentViewLon = currentViewLon;
            _currentViewLat = currentViewLat;
            _onLiveChange   = onLiveChange;
            _onKeepDeletedPoiAsStandalone = onKeepDeletedPoiAsStandalone;
            _selectedColor  = string.IsNullOrWhiteSpace(route.ColorHex) ? PercorsoRenderer.DefaultColorHex : route.ColorHex;
            // Lavora DIRETTAMENTE sulla lista/oggetti reali del percorso
            // (non una copia): ogni Add/Remove/modifica qui sotto è già
            // visibile su route.Points, il che è ciò che rende possibile
            // l'anteprima live sulla mappa.
            _workingPoints  = route.Points;

            _backupLabel       = route.Label;
            _backupDescription = route.Description;
            _backupColorHex    = _selectedColor;
            _backupStart       = route.StartDateTime;
            _backupEnd         = route.EndDateTime;
            _backupPoints      = route.Points.Select(ClonePoint).ToList();

            Title  = route.Id == 0 ? Strings.Get("RouteEditWindow_TitoloNuovo") : string.Format(Strings.Get("RouteEditWindow_TitoloModifica"), route.Label);
            Width  = 980;
            Height = 640;
            MinWidth  = 820;
            MinHeight = 480;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            BuildUI(route);
            RefreshPointPage();

            Closing += (_, _) =>
            {
                if (!Confirmed) RestoreBackup();
            };
        }

        internal static GeoPoint ClonePoint(GeoPoint p) => new GeoPoint
        {
            Lon = p.Lon, Lat = p.Lat,
            IsPoi = p.IsPoi, PoiLabel = p.PoiLabel, PoiDescription = p.PoiDescription, PoiIcon = p.PoiIcon
        };

        internal static Percorso ClonePercorso(Percorso p) => new Percorso
        {
            Id = p.Id, Label = p.Label, Description = p.Description, ColorHex = p.ColorHex,
            StartDateTime = p.StartDateTime, EndDateTime = p.EndDateTime, IsLocked = p.IsLocked,
            Points = p.Points.Select(ClonePoint).ToList()
        };

        private void RestoreBackup()
        {
            _route.Label         = _backupLabel;
            _route.Description   = _backupDescription;
            _route.ColorHex      = _backupColorHex;
            _route.StartDateTime = _backupStart;
            _route.EndDateTime   = _backupEnd;
            _route.Points.Clear();
            _route.Points.AddRange(_backupPoints.Select(ClonePoint));
            _onLiveChange?.Invoke();
        }

        private void BuildUI(Percorso r)
        {
            var root = new DockPanel { LastChildFill = true, Margin = new Thickness(16) };

            // ---- Bottoni OK/Annulla in basso ----
            var btnRow = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing             = 10,
                Margin              = new Thickness(0, 10, 0, 0)
            };
            var btnOk     = DialogUi.MakeDialogButton(Strings.Get("RouteEditWindow_Ok"), primary: true);
            var btnCancel = DialogUi.MakeDialogButton(Strings.Get("RouteEditWindow_Annulla"));
            btnOk.Click     += OnOkClick;
            // Il ripristino vero e proprio avviene nell'handler Closing
            // (coperto anche dalla X della finestra) — qui basta chiudere.
            btnCancel.Click += (_, _) => Close();
            btnRow.Children.Add(btnOk);
            btnRow.Children.Add(btnCancel);
            DockPanel.SetDock(btnRow, Dock.Bottom);
            root.Children.Add(btnRow);

            _statusText = new TextBlock
            {
                FontSize     = 10,
                Foreground   = Brushes.Crimson,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 4, 0, 0)
            };
            DockPanel.SetDock(_statusText, Dock.Bottom);
            root.Children.Add(_statusText);

            var tabs = new TabControl();
            tabs.Items.Add(new TabItem { Header = Strings.Get("RouteEditWindow_TabPercorso"), Content = BuildRouteTab(r) });
            tabs.Items.Add(new TabItem { Header = Strings.Get("RouteEditWindow_TabPunti"),     Content = BuildPointsTab() });
            root.Children.Add(tabs);

            Content = root;
        }

        // ---- Tab 1: dati del percorso ----------------------------------
        // Stesso schema del tab "Punti" (BuildPointPage): campi impilati in
        // alto in un DockPanel, Descrizione come ultimo figlio che riempie
        // (LastChildFill) tutto lo spazio residuo — non più affiancata a
        // Da/A, richiesta esplicita dopo la prima versione "a due colonne".
        private Control BuildRouteTab(Percorso r)
        {
            var outer = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 14, 0, 0) };

            var grid = new Grid
            {
                RowDefinitions    = new RowDefinitions("Auto,Auto,Auto,Auto"),
                ColumnDefinitions = new ColumnDefinitions("100,*"),
                Margin            = new Thickness(0, 0, 0, 14)
            };

            AddLabel(grid, Strings.Get("RouteEditWindow_Etichetta"), 0);
            _tbLabel = new TextBox { Text = r.Label };
            _tbLabel.LostFocus += (_, _) =>
            {
                string label = _tbLabel.Text?.Trim() ?? "";
                _route.Label = string.IsNullOrEmpty(label) ? Strings.Get("RouteEditWindow_LabelDefault") : label;
                _onLiveChange?.Invoke();
            };
            AddControl(grid, _tbLabel, 0);

            AddLabel(grid, Strings.Get("RouteEditWindow_Da"), 1);
            _daField = DialogUi.MakeDateTimeFieldPair(r.StartDateTime);
            AddControl(grid, _daField.Panel, 1);

            AddLabel(grid, Strings.Get("RouteEditWindow_A"), 2);
            _aField = DialogUi.MakeDateTimeFieldPair(r.EndDateTime);
            AddControl(grid, _aField.Panel, 2);

            // Finché "A" resta vuoto, segue quello che si scrive in "Da".
            DialogUi.WireAutoFillSecondFromFirst(_daField, _aField);

            void CommitDates()
            {
                _route.StartDateTime = DialogUi.CombineDateTimeFields(_daField!);
                _route.EndDateTime   = DialogUi.CombineDateTimeFields(_aField!);
                _onLiveChange?.Invoke();
            }
            _daField.Calendar.PropertyChanged += (_, _) => CommitDates();
            _aField.Calendar.PropertyChanged  += (_, _) => CommitDates();
            _daField.TimeChanged += _ => CommitDates();
            _aField.TimeChanged  += _ => CommitDates();

            AddLabel(grid, Strings.Get("RouteEditWindow_Colore"), 3);
            _colorPanel = new WrapPanel { Orientation = Orientation.Horizontal };
            AddControl(grid, _colorPanel, 3);
            BuildColorSwatches();

            DockPanel.SetDock(grid, Dock.Top);
            outer.Children.Add(grid);

            _summaryText = new TextBlock { FontSize = 11, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 14) };
            DockPanel.SetDock(_summaryText, Dock.Top);
            outer.Children.Add(_summaryText);
            RecalcSummary();

            // Ultimo figlio (fill): Descrizione, a piena larghezza e
            // riempie tutto lo spazio verticale residuo del tab.
            var descRow = new Grid { ColumnDefinitions = new ColumnDefinitions("100,*") };
            descRow.Children.Add(new TextBlock { Text = Strings.Get("RouteEditWindow_Descrizione"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 8, 8, 0) });
            _tbDescription = new TextBox
            {
                Text          = r.Description,
                AcceptsReturn = true,
                TextWrapping  = TextWrapping.Wrap,
                VerticalContentAlignment = VerticalAlignment.Top,
                VerticalAlignment = VerticalAlignment.Stretch,
                MinHeight     = 90
            };
            _tbDescription.LostFocus += (_, _) =>
            {
                _route.Description = _tbDescription.Text?.Trim() ?? "";
                _onLiveChange?.Invoke();
            };
            Grid.SetColumn(_tbDescription, 1);
            descRow.Children.Add(_tbDescription);
            outer.Children.Add(descRow);

            return outer;
        }

        private void BuildColorSwatches()
        {
            if (_colorPanel == null) return;
            _colorPanel.Children.Clear();

            foreach (var hex in PoiIconRenderer.Palette)
            {
                bool isSelected = string.Equals(hex, _selectedColor, StringComparison.OrdinalIgnoreCase);
                var swatch = new Border
                {
                    Width           = 26,
                    Height          = 26,
                    Margin          = new Thickness(3),
                    CornerRadius    = new CornerRadius(4),
                    Background      = new SolidColorBrush(Color.Parse(hex)),
                    BorderBrush     = isSelected ? Brushes.Black : Brushes.Transparent,
                    BorderThickness = new Thickness(2),
                    Cursor          = new Cursor(StandardCursorType.Hand)
                };
                swatch.PointerPressed += (_, _) =>
                {
                    _selectedColor = hex;
                    _route.ColorHex = hex;
                    BuildColorSwatches();
                    RefreshPointPage(); // la spunta POI del punto corrente riflette il colore del percorso
                    _onLiveChange?.Invoke();
                };
                _colorPanel.Children.Add(swatch);
            }
        }

        private void RecalcSummary()
        {
            double lengthKm = 0;
            for (int i = 1; i < _workingPoints.Count; i++)
                lengthKm += GeoUtils.DistanceKm(
                    _workingPoints[i - 1].Lon, _workingPoints[i - 1].Lat,
                    _workingPoints[i].Lon,     _workingPoints[i].Lat);

            if (_summaryText != null)
                _summaryText.Text = string.Format(Strings.Get("RouteEditWindow_PuntiLunghezza"), _workingPoints.Count, lengthKm.ToString("0.##"));
        }

        // ---- Tab 2: punti, una pagina alla volta -----------------------
        private Control BuildPointsTab()
        {
            var root = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 14, 0, 0) };

            var navRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };

            _btnPointPrev = DialogUi.MakeTreeIconButton(BootstrapIcons.ChevronLeft, Strings.Get("RouteEditWindow_PuntoPrecedente"), Brushes.SteelBlue, () =>
            {
                if (_pointIndex > 0) { _pointIndex--; RefreshPointPage(); }
            }, size: 30);
            Grid.SetColumn(_btnPointPrev, 0);
            navRow.Children.Add(_btnPointPrev);

            _pointNavTitle = new TextBlock
            {
                FontWeight          = FontWeight.Bold,
                FontSize            = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center
            };
            Grid.SetColumn(_pointNavTitle, 1);
            navRow.Children.Add(_pointNavTitle);

            _btnPointNext = DialogUi.MakeTreeIconButton(BootstrapIcons.ChevronRight, Strings.Get("RouteEditWindow_PuntoSuccessivo"), Brushes.SteelBlue, () =>
            {
                if (_pointIndex < _workingPoints.Count - 1) { _pointIndex++; RefreshPointPage(); }
            }, size: 30);
            Grid.SetColumn(_btnPointNext, 2);
            navRow.Children.Add(_btnPointNext);

            var btnAddPoint = DialogUi.MakeIconTextButton(BootstrapIcons.Locate, Strings.Get("RouteEditWindow_AggiungiPunto"));
            btnAddPoint.Padding  = new Thickness(8, 4);
            btnAddPoint.FontSize = 11;
            btnAddPoint.Margin   = new Thickness(12, 0, 0, 0);
            btnAddPoint.Click += (_, _) =>
            {
                _workingPoints.Add(new GeoPoint { Lon = _currentViewLon, Lat = _currentViewLat });
                _pointIndex = _workingPoints.Count - 1;
                RefreshPointPage();
                _onLiveChange?.Invoke();
            };
            Grid.SetColumn(btnAddPoint, 3);
            navRow.Children.Add(btnAddPoint);

            DockPanel.SetDock(navRow, Dock.Top);
            root.Children.Add(navRow);

            // Niente ScrollViewer attorno: un ContentControl dentro uno
            // ScrollViewer verrebbe misurato con altezza "infinita" e non
            // si estenderebbe mai oltre il proprio contenuto minimo — per
            // far arrivare la Descrizione fino in fondo al tab serve che
            // questo host riceva davvero l'altezza residua reale del
            // DockPanel (fill, LastChildFill).
            _pointPageHost = new ContentControl { Margin = new Thickness(0, 14, 0, 0) };
            root.Children.Add(_pointPageHost);

            return root;
        }

        private void RefreshPointPage()
        {
            if (_pointIndex >= _workingPoints.Count) _pointIndex = _workingPoints.Count - 1;
            if (_pointIndex < 0) _pointIndex = 0;

            if (_btnPointPrev != null) _btnPointPrev.IsEnabled = _pointIndex > 0;
            if (_btnPointNext != null) _btnPointNext.IsEnabled = _pointIndex < _workingPoints.Count - 1;

            if (_pointNavTitle != null)
            {
                _pointNavTitle.Text = _workingPoints.Count == 0
                    ? Strings.Get("RouteEditWindow_NessunPunto")
                    : string.Format(Strings.Get("RouteEditWindow_PuntoDiTotale"), _pointIndex + 1, _workingPoints.Count);
            }

            if (_pointPageHost != null)
                _pointPageHost.Content = _workingPoints.Count == 0 ? null : BuildPointPage(_pointIndex);

            RecalcSummary();
        }

        private Control BuildPointPage(int index)
        {
            var p = _workingPoints[index];

            // DockPanel, non StackPanel: solo così l'ultimo figlio (il
            // pannello POI, quando presente) può riempire con
            // LastChildFill tutta l'altezza residua della pagina invece di
            // fermarsi alla propria dimensione minima.
            var outer = new DockPanel { LastChildFill = true };

            var grid = new Grid
            {
                RowDefinitions    = new RowDefinitions("Auto,Auto"),
                ColumnDefinitions = new ColumnDefinitions("110,*"),
                Margin            = new Thickness(0, 0, 0, 14)
            };

            AddLabel(grid, Strings.Get("RouteEditWindow_Longitudine"), 0);
            var tbLon = new TextBox { Text = $"{p.Lon:F6}", MaxWidth = 200, HorizontalAlignment = HorizontalAlignment.Left };
            AddControl(grid, tbLon, 0);

            AddLabel(grid, Strings.Get("RouteEditWindow_Latitudine"), 1);
            var tbLat = new TextBox { Text = $"{p.Lat:F6}", MaxWidth = 200, HorizontalAlignment = HorizontalAlignment.Left };
            AddControl(grid, tbLat, 1);

            tbLon.LostFocus += (_, _) => CommitPoint(index, tbLon, tbLat);
            tbLat.LostFocus += (_, _) => CommitPoint(index, tbLon, tbLat);

            DockPanel.SetDock(grid, Dock.Top);
            outer.Children.Add(grid);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 0, 14) };

            var poiBrush = p.IsPoi ? new SolidColorBrush(Color.Parse(_selectedColor)) : (IBrush)Brushes.Gray;
            var btnPoi = DialogUi.MakeIconTextButton(BootstrapIcons.Locate, Strings.Get("RouteEditWindow_TogglePoi"));
            btnPoi.FontSize = 11;
            btnPoi.Padding  = new Thickness(8, 4);
            btnPoi.Click += (_, _) =>
            {
                p.IsPoi = !p.IsPoi;
                if (p.IsPoi && string.IsNullOrWhiteSpace(p.PoiLabel))
                    p.PoiLabel = $"POI{index + 1}";
                if (p.IsPoi) ApplySuggestedIcon(p);
                RefreshPointPage();
                _onLiveChange?.Invoke();
            };
            actions.Children.Add(btnPoi);

            var btnUp = DialogUi.MakeTreeIconButton(BootstrapIcons.ChevronUp, Strings.Get("RouteEditWindow_SpostaSu"), Brushes.SteelBlue, () =>
            {
                if (index == 0) return;
                (_workingPoints[index - 1], _workingPoints[index]) = (_workingPoints[index], _workingPoints[index - 1]);
                _pointIndex = index - 1;
                RefreshPointPage();
                _onLiveChange?.Invoke();
            });
            actions.Children.Add(btnUp);

            var btnDown = DialogUi.MakeTreeIconButton(BootstrapIcons.ChevronDown, Strings.Get("RouteEditWindow_SpostaGiu"), Brushes.SteelBlue, () =>
            {
                if (index == _workingPoints.Count - 1) return;
                (_workingPoints[index + 1], _workingPoints[index]) = (_workingPoints[index], _workingPoints[index + 1]);
                _pointIndex = index + 1;
                RefreshPointPage();
                _onLiveChange?.Invoke();
            });
            actions.Children.Add(btnDown);

            var btnDel = DialogUi.MakeTreeIconButton(BootstrapIcons.Close, Strings.Get("RouteEditWindow_EliminaPunto"), Brushes.Crimson, async () =>
            {
                string pointName = p.IsPoi && !string.IsNullOrWhiteSpace(p.PoiLabel)
                    ? p.PoiLabel
                    : (index + 1).ToString();
                bool confirmedDelete = await DialogUi.AskYesNo(this,
                    Strings.Get("RouteEditWindow_EliminaPuntoTitolo"),
                    string.Format(Strings.Get("RouteEditWindow_EliminarePunto"), pointName),
                    Strings.Get("MainWindow_Si"), Strings.Get("MainWindow_Annulla"));
                if (!confirmedDelete) return;

                // Un punto marcato come POI (v. BuildPointPoiPanel) porta
                // etichetta/descrizione/icona proprie: eliminarlo dal
                // percorso senza avvisare le perderebbe silenziosamente.
                // Chiede se conservarlo come POI indipendente (nuovo gruppo)
                // prima di rimuoverlo dalla sequenza — in ogni caso il punto
                // esce dal percorso, la scelta riguarda solo se sopravvive
                // altrove.
                if (p.IsPoi && _onKeepDeletedPoiAsStandalone != null)
                {
                    string poiName = string.IsNullOrWhiteSpace(p.PoiLabel) ? Strings.Get("RouteEditWindow_TogglePoi") : p.PoiLabel;
                    bool keep = await DialogUi.AskYesNo(this,
                        Strings.Get("RouteEditWindow_ConservaPoiTitolo"),
                        string.Format(Strings.Get("RouteEditWindow_ConservaPoiMessaggio"), poiName),
                        Strings.Get("RouteEditWindow_ConservaPoiSi"), Strings.Get("RouteEditWindow_ConservaPoiNo"));
                    if (keep) _onKeepDeletedPoiAsStandalone(p, _route);
                }
                _workingPoints.RemoveAt(index);
                if (_pointIndex >= _workingPoints.Count) _pointIndex = _workingPoints.Count - 1;
                RefreshPointPage();
                _onLiveChange?.Invoke();
            });
            actions.Children.Add(btnDel);

            DockPanel.SetDock(actions, Dock.Top);
            outer.Children.Add(actions);

            // Ultimo figlio del DockPanel (LastChildFill): riempie tutto lo
            // spazio verticale residuo. Se il punto non è marcato POI non
            // c'è niente da mostrare qui — un pannello vuoto assorbe
            // comunque lo spazio così "actions" sopra non viene stirato al
            // suo posto.
            outer.Children.Add(p.IsPoi ? BuildPointPoiPanel(p) : new Panel());

            return outer;
        }

        // Pannello espanso mostrato quando un punto è marcato come POI:
        // griglia icone (stesso pattern di PoiGroupEditWindow.BuildIconGrid,
        // ma colorata col colore del PERCORSO, mai un colore proprio) più
        // etichetta e descrizione, ora a piena larghezza/altezza (l'intera
        // pagina del punto è dedicata a un solo punto). Modifiche applicate
        // direttamente sul GeoPoint di lavoro (già l'oggetto reale, v.
        // anteprima live).
        private Control BuildPointPoiPanel(GeoPoint p)
        {
            // DockPanel: icone ed etichetta restano in alto (Dock.Top), la
            // riga Descrizione è l'ultimo figlio e con LastChildFill si
            // estende fino in fondo alla pagina del punto.
            var panel = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 4, 0, 4) };

            var iconPanel = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };

            var color = PoiIconRenderer.ParseColor(_selectedColor);
            foreach (var entry in PoiIcons.All)
            {
                bool isSelected = entry.Type == p.PoiIcon;

                using var bmp         = PoiIconRenderer.RenderToBitmap(entry.Type, color, 32);
                var       avaloniaBmp = SkiaImageHelper.ToAvaloniaBitmap(bmp);

                var tile = new Border
                {
                    Width           = 34,
                    Height          = 34,
                    Margin          = new Thickness(1),
                    CornerRadius    = new CornerRadius(4),
                    Background      = isSelected ? new SolidColorBrush(Color.Parse("#CCE8FF")) : Brushes.Transparent,
                    BorderBrush     = isSelected ? Brushes.SteelBlue : Brushes.LightGray,
                    BorderThickness = new Thickness(1),
                    Cursor          = new Cursor(StandardCursorType.Hand),
                    Child           = new Image { Source = avaloniaBmp, Width = 24, Height = 24, Stretch = Stretch.Uniform }
                };
                ToolTip.SetTip(tile, entry.Name);
                tile.PointerPressed += (_, _) =>
                {
                    p.PoiIcon = entry.Type;
                    RefreshPointPage();
                    _onLiveChange?.Invoke();
                };
                iconPanel.Children.Add(tile);
            }
            DockPanel.SetDock(iconPanel, Dock.Top);
            panel.Children.Add(iconPanel);

            var labelRow = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*"), Margin = new Thickness(0, 0, 0, 8) };
            labelRow.Children.Add(new TextBlock { Text = Strings.Get("RouteEditWindow_PoiLabel"), VerticalAlignment = VerticalAlignment.Center });
            var tbPoiLabel = new TextBox { Text = p.PoiLabel, MaxWidth = 300, HorizontalAlignment = HorizontalAlignment.Left };
            tbPoiLabel.LostFocus += (_, _) =>
            {
                p.PoiLabel = tbPoiLabel.Text ?? "";
                bool iconChanged = ApplySuggestedIcon(p);
                if (iconChanged) RefreshPointPage();
                _onLiveChange?.Invoke();
            };
            Grid.SetColumn(tbPoiLabel, 1);
            labelRow.Children.Add(tbPoiLabel);
            DockPanel.SetDock(labelRow, Dock.Top);
            panel.Children.Add(labelRow);

            var descRow = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*") };
            descRow.Children.Add(new TextBlock { Text = Strings.Get("RouteEditWindow_PoiDescrizione"), VerticalAlignment = VerticalAlignment.Top });
            var tbPoiDesc = new TextBox
            {
                Text          = p.PoiDescription,
                AcceptsReturn = true,
                TextWrapping  = TextWrapping.Wrap,
                VerticalContentAlignment = VerticalAlignment.Top,
                MinHeight     = 110,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            tbPoiDesc.LostFocus += (_, _) =>
            {
                p.PoiDescription = tbPoiDesc.Text ?? "";
                bool iconChanged = ApplySuggestedIcon(p);
                if (iconChanged) RefreshPointPage();
                _onLiveChange?.Invoke();
            };
            Grid.SetColumn(tbPoiDesc, 1);
            descRow.Children.Add(tbPoiDesc);
            panel.Children.Add(descRow);

            return panel;
        }

        // Ritorna true se l'icona è cambiata (serve al chiamante per decidere
        // se ricostruire la UI). Non tocca l'icona se nessuna parola chiave
        // combacia — un'icona scelta a mano manualmente in assenza di un
        // match testuale successivo resta quella dell'utente. Stesse parole
        // chiave usate anche in import (v. Services/PoiIconSuggestion e
        // MainWindow.ReconcileImportedPoiWithRoutes), dove serve applicare
        // il suggerimento subito, senza aspettare un evento di UI.
        private static bool ApplySuggestedIcon(GeoPoint p)
        {
            if (!PoiIconSuggestion.TrySuggest(p.PoiLabel, p.PoiDescription, out var icon)) return false;
            if (p.PoiIcon == icon) return false;
            p.PoiIcon = icon;
            return true;
        }

        private void CommitPoint(int index, TextBox tbLon, TextBox tbLat)
        {
            if (index < 0 || index >= _workingPoints.Count) return;
            var inv = CultureInfo.InvariantCulture;
            if (double.TryParse((tbLon.Text ?? "").Replace(',', '.'), NumberStyles.Float, inv, out double lon))
                _workingPoints[index].Lon = Math.Clamp(lon, -180, 180);
            if (double.TryParse((tbLat.Text ?? "").Replace(',', '.'), NumberStyles.Float, inv, out double lat))
                _workingPoints[index].Lat = Math.Clamp(lat, -85, 85);

            RecalcSummary();
            _onLiveChange?.Invoke();
        }

        private void AddLabel(Grid grid, string text, int row, int column = 0, int span = 1, bool isDescLabel = false)
        {
            var lbl = new TextBlock
            {
                Text              = text,
                VerticalAlignment = VerticalAlignment.Top,
                Margin            = new Thickness(0, isDescLabel ? 20 : 8, 8, 0)
            };
            Grid.SetRow(lbl, row);
            Grid.SetColumn(lbl, column);
            if (span > 1) Grid.SetColumnSpan(lbl, span);
            grid.Children.Add(lbl);
        }

        private void AddControl(Grid grid, Control ctrl, int row, int column = 1, int span = 1)
        {
            ctrl.Margin = new Thickness(0, 4, 0, 0);
            Grid.SetRow(ctrl, row);
            Grid.SetColumn(ctrl, column);
            if (span > 1) Grid.SetColumnSpan(ctrl, span);
            grid.Children.Add(ctrl);
        }

        private void OnOkClick(object? sender, RoutedEventArgs e)
        {
            if (_workingPoints.Count < 2)
            {
                SetStatus(Strings.Get("RouteEditWindow_MinimoDuePunti"));
                return;
            }

            // Rilegge esplicitamente i campi di testo/data invece di
            // affidarsi solo ai LostFocus già scattati: se l'utente preme OK
            // mentre un campo ha ancora il focus, il commit va forzato qui
            // (l'anteprima live sui punti/colore/icone è invece già
            // garantita, quei controlli non usano LostFocus).
            string label = _tbLabel?.Text?.Trim() ?? "";
            _route.Label         = string.IsNullOrEmpty(label) ? Strings.Get("RouteEditWindow_LabelDefault") : label;
            _route.Description   = _tbDescription?.Text?.Trim() ?? "";
            _route.ColorHex      = _selectedColor;
            _route.StartDateTime = DialogUi.CombineDateTimeFields(_daField!);
            _route.EndDateTime   = DialogUi.CombineDateTimeFields(_aField!);

            Confirmed = true;
            _onLiveChange?.Invoke();
            Close();
        }

        private void SetStatus(string msg)
        {
            if (_statusText == null) return;
            _statusText.Text = msg;
        }
    }
}
