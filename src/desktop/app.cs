using System.IO;
using System.Windows;
using System.Windows.Media;
using OptiScalerBackup.Core;
using OptiScalerBackup.Windows;

namespace OptiScalerBackup.Desktop;

public sealed class App : Application
{
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
            using var store = new Store(home);
            var engine = new Engine(store, Detection.EnsureStopped);
            engine.Recover();
            var app = new App();
            var window = new MainWindow(store, engine, screenshot != null);
            app.DispatcherUnhandledException += (_, e) =>
            {
                e.Handled = true;
                if (screenshot != null) File.WriteAllText(screenshot + ".error", e.Exception.ToString());
                if (screenshot == null) MessageBox.Show(e.Exception.Message, "OptiScaler Backup", MessageBoxButton.OK, MessageBoxImage.Error);
                app.Shutdown(1);
            };
            if (screenshot != null)
            {
                window.ContentRendered += (_, _) =>
                {
                    try { window.SmokeTest(screenshot); app.Shutdown(0); }
                    catch (Exception error) { File.WriteAllText(screenshot + ".error", error.ToString()); app.Shutdown(1); }
                };
            }
            return app.Run(window);
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
