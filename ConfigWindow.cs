using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace XivDlssFix;

internal sealed class ConfigWindow : Window
{
    private static readonly Vector4 Green = new(0.4f, 1, 0.4f, 1);
    private static readonly Vector4 Orange = new(1, 0.6f, 0.3f, 1);
    private static readonly Vector4 Amber = new(1, 0.75f, 0.3f, 1);
    private static readonly Vector4 Red = new(1, 0.45f, 0.35f, 1);
    private static readonly Vector4 Blue = new(0.7f, 0.8f, 1, 1);

    private static readonly (DlssMode Mode, string Label)[] Modes =
    {
        (DlssMode.Dlaa, "DLAA (100%)"),
        (DlssMode.Quality, "Quality (66.7%  -> 4K: 2560x1440)"),
        (DlssMode.Balanced, "Balanced (58%  -> 4K: 2227x1253)"),
        (DlssMode.Performance, "Performance (50%  -> 4K: 1920x1080)"),
        (DlssMode.UltraPerformance, "Ultra Performance (33%  -> 4K: 1280x720)"),
        (DlssMode.Custom, "Custom (%)"),
        (DlssMode.GameDefault, "Game default (dynamic, observe only)"),
    };

    private static readonly (PresetOverride Preset, string Label)[] Presets =
    {
        (PresetOverride.NvidiaDefault, "NVIDIA default for the loaded DLL (recommended)"),
        (PresetOverride.K, "K (transformer)"),
        (PresetOverride.J, "J (transformer)"),
        (PresetOverride.M, "M (2nd-gen transformer)"),
        (PresetOverride.L, "L (2nd-gen transformer)"),
        (PresetOverride.F, "F (CNN)"),
        (PresetOverride.Game, "Game's own (C, old CNN)"),
    };

    private readonly Plugin plugin;

