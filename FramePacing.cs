using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace XivDlssFix;

/// <summary>
/// Refresh rate, frame limiter and NVIDIA Reflex. Reverse engineered from ffxiv_dx11.exe 2026.10.16:
///
///  * Device+0x9F0 ("FrameRate") is filled once, when the device is created (+0x220C90):
///    GetDeviceCaps(GetDC(hwnd), VREFRESH), 59 or &lt;10 forced to 60. A screen DC reports the primary
///    monitor, and the value is never refreshed when the window moves or the refresh rate changes.
///    It feeds the frame limiter (windowed/borderless) and the dynamic-resolution FPS target (+0x2DA8FD).
///  * The frame limiter is Kernel::SwapChain::Present (+0x21B600). Device+0xA4 picks the
///    IDXGISwapChain::Present sync interval: 0 = none, 1 = V-Sync, 2..4 = V-Sync every N refreshes.
///    Mode 1 also sleeps (1 ms granularity, 1-2 ms slack) to the cap at Device+0xAC when the refresh rate is
///    higher. Present flags are always 0 (never ALLOW_TEARING). So the game's limiter can only produce whole
///    refresh intervals: 16.7 / 33.3 ms at 1/2 of 120 Hz, 8.3 / 16.7 ms at 1/1.
/// </summary>
internal sealed unsafe partial class FramePacing : IDisposable
{
    // RVA 0x21B600  void Kernel::SwapChain::Present(Kernel::SwapChain*)
    private const string SigPresent = "4C 8B DC 53 57 41 57 48 81 EC 90 00 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 80 00 00 00 " +
                                      "4C 8B 05 ?? ?? ?? ?? 4C 8B F9 49 89 6B 10 49 89 73 18 33 F6 4D 89 73 20 41 8B 90 A4 00 00 00";

    // Kernel::Device
    private const int DevLimitMode = 0xA4;      // applied: 0 none, 1 V-Sync (+ soft cap), 2..4 sync interval N
    private const int DevLimitModeCfg = 0xA8;
    private const int DevLimitFps = 0xAC;       // ushort, soft cap used by mode 1
    private const int DevLimitFpsCfg = 0xAE;    // ushort
    private const int DevFullscreen = 0x9C8;    // byte; non-zero = use the display mode's refresh rate instead of +0x9F0
    private const int DevHwnd = 0x9D8;
    private const int DevRefreshRate = 0x9F0;   // uint
    private const int DevD3D11Device = 0xE0AA8;

    // Kernel::SwapChain
    private const int SwapDxgi = 0x68;

    private const uint MonitorDefaultToNearest = 2;
    private const uint MonitorDefaultToPrimary = 1;
    private const int EnumCurrentSettings = -1;

    private static readonly Guid IidD3D11Device = new("db6f6ddb-ac77-4e88-8253-819df9bbf140");
    private static readonly List<nint> MonitorScratch = new();

    private delegate void PresentDelegate(nint swapChain);

    private readonly Configuration config;
    private readonly FileLog log;
    private readonly Hook<PresentDelegate>? presentHook;

    // Reflex
    private Reflex? reflex;
    private string reflexError = string.Empty;
    private bool reflexTried;
    private (bool LowLatency, bool Boost, uint IntervalUs)? reflexApplied;
    private volatile bool reflexLive;
    private long simFrameId;
    private ulong lastPresentFrameId;
    private int mainThreadId;
    private int sleepErrors;
    private readonly List<Reflex.FrameReport> reports = new();

    // refresh rate
    private uint? gameHzOriginal;
    private bool wroteRefresh;
    private DateTime nextDisplayCheck = DateTime.MinValue;
    private double displayCheckMaxMs; // the once-a-second check on the framework thread: must stay well under 1 ms
    private string lastDisplayLine = string.Empty;

    // present thread statistics
    private readonly object gate = new();
    private readonly List<float> intervals = new(1024);
    private readonly List<float> inPresent = new(1024);
    private long lastPresentTicks;
    private int presentThreadId;
    private bool threadLogged;
    private long nextSwapSample;
    private string swapLine = string.Empty;
    private string loggedSwapLine = string.Empty;
    private ulong lastSyncRefresh;
    private long lastSyncQpc;
    private double vblankHz;

