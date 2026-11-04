using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using FFXIVClientStructs.FFXIV.Client.Game.Control;

namespace XivDlssFix;

/// <summary>
/// /dlssfix test: an unattended G-Sync / frame-pacing test. It steps through frame caps while turning the camera at a
/// constant speed. For each step it logs the game's frame cadence next to what the Windows compositor (DWM)
/// actually refreshed (DwmGetCompositionTimingInfo, sampled on a worker thread), plus focus, G-Sync and DLSS 5 state.
/// Restores the cap and the camera's turn when it ends or is stopped.
/// </summary>
internal sealed unsafe class TestRunner
{
    private const float PanRadPerSecond = 0.5f; // ~29°/s
    private const double SettleSeconds = 2.0;   // ignored at the start of each step

    private readonly Plugin plugin;
    private readonly object gate = new();
    private readonly Stopwatch clock = new();
    private readonly List<double> frames = new(4096);
    private readonly List<double> vblanks = new(4096);
    private readonly List<double> composes = new(4096);

    private float[] caps = Array.Empty<float>();
    private int step = -1;
    private double stepSeconds;
    private double stepStart;
    private double lastFrame;
    private float savedCap;
    private bool savedAuto;
    private int focusedTicks, gsyncTicks, ticks;
    private string nrSeen = string.Empty;
    private Thread? sampler;
    private volatile bool sampling;

    public TestRunner(Plugin plugin) => this.plugin = plugin;

    public bool Running => this.step >= 0;

