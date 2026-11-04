using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace XivDlssFix;

/// <summary>
/// NVIDIA Smooth Motion (driver frame generation: one generated frame between every two the game renders) and the
/// frame cap that goes with it. The cap the player sets is the frame rate they SEE. With Smooth Motion the driver
/// doubles what the game renders, so the plugin caps rendering at half: 60 rendered = 120 shown on a 120 Hz screen.
/// Without that, a cap meant for the screen (116, 120) makes Smooth Motion produce 232-240 frames a second for a
/// 120 Hz screen, and the extra ones are thrown away unevenly.
///
/// Detection: the driver loads NvPresent64.dll (its Smooth Motion runtime, logs in C:\NvPresent) into the game when
/// Smooth Motion is on for it; the driver profile says whether it's switched on (NVIDIA app > Graphics > Program
/// settings > Smooth Motion); Reflex's sleep status reports the frame-generation multiplier the driver used; and in
/// borderless DWM composes about twice as many frames as the game presents.
/// </summary>
internal sealed unsafe partial class FramePacing
{
    private const string SmoothMotionDll = "NvPresent64.dll";

    private string lastSmoothLine = string.Empty;
    private bool? vrrForGame;
    private string vrrWhy = "driver profile not read yet";

    // DWM compositions, summed by the worker between two PRESENT lines
    private ulong dwmLastFrame;
    private long dwmLastTicks;
    private double dwmFramesAcc;
    private double dwmSecondsAcc;

    /// <summary>The driver's Smooth Motion runtime is loaded in the game.</summary>
    public bool SmoothMotionLoaded { get; private set; }

    /// <summary>Frame-generation multiplier the driver reports through Reflex (0 or 1 = none). Read every stats interval.</summary>
    public int DriverFgMultiplier => this.LastSleepStatus?.FgMultiplier ?? 0;

    /// <summary>Frames on screen per frame the game renders.</summary>
    public int FrameMultiplier => Math.Max(this.SmoothMotionLoaded ? 2 : 1, this.DriverFgMultiplier);

    /// <summary>
    /// How much the driver stretches the Reflex minimum interval we set: 2 with Smooth Motion (it treats the interval as
    /// the time between frames on screen), else 1. Measured from the sleep status; until then, 2 while Smooth Motion is loaded.
    /// </summary>
    public int DriverIntervalScale => this.driverIntervalScale > 0 ? this.driverIntervalScale : this.SmoothMotionLoaded ? 2 : 1;

    /// <summary>From the stats line: the sleep interval the driver reports, divided by the one we set.</summary>
    private void MeasureDriverIntervalScale(Reflex.SleepStatus s)
    {
        if (this.reflexApplied is not { IntervalUs: > 0 } applied || s.Result != 0 || s.SleepIntervalUs == 0)
            return;
        if ((DateTime.Now - this.reflexAppliedAt).TotalSeconds < 1.5)
            return; // the status may still show the previous interval
        var ratio = (double)s.SleepIntervalUs / applied.IntervalUs;
        var k = (int)Math.Round(ratio);
        if (k is < 1 or > 4 || Math.Abs(ratio - k) > 0.05 || k == this.driverIntervalScale)
            return;
        this.log.Info($"Reflex: the driver sleeps {s.SleepIntervalUs} us for the {applied.IntervalUs} us we set: x{k}" +
                      (this.SmoothMotionLoaded ? " (Smooth Motion)" : string.Empty) + "; the cap divides by that from now on");
        this.driverIntervalScale = k;
    }

    /// <summary>G-Sync is in use for the game: true, false (Fixed Refresh, off, ...), or null while not known.</summary>
    public bool? VrrForGame => this.vrrForGame;

    /// <summary>DWM's exact refresh rate (e.g. 119.88), 0 if unknown.</summary>
    public double DwmRefreshHz { get; private set; }

    /// <summary>Frames DWM composed per second over the last stats interval (windowed / borderless only).</summary>
    public double DwmComposedFps { get; private set; }

    /// <summary>The refresh rate, as exact as it can be read: DWM's in a window, else the display mode's.</summary>
    public double RefreshHz
    {
        get
        {
            var hz = this.WindowHz;
            if (!this.Fullscreen && this.DwmRefreshHz > 0 && Math.Abs(this.DwmRefreshHz - hz) < 2)
                return this.DwmRefreshHz;
            return hz switch
            {
                59 => 60000.0 / 1001,
                119 => 120000.0 / 1001,
                143 => 144000.0 / 1001,
                _ => hz,
            };
        }
    }