    public FramePacing(Configuration config, FileLog log)
    {
        this.config = config;
        this.log = log;
        try
        {
            if (Plugin.SigScanner.TryScanText(SigPresent, out var address))
            {
                this.presentHook = Plugin.GameInterop.HookFromAddress<PresentDelegate>(address, this.PresentDetour);
                this.presentHook.Enable();
                log.Info($"hook Kernel::SwapChain::Present: found at ffxiv_dx11.exe+0x{address - Process.GetCurrentProcess().MainModule!.BaseAddress:X}");
            }
            else
            {
                log.Error("hook Kernel::SwapChain::Present: signature NOT found (present stats and Reflex present markers off)");
            }
        }
        catch (Exception e)
        {
            log.Error($"hook Kernel::SwapChain::Present failed: {e.Message}");
        }
    }

    // ------------------------------------------------------------------ status for the UI

    public string WindowMonitor { get; private set; } = "?";

    public uint WindowHz { get; private set; }

    public string PrimaryMonitor { get; private set; } = "?";

    public uint PrimaryHz { get; private set; }

    public int MonitorCount { get; private set; }

    public string MonitorList { get; private set; } = string.Empty;

    /// <summary>What the game read at startup (before the plugin touched it).</summary>
    public uint GameHzOriginal => this.gameHzOriginal ?? 0;

    public uint GameHzNow { get; private set; }

    public uint LimitMode { get; private set; }

    public ushort LimitFps { get; private set; }

    public bool Fullscreen { get; private set; }

    public bool ReflexAvailable => this.reflex != null;

    public string ReflexState { get; private set; } = "off";

    public string ReflexLatency { get; private set; } = string.Empty;

    public Reflex.SleepStatus? LastSleepStatus { get; private set; }

    public string SwapChainInfo => this.swapLine;

    /// <summary>DXGI_SWAP_EFFECT the game created its swap chain with (-1 = not seen yet). 0/1 = legacy blit model.</summary>
    public int SwapEffect { get; private set; } = -1;

    public bool BlitModel => this.SwapEffect is 0 or 1;

    /// <summary>The game's monitor is in HDR10 (Windows HDR on), read from IDXGIOutput6::GetDesc1 every 2 s.</summary>
    public bool OutputHdr { get; private set; }

    /// <summary>Windows 11 "Optimizations for windowed games" (upgrades blit-model DX10/11 swap chains to flip model),
    /// read from HKCU\Software\Microsoft\DirectX\UserGpuPreferences: per-app value first, then the global one.</summary>
    public (bool? On, string Source) WindowedOptimizationsState { get; private set; } = (null, "?");

    private static readonly string? ExePath = Process.GetCurrentProcess().MainModule?.FileName;

