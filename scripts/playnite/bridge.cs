using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Plugins;

namespace OptiScalerBackup.PlayniteBridge
{
    public sealed class ExportGame
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string InstallDirectory { get; set; }
        public bool Installed { get; set; }
        public string Cover { get; set; }
        public string[] Platforms { get; set; } = new string[0];
        public string[] Tags { get; set; } = new string[0];
        public string[] Categories { get; set; } = new string[0];
        [ScriptIgnore] public string CoverSource { get; set; }
    }
    public static class ExportWriter
    {
        public static void NoLinks(string path)
        {
            for (var current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Linked bridge paths are not supported.");
        }
        public static void Write(string folder, List<ExportGame> games)
        {
            if (games.Count > 50000) throw new IOException("Playnite export exceeds 50,000 games.");
            NoLinks(folder); Directory.CreateDirectory(Path.Combine(folder, "covers"));
            var warnings = new List<string>();
            foreach (var game in games)
            {
                game.Cover = null;
                try
                {
                    if (!Guid.TryParse(game.Id, out var id)) throw new IOException("Invalid game ID.");
                    var source = game.CoverSource;
                    if (string.IsNullOrEmpty(source) || !Path.IsPathRooted(source) || !File.Exists(source)) continue;
                    NoLinks(source); var extension = Path.GetExtension(source).ToLowerInvariant();
                    if (!new[] { ".jpg", ".jpeg", ".png" }.Contains(extension) || new FileInfo(source).Length > 4 * 1024 * 1024)
                        throw new IOException("Cover must be JPEG/PNG and at most 4 MiB.");
                    var relative = "covers/" + id.ToString("N") + extension; var destination = Path.Combine(folder, relative); NoLinks(destination);
                    if (!File.Exists(destination) || File.GetLastWriteTimeUtc(source) != File.GetLastWriteTimeUtc(destination) || new FileInfo(source).Length != new FileInfo(destination).Length)
                        File.Copy(source, destination, true);
                    game.Cover = relative;
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException)
                { warnings.Add(game.Name + " cover: " + error.Message); }
            }
            var serializer = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024, RecursionLimit = 32 };
            var json = serializer.Serialize(new { Version = 1, ExportedUtc = DateTimeOffset.UtcNow.ToString("o"), Games = games, Warnings = warnings });
            if (Encoding.UTF8.GetByteCount(json) > 32 * 1024 * 1024) throw new IOException("Playnite export exceeds 32 MiB.");
            var target = Path.Combine(folder, "library.json"); NoLinks(target); var temporary = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(temporary, json, new UTF8Encoding(false));
                if (File.Exists(target)) File.Replace(temporary, target, null); else File.Move(temporary, target);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
    public sealed class Bridge : GenericPlugin
    {
        private readonly object gate = new object();
        private int generation;
        private static readonly ILogger Logger = LogManager.GetLogger();
        public override Guid Id { get; } = Guid.Parse("3f47e970-c45d-4e42-aea4-9472acdf5040");
        public Bridge(IPlayniteAPI api) : base(api) { Properties = new GenericPluginProperties { HasSettings = false }; }
        private void Export()
        {
            try
            {
                // Read SDK objects on the event thread; only local file writes run in the worker.
                var games = PlayniteApi.Database.Games.Select(game => new ExportGame
                {
                    Id = game.Id.ToString(),
                    Name = game.Name,
                    InstallDirectory = game.InstallDirectory,
                    Installed = game.IsInstalled,
                    Platforms = game.Platforms?.Select(p => p.Name).ToArray() ?? new string[0],
                    Tags = game.Tags?.Select(p => p.Name).ToArray() ?? new string[0],
                    Categories = game.Categories?.Select(p => p.Name).ToArray() ?? new string[0],
                    CoverSource = string.IsNullOrEmpty(game.CoverImage) || game.CoverImage.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? null : PlayniteApi.Database.GetFullFilePath(game.CoverImage)
                }).ToList();
                var current = Interlocked.Increment(ref generation);
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OptiScalerBackup", "playnite");
                Task.Run(() =>
                {
                    lock (gate)
                    {
                        if (current != generation) return;
                        try { ExportWriter.Write(folder, games); }
                        catch (Exception error) { Logger.Error(error, "OptiScaler library export failed."); }
                    }
                });
            }
            catch (Exception error) { Logger.Error(error, "Could not read Playnite library for OptiScaler."); }
        }
        public override void OnApplicationStarted(OnApplicationStartedEventArgs args) => Export();
        public override void OnLibraryUpdated(OnLibraryUpdatedEventArgs args) => Export();
        public override void OnGameInstalled(OnGameInstalledEventArgs args) => Export();
        public override void OnGameUninstalled(OnGameUninstalledEventArgs args) => Export();
        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args) => new[]
        {
            new MainMenuItem { MenuSection = "@OptiScaler Backup", Description = "Export library and covers", Action = _ => Export() }
        };
    }
}
