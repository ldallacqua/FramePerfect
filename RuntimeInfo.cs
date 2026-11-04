using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FramePerfect;

/// <summary>
/// Which DLSS DLLs are actually loaded in the process right now, plus what the RenoDX DLSS5 addon
/// reports about Neural Rendering (read from ReShade.log, read-only).
///
/// v4: modules are looked up by name (GetModuleHandle) instead of enumerating every module, and
/// ReShade.log is read incrementally on a worker thread, so nothing here can stall a frame.
/// </summary>
internal sealed partial class RuntimeInfo
{
    private static readonly Regex NrRuntime = new(@"signed DLSSNR (\S+) \S+ runtime initialized", RegexOptions.Compiled);
    private static readonly Regex NrVerdict = new(@"NR-VERDICT v\d+ state=(\w+)", RegexOptions.Compiled);
    private static readonly Regex NrCost = new(@"NR cost: gpu_ms=([\d.]+)", RegexOptions.Compiled);
    private static readonly Regex NrFlicker = new(@"edit trace \S+ \S+ frames=(\d+) .*? flicker=([\d.]+)st", RegexOptions.Compiled);
    private static readonly Regex NrContract = new(@"frame contract path=(\S+) in=(\S+) out=(\S+) .*?encoding=(\S+) units=(\S+)", RegexOptions.Compiled);
    private static readonly Regex NrToggle = new(@"^(\d\d:\d\d:\d\d):\d+ .*NR toggled (ON|OFF)", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex NrLookRestarts = new(@"look\[[^\]]*?restarts=(\d+)", RegexOptions.Compiled);

    private static readonly string[] Watched = { "nvngx_dlss.dll", "nvngx_dlssnr.dll" };

    private readonly string gameDir;
    private readonly Dictionary<string, string> versionCache = new(StringComparer.OrdinalIgnoreCase);
    private long logPos = -1;
    private string carry = string.Empty;
    private int busy;

    public RuntimeInfo(string gameDir) => this.gameDir = gameDir;

    public string Upscaler { get; private set; } = "?";

    public string NeuralRendering { get; private set; } = "?";

    public string NrRuntimeVersion { get; private set; } = "?";

    public string NrState { get; private set; } = "?";

    public string NrCostMs { get; private set; } = "?";

    public string NrFlickerStops { get; private set; } = "?";

    public string NrContractSummary { get; private set; } = "?";

    /// <summary>F6 toggles seen in ReShade.log since the plugin started ("HH:mm:ss ON", ...), newest last.</summary>
    public string NrToggles { get; private set; } = string.Empty;

    public string NrStabilizerRestarts { get; private set; } = "?";

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandle(string name);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleFileNameW")]
    private static unsafe partial uint GetModuleFileName(nint module, char* buffer, uint size);

    /// <summary>Re-reads the loaded DLSS modules. Returns a log line when something changed, else null.</summary>
    public unsafe string? RefreshModules()
    {
        string Describe(string name)
        {
            var h = GetModuleHandle(name);
            if (h == 0)
                return $"{name} not loaded";
            var buf = stackalloc char[1024];
            var len = GetModuleFileName(h, buf, 1024);
            var path = new string(buf, 0, (int)len);
            if (!this.versionCache.TryGetValue(path, out var ver))
            {
                try
                {
                    ver = FileVersionInfo.GetVersionInfo(path).FileVersion?.Replace(',', '.').Replace(" ", string.Empty) ?? "?";
                }
                catch
                {
                    ver = "?";
                }

                this.versionCache[path] = ver;
            }

            return $"{name} {ver} ({this.Where(path)})";
        }

        var up = Describe(Watched[0]);
        var n = Describe(Watched[1]);
        if (up == this.Upscaler && n == this.NeuralRendering)
            return null;
        this.Upscaler = up;
        this.NeuralRendering = n;
        return $"loaded DLLs: upscaler = {up} | NR = {n}";
    }

    private string Where(string path)
    {
        if (path.StartsWith(this.gameDir, StringComparison.OrdinalIgnoreCase))
            return "game folder";
        if (path.Contains(@"\NGX\models\", StringComparison.OrdinalIgnoreCase))
            return "driver / NVIDIA App override: " + path;
        if (path.Contains("DriverStore", StringComparison.OrdinalIgnoreCase))
            return "driver: " + path;
        return path;
    }

    /// <summary>Starts a background read of what ReShade.log gained since the last call (never blocks).</summary>
    public void RefreshFromReShadeLogAsync()
    {
        if (Interlocked.Exchange(ref this.busy, 1) == 1)
            return;
        Task.Run(() =>
        {
            try
            {
                this.ReadNewLogText();
            }
            catch
            {
                // best effort
            }
            finally
            {
                Volatile.Write(ref this.busy, 0);
            }
        });
    }

    private void ReadNewLogText()
    {
        var path = Path.Combine(this.gameDir, "ReShade.log");
        if (!File.Exists(path))
            return;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var len = fs.Length;
        if (this.logPos < 0 || len < this.logPos)
        {
            // first read (or the log was restarted): only the recent tail matters
            this.logPos = Math.Max(0, len - (256 * 1024));
            this.carry = string.Empty;
        }

        var take = (int)Math.Min(len - this.logPos, 2 * 1024 * 1024);
        if (take <= 0)
            return;
        fs.Seek(this.logPos, SeekOrigin.Begin);
        var buf = new byte[take];
        var read = fs.Read(buf, 0, take);
        this.logPos += read;

        var text = this.carry + Encoding.UTF8.GetString(buf, 0, read);
        var cut = text.LastIndexOf('\n');
        if (cut < 0)
        {
            this.carry = text;
            return;
        }

        this.carry = text[(cut + 1)..];
        text = text[..cut];

        this.NrRuntimeVersion = LastGroup(NrRuntime, text, 1) ?? this.NrRuntimeVersion;
        this.NrState = LastGroup(NrVerdict, text, 1) ?? this.NrState;
        this.NrCostMs = LastGroup(NrCost, text, 1) ?? this.NrCostMs;
        this.NrStabilizerRestarts = LastGroup(NrLookRestarts, text, 1) ?? this.NrStabilizerRestarts;
        var fm = LastMatch(NrFlicker, text);
        if (fm != null)
            this.NrFlickerStops = $"{fm.Groups[2].Value} st ({fm.Groups[1].Value} frames/s)";
        var cm = LastMatch(NrContract, text);
        if (cm != null)
            this.NrContractSummary = $"{cm.Groups[1].Value} in={cm.Groups[2].Value} out={cm.Groups[3].Value} encoding={cm.Groups[4].Value} units={cm.Groups[5].Value}";

        var toggles = new StringBuilder(this.NrToggles);
        for (var m = NrToggle.Match(text); m.Success; m = m.NextMatch())
            toggles.Append(toggles.Length > 0 ? ", " : string.Empty).Append(m.Groups[1].Value).Append(' ').Append(m.Groups[2].Value);
        var all = toggles.ToString();
        this.NrToggles = all.Length > 400 ? "..." + all[^400..] : all;
    }

    private static Match? LastMatch(Regex r, string text)
    {
        Match? last = null;
        for (var m = r.Match(text); m.Success; m = m.NextMatch())
            last = m;
        return last;
    }

    private static string? LastGroup(Regex r, string text, int g) => LastMatch(r, text)?.Groups[g].Value;
}
