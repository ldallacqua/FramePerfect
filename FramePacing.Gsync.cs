using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace XivDlssFix;

/// <summary>
/// G-Sync state, read from the driver (see <see cref="NvDriver"/>): is VRR live on the game's monitor, and what does the
/// game's driver profile say. NVIDIA Control Panel's per-program "Monitor Technology" only shows the "G-SYNC" field;
/// switching it to Fixed Refresh and back can leave the hidden "Application Mode" / "Application Requested State"
/// fields at Off, which keeps G-Sync off for the game while the Control Panel shows G-SYNC.
/// </summary>
internal sealed unsafe partial class FramePacing
{
    private string windowDevice = string.Empty;
    private nint gameHwnd;
    private DateTime nextProfileRead;
    private Task? profileTask;
    private Task? vrrTask;
    private string lastGsyncLine = string.Empty;

    public NvDriver.VrrInfo? Vrr { get; private set; }

    /// <summary>How long the last NvAPI_Disp_GetVRRInfo took (on a worker thread).</summary>
    public double VrrPollMs { get; private set; }

    public bool GameFocused { get; private set; }

    public NvDriver.Profile? GsyncProfile { get; private set; }

    public string GsyncSummary { get; private set; } = string.Empty;

    private void CheckGsync()
    {
        if (DateTime.Now >= this.nextProfileRead && (this.profileTask == null || this.profileTask.IsCompleted))
        {
            this.nextProfileRead = DateTime.Now.AddSeconds(15);
            var exe = Path.GetFileName(ExePath) ?? "ffxiv_dx11.exe";
            this.profileTask = Task.Run(() =>
            {
                try
                {
                    this.GsyncProfile = NvDriver.ReadProfile(exe);
                }
                catch (Exception e)
                {
                    this.GsyncProfile = new NvDriver.Profile(-1, $"read failed: {e.Message}", NvDriver.NotSet, NvDriver.NotSet, NvDriver.NotSet, NvDriver.NotSet, NvDriver.NotSet);
                }
            });
        }

        if (this.windowDevice.Length == 0)
            return;

        // NvAPI_Disp_GetVRRInfo takes 5-10 ms. On the framework thread that was a 5-10 ms hitch every second
        // (1.4.0), which G-Sync turned into a refresh dip. Always poll it on a worker; the result lags by one poll.
        if (this.vrrTask == null || this.vrrTask.IsCompleted)
        {
            var device = this.windowDevice;
            this.vrrTask = Task.Run(() =>
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    this.Vrr = NvDriver.GetVrr(device);
                    this.VrrPollMs = sw.Elapsed.TotalMilliseconds;
                }
                catch
                {
                    this.Vrr = null;
                }
            });
        }

        var fg = GetForegroundWindow();
        this.GameFocused = this.gameHwnd != 0 && fg != 0 && (fg == this.gameHwnd || GetAncestor(fg, 2) == GetAncestor(this.gameHwnd, 2));

        var v = this.Vrr;
        var live = v == null ? "NVAPI unavailable"
            : v.Value.Status != 0 ? $"GetVRRInfo error {v.Value.Status}"
            : $"active={v.Value.Active} requested={v.Value.Requested} possible={v.Value.Possible} displayInVrrMode={v.Value.DisplayInVrrMode} indicator={v.Value.IndicatorOn}";
        var p = this.GsyncProfile;
        var profile = p == null ? "driver profile not read yet"
            : p.Status != 0 && !p.GlobalMode.Found ? $"driver profile: {p.Name} (status {p.Status})"
            : $"driver profile \"{p.Name}\": Monitor Technology={NvDriver.Describe(p.AppOverride, NvDriver.OverrideName)} | " +
              $"G-SYNC application mode={NvDriver.Describe(p.AppMode, NvDriver.ModeName)} | application requested state={NvDriver.Describe(p.AppRequested, NvDriver.ModeName)} | " +
              $"global mode={NvDriver.Describe(p.GlobalMode, NvDriver.ModeName, "global")}";

        this.GsyncSummary = $"{live} on {this.windowDevice} | game window focused={this.GameFocused} | {profile}";
        var line = "GSYNC: live " + this.GsyncSummary;
        if (line != this.lastGsyncLine)
        {
            this.lastGsyncLine = line;
            this.log.Info(line);
        }
    }

    /// <summary>One "G-Sync" line for the game settings check.</summary>
    private IEnumerable<SettingCheck> GsyncChecks()
    {
        var p = this.GsyncProfile;
        var v = this.Vrr;
        if (p == null && v == null)
            yield break;

        var liveOk = v is { Status: 0 };
        var active = liveOk && v!.Value.Active;
        var now = !liveOk ? "?" : active ? "active" : this.GameFocused ? "not active" : "not active (game not focused)";

        if (p is { Status: 0 })
        {
            var ov = p.AppOverride;
            if (ov.Found && ov.Value != 0)
            {
                yield return new SettingCheck("G-Sync", $"off for this game ({NvDriver.OverrideName(ov.Value)})", CheckLevel.Info,
                                              $"NVIDIA Control Panel > Manage 3D settings > Program Settings > {p.Name}: Monitor Technology is {NvDriver.OverrideName(ov.Value)}. " +
                                              "That's the DLSS 5 flicker workaround. Set it to G-SYNC Compatible to use G-Sync.");
                yield break;
            }

            var hiddenOff = (p.AppMode.InProfile && p.AppMode.Value == 0) || (p.AppRequested.InProfile && p.AppRequested.Value == 0);
            if (hiddenOff)
            {
                yield return new SettingCheck("G-Sync", $"off for this game (hidden profile fields), live: {now}", CheckLevel.Recommend,
                                              "NVIDIA Control Panel shows G-SYNC for this game, but the game's driver profile also has \"G-SYNC - Application Mode\" " +
                                              "and/or \"Application Requested State\" set to Off. The Control Panel doesn't show these fields and leaves them behind " +
                                              "when Monitor Technology goes from Fixed Refresh back to G-SYNC. To turn G-Sync back on: NVIDIA Control Panel > Manage 3D settings > " +
                                              $"Program Settings > {p.Name} > Restore (resets every setting of this game's profile), or set both fields to " +
                                              "\"Fullscreen and Windowed\" in NVIDIA Profile Inspector. Then restart the game.");
                yield break;
            }

            var mode = p.AppMode.Found ? p.AppMode.Value : p.GlobalMode.Value;
            if (mode == 1 && !this.Fullscreen)
            {
                yield return new SettingCheck("G-Sync", $"full screen only, live: {now}", CheckLevel.Info,
                                              "Borderless needs \"Enable for windowed and full screen mode\" (NVIDIA Control Panel > Display > Set up G-SYNC).");
                yield break;
            }

            if (mode == 0 && p.GlobalMode.Found)
            {
                yield return new SettingCheck("G-Sync", $"off, live: {now}", CheckLevel.Info, "G-Sync is off in NVIDIA Control Panel > Display > Set up G-SYNC.");
                yield break;
            }
        }

        if (active)
            yield return new SettingCheck("G-Sync", "active", CheckLevel.Ok, $"The driver reports variable refresh live on {this.windowDevice}.");
        else if (liveOk)
            yield return new SettingCheck("G-Sync", now, CheckLevel.Info,
                                          this.GameFocused
                                              ? "G-Sync is allowed for this game, but the driver doesn't report variable refresh on the game's monitor right now."
                                              : "Click into the game: windowed G-Sync only runs while the game window has focus.");
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial nint GetAncestor(nint hwnd, uint flags);
}
