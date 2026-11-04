using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace FramePerfect;

/// <summary>
/// Refresh lock: with a fixed refresh rate in borderless, keep the game's presents in the middle between two refreshes.
///
/// Why (PresentMon, 2026-11-04, Smooth Motion, 120 Hz Fixed Refresh): DWM picks the frame for a refresh about 1 ms
/// before it. A present in that last millisecond waits one refresh, and the next present replaces it: one frame dropped,
/// one frame shown twice. A free-running cap at exactly 120 shown drifts slowly against the refresh and spends
/// minutes in that zone (20-34 drops a second); the game's V-Sync held presents ~1 ms after the refresh, too close:
/// a few ms of jitter put them back in the zone (bursts of 3-8 drops a second).
///
/// How: the vblank time comes from DwmGetCompositionTimingInfo (qpcVBlank, qpcRefreshPeriod), read once a second on a
/// worker (it agreed with IDXGIOutput::WaitForVBlank to 0.03-0.06 ms). At the start of every frame, after the Reflex
/// sleep, the plugin waits until the frame's slot on the refresh grid (Reflex's cap is set 6% short so it never holds a
/// frame past its slot; in practice the lock does all the waiting). A slow controller moves the slot so the frames
/// reach DWM in the middle of the safe part of the refresh (see <see cref="LockTargetMs"/>).
/// </summary>
internal sealed unsafe partial class FramePacing
{
    private const double LockReflexShare = 0.94;   // Reflex waits this share of the frame time, the lock the rest
    private const double DropZoneMs = 1.2;         // presents this close before a vblank were the ones dropped
    // When the real DXGI presents happen, relative to the return of the game's Present call (PresentMon vs the plugin's
    // phase, same 20 s, 2026-11-04 15:55, no V-Sync): with Smooth Motion its presents come ~1.95 ms AFTER the game's call
    // returns (game 4.91 ms after vblank -> DXGI 6.76 / 6.96 ms, the two streams). Without Smooth Motion the game's
    // own DXGI present is inside the call, before it returns (estimate).
    private const double SmoothMotionPresentDelayMs = 1.95;
    private const double PlainPresentDelayMs = -0.5;

    private volatile VBlankAnchor? vblankAnchor;
    private nint lockTimer;
    private bool lockWasActive;
    private double lockTheta;      // frame-start slot, QPC ticks after the vblank
    private bool lockThetaSet;
    private long lastLockStart;
    private int lockLate;
    private int lockWaits;
    private double lockWaitTicks;
    private DateTime nextLockAdjust;

    // present phase, from the present thread (under gate)
    private readonly List<float> presentPhases = new(1024);
    private double phaseSin;
    private double phaseCos;
    private int phaseCount;

    private sealed record VBlankAnchor(long Qpc, double PeriodTicks, long ReadAt);

    /// <summary>Frame starts are being held to the refresh grid right now.</summary>
    public bool RefreshLockActive { get; private set; }

    /// <summary>Refreshes per rendered frame while the lock is active (2 = Smooth Motion at the refresh rate).</summary>
    public int RefreshLockRefreshes { get; private set; }

    public string RefreshLockState { get; private set; } = "off";

    /// <summary>Should the lock run? Fixed refresh, borderless, a cap that is the refresh rate or an exact fraction of it.</summary>
    private int RefreshLockFactor(float rendered)
    {
        if (!this.config.RefreshLock || this.vrrForGame != false || this.Fullscreen || rendered <= 0 || this.LimitMode != 0)
            return 0;
        var a = this.vblankAnchor;
        if (a == null || Stopwatch.GetTimestamp() - a.ReadAt > 3 * Stopwatch.Frequency)
            return 0;
        var k = this.RefreshHz / rendered;
        var n = (int)Math.Round(k);
        return n is >= 1 and <= 4 && Math.Abs(k - n) < 0.02 ? n : 0;
    }