    private static (bool? On, string Source) WindowedOptimizations()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences");
            if (key == null)
                return (null, "Windows default");
            var exe = ExePath;
            if (exe != null && key.GetValue(exe) is string app && Parse(app) is { } a)
                return (a, "set for ffxiv_dx11.exe");
            if (key.GetValue("DirectXUserGlobalSettings") is string global && Parse(global) is { } g)
                return (g, "global setting");
            return (null, "Windows default");
        }
        catch
        {
            return (null, "unreadable");
        }

        static bool? Parse(string s)
        {
            var i = s.IndexOf("SwapEffectUpgradeEnable=", StringComparison.OrdinalIgnoreCase);
            if (i < 0)
                return null;
            return s.Length > i + 24 && s[i + 24] == '1';
        }
    }

    public string PresentStats { get; private set; } = string.Empty;

    public bool HookOk => this.presentHook != null;

    /// <summary>Frame cap that keeps G-Sync engaged with V-Sync on, NVIDIA's own Reflex formula.</summary>
    public int GsyncCap => this.WindowHz >= 30 ? (int)Math.Floor(3600.0 * this.WindowHz / (this.WindowHz + 3600.0)) : 0;

    public static string LimitModeName(uint mode, ushort fps) => mode switch
    {
        0 => "None (no V-Sync)",
        1 => fps > 0 ? $"V-Sync every refresh + sleep cap {fps} fps" : "V-Sync every refresh",
        2 => "V-Sync every 2nd refresh (1/2)",
        3 => "V-Sync every 3rd refresh (1/3)",
        4 => "V-Sync every 4th refresh (1/4)",
        _ => $"? ({mode})",
    };

    // ------------------------------------------------------------------ framework thread

    /// <summary>Call first thing in every framework tick: the Reflex sleep point, before the game samples input.</summary>
    public void FrameStart()
    {
        this.mainThreadId = Environment.CurrentManagedThreadId;
        var r = this.reflex;
        if (r == null || !this.reflexLive)
            return;

        // With a separate render thread the game thread's work for the previous frame ends here.
        // (On a single thread SIMULATION_END is sent at present instead, so the markers stay in order.)
        var prev = (ulong)Interlocked.Read(ref this.simFrameId);
        if (prev > 0 && this.SeparateRenderThread)
            r.Marker(prev, Reflex.SimulationEnd);

        var st = r.Sleep();
        if (st != 0 && this.sleepErrors++ < 3)
            this.log.Warn($"Reflex: NvAPI_D3D_Sleep returned {st}");
        var id = (ulong)Interlocked.Increment(ref this.simFrameId);
        r.Marker(id, Reflex.SimulationStart);
    }

    /// <summary>Known once both the framework thread and the present thread have run.</summary>
    private bool SeparateRenderThread => this.presentThreadId != 0 && this.mainThreadId != 0 && this.presentThreadId != this.mainThreadId;

    public void Update()
    {
        this.EnsureReflex();

        if (DateTime.Now >= this.nextDisplayCheck)
        {
            this.nextDisplayCheck = DateTime.Now.AddSeconds(1);
            var sw = Stopwatch.StartNew();
            this.CheckDisplay();
            this.displayCheckMaxMs = Math.Max(this.displayCheckMaxMs, sw.Elapsed.TotalMilliseconds);
        }
    }

    private void EnsureReflex()
    {
        var mode = this.config.Reflex;
        var cap = this.config.FrameCapFps;
        var wanted = mode != ReflexMode.Off || cap > 0;
        if (wanted && this.reflex == null && !this.reflexTried)
        {
            this.reflexTried = true;
            var dev = (byte*)Device.Instance();
            var raw = dev != null ? *(nint*)(dev + DevD3D11Device) : 0;
            nint d3d = 0;
            if (raw != 0 && Marshal.QueryInterface(raw, in IidD3D11Device, out d3d) != 0)
                d3d = 0;
            this.reflex = Reflex.TryCreate(d3d, out this.reflexError);
            if (this.reflex == null)
            {
                if (d3d != 0)
                    Marshal.Release(d3d);
                this.log.Warn($"Reflex unavailable: {this.reflexError}");
                this.ReflexState = $"unavailable: {this.reflexError}";
            }
            else
            {
                this.log.Info($"Reflex: NVAPI loaded, D3D11 device 0x{d3d:X}");
            }
        }

        if (this.reflex == null)
            return;

        var interval = cap > 0 ? (uint)Math.Round(1_000_000.0 / Math.Clamp(cap, 20f, 1000f)) : 0u;
        var want = (mode != ReflexMode.Off, mode == ReflexMode.Boost, interval);
        if (this.reflexApplied != want)
        {
            var st = this.reflex.SetSleepMode(want.Item1, want.Item2, want.Item3);
            this.reflexApplied = want;
            this.reflexLive = wanted;
            this.log.Info($"Reflex: SetSleepMode(lowLatency={want.Item1}, boost={want.Item2}, minimumIntervalUs={want.Item3}" +
                          $"{(interval > 0 ? $" = {1_000_000.0 / interval:0.#} fps cap" : string.Empty)}) -> {st}");
            this.ReflexState = !wanted ? "off" : st == 0 ? "active" : $"SetSleepMode error {st}";
        }
    }

    private void CheckDisplay()
    {
        var dev = (byte*)Device.Instance();
        if (dev == null)
            return;

        var hwnd = *(nint*)(dev + DevHwnd);
        this.LimitMode = *(uint*)(dev + DevLimitMode);
        this.LimitFps = *(ushort*)(dev + DevLimitFps);
        this.Fullscreen = dev[DevFullscreen] != 0;
        var gameHz = *(uint*)(dev + DevRefreshRate);
        this.gameHzOriginal ??= gameHz;

        if (hwnd != 0)
        {
            var (name, hz, primary) = Describe(MonitorFromWindow(hwnd, MonitorDefaultToNearest));
            this.WindowMonitor = primary ? $"{name} (primary)" : name;
            this.WindowHz = hz;
            this.windowDevice = name == "?" ? string.Empty : name;
            this.gameHwnd = hwnd;
        }

        var (pName, pHz, _) = Describe(MonitorFromPoint(default, MonitorDefaultToPrimary));
        this.PrimaryMonitor = pName;
        this.PrimaryHz = pHz;
        this.MonitorList = ListMonitors(out var count);
        this.MonitorCount = count;

        // the refresh-rate fix: give the game the rate of the monitor its window is on
        var target = this.WindowHz == 59 ? 60u : this.WindowHz;
        if (this.config.FixRefreshRate && target is >= 24 and <= 1000 && gameHz != target)
        {
            *(uint*)(dev + DevRefreshRate) = target;
            this.wroteRefresh = true;
            this.log.Info($"refresh rate: game value {gameHz} Hz -> {target} Hz (window on {this.WindowMonitor}; the game read {this.gameHzOriginal} Hz at startup)");
            gameHz = target;
        }
        else if (!this.config.FixRefreshRate && this.wroteRefresh && this.gameHzOriginal.HasValue && gameHz != this.gameHzOriginal.Value)
        {
            *(uint*)(dev + DevRefreshRate) = this.gameHzOriginal.Value;
            this.wroteRefresh = false;
            this.log.Info($"refresh rate: fix off, game value restored to {this.gameHzOriginal.Value} Hz");
            gameHz = this.gameHzOriginal.Value;
        }

        this.GameHzNow = gameHz;
        this.WindowedOptimizationsState = WindowedOptimizations();
        var (wo, woSource) = this.WindowedOptimizationsState;

        var line = $"DISPLAY: window on {this.WindowMonitor} {this.WindowHz} Hz | primary {this.PrimaryMonitor} {this.PrimaryHz} Hz | " +
                   $"{count} monitor(s): {this.MonitorList} | game refresh value {gameHz} Hz (read at startup: {this.gameHzOriginal} Hz, fix {(this.config.FixRefreshRate ? "on" : "off")}) | " +
                   $"limiter mode={this.LimitMode} ({LimitModeName(this.LimitMode, this.LimitFps)}) cfg mode={*(uint*)(dev + DevLimitModeCfg)} cfg fps={*(ushort*)(dev + DevLimitFpsCfg)} " +
                   $"fullscreenFlag={dev[DevFullscreen]} | windowed-game optimizations {(wo == true ? "on" : wo == false ? "off" : "not set")} ({woSource}) | game config Fps={ReadConfig("Fps")} FPSInActive={ReadConfig("FPSInActive")} ScreenMode={ReadConfig("ScreenMode")}";
        if (line != this.lastDisplayLine)
        {
            this.lastDisplayLine = line;
            this.log.Info(line);
        }

        this.CheckGsync();
    }

    private static string ReadConfig(string name)
    {
        try
        {
            return Plugin.GameConfig.System.TryGetUInt(name, out var v) ? v.ToString() : "?";
        }
        catch
        {
            return "?";
        }
    }

    // ------------------------------------------------------------------ present (game's render/present thread)

    private void PresentDetour(nint swapChain)
    {
        var t0 = Stopwatch.GetTimestamp();
        var r = this.reflexLive ? this.reflex : null;
        ulong fid = 0;
        if (r != null)
        {
            // frame IDs must match the SIMULATION_START of the same frame; if presenting runs on its own
            // thread it is one frame behind the simulation.
            var sim = (ulong)Interlocked.Read(ref this.simFrameId);
            fid = Environment.CurrentManagedThreadId == this.mainThreadId ? sim : (sim > 0 ? sim - 1 : 0);
            if (fid == 0 || fid <= this.lastPresentFrameId)
            {
                fid = 0; // IDs must strictly increase per marker type
            }
            else
            {
                // GetLatency only returns reports for frames that carry the full marker set
                // (2026-11-04 test: SIMULATION_START + PRESENT_* alone gave 0 reports).
                this.lastPresentFrameId = fid;
                if (!this.SeparateRenderThread)
                    r.Marker(fid, Reflex.SimulationEnd);
                r.Marker(fid, Reflex.RenderSubmitStart);
                r.Marker(fid, Reflex.RenderSubmitEnd);
                r.Marker(fid, Reflex.PresentStart);
            }
        }

        this.presentHook!.Original(swapChain);

        var t1 = Stopwatch.GetTimestamp();
        if (r != null && fid != 0)
            r.Marker(fid, Reflex.PresentEnd);

        try
        {
            this.AfterPresent(swapChain, t0, t1);
        }
        catch
        {
            // diagnostics must never break presenting
        }
    }

    private void AfterPresent(nint swapChain, long t0, long t1)
    {
        var freq = (double)Stopwatch.Frequency;
        lock (this.gate)
        {
            if (this.lastPresentTicks != 0)
            {
                var ms = (float)((t0 - this.lastPresentTicks) * 1000.0 / freq);
                if (ms < 1000 && this.intervals.Count < 20000)
                {
                    this.intervals.Add(ms);
                    this.inPresent.Add((float)((t1 - t0) * 1000.0 / freq));
                }
            }

            this.lastPresentTicks = t0;
        }

        this.presentThreadId = Environment.CurrentManagedThreadId;
        if (!this.threadLogged && this.mainThreadId != 0)
        {
            this.threadLogged = true;
            this.log.Enqueue($"present thread id={this.presentThreadId}, framework thread id={this.mainThreadId} " +
                             $"({(this.presentThreadId == this.mainThreadId ? "same thread" : "separate render thread")})");
        }

        if (t1 < this.nextSwapSample)
            return;
        this.nextSwapSample = t1 + (long)(2 * freq);
        this.SampleSwapChain(*(IDXGISwapChain**)(swapChain + SwapDxgi));
    }

    private void SampleSwapChain(IDXGISwapChain* sc)
    {
        if (sc == null)
            return;

        var sb = new StringBuilder();
        IDXGISwapChain1* sc1 = null;
        var iid1 = IID.IID_IDXGISwapChain1;
        if (SUCCEEDED(sc->QueryInterface(&iid1, (void**)&sc1)) && sc1 != null)
        {
            DXGI_SWAP_CHAIN_DESC1 d;
            DXGI_SWAP_CHAIN_FULLSCREEN_DESC fs;
            if (SUCCEEDED(sc1->GetDesc1(&d)))
            {
                this.SwapEffect = (int)d.SwapEffect;
                sb.Append($"{SwapEffectName(d.SwapEffect)} buffers={d.BufferCount} {d.Width}x{d.Height} {d.Format} flags=0x{d.Flags:X}");
                if ((d.Flags & (uint)DXGI_SWAP_CHAIN_FLAG.DXGI_SWAP_CHAIN_FLAG_ALLOW_MODE_SWITCH) != 0)
                    sb.Append("(ALLOW_MODE_SWITCH)");
                if ((d.Flags & (uint)DXGI_SWAP_CHAIN_FLAG.DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING) != 0)
                    sb.Append("(ALLOW_TEARING)");
                if ((d.Flags & (uint)DXGI_SWAP_CHAIN_FLAG.DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT) != 0)
                    sb.Append("(WAITABLE)");
            }

            if (SUCCEEDED(sc1->GetFullscreenDesc(&fs)))
                sb.Append(fs.Windowed ? " windowed" : $" exclusive-fullscreen {fs.RefreshRate.Numerator}/{fs.RefreshRate.Denominator}");
            sc1->Release();
        }

        IDXGIOutput* output = null;
        if (SUCCEEDED(sc->GetContainingOutput(&output)) && output != null)
        {
            DXGI_OUTPUT_DESC od;
            if (SUCCEEDED(output->GetDesc(&od)))
                sb.Append($" | output {new string(MemoryMarshal.CreateReadOnlySpanFromNullTerminated((char*)&od.DeviceName))}");

            // The display's current mode: Windows HDR on means DWM converts this SDR swap chain to HDR10 every frame.
#pragma warning disable CA1416 // IDXGIOutput6 is Windows 10 1803+; the QueryInterface just fails on older systems
            IDXGIOutput6* output6 = null;
            var iid6 = IID.IID_IDXGIOutput6;
            if (SUCCEEDED(output->QueryInterface(&iid6, (void**)&output6)) && output6 != null)
            {
                DXGI_OUTPUT_DESC1 od1;
                if (SUCCEEDED(output6->GetDesc1(&od1)))
                {
                    this.OutputHdr = od1.ColorSpace == DXGI_COLOR_SPACE_TYPE.DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020;
                    sb.Append(this.OutputHdr ? $" HDR10 ({od1.BitsPerColor}-bit, {od1.MaxLuminance:0} nits)" : $" SDR ({od1.ColorSpace})");
                }

                output6->Release();
            }
#pragma warning restore CA1416

            output->Release();
        }

        IDXGISwapChainMedia* media = null;
        var iidm = IID.IID_IDXGISwapChainMedia;
        if (SUCCEEDED(sc->QueryInterface(&iidm, (void**)&media)) && media != null)
        {
            DXGI_FRAME_STATISTICS_MEDIA ms;
            var hr = media->GetFrameStatisticsMedia(&ms);
            sb.Append(SUCCEEDED(hr) ? $" | presentation {CompositionName(ms.CompositionMode)}" : $" | presentation ? (0x{hr.Value:X8})");
            media->Release();
        }
        else
        {
            sb.Append(" | presentation ? (no IDXGISwapChainMedia)");
        }

        DXGI_FRAME_STATISTICS st;
        if (SUCCEEDED(sc->GetFrameStatistics(&st)))
        {
            if (this.lastSyncQpc != 0 && st.SyncQPCTime.QuadPart > this.lastSyncQpc && st.SyncRefreshCount >= this.lastSyncRefresh)
                this.vblankHz = (st.SyncRefreshCount - this.lastSyncRefresh) * (double)Stopwatch.Frequency / (st.SyncQPCTime.QuadPart - this.lastSyncQpc);
            this.lastSyncRefresh = st.SyncRefreshCount;
            this.lastSyncQpc = st.SyncQPCTime.QuadPart;
        }

        var line = sb.ToString();
        this.swapLine = line;
        if (line != this.loggedSwapLine)
        {
            this.loggedSwapLine = line;
            this.log.Enqueue($"SWAPCHAIN: {line}");
        }
    }

    private static string SwapEffectName(DXGI_SWAP_EFFECT e) => e switch
    {
        DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_DISCARD => "BLT_DISCARD",
        DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_SEQUENTIAL => "BLT_SEQUENTIAL",
        DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL => "FLIP_SEQUENTIAL",
        DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_FLIP_DISCARD => "FLIP_DISCARD",
        _ => e.ToString(),
    };

    private static string CompositionName(DXGI_FRAME_PRESENTATION_MODE m) => m switch
    {
        DXGI_FRAME_PRESENTATION_MODE.DXGI_FRAME_PRESENTATION_MODE_COMPOSED => "COMPOSED (DWM copies the frame)",
        DXGI_FRAME_PRESENTATION_MODE.DXGI_FRAME_PRESENTATION_MODE_OVERLAY => "OVERLAY (hardware plane)",
        DXGI_FRAME_PRESENTATION_MODE.DXGI_FRAME_PRESENTATION_MODE_NONE => "INDEPENDENT FLIP",
        DXGI_FRAME_PRESENTATION_MODE.DXGI_FRAME_PRESENTATION_MODE_COMPOSITION_FAILURE => "COMPOSITION FAILURE",
        _ => m.ToString(),
    };

    // ------------------------------------------------------------------ periodic log lines (framework thread)

    public void WriteStats()
    {
        float[] iv, ip;
        lock (this.gate)
        {
            iv = this.intervals.ToArray();
            ip = this.inPresent.ToArray();
            this.intervals.Clear();
            this.inPresent.Clear();
        }

        if (iv.Length > 2)
        {
            var swings = 0;
            for (var i = 1; i < iv.Length; i++)
                if (Math.Abs(iv[i] - iv[i - 1]) > 0.15f * Math.Max(iv[i], iv[i - 1]))
                    swings++;
            var p50 = Pct(iv, 0.50);
            Array.Sort(ip);
            this.PresentStats = $"{1000.0 / Math.Max(0.01, Pct(iv, 0.5)):0} fps, frame time p50 {p50:0.00} ms, p99 {Pct(iv, 0.99):0.00} ms, " +
                                $"{100.0 * swings / (iv.Length - 1):0.0}% swings";
            this.log.Info($"  PRESENT: {iv.Length} presents | interval p05={Pct(iv, 0.05):0.00} p50={p50:0.00} p95={Pct(iv, 0.95):0.00} p99={Pct(iv, 0.99):0.00} ms " +
                          $"| swings >15% {100.0 * swings / (iv.Length - 1):0.0}% | in game present (limiter + Present) p50={ip[ip.Length / 2]:0.00} p95={ip[(int)(ip.Length * 0.95)]:0.00} ms " +
                          $"| sync interval {SyncInterval(this.LimitMode)} ({LimitModeName(this.LimitMode, this.LimitFps)}) | vblank {this.vblankHz:0.0} Hz | {this.swapLine} " +
                          $"| plugin display check on game thread max {this.displayCheckMaxMs:0.00} ms, G-Sync poll {this.VrrPollMs:0.0} ms (worker)");
            this.displayCheckMaxMs = 0;
        }

        var r = this.reflex;
        if (r != null && this.reflexLive)
        {
            var s = r.GetSleepStatus();
            this.LastSleepStatus = s;
            var lat = new StringBuilder();
            var st = r.GetLatency(this.reports);
            if (st == 0 && this.reports.Count > 0)
            {
                var total = new List<double>();
                double gpuActive = 0, gpuFrame = 0;
                foreach (var f in this.reports)
                {
                    if (f.SimStart != 0 && f.GpuRenderEnd > f.SimStart)
                        total.Add((f.GpuRenderEnd - f.SimStart) / 1000.0);
                    gpuActive += f.GpuActiveUs / 1000.0;
                    gpuFrame += f.GpuFrameUs / 1000.0;
                }

                total.Sort();
                if (total.Count > 0)
                {
                    lat.Append($"sim start -> GPU done avg {Avg(total):0.0} ms p95 {total[(int)(total.Count * 0.95)]:0.0} ms ({total.Count} frames)");
                    this.ReflexLatency = $"{Avg(total):0.0} ms (simulation start to GPU done)";
                }
                else
                {
                    lat.Append("no complete frame reports yet");
                }

                lat.Append($" | GPU active {gpuActive / this.reports.Count:0.00} ms, GPU frame {gpuFrame / this.reports.Count:0.00} ms");
                var last = this.reports[^1];
                lat.Append($" | last report frame {last.FrameId}: sim={last.SimStart} presentStart={last.PresentStart} gpuEnd={last.GpuRenderEnd}");
            }
            else
            {
                lat.Append($"GetLatency={st} ({this.reports.Count} reports)");
            }

            this.log.Info($"  REFLEX: {this.config.Reflex} cap={(this.config.FrameCapFps > 0 ? $"{this.config.FrameCapFps:0.#} fps" : "off")} | " +
                          $"status={s.Result} lowLatency={s.LowLatency} gsync(fsVrr)={s.FullscreenVrr} cplVsync={s.CplVsyncOn} iFlip={s.FullscreenIFlip} " +
                          $"gameSleep={s.UseGameSleep} sleepInterval={s.SleepIntervalUs}us frameGen={(s.FgMultiplier > 1 ? $"{s.FgMultiplier}x" : "off")} | sleep={r.LastSleepStatus} marker={r.LastMarkerStatus} | {lat}");
        }
    }

    public static int SyncInterval(uint mode) => mode is >= 1 and <= 4 ? (int)mode : 0;

    private static float Pct(float[] v, double p)
    {
        var c = (float[])v.Clone();
        Array.Sort(c);
        return c[Math.Clamp((int)(c.Length * p), 0, c.Length - 1)];
    }

    private static double Avg(List<double> v)
    {
        double s = 0;
        foreach (var x in v)
            s += x;
        return v.Count > 0 ? s / v.Count : 0;
    }

    // ------------------------------------------------------------------ checks shown with the game settings

    public IEnumerable<SettingCheck> Checks()
    {
        if (this.WindowHz == 0)
            yield break;

        var orig = this.GameHzOriginal;
        if (orig == this.WindowHz || (orig == 60 && this.WindowHz == 59))
            yield return new SettingCheck("Refresh rate the game uses", $"{this.GameHzNow} Hz", CheckLevel.Ok, $"Matches the monitor the game is on ({this.WindowMonitor}).");
        else if (this.config.FixRefreshRate)
            yield return new SettingCheck("Refresh rate the game uses", $"{this.GameHzNow} Hz", CheckLevel.Ok,
                                          $"Corrected by the plugin. The game read {orig} Hz at startup (primary monitor), but its window is on {this.WindowMonitor} at {this.WindowHz} Hz.");
        else
            yield return new SettingCheck("Refresh rate the game uses", $"{this.GameHzNow} Hz", CheckLevel.Change,
                                          $"The game read {orig} Hz at startup (primary monitor), but its window is on a {this.WindowHz} Hz monitor. " +
                                          "Turn on \"Use the refresh rate of the monitor the game is on\".");

        if (this.BlitModel && !this.Fullscreen)
        {
            var (on, source) = this.WindowedOptimizationsState;
            var iflip = this.LastSleepStatus?.FullscreenIFlip;
            var state = $"Optimizations for windowed games: {(on == true ? "on" : on == false ? "off" : "not set")} ({source})" +
                        (iflip.HasValue ? $", driver sees independent flip: {(iflip.Value ? "yes" : "no")}" : string.Empty);
            yield return new SettingCheck("Swap chain", "legacy blit model (BLT_DISCARD)", iflip == true ? CheckLevel.Ok : CheckLevel.Info,
                                          "In borderless, Windows composites every frame of a blit-model swap chain: a little more latency, " +
                                          "and G-Sync only through NVIDIA's windowed G-Sync, which is what flickered. Windows 11 can upgrade it to " +
                                          "flip model: Settings > System > Display > Graphics > Optimizations for windowed games (restart the game). " +
                                          $"Now: {state}.");
        }

        var capOn = this.config.FrameCapFps > 0 && this.ReflexAvailable;
        if (capOn && this.LimitMode != 0)
            yield return new SettingCheck("Game frame rate limit", LimitModeName(this.LimitMode, this.LimitFps), CheckLevel.Recommend,
                                          "Set it to None (System Configuration > Display Settings) so only the Reflex cap paces frames. " +
                                          "The game's limiter is V-Sync in whole refresh steps and fights the cap.");
        else if (this.LimitMode is >= 2 and <= 4)
            yield return new SettingCheck("Game frame rate limit", LimitModeName(this.LimitMode, this.LimitFps), CheckLevel.Info,
                                          "Every frame waits for whole refreshes: when the GPU misses one, the frame time doubles (e.g. 16.7 -> 33.3 ms). " +
                                          "A Reflex frame cap with the game limit at None paces evenly.");
        else
            yield return new SettingCheck("Game frame rate limit", LimitModeName(this.LimitMode, this.LimitFps), CheckLevel.Ok,
                                          capOn ? "Correct: the Reflex cap paces frames." : "OK.");

        foreach (var c in this.GsyncChecks())
            yield return c;
    }

    // ------------------------------------------------------------------ Win32

    private static (string Name, uint Hz, bool Primary) Describe(nint monitor)
    {
        if (monitor == 0)
            return ("?", 0, false);
        var mi = default(MonitorInfoEx);
        mi.CbSize = sizeof(MonitorInfoEx);
        if (GetMonitorInfoW(monitor, &mi) == 0)
            return ("?", 0, false);
        var name = new string(MemoryMarshal.CreateReadOnlySpanFromNullTerminated(mi.SzDevice));
        var dm = stackalloc byte[220];
        new Span<byte>(dm, 220).Clear();
        *(ushort*)(dm + 68) = 220; // dmSize
        uint hz = 0;
        if (EnumDisplaySettingsW(mi.SzDevice, EnumCurrentSettings, dm) != 0)
            hz = *(uint*)(dm + 184); // dmDisplayFrequency
        return (name, hz, (mi.DwFlags & 1) != 0);
    }

    private static string ListMonitors(out int count)
    {
        MonitorScratch.Clear();
        EnumDisplayMonitors(0, 0, &CollectMonitor, 0);
        count = MonitorScratch.Count;
        var sb = new StringBuilder();
        foreach (var m in MonitorScratch)
        {
            var (n, hz, p) = Describe(m);
            if (sb.Length > 0)
                sb.Append(", ");
            sb.Append($"{n} {hz} Hz{(p ? " primary" : string.Empty)}");
        }

        return sb.ToString();
    }

    [UnmanagedCallersOnly]
    private static int CollectMonitor(nint monitor, nint hdc, nint rect, nint data)
    {
        MonitorScratch.Add(monitor);
        return 1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfoEx
    {
        public int CbSize;
        public int MonLeft, MonTop, MonRight, MonBottom;
        public int WorkLeft, WorkTop, WorkRight, WorkBottom;
        public uint DwFlags;
        public fixed char SzDevice[32];
    }

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint hwnd, uint flags);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromPoint(long point, uint flags);

    [LibraryImport("user32.dll")]
    private static partial int GetMonitorInfoW(nint monitor, MonitorInfoEx* info);

    [LibraryImport("user32.dll")]
    private static partial int EnumDisplaySettingsW(char* device, int mode, byte* devMode);

    [LibraryImport("user32.dll")]
    private static partial int EnumDisplayMonitors(nint hdc, nint clip, delegate* unmanaged<nint, nint, nint, nint, int> proc, nint data);

    public void Dispose()
    {
        this.presentHook?.Dispose();
        this.reflexLive = false;
        Thread.Sleep(50);
        this.reflex?.Dispose();
        this.reflex = null;

        var dev = (byte*)Device.Instance();
        if (dev != null && this.wroteRefresh && this.gameHzOriginal.HasValue)
            *(uint*)(dev + DevRefreshRate) = this.gameHzOriginal.Value;
    }
}
