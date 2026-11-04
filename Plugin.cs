using System;
using System.IO;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace FramePerfect;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/frameperfect";
    private const string LegacyCommand = "/dlssfix"; // the plugin's name before 2.0
    private const string OldInternalName = "XivDlssFix";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInterop { get; private set; } = null!;
    [PluginService] internal static ISigScanner SigScanner { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static ICommandManager Commands { get; private set; } = null!;
    [PluginService] internal static IChatGui Chat { get; private set; } = null!;
    [PluginService] internal static IPluginLog PluginLog { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IGameConfig GameConfig { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static INotificationManager Notifications { get; private set; } = null!;
    [PluginService] internal static IDtrBar DtrBar { get; private set; } = null!;

    private readonly WindowSystem windows = new("FramePerfect");
    private readonly MainWindow mainWindow;
    private readonly GameSettingsNotifier notifier;
    private IDtrBarEntry? dtr;
    private DateTime nextSecond = DateTime.Now.AddSeconds(1);
    private int ticks;
    private bool welcomeOpened;

    internal Configuration Config { get; }

    internal FileLog Log { get; }

    internal DlssController Controller { get; }

    internal FramePacing Pacing { get; }

    internal DllUpdater Updater { get; }

    internal TestRunner Test { get; }

    internal GameOptimizer Optimizer { get; }

    /// <summary>Frames the game rendered in the last second (framework ticks).</summary>
    internal int RenderedFps { get; private set; }

    /// <summary>Frames on screen per second: rendered, times Smooth Motion's frames.</summary>
    internal int ShownFps => this.RenderedFps * this.Pacing.FrameMultiplier;

    internal string Version { get; } = typeof(Plugin).Assembly.GetName().Version?.ToString(3) ?? "?";

    public Plugin()
    {
        // Two copies would hook the same game functions and both call Reflex: refuse to start next to the old one.
        foreach (var other in PluginInterface.InstalledPlugins)
        {
            if (other.InternalName == OldInternalName && other.IsLoaded)
            {
                const string msg = "DLSS Fix (the old version of Frame Perfect) is still loaded. Unload or remove it in /xlplugins, then load Frame Perfect.";
                Chat.PrintError($"[Frame Perfect] {msg}");
                throw new InvalidOperationException(msg);
            }
        }

        MigrateOldSettings();
        this.Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        this.Config.Version = 2;
        this.Log = new FileLog(PluginInterface.GetPluginConfigDirectory());
        this.Controller = new DlssController(this.Config, this.Log);
        this.Pacing = new FramePacing(this.Config, this.Log);
        this.Controller.Pacing = this.Pacing;
        this.Updater = new DllUpdater(System.IO.Path.GetDirectoryName(System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName)!, this.Log);
        this.notifier = new GameSettingsNotifier(this);
        this.Test = new TestRunner(this);
        this.Optimizer = new GameOptimizer(this);

        this.mainWindow = new MainWindow(this);
        this.windows.AddWindow(this.mainWindow);

        Commands.AddHandler(CommandName, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "Opens Frame Perfect. Also: status | mode <dlaa|quality|balanced|performance|ultra|custom|game> | sharp <0-100> | reflex <off|on|boost> | cap <fps|auto|off> | refreshlock <on|off> | gamesettings <on|off|restore> | bar <on|off> | dll [update] | on | off. Advanced: custom <33-100> | preset <default|j|k|l|m|f|game> | lock <on|off> | test [seconds|stop] | flash <on|off> | reset | apply",
        });
        Commands.AddHandler(LegacyCommand, new CommandInfo(this.OnCommand) { HelpMessage = "Old name of /frameperfect.", ShowInHelp = false });

        PluginInterface.UiBuilder.Draw += this.windows.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += this.ToggleWindow;
        PluginInterface.UiBuilder.OpenMainUi += this.ToggleWindow;
        Framework.Update += this.OnFrameworkUpdate;
    }

    internal void ToggleWindow() => this.mainWindow.Toggle();

    internal void OpenWindow(MainWindow.Page? page = null)
    {
        if (page != null)
            this.mainWindow.Current = page.Value;
        this.mainWindow.IsOpen = true;
        this.mainWindow.BringToFront();
    }

    /// <summary>A Dalamud notification (bottom right). Clicking it opens the window.</summary>
    internal void Notify(string text, NotificationType type)
    {
        if (!this.Config.ChatNotices)
            return;
        var n = Notifications.AddNotification(new Notification
        {
            Title = "Frame Perfect",
            Content = text,
            Type = type,
            InitialDuration = TimeSpan.FromSeconds(type == NotificationType.Warning ? 15 : 8),
        });
        n.Click += _ => this.OpenWindow();
    }

    /// <summary>The plugin was called DLSS Fix (XivDlssFix) before 2.0: carry its settings over once.</summary>
    private static void MigrateOldSettings()
    {
        try
        {
            var target = PluginInterface.ConfigFile;
            var old = Path.Combine(target.DirectoryName ?? string.Empty, OldInternalName + ".json");
            if (target.Exists || !File.Exists(old))
                return;
            var json = File.ReadAllText(old).Replace($"{OldInternalName}.Configuration, {OldInternalName}", "FramePerfect.Configuration, FramePerfect");
            File.WriteAllText(target.FullName, json);
        }
        catch (Exception e)
        {
            PluginLog.Warning($"Could not carry over the DLSS Fix settings: {e.Message}");
        }
    }

    /// <summary>Once a second: the frame rate in the server info bar.</summary>
    private void UpdateServerInfoBar()
    {
        if (!this.Config.ShowServerInfoBar || !this.Config.Enabled)
        {
            this.dtr?.Remove();
            this.dtr = null;
            return;
        }

        if (this.dtr == null)
        {
            this.dtr = DtrBar.Get("Frame Perfect");
            this.dtr.OnClick = _ => this.ToggleWindow();
        }

        var p = this.Pacing;
        var (shownCap, _) = p.CapNow();
        this.dtr.Text = $"{this.ShownFps} fps";
        this.dtr.Tooltip = $"Frame Perfect\n{this.ShownFps} frames a second on screen" +
                           (p.FrameMultiplier > 1 ? $" (the game renders {this.RenderedFps}, Smooth Motion adds the rest)" : string.Empty) +
                           (shownCap > 0 ? $"\nCap: {shownCap:0.##} fps{(p.CapIsAuto ? " (automatic)" : string.Empty)}" : "\nNo frame cap") +
                           $"\nDLSS: {this.Controller.ModeLabel}{(this.Config.LockResolution ? ", resolution locked" : string.Empty)}" +
                           $"\nReflex: {this.Config.Reflex}{(p.RefreshLockActive ? " | refresh lock on" : string.Empty)}" +
                           "\nClick to open";
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        try
        {
            this.Pacing.FrameStart(); // Reflex sleep point: before the game samples input this frame
            this.Pacing.Update();
            this.Test.Tick();
            this.Controller.Update();
            this.Optimizer.Tick();
            this.notifier.Tick();

            this.ticks++;
            if (DateTime.Now >= this.nextSecond)
            {
                this.nextSecond = DateTime.Now.AddSeconds(1);
                this.RenderedFps = this.ticks;
                this.ticks = 0;
                this.UpdateServerInfoBar();
                if (!this.welcomeOpened && !this.Config.WelcomeDismissed && ClientState.IsLoggedIn)
                {
                    this.welcomeOpened = true; // first start: show what the plugin does and what it set
                    this.OpenWindow(MainWindow.Page.Overview);
                }
            }
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
                var msg = $"[Frame Perfect] {this.Controller.DescribeSettings()} | DLSS {s.RenderW}x{s.RenderH} -> {s.OutputW}x{s.OutputH} " +
                          $"({(s.OutputH != 0 ? 100.0 * s.RenderH / s.OutputH : 0):0.0}%) {(s.Stable ? "stable" : "varying")} | NGX {s.NgxMode} | hooks {(this.Controller.HooksOk ? "ok" : "FAILED")}";
                Chat.Print(msg);
                var rt = this.Controller.Runtime;
                var dll = $"[Frame Perfect] upscaler: {rt.Upscaler} | NR: {rt.NeuralRendering} (runtime {rt.NrRuntimeVersion}, {rt.NrState}) | sharpness {this.Config.Sharpness * 100:0}%";
                Chat.Print(dll);
                var probe = this.Controller.Probe;
                var flash = $"[Frame Perfect] flashes detected: {probe.TotalFlashes} (last: {probe.LastFlash}) | detector {(this.Config.FlashDetector ? probe.State : "off")}";
                Chat.Print(flash);
                Chat.Print($"[Frame Perfect] monitor: {this.Pacing.WindowMonitor} {this.Pacing.WindowHz} Hz | game uses {this.Pacing.GameHzNow} Hz | " +
                           $"limiter: {FramePacing.LimitModeName(this.Pacing.LimitMode, this.Pacing.LimitFps)} | Reflex: {this.Config.Reflex} ({this.Pacing.ReflexState}), " +
                           $"cap {this.Pacing.CapDescription} | {this.Pacing.PresentStats}");
                foreach (var c in GameSettings.Evaluate(this))
                    if (c.Level != CheckLevel.Ok)
                        Chat.Print($"[Frame Perfect] {GameSettings.LevelTag(c.Level)} {c.Option} = {c.Current}: {c.Advice}");
                this.Log.Info($"/frameperfect status: {msg} || {dll} || {flash}");
                return;
            case "test":
                if (parts.Length > 1 && parts[1] == "stop")
                {
                    this.Test.Stop("stopped");
                    return;
                }

                var perStep = parts.Length > 1 && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sec) ? sec : 20;
                this.Test.Start(perStep, new[] { 60f, 100f, 116f, 60f, 100f, 116f });
                Chat.Print($"[Frame Perfect] G-Sync test: caps 60 / 100 / 116, twice, {Math.Clamp(perStep, 6, 120):0} s each, with the camera turning. " +
                           "Keep the game window focused and don't touch the mouse. /frameperfect test stop ends it.");
                return;
            case "reset":
                this.Controller.RequestHistoryReset("command /frameperfect reset");
                Chat.Print("[Frame Perfect] DLSS / DLSS 5 history will be reset on the next frame");
                return;
            case "flash" when parts.Length > 1:
                this.Config.FlashDetector = parts[1] is "on" or "1" or "true";
                this.Config.Save();
                this.Controller.OnSettingsChanged(rebuild: false);
                Chat.Print($"[Frame Perfect] flash detector: {(this.Config.FlashDetector ? "on" : "off")}");
                return;
            case "sharp" when parts.Length > 1 && float.TryParse(parts[1].TrimEnd('%'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sharp):
                this.Config.Sharpness = Math.Clamp(sharp / 100f, 0f, 1f);
                this.Config.Save();
                this.Controller.OnSettingsChanged(rebuild: false);
                Chat.Print($"[Frame Perfect] sharpness: {this.Config.Sharpness * 100:0}%");
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
                    Chat.PrintError("[Frame Perfect] use /frameperfect reflex off|on|boost");
                    return;
                }

                this.Config.Reflex = rm.Value;
                this.Config.Save();
                Chat.Print($"[Frame Perfect] Reflex: {rm.Value}");
                return;
            case "cap" when parts.Length > 1:
                if (parts[1] is "off" or "0")
                {
                    this.Config.FrameCapFps = 0;
                    this.Config.FrameCapAuto = false;
                }
                else if (parts[1] == "auto")
                {
                    this.Config.FrameCapAuto = true;
                }
                else if (parts[1] == "gsync" && this.Pacing.GsyncCap > 0)
                {
                    this.Config.FrameCapFps = this.Pacing.GsyncCap;
                    this.Config.FrameCapAuto = false;
                }
                else if (float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var capFps))
                {
                    this.Config.FrameCapFps = Math.Clamp(capFps, 20f, 1000f);
                    this.Config.FrameCapAuto = false;
                }
                else
                {
                    Chat.PrintError("[Frame Perfect] use /frameperfect cap <fps>|auto|off|gsync (fps = what you see; with Smooth Motion the game renders half)");
                    return;
                }

                this.Config.Save();
                Chat.Print($"[Frame Perfect] frame cap: {this.Pacing.CapDescription}");
                return;
            case "gamesettings" when parts.Length > 1:
                if (parts[1] == "restore")
                {
                    this.Optimizer.RestoreAll();
                    return;
                }

                this.Config.AutoGameSettings = parts[1] is "on" or "1" or "true";
                this.Config.Save();
                Chat.Print($"[Frame Perfect] automatic game settings: {(this.Config.AutoGameSettings ? "on" : "off (your own values are put back)")}");
                return;
            case "bar" when parts.Length > 1:
                this.Config.ShowServerInfoBar = parts[1] is "on" or "1" or "true";
                this.Config.Save();
                Chat.Print($"[Frame Perfect] server info bar entry: {(this.Config.ShowServerInfoBar ? "on" : "off")}");
                return;
            case "refreshlock" when parts.Length > 1:
                this.Config.RefreshLock = parts[1] is "on" or "1" or "true";
                this.Config.Save();
                Chat.Print($"[Frame Perfect] refresh lock (Fixed Refresh, borderless): {(this.Config.RefreshLock ? "on" : "off")}");
                return;
            case "dll":
                if (parts.Length > 1 && parts[1] == "update")
                {
                    this.Updater.InstallAsync();
                    Chat.Print("[Frame Perfect] checking and installing NVIDIA's latest nvngx_dlss.dll; progress in the Frame Perfect window (/frameperfect dll shows the result)");
                }
                else if (this.Updater.LatestVersion.Length == 0 && !this.Updater.Busy)
                {
                    this.Updater.CheckAsync();
                    Chat.Print($"[Frame Perfect] game folder has nvngx_dlss.dll {this.Updater.InstalledVersion()}; asking NVIDIA's GitHub for the latest (run /frameperfect dll again for the result)");
                }
                else
                {
                    Chat.Print($"[Frame Perfect] {(this.Updater.State.Length > 0 ? this.Updater.State : "working...")}" +
                               (this.Updater.UpdateAvailable ? " Use /frameperfect dll update to install it." : string.Empty));
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
                this.Controller.ApplyNow("command /frameperfect apply");
                Chat.Print("[Frame Perfect] reapplying (the screen blinks once)...");
                return;
            case "mv":
                this.Config.MvScale = parts.Length > 1
                    ? (parts[1] is "render" or "r" ? MotionVectorScale.RenderSize : MotionVectorScale.Game)
                    : (this.Config.MvScale == MotionVectorScale.Game ? MotionVectorScale.RenderSize : MotionVectorScale.Game);
                this.Config.Save();
                this.Controller.OnSettingsChanged(rebuild: false);
                Chat.Print($"[Frame Perfect] motion vector scale: {(this.Config.MvScale == MotionVectorScale.Game ? "GAME (display)" : "RENDER")}");
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
                    Chat.PrintError("[Frame Perfect] unknown mode");
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
                    Chat.PrintError("[Frame Perfect] unknown preset");
                    return;
                }

                this.Config.Preset = preset.Value;
                changed = true;
                break;
            default:
                Chat.PrintError("[Frame Perfect] unknown command. Use /frameperfect status | mode | sharp | reflex | cap | refreshlock | gamesettings | bar | dll | on | off (advanced: custom | preset | lock | test | flash | reset | apply)");
                return;
        }

        if (changed)
        {
            this.Config.Save();
            this.Controller.OnSettingsChanged();
            Chat.Print($"[Frame Perfect] {this.Controller.DescribeSettings()}");
        }
    }

    public void Dispose()
    {
        Framework.Update -= this.OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= this.windows.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= this.ToggleWindow;
        PluginInterface.UiBuilder.OpenMainUi -= this.ToggleWindow;
        Commands.RemoveHandler(CommandName);
        Commands.RemoveHandler(LegacyCommand);
        this.dtr?.Remove();
        this.windows.RemoveAllWindows();
        this.mainWindow.Dispose();
        this.Test.Stop("ended (plugin unloaded)");
        this.Pacing.Dispose();
        this.Controller.Dispose();
        this.Log.Dispose();
    }
}
