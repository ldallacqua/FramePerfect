using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;

namespace FramePerfect;

/// <summary>
/// Sets the game's own options (System Configuration) to what the plugin needs, so players don't have to:
///
///  * Graphics Upscaling = NVIDIA DLSS. Only on an NVIDIA RTX card (the game offers DLSS only there).
///  * Frame Rate Limit = None, while the plugin's Reflex frame cap runs. The game's limiter is V-Sync in whole refresh
///    steps (16.7 / 33.3 ms) and fights the cap; Smooth Motion ignores it; the refresh lock needs it off.
///  * Radial Blur = off. It smears the whole image while the camera turns.
///
/// Only game options. NVIDIA driver settings (Fixed Refresh, Smooth Motion) stay the player's: the plugin explains them.
/// The player's own value is saved before the first change and put back when they hand an option back (or press
/// Restore). Nothing is changed while System Configuration is open, so the plugin never fights the menu.
///
/// Option values (Dalamud SystemConfigOption, FFXIV.cfg, ffxiv_dx11.exe 2026.10.16):
///   GraphicsRezoUpscaleType  0 = AMD FSR, 1 = NVIDIA DLSS (GraphicsConfig+0x54 is that + 1: 1 = FSR, 2 = DLSS)
///   Fps                      0 = None, 1 = Main Display Refresh Rate, 2 = 1/2, 3 = 1/4
///   RadialBlur_DX11          0 = off, 1 = on
/// </summary>
internal sealed class GameOptimizer
{
    public const string Upscaling = "GraphicsRezoUpscaleType";
    public const string FrameLimit = "Fps";
    public const string RadialBlur = "RadialBlur_DX11";

    private const uint Dlss = 1;
    private const uint LimitNone = 0;
    private const uint BlurOff = 0;

    private readonly Plugin plugin;
    private DateTime nextTick;
    private string lastLog = string.Empty;
    private readonly Dictionary<string, int> reapplied = new();

    public GameOptimizer(Plugin plugin)
    {
        this.plugin = plugin;
        Task.Run(DetectGpu);
    }

    /// <summary>The game's graphics card, e.g. "NVIDIA GeForce RTX 4090" (empty until read).</summary>
    public static string GpuName { get; private set; } = string.Empty;

    /// <summary>An NVIDIA RTX card: DLSS and Reflex work. Null until the graphics card was read.</summary>
    public static bool? NvidiaRtx { get; private set; }

    /// <summary>Is System Configuration open (the plugin waits until it's closed).</summary>
    public bool WaitingForMenu { get; private set; }

    public sealed record Item(string Name, string Label, string Path, string BestText, string Why);

    public static readonly Item[] Items =
    {
        new(Upscaling, "Graphics Upscaling", "System Configuration > Graphics Settings > Resolution", "NVIDIA DLSS",
            "Frame Perfect controls DLSS. With AMD FSR selected it can't do anything."),
        new(FrameLimit, "Frame Rate Limit", "System Configuration > Display Settings", "None",
            "Frame Perfect caps the frame rate itself, more precisely. The game's limit is V-Sync, which can only wait whole " +
            "refreshes (a missed one doubles the frame time) and makes Smooth Motion drop frames."),
        new(RadialBlur, "Radial Blur", "System Configuration > Graphics Settings > Effects", "Off",
            "It blurs the whole picture whenever the camera turns, which hides what DLSS keeps sharp."),
    };

    public bool IsManaged(string name)
    {
        var c = this.plugin.Config;
        if (!c.AutoGameSettings)
            return false;
        return name switch
        {
            Upscaling => c.ManageUpscaling,
            FrameLimit => c.ManageFrameLimit,
            RadialBlur => c.ManageRadialBlur,
            _ => false,
        };
    }

    public void SetManaged(string name, bool on)
    {
        var c = this.plugin.Config;
        switch (name)
        {
            case Upscaling: c.ManageUpscaling = on; break;
            case FrameLimit: c.ManageFrameLimit = on; break;
            case RadialBlur: c.ManageRadialBlur = on; break;
        }

        c.Save();
        this.nextTick = DateTime.MinValue;
    }

