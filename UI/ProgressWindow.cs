using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace StradarioApp.UI
{
    /// <summary>
    /// Simple progress window showing a message and optional fraction bar.
    /// </summary>
    public class ProgressWindow : Window
    {
        private readonly TextBlock _messageText;
        private readonly ProgressBar _progressBar;
        private bool _allowClose;

        public ProgressWindow(string title = "Avanzamento")
        {
            Title          = title;
            Width          = 400;
            Height         = 130;
            CanResize      = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            _messageText = new TextBlock
            {
                Text              = "Operazione in corso…",
                TextWrapping      = TextWrapping.Wrap,
                Margin            = new Thickness(16, 16, 16, 8),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            _progressBar = new ProgressBar
            {
                Minimum  = 0,
                Maximum  = 1,
                Value    = 0,
                Height   = 16,
                Margin   = new Thickness(16, 0, 16, 16),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            Content = new StackPanel
            {
                Children = { _messageText, _progressBar }
            };

            // Prevent user from closing while working
            Closing += (_, e) =>
            {
                if (!_allowClose) e.Cancel = true;
            };
        }

        public void Report(string message, double fraction)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _messageText.Text  = message;
                _progressBar.Value = fraction;
            });
        }

        /// <summary>Safe close that bypasses the Closing guard.</summary>
        public void SafeClose()
        {
            _allowClose = true;
            Close();
        }
    }
}
