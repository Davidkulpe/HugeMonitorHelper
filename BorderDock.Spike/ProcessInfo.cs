using System.Runtime.InteropServices;
using System.Text;

namespace BorderDock.Spike;

/// <summary>
/// Resolves the WORKING DIRECTORY of the terminal hosted by a window — the stable,
/// unique key we glue a border's settings to. The window title is useless as a key
/// for Claude terminals (it's Claude's live task text + spinner, which mutates), so
/// we instead find the node/claude/shell process behind the window and read its
/// current directory straight from the PEB (the Process-Explorer technique).
///
///   HWND ──GetWindowThreadProcessId──▶ WindowsTerminal.exe
///                                        └─OpenConsole.exe
///                                            └─pwsh.exe
///                                                └─node.exe (claude)  ◀── read its CWD
///
/// Non-terminal windows (VLC, browsers) have no node/shell descendant → returns null
/// and the caller falls back to title matching.
/// </summary>
internal static class ProcessInfo
{
    /// <summary>The project directory of the terminal behind this window, or null.
    /// Returns null both when the window isn't a terminal AND when the result would
    /// be AMBIGUOUS — i.e. one process hosts several projects (shared-process Windows
    /// Terminal: HWND→PID lands on a tree with many node cwds). We only trust the cwd
    /// when exactly one project sits behind the window (a separate-process terminal).
    /// Ambiguous/none → caller falls back to the window title.</summary>
    public static string? WorkingDirForWindow(IntPtr hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out uint rootPid);
        if (rootPid == 0) return null;