    /// <summary>
    /// What the cap should be for the screen: with G-Sync, NVIDIA's cap a few fps under the refresh rate (keeps every
    /// frame inside the G-Sync range); with a fixed refresh rate, exactly the refresh rate (anything else shows some
    /// frames twice: 116 on 120 Hz repeats 4 frames every second).
    /// </summary>
    public float AutoCapFps
    {
        get
        {
            var hz = this.RefreshHz;
            if (hz < 30)
                return 0;
            return this.vrrForGame == false ? (float)hz : (float)Math.Floor(3600.0 * hz / (hz + 3600.0));
        }
    }

    /// <summary>The cap is chosen by the plugin: Auto, or Smooth Motion with the cap off.</summary>
    public bool CapIsAuto => this.config.FrameCapAuto || (this.FrameMultiplier > 1 && this.config.FrameCapFps <= 0);

    /// <summary>The frame rate the cap allows on screen, and what the game may render for it. 0 = no cap.</summary>
    public (float Shown, float Rendered) CapNow()
    {
        var shown = this.CapIsAuto ? this.AutoCapFps : this.config.FrameCapFps;
        return shown > 0 ? (shown, shown / this.FrameMultiplier) : (0f, 0f);
    }

    public string CapDescription
    {
        get
        {
            var (shown, rendered) = this.CapNow();
            if (shown <= 0)
                return "off";
            var why = !this.CapIsAuto ? "set by you"
                : this.vrrForGame == false ? $"auto: the refresh rate ({this.RefreshHz:0.##} Hz, no G-Sync)"
                : $"auto: G-Sync cap for {this.RefreshHz:0.##} Hz";
            if (this.CapIsAuto && !this.config.FrameCapAuto)
                why += ", because Smooth Motion is on and the cap was off";
            return this.FrameMultiplier > 1
                ? $"{shown:0.##} fps on screen = game renders {rendered:0.##} fps, Smooth Motion makes the other {this.FrameMultiplier - 1} of every {this.FrameMultiplier} ({why})"
                : $"{shown:0.##} fps ({why})";
        }
    }

    /// <summary>Once a second on the framework thread, after <see cref="CheckGsync"/>.</summary>
    private void CheckSmoothMotion()
    {
        this.SmoothMotionLoaded = ModuleHandle(SmoothMotionDll) != 0;
        (this.vrrForGame, this.vrrWhy) = this.DecideVrr();

        var p = this.GsyncProfile;
        var profile = p == null ? "not read yet"
            : $"{NvDriver.Describe(p.SmoothMotion, NvDriver.OnOffName)}, APIs {NvDriver.Describe(p.SmoothMotionApis, NvDriver.ApiMaskName)}" +
              (p.SmoothMotionApis.Found ? string.Empty : " (= all)");
        var line = $"SMOOTHMOTION: {SmoothMotionDll} {(this.SmoothMotionLoaded ? "LOADED" : "not loaded")} | driver profile: {profile} | " +
                   $"driver frame-gen multiplier {(this.DriverFgMultiplier > 1 ? $"{this.DriverFgMultiplier}x" : "none")} (Reflex sleep status) | " +
                   $"G-Sync for the game: {(this.vrrForGame is { } v ? v ? "yes" : "no" : "?")} ({this.vrrWhy}) | cap {this.CapDescription}";
        if (line != this.lastSmoothLine)
        {
            this.lastSmoothLine = line;
            this.log.Info(line);
        }
    }

    /// <summary>Is G-Sync in use for the game? The driver profile decides; the live state only when the profile can't tell.</summary>
    private (bool? Vrr, string Why) DecideVrr()
    {
        var p = this.GsyncProfile;
        if (p is { Status: 0 })
        {
            var ov = p.AppOverride;
            if (ov.Found && ov.Value != 0)
                return (false, $"Monitor Technology {NvDriver.OverrideName(ov.Value)}");
            if ((p.AppMode.InProfile && p.AppMode.Value == 0) || (p.AppRequested.InProfile && p.AppRequested.Value == 0))
                return (false, "hidden G-Sync fields off in the game profile");
            var mode = p.AppMode.Found ? p.AppMode.Value : p.GlobalMode.Found ? p.GlobalMode.Value : uint.MaxValue;
            if (mode == 0)
                return (false, "G-Sync off globally");
            if (mode == 1 && !this.Fullscreen)
                return (false, "G-Sync is full screen only, the game is windowed");
            if (mode is 1 or 2)
                return (true, "driver profile allows it");
        }

        if (this.GameFocused && this.Vrr is { Status: 0 } live)
            return (live.Active, "live driver state");
        return (this.vrrForGame, this.vrrWhy); // keep the last answer while the game isn't focused
    }