    /// <summary>Worker: remember the last vblank and the refresh period.</summary>
    private void SetVBlankAnchor(ulong qpcVBlank, ulong qpcRefreshPeriod)
    {
        if (qpcVBlank != 0 && (long)qpcRefreshPeriod > Stopwatch.Frequency / 1000)
            this.vblankAnchor = new VBlankAnchor((long)qpcVBlank, qpcRefreshPeriod, Stopwatch.GetTimestamp());
    }

    /// <summary>Framework thread, after the Reflex sleep: wait until this frame's slot on the refresh grid.</summary>
    private void RefreshLockWait()
    {
        var a = this.vblankAnchor;
        var k = this.RefreshLockRefreshes;
        if (!this.RefreshLockActive || a == null || k == 0)
        {
            this.lastLockStart = 0;
            this.lockThetaSet = false;
            return;
        }

        var p = a.PeriodTicks;
        this.AdjustLockSlot(p);

        var now = Stopwatch.GetTimestamp();
        var period = p * k;
        double target;
        if (this.lastLockStart == 0)
        {
            // first frame: the next slot on the grid
            var m0 = Math.Ceiling((now - a.Qpc - this.lockTheta) / p);
            target = a.Qpc + this.lockTheta + (m0 * p);
        }
        else
        {
            var ideal = this.lastLockStart + period;
            var m = Math.Round((ideal - a.Qpc - this.lockTheta) / p);
            target = a.Qpc + this.lockTheta + (m * p);
        }

        if (target - now > period)
            target = now; // anchor or slot jumped; start over from here

        if (target > now)
        {
            this.Wait((long)target);
            this.lockWaits++;
            this.lockWaitTicks += target - now;
            this.lastLockStart = (long)target;
        }
        else if (now - target > period)
        {
            this.lastLockStart = now; // far behind (long frame, loading): resync
            this.lockLate++;
        }
        else
        {
            this.lastLockStart = (long)target; // a bit late: keep the grid, Reflex's 6% slack catches up
            if (now - target > 0.25 * p)
                this.lockLate++;
        }
    }

    /// <summary>
    /// Where the game's Present call should return, in ms after the vblank: the real presents then land in the middle of
    /// the safe part of the refresh (0 .. period - drop zone). 120 Hz with Smooth Motion: (8.33 - 1.2) / 2 - 1.95 = 1.6 ms.
    /// </summary>
    public double LockTargetMs(double periodMs) =>
        ((periodMs - DropZoneMs) / 2) - (this.SmoothMotionLoaded ? SmoothMotionPresentDelayMs : PlainPresentDelayMs);

    /// <summary>Move the frame-start slot so the game's Present returns <see cref="LockTargetMs"/> after the vblank.</summary>
    private void AdjustLockSlot(double p)
    {
        if (DateTime.Now < this.nextLockAdjust)
            return;
        this.nextLockAdjust = DateTime.Now.AddSeconds(0.5);

        double s, c;
        int n;
        lock (this.gate)
        {
            (s, c, n) = (this.phaseSin, this.phaseCos, this.phaseCount);
            this.phaseSin = this.phaseCos = 0;
            this.phaseCount = 0;
        }

        if (n < 10)
            return;
        var mean = (Math.Atan2(s, c) / (2 * Math.PI) * p + p) % p; // circular mean of the present phase, ticks
        var target = Wrap(this.LockTargetMs(p * 1000.0 / Stopwatch.Frequency) * Stopwatch.Frequency / 1000.0, p);
        var err = target - mean;
        err -= Math.Round(err / p) * p; // shortest way around
        if (!this.lockThetaSet)
        {
            this.lockTheta = Wrap(this.lockTheta + err, p); // first correction in one step (one uneven frame)
            this.lockThetaSet = true;
        }
        else
        {
            var maxStep = Stopwatch.Frequency * 0.0005; // then at most 0.5 ms per half second
            this.lockTheta = Wrap(this.lockTheta + Math.Clamp(err * 0.7, -maxStep, maxStep), p);
        }
    }

    private static double Wrap(double x, double p) => ((x % p) + p) % p;

