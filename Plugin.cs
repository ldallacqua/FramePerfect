using System;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace XivDlssFix;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/dlssfix";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInterop { get; private set; } = null!;
    [PluginService] internal static ISigScanner SigScanner { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static ICommandManager Commands { get; private set; } = null!;
    [PluginService] internal static IChatGui Chat { get; private set; } = null!;
    [PluginService] internal static IPluginLog PluginLog { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IGameConfig GameConfig { get; private set; } = null!;

    private readonly WindowSystem windows = new("XivDlssFix");
    private readonly ConfigWindow configWindow;
    private readonly GameSettingsNotifier notifier;

    internal Configuration Config { get; }

    internal FileLog Log { get; }

    internal DlssController Controller { get; }

    internal FramePacing Pacing { get; }

    internal DllUpdater Updater { get; }

    internal TestRunner Test { get; }

    public Plugin()
    {
        this.Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        this.Log = new FileLog(PluginInterface.GetPluginConfigDirectory());
        this.Controller = new DlssController(this.Config, this.Log);
        this.Pacing = new FramePacing(this.Config, this.Log);
        this.Controller.Pacing = this.Pacing;
        this.Updater = new DllUpdater(System.IO.Path.GetDirectoryName(System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName)!, this.Log);
        this.notifier = new GameSettingsNotifier(this);
        this.Test = new TestRunner(this);

        this.configWindow = new ConfigWindow(this);
        this.windows.AddWindow(this.configWindow);

        Commands.AddHandler(CommandName, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "Opens the window. /dlssfix status | mode <dlaa|quality|balanced|performance|ultra|custom|game> | custom <33-100> | preset <default|j|k|l|m|f|game> | sharp <0-100> | lock <on|off> | reflex <off|on|boost> | cap <fps|off|gsync> | test [seconds|stop] | flash <on|off> | dll [update] | reset | on | off | apply",
        });

        PluginInterface.UiBuilder.Draw += this.windows.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += this.ToggleWindow;
        PluginInterface.UiBuilder.OpenMainUi += this.ToggleWindow;
        Framework.Update += this.OnFrameworkUpdate;
    }

    private void ToggleWindow() => this.configWindow.Toggle();

    private void OnFrameworkUpdate(IFramework framework)
    {
        try
        {
            this.Pacing.FrameStart(); // Reflex sleep point: before the game samples input this frame
            this.Pacing.Update();
            this.Test.Tick();
            this.Controller.Update();
            this.notifier.Tick();
        }
        catch (Exception e)
        {
            this.Log.Error($"update: {e}");
        }
    }

    private void OnCommand(string command, string args)
    {
        var parts = args.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            this.ToggleWindow();
            return;
        }

        var changed = false;
        switch (parts[0])
        {
            case "status":
                var s = this.Controller.Status;
                var msg = $"[DLSS Fix] {this.Controller.DescribeSettings()} | DLSS {s.RenderW}x{s.RenderH} -> {s.OutputW}x{s.OutputH} " +
                          $"({(s.OutputH != 0 ? 100.0 * s.RenderH / s.OutputH : 0):0.0}%) {(s.Stable ? "stable" : "varying")} | NGX {s.NgxMode} | hooks {(this.Controller.HooksOk ? "ok" : "FAILED")}";
                Chat.Print(msg);
                var rt = this.Controller.Runtime;
                var dll = $"[DLSS Fix] upscaler: {rt.Upscaler} | NR: {rt.NeuralRendering} (runtime {rt.NrRuntimeVersion}, {rt.NrState}) | sharpness {this.Config.Sharpness * 100:0}%";
                Chat.Print(dll);
                var probe = this.Controller.Probe;
                var flash = $"[DLSS Fix] flashes detected: {probe.TotalFlashes} (last: {probe.LastFlash}) | detector {(this.Config.FlashDetector ? probe.State : "off")}";
                Chat.Print(flash);
                Chat.Print($"[DLSS Fix] monitor: {this.Pacing.WindowMonitor} {this.Pacing.WindowHz} Hz | game uses {this.Pacing.GameHzNow} Hz | " +
                           $"limiter: {FramePacing.LimitModeName(this.Pacing.LimitMode, this.Pacing.LimitFps)} | Reflex: {this.Config.Reflex} ({this.Pacing.ReflexState}), " +
                           $"cap {(this.Config.FrameCapFps > 0 ? $"{this.Config.FrameCapFps:0.#} fps" : "off")} | {this.Pacing.PresentStats}");
                foreach (var c in GameSettings.Evaluate(GameSettings.Read(), this.Config, this.Pacing))
                    if (c.Level != CheckLevel.Ok)
                        Chat.Print($"[DLSS Fix] {GameSettings.LevelTag(c.Level)} {c.Option} = {c.Current}: {c.Advice}");
                this.Log.Info($"/dlssfix status: {msg} || {dll} || {flash}");
                return;
            case "test":
                if (parts.Length > 1 && parts[1] == "stop")
                {
                    this.Test.Stop("stopped");
                    return;
                }

                var perStep = parts.Length > 1 && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sec) ? sec : 20;
                this.Test.Start(perStep, new[] { 60f, 100f, 116f, 60f, 100f, 116f });
                Chat.Print($"[DLSS Fix] G-Sync test: caps 60 / 100 / 116, twice, {Math.Clamp(perStep, 6, 120):0} s each, with the camera turning. " +
                           "Keep the game window focused and don't touch the mouse. /dlssfix test stop ends it.");
                return;
            case "reset":
                this.Controller.RequestHistoryReset("command /dlssfix reset");
                Chat.Print("[DLSS Fix] DLSS / DLSS 5 history will be reset on the next frame");
                return;
            case "flash" when parts.Length > 1:
                this.Config.FlashDetector = parts[1] is "on" or "1" or "true";
                this.Config.Save();
                this.Controller.OnSettingsChanged(rebuild: false);
                Chat.Print($"[DLSS Fix] flash detector: {(this.Config.FlashDetector ? "on" : "off")}");
                return;
            case "sharp" when parts.Length > 1 && float.TryParse(parts[1].TrimEnd('%'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sharp):
                this.Config.Sharpness = Math.Clamp(sharp / 100f, 0f, 1f);
                this.Config.Save();
                this.Controller.OnSettingsChanged(rebuild: false);
                Chat.Print($"[DLSS Fix] sharpness: {this.Config.Sharpness * 100:0}%");
                return;
            case "reflex" when parts.Length > 1:
                ReflexMode? rm = parts[1] switch
                {
                    "off" or "0" => ReflexMode.Off,
                    "on" or "1" => ReflexMode.On,
                    "boost" or "2" => ReflexMode.Boost,
                    _ => null,
                };
                if (rm == null)
                {
                    Chat.PrintError("[DLSS Fix] use /dlssfix reflex off|on|boost");
                    return;
                }

                this.Config.Reflex = rm.Value;
                this.Config.Save();
                Chat.Print($"[DLSS Fix] Reflex: {rm.Value}");
                return;
            case "cap" when parts.Length > 1:
                if (parts[1] is "off" or "0")
                {
                    this.Config.FrameCapFps = 0;
                }
                else if (parts[1] == "gsync" && this.Pacing.GsyncCap > 0)
                {
                    this.Config.FrameCapFps = this.Pacing.GsyncCap;
                }
                else if (float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var capFps))
                {
                    this.Config.FrameCapFps = Math.Clamp(capFps, 20f, 1000f);
                }
                else
                {
                    Chat.PrintError("[DLSS Fix] use /dlssfix cap <fps>|off|gsync");
                    return;
                }

                this.Config.Save();
                Chat.Print($"[DLSS Fix] frame cap: {(this.Config.FrameCapFps > 0 ? $"{this.Config.FrameCapFps:0.#} fps" : "off")}");
                return;
            case "dll":
                if (parts.Length > 1 && parts[1] == "update")
                {
                    this.Updater.InstallAsync();
                    Chat.Print("[DLSS Fix] checking and installing NVIDIA's latest nvngx_dlss.dll; progress in the DLSS Fix window (/dlssfix dll shows the result)");
                }
                else if (this.Updater.LatestVersion.Length == 0 && !this.Updater.Busy)
                {
                    this.Updater.CheckAsync();
                    Chat.Print($"[DLSS Fix] game folder has nvngx_dlss.dll {this.Updater.InstalledVersion()}; asking NVIDIA's GitHub for the latest (run /dlssfix dll again for the result)");
                }
                else
                {
                    Chat.Print($"[DLSS Fix] {(this.Updater.State.Length > 0 ? this.Updater.State : "working...")}" +
                               (this.Updater.UpdateAvailable ? " Use /dlssfix dll update to install it." : string.Empty));
                }

                return;
            case "on":
                this.Config.Enabled = true;
                changed = true;
                break;
            case "off":
                this.Config.Enabled = false;
                changed = true;
                break;
            case "apply":
                this.Controller.ApplyNow("command /dlssfix apply");
                Chat.Print("[DLSS Fix] reapplying (the screen blinks once)...");
                return;
            case "mv":
                this.Config.MvScale = parts.Length > 1
                    ? (parts[1] is "render" or "r" ? MotionVectorScale.RenderSize : MotionVectorScale.Game)
                    : (this.Config.MvScale == MotionVectorScale.Game ? MotionVectorScale.RenderSize : MotionVectorScale.Game);
                this.Config.Save();
                this.Controller.OnSettingsChanged(rebuild: false);
                Chat.Print($"[DLSS Fix] motion vector scale: {(this.Config.MvScale == MotionVectorScale.Game ? "GAME (display)" : "RENDER")}");
                return;
            case "lock" when parts.Length > 1:
                this.Config.LockResolution = parts[1] is "on" or "1" or "true";
                changed = true;
                break;
            case "custom" when parts.Length > 1 && float.TryParse(parts[1].TrimEnd('%'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pct):
                this.Config.CustomScalePercent = Math.Clamp(pct, 33f, 100f);
                this.Config.Mode = DlssMode.Custom;
                changed = true;
                break;
            case "mode" when parts.Length > 1:
                DlssMode? mode = parts[1] switch
                {
                    "dlaa" => DlssMode.Dlaa,
                    "quality" => DlssMode.Quality,
                    "balanced" => DlssMode.Balanced,
                    "performance" => DlssMode.Performance,
                    "ultra" or "ultraperformance" => DlssMode.UltraPerformance,
                    "custom" => DlssMode.Custom,
                    "game" => DlssMode.GameDefault,
                    _ => null,
                };
                if (mode == null)
                {
                    Chat.PrintError("[DLSS Fix] unknown mode");
                    return;
                }

                this.Config.Mode = mode.Value;
                changed = true;
                break;
            case "preset" when parts.Length > 1:
                PresetOverride? preset = parts[1] switch
                {
                    "default" => PresetOverride.NvidiaDefault,
                    "j" => PresetOverride.J,
                    "k" => PresetOverride.K,
                    "l" => PresetOverride.L,
                    "m" => PresetOverride.M,
                    "f" => PresetOverride.F,
                    "game" or "c" => PresetOverride.Game,
                    _ => null,
                };
                if (preset == null)
                {
                    Chat.PrintError("[DLSS Fix] unknown preset");
                    return;
                }

                this.Config.Preset = preset.Value;
                changed = true;
                break;
            default:
                Chat.PrintError("[DLSS Fix] unknown command. Use /dlssfix status | mode | custom | preset | sharp | lock | reflex | cap | test | flash | dll | reset | on | off | apply");
                return;
        }

        if (changed)
        {
            this.Config.Save();
            this.Controller.OnSettingsChanged();
            Chat.Print($"[DLSS Fix] {this.Controller.DescribeSettings()}");
        }
    }

    public void Dispose()
    {
        Framework.Update -= this.OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= this.windows.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= this.ToggleWindow;
        PluginInterface.UiBuilder.OpenMainUi -= this.ToggleWindow;
        Commands.RemoveHandler(CommandName);
        this.windows.RemoveAllWindows();
        this.Test.Stop("ended (plugin unloaded)");
        this.Pacing.Dispose();
        this.Controller.Dispose();
        this.Log.Dispose();
    }
}
