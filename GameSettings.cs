using System;
using System.Collections.Generic;
using GraphicsConfig = FFXIVClientStructs.FFXIV.Client.Graphics.Render.GraphicsConfig;

namespace XivDlssFix;

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

    public static List<SettingCheck> Evaluate(Snapshot s, Configuration config, FramePacing? pacing = null)
    {
        var list = new List<SettingCheck>();
        if (!s.Valid)
            return list;

        list.Add(s.Upscale == 2
            ? new SettingCheck("Graphics Upscaling", UpscaleName(s.Upscale), CheckLevel.Ok, "Correct. The plugin only works with DLSS.")
            : new SettingCheck("Graphics Upscaling", UpscaleName(s.Upscale), CheckLevel.Change,
                               $"Switch to NVIDIA DLSS: {ResolutionPath} > Graphics Upscaling."));

        if (s.Upscale == 2)
        {
            list.Add(config.Enabled && config.LockResolution && config.Mode != DlssMode.GameDefault
                ? new SettingCheck("Frame Rate Threshold", ThresholdName(s.Threshold), CheckLevel.Ok,
                                   "Any option works: with the lock the resolution never changes.")
                : new SettingCheck("Frame Rate Threshold", ThresholdName(s.Threshold), CheckLevel.Info,
                                   "Without the lock, this sets when the game lowers the resolution: Always Enabled = whenever the GPU can't " +
                                   "keep up with the frame cap / refresh rate; Below 60 or 30 fps = only below that."));

            list.Add(new SettingCheck("3D Resolution Scaling", $"{s.RezoScale * 100:0}", CheckLevel.Ok,
                                      "Ignored with DLSS: the plugin's mode sets the internal resolution."));
        }

        list.Add(s.RadialBlur
            ? new SettingCheck("Radial Blur", "on", CheckLevel.Recommend,
                               $"Recommended off: {EffectsPath} > Radial Blur. It blurs the image when the camera moves.")
            : new SettingCheck("Radial Blur", "off", CheckLevel.Ok, "Correct."));

        if (pacing != null)
            list.AddRange(pacing.Checks());

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

/// <summary>Prints the game-settings advice to chat once after login and whenever the relevant options change.</summary>
internal sealed class GameSettingsNotifier
{
    private readonly Plugin plugin;
    private string printed = string.Empty;
    private string pending = string.Empty;
    private DateTime pendingSince;

    public GameSettingsNotifier(Plugin plugin) => this.plugin = plugin;

    public void Tick()
    {
        var cfg = this.plugin.Config;
        if (!Plugin.ClientState.IsLoggedIn)
        {
            this.printed = this.pending = string.Empty; // say it again after the next login
            return;
        }

        if (!cfg.ChatNotices || !cfg.Enabled)
            return;

        var s = GameSettings.Read();
        if (!s.Valid)
            return;

        var pacing = this.plugin.Pacing;
        var pacingIssues = new List<SettingCheck>();
        foreach (var c in pacing.Checks())
            if (c.Level is CheckLevel.Change or CheckLevel.Recommend || (c.Option.StartsWith("Refresh") && c.Advice.StartsWith("Corrected")))
                pacingIssues.Add(c);
        var key = $"{s.Upscale}|{s.RadialBlur}|{cfg.Mode}|{cfg.LockResolution}|{string.Join(";", pacingIssues.ConvertAll(c => c.Option + c.Current + c.Level))}";
        if (key == this.printed)
            return;
        if (key != this.pending)
        {
            // wait until the option has stayed put for a moment (the player may still be in the menu)
            this.pending = key;
            this.pendingSince = DateTime.Now;
            return;
        }

        if ((DateTime.Now - this.pendingSince).TotalSeconds < 1.5)
            return;

        this.printed = key;
        if (s.Upscale != 2)
        {
            Plugin.Chat.PrintError($"[DLSS Fix] The game is set to {GameSettings.UpscaleName(s.Upscale)}, so the plugin has no effect. " +
                                   $"Change {GameSettings.ResolutionPath} > Graphics Upscaling to NVIDIA DLSS.");
        }
        else
        {
            var res = cfg.Mode == DlssMode.GameDefault ? "game's dynamic resolution (plugin only observing)"
                : cfg.LockResolution ? $"{this.plugin.Controller.ModeLabel} mode, resolution locked"
                : $"{this.plugin.Controller.ModeLabel} mode, dynamic resolution";
            Plugin.Chat.Print($"[DLSS Fix] Game settings OK: Graphics Upscaling = NVIDIA DLSS. {res}.");
        }

        if (s.RadialBlur)
            Plugin.Chat.Print($"[DLSS Fix] Recommended: turn off {GameSettings.EffectsPath} > Radial Blur (it blurs the image when the camera moves).");

        foreach (var c in pacingIssues)
            Plugin.Chat.Print($"[DLSS Fix] {GameSettings.LevelTag(c.Level)} {c.Option} = {c.Current}: {c.Advice}");

        this.plugin.Log.Info($"chat notice: upscaling={GameSettings.UpscaleName(s.Upscale)} threshold={GameSettings.ThresholdName(s.Threshold)} radialBlur={s.RadialBlur}");
    }
}