    /// <summary>Present thread: where in the refresh did this present land.</summary>
    private void RecordPresentPhase(long t)
    {
        var a = this.vblankAnchor;
        if (a == null || this.Fullscreen)
            return;
        var p = a.PeriodTicks;
        var ph = Wrap(t - a.Qpc, p);
        var ang = ph / p * 2 * Math.PI;
        lock (this.gate)
        {
            if (this.presentPhases.Count < 20000)
                this.presentPhases.Add((float)(ph * 1000.0 / Stopwatch.Frequency));
            this.phaseSin += Math.Sin(ang);
            this.phaseCos += Math.Cos(ang);
            this.phaseCount++;
        }
    }

    /// <summary>For the PRESENT line: present phase distribution and the lock's state since the last call.</summary>
    private string TakeRefreshPhaseStats()
    {
        float[] ph;
        lock (this.gate)
        {
            ph = this.presentPhases.ToArray();
            this.presentPhases.Clear();
        }

        var a = this.vblankAnchor;
        var lockText = this.RefreshLockActive
            ? $"refresh lock ON ({this.RefreshLockRefreshes} refreshes per rendered frame, target {this.LockTargetMs(a?.PeriodTicks * 1000.0 / Stopwatch.Frequency ?? 8.333):0.00} ms, " +
              $"slot {this.lockTheta * 1000.0 / Stopwatch.Frequency:0.00} ms after vblank, " +
              $"waited {(this.lockWaits > 0 ? this.lockWaitTicks * 1000.0 / Stopwatch.Frequency / this.lockWaits : 0):0.00} ms avg on {this.lockWaits} frames, late {this.lockLate})"
            : $"refresh lock off ({this.RefreshLockState})";
        this.lockWaits = this.lockLate = 0;
        this.lockWaitTicks = 0;
        if (ph.Length < 10 || a == null)
            return lockText;

        Array.Sort(ph);
        var periodMs = a.PeriodTicks * 1000.0 / Stopwatch.Frequency;
        var inZone = 0;
        foreach (var x in ph)
            if (x > periodMs - DropZoneMs)
                inZone++;
        return $"game presents {ph[ph.Length / 10]:0.00} / {ph[ph.Length / 2]:0.00} / {ph[ph.Length * 9 / 10]:0.00} ms after vblank (p10/p50/p90 of {periodMs:0.000} ms), " +
               $"{100.0 * inZone / ph.Length:0.0}% in the last {DropZoneMs} ms before a vblank (drop zone) | {lockText}";
    }

    /// <summary>Precise wait on the framework thread: a high-resolution timer for the bulk, then a short spin.</summary>
    private void Wait(long target)
    {
        var freq = Stopwatch.Frequency;
        var remaining = target - Stopwatch.GetTimestamp();
        if (remaining > freq * 3 / 4000)
        {
            if (this.lockTimer == 0)
                this.lockTimer = CreateWaitableTimerExW(0, 0, 2 /* CREATE_WAITABLE_TIMER_HIGH_RESOLUTION */, 0x1F0003);
            if (this.lockTimer != 0)
            {
                var due = -(long)((remaining - (freq / 2000)) * 10_000_000.0 / freq); // relative, 100 ns units, wake 0.5 ms early
                if (SetWaitableTimer(this.lockTimer, &due, 0, 0, 0, 0) != 0)
                    WaitForSingleObject(this.lockTimer, 50);
            }
        }

        while (Stopwatch.GetTimestamp() < target)
            Thread.SpinWait(20);
    }

    private void DisposeRefreshLock()
    {
        if (this.lockTimer != 0)
            CloseHandle(this.lockTimer);
        this.lockTimer = 0;
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint CreateWaitableTimerExW(nint attributes, nint name, uint flags, uint access);

    [LibraryImport("kernel32.dll")]
    private static partial int SetWaitableTimer(nint timer, long* dueTime, int period, nint completion, nint arg, int resume);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(nint handle, uint ms);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
