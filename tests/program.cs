using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using OptiScalerBackup.Core;
using OptiScalerBackup.Windows;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Snapshots restore exact managed state and preserve unrelated files", () => Sync(SnapshotRoundTrip)),
    ("Original requires clean confirmation and cannot be replaced", () => Sync(OriginalGuard)),
    ("External settings restore under explicitly approved roots", () => Sync(ExternalSettings)),
    ("Corrupt payload is rejected before any mutation", () => Sync(Corruption)),
    ("Injected write failure rolls back files and package metadata", () => Sync(TransactionalFailure)),
    ("Interrupted operation recovers after reopening store", () => Sync(InterruptedRecovery)),
    ("Overlapping packages enforce reverse order; independent rollback allowed", () => Sync(Layers)),
    ("Edited package files stop rollback; override saves recovery", () => Sync(EditedFiles)),
    ("Snapshot restoration blocks active package overlap", () => Sync(SnapshotPackageConflict)),
    ("ZIP matching handles wrappers, exact paths and ambiguous basenames", () => Sync(ZipMapping)),
    ("ZIP imports require explicit mapping or exclusion for every file", () => Sync(ZipReview)),
    ("ZIP rejects traversal, absolute paths, devices and case collisions", () => Sync(UnsafeZip)),
    ("ZIP rejects symlinks, limits expanded size and entry count", () => Sync(ZipLimits)),
    ("Path resolution rejects links and unapproved external roots", () => Sync(PathSafety)),
    ("Unavailable game prevents backup; catalog entry remains", () => Sync(Unavailable)),
    ("Exclusive session lock and unsupported schemas fail closed", () => Sync(StorageGuards)),
    ("Steam parses multiple libraries and nested game executables", () => Sync(SteamDiscovery)),
    ("Epic skips incomplete installs; Xbox uses title/config", () => Sync(ManifestDiscovery)),
    ("Registry adapter interprets GOG, EA, Ubisoft and Battle.net metadata", () => Sync(RegistryMetadata)),
    ("Amazon adapter reads installed games from Windows SQLite", () => Sync(AmazonMetadata)),
    ("Destinations reject file/directory conflicts before installation", () => Sync(DestinationConflicts)),
    ("External path aliases cannot bypass package overlap protection", () => Sync(PhysicalOverlap)),
    ("Custom discovery deduplicates and retains disconnected games", () => Sync(CustomDiscovery)),
    ("Graphics detection flags ambiguous proxies without managing them", () => Sync(FileDetection)),
    ("Running-game guard prevents mutation", () => Sync(RunningGuard)),
    ("Locked files abort before mutation on Windows", () => Sync(LockedFiles)),
    ("Disk-space guard rejects impossible reservation", () => Sync(SpaceGuard)),
    ("Updater selects newer stable versions and trusted release assets", () => Sync(VersionSelection)),
    ("Updater handles no release, rate limiting and offline errors", UpdateCheckErrors),
    ("Updater verifies downloads and deletes corrupt staging", UpdateDownloads),
    ("Updater refuses game-operation journal before helper staging", () => Sync(UpdateIdleGuard))
};
var failed = 0;
var skipped = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Skipped e) { skipped++; Console.WriteLine("SKIP " + test.Name + ": " + e.Message); }
    catch (Exception e) { failed++; Console.Error.WriteLine("FAIL " + test.Name + "\n" + e); }
}
Console.WriteLine($"{tests.Length - failed - skipped} passed, {skipped} skipped, {failed} failed ({System.Runtime.InteropServices.RuntimeInformation.OSDescription}).");
return failed == 0 ? 0 : 1;