    /// <summary>The value the plugin wants for an option right now, or null when it should be left to the player.</summary>
    public uint? Wanted(string name)
    {
        if (!this.plugin.Config.Enabled)
            return null;
        switch (name)
        {
            case Upscaling:
                return NvidiaRtx == true && this.UpscaleMappingOk() ? Dlss : null;
            case FrameLimit:
                var p = this.plugin.Pacing;
                return p.ReflexAvailable && p.CapNow().Rendered > 0 ? LimitNone : null;
            case RadialBlur:
                return BlurOff;
            default:
                return null;
        }
    }

    /// <summary>Why an option isn't being set right now (for the UI), or empty.</summary>
    public string WhyNot(string name)
    {
        if (!this.plugin.Config.Enabled)
            return "Frame Perfect is turned off";
        return name switch
        {
            Upscaling when NvidiaRtx == null => "reading the graphics card...",
            Upscaling when NvidiaRtx == false => $"needs an NVIDIA RTX graphics card ({(GpuName.Length > 0 ? GpuName : "none found")})",
            Upscaling when !this.UpscaleMappingOk() => "this game version stores the option differently; left alone",
            FrameLimit when !this.plugin.Pacing.ReflexAvailable => "the Reflex frame cap isn't available (NVIDIA only)",
            FrameLimit => "the frame cap is off, so the game's own limit is kept",
            _ => string.Empty,
        };
    }

    public static uint? Read(string name)
    {
        try
        {
            return Plugin.GameConfig.System.TryGetUInt(name, out var v) ? v : null;
        }
        catch
        {
            return null;
        }
    }

    public static string ValueName(string name, uint? v) => v == null ? "?" : name switch
    {
        Upscaling => v switch { 0 => "AMD FSR", 1 => "NVIDIA DLSS", _ => $"? ({v})" },
        FrameLimit => v switch { 0 => "None", 1 => "Main Display Refresh Rate", 2 => "1/2 Main Display Refresh Rate", 3 => "1/4 Main Display Refresh Rate", _ => $"? ({v})" },
        RadialBlur => v == 0 ? "Off" : "On",
        _ => v.ToString()!,
    };

    /// <summary>The option value and the GraphicsConfig byte agree with the documented mapping (FSR 0/1, DLSS 1/2).</summary>
    private bool UpscaleMappingOk()
    {
        var cfg = Read(Upscaling);
        var s = GameSettings.Read();
        return cfg is 0 or 1 && (!s.Valid || s.Upscale == cfg + 1);
    }

    /// <summary>Framework thread, every frame (works once a second).</summary>
    public void Tick()
    {
        if (DateTime.Now < this.nextTick)
            return;
        this.nextTick = DateTime.Now.AddSeconds(1);
        if (!Plugin.ClientState.IsLoggedIn)
            return;

        // never change an option under the player's cursor: wait until System Configuration is closed
        var menu = Plugin.GameGui.GetAddonByName("ConfigSystem");
        this.WaitingForMenu = !menu.IsNull && menu.IsVisible;
        if (this.WaitingForMenu)
            return;

        var cfg = this.plugin.Config;
        var changed = new List<string>();
        var fought = new List<string>();
        var saveConfig = false;
        foreach (var item in Items)
        {
            var now = Read(item.Name);
            if (now == null)
                continue;
            var want = this.IsManaged(item.Name) ? this.Wanted(item.Name) : null;
            if (want is { } w)
            {
                if (now == w)
                    continue;
                if (!cfg.SavedGameSettings.ContainsKey(item.Name))
                {
                    cfg.SavedGameSettings[item.Name] = now.Value;
                    saveConfig = true;
                }

                if (!this.Set(item.Name, w))
                    continue;
                changed.Add($"{item.Label}: {ValueName(item.Name, now)} -> {ValueName(item.Name, w)}");
                this.reapplied.TryGetValue(item.Name, out var n);
                this.reapplied[item.Name] = n + 1;
                if (n >= 1)
                    fought.Add(item.Label); // the player changed it back since: say how to keep their value
            }
            else if (cfg.SavedGameSettings.TryGetValue(item.Name, out var orig))
            {
                // handed back to the player (or no longer needed): put their own value back
                cfg.SavedGameSettings.Remove(item.Name);
                saveConfig = true;
                this.reapplied.Remove(item.Name);
                if (now != orig && this.Set(item.Name, orig))
                    changed.Add($"{item.Label}: back to your {ValueName(item.Name, orig)}");
            }
        }

        if (saveConfig)
            cfg.Save();
        if (changed.Count == 0)
            return;

        var text = string.Join(", ", changed);
        this.plugin.Log.Info($"GAME SETTINGS: {text}");
        if (text == this.lastLog && fought.Count == 0)
            return;
        this.lastLog = text;
        if (fought.Count > 0)
            this.plugin.Notify($"{string.Join(" and ", fought)} {(fought.Count > 1 ? "are" : "is")} set by Frame Perfect. " +
                               "To choose it yourself, untick it in Frame Perfect > Game settings.", Dalamud.Interface.ImGuiNotification.NotificationType.Info);
        else
            this.plugin.Notify($"Set the best game options: {text}.", Dalamud.Interface.ImGuiNotification.NotificationType.Success);
    }

