// =============================================================================
// UI/PoiItemEditWindow.cs
//
// SINOSSI: Dialog di creazione/modifica di un singolo POI.
//   Campi modificabili: etichetta, descrizione, longitudine, latitudine,
//   icona (griglia di anteprime nel colore del gruppo proprietario — il
//   colore resta del gruppo, l'icona è sempre una scelta del singolo POI),
//   data/ora opzionale "Da"/"A" (sempre visibili, vuote se non impostate).
//   Bottone "Usa centro vista mappa": precompila lon/lat con il centro
//   corrente della vista mappa passato dal chiamante (stesso spirito del
//   bottone "Città principali" di EditPageWindow).
// =============================================================================

using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using StradarioApp.Models;
using StradarioApp.Resources;
using StradarioApp.Services;

namespace StradarioApp.UI
{
    public class PoiItemEditWindow : Window
    {
        public bool    Confirmed  { get; private set; } = false;
        public PoiItem ResultItem { get; private set; }

        private readonly PoiItem _original;
        private readonly double  _currentViewLon;
        private readonly double  _currentViewLat;
        private readonly string  _groupColorHex;

        private PoiIconType _selectedIcon;

        private TextBox?    _tbLabel;
        private TextBox?    _tbDescription;
        private TextBox?    _tbLon;
        private TextBox?    _tbLat;
        private WrapPanel?  _iconPanel;
        private DateTimeFieldPair? _daField;
        private DateTimeFieldPair? _aField;
        private TextBlock?  _tbStatus;

        public PoiItemEditWindow(PoiItem item, double currentViewLon, double currentViewLat, string groupColorHex)
        {
            _original       = item;
            ResultItem      = item;
            _currentViewLon = currentViewLon;
            _currentViewLat = currentViewLat;
            _groupColorHex  = groupColorHex;
            _selectedIcon   = item.Icon ?? PoiIconType.Pin;

            Title  = item.Id == 0 ? Strings.Get("PoiItemEditWindow_TitoloNuovo") : string.Format(Strings.Get("PoiItemEditWindow_TitoloModifica"), item.Label);
            Width  = 640;
            Height = 500;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            BuildUI(item);
        }

        private void BuildUI(PoiItem p)
        {
            var grid = new Grid
            {
                Margin            = new Thickness(16),
                RowDefinitions    = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto"),
                ColumnDefinitions = new ColumnDefinitions("110,*")
            };

            int row = 0;
            bool isNew = p.Id == 0 && p.Lon == 0 && p.Lat == 0;

            AddLabel(grid, Strings.Get("PoiItemEditWindow_Etichetta"), row);
            _tbLabel = new TextBox { Text = p.Label };
            AddControl(grid, _tbLabel, row++);

            AddLabel(grid, Strings.Get("PoiItemEditWindow_Longitudine"), row);
            _tbLon = new TextBox { Text = isNew ? $"{_currentViewLon:F6}" : $"{p.Lon:F6}" };
            AddControl(grid, _tbLon, row++);

            AddLabel(grid, Strings.Get("PoiItemEditWindow_Latitudine"), row);
            _tbLat = new TextBox { Text = isNew ? $"{_currentViewLat:F6}" : $"{p.Lat:F6}" };
            AddControl(grid, _tbLat, row++);

            AddLabel(grid, Strings.Get("PoiItemEditWindow_Descrizione"), row);
            _tbDescription = new TextBox
            {
                Text          = p.Description,
                AcceptsReturn = true,
                TextWrapping  = TextWrapping.Wrap,
                MinHeight     = 64,
                MaxHeight     = 80
            };
            AddControl(grid, _tbDescription, row++);

            AddLabel(grid, Strings.Get("PoiItemEditWindow_Icona"), row);
            var iconScroll = new ScrollViewer { MaxHeight = 130 };
            _iconPanel = new WrapPanel { Orientation = Orientation.Horizontal };
            iconScroll.Content = _iconPanel;
            AddControl(grid, iconScroll, row++);
            BuildIconGrid();

            AddLabel(grid, Strings.Get("PoiItemEditWindow_Da"), row);
            _daField = DialogUi.MakeDateTimeFieldPair(p.DateStart);
            AddControl(grid, _daField.Panel, row++);

            AddLabel(grid, Strings.Get("PoiItemEditWindow_A"), row);
            _aField = DialogUi.MakeDateTimeFieldPair(p.DateEnd);
            AddControl(grid, _aField.Panel, row++);

            // Finché "A" resta vuoto, segue quello che si scrive in "Da" —
            // comodo per il caso più comune (un singolo istante).
            DialogUi.WireAutoFillSecondFromFirst(_daField, _aField);

            var btnCenter = DialogUi.MakeIconTextButton(BootstrapIcons.Locate, Strings.Get("PoiItemEditWindow_UsaCentroVistaMappa"));
            btnCenter.Margin = new Thickness(0, 4, 0, 0);
            btnCenter.HorizontalAlignment = HorizontalAlignment.Stretch;
            btnCenter.Click += (_, _) =>
            {
                if (_tbLon != null) _tbLon.Text = $"{_currentViewLon:F6}";
                if (_tbLat != null) _tbLat.Text = $"{_currentViewLat:F6}";
                SetStatus(Strings.Get("PoiItemEditWindow_CoordinateImpostate"), false);
            };
            Grid.SetRow(btnCenter, row);
            Grid.SetColumn(btnCenter, 0);
            Grid.SetColumnSpan(btnCenter, 2);
            grid.Children.Add(btnCenter);
            row++;

            _tbStatus = new TextBlock
            {
                FontSize     = 10,
                Foreground   = Brushes.DimGray,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 2, 0, 0)
            };
            Grid.SetRow(_tbStatus, row);
            Grid.SetColumn(_tbStatus, 0);
            Grid.SetColumnSpan(_tbStatus, 2);
            grid.Children.Add(_tbStatus);
            row++;

