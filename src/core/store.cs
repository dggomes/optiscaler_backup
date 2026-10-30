using System.Text.Json;

namespace OptiScalerBackup.Core;

public sealed partial class Store : IDisposable
{
    private readonly FileStream gate;
    public string Home { get; }
    public Catalog Data { get; private set; }
    public string PayloadRoot => Path.GetFullPath(Data.Settings.BackupDirectory ?? Path.Combine(Home, "backups"));
    public string JournalPath => Path.Combine(Home, "pending.json");
    public string RenameJournalPath => Path.Combine(Home, "rename-pending.json");
    public static bool SupportedSchema(int version) => version is 1 or Format.Version;

    public Store(string home)
    {
        Home = Path.GetFullPath(home);
        SafePaths.NoLinks(Home);
        Directory.CreateDirectory(Home);
        gate = SessionLock.Open(Home);
        try
        {
            Data = File.Exists(Path.Combine(Home, "catalog.json")) ? Read<Catalog>(Path.Combine(Home, "catalog.json")) : new();
            if (!SupportedSchema(Data.SchemaVersion)) throw new InvalidOperationException("Unsupported catalog schema; no data changed.");
            SafePaths.NoLinks(PayloadRoot);
            RecoverRename();
            Upgrade();
        }
        catch { gate.Dispose(); throw; }
    }

    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Format.Json)
        ?? throw new InvalidOperationException($"Invalid metadata: {path}");

    public static void Atomic<T>(string path, T value)
    {
        SafePaths.NoLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Format.Id() + ".tmp";
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(output, value, Format.Json);
                output.Flush(true);
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public void Save() => Atomic(Path.Combine(Home, "catalog.json"), Data);
    public Game Game(string id) => Data.Games.SingleOrDefault(g => g.Id == id)
        ?? throw new InvalidOperationException($"Unknown game ID: {id}");
    public void Put(Game game)
    {
        var index = Data.Games.FindIndex(g => g.Id == game.Id);
        var previous = index < 0 ? null : Data.Games[index];
        if (index < 0) Data.Games.Add(game); else Data.Games[index] = game;
        try { Save(); }
        catch
        {
            if (index < 0) Data.Games.Remove(game); else Data.Games[index] = previous!;
            throw;
        }
    }
    public string Blob(string hash)
    {
        if (hash.Length != 64 || hash.Any(c => !char.IsAsciiHexDigit(c))) throw new IOException("Invalid payload hash.");
        var file = Path.Combine(PayloadRoot, "blobs", hash[..2], hash);
        SafePaths.NoLinks(file);
        return file;
    }
    public string Capture(string file)
    {
        SafePaths.NoLinks(file);
        var temp = Path.Combine(PayloadRoot, "staging", Format.Id());
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        try
        {
            SafePaths.Space(temp, new FileInfo(file).Length);
            using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { input.CopyTo(output); output.Flush(true); }
            var hash = SafePaths.Hash(temp);
            var dest = Blob(hash);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (!File.Exists(dest)) File.Move(temp, dest);
            Verify(hash);
            return hash;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public void Verify(string hash)
    {
        if (!File.Exists(Blob(hash)) || SafePaths.Hash(Blob(hash)) != hash)
            throw new IOException($"Missing or corrupted payload {hash}; operation cancelled.");
    }
    public void Dispose() => gate.Dispose();
}
