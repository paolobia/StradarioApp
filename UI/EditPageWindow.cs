// =============================================================================
// UI/EditPageWindow.cs
//
// SINOSSI: Dialog di modifica di una singola pagina dello stradario.
//   Campi modificabili:
//     - Etichetta
//     - Descrizione (TextBox multilinea, 4 righe visibili)
//     - Centro geografico (Longitudine, Latitudine) in gradi decimali
//   Bottone "Città principali":
//     - Cerca nel database cities500.csv le città dentro il bounding box
//     - Ordina per popolazione decrescente, prende le prime 3
//     - Inserisce i nomi nel campo Descrizione (es. "Roma, Tivoli, Palestrina")
//     - Se cities500.csv non è caricato mostra lo stato corrente del database
// =============================================================================

using System;
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
    public class EditPageWindow : Window
    {
        public bool    Confirmed  { get; private set; } = false;
        public MapPage ResultPage { get; private set; }

        private readonly MapPage           _original;
        private readonly StradarioSettings _settings;

        private TextBox?   _tbLabel;
        private TextBox?   _tbDescription;
        private TextBox?   _tbLon;
        private TextBox?   _tbLat;
        private TextBlock? _tbStatus;

        public EditPageWindow(MapPage page, StradarioSettings settings)
        {
            _original  = page;
            _settings  = settings;
            ResultPage = page;

            Title  = string.Format(Strings.Get("EditPageWindow_Titolo"), page.Label);
            Width  = 420;
            Height = 390;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            BuildUI(page);
        }

        private void BuildUI(MapPage p)
        {
            var grid = new Grid
            {
                Margin            = new Thickness(16),
                RowDefinitions    = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,Auto"),
                ColumnDefinitions = new ColumnDefinitions("110,*")
            };

            int row = 0;

            // ---- Etichetta ----
            AddLabel(grid, Strings.Get("EditPageWindow_Etichetta"), row);
            _tbLabel = new TextBox { Text = p.Label };
            AddControl(grid, _tbLabel, row++);

            // ---- Longitudine ----
            AddLabel(grid, Strings.Get("EditPageWindow_Longitudine"), row);
            _tbLon = new TextBox { Text = $"{p.GeoBounds.CenterLon:F6}" };
            AddControl(grid, _tbLon, row++);

            // ---- Latitudine ----
            AddLabel(grid, Strings.Get("EditPageWindow_Latitudine"), row);
            _tbLat = new TextBox { Text = $"{p.GeoBounds.CenterLat:F6}" };
            AddControl(grid, _tbLat, row++);

            // ---- Descrizione multilinea ----
            AddLabel(grid, Strings.Get("EditPageWindow_Descrizione"), row);
            _tbDescription = new TextBox
            {
                Text          = p.Description,
                AcceptsReturn = true,
                TextWrapping  = TextWrapping.Wrap,
                MinHeight     = 72,
                MaxHeight     = 90,
                VerticalContentAlignment = VerticalAlignment.Top
            };
            AddControl(grid, _tbDescription, row++);

            // ---- Bottone "Città principali" ----
            var btnCities = DialogUi.MakeIconTextButton(BootstrapIcons.Locate, Strings.Get("EditPageWindow_CittaPrincipali"));
            btnCities.Margin = new Thickness(0, 4, 0, 0);
            btnCities.HorizontalAlignment = HorizontalAlignment.Stretch;
            ToolTip.SetTip(btnCities, Strings.Get("EditPageWindow_CittaPrincipaliTooltip"));
            btnCities.Click += OnCitiesClick;
            Grid.SetRow(btnCities, row);
            Grid.SetColumn(btnCities, 0);
            Grid.SetColumnSpan(btnCities, 2);
            grid.Children.Add(btnCities);
            row++;

            // ---- Status / info database ----
            _tbStatus = new TextBlock
            {
                FontSize     = 10,
                Foreground   = Brushes.DimGray,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 2, 0, 0),
                // Mostra già quante città sono disponibili
                Text         = CityDatabase.CityCount > 0
                    ? string.Format(Strings.Get("EditPageWindow_DatabaseCittaDisponibili"), CityDatabase.CityCount.ToString("N0"))
                    : CityDatabase.LoadStatus
            };
            Grid.SetRow(_tbStatus, row);
            Grid.SetColumn(_tbStatus, 0);
            Grid.SetColumnSpan(_tbStatus, 2);
            grid.Children.Add(_tbStatus);
            row++;

            // ---- OK / Annulla ----
            var btnRow = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing             = 10,
                Margin              = new Thickness(0, 10, 0, 0)
            };
            var btnOk     = DialogUi.MakeDialogButton(Strings.Get("EditPageWindow_Ok"), primary: true);
            var btnCancel = DialogUi.MakeDialogButton(Strings.Get("EditPageWindow_Annulla"));
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
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var ns  = System.Globalization.NumberStyles.Float;
            if (!double.TryParse((_tbLon?.Text ?? "").Replace(',', '.'), ns, inv, out lon)) return false;
            if (!double.TryParse((_tbLat?.Text ?? "").Replace(',', '.'), ns, inv, out lat)) return false;
            return lon >= -180 && lon <= 180 && lat >= -85 && lat <= 85;
        }

        private void OnCitiesClick(object? sender, RoutedEventArgs e)
        {
            if (!TryParseCoords(out double lon, out double lat))
            {
                SetStatus(Strings.Get("EditPageWindow_CoordinateValidePrima"), true);
                return;
            }

            // Assicura che il DB sia caricato (potrebbe essere ancora in caricamento)
            CityDatabase.EnsureLoaded();

            if (CityDatabase.CityCount == 0)
            {
                SetStatus($"⚠ {CityDatabase.LoadStatus}", true);
                return;
            }

            var bounds = GeoUtils.CalcPageBounds(lon, lat, _settings);
            string desc = CityDatabase.Describe(bounds, 3);

            if (string.IsNullOrEmpty(desc))
            {
                SetStatus(Strings.Get("EditPageWindow_NessunaCittaTrovata"), false);
                return;
            }

            if (_tbDescription != null)
                _tbDescription.Text = desc;

            int found = CityDatabase.FindTopCities(bounds, 3).Count;
            SetStatus(string.Format(Strings.Get("EditPageWindow_CittaTrovate"), found), false);
        }

        private void OnOkClick(object? sender, RoutedEventArgs e)
        {
            if (!TryParseCoords(out double lon, out double lat))
            {
                SetStatus(Strings.Get("EditPageWindow_CoordinateNonValide"), true);
                return;
            }
            ResultPage = new MapPage
            {
                Id          = _original.Id,
                Label       = _tbLabel?.Text?.Trim() ?? _original.Label,
                Description = _tbDescription?.Text?.Trim() ?? "",
                PageNumber  = _original.PageNumber,
                GeoBounds   = GeoUtils.CalcPageBounds(lon, lat, _settings)
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