            var btnRow = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing             = 10,
                Margin              = new Thickness(0, 10, 0, 0)
            };
            var btnOk     = DialogUi.MakeDialogButton(Strings.Get("PoiItemEditWindow_Ok"), primary: true);
            var btnCancel = DialogUi.MakeDialogButton(Strings.Get("PoiItemEditWindow_Annulla"));
            btnOk.Click     += OnOkClick;
            btnCancel.Click += (_, _) => Close();
            btnRow.Children.Add(btnOk);
            btnRow.Children.Add(btnCancel);
            Grid.SetRow(btnRow, row);
            Grid.SetColumn(btnRow, 0);
            Grid.SetColumnSpan(btnRow, 2);
            grid.Children.Add(btnRow);

            Content = grid;
        }

        private void BuildIconGrid()
        {
            if (_iconPanel == null) return;
            _iconPanel.Children.Clear();

            var color = PoiIconRenderer.ParseColor(_groupColorHex);

            foreach (var entry in PoiIcons.All)
            {
                bool isSelected = entry.Type == _selectedIcon;

                using var bmp         = PoiIconRenderer.RenderToBitmap(entry.Type, color, 40);
                var       avaloniaBmp = SkiaImageHelper.ToAvaloniaBitmap(bmp);

                var tile = new Border
                {
                    Width           = 40,
                    Height          = 40,
                    Margin          = new Thickness(2),
                    CornerRadius    = new CornerRadius(4),
                    Background      = isSelected ? new SolidColorBrush(Color.Parse("#CCE8FF")) : Brushes.Transparent,
                    BorderBrush     = isSelected ? Brushes.SteelBlue : Brushes.LightGray,
                    BorderThickness = new Thickness(1),
                    Cursor          = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                    Child           = new Image { Source = avaloniaBmp, Width = 28, Height = 28, Stretch = Stretch.Uniform }
                };
                ToolTip.SetTip(tile, entry.Name);
                tile.PointerPressed += (_, _) =>
                {
                    _selectedIcon = entry.Type;
                    BuildIconGrid();
                };
                _iconPanel.Children.Add(tile);
            }
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

        private bool TryParseCoords(out double lon, out double lat)
        {
            lon = lat = 0;
            var inv = CultureInfo.InvariantCulture;
            var ns  = NumberStyles.Float;
            if (!double.TryParse((_tbLon?.Text ?? "").Replace(',', '.'), ns, inv, out lon)) return false;
            if (!double.TryParse((_tbLat?.Text ?? "").Replace(',', '.'), ns, inv, out lat)) return false;
            return lon >= -180 && lon <= 180 && lat >= -85 && lat <= 85;
        }

        private void OnOkClick(object? sender, RoutedEventArgs e)
        {
            if (!TryParseCoords(out double lon, out double lat))
            {
                SetStatus(Strings.Get("PoiItemEditWindow_CoordinateNonValide"), true);
                return;
            }

            DateTime? dateStart = DialogUi.CombineDateTimeFields(_daField!);
            DateTime? dateEnd   = DialogUi.CombineDateTimeFields(_aField!);
            if (dateEnd.HasValue && !dateStart.HasValue)
            {
                SetStatus(Strings.Get("PoiItemEditWindow_DataAFinSenzaDa"), true);
                return;
            }

            string label = _tbLabel?.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(label)) label = Strings.Get("PoiItemEditWindow_LabelDefault");

            ResultItem = new PoiItem
            {
                Id          = _original.Id,
                Label       = label,
                Description = _tbDescription?.Text?.Trim() ?? "",
                Lon         = lon,
                Lat         = lat,
                Icon        = _selectedIcon,
                DateStart   = dateStart,
                DateEnd     = dateEnd
            };
            Confirmed = true;
            Close();
        }

        private void SetStatus(string msg, bool isError)
        {
            if (_tbStatus == null) return;
            _tbStatus.Text       = msg;
            _tbStatus.Foreground = isError ? Brushes.Crimson : Brushes.DimGray;
        }
    }
}
