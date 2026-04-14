using Avalonia;
using Avalonia.Themes.Fluent;
using StradarioApp.Services;
using StradarioApp.UI;
using System.Threading.Tasks;

namespace StradarioApp
{
    internal class Program
    {
        [System.STAThread]
        public static void Main(string[] args)
        {
            // Register font resolver BEFORE anything else (PdfSharpCore needs it)
            FontResolver.Register();

            // Start loading city database in background
            Task.Run(() => CityDatabase.EnsureLoaded());

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        private static AppBuilder BuildAvaloniaApp()
        {
            return AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .LogToTrace();
        }
    }

    internal class App : Application
    {
        public override void Initialize()
        {
            Styles.Add(new FluentTheme());
            RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = new MainWindow();
            }
            base.OnFrameworkInitializationCompleted();
        }
    }
}
