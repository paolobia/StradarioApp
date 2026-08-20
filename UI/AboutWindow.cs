// =============================================================================
// UI/AboutWindow.cs
//
// SINOSSI: Dialog "Informazioni su StradarioApp" — icona applicativa, nome,
//   versione corrente (Services/UpdateChecker.CurrentVersion, da <Version>
//   nel .csproj), breve descrizione, link a repository/licenza e un bottone
//   per ripetere manualmente il controllo aggiornamenti già eseguito in
//   background all'avvio (vedi MainWindow.CheckForUpdateOnStartupAsync).
// =============================================================================

using System;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using StradarioApp.Resources;
using StradarioApp.Services;

namespace StradarioApp.UI
{
    public class AboutWindow : Window
    {
        private TextBlock? _statusText;
        private Button?    _btnOpenRelease;
        private string?    _releaseUrl;

        public AboutWindow()
        {
            Title       = Strings.Get("AboutWindow_Titolo");
            Width       = 380;
            // L'altezza si adatta al contenuto invece di essere fissa: con
            // un valore fisso il bottone "Chiudi" finiva tagliato non appena
            // "Controlla aggiornamenti" allungava il testo di stato di una
            // riga (es. "È disponibile la versione ...") o mostrava anche il
            // bottone "Apri pagina della release".
            SizeToContent = SizeToContent.Height;
            CanResize   = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            // Larghezza fissa esplicita (non solo il Margin sotto) così ogni
            // figlio si centra rispetto allo stesso riferimento: altrimenti,
            // essendo lo StackPanel Auto-larghezza (HorizontalAlignment
            // Center anziché Stretch), la sua larghezza segue il figlio più
            // largo del momento (la descrizione) e un testo più corto (es.
            // l'esito del controllo aggiornamenti) risultava centrato in
            // modo solo apparentemente "spostato" rispetto ai bottoni sotto,
            // che sono invece larghi quanto il loro contenuto.
            var root = new StackPanel
            {
                Margin  = new Avalonia.Thickness(24),
                Spacing = 10,
                Width   = 380 - 48,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var icon = LoadIconImage();
            if (icon != null)
            {
                root.Children.Add(new Image
                {
                    Source = icon,
                    Width  = 72,
                    Height = 72,
                    HorizontalAlignment = HorizontalAlignment.Center
                });
            }

            root.Children.Add(new TextBlock
            {
                Text       = "StradarioApp",
                FontSize   = 20,
                FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Center
            });

            root.Children.Add(new TextBlock
            {
                Text       = string.Format(Strings.Get("AboutWindow_Versione"), UpdateChecker.CurrentVersion),
                FontSize   = 12,
                Foreground = Brushes.DimGray,
                HorizontalAlignment = HorizontalAlignment.Center
            });

            root.Children.Add(new TextBlock
            {
                Text         = Strings.Get("AboutWindow_Descrizione"),
                FontSize     = 12,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                TextAlignment = Avalonia.Media.TextAlignment.Center,
                Margin       = new Avalonia.Thickness(0, 6, 0, 6)
            });

            root.Children.Add(MakeLink(Strings.Get("AboutWindow_RepositoryGitHub"), "https://github.com/paolobia/StradarioApp"));
            root.Children.Add(MakeLink(Strings.Get("AboutWindow_StradarioViewer"), "https://paolobia.github.io/StradarioApp/"));
            root.Children.Add(new TextBlock
            {
                Text       = Strings.Get("AboutWindow_Licenza"),
                FontSize   = 11,
                Foreground = Brushes.DimGray,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            root.Children.Add(new TextBlock
            {
                Text       = Strings.Get("AboutWindow_DatiMappe"),
                FontSize   = 11,
                Foreground = Brushes.DimGray,
                HorizontalAlignment = HorizontalAlignment.Center
            });

            var btnCheck = DialogUi.MakeDialogButton(Strings.Get("AboutWindow_ControllaAggiornamenti"));
            btnCheck.HorizontalAlignment = HorizontalAlignment.Center;
            btnCheck.Margin = new Avalonia.Thickness(0, 10, 0, 0);
            btnCheck.Click += async (_, _) => await RunUpdateCheckAsync();
            root.Children.Add(btnCheck);

            _statusText = new TextBlock
            {
                FontSize     = 11,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                TextAlignment = Avalonia.Media.TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                // Larghezza esplicita (pari a quella dello StackPanel padre)
                // invece di lasciarla Auto: essendo vuoto all'apertura del
                // dialog, il DesiredSize iniziale misurato è quasi a
                // larghezza zero, e quando RunUpdateCheckAsync valorizza il
                // testo in un secondo momento (dopo il primo layout) il
                // risultato veniva tagliato invece di andare a capo/centrarsi
                // correttamente sulla larghezza piena.
                Width = root.Width
            };
            root.Children.Add(_statusText);

            _btnOpenRelease = DialogUi.MakeDialogButton(Strings.Get("AboutWindow_ApriPagina"), primary: true);
            _btnOpenRelease.HorizontalAlignment = HorizontalAlignment.Center;
            _btnOpenRelease.IsVisible = false;
            _btnOpenRelease.Click += (_, _) => OpenUrl(_releaseUrl);
            root.Children.Add(_btnOpenRelease);

            var btnClose = DialogUi.MakeDialogButton(Strings.Get("AboutWindow_Chiudi"));
            btnClose.HorizontalAlignment = HorizontalAlignment.Center;
            btnClose.Margin = new Avalonia.Thickness(0, 6, 0, 0);
            btnClose.Click += (_, _) => Close();
            root.Children.Add(btnClose);

            Content = root;
        }

        private async System.Threading.Tasks.Task RunUpdateCheckAsync()
        {
            if (_statusText == null) return;

            _statusText.Foreground = Brushes.DimGray;
            _statusText.Text       = Strings.Get("AboutWindow_ControlloInCorso");
            if (_btnOpenRelease != null) _btnOpenRelease.IsVisible = false;

            UpdateInfo? info;
            try
            {
                info = await UpdateChecker.CheckForNewerVersionAsync();
            }
            catch
            {
                info = null;
            }

            if (info != null)
            {
                _statusText.Foreground = Brushes.SeaGreen;
                _statusText.Text       = string.Format(Strings.Get("AboutWindow_NuovaVersioneTrovata"), info.LatestVersion);
                _releaseUrl = info.ReleaseUrl;
                if (_btnOpenRelease != null) _btnOpenRelease.IsVisible = true;
            }
            else
            {
                _statusText.Foreground = Brushes.DimGray;
                _statusText.Text       = Strings.Get("AboutWindow_NessunAggiornamento");
            }
        }

        private static Control MakeLink(string text, string url)
        {
            var block = new TextBlock
            {
                Text       = text,
                FontSize   = 12,
                Foreground = Brushes.SteelBlue,
                TextDecorations = Avalonia.Media.TextDecorations.Underline,
                Cursor     = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            block.PointerPressed += (_, _) => OpenUrl(url);
            return block;
        }

        private static void OpenUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch { /* nessun browser disponibile: nulla da fare lato utente */ }
        }

        private static Bitmap? LoadIconImage()
        {
            try
            {
                var assembly = typeof(AboutWindow).Assembly;
                using var stream = assembly.GetManifestResourceStream("StradarioApp.AppIcon.png");
                return stream is null ? null : new Bitmap(stream);
            }
            catch
            {
                return null;
            }
        }
    }
}