    /// <summary>Worker thread, about once a second: DWM's exact refresh rate and how many frames it composed.</summary>
    private void SampleDwmRate()
    {
        var info = default(TestRunner.DwmTimingInfo);
        info.CbSize = (uint)sizeof(TestRunner.DwmTimingInfo);
        if (TestRunner.DwmGetCompositionTimingInfo(0, &info) != 0)
            return;
        if (info.RateRefreshDen != 0)
            this.DwmRefreshHz = (double)info.RateRefreshNum / info.RateRefreshDen;

        var now = Stopwatch.GetTimestamp();
        if (this.dwmLastTicks != 0 && info.CFrame >= this.dwmLastFrame)
        {
            var dt = (now - this.dwmLastTicks) / (double)Stopwatch.Frequency;
            lock (this.gate)
            {
                this.dwmFramesAcc += info.CFrame - this.dwmLastFrame;
                this.dwmSecondsAcc += dt;
            }
        }

        this.dwmLastFrame = info.CFrame;
        this.dwmLastTicks = now;
    }

    /// <summary>Called by WriteStats: DWM compositions per second since the last call.</summary>
    private double TakeDwmComposedFps()
    {
        lock (this.gate)
        {
            var fps = this.dwmSecondsAcc > 0.5 ? this.dwmFramesAcc / this.dwmSecondsAcc : 0;
            this.dwmFramesAcc = this.dwmSecondsAcc = 0;
            this.DwmComposedFps = fps;
            return fps;
        }
    }

    private IEnumerable<SettingCheck> SmoothMotionChecks()
    {
        var p = this.GsyncProfile;
        var profileOn = p?.SmoothMotionOn == true;
        if (this.FrameMultiplier > 1)
        {
            var (shown, rendered) = this.CapNow();
            var screen = this.vrrForGame == false ? "fixed refresh" : "G-Sync";
            var gameLimiterOk = this.LimitMode == 0;
            yield return new SettingCheck("Smooth Motion", $"on ({this.FrameMultiplier}x)", gameLimiterOk ? CheckLevel.Ok : CheckLevel.Recommend,
                                          $"The driver generates every other frame. The plugin caps the game at {rendered:0.##} fps so you see {shown:0.##} " +
                                          $"({screen}, {this.RefreshHz:0.##} Hz). " +
                                          (gameLimiterOk ? string.Empty : "Set the game's frame rate limit to None so only this cap paces frames. ") +
                                          "Smooth Motion adds about one rendered frame of latency; turn it off in the NVIDIA app if input feels slow.");
        }
        else if (profileOn)
        {
            yield return new SettingCheck("Smooth Motion", "on in the driver, not running in the game", CheckLevel.Info,
                                          "The NVIDIA app has Smooth Motion on for this game, but the driver didn't load it (NvPresent64.dll). " +
                                          "Restart the game: it's loaded at startup. RTX 40 cards need driver 590.26 or newer.");
        }
        else if (p != null)
        {
            yield return new SettingCheck("Smooth Motion", "off", CheckLevel.Ok,
                                          "NVIDIA's driver frame generation is off. If you turn it on (NVIDIA app > Graphics > Program settings > " +
                                          "Smooth Motion, then restart the game), the plugin halves the frame cap so the generated frames fit the screen.");
        }

        // A cap that doesn't fit a fixed refresh rate shows some frames twice.
        var (s, _) = this.CapNow();
        var hz = this.RefreshHz;
        if (this.vrrForGame == false && s > 0 && hz >= 30 && !this.CapIsAuto)
        {
            var steps = hz / s;
            var effect = s < hz && s > hz / 2
                ? $"{s:0.##} fps shows {hz - s:0} frames a second twice (stutter)"
                : $"{s:0.##} fps keeps frames on screen for uneven lengths of time (stutter)";
            if (Math.Abs(steps - Math.Round(steps)) > 0.02)
                yield return new SettingCheck("Frame cap", $"{s:0.##} fps on a fixed {hz:0.##} Hz screen", CheckLevel.Recommend,
                                              $"Without G-Sync the screen shows a new frame only on its {hz:0.##} Hz refresh, so {effect}. " +
                                              $"Use Auto ({hz:0.##}) or exactly half ({hz / 2:0.##}).");
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint ModuleHandle(string name);
}
