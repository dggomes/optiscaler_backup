using System.Text.Json;

namespace OptiScalerBackup.Core;

public static class SessionLock
{
    public static FileStream Open(string home)
    {
        var path = Path.Combine(home, "session.lock");
        SafePaths.NoLinks(path);
        Exception? last = null;
        for (var attempt = 0; attempt < 11; attempt++)
        {
            try { return Acquire(path); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                last = error;
                if (attempt < 10) Thread.Sleep(100);
            }
        }
        throw new SessionLockException(path, last!);
    }
    private static FileStream Acquire(string path)
    {
        SafePaths.NoLinks(path);
        FileStream stream;
        try { stream = OpenWritable(path); }
        catch (UnauthorizedAccessException) when (ReadOnly(path))
        {
            // An old marker can inherit read-only attributes from copied app data.
            // Exclusivity is required before touching even this app-owned metadata.
            using (var guard = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                SafePaths.NoLinks(path);
                File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            }
            stream = OpenWritable(path);
        }
        try
        {
            stream.SetLength(0);
            JsonSerializer.Serialize(stream, new { ProcessId = Environment.ProcessId, OpenedUtc = DateTimeOffset.UtcNow, Executable = Environment.ProcessPath });
            stream.Flush(true); stream.Position = 0; return stream;
        }
        catch { stream.Dispose(); throw; }
    }
    private static FileStream OpenWritable(string path) => new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
    private static bool ReadOnly(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
}

public sealed class SessionLockException(string path, Exception inner) : IOException(
    $"Cannot open session.lock in {Path.GetDirectoryName(path)}. Another OptiScaler Backup app or updater may still be running, or the data folder is not writable. Unlocked leftover markers are reset automatically; an active session cannot be bypassed. Details: {inner.Message}", inner)
{
    public string LockPath { get; } = path;
}
