// =============================================================================
// UI/RouteEditWindow.cs
//
// SINOSSI: Dialog di modifica di un percorso.
//   Campi: etichetta, descrizione, colore (palette di swatch, riuso di
//   PoiIconRenderer.Palette) ed elenco punti (lon/lat modificabili, elimina,
//   sposta su/giù, aggiungi punto dal centro vista mappa corrente).
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
        private StackPanel? _pointsPanel;
        private TextBlock?  _statusText;
        private TextBlock?  _summaryText;
        private DateTimeFieldPair? _daField;
        private DateTimeFieldPair? _aField;

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
            Width  = 720;
            Height = 620;
            MinWidth  = 640;
            MinHeight = 440;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            BuildUI(route);
            RefreshPoints();

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

            var top = new Grid
            {
                RowDefinitions    = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto"),
                ColumnDefinitions = new ColumnDefinitions("100,*")
            };

            int row = 0;
            AddLabel(top, Strings.Get("RouteEditWindow_Etichetta"), row);
            _tbLabel = new TextBox { Text = r.Label };
            _tbLabel.LostFocus += (_, _) =>
            {
                string label = _tbLabel.Text?.Trim() ?? "";
                _route.Label = string.IsNullOrEmpty(label) ? Strings.Get("RouteEditWindow_LabelDefault") : label;
                _onLiveChange?.Invoke();
            };
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
            _tbDescription.LostFocus += (_, _) =>
            {
                _route.Description = _tbDescription.Text?.Trim() ?? "";
                _onLiveChange?.Invoke();
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
                _onLiveChange?.Invoke();
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
                    _route.ColorHex = hex;
                    BuildColorSwatches();
                    _onLiveChange?.Invoke();
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
                _onLiveChange?.Invoke();
            });
            Grid.SetColumn(btnPoi, 3);
            row.Children.Add(btnPoi);

            var btnUp = DialogUi.MakeTreeIconButton(BootstrapIcons.ChevronUp, Strings.Get("RouteEditWindow_SpostaSu"), Brushes.SteelBlue, () =>
            {
                if (index == 0) return;
                (_workingPoints[index - 1], _workingPoints[index]) = (_workingPoints[index], _workingPoints[index - 1]);
                RefreshPoints();
                _onLiveChange?.Invoke();
            });
            Grid.SetColumn(btnUp, 4);
            row.Children.Add(btnUp);

            var btnDown = DialogUi.MakeTreeIconButton(BootstrapIcons.ChevronDown, Strings.Get("RouteEditWindow_SpostaGiu"), Brushes.SteelBlue, () =>
            {
                if (index == _workingPoints.Count - 1) return;
                (_workingPoints[index + 1], _workingPoints[index]) = (_workingPoints[index], _workingPoints[index + 1]);
                RefreshPoints();
                _onLiveChange?.Invoke();
            });
            Grid.SetColumn(btnDown, 5);
            row.Children.Add(btnDown);

            var btnDel = DialogUi.MakeTreeIconButton(BootstrapIcons.Close, Strings.Get("RouteEditWindow_EliminaPunto"), Brushes.Crimson, async () =>
            {
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
                RefreshPoints();
                _onLiveChange?.Invoke();
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
        // GeoPoint di lavoro (già l'oggetto reale, v. anteprima live).
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
                    _onLiveChange?.Invoke();
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
                bool iconChanged = ApplySuggestedIcon(p);
                if (iconChanged) RefreshPoints();
                _onLiveChange?.Invoke();
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
                bool iconChanged = ApplySuggestedIcon(p);
                if (iconChanged) RefreshPoints();
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

            double lengthKm = 0;
            for (int i = 1; i < _workingPoints.Count; i++)
                lengthKm += GeoUtils.DistanceKm(
                    _workingPoints[i - 1].Lon, _workingPoints[i - 1].Lat,
                    _workingPoints[i].Lon,     _workingPoints[i].Lat);
            if (_summaryText != null)
                _summaryText.Text = string.Format(Strings.Get("RouteEditWindow_PuntiLunghezza"), _workingPoints.Count, lengthKm.ToString("0.##"));

            _onLiveChange?.Invoke();
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
