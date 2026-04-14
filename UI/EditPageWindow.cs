using System;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using StradarioApp.Models;
using StradarioApp.Services;

namespace StradarioApp.UI
{
    public class EditPageWindow : Window
    {
        private readonly MapPage _page;
        private readonly StradarioSettings _settings;

        private readonly TextBox _labelBox;
        private readonly TextBox _lonBox;
        private readonly TextBox _latBox;
        private readonly TextBox _descBox;

        public EditPageWindow(MapPage page, StradarioSettings settings)
        {
            _page     = page;
            _settings = settings;

            Title  = "Modifica pagina";
            Width  = 420;
            Height = 320;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            _labelBox = new TextBox { Text = page.Label };
            _lonBox   = new TextBox { Text = page.GeoBounds.CenterLon.ToString("F6", CultureInfo.InvariantCulture) };
            _latBox   = new TextBox { Text = page.GeoBounds.CenterLat.ToString("F6", CultureInfo.InvariantCulture) };
            _descBox  = new TextBox
            {
                Text         = page.Description,
                AcceptsReturn = true,
                TextWrapping  = TextWrapping.Wrap,
                MinHeight    = 72
            };

            var citiesBtn = new Button { Content = "📍 Città principali", Margin = new Thickness(0, 4, 0, 0) };
            citiesBtn.Click += OnCitiesClick;

            var okBtn     = new Button { Content = "✔ OK",     Margin = new Thickness(0, 8, 8, 0) };
            var cancelBtn = new Button { Content = "✖ Annulla", Margin = new Thickness(0, 8, 0, 0) };
            okBtn.Click     += (_, _) => OnOk();
            cancelBtn.Click += (_, _) => Close();

            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = "Etichetta:" },
                    _labelBox,
                    new TextBlock { Text = "Longitudine:" },
                    _lonBox,
                    new TextBlock { Text = "Latitudine:" },
                    _latBox,
                    new TextBlock { Text = "Descrizione:" },
                    _descBox,
                    citiesBtn,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { okBtn, cancelBtn }
                    }
                }
            };
        }

        private void OnCitiesClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (!TryParseCoords(out double lon, out double lat))
            {
                _descBox.Text = "Coordinate non valide";
                return;
            }

            if (!CityDatabase.LoadStatus.StartsWith("Caricate"))
            {
                _descBox.Text = CityDatabase.LoadStatus;
                return;
            }

            var bounds = GeoUtils.CalcPageBounds(lon, lat, _settings);
            string desc = CityDatabase.Describe(bounds, 3);
            _descBox.Text = string.IsNullOrEmpty(desc) ? CityDatabase.LoadStatus : desc;
        }

        private void OnOk()
        {
            if (!TryParseCoords(out double lon, out double lat))
            {
                // Show error in lat box
                _latBox.Text = "Valore non valido";
                return;
            }

            _page.Label       = _labelBox.Text?.Trim() ?? string.Empty;
            _page.Description = _descBox.Text?.Trim() ?? string.Empty;
            _page.GeoBounds   = GeoUtils.CalcPageBounds(lon, lat, _settings);

            Close(true);
        }

        private bool TryParseCoords(out double lon, out double lat)
        {
            lon = lat = 0;
            string lonStr = (_lonBox.Text ?? "").Replace(',', '.');
            string latStr = (_latBox.Text ?? "").Replace(',', '.');

            return double.TryParse(lonStr, NumberStyles.Float, CultureInfo.InvariantCulture, out lon)
                && double.TryParse(latStr, NumberStyles.Float, CultureInfo.InvariantCulture, out lat);
        }
    }
}
