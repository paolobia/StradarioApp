// =============================================================================
// UI/RouteEditWindow.cs
//
// SINOSSI: Dialog di creazione/modifica di un percorso.
//   Campi: etichetta, descrizione, colore (palette di swatch, riuso di
//   PoiIconRenderer.Palette) ed elenco punti (lon/lat modificabili, elimina,
//   sposta su/giù, aggiungi punto dal centro vista mappa corrente).
//   I punti si aggiungono già disegnandoli sulla mappa (modalità "disegna
//   percorso" avviata da MainWindow); qui si rifiniscono etichetta/colore
//   e si possono correggere/aggiungere singoli punti manualmente.
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
        public bool     Confirmed   { get; private set; } = false;
        public Percorso ResultRoute { get; private set; }

        private readonly Percorso  _original;
        private readonly double    _currentViewLon;
        private readonly double    _currentViewLat;
        private readonly List<GeoPoint> _workingPoints;
        private string _selectedColor;

        private TextBox?   _tbLabel;
        private TextBox?   _tbDescription;
        private WrapPanel?  _colorPanel;
        private StackPanel? _pointsPanel;
        private TextBlock?  _statusText;
        private TextBlock?  _summaryText;
        private DateTimeFieldPair? _daField;
        private DateTimeFieldPair? _aField;

        public RouteEditWindow(Percorso route, double currentViewLon, double currentViewLat)
        {
            _original       = route;
            ResultRoute     = route;
            _currentViewLon = currentViewLon;
            _currentViewLat = currentViewLat;
            _selectedColor  = string.IsNullOrWhiteSpace(route.ColorHex) ? PercorsoRenderer.DefaultColorHex : route.ColorHex;
            _workingPoints  = route.Points.Select(p => new GeoPoint
            {
                Lon = p.Lon, Lat = p.Lat,
                IsPoi = p.IsPoi, PoiLabel = p.PoiLabel, PoiDescription = p.PoiDescription, PoiIcon = p.PoiIcon
            }).ToList();

            Title  = route.Id == 0 ? Strings.Get("RouteEditWindow_TitoloNuovo") : string.Format(Strings.Get("RouteEditWindow_TitoloModifica"), route.Label);
            Width  = 720;
            Height = 620;
            MinWidth  = 640;
            MinHeight = 440;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            BuildUI(route);
            RefreshPoints();
        }

        private void BuildUI(Percorso r)
        {
            var root = new DockPanel { LastChildFill = true, Margin = new Thickness(16) };

            var top = new Grid
            {
                RowDefinitions    = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto"),
                ColumnDefinitions = new ColumnDefinitions("100,*")
            };

            int row = 0;
            AddLabel(top, Strings.Get("RouteEditWindow_Etichetta"), row);
            _tbLabel = new TextBox { Text = r.Label };
            AddControl(top, _tbLabel, row++);

            AddLabel(top, Strings.Get("RouteEditWindow_Descrizione"), row);
            _tbDescription = new TextBox
            {
                Text          = r.Description,
                AcceptsReturn = true,
                TextWrapping  = TextWrapping.Wrap,
                MinHeight     = 44,
                MaxHeight     = 54
            };
            AddControl(top, _tbDescription, row++);

            AddLabel(top, Strings.Get("RouteEditWindow_Da"), row);
            _daField = DialogUi.MakeDateTimeFieldPair(r.StartDateTime);
            AddControl(top, _daField.Panel, row++);

            AddLabel(top, Strings.Get("RouteEditWindow_A"), row);
            _aField = DialogUi.MakeDateTimeFieldPair(r.EndDateTime);
            AddControl(top, _aField.Panel, row++);

            // Finché "A" resta vuoto, segue quello che si scrive in "Da".
            DialogUi.WireAutoFillSecondFromFirst(_daField, _aField);

            AddLabel(top, Strings.Get("RouteEditWindow_Colore"), row);
            _colorPanel = new WrapPanel { Orientation = Orientation.Horizontal };
            AddControl(top, _colorPanel, row++);
            BuildColorSwatches();

            _summaryText = new TextBlock { FontSize = 11, Foreground = Brushes.DimGray, Margin = new Thickness(0, 6, 0, 0) };
            Grid.SetRow(_summaryText, row);
            Grid.SetColumn(_summaryText, 0);
            Grid.SetColumnSpan(_summaryText, 2);
            top.Children.Add(_summaryText);

            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);

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

            // ---- Sezione punti ----
            var pointsHeader = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing     = 8,
                Margin      = new Thickness(0, 10, 0, 4)
            };
            pointsHeader.Children.Add(new TextBlock { Text = Strings.Get("RouteEditWindow_PuntiDelPercorso"), FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center });
            var btnAddPoint = DialogUi.MakeIconTextButton(BootstrapIcons.Locate, Strings.Get("RouteEditWindow_AggiungiPunto"));
            btnAddPoint.Padding = new Thickness(8, 4);
            btnAddPoint.FontSize = 11;
            btnAddPoint.Click += (_, _) =>
            {
                _workingPoints.Add(new GeoPoint { Lon = _currentViewLon, Lat = _currentViewLat });
                RefreshPoints();
            };
            pointsHeader.Children.Add(btnAddPoint);
            DockPanel.SetDock(pointsHeader, Dock.Top);
            root.Children.Add(pointsHeader);

            _pointsPanel = new StackPanel { Spacing = 3 };
            var scroll = new ScrollViewer { Content = _pointsPanel };
            root.Children.Add(scroll);

            Content = root;
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
                    BuildColorSwatches();
                };
                _colorPanel.Children.Add(swatch);
            }
        }

        private void RefreshPoints()
        {
            if (_pointsPanel == null) return;
            _pointsPanel.Children.Clear();

            if (_workingPoints.Count == 0)
            {
                _pointsPanel.Children.Add(new TextBlock
                {
                    Text         = Strings.Get("RouteEditWindow_NessunPunto"),
                    FontSize     = 11,
                    Foreground   = Brushes.Gray,
                    Margin       = new Thickness(2, 8),
                    TextWrapping = TextWrapping.Wrap
                });
            }

            for (int i = 0; i < _workingPoints.Count; i++)
                _pointsPanel.Children.Add(BuildPointRow(i));

            double lengthKm = 0;
            for (int i = 1; i < _workingPoints.Count; i++)
                lengthKm += GeoUtils.DistanceKm(
                    _workingPoints[i - 1].Lon, _workingPoints[i - 1].Lat,
                    _workingPoints[i].Lon,     _workingPoints[i].Lat);

            if (_summaryText != null)
                _summaryText.Text = string.Format(Strings.Get("RouteEditWindow_PuntiLunghezza"), _workingPoints.Count, lengthKm.ToString("0.##"));
        }

        private Control BuildPointRow(int index)
        {
            var p = _workingPoints[index];

            var border = new Border
            {
                Background      = Brushes.White,
                BorderBrush     = Brushes.Gainsboro,
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(3),
                Margin          = new Thickness(0, 1),
                Padding         = new Thickness(6, 3)
            };

            var outer = new StackPanel { Spacing = 4 };

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,*,Auto,Auto,Auto,Auto") };

            row.Children.Add(new TextBlock
            {
                Text = (index + 1).ToString(),
                Width = 18,
                FontSize = 11,
                Foreground = Brushes.DimGray,
                VerticalAlignment = VerticalAlignment.Center
            });

            var tbLon = new TextBox { Text = $"{p.Lon:F6}", FontSize = 11, Margin = new Thickness(2, 0) };
            var tbLat = new TextBox { Text = $"{p.Lat:F6}", FontSize = 11, Margin = new Thickness(2, 0) };
            tbLon.LostFocus += (_, _) => CommitPoint(index, tbLon, tbLat);
            tbLat.LostFocus += (_, _) => CommitPoint(index, tbLon, tbLat);
            Grid.SetColumn(tbLon, 1);
            Grid.SetColumn(tbLat, 2);
            row.Children.Add(tbLon);
            row.Children.Add(tbLat);

            // Attiva/disattiva "questo punto è anche un POI": il colore
            // dell'icona segue quello del percorso quando attivo, così si
            // vede a colpo d'occhio senza bisogno di un riquadro colorato
            // separato (coerente con MakeTreeIconButton, che colora solo il
            // glifo, mai lo sfondo).
            var poiBrush = p.IsPoi ? new SolidColorBrush(Color.Parse(_selectedColor)) : (IBrush)Brushes.Gray;
            var btnPoi = DialogUi.MakeTreeIconButton(BootstrapIcons.Locate, Strings.Get("RouteEditWindow_TogglePoi"), poiBrush, () =>
            {
                p.IsPoi = !p.IsPoi;
                if (p.IsPoi && string.IsNullOrWhiteSpace(p.PoiLabel))
                    p.PoiLabel = $"POI{index + 1}";
                if (p.IsPoi) ApplySuggestedIcon(p);
                RefreshPoints();
            });
            Grid.SetColumn(btnPoi, 3);
            row.Children.Add(btnPoi);

            var btnUp = DialogUi.MakeTreeIconButton(BootstrapIcons.ChevronUp, Strings.Get("RouteEditWindow_SpostaSu"), Brushes.SteelBlue, () =>
            {
                if (index == 0) return;
                (_workingPoints[index - 1], _workingPoints[index]) = (_workingPoints[index], _workingPoints[index - 1]);
                RefreshPoints();
            });
            Grid.SetColumn(btnUp, 4);
            row.Children.Add(btnUp);

            var btnDown = DialogUi.MakeTreeIconButton(BootstrapIcons.ChevronDown, Strings.Get("RouteEditWindow_SpostaGiu"), Brushes.SteelBlue, () =>
            {
                if (index == _workingPoints.Count - 1) return;
                (_workingPoints[index + 1], _workingPoints[index]) = (_workingPoints[index], _workingPoints[index + 1]);
                RefreshPoints();
            });
            Grid.SetColumn(btnDown, 5);
            row.Children.Add(btnDown);

            var btnDel = DialogUi.MakeTreeIconButton(BootstrapIcons.Close, Strings.Get("RouteEditWindow_EliminaPunto"), Brushes.Crimson, () =>
            {
                _workingPoints.RemoveAt(index);
                RefreshPoints();
            });
            Grid.SetColumn(btnDel, 6);
            row.Children.Add(btnDel);

            outer.Children.Add(row);

            if (p.IsPoi)
                outer.Children.Add(BuildPointPoiPanel(p));

            border.Child = outer;
            return border;
        }

        // Pannello espanso mostrato quando un punto è marcato come POI:
        // griglia icone (stesso pattern di PoiGroupEditWindow.BuildIconGrid,
        // ma colorata col colore del PERCORSO, mai un colore proprio) più
        // etichetta e descrizione. Modifiche applicate direttamente sul
        // GeoPoint di lavoro, niente commit differito.
        private Control BuildPointPoiPanel(GeoPoint p)
        {
            var panel = new StackPanel { Spacing = 4, Margin = new Thickness(20, 2, 2, 4) };

            var iconPanel = new WrapPanel { Orientation = Orientation.Horizontal };

            var color = PoiIconRenderer.ParseColor(_selectedColor);
            foreach (var entry in PoiIcons.All)
            {
                bool isSelected = entry.Type == p.PoiIcon;

                using var bmp         = PoiIconRenderer.RenderToBitmap(entry.Type, color, 32);
                var       avaloniaBmp = SkiaImageHelper.ToAvaloniaBitmap(bmp);

                var tile = new Border
                {
                    Width           = 30,
                    Height          = 30,
                    Margin          = new Thickness(1),
                    CornerRadius    = new CornerRadius(4),
                    Background      = isSelected ? new SolidColorBrush(Color.Parse("#CCE8FF")) : Brushes.Transparent,
                    BorderBrush     = isSelected ? Brushes.SteelBlue : Brushes.LightGray,
                    BorderThickness = new Thickness(1),
                    Cursor          = new Cursor(StandardCursorType.Hand),
                    Child           = new Image { Source = avaloniaBmp, Width = 22, Height = 22, Stretch = Stretch.Uniform }
                };
                ToolTip.SetTip(tile, entry.Name);
                tile.PointerPressed += (_, _) =>
                {
                    p.PoiIcon = entry.Type;
                    RefreshPoints();
                };
                iconPanel.Children.Add(tile);
            }
            panel.Children.Add(iconPanel);

            var labelRow = new Grid { ColumnDefinitions = new ColumnDefinitions("70,*") };
            labelRow.Children.Add(new TextBlock { Text = Strings.Get("RouteEditWindow_PoiLabel"), VerticalAlignment = VerticalAlignment.Center, FontSize = 11 });
            var tbPoiLabel = new TextBox { Text = p.PoiLabel, FontSize = 11 };
            tbPoiLabel.LostFocus += (_, _) =>
            {
                p.PoiLabel = tbPoiLabel.Text ?? "";
                if (ApplySuggestedIcon(p)) RefreshPoints();
            };
            Grid.SetColumn(tbPoiLabel, 1);
            labelRow.Children.Add(tbPoiLabel);
            panel.Children.Add(labelRow);

            var descRow = new Grid { ColumnDefinitions = new ColumnDefinitions("70,*") };
            descRow.Children.Add(new TextBlock { Text = Strings.Get("RouteEditWindow_PoiDescrizione"), VerticalAlignment = VerticalAlignment.Top, FontSize = 11 });
            var tbPoiDesc = new TextBox
            {
                Text          = p.PoiDescription,
                FontSize      = 11,
                AcceptsReturn = true,
                TextWrapping  = TextWrapping.Wrap,
                MinHeight     = 40,
                MaxHeight     = 60
            };
            tbPoiDesc.LostFocus += (_, _) =>
            {
                p.PoiDescription = tbPoiDesc.Text ?? "";
                if (ApplySuggestedIcon(p)) RefreshPoints();
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

            double lengthKm = 0;
            for (int i = 1; i < _workingPoints.Count; i++)
                lengthKm += GeoUtils.DistanceKm(
                    _workingPoints[i - 1].Lon, _workingPoints[i - 1].Lat,
                    _workingPoints[i].Lon,     _workingPoints[i].Lat);
            if (_summaryText != null)
                _summaryText.Text = string.Format(Strings.Get("RouteEditWindow_PuntiLunghezza"), _workingPoints.Count, lengthKm.ToString("0.##"));
        }

        private void AddLabel(Grid grid, string text, int row)
        {
            var lbl = new TextBlock
            {
                Text              = text,
                VerticalAlignment = VerticalAlignment.Top,
                Margin            = new Thickness(0, 8, 8, 0)
            };
            Grid.SetRow(lbl, row);
            Grid.SetColumn(lbl, 0);
            grid.Children.Add(lbl);
        }

        private void AddControl(Grid grid, Control ctrl, int row)
        {
            ctrl.Margin = new Thickness(0, 4, 0, 0);
            Grid.SetRow(ctrl, row);
            Grid.SetColumn(ctrl, 1);
            grid.Children.Add(ctrl);
        }

        private void OnOkClick(object? sender, RoutedEventArgs e)
        {
            if (_workingPoints.Count < 2)
            {
                SetStatus(Strings.Get("RouteEditWindow_MinimoDuePunti"));
                return;
            }

            DateTime? startDateTime = DialogUi.CombineDateTimeFields(_daField!);
            DateTime? endDateTime   = DialogUi.CombineDateTimeFields(_aField!);

            string label = _tbLabel?.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(label)) label = Strings.Get("RouteEditWindow_LabelDefault");

            ResultRoute = new Percorso
            {
                Id            = _original.Id,
                Label         = label,
                Description   = _tbDescription?.Text?.Trim() ?? "",
                ColorHex      = _selectedColor,
                StartDateTime = startDateTime,
                EndDateTime   = endDateTime,
                Points        = _workingPoints
            };
            Confirmed = true;
            Close();
        }

        private void SetStatus(string msg)
        {
            if (_statusText == null) return;
            _statusText.Text = msg;
        }
    }
}
