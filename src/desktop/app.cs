using System.IO;
using System.Windows;
using System.Windows.Media;
using OptiScalerBackup.Core;
using OptiScalerBackup.Windows;

namespace OptiScalerBackup.Desktop;

public sealed class App : Application
{
    public App() => Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/optibackup-ui;component/theme.xaml", UriKind.Relative) });
    [STAThread]
    public static int Main(string[] args)
    {
        string? screenshot = null;
        try
        {
            var home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OptiScalerBackup");
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] == "--home" && i + 1 < args.Length) home = Path.GetFullPath(args[++i]);
                else if (args[i] == "--smoke-test" && i + 1 < args.Length) screenshot = Path.GetFullPath(args[++i]);
                else throw new ArgumentException("Supported options: --home <folder>, --smoke-test <screenshot.png>.");
            }
            RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Store? store = null;
            var startup = new Window
            {
                Title = "OptiScaler Backup",
                Width = 500,
                Height = 240,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Background = (System.Windows.Media.Brush)app.FindResource("Canvas"),
                Foreground = (System.Windows.Media.Brush)app.FindResource("Ink"),
                FontFamily = (System.Windows.Media.FontFamily)app.FindResource("BodyFont"),
                Content = new System.Windows.Controls.StackPanel
                {
                    Margin = new Thickness(30),
                    Children =
                    {
                        new System.Windows.Controls.TextBlock { Text = "Opening your library", FontSize = 25 },
                        new System.Windows.Controls.TextBlock { Text = "Checking storage and recovering any unfinished changes…", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 15, 0, 22) },
                        new System.Windows.Controls.ProgressBar { IsIndeterminate = true, Height = 3 }
                    }
                }
            };
            WindowChrome.Apply(startup);
            var opening = true;
            startup.Closing += (_, e) => { if (opening) e.Cancel = true; };
            void Fail(Exception error)
            {
                if (screenshot != null) File.WriteAllText(screenshot + ".error", error.ToString());
                else MessageBox.Show(error.Message + "\nClose other app sessions and resolve pending recovery before retrying.", "Cannot open OptiScaler Backup", MessageBoxButton.OK, MessageBoxImage.Error);
                opening = false; app.Shutdown(1);
            }
            app.DispatcherUnhandledException += (_, e) => { e.Handled = true; Fail(e.Exception); };
            startup.Loaded += async (_, _) =>
            {
                try
                {
                    Engine? engine = null;
                    await Task.Run(() => { store = new Store(home); engine = new Engine(store, Detection.EnsureStopped); engine.Recover(); });
                    var window = new MainWindow(store!, engine!, screenshot != null);
                    if (screenshot != null)
                    {
                        var captured = false;
                        window.ContentRendered += async (_, _) =>
                        {
                            if (captured) return;
                            captured = true;
                            try { await window.SmokeTest(screenshot); app.Shutdown(0); }
                            catch (Exception error) { Fail(error); }
                        };
                    }
                    app.MainWindow = window; app.ShutdownMode = ShutdownMode.OnMainWindowClose;
                    window.Show(); opening = false; startup.Close();
                }
                catch (Exception error) { Fail(error); }
            };
            try { return app.Run(startup); }
            finally { store?.Dispose(); }
        }
        catch (Exception error)
        {
            if (screenshot != null)
            {
                try { SafePaths.NoLinks(screenshot); File.WriteAllText(screenshot + ".error", error.ToString()); }
                catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { Console.Error.WriteLine(logError.Message); }
            }
            if (!args.Contains("--smoke-test")) MessageBox.Show(error.Message + "\nClose other OptiScaler Backup sessions and resolve any pending recovery before retrying.", "Cannot open OptiScaler Backup", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }
}