    public void Start(double secondsPerStep, float[] stepCaps)
    {
        if (this.Running)
            this.Stop("restarted");
        this.caps = stepCaps;
        this.stepSeconds = Math.Clamp(secondsPerStep, 6, 120);
        this.savedCap = this.plugin.Config.FrameCapFps;
        this.savedAuto = this.plugin.Config.FrameCapAuto;
        this.plugin.Config.FrameCapAuto = false; // the steps set the cap
        this.clock.Restart();
        this.plugin.Log.Info($"TEST: start | steps {string.Join(", ", this.caps.Select(c => c > 0 ? $"{c:0}" : "off"))} fps, {this.stepSeconds:0} s each | " +
                             $"camera turn {PanRadPerSecond * 180 / Math.PI:0}°/s | cap before: {(this.savedCap > 0 ? $"{this.savedCap:0.#}" : "off")}");
        this.sampling = true;
        this.sampler = new Thread(this.SampleDwm) { IsBackground = true, Name = "DLSSFix DWM sampler", Priority = ThreadPriority.BelowNormal };
        this.sampler.Start();
        this.BeginStep(0);
    }

    public void Stop(string why)
    {
        if (!this.Running)
            return;
        this.sampling = false;
        this.sampler?.Join(500);
        this.sampler = null;
        this.plugin.Config.FrameCapFps = this.savedCap;
        this.plugin.Config.FrameCapAuto = this.savedAuto;
        this.step = -1;
        this.plugin.Log.Info($"TEST: {why} | cap restored to {(this.savedCap > 0 ? $"{this.savedCap:0.#}" : "off")}");
        Plugin.Chat.Print($"[DLSS Fix] test {why}. Results are in the log (TEST lines); the cap is back to {(this.savedCap > 0 ? $"{this.savedCap:0.#}" : "off")}.");
    }

    /// <summary>Framework thread, every frame.</summary>
    public void Tick()
    {
        if (!this.Running)
            return;

        var now = this.clock.Elapsed.TotalSeconds;
        var dt = this.lastFrame > 0 ? now - this.lastFrame : 0;
        this.lastFrame = now;

        // turn the camera at a constant speed
        var cm = CameraManager.Instance();
        var cam = cm != null ? cm->GetActiveCamera() : null;
        if (cam != null && dt is > 0 and < 0.25)
        {
            var h = cam->DirH + (float)(PanRadPerSecond * dt);
            if (h > MathF.PI)
                h -= 2 * MathF.PI;
            cam->DirH = h;
        }

        if (now - this.stepStart >= SettleSeconds)
        {
            lock (this.gate)
            {
                if (dt > 0)
                    this.frames.Add(dt * 1000);
            }

            this.ticks++;
            if (this.plugin.Pacing.GameFocused)
                this.focusedTicks++;
            if (this.plugin.Pacing.Vrr is { Status: 0, Active: true })
                this.gsyncTicks++;
            var nr = this.plugin.Controller.Runtime.NrState;
            if (!this.nrSeen.Contains(nr))
                this.nrSeen = this.nrSeen.Length == 0 ? nr : $"{this.nrSeen}/{nr}";
        }

        if (now - this.stepStart >= this.stepSeconds)
        {
            this.EndStep();
            if (this.step + 1 < this.caps.Length)
                this.BeginStep(this.step + 1);
            else
                this.Stop("done");
        }
    }

    private void BeginStep(int index)
    {
        this.step = index;
        this.stepStart = this.clock.Elapsed.TotalSeconds;
        this.plugin.Config.FrameCapFps = this.caps[index];
        lock (this.gate)
        {
            this.frames.Clear();
            this.vblanks.Clear();
            this.composes.Clear();
        }

        this.ticks = this.focusedTicks = this.gsyncTicks = 0;
        this.nrSeen = string.Empty;
        Plugin.Chat.Print($"[DLSS Fix] test step {index + 1}/{this.caps.Length}: cap {(this.caps[index] > 0 ? $"{this.caps[index]:0}" : "off")} for {this.stepSeconds:0} s");
    }

    private void EndStep()
    {
        string f, v, c;
        lock (this.gate)
        {
            f = Describe(this.frames);
            v = Describe(this.vblanks);
            c = Describe(this.composes);
        }

        var t = Math.Max(1, this.ticks);
        this.plugin.Log.Info($"TEST step {this.step + 1}/{this.caps.Length} cap={(this.caps[this.step] > 0 ? $"{this.caps[this.step]:0}" : "off")} | " +
                             $"focused {100.0 * this.focusedTicks / t:0}% G-Sync active {100.0 * this.gsyncTicks / t:0}% DLSS5 {this.nrSeen} " +
                             $"display {(this.plugin.Pacing.OutputHdr ? "HDR" : "SDR")} | " +
                             $"game frames {f} | DWM refreshes {v} | DWM compositions {c}");
    }

    /// <summary>Worker thread: polls the compositor's timing and records the interval of every new refresh / composition.</summary>
    private void SampleDwm()
    {
        var info = default(DwmTimingInfo);
        info.CbSize = (uint)sizeof(DwmTimingInfo);
        if (DwmGetCompositionTimingInfo(0, &info) != 0)
        {
            this.plugin.Log.Warn("TEST: DwmGetCompositionTimingInfo failed; no DWM timing");
            return;
        }

        var freq = (double)Stopwatch.Frequency;
        ulong lastRefresh = info.CRefresh, lastFrame = info.CFrame, lastVBlank = info.QpcVBlank, lastCompose = info.QpcCompose;
        while (this.sampling)
        {
            info.CbSize = (uint)sizeof(DwmTimingInfo);
            if (DwmGetCompositionTimingInfo(0, &info) == 0)
            {
                var settled = this.clock.Elapsed.TotalSeconds - this.stepStart >= SettleSeconds;
                if (info.CRefresh != lastRefresh)
                {
                    var n = info.CRefresh - lastRefresh;
                    if (settled && n is > 0 and < 8 && info.QpcVBlank > lastVBlank)
                    {
                        lock (this.gate)
                            this.vblanks.Add((info.QpcVBlank - lastVBlank) / freq * 1000 / n);
                    }

                    lastRefresh = info.CRefresh;
                    lastVBlank = info.QpcVBlank;
                }

                if (info.CFrame != lastFrame)
                {
                    var n = info.CFrame - lastFrame;
                    if (settled && n is > 0 and < 8 && info.QpcCompose > lastCompose)
                    {
                        lock (this.gate)
                            this.composes.Add((info.QpcCompose - lastCompose) / freq * 1000 / n);
                    }

                    lastFrame = info.CFrame;
                    lastCompose = info.QpcCompose;
                }
            }

            Thread.Sleep(0);
            Thread.SpinWait(20000); // ~0.1-0.5 ms between polls
        }
    }

    private static string Describe(List<double> v)
    {
        if (v.Count < 3)
            return $"n={v.Count}";
        var s = v.OrderBy(x => x).ToArray();
        double P(double q) => s[Math.Clamp((int)(q * (s.Length - 1)), 0, s.Length - 1)];
        var swings = 0;
        for (var i = 1; i < v.Count; i++)
            if (Math.Abs(v[i] - v[i - 1]) > 0.15 * Math.Max(v[i], v[i - 1]))
                swings++;
        var edges = new[] { 7.5, 9.0, 10.5, 12.5, 15.5, 18.0, 25.0, double.MaxValue };
        var names = new[] { "<7.5", "7.5-9", "9-10.5", "10.5-12.5", "12.5-15.5", "15.5-18", "18-25", ">25" };
        var counts = new int[edges.Length];
        foreach (var x in v)
            counts[Array.FindIndex(edges, e => x < e)]++;
        var hist = string.Join(" ", names.Zip(counts).Where(z => z.Second > 0).Select(z => $"{z.First}:{z.Second}"));
        return $"n={v.Count} p05={P(0.05):0.00} p50={P(0.5):0.00} p95={P(0.95):0.00} p99={P(0.99):0.00} ms swings>15% {100.0 * swings / (v.Count - 1):0.0}% [{hist}]";
    }

    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetCompositionTimingInfo(nint hwnd, DwmTimingInfo* info);

    // dwmapi.h DWM_TIMING_INFO, pack(1). Only the first 64 bytes are read; Rest keeps the size right (292 bytes).
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct DwmTimingInfo
    {
        public uint CbSize;
        public uint RateRefreshNum, RateRefreshDen;
        public ulong QpcRefreshPeriod;
        public uint RateComposeNum, RateComposeDen;
        public ulong QpcVBlank;
        public ulong CRefresh;
        public uint CDXRefresh;
        public ulong QpcCompose;
        public ulong CFrame;
        public fixed byte Rest[292 - 64];
    }
}
