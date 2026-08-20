using System.Diagnostics;
using System.IO;

namespace WAuraDows;

/// <summary>
/// Launches the headless Python gesture sidecar and turns its stdout lines
/// (GESTURE:OPEN_PALM, ...) into events. Process-stdout IPC — no sockets, no
/// extra deps. The vision runs in Python; WAuraDows just reacts.
/// </summary>
internal sealed class GestureBridge : IDisposable
{
    private Process? _proc;

    /// <summary>Raised on a background thread with the gesture name
    /// (OPEN_PALM / FIST / SWIPE_LEFT / SWIPE_RIGHT). Marshal to UI before acting.</summary>
    public event Action<string>? Gesture;
    public event Action<string>? Status; // READY / ERROR:... / stopped

    public bool Running => _proc is { HasExited: false };

    public bool Start(int cam = 0)
    {
        if (Running) return true;
        if (!TryLocate(out string py, out string script, out string workdir))
        {
            Status?.Invoke("ERROR:sidecar not found (gesture-sidecar venv)");
            return false;
        }

        var psi = new ProcessStartInfo(py)
        {
            Arguments = $"\"{script}\" {cam}",
            WorkingDirectory = workdir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _proc.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            if (e.Data.StartsWith("GESTURE:")) Gesture?.Invoke(e.Data["GESTURE:".Length..].Trim());
            else Status?.Invoke(e.Data.Trim());
        };
        _proc.Exited += (_, _) => Status?.Invoke("stopped");

        try
        {
            _proc.Start();
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine(); // drain stderr (mediapipe logs) so it can't block
            return true;
        }
        catch (Exception ex)
        {
            Status?.Invoke($"ERROR:{ex.Message}");
            _proc = null;
            return false;
        }
    }

    public void Stop()
    {
        if (_proc is null) return;
        try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { }
        _proc.Dispose();
        _proc = null;
    }

    /// <summary>Find the venv python + sidecar by walking up to the repo's
    /// gesture-sidecar folder. Falls back to system python if the venv is missing.</summary>
    private static bool TryLocate(out string py, out string script, out string workdir)
    {
        py = script = workdir = "";
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            string gs = Path.Combine(dir.FullName, "gesture-sidecar");
            if (Directory.Exists(gs))
            {
                string s = Path.Combine(gs, "gesture_sidecar.py");
                if (!File.Exists(s)) return false;
                string venvPy = Path.Combine(gs, ".venv", "Scripts", "python.exe");
                py = File.Exists(venvPy) ? venvPy : "python";
                script = s; workdir = gs;
                return true;
            }
        }
        return false;
    }

    public void Dispose() => Stop();
}
