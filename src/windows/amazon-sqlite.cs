using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace OptiScalerBackup.Windows;

[SupportedOSPlatform("windows")]
internal static class AmazonSqlite
{
    public static List<DiscoveredGame> Read(string file)
    {
        var result = new List<DiscoveredGame>();
        if (!File.Exists(file)) return result;
        var code = sqlite3_open_v2(file, out var db, 1, IntPtr.Zero);
        IntPtr statement = IntPtr.Zero;
        try
        {
            if (code != 0) throw new IOException("Cannot open Amazon Games install database read-only.");
            if (sqlite3_prepare_v2(db, "SELECT ProductTitle, InstallDirectory FROM DbSet WHERE Installed = 1", -1, out statement, IntPtr.Zero) != 0)
                throw new IOException("Unsupported Amazon Games install database schema.");
            while ((code = sqlite3_step(statement)) == 100)
            {
                var title = Marshal.PtrToStringUTF8(sqlite3_column_text(statement, 0));
                var dir = Marshal.PtrToStringUTF8(sqlite3_column_text(statement, 1));
                if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(dir)) result.Add(new(title, dir, "Amazon Games"));
            }
            if (code != 101) throw new IOException("Amazon Games install database read failed.");
        }
        finally
        {
            if (statement != IntPtr.Zero) sqlite3_finalize(statement);
            if (db != IntPtr.Zero) sqlite3_close(db);
        }
        return result;
    }
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int count, out IntPtr statement, IntPtr tail);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_step(IntPtr statement);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_finalize(IntPtr statement);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close(IntPtr db);
}