        try
        {
            var procs = SnapshotProcesses();

            // Shared-host guard: if the window's OWN process is a terminal that
            // multiplexes many windows into one process (Windows Terminal), the
            // window→project mapping is impossible by construction — bail to title.
            // Otherwise walking its subtree could pick up a cc console that happens
            // to be nested under WT (cc launched from a WT tab) and mis-attribute
            // EVERY WT window to that one project. Own-process consoles have their
            // window owned by conhost.exe, so they sail past this guard.
            if (procs.TryGetValue(rootPid, out var rootInfo) &&
                SharedHosts.Contains(rootInfo.exe.ToLowerInvariant()))
                return null;

            FindTerminalProcesses(rootPid, procs, out var nodePids, out var shellPids);

            // Prefer the node/claude cwd (fixed at the project root). Only accept it
            // if every node candidate agrees on ONE directory — more than one means a
            // shared host multiplexing projects, which we can't attribute to a window.
            var nodeDirs = DistinctDirs(nodePids);
            if (nodeDirs.Count == 1) return nodeDirs[0];
            if (nodeDirs.Count > 1) return null;            // ambiguous shared host

            var shellDirs = DistinctDirs(shellPids);
            return shellDirs.Count == 1 ? shellDirs[0] : null;
        }
        catch { return null; }
    }

    private static List<string> DistinctDirs(List<uint> pids)
    {
        var set = new List<string>();
        foreach (var pid in pids)
        {
            var d = ReadWorkingDir(pid);
            if (d is not null && !set.Any(x => string.Equals(x, d, StringComparison.OrdinalIgnoreCase)))
                set.Add(d);
        }
        return set;
    }

    /// <summary>Normalized form for comparison/storage: trimmed, no trailing slash.</summary>
    public static string Normalize(string path) => path.TrimEnd('\\', '/', ' ', '\0');

    // ---- pick the right process behind a terminal window ----------------------

    // Claude runs under node; the shell is the fallback (its cwd is the project too,
    // until you cd elsewhere — node's cwd is fixed at the project root, so prefer it).
    private static readonly string[] ShellExes =
        { "pwsh.exe", "powershell.exe", "cmd.exe", "bash.exe", "wsl.exe", "nu.exe", "fish.exe", "zsh.exe" };

    // Terminals that host MANY windows in ONE process → window↔project is unresolvable.
    private static readonly HashSet<string> SharedHosts = new(StringComparer.OrdinalIgnoreCase)
        { "windowsterminal.exe", "wezterm-gui.exe" };

    private static void FindTerminalProcesses(uint rootPid, Dictionary<uint, (uint parent, string exe)> procs,
        out List<uint> nodePids, out List<uint> shellPids)
    {
        var childrenOf = new Dictionary<uint, List<uint>>();
        foreach (var kv in procs)
            (childrenOf.TryGetValue(kv.Value.parent, out var l) ? l : childrenOf[kv.Value.parent] = new()).Add(kv.Key);

        // PHASE 1 — the window's process and everything under it. This covers the
        // own-process console (conhost owns the window, its shell/node are children)
        // AND Windows Terminal (reaches every node → intentionally ambiguous).
        var set = new HashSet<uint> { rootPid };
        AddSubtree(rootPid, childrenOf, set, procs);
        Classify(set, procs, out nodePids, out shellPids);
        if (nodePids.Count > 0 || shellPids.Count > 0) return;

        // PHASE 2 — nothing below the window. Classic case: conhost OWNS the window
        // but the shell is its PARENT and node is a SIBLING. Climb to the first shell
        // ancestor, fold in ITS subtree only (catching that sibling node), and stop —
        // don't keep climbing into explorer.exe and vacuum up the world.
        //
        // Critically we only do this when Phase 1 found nothing: when cc is launched
        // FROM a Windows Terminal tab, the conhost's grandparent is the WT tab's shell
        // with a DIFFERENT cwd — folding it in would add a bogus second shell and make
        // the result falsely ambiguous. Descendants win.
        uint a = rootPid;
        for (int guard = 0; guard < 32 && procs.TryGetValue(a, out var info); guard++)
        {
            uint parent = info.parent;
            if (parent == 0 || !procs.TryGetValue(parent, out var pinfo)) break;
            if (Array.IndexOf(ShellExes, pinfo.exe.ToLowerInvariant()) >= 0)
            {
                var anc = new HashSet<uint>();
                AddSubtree(parent, childrenOf, anc, procs);
                Classify(anc, procs, out nodePids, out shellPids);
                return;
            }
            a = parent;
        }
    }

    private static void Classify(HashSet<uint> set, Dictionary<uint, (uint parent, string exe)> procs,
        out List<uint> nodePids, out List<uint> shellPids)
    {
        nodePids = new List<uint>();
        shellPids = new List<uint>();
        foreach (var pid in set)
        {
            if (!procs.TryGetValue(pid, out var info)) continue;
            string exe = info.exe.ToLowerInvariant();
            if (exe.Contains("claude") || exe == "node.exe") nodePids.Add(pid);
            else if (Array.IndexOf(ShellExes, exe) >= 0) shellPids.Add(pid);
        }
    }

    // Walk descendants, but PRUNE at a shared host: never descend into
    // windowsterminal.exe. Otherwise an ancestor of WT (explorer.exe, or
    // PowerToys.PowerLauncher which spawned WT) would vacuum up all the terminals'
    // cwds and get its own window falsely resolved to one of them.
    private static void AddSubtree(uint root, Dictionary<uint, List<uint>> childrenOf,
        HashSet<uint> set, Dictionary<uint, (uint parent, string exe)> procs)
    {
        var stack = new Stack<uint>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var p = stack.Pop();
            if (!childrenOf.TryGetValue(p, out var cs)) continue;
            foreach (var c in cs)
            {
                if (!set.Add(c)) continue;
                bool isHost = procs.TryGetValue(c, out var ci) && SharedHosts.Contains(ci.exe.ToLowerInvariant());
                if (!isHost) stack.Push(c);   // keep the host node itself, but don't expand it
            }
        }
    }

    // ---- toolhelp process snapshot -------------------------------------------

    private static Dictionary<uint, (uint parent, string exe)> SnapshotProcesses()
    {
        var map = new Dictionary<uint, (uint, string)>();
        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == InvalidHandle) return map;
        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (Process32FirstW(snap, ref e))
                do { map[e.th32ProcessID] = (e.th32ParentProcessID, e.szExeFile ?? ""); }
                while (Process32NextW(snap, ref e));
        }
        finally { CloseHandle(snap); }
        return map;
    }

    // ---- read CurrentDirectory from the target process's PEB (x64) ------------

    private static string? ReadWorkingDir(uint pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, pid);
        if (h == IntPtr.Zero) return null;            // e.g. elevated terminal vs our medium IL
        try
        {
            var pbi = new PROCESS_BASIC_INFORMATION();
            if (NtQueryInformationProcess(h, 0, ref pbi, Marshal.SizeOf(pbi), out _) != 0) return null;
            if (pbi.PebBaseAddress == IntPtr.Zero) return null;

            // PEB.ProcessParameters @ +0x20  →  RTL_USER_PROCESS_PARAMETERS
            IntPtr prms = ReadPtr(h, pbi.PebBaseAddress + 0x20);
            if (prms == IntPtr.Zero) return null;

            // RTL_USER_PROCESS_PARAMETERS.CurrentDirectory.DosPath (UNICODE_STRING) @ +0x38
            IntPtr us = prms + 0x38;
            ushort lenBytes = ReadUShort(h, us);      // UNICODE_STRING.Length
            IntPtr buffer = ReadPtr(h, us + 8);       // UNICODE_STRING.Buffer
            if (buffer == IntPtr.Zero || lenBytes == 0 || lenBytes > 0x2000) return null;

            var bytes = new byte[lenBytes];
            if (!ReadProcessMemory(h, buffer, bytes, lenBytes, out _)) return null;
            string dir = Encoding.Unicode.GetString(bytes);
            dir = Normalize(dir);
            return string.IsNullOrWhiteSpace(dir) ? null : dir;
        }
        catch { return null; }
        finally { CloseHandle(h); }
    }

    private static IntPtr ReadPtr(IntPtr h, IntPtr addr)
    {
        var buf = new byte[IntPtr.Size];
        return ReadProcessMemory(h, addr, buf, buf.Length, out _) ? (IntPtr)BitConverter.ToInt64(buf, 0) : IntPtr.Zero;
    }

    private static ushort ReadUShort(IntPtr h, IntPtr addr)
    {
        var buf = new byte[2];
        return ReadProcessMemory(h, addr, buf, 2, out _) ? BitConverter.ToUInt16(buf, 0) : (ushort)0;
    }

    // ---- P/Invoke ------------------------------------------------------------

    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_VM_READ = 0x0010;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress,
        byte[] lpBuffer, int dwSize, out IntPtr lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr hProcess, int processInformationClass,
        ref PROCESS_BASIC_INFORMATION processInformation, int processInformationLength, out int returnLength);
}