static Task Sync(Action action) { action(); return Task.CompletedTask; }
static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}.");
}
static void True(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
static void Throws(Action action, string? contains = null)
{
    try { action(); }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
    {
        if (contains != null && !e.Message.Contains(contains, StringComparison.OrdinalIgnoreCase)) throw new Exception($"Expected error containing '{contains}', got '{e.Message}'.", e);
        return;
    }
    throw new Exception("Expected operation to fail.");
}
static async Task ThrowsAsync(Func<Task> action)
{
    try { await action(); }
    catch (Exception e) when (e is IOException or HttpRequestException or TaskCanceledException) { return; }
    throw new Exception("Expected asynchronous operation to fail.");
}
static Target T(string relative) => new("game", relative);

static void SnapshotRoundTrip()
{
    using var f = new Fixture();
    f.Write("OptiScaler.ini", "clean");
    f.Write("unrelated.txt", "keep");
    f.Game.Managed = [T("OptiScaler.ini"), T("dxgi.dll")];
    f.Store.Put(f.Game);
    var original = f.Engine.CreateSnapshot(f.Game, "Original", true, true);
    f.Write("OptiScaler.ini", "tuned"); f.Write("dxgi.dll", "mod");
    var tuned = f.Engine.CreateSnapshot(f.Current, "Tuned", false, false);
    f.Engine.Restore(f.Current, original);
    Equal("clean", f.Read("OptiScaler.ini")); True(!File.Exists(f.Path("dxgi.dll"))); Equal("keep", f.Read("unrelated.txt"));
    f.Engine.Restore(f.Current, tuned);
    Equal("tuned", f.Read("OptiScaler.ini")); Equal("mod", f.Read("dxgi.dll"));
    Equal(2, f.Current.Snapshots.Count(s => s.Recovery));
}
static void OriginalGuard()
{
    using var f = new Fixture(); f.Game.Managed = [T("dxgi.dll")];
    Throws(() => f.Engine.CreateSnapshot(f.Game, "Original", true, false), "confirm");
    f.Engine.CreateSnapshot(f.Game, "Original", true, true);
    Throws(() => f.Engine.CreateSnapshot(f.Current, "Original again", true, true));
}
static void ExternalSettings()
{
    using var f = new Fixture();
    var external = System.IO.Path.Combine(f.Root, "settings"); Directory.CreateDirectory(external);
    File.WriteAllText(System.IO.Path.Combine(external, "GameUserSettings.ini"), "quality=1");
    f.Game.ExternalRoots["settings"] = external; f.Game.Managed = [new("settings", "GameUserSettings.ini")];
    var snapshot = f.Engine.CreateSnapshot(f.Game, "Settings", false, false);
    File.WriteAllText(System.IO.Path.Combine(external, "GameUserSettings.ini"), "quality=2");
    f.Engine.Restore(f.Current, snapshot);
    Equal("quality=1", File.ReadAllText(System.IO.Path.Combine(external, "GameUserSettings.ini")));
}
static void Corruption()
{
    using var f = new Fixture(); f.Write("a.dll", "before"); f.Game.Managed = [T("a.dll")];
    var snapshot = f.Engine.CreateSnapshot(f.Game, "A", false, false);
    f.Write("a.dll", "current");
    File.WriteAllText(f.Store.Blob(snapshot.Files[0].Hash!), "corrupt");
    Throws(() => f.Engine.Restore(f.Current, snapshot), "corrupt"); Equal("current", f.Read("a.dll")); True(!File.Exists(f.Store.JournalPath));
}
static void TransactionalFailure()
{
    using var f = new Fixture(); f.Write("a.dll", "original");
    var p = f.Package("A", ("a.dll", "new"), ("b.ini", "new"));
    f.Engine.AfterWrite = n => { if (n == 2) throw new IOException("injected disk failure"); };
    Throws(() => f.Engine.Install(f.Current, p, false), "rolled back");
    Equal("original", f.Read("a.dll")); True(!File.Exists(f.Path("b.ini"))); Equal(0, f.Current.Installations.Count); True(!File.Exists(f.Store.JournalPath));
}
static void InterruptedRecovery()
{
    using var f = new Fixture(); f.Write("a.dll", "old");
    var before = f.Engine.Capture(f.Game, [T("a.dll"), T("b.dll")], "Recovery", true);
    Store.Atomic(f.Store.JournalPath, new Journal { BeforeGame = Format.Clone(f.Game), BeforeFiles = before });
    f.Write("a.dll", "partially installed"); f.Write("b.dll", "new");
    f.Store.Dispose();
    using var reopened = new Store(f.Home);
    True(new Engine(reopened).Recover()); Equal("old", f.Read("a.dll")); True(!File.Exists(f.Path("b.dll"))); True(!File.Exists(reopened.JournalPath));
}
static void Layers()
{
    using var f = new Fixture(); f.Write("a.dll", "original");
    var a = f.Package("A", ("a.dll", "A")); var first = f.Engine.Install(f.Current, a, false);
    var b = f.Package("B", ("a.dll", "B"));
    Throws(() => f.Engine.Install(f.Current, b, false), "overlap");
    var second = f.Engine.Install(f.Current, b, true);
    var c = f.Package("C", ("independent.ini", "C")); var third = f.Engine.Install(f.Current, c, false);
    Throws(() => f.Engine.Rollback(f.Current, first, false), "newer");
    f.Engine.Rollback(f.Current, second, false); Equal("A", f.Read("a.dll"));
    f.Engine.Rollback(f.Current, first, false); Equal("original", f.Read("a.dll"));
    f.Engine.Rollback(f.Current, third, false); True(!File.Exists(f.Path("independent.ini")));
}
static void EditedFiles()
{
    using var f = new Fixture(); f.Write("a.ini", "old");
    var package = f.Package("Config", ("a.ini", "package")); var install = f.Engine.Install(f.Current, package, false);
    f.Write("a.ini", "edited");
    Throws(() => f.Engine.Rollback(f.Current, install, false), "changed"); Equal("edited", f.Read("a.ini"));
    f.Engine.Rollback(f.Current, install, true); Equal("old", f.Read("a.ini"));
    var recovery = f.Current.Snapshots.Last();
    Equal("edited", File.ReadAllText(f.Store.Blob(recovery.Files[0].Hash!)));
    Throws(() => f.Engine.Rollback(f.Current, f.Current.Installations[0], true), "already");
}
static void SnapshotPackageConflict()
{
    using var f = new Fixture(); f.Write("a.dll", "old"); f.Game.Managed = [T("a.dll")];
    var snapshot = f.Engine.CreateSnapshot(f.Game, "Old", false, false);
    var package = f.Package("A", ("a.dll", "new")); f.Engine.Install(f.Current, package, false);
    Throws(() => f.Engine.Restore(f.Current, snapshot), "overlapping"); Equal("new", f.Read("a.dll"));
}
static void ZipMapping()
{
    using var f = new Fixture();
    f.Write("bin/a.ini", "a"); f.Write("one/b.dll", "b"); f.Write("two/b.dll", "b");
    var zip = f.Zip(("Release/bin/a.ini", "new"), ("Release/b.dll", "new"), ("Release/new.cfg", "new"));
    var plan = new Packages(f.Store).Inspect(f.Game, zip);
    Equal("bin/a.ini", plan.Files[0].SuggestedPath); Equal<string?>(null, plan.Files[1].SuggestedPath);
    Equal(2, plan.Files[1].Candidates.Count); Equal("new.cfg", plan.Files[2].SuggestedPath);
    var mappings = plan.Files.ToDictionary(e => e.Entry, e => e.Entry.EndsWith("b.dll") ? "one/b.dll" : e.SuggestedPath);
    var package = new Packages(f.Store).Import(f.Game, plan, "ZIP", "1", mappings);
    f.Engine.Install(f.Current, package, false); Equal("new", f.Read("bin/a.ini")); Equal("b", f.Read("two/b.dll"));
}
static void ZipReview()
{
    using var f = new Fixture(); var zip = f.Zip(("a.ini", "a"), ("readme.txt", "text"));
    var packages = new Packages(f.Store); var plan = packages.Inspect(f.Game, zip);
    Throws(() => packages.Import(f.Game, plan, "A", "1", new Dictionary<string, string?> { ["a.ini"] = "a.ini" }), "mapping");
    var package = packages.Import(f.Game, plan, "A", "1", new Dictionary<string, string?> { ["a.ini"] = "a.ini", ["readme.txt"] = null });
    Equal(1, package.Files.Count); Equal("a.ini", package.Mappings[0].Entry); Equal("readme.txt", package.ExcludedEntries[0]); True(!File.Exists(f.Path("a.ini")));
    Throws(() => packages.Import(f.Current, plan, "B", "1", new Dictionary<string, string?> { ["a.ini"] = "same.ini", ["readme.txt"] = "SAME.ini" }), "collision");
}
static void UnsafeZip()
{
    using var f = new Fixture();
    foreach (var entry in new[] { "../outside", "/absolute", "C:\\evil", "a/../../b", "NUL.txt", "a./b", "a:stream", "a\\..\\b" })
    {
        var zip = f.Zip((entry, "bad"));
        Throws(() => new Packages(f.Store).Inspect(f.Game, zip), "unsafe");
    }
    var collision = f.Zip(("A.ini", "a"), ("a.INI", "b"));
    Throws(() => new Packages(f.Store).Inspect(f.Game, collision), "collision");
}
static void ZipLimits()
{
    using var f = new Fixture(); var zip = f.Zip(("a", "12345"));
    f.Store.Data.Settings.MaxArchiveBytes = 4;
    Throws(() => new Packages(f.Store).Inspect(f.Game, zip), "size");
    f.Store.Data.Settings.MaxArchiveBytes = 1024; f.Store.Data.Settings.MaxArchiveFiles = 1;
    zip = f.Zip(("a", "a"), ("b", "b")); Throws(() => new Packages(f.Store).Inspect(f.Game, zip), "count");
    f.Store.Data.Settings.MaxArchiveFiles = 10;
    zip = System.IO.Path.Combine(f.Root, "link.zip");
    using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
    {
        var entry = archive.CreateEntry("link"); entry.ExternalAttributes = unchecked((int)0xA1FF0000);
        using var writer = new StreamWriter(entry.Open()); writer.Write("target");
    }
    Throws(() => new Packages(f.Store).Inspect(f.Game, zip), "link");
}
static void PathSafety()
{
    using var f = new Fixture();
    Throws(() => SafePaths.Resolve(f.Game, new("unknown", "a.ini")), "unapproved");
    var outside = System.IO.Path.Combine(f.Root, "outside"); Directory.CreateDirectory(outside);
    try { Directory.CreateSymbolicLink(f.Path("linked"), outside); }
    catch (UnauthorizedAccessException) { throw new Skipped("Symbolic link creation is unavailable for this account."); }
    Throws(() => SafePaths.Resolve(f.Game, T("linked/a.ini")), "linked");
}
static void Unavailable()
{
    using var f = new Fixture(); f.Game.Managed = [T("a.ini")];
    Directory.Move(f.Game.Directory, f.Game.Directory + "-disconnected");
    Throws(() => f.Engine.CreateSnapshot(f.Game, "A", false, false), "unavailable"); Equal(1, f.Store.Data.Games.Count);
}
static void StorageGuards()
{
    using var f = new Fixture();
    Throws(() => { using var other = new Store(f.Home); });
    f.Store.Dispose(); Store.Atomic(System.IO.Path.Combine(f.Home, "catalog.json"), new Catalog { SchemaVersion = 999 });
    var before = File.ReadAllText(System.IO.Path.Combine(f.Home, "catalog.json"));
    Throws(() => { using var other = new Store(f.Home); }, "unsupported");
    Equal(before, File.ReadAllText(System.IO.Path.Combine(f.Home, "catalog.json")));
}
static void SteamDiscovery()
{
    using var f = new Fixture(); var steam = System.IO.Path.Combine(f.Root, "Steam"); var library = System.IO.Path.Combine(f.Root, "Library");
    Directory.CreateDirectory(System.IO.Path.Combine(steam, "steamapps")); Directory.CreateDirectory(System.IO.Path.Combine(library, "steamapps"));
    var escaped = library.Replace("\\", "\\\\");
    File.WriteAllText(System.IO.Path.Combine(steam, "steamapps", "libraryfolders.vdf"), $"\"libraryfolders\" {{ \"1\" {{ \"path\" \"{escaped}\" }} }}");
    File.WriteAllText(System.IO.Path.Combine(library, "steamapps", "appmanifest_1.acf"), "\"AppState\" { \"name\" \"Fixture Game\" \"installdir\" \"Fixture\" }");
    var binary = System.IO.Path.Combine(library, "steamapps", "common", "Fixture", "Project", "Binaries", "Win64"); Directory.CreateDirectory(binary);
    File.WriteAllText(System.IO.Path.Combine(binary, "Project-Win64-Shipping.exe"), "fixture");
    var results = new Discovery(_ => { }).Steam(steam); Equal(1, results.Count);
    Equal("Fixture Game", results[0].Name); Equal("Project/Binaries/Win64/Project-Win64-Shipping.exe", Detection.Executables(results[0].Directory)[0]);
}
static void ManifestDiscovery()
{
    using var f = new Fixture(); var manifests = System.IO.Path.Combine(f.Root, "manifests"); Directory.CreateDirectory(manifests);
    File.WriteAllText(System.IO.Path.Combine(manifests, "a.item"), JsonSerializer.Serialize(new { InstallLocation = f.Game.Directory, DisplayName = "Epic Fixture", bIsIncompleteInstall = false }));
    File.WriteAllText(System.IO.Path.Combine(manifests, "b.item"), JsonSerializer.Serialize(new { InstallLocation = f.Game.Directory, DisplayName = "Incomplete", bIsIncompleteInstall = true }));
    var scanner = new Discovery(_ => { }); Equal(1, scanner.Epic(manifests).Count);
    var xbox = System.IO.Path.Combine(f.Root, "XboxGames", "Folder", "Content"); Directory.CreateDirectory(xbox);
    File.WriteAllText(System.IO.Path.Combine(xbox, "MicrosoftGame.config"), "<Game><ShellVisuals DefaultDisplayName=\"Xbox Fixture\" /></Game>");
    var result = scanner.Xbox([f.Root]); Equal("Xbox Fixture", result[0].Name); Equal(xbox, result[0].Directory);
}
static void CustomDiscovery()
{
    using var f = new Fixture(); f.Write("Fixture.exe", "exe");
    var scanner = new Discovery(_ => { }); var found = scanner.Custom(f.Game.Directory); Equal(1, found.Count);
    var originalId = f.Game.Id; scanner.Merge(f.Store, found.Concat(found)); Equal(1, f.Store.Data.Games.Count); Equal(originalId, f.Current.Id);
    Directory.Move(f.Game.Directory, f.Game.Directory + "-offline"); scanner.Merge(f.Store, []); Equal(1, f.Store.Data.Games.Count);
}
static void FileDetection()
{
    using var f = new Fixture(); f.Write("dxgi.dll", "unidentified"); f.Write("OptiScaler.ini", "config"); f.Write("random.dll", "ignore"); f.Write("save.dat", "ignore");
    var result = Detection.Files(f.Game); Equal(2, result.Count); True(result.Single(r => r.Path == "dxgi.dll").ReviewRequired); Equal(0, f.Game.Managed.Count);
}
static void RunningGuard()
{
    using var f = new Fixture(); f.Game.Managed = [T("a.ini")];
    var engine = new Engine(f.Store, _ => throw new IOException("Game running"));
    Throws(() => engine.CreateSnapshot(f.Game, "A", false, false), "running");
}
static void LockedFiles()
{
    if (!OperatingSystem.IsWindows()) throw new Skipped("Windows file-share semantics");
    using var f = new Fixture(); f.Write("a.ini", "original"); var p = f.Package("A", ("a.ini", "new"));
    using var locked = new FileStream(f.Path("a.ini"), FileMode.Open, FileAccess.Read, FileShare.None);
    Throws(() => f.Engine.Install(f.Current, p, false)); Equal(0, f.Current.Installations.Count);
}
static void SpaceGuard()
{
    using var f = new Fixture(); Throws(() => SafePaths.Space(f.Root, long.MaxValue - 16 * 1024 * 1024), "space");
}
static void VersionSelection()
{
    True(Releases.NewerStable("v0.2.0", new Version(0, 1, 0, 0)));
    True(!Releases.NewerStable("v0.1.0", new Version(0, 1, 0, 0))); True(!Releases.NewerStable("v0.3.0-beta", new Version(0, 1, 0)));
    using var json = JsonDocument.Parse(ReleaseJson()); var result = Releases.Parse(json.RootElement, new Version(0, 1, 0));
    Equal("v0.2.0", result!.Tag);
    using var evil = JsonDocument.Parse(ReleaseJson().Replace("https://github.com/", "http://example.com/"));
    Throws(() => Releases.Parse(evil.RootElement, new Version(0, 1, 0)), "repository");
}
static string ReleaseJson() => JsonSerializer.Serialize(new
{
    tag_name = "v0.2.0",
    draft = false,
    prerelease = false,
    body = "Release fixture",
    html_url = "https://github.com/" + Releases.Repository + "/releases/tag/v0.2.0",
    assets = new[] { new { name = Releases.Asset, browser_download_url = AssetUrl() }, new { name = Releases.Asset + ".sha256", browser_download_url = AssetUrl() + ".sha256" } }
});
static string AssetUrl() => "https://github.com/" + Releases.Repository + "/releases/download/v0.2.0/" + Releases.Asset;
static async Task UpdateCheckErrors()
{
    using var notFound = new HttpClient(new FakeHttp(_ => new(HttpStatusCode.NotFound)));
    Equal<ReleaseInfo?>(null, await new Releases(notFound).Check());
    using var limited = new HttpClient(new FakeHttp(_ => new(HttpStatusCode.Forbidden)));
    await ThrowsAsync(async () => await new Releases(limited).Check());
    using var offline = new HttpClient(new FakeHttp(_ => throw new HttpRequestException("offline")));
    await ThrowsAsync(async () => await new Releases(offline).Check());
}
static async Task UpdateDownloads()
{
    using var f = new Fixture(); var content = Encoding.UTF8.GetBytes("binary fixture");
    var hash = Convert.ToHexStringLower(SHA256.HashData(content));
    var release = new ReleaseInfo("v0.2.0", "", "", AssetUrl(), AssetUrl() + ".sha256");
    using var client = new HttpClient(new FakeHttp(req => new(HttpStatusCode.OK) { Content = req.RequestUri!.AbsolutePath.EndsWith(".sha256") ? new StringContent(hash + "  " + Releases.Asset) : new ByteArrayContent(content) }));
    var path = await new Releases(client).Download(release, System.IO.Path.Combine(f.Root, "download")); Equal(hash, SafePaths.Hash(path));
    using var bad = new HttpClient(new FakeHttp(req => new(HttpStatusCode.OK) { Content = req.RequestUri!.AbsolutePath.EndsWith(".sha256") ? new StringContent(new string('0', 64)) : new ByteArrayContent(content) }));
    var folder = System.IO.Path.Combine(f.Root, "bad"); await ThrowsAsync(async () => await new Releases(bad).Download(release, folder));
    True(!File.Exists(System.IO.Path.Combine(folder, Releases.Asset)));
}
static void UpdateIdleGuard()
{
    using var f = new Fixture(); Store.Atomic(f.Store.JournalPath, new Journal());
    Throws(() => UpdateHost.Stage(f.Store, f.Path("missing"), new("v1.0.0", "", "", AssetUrl(), AssetUrl() + ".sha256")), "pending");
    True(!File.Exists(UpdateHost.Pending(f.Home)));
}

static void RegistryMetadata()
{
    var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["path"] = @"C:\Games\GOG", ["gameName"] = "GOG Fixture" };
    Equal("GOG", Discovery.RegistryEntry("GOG", "1", fields)!.Source);
    Equal("GOG Fixture", Discovery.RegistryEntry("GOG", "1", fields)!.Name);
    fields = new(StringComparer.OrdinalIgnoreCase) { ["InstallDir"] = @"D:\Games\Ubisoft Fixture" };
    Equal("Ubisoft Fixture", Discovery.RegistryEntry("Ubisoft", "123", fields)!.Name);
    fields = new(StringComparer.OrdinalIgnoreCase) { ["Install Dir"] = @"D:\Games\EA", ["DisplayName"] = "EA Fixture" };
    Equal("EA", Discovery.RegistryEntry("EA", "EA Fixture", fields)!.Source);
    fields = new(StringComparer.OrdinalIgnoreCase) { ["InstallLocation"] = @"D:\Games\Battle", ["DisplayName"] = "Battle Fixture", ["Publisher"] = "Blizzard Entertainment" };
    Equal("Battle.net", Discovery.RegistryEntry("Uninstall", "Fixture", fields)!.Source);
    fields["DisplayName"] = "Battle.net";
    Equal<DiscoveredGame?>(null, Discovery.RegistryEntry("Uninstall", "Fixture", fields));
    fields["DisplayName"] = "Unrelated software"; fields["Publisher"] = "Other";
    Equal<DiscoveredGame?>(null, Discovery.RegistryEntry("Uninstall", "Fixture", fields));
}
static void AmazonMetadata()
{
    if (!OperatingSystem.IsWindows()) throw new Skipped("Windows winsqlite3 adapter");
    using var f = new Fixture(); var database = System.IO.Path.Combine(f.Root, "GameInstallInfo.sqlite");
    SqliteFixture.Create(database, f.Game.Directory);
    var results = Discovery.AmazonDatabase(database);
    Equal(1, results.Count); Equal("Amazon Fixture", results[0].Name); Equal(f.Game.Directory, results[0].Directory);
}
static void DestinationConflicts()
{
    using var f = new Fixture(); var packages = new Packages(f.Store);
    var plan = packages.Inspect(f.Game, f.Zip(("a", "file"), ("a/b", "nested")));
    Throws(() => packages.Import(f.Game, plan, "Invalid", "1", new Dictionary<string, string?> { ["a"] = "a", ["a/b"] = "a/b" }), "parent");
    f.Write("parent", "original");
    var package = f.Package("Conflict", ("parent/child", "new"));
    Throws(() => f.Engine.Install(f.Current, package, false), "parent");
    Equal("original", f.Read("parent")); True(!File.Exists(f.Store.JournalPath));
}
static void PhysicalOverlap()
{
    using var f = new Fixture(); f.Write("a.ini", "clean");
    f.Game.ExternalRoots["alias"] = f.Game.Directory; f.Game.Managed = [new("alias", "a.ini")];
    var snapshot = f.Engine.CreateSnapshot(f.Game, "Clean", false, false);
    var package = f.Package("A", ("a.ini", "new")); f.Engine.Install(f.Current, package, false);
    Throws(() => f.Engine.Restore(f.Current, snapshot), "overlap"); Equal("new", f.Read("a.ini"));
}

