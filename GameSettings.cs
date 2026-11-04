using System;
using System.Collections.Generic;
using GraphicsConfig = FFXIVClientStructs.FFXIV.Client.Graphics.Render.GraphicsConfig;

namespace FramePerfect;

internal enum CheckLevel
{
    Ok,
    Info,
    Recommend,
    Change,
}

internal sealed record SettingCheck(string Option, string Current, CheckLevel Level, string Advice);

/// <summary>
/// Reads the game's own graphics options (System Configuration > Graphics Settings) and says which
/// ones the plugin needs, and what to change. Menu names are the English client's.
///
/// GraphicsConfig (FFXIVClientStructs names, values from ffxiv_dx11.exe 2026.10.16):
///   +0x54 GraphicsRezoUpscaleType  1 = AMD FSR, 2 = NVIDIA DLSS
///   +0x49 DynamicRezoThreshold     0 = Always Enabled, 1 = Below 30 fps, 2 = Below 60 fps
///         (only sets the frame-rate target of the dynamic-resolution controller at +0x2DA946:
///          Always = the frame cap / refresh rate; with the plugin's lock the controller has no range)
///   +0x4C GraphicsRezoScale        3D Resolution Scaling / 100 (unused with DLSS unless Custom)
///   +0x28 RadialBlur
/// </summary>
internal static unsafe class GameSettings
{
    public const string ResolutionPath = "System Configuration > Graphics Settings > Resolution";
    public const string EffectsPath = "System Configuration > Graphics Settings > Effects";

    public readonly record struct Snapshot(bool Valid, byte Upscale, byte Threshold, float RezoScale, bool RadialBlur);

    public static Snapshot Read()
    {
        var cfg = (byte*)GraphicsConfig.Instance();
        if (cfg == null)
            return default;
        return new Snapshot(true, cfg[0x54], cfg[0x49], *(float*)(cfg + 0x4C), cfg[0x28] != 0);
    }

    public static string UpscaleName(byte v) => v switch
    {
        1 => "AMD FSR",
        2 => "NVIDIA DLSS",
        _ => $"? ({v})",
    };

    public static string ThresholdName(byte v) => v switch
    {
        0 => "Always Enabled",
        1 => "Below 30 fps",
        2 => "Below 60 fps",
        _ => $"? ({v})",
    };

    public static List<SettingCheck> Evaluate(Plugin plugin)
    {
        var s = Read();
        var config = plugin.Config;
        var opt = plugin.Optimizer;
        var list = new List<SettingCheck>();
        if (!s.Valid)
            return list;

        if (s.Upscale == 2)
            list.Add(new SettingCheck("Graphics Upscaling", UpscaleName(s.Upscale), CheckLevel.Ok,
                                      opt.IsManaged(GameOptimizer.Upscaling) ? "Correct (kept on NVIDIA DLSS by Frame Perfect)." : "Correct."));
        else if (opt.IsManaged(GameOptimizer.Upscaling) && opt.Wanted(GameOptimizer.Upscaling) != null)
            list.Add(new SettingCheck("Graphics Upscaling", UpscaleName(s.Upscale), CheckLevel.Info,
                                      "Frame Perfect switches it to NVIDIA DLSS as soon as System Configuration is closed."));
        else if (GameOptimizer.NvidiaRtx == false)
            list.Add(new SettingCheck("Graphics Upscaling", UpscaleName(s.Upscale), CheckLevel.Change,
                                      $"DLSS needs an NVIDIA RTX graphics card ({(GameOptimizer.GpuName.Length > 0 ? GameOptimizer.GpuName : "none found")}). " +
                                      "Without it Frame Perfect can't change the image; the frame rate features need an NVIDIA card too."));
        else
            list.Add(new SettingCheck("Graphics Upscaling", UpscaleName(s.Upscale), CheckLevel.Change,
                                      $"Switch to NVIDIA DLSS: {ResolutionPath} > Graphics Upscaling, or tick it in Frame Perfect > Game settings."));

        if (s.Upscale == 2 && !(config.Enabled && config.LockResolution && config.Mode != DlssMode.GameDefault))
            list.Add(new SettingCheck("Frame Rate Threshold", ThresholdName(s.Threshold), CheckLevel.Info,
                                      "The DLSS resolution isn't locked, so this decides when the game lowers it: Always Enabled = whenever the GPU " +
                                      "can't keep up; Below 60 or 30 fps = only below that."));

        if (s.RadialBlur && opt.IsManaged(GameOptimizer.RadialBlur))
            list.Add(new SettingCheck("Radial Blur", "on", CheckLevel.Info, "Frame Perfect turns it off as soon as System Configuration is closed."));
        else if (s.RadialBlur)
            list.Add(new SettingCheck("Radial Blur", "on", CheckLevel.Recommend,
                                      $"Recommended off: {EffectsPath} > Radial Blur. It blurs the image when the camera moves."));
        else
            list.Add(new SettingCheck("Radial Blur", "off", CheckLevel.Ok, "Correct."));

        list.AddRange(plugin.Pacing.Checks());
        return list;
    }

    public static string LevelTag(CheckLevel l) => l switch
    {
        CheckLevel.Ok => "[OK]",
        CheckLevel.Info => "[INFO]",
        CheckLevel.Recommend => "[RECOMMENDED]",
        CheckLevel.Change => "[CHANGE]",
        _ => string.Empty,
    };
}

/// <summary>
/// After login, one notification if something needs the player (a setting the plugin can't change itself). Everything
/// the plugin fixes on its own is reported by <see cref="GameOptimizer"/>; everything else is on the Overview page.
/// </summary>
internal sealed class GameSettingsNotifier
{
    private readonly Plugin plugin;
    private string shown = string.Empty;
    private DateTime loggedInAt = DateTime.MaxValue;

    public GameSettingsNotifier(Plugin plugin) => this.plugin = plugin;

    public void Tick()
    {
        if (!Plugin.ClientState.IsLoggedIn)
        {
            this.shown = string.Empty; // say it again after the next login
            this.loggedInAt = DateTime.MaxValue;
            return;
        }

        if (this.loggedInAt == DateTime.MaxValue)
            this.loggedInAt = DateTime.Now;
        var cfg = this.plugin.Config;
        if (!cfg.ChatNotices || !cfg.Enabled || (DateTime.Now - this.loggedInAt).TotalSeconds < 8)
            return; // give the automatic settings and the driver reads a few seconds first

        var todo = GameSettings.Evaluate(this.plugin).FindAll(c => c.Level == CheckLevel.Change);
        var key = string.Join(";", todo.ConvertAll(c => c.Option + c.Current));
        if (key == this.shown)
            return;
        this.shown = key;
        if (todo.Count == 0)
            return;

        var first = todo[0];
        this.plugin.Notify($"{first.Option} is {first.Current}. {first.Advice}" + (todo.Count > 1 ? $" (+{todo.Count - 1} more in /frameperfect)" : string.Empty),
                           Dalamud.Interface.ImGuiNotification.NotificationType.Warning);
        this.plugin.Log.Info($"notice: {string.Join(" | ", todo.ConvertAll(c => $"{c.Option}={c.Current}: {c.Advice}"))}");
    }
}
