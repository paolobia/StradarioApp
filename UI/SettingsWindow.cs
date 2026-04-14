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
        private readonly StradarioSettings _initial;

        private ComboBox? _sizeCombo;
        private ComboBox? _orientCombo;
        private ComboBox? _dpiCombo;
        private ComboBox? _scaleCombo;
        private ComboBox? _serverCombo;
        private TextBlock? _previewText;

        public StradarioSettings? ResultSettings { get; private set; }

        public SettingsWindow(StradarioSettings current)
        {
            _initial = current;
            Title    = "Impostazioni";
            Width    = 420;
            Height   = 380;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            BuildUI();
            LoadValues(current);
        }

        private void BuildUI()
        {
            _sizeCombo = new ComboBox
            {
                ItemsSource = new[] { "A5", "A4", "A3" },
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            _orientCombo = new ComboBox
            {
                ItemsSource = new[] { "Portrait", "Landscape" },
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            _dpiCombo = new ComboBox
            {
                ItemsSource = new[] { "72", "96", "150", "300" },
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            _scaleCombo = new ComboBox
            {
                ItemsSource = new[] { "1:1.000", "1:5.000", "1:10.000", "1:100.000", "1:200.000" },
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            _serverCombo = new ComboBox
            {
                ItemsSource = TileServers.All.Select(s => s.Name).ToList(),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            _previewText = new TextBlock
            {
                Margin  = new Thickness(0, 8, 0, 0),
                Text    = "—",
                FontStyle = FontStyle.Italic
            };

            _sizeCombo.SelectionChanged  += (_, _) => UpdatePreview();
            _orientCombo.SelectionChanged += (_, _) => UpdatePreview();
            _scaleCombo.SelectionChanged += (_, _) => UpdatePreview();

            var okBtn     = new Button { Content = "✔ OK",      Margin = new Thickness(0, 12, 8, 0) };
            var cancelBtn = new Button { Content = "✖ Annulla", Margin = new Thickness(0, 12, 0, 0) };
            okBtn.Click     += (_, _) => OnOk();
            cancelBtn.Click += (_, _) => Close();

            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = "Formato pagina:" },
                    _sizeCombo,
                    new TextBlock { Text = "Orientamento:" },
                    _orientCombo,
                    new TextBlock { Text = "DPI:" },
                    _dpiCombo,
                    new TextBlock { Text = "Scala:" },
                    _scaleCombo,
                    new TextBlock { Text = "Tile server:" },
                    _serverCombo,
                    _previewText,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { okBtn, cancelBtn }
                    }
                }
            };
        }

        private void LoadValues(StradarioSettings s)
        {
            _sizeCombo!.SelectedIndex   = (int)s.PageSize;
            _orientCombo!.SelectedIndex = (int)s.Orientation;

            int[] dpiValues = { 72, 96, 150, 300 };
            int dpiIdx = Array.IndexOf(dpiValues, s.Dpi);
            _dpiCombo!.SelectedIndex = dpiIdx >= 0 ? dpiIdx : 2;

            _scaleCombo!.SelectedIndex  = (int)s.Scale;

            int serverIdx = TileServers.All.FindIndex(t => t.Url == s.TileServerUrl);
            _serverCombo!.SelectedIndex = serverIdx >= 0 ? serverIdx : 0;

            UpdatePreview();
        }

        private void UpdatePreview()
        {
            if (_sizeCombo == null || _scaleCombo == null || _previewText == null) return;

            var s = BuildSettingsFromControls();
            if (s == null) return;

            double w = s.GetPageWidthKm();
            double h = s.GetPageHeightKm();
            _previewText.Text = $"Copertura: {w:F1} km × {h:F1} km  ({s.GetScaleLabel()})";
        }

        public StradarioSettings? BuildSettingsFromControls()
        {
            if (_sizeCombo == null || _orientCombo == null || _dpiCombo == null ||
                _scaleCombo == null || _serverCombo == null)
                return null;

            int[] dpiValues = { 72, 96, 150, 300 };
            int dpiIdx = _dpiCombo.SelectedIndex;
            int dpi    = dpiIdx >= 0 && dpiIdx < dpiValues.Length ? dpiValues[dpiIdx] : 150;

            int serverIdx  = _serverCombo.SelectedIndex;
            string tileUrl = serverIdx >= 0 && serverIdx < TileServers.All.Count
                ? TileServers.All[serverIdx].Url
                : TileServers.Default;

            return new StradarioSettings
            {
                PageSize     = (PageSize)(_sizeCombo.SelectedIndex  >= 0 ? _sizeCombo.SelectedIndex  : 1),
                Orientation  = (PageOrientation)(_orientCombo.SelectedIndex >= 0 ? _orientCombo.SelectedIndex : 0),
                Dpi          = dpi,
                Scale        = (MapScale)(_scaleCombo.SelectedIndex >= 0 ? _scaleCombo.SelectedIndex : 4),
                TileServerUrl = tileUrl
            };
        }

        private void OnOk()
        {
            ResultSettings = BuildSettingsFromControls();
            if (ResultSettings != null)
                Close(true);
        }
    }
}
