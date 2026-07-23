// =============================================================================
// Program.cs
//
// SINOSSI: Entry point dell'applicazione Stradario.
//   - Inizializza Avalonia con tema Fluent e rendering SkiaSharp
//   - Avvia la MainWindow come finestra principale
//   - Compatibile Linux e Windows grazie ad Avalonia Desktop
// =============================================================================

using Avalonia;
using Avalonia.Themes.Fluent;
using StradarioApp.Services;
using StradarioApp.UI;

namespace StradarioApp
{
    class Program
    {
        public static void Main(string[] args)
        {
            // Registra il font resolver per PdfSharpCore prima di qualsiasi altra cosa.
            FontResolver.Register();

            // Lingua UI: preferenza salvata, o al primo avvio dedotta dalla
            // lingua di sistema (vedi AppPreferencesService.LoadLanguage).
            // Deve avvenire PRIMA di costruire MainWindow: il suo costruttore
            // legge già Strings.Get per etichette/tooltip/titolo.
            StradarioApp.Resources.Strings.SetLanguage(new AppPreferencesService().LoadLanguage());

            // Avvia il caricamento del database città in background
            // così è pronto quando l'utente apre il dialog di modifica pagina
            System.Threading.Tasks.Task.Run(() => CityDatabase.EnsureLoaded());

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        public static AppBuilder BuildAvaloniaApp()
        {
            return AppBuilder.Configure<App>()
                .UsePlatformDetect()       // Rileva automaticamente Linux/Windows/macOS
                .WithInterFont()
                .LogToTrace();
        }
    }

    // Classe applicazione Avalonia
    public class App : Application
    {
        public override void Initialize()
        {
            // Forza sempre il tema chiaro indipendentemente dalle impostazioni di sistema.
            // Evita il problema su Windows dark mode dove i testi bianchi
            // diventano illeggibili su sfondi grigi chiari.
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