    public ConfigWindow(Plugin plugin)
        : base("DLSS Fix###XivDlssFixConfig")
    {
        this.plugin = plugin;
        this.SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(560, 380), MaximumSize = new Vector2(1600, 1200) };
    }

    public override void Draw()
    {
        if (!ImGui.BeginTabBar("##dlssfix-tabs"))
            return;

        if (ImGui.BeginTabItem("Settings"))
        {
            this.DrawSettings();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Debug"))
        {
            this.DrawDebug();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private void DrawSettings()
    {
        var cfg = this.plugin.Config;
        var ctl = this.plugin.Controller;
        var changed = false;

        var enabled = cfg.Enabled;
        if (ImGui.Checkbox("Enabled", ref enabled))
        {
            cfg.Enabled = enabled;
            changed = true;
        }

        var modeLabel = Array.Find(Modes, m => m.Mode == cfg.Mode).Label ?? cfg.Mode.ToString();
        if (ImGui.BeginCombo("DLSS mode", modeLabel))
        {
            foreach (var (mode, label) in Modes)
            {
                if (ImGui.Selectable(label, mode == cfg.Mode))
                {
                    cfg.Mode = mode;
                    changed = true;
                }
            }

            ImGui.EndCombo();
        }

        if (cfg.Mode == DlssMode.Custom)
        {
            var pct = cfg.CustomScalePercent;
            if (ImGui.SliderFloat("Custom scale", ref pct, 33f, 100f, "%.1f%%"))
                cfg.CustomScalePercent = pct;
            if (ImGui.IsItemDeactivatedAfterEdit())
                changed = true;
        }

        var lockRes = cfg.LockResolution;
        if (ImGui.Checkbox("Lock resolution (no dynamic resolution)", ref lockRes))
        {
            cfg.LockResolution = lockRes;
            changed = true;
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Keeps the internal resolution at the mode's size.\nWithout it the game lowers the resolution whenever the camera moves and the GPU is busy.");

        var presetLabel = Array.Find(Presets, p => p.Preset == cfg.Preset).Label ?? cfg.Preset.ToString();
        if (ImGui.BeginCombo("Model preset", presetLabel))
        {
            foreach (var (preset, label) in Presets)
            {
                if (ImGui.Selectable(label, preset == cfg.Preset))
                {
                    cfg.Preset = preset;
                    changed = true;
                }
            }

            ImGui.EndCombo();
        }

        var sharpPct = cfg.Sharpness * 100f;
        if (ImGui.SliderFloat("Sharpness (CAS)", ref sharpPct, 0f, 100f, sharpPct < 0.5f ? "off" : "%.0f%%"))
            cfg.Sharpness = Math.Clamp(sharpPct / 100f, 0f, 1f); // live; read every frame on the render thread
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            cfg.Save();
            ctl.OnSettingsChanged(rebuild: false);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("AMD CAS on the DLSS output (after DLSS 5), before the UI.\nDLSS has ignored its own sharpness setting since version 2.5.1.\nChanges ease in over ~0.3 s so the image doesn't jump.");

        var chat = cfg.ChatNotices;
        if (ImGui.Checkbox("Game settings notices in chat", ref chat))
        {
            cfg.ChatNotices = chat;
            cfg.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("After login, and whenever they change, says in chat whether the game's graphics options are right for the plugin.");

        if (changed)
        {
            cfg.Save();
            ctl.OnSettingsChanged();
        }

        this.DrawDll();
        this.DrawPacing();

        ImGui.Separator();
        if (!ctl.HooksOk)
            ImGui.TextColored(Red, "Game functions not found (new game patch?). The plugin can't work. Details in the Debug tab.");

        this.DrawGameSettings();
        ImGui.Separator();

        var s = ctl.Status;
        if (s.RenderH != 0 && s.OutputH != 0)
        {
            ImGui.TextUnformatted($"Rendering {s.RenderW}x{s.RenderH} -> {s.OutputW}x{s.OutputH} ({100.0 * s.RenderH / s.OutputH:0.0}%)");
            ImGui.SameLine();
            if (s.Stable)
                ImGui.TextColored(Green, "stable");
            else
                ImGui.TextColored(Orange, "resolution changed in the last few seconds");
        }
        else
        {
            ImGui.TextDisabled("No DLSS frames yet (DLSS off, menu or loading screen).");
        }

        var rt = ctl.Runtime;
        if (rt.NeuralRendering.StartsWith("nvngx_dlssnr.dll ") && !rt.NeuralRendering.Contains("not loaded")
            && ImGui.CollapsingHeader("DLSS 5 flickers when the camera moves?"))
        {
            ImGui.TextColored(Blue,
                "That's G-Sync: DLSS 5 costs enough GPU time to pull the game below its frame cap, and the\n" +
                "monitor's brightness changes with the refresh rate. Fix it for this game only:\n" +
                "NVIDIA Control Panel > Manage 3D settings > Program Settings > ffxiv_dx11.exe >\n" +
                "Monitor Technology: Fixed Refresh. Other games keep G-Sync.");
        }
    }

    private static readonly (ReflexMode Mode, string Label)[] ReflexModes =
    {
        (ReflexMode.Off, "Off"),
        (ReflexMode.On, "On"),
        (ReflexMode.Boost, "On + Boost"),
    };

    private void DrawDll()
    {
        var u = this.plugin.Updater;
        var rt = this.plugin.Controller.Runtime;

        ImGui.Separator();
        var installed = u.InstalledVersion();
        ImGui.TextUnformatted($"DLSS DLL in the game folder: {(installed.Length > 0 ? installed : "none")}");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Loaded right now: {rt.Upscaler}");
        ImGui.SameLine();
        if (u.Busy)
        {
            ImGui.TextDisabled("working...");
        }
        else
        {
            if (ImGui.SmallButton("Check for update"))
                u.CheckAsync();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Asks github.com/NVIDIA/DLSS (NVIDIA's DLSS SDK) for its latest release.\nNothing is downloaded until you click install.");
            if (u.UpdateAvailable)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton($"Download and install {u.LatestVersion} ({u.LatestSize / 1048576.0:0} MB)"))
                    u.InstallAsync();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Downloads nvngx_dlss.dll from NVIDIA's DLSS repository, checks its hash against GitHub\n" +
                                     "and that it is signed by NVIDIA, keeps the current DLL as nvngx_dlss.dll.<version>.bak,\n" +
                                     "and puts the new one in the game folder. The game loads it on the next start.");
            }
        }

        if (u.State.Length > 0)
        {
            var color = u.State.StartsWith("Failed") ? Red : u.RestartNeeded ? Amber : u.UpdateAvailable ? Amber : Blue;
            ImGui.TextColored(color, u.State);
        }

        if (rt.Upscaler.Contains("override", StringComparison.OrdinalIgnoreCase))
            ImGui.TextColored(Blue, "The NVIDIA App's DLSS override is supplying the DLL, so the game-folder copy isn't used.");
    }

    private void DrawPacing()
    {
        var cfg = this.plugin.Config;
        var p = this.plugin.Pacing;

        ImGui.Separator();
        ImGui.TextUnformatted("Latency & frame pacing");

        var reflexLabel = Array.Find(ReflexModes, m => m.Mode == cfg.Reflex).Label ?? cfg.Reflex.ToString();
        if (ImGui.BeginCombo("NVIDIA Reflex", reflexLabel))
        {
            foreach (var (mode, label) in ReflexModes)
            {
                if (ImGui.Selectable(label, mode == cfg.Reflex))
                {
                    cfg.Reflex = mode;
                    cfg.Save();
                }
            }

            ImGui.EndCombo();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Lowers input latency: the driver keeps the GPU queue short and the plugin calls\nNVIDIA's sleep at the start of every frame. Boost also keeps GPU clocks up.");

        var cap = cfg.FrameCapFps;
        if (ImGui.SliderFloat("Frame cap (Reflex)", ref cap, 0f, 240f, cap < 20f ? "off" : "%.0f fps"))
            cfg.FrameCapFps = cap < 20f ? 0f : MathF.Round(cap);
        if (ImGui.IsItemDeactivatedAfterEdit())
            cfg.Save();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("A precise driver frame cap. The game's own limiter is V-Sync in whole refresh steps\n(16.7 / 33.3 ms), so set the game's frame rate limit to None when you use this.\nWith G-Sync: a few fps below the refresh rate, or a rate DLSS 5 can always hold.");

        if (p.GsyncCap > 0)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton($"G-Sync ({p.GsyncCap})"))
            {
                cfg.FrameCapFps = p.GsyncCap;
                cfg.Save();
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"NVIDIA's G-Sync cap for a {p.WindowHz} Hz monitor: keeps frames inside the G-Sync range.");
        }

        var fix = cfg.FixRefreshRate;
        if (ImGui.Checkbox("Use the refresh rate of the monitor the game is on", ref fix))
        {
            cfg.FixRefreshRate = fix;
            cfg.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("The game reads the refresh rate once at startup, from the primary monitor.\nIts frame limiter and dynamic resolution use that number, even when the window is on another monitor.");

        if (p.WindowHz > 0)
        {
            var reflex = cfg.Reflex == ReflexMode.Off && cfg.FrameCapFps <= 0 ? "Reflex off" : $"Reflex {p.ReflexState}";
            ImGui.TextDisabled($"Monitor: {p.WindowHz} Hz ({p.WindowMonitor}) | game uses {p.GameHzNow} Hz | {reflex}" +
                               (p.PresentStats.Length > 0 ? $" | {p.PresentStats}" : string.Empty));
        }
    }

    private void DrawDebug()
    {
        var cfg = this.plugin.Config;
        var ctl = this.plugin.Controller;

        if (cfg.MvScale != MotionVectorScale.Game)
            ImGui.TextColored(Orange, "Motion vector scale is in diagnostic mode (render). Use /dlssfix mv game.");
        if (!ctl.HooksOk)
            ImGui.TextColored(Red, "Hooks NOT found (game patch?). See the log.");

        var flash = cfg.FlashDetector;
        if (ImGui.Checkbox("Flash detector", ref flash))
        {
            cfg.FlashDetector = flash;
            cfg.Save();
            ctl.OnSettingsChanged(rebuild: false);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Measures every frame's brightness on the GPU and logs short light/dark blinks (FLASH lines),\nplus local flicker and frame pacing every 5 s. Cost: one tiny compute dispatch per frame.");

        if (ImGui.Button("Reapply now"))
            ctl.ApplyNow("button 'Reapply now'");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Makes the game rebuild its render targets and the DLSS feature (the screen blinks once).\nNormally not needed: the plugin reapplies on its own when something changes.");

        ImGui.SameLine();
        if (ImGui.Button("Reset DLSS history"))
            ctl.RequestHistoryReset("button 'Reset DLSS history'");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Sends a reset to DLSS on the next frame. DLSS 5 treats it as a scene cut and restarts its history.");

        ImGui.Separator();
        var s = ctl.Status;
        if (s.RenderH != 0 && s.OutputH != 0)
            ImGui.TextUnformatted($"DLSS: {s.RenderW}x{s.RenderH} -> {s.OutputW}x{s.OutputH} ({100.0 * s.RenderH / s.OutputH:0.0}%) | NGX mode: {s.NgxMode} | {(s.Stable ? "stable" : "VARYING")}");
        else
            ImGui.TextUnformatted("DLSS: no frames yet");
        ImGui.TextUnformatted($"RTM range: {s.RangeMinH}..{s.RangeMaxH} lines | {s.EvalsPerSecond:0} evaluate calls/s | mode byte: {s.ModeByte}");
        ImGui.TextUnformatted($"Sharpening: {ctl.SharpenState}");
        var probe = ctl.Probe;
        ImGui.TextUnformatted(cfg.FlashDetector
            ? $"Screen flashes: {probe.TotalFlashes} (last: {probe.LastFlash}) | local flicker: {probe.LastLocalFlicker:0.00}/1000 | {probe.State}"
            : "Flash detector off");

        ImGui.Separator();
        var rt = ctl.Runtime;
        ImGui.TextWrapped($"Upscaler DLL: {rt.Upscaler}");
        ImGui.TextWrapped($"DLSS 5 (NR) DLL: {rt.NeuralRendering}");
        ImGui.TextWrapped($"NR per RenoDX: runtime {rt.NrRuntimeVersion} | state {rt.NrState} | {rt.NrCostMs} ms GPU | flicker {rt.NrFlickerStops}");

        ImGui.Separator();
        var p = this.plugin.Pacing;
        if (!p.HookOk)
            ImGui.TextColored(Orange, "Present hook not found (game patch?): no present stats, Reflex without present markers.");
        ImGui.TextWrapped($"Monitors ({p.MonitorCount}): {p.MonitorList}");
        ImGui.TextWrapped($"Window on {p.WindowMonitor} {p.WindowHz} Hz | primary {p.PrimaryMonitor} {p.PrimaryHz} Hz | " +
                          $"game refresh value {p.GameHzNow} Hz (read at startup: {p.GameHzOriginal} Hz)");
        ImGui.TextWrapped($"Game limiter: {FramePacing.LimitModeName(p.LimitMode, p.LimitFps)} (Present sync interval {FramePacing.SyncInterval(p.LimitMode)})" +
                          (p.Fullscreen ? " | exclusive fullscreen flag set" : string.Empty));
        ImGui.TextWrapped($"Swap chain: {(p.SwapChainInfo.Length > 0 ? p.SwapChainInfo : "?")}");
        ImGui.TextWrapped($"Presents: {(p.PresentStats.Length > 0 ? p.PresentStats : "?")}");
        var ss = p.LastSleepStatus;
        ImGui.TextWrapped($"Reflex: {p.ReflexState}" +
                          (ss.HasValue ? $" | low latency {ss.Value.LowLatency}, exclusive-fullscreen G-Sync {ss.Value.FullscreenVrr}, control panel V-Sync {ss.Value.CplVsyncOn}, " +
                                         $"iFlip {ss.Value.FullscreenIFlip}, game sleep {ss.Value.UseGameSleep}" : string.Empty) +
                          (p.ReflexLatency.Length > 0 ? $" | latency {p.ReflexLatency}" : string.Empty));
        ImGui.TextWrapped($"G-Sync: {(p.GsyncSummary.Length > 0 ? p.GsyncSummary : "?")}");

        ImGui.Separator();
        ImGui.TextWrapped($"Log: {this.plugin.Log.FilePath}");
        if (ImGui.Button("Open log folder"))
        {
            var dir = Path.GetDirectoryName(this.plugin.Log.FilePath);
            if (dir != null)
                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
    }

    private void DrawGameSettings()
    {
        var checks = GameSettings.Evaluate(GameSettings.Read(), this.plugin.Config, this.plugin.Pacing);
        if (checks.Count == 0)
            return;

        if (checks.Exists(c => c.Level == CheckLevel.Change))
            ImGui.TextColored(Red, "Game settings: something needs to change (below)");
        else if (checks.Exists(c => c.Level == CheckLevel.Recommend))
            ImGui.TextColored(Amber, "Game settings: OK, with one recommendation");
        else
            ImGui.TextColored(Green, "Game settings: all good");

        foreach (var c in checks)
        {
            var color = c.Level switch
            {
                CheckLevel.Ok => Green,
                CheckLevel.Change => Red,
                CheckLevel.Recommend => Amber,
                _ => Blue,
            };
            ImGui.TextColored(color, GameSettings.LevelTag(c.Level));
            ImGui.SameLine();
            ImGui.TextWrapped($"{c.Option} = {c.Current}. {c.Advice}");
        }
    }
}