    /// <summary>Put every option the plugin changed back to the player's value and stop managing them.</summary>
    public void RestoreAll()
    {
        var cfg = this.plugin.Config;
        cfg.AutoGameSettings = false;
        var back = new List<string>();
        foreach (var item in Items)
        {
            if (!cfg.SavedGameSettings.TryGetValue(item.Name, out var orig))
                continue;
            if (Read(item.Name) != orig && this.Set(item.Name, orig))
                back.Add($"{item.Label}: {ValueName(item.Name, orig)}");
        }

        cfg.SavedGameSettings.Clear();
        cfg.Save();
        this.reapplied.Clear();
        this.plugin.Log.Info($"GAME SETTINGS: restored the player's values ({(back.Count > 0 ? string.Join(", ", back) : "nothing to change")}); automatic settings off");
        this.plugin.Notify(back.Count > 0 ? $"Restored your game options: {string.Join(", ", back)}." : "Your game options were already your own.",
                           Dalamud.Interface.ImGuiNotification.NotificationType.Info);
    }

    private bool Set(string name, uint value)
    {
        try
        {
            Plugin.GameConfig.System.Set(name, value);
            return true;
        }
        catch (Exception e)
        {
            this.plugin.Log.Warn($"GAME SETTINGS: could not set {name} = {value}: {e.Message}");
            return false;
        }
    }

    /// <summary>Worker: the first NVIDIA adapter (the game runs on it on a desktop; laptops pick it for games too).</summary>
    private static unsafe void DetectGpu()
    {
        try
        {
            IDXGIFactory1* factory = null;
            var iid = IID.IID_IDXGIFactory1;
            if (CreateDXGIFactory1(&iid, (void**)&factory) < 0 || factory == null)
            {
                NvidiaRtx = false;
                return;
            }

            try
            {
                var found = string.Empty;
                var rtx = false;
                IDXGIAdapter1* adapter = null;
                for (uint i = 0; factory->EnumAdapters1(i, &adapter) >= 0; i++)
                {
                    DXGI_ADAPTER_DESC1 d;
                    if (adapter->GetDesc1(&d) >= 0)
                    {
                        var name = new string((char*)&d.Description);
                        if (d.VendorId == 0x10DE && name.Contains("RTX", StringComparison.OrdinalIgnoreCase))
                        {
                            found = name;
                            rtx = true;
                        }
                        else if (found.Length == 0 && (d.Flags & (uint)DXGI_ADAPTER_FLAG.DXGI_ADAPTER_FLAG_SOFTWARE) == 0)
                        {
                            found = name;
                        }
                    }

                    adapter->Release();
                    if (rtx)
                        break;
                }

                GpuName = found.Trim();
                NvidiaRtx = rtx;
            }
            finally
            {
                factory->Release();
            }
        }
        catch
        {
            NvidiaRtx = false;
        }
    }
}