sealed class Skipped(string message) : Exception(message);
sealed class FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
}
sealed class Fixture : IDisposable
{
    public string Root { get; } = System.IO.Path.Combine(Directory.GetCurrentDirectory(), ".context", "tests-" + Guid.NewGuid().ToString("N"));
    public string Home => System.IO.Path.Combine(Root, "app");
    public Store Store { get; }
    public Engine Engine { get; }
    public Game Game { get; }
    public Game Current => Store.Game(Game.Id);
    public Fixture()
    {
        Directory.CreateDirectory(Root);
        Store = new Store(Home); Engine = new Engine(Store);
        Game = new() { Name = "Fixture Game", Directory = System.IO.Path.Combine(Root, "game") };
        Directory.CreateDirectory(Game.Directory); Store.Put(Game);
    }
    public string Path(string relative) => System.IO.Path.Combine(Game.Directory, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
    public void Write(string relative, string text) { var path = Path(relative); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }
    public string Read(string relative) => File.ReadAllText(Path(relative));
    public string Zip(params (string Entry, string Text)[] files)
    {
        var path = System.IO.Path.Combine(Root, Format.Id() + ".zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var file in files) { var entry = archive.CreateEntry(file.Entry); using var writer = new StreamWriter(entry.Open()); writer.Write(file.Text); }
        return path;
    }
    public Package Package(string name, params (string Path, string Text)[] files)
    {
        var packages = new Packages(Store); var plan = packages.Inspect(Current, Zip(files));
        return packages.Import(Current, plan, name, "1", files.ToDictionary(f => f.Path, f => (string?)f.Path));
    }
    public void Dispose() { Store.Dispose(); Directory.Delete(Root, true); }
}

static class SqliteFixture
{
    public static void Create(string file, string gameDirectory)
    {
        if (sqlite3_open_v2(file, out var db, 6, IntPtr.Zero) != 0) throw new Exception("SQLite fixture open failed.");
        IntPtr statement = IntPtr.Zero;
        try
        {
            const string schema = "CREATE TABLE DbSet (Installed INTEGER, ProductTitle TEXT, InstallDirectory TEXT); INSERT INTO DbSet VALUES(0, 'Not installed', 'unused');";
            if (sqlite3_exec(db, schema, IntPtr.Zero, IntPtr.Zero, out var error) != 0)
            {
                if (error != IntPtr.Zero) sqlite3_free(error);
                throw new Exception("SQLite fixture schema failed.");
            }
            if (sqlite3_prepare_v2(db, "INSERT INTO DbSet VALUES(1, ?, ?)", -1, out statement, IntPtr.Zero) != 0 ||
                sqlite3_bind_text(statement, 1, "Amazon Fixture", -1, new IntPtr(-1)) != 0 ||
                sqlite3_bind_text(statement, 2, gameDirectory, -1, new IntPtr(-1)) != 0 || sqlite3_step(statement) != 101)
                throw new Exception("SQLite fixture insert failed.");
        }
        finally
        {
            if (statement != IntPtr.Zero) sqlite3_finalize(statement);
            sqlite3_close(db);
        }
    }
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_exec(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, IntPtr callback, IntPtr arg, out IntPtr error);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int count, out IntPtr statement, IntPtr tail);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_bind_text(IntPtr statement, int column, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, int size, IntPtr destructor);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_step(IntPtr statement);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_finalize(IntPtr statement);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close(IntPtr db);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern void sqlite3_free(IntPtr memory);
}
