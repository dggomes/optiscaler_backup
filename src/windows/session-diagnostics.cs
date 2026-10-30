using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using OptiScalerBackup.Core;

namespace OptiScalerBackup.Windows;

public sealed record SessionOwner(int ProcessId, string Name);
public static class SessionDiagnostics
{
    public static string Message(Exception error)
    {
        if (error is not SessionLockException session) return error.Message;
        List<SessionOwner> owners;
        try { owners = Owners(session.LockPath); }
        catch (Exception native) when (native is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        { Trace.WriteLine("Session lock diagnostics unavailable: " + native.Message); owners = []; }
        return owners.Count == 0 ? error.Message : error.Message + "\n\nCurrently holding this lock: " +
            string.Join(", ", owners.Select(o => $"{o.Name} (PID {o.ProcessId})")) + ". Close that app or let its update finish, then retry.";
    }
    public static List<SessionOwner> Owners(string path)
    {
        if (!OperatingSystem.IsWindows()) return [];
        var result = RmStartSession(out var session, 0, new StringBuilder(33));
        if (result != 0) { Trace.WriteLine($"Session lock diagnostics: RmStartSession={result}"); return []; }
        try
        {
            result = RmRegisterResources(session, 1, [Path.GetFullPath(path)], 0, IntPtr.Zero, 0, IntPtr.Zero);
            if (result != 0) { Trace.WriteLine($"Session lock diagnostics: RmRegisterResources={result}"); return []; }
            uint count = 0;
            result = RmGetList(session, out var needed, ref count, null, out _);
            for (var attempt = 0; result == 234 && attempt < 3; attempt++)
            {
                if (needed > 1024) return [];
                var buffer = new NativeProcessInfo[needed]; count = needed;
                result = RmGetList(session, out needed, ref count, buffer, out _);
                if (result == 0) return buffer.Take((int)count).Select(p => new SessionOwner((int)p.Process.Id, p.AppName)).ToList();
            }
            if (result != 0) Trace.WriteLine($"Session lock diagnostics: RmGetList={result}");
            return [];
        }
        finally { RmEndSession(session); }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct UniqueProcess { public uint Id; public System.Runtime.InteropServices.ComTypes.FILETIME Started; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeProcessInfo
    {
        public UniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceName;
        public uint Type, Status, Session;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RmStartSession(out uint session, uint flags, StringBuilder key);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RmRegisterResources(uint session, uint files, [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] paths,
        uint applications, IntPtr processes, uint services, IntPtr serviceNames);
    [DllImport("rstrtmgr.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] NativeProcessInfo[]? information, out uint rebootReasons);
    [DllImport("rstrtmgr.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RmEndSession(uint session);
}
