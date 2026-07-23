// =============================================================================
// UI/PoiGroupEditWindow.cs
//
// SINOSSI: Dialog di creazione/modifica di un gruppo di POI.
//   Campi: nome, descrizione, colore (palette di swatch) e icona (griglia
//   di anteprime renderizzate con PoiIconRenderer nel colore selezionato).
// =============================================================================

using System;
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
    public class PoiGroupEditWindow : Window
    {
        public bool     Confirmed   { get; private set; } = false;
        public PoiGroup ResultGroup { get; private set; }

        private readonly PoiGroup _original;
        private PoiIconType _selectedIcon;
        private string      _selectedColor;

        private TextBox?   _tbName;
        private TextBox?   _tbDescription;
        private WrapPanel? _iconPanel;
        private WrapPanel? _colorPanel;

        public PoiGroupEditWindow(PoiGroup group)
        {
            _original      = group;
            ResultGroup    = group;
            _selectedIcon  = group.Icon;
            _selectedColor = group.ColorHex;

            Title  = group.Id == 0 ? Strings.Get("PoiGroupEditWindow_TitoloNuovo") : string.Format(Strings.Get("PoiGroupEditWindow_TitoloModifica"), group.Name);
            Width  = 430;
            Height = 500;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            BuildUI(group);
        }

        private void BuildUI(PoiGroup g)
        {
            var grid = new Grid
            {
                Margin            = new Thickness(16),
                RowDefinitions    = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,*,Auto"),
                ColumnDefinitions = new ColumnDefinitions("100,*")
            };

            int row = 0;

            AddLabel(grid, Strings.Get("PoiGroupEditWindow_Nome"), row);
            _tbName = new TextBox { Text = g.Name };
            AddControl(grid, _tbName, row++);

            AddLabel(grid, Strings.Get("PoiGroupEditWindow_Descrizione"), row);
            _tbDescription = new TextBox
            {
                Text          = g.Description,
                AcceptsReturn = true,
                TextWrapping  = TextWrapping.Wrap,
                MinHeight     = 50,
                MaxHeight     = 60
            };
            AddControl(grid, _tbDescription, row++);

            AddLabel(grid, Strings.Get("PoiGroupEditWindow_Colore"), row);
            _colorPanel = new WrapPanel { Orientation = Orientation.Horizontal };
            AddControl(grid, _colorPanel, row++);
            BuildColorSwatches();

            AddLabel(grid, Strings.Get("PoiGroupEditWindow_Icona"), row);
            var iconScroll = new ScrollViewer { MaxHeight = 190 };
            _iconPanel = new WrapPanel { Orientation = Orientation.Horizontal };
            iconScroll.Content = _iconPanel;
            AddControl(grid, iconScroll, row++);
            BuildIconGrid();

            row = 6; // salta la riga elastica "*"
            var btnRow = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing             = 10,
                Margin              = new Thickness(0, 10, 0, 0)
            };
            var btnOk     = DialogUi.MakeDialogButton(Strings.Get("PoiGroupEditWindow_Ok"), primary: true);
            var btnCancel = DialogUi.MakeDialogButton(Strings.Get("PoiGroupEditWindow_Annulla"));
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
                    BuildIconGrid();
                };
                _colorPanel.Children.Add(swatch);
            }
        }

        private void BuildIconGrid()
        {
            if (_iconPanel == null) return;
            _iconPanel.Children.Clear();

            var color = PoiIconRenderer.ParseColor(_selectedColor);

            foreach (var entry in PoiIcons.All)
            {
                bool isSelected = entry.Type == _selectedIcon;

                using var bmp        = PoiIconRenderer.RenderToBitmap(entry.Type, color, 40);
                var       avaloniaBmp = SkiaImageHelper.ToAvaloniaBitmap(bmp);

                var tile = new Border
                {
                    Width           = 44,
                    Height          = 44,
                    Margin          = new Thickness(2),
                    CornerRadius    = new CornerRadius(4),
                    Background      = isSelected ? new SolidColorBrush(Color.Parse("#CCE8FF")) : Brushes.Transparent,
                    BorderBrush     = isSelected ? Brushes.SteelBlue : Brushes.LightGray,
                    BorderThickness = new Thickness(1),
                    Cursor          = new Cursor(StandardCursorType.Hand),
                    Child           = new Image { Source = avaloniaBmp, Width = 32, Height = 32, Stretch = Stretch.Uniform }
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

        private void OnOkClick(object? sender, RoutedEventArgs e)
        {
            string name = _tbName?.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(name)) name = Strings.Get("PoiGroupEditWindow_NomeDefault");

            ResultGroup = new PoiGroup
            {
                Id          = _original.Id,
                Name        = name,
                Description = _tbDescription?.Text?.Trim() ?? "",
                Icon        = _selectedIcon,
                ColorHex    = _selectedColor,
                Items       = _original.Items
            };
            Confirmed = true;
            Close();
        }
    }
}
