using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace FramePerfect;

/// <summary>
/// The plugin window. A sidebar with pages, the most important first:
///   Overview       - is everything working, what the plugin does for you, what needs your attention (with one-click fixes)
///   Image quality  - DLSS resolution, sharpening, DLSS version
///   Smoothness     - Reflex, frame cap, refresh lock, Smooth Motion
///   Game settings  - the game options the plugin sets for you (and how to hand them back)
///   Help & fixes   - common problems in plain words (brightness flicker, stutter, latency, ...)
///   Diagnostics    - technical details for bug reports
/// Everything a player has to read is in plain words; numbers and driver names live under Diagnostics.
/// </summary>
internal sealed class MainWindow : Window, IDisposable
{
    public enum Page
    {
        Overview,
        Image,
        Smoothness,
        GameSettings,
        Help,
        Diagnostics,
    }

    private static readonly Vector4 Good = ImGuiColors.HealerGreen;
    private static readonly Vector4 Warn = ImGuiColors.DalamudYellow;
    private static readonly Vector4 Bad = ImGuiColors.DalamudRed;
    private static readonly Vector4 Info = new(0.55f, 0.75f, 1f, 1f);
    private static readonly Vector4 Accent = new(0.47f, 0.78f, 1f, 1f);
    private static readonly Vector4 Muted = ImGuiColors.DalamudGrey;
    private static readonly Vector4 White = ImGuiColors.DalamudWhite;

    private static readonly (Page Page, FontAwesomeIcon Icon, string Label)[] Pages =
    {
        (Page.Overview, FontAwesomeIcon.Home, "Overview"),
        (Page.Image, FontAwesomeIcon.Eye, "Image quality"),
        (Page.Smoothness, FontAwesomeIcon.TachometerAlt, "Smoothness"),
        (Page.GameSettings, FontAwesomeIcon.SlidersH, "Game settings"),
        (Page.Help, FontAwesomeIcon.LifeRing, "Help & fixes"),
        (Page.Diagnostics, FontAwesomeIcon.Stethoscope, "Diagnostics"),
    };

    private static readonly (DlssMode Mode, string Label, string Text)[] Modes =
    {
        (DlssMode.Dlaa, "DLAA", "Full resolution. The sharpest image and the heaviest on the GPU."),
        (DlssMode.Quality, "Quality", "67% resolution. Hard to tell from DLAA at 1440p and 4K."),
        (DlssMode.Balanced, "Balanced", "58% resolution. The best trade at 4K (recommended)."),
        (DlssMode.Performance, "Performance", "50% resolution. For weaker GPUs or very high frame rates."),
        (DlssMode.UltraPerformance, "Ultra Performance", "33% resolution. Only for 8K or very weak GPUs."),
        (DlssMode.Custom, "Custom", "Pick any resolution from 33% to 100%."),
        (DlssMode.GameDefault, "Let the game decide", "The game's dynamic resolution (it drops whenever the camera moves). Frame Perfect only watches."),
    };

    private static readonly (PresetOverride Preset, string Label)[] Presets =
    {
        (PresetOverride.NvidiaDefault, "NVIDIA's choice for the installed DLSS (recommended)"),
        (PresetOverride.K, "K - transformer"),
        (PresetOverride.J, "J - transformer"),
        (PresetOverride.M, "M - 2nd-gen transformer"),
        (PresetOverride.L, "L - 2nd-gen transformer"),
        (PresetOverride.F, "F - CNN"),
        (PresetOverride.Game, "The game's own (C, an old CNN model)"),
    };

    private readonly Plugin plugin;
    private readonly IFontHandle titleFont;
    private bool openFlickerHelp;

    public MainWindow(Plugin plugin)
        : base("Frame Perfect###FramePerfectMain")
    {
        this.plugin = plugin;
        this.Size = new Vector2(820, 600);
        this.SizeCondition = ImGuiCond.FirstUseEver;
        this.SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(660, 460), MaximumSize = new Vector2(2000, 1600) };
        this.titleFont = Plugin.PluginInterface.UiBuilder.FontAtlas.NewGameFontHandle(new GameFontStyle(GameFontFamilyAndSize.TrumpGothic34));
    }

    public Page Current { get; set; } = Page.Overview;

    private static float S => ImGuiHelpers.GlobalScale;

    private Configuration Cfg => this.plugin.Config;

    private FramePacing Pacing => this.plugin.Pacing;

    public void Dispose() => this.titleFont.Dispose();

    public override void Draw()
    {
        var checks = GameSettings.Evaluate(this.plugin);
        var attention = this.Attention(checks);

        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0, 0, 0, 0.18f));
        ImGui.BeginChild("##fp-nav", new Vector2(200 * S, 0), true);
        this.DrawNav(attention);
        ImGui.EndChild();
        ImGui.PopStyleColor();

        ImGui.SameLine();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(14 * S, 10 * S));
        ImGui.BeginChild("##fp-page", Vector2.Zero, false, ImGuiWindowFlags.AlwaysUseWindowPadding);
        switch (this.Current)
        {
            case Page.Overview: this.DrawOverview(checks, attention); break;
            case Page.Image: this.DrawImage(); break;
            case Page.Smoothness: this.DrawSmoothness(); break;
            case Page.GameSettings: this.DrawGameSettings(checks); break;
            case Page.Help: this.DrawHelp(); break;
            case Page.Diagnostics: this.DrawDiagnostics(); break;
        }

        ImGui.EndChild();
        ImGui.PopStyleVar();
    }

    // ------------------------------------------------------------------ sidebar

    private void DrawNav(List<Attn> attention)
    {
        ImGui.Dummy(new Vector2(0, 2 * S));
        using (this.titleFont.Push())
            ImGui.TextColored(Accent, "Frame Perfect");
        ImGui.TextColored(Muted, $"v{this.plugin.Version}");
        ImGui.Dummy(new Vector2(0, 6 * S));

        var rowH = ImGui.GetTextLineHeight() + (10 * S);
        foreach (var (page, icon, label) in Pages)
        {
            if (page == Page.Diagnostics)
            {
                ImGui.Dummy(new Vector2(0, 4 * S));
                ImGui.Separator();
                ImGui.Dummy(new Vector2(0, 4 * S));
            }

            var start = ImGui.GetCursorPos();
            if (ImGui.Selectable($"##nav-{page}", this.Current == page, ImGuiSelectableFlags.None, new Vector2(0, rowH)))
                this.Current = page;
            var after = ImGui.GetCursorPos();
            var rowMax = ImGui.GetItemRectMax();

            ImGui.SetCursorPos(start + new Vector2(8 * S, 5 * S));
            Icon(icon, this.Current == page ? Accent : Muted);
            ImGui.SameLine(start.X + (34 * S));
            ImGui.TextColored(this.Current == page ? White : ImGuiColors.DalamudWhite2, label);

            // a dot on Overview when something needs the player
            if (page == Page.Overview && attention.Count > 0)
            {
                var color = attention.Exists(a => a.Level == CheckLevel.Change) ? Bad : Warn;
                ImGui.GetWindowDrawList().AddCircleFilled(new Vector2(rowMax.X - (12 * S), rowMax.Y - (rowH / 2)), 4 * S, ImGui.GetColorU32(color));
            }

            ImGui.SetCursorPos(after);
        }

        // on/off at the bottom
        var bottom = ImGui.GetWindowHeight() - ImGui.GetFrameHeight() - (12 * S);
        if (ImGui.GetCursorPosY() < bottom)
            ImGui.SetCursorPosY(bottom);
        var on = this.Cfg.Enabled;
        if (ImGuiComponents.ToggleButton("##fp-enabled", ref on))
        {
            this.Cfg.Enabled = on;
            this.Cfg.Save();
            this.plugin.Controller.OnSettingsChanged();
        }

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(on ? Good : Muted, on ? "Running" : "Turned off");
    }

    // ------------------------------------------------------------------ overview

    private sealed record Attn(CheckLevel Level, string Title, string Text, string? Button, Action? Fix);

    /// <summary>The checks that need the player, each with a one-click fix where the plugin can do it.</summary>
    private List<Attn> Attention(List<SettingCheck> checks)
    {
        var list = new List<Attn>();
        var cfg = this.Cfg;
        if (!cfg.Enabled)
            return list;
        foreach (var c in checks)
        {
            var title = $"{c.Option}: {c.Current}";
            switch (c.Option)
            {
                case "Graphics Upscaling" when c.Level == CheckLevel.Change:
                    list.Add(new Attn(c.Level, title, c.Advice, GameOptimizer.NvidiaRtx == true ? "Switch it for me" : null,
                                      () => this.Manage(GameOptimizer.Upscaling)));
                    break;
                case "Radial Blur" when c.Level == CheckLevel.Recommend:
                    list.Add(new Attn(c.Level, title, c.Advice, "Turn it off for me", () => this.Manage(GameOptimizer.RadialBlur)));
                    break;
                case "Game frame rate limit" when c.Level == CheckLevel.Recommend:
                    list.Add(new Attn(c.Level, title, c.Advice, "Set it for me", () => this.Manage(GameOptimizer.FrameLimit)));
                    break;
                case "Refresh rate the game uses" when c.Level == CheckLevel.Change:
                    list.Add(new Attn(c.Level, title, c.Advice, "Fix it", () =>
                    {
                        cfg.FixRefreshRate = true;
                        cfg.Save();
                    }));
                    break;
                case "Frame cap" when c.Level == CheckLevel.Recommend:
                    list.Add(new Attn(c.Level, title, c.Advice, "Use the automatic cap", () =>
                    {
                        cfg.FrameCapAuto = true;
                        cfg.Save();
                    }));
                    break;
                case "G-Sync" when c.Level == CheckLevel.Recommend || c.Current.StartsWith("active in borderless"):
                    list.Add(new Attn(CheckLevel.Recommend, "Seeing the picture flicker in brightness?",
                                      "G-Sync is on for the game in borderless. On many screens that makes the brightness flicker, mostly while the " +
                                      "camera moves. One NVIDIA setting fixes it, and Frame Perfect adjusts its frame cap to match.",
                                      "Show me how", () =>
                                      {
                                          this.openFlickerHelp = true;
                                          this.Current = Page.Help;
                                      }));
                    break;
                case "Smooth Motion" when c.Level == CheckLevel.Info && c.Current.Contains("not running"):
                    list.Add(new Attn(CheckLevel.Info, "Smooth Motion is on, but not running yet", c.Advice, null, null));
                    break;
                default:
                    if (c.Level is CheckLevel.Change or CheckLevel.Recommend)
                        list.Add(new Attn(c.Level, title, c.Advice, null, null));
                    break;
            }
        }

        return list;
    }

    private void Manage(string option)
    {
        this.Cfg.AutoGameSettings = true;
        this.plugin.Optimizer.SetManaged(option, true);
    }

    private void DrawOverview(List<SettingCheck> checks, List<Attn> attention)
    {
        var cfg = this.Cfg;
        var ctl = this.plugin.Controller;
        var p = this.Pacing;

        if (!cfg.WelcomeDismissed)
        {
            this.Card(Accent, () =>
            {
                Icon(FontAwesomeIcon.HandSparkles, Accent);
                ImGui.SameLine();
                ImGui.TextColored(White, "Welcome to Frame Perfect");
                ImGui.TextWrapped("It's already working: DLSS stays at a fixed, sharp resolution, NVIDIA Reflex paces every frame, " +
                                  "and the game options that matter were set for you. You don't have to change anything.");
                ImGui.TextColored(Muted, "Each page on the left explains what it does. Your own game settings are saved and can be put back anytime.");
                ImGui.Dummy(new Vector2(0, 2 * S));
                if (ImGui.Button("Got it"))
                {
                    cfg.WelcomeDismissed = true;
                    cfg.Save();
                }
            });
        }

        // the headline
        var change = attention.Exists(a => a.Level == CheckLevel.Change);
        var (color, icon, head, sub) =
            !cfg.Enabled ? (Muted, FontAwesomeIcon.PowerOff, "Frame Perfect is turned off", "Nothing is changed while it's off. Turn it on with the switch at the bottom left.")
            : !ctl.HooksOk ? (Bad, FontAwesomeIcon.TimesCircle, "This game version isn't supported yet",
                              "A game update moved the code Frame Perfect works with. It stays out of the way until the plugin is updated.")
            : change ? (Bad, FontAwesomeIcon.ExclamationCircle, "One change needed", "Frame Perfect can't do its job until this is fixed (below).")
            : attention.Count > 0 ? (Warn, FontAwesomeIcon.ExclamationTriangle, $"Working, with {attention.Count} suggestion{(attention.Count > 1 ? "s" : string.Empty)}",
                                     "Everything runs. The suggestions below can make it better.")
            : (Good, FontAwesomeIcon.CheckCircle, "Everything is set up", "Nothing to do. Enjoy the game.");
        this.Card(color, () =>
        {
            using (this.titleFont.Push())
            {
                ImGui.PushFont(UiBuilder.IconFont);
                ImGui.TextColored(color, icon.ToIconString());
                ImGui.PopFont();
                ImGui.SameLine();
                ImGui.TextColored(White, head);
            }

            ImGui.TextColored(Muted, sub);
        });

        this.DrawTiles();

        // what the plugin does for the player right now
        Section("What Frame Perfect does for you");
        var s = ctl.Status;
        var (shown, rendered) = p.CapNow();
        var any = false;
        void Done(string text)
        {
            any = true;
            Icon(FontAwesomeIcon.Check, Good);
            ImGui.SameLine();
            ImGui.TextWrapped(text);
        }

        if (cfg.Enabled)
        {
            if (cfg.Mode != DlssMode.GameDefault && cfg.LockResolution && s.OutputH != 0)
                Done($"Keeps DLSS at {ctl.ModeLabel} ({s.RenderW}x{s.RenderH}, upscaled to {s.OutputW}x{s.OutputH}) instead of the game's dynamic " +
                     "resolution, so the picture doesn't turn blurry when the camera moves.");
            else if (cfg.Mode != DlssMode.GameDefault && cfg.LockResolution)
                Done($"Keeps DLSS at {ctl.ModeLabel} instead of the game's dynamic resolution (as soon as DLSS draws its first frame).");
            if (cfg.Sharpness > 0.005f)
                Done($"Sharpens the DLSS image ({cfg.Sharpness * 100:0}%) before the UI is drawn, so text and icons stay untouched.");
            if (shown > 0 && p.ReflexAvailable)
                Done(p.FrameMultiplier > 1
                         ? $"Caps the game at exactly {rendered:0.##} fps so NVIDIA Smooth Motion can turn it into {shown:0.##} on your screen."
                         : $"Caps the frame rate at exactly {shown:0.##} fps with NVIDIA Reflex: even frame times and less input lag than the game's limiter.");
            else if (cfg.Reflex != ReflexMode.Off && p.ReflexAvailable)
                Done("Lowers input lag with NVIDIA Reflex.");
            if (p.RefreshLockActive)
                Done($"Times every frame to your {p.RefreshHz:0.##} Hz screen, so none arrive too late and get dropped.");
            if (cfg.FixRefreshRate && p.GameHzOriginal != 0 && p.GameHzOriginal != p.WindowHz && !(p.GameHzOriginal == 60 && p.WindowHz == 59))
                Done($"Gave the game the real refresh rate of its monitor ({p.WindowHz} Hz; it had read {p.GameHzOriginal} Hz from your primary monitor).");
            var managed = 0;
            foreach (var item in GameOptimizer.Items)
                if (this.plugin.Optimizer.IsManaged(item.Name) && this.plugin.Optimizer.Wanted(item.Name) != null)
                    managed++;
            if (managed > 0)
                Done($"Keeps {managed} game option{(managed > 1 ? "s" : string.Empty)} at the best value for you (Game settings).");
        }

        if (!any)
            ImGui.TextColored(Muted, "Nothing right now.");

        if (attention.Count > 0)
        {
            Section("Needs your attention");
            var i = 0;
            foreach (var a in attention)
            {
                var c = a.Level == CheckLevel.Change ? Bad : a.Level == CheckLevel.Recommend ? Warn : Info;
                ImGui.PushID(i++);
                this.Card(c, () =>
                {
                    Icon(a.Level == CheckLevel.Change ? FontAwesomeIcon.ExclamationCircle : a.Level == CheckLevel.Recommend ? FontAwesomeIcon.Lightbulb : FontAwesomeIcon.InfoCircle, c);
                    ImGui.SameLine();
                    ImGui.TextColored(White, a.Title);
                    ImGui.TextWrapped(a.Text);
                    if (a.Button != null && a.Fix != null)
                    {
                        ImGui.Dummy(new Vector2(0, 2 * S));
                        if (ImGui.Button(a.Button))
                            a.Fix();
                    }
                });
                ImGui.PopID();
            }
        }
    }

    /// <summary>Three tiles: image, frame rate, latency.</summary>
    private void DrawTiles()
    {
        var cfg = this.Cfg;
        var p = this.Pacing;
        var s = this.plugin.Controller.Status;
        var (shown, rendered) = p.CapNow();

        var image = cfg.Mode == DlssMode.GameDefault ? "Dynamic" : $"DLSS {this.plugin.Controller.ModeLabel}";
        var imageLine1 = s.OutputH != 0 ? $"{s.RenderW}x{s.RenderH} -> {s.OutputW}x{s.OutputH}" : "Waiting for the first DLSS frame";
        var imageLine2 = (cfg.LockResolution && cfg.Mode != DlssMode.GameDefault ? "Locked" : "Not locked") +
                         (cfg.Sharpness > 0.005f ? $" | sharpening {cfg.Sharpness * 100:0}%" : string.Empty);

        var fps = $"{this.plugin.ShownFps} fps";
        var fpsLine1 = shown > 0 ? $"Cap {shown:0.##}{(p.CapIsAuto ? " (automatic)" : string.Empty)}" : "No cap";
        var fpsLine2 = p.FrameMultiplier > 1 ? $"Smooth Motion: renders {this.plugin.RenderedFps}"
            : p.VrrForGame == true ? "G-Sync"
            : p.RefreshHz > 0 ? $"{p.RefreshHz:0.##} Hz{(p.RefreshLockActive ? " | in step" : string.Empty)}" : string.Empty;

        var latency = cfg.Reflex switch { ReflexMode.Off => "Reflex off", ReflexMode.Boost => "Reflex + Boost", _ => "Reflex on" };
        var latLine1 = !p.ReflexAvailable ? "Not available (NVIDIA only)" : p.ReflexState == "active" ? "Low-latency mode active" : p.ReflexState;
        var latLine2 = p.FrameMultiplier > 1 ? "Smooth Motion adds ~1 frame" : string.Empty;

        var gap = 8 * S;
        var width = (ImGui.GetContentRegionAvail().X - (2 * gap)) / 3;
        var pad = 10 * S;
        var line = ImGui.GetTextLineHeightWithSpacing();
        float valueH;
        using (this.titleFont.Push())
            valueH = ImGui.GetTextLineHeight();
        var height = (2 * pad) + (3 * line) + valueH;
        var start = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();

        void Tile(int i, FontAwesomeIcon icon, string label, string value, string l1, string l2, Vector4 tint)
        {
            var min = start + new Vector2(i * (width + gap), 0);
            var max = min + new Vector2(width, height);
            dl.AddRectFilled(min, max, ImGui.GetColorU32(new Vector4(tint.X, tint.Y, tint.Z, 0.08f)), 6 * S);
            dl.AddRect(min, max, ImGui.GetColorU32(new Vector4(tint.X, tint.Y, tint.Z, 0.25f)), 6 * S);
            ImGui.PushClipRect(min, max, true);
            ImGui.SetCursorScreenPos(min + new Vector2(pad, pad));
            ImGui.BeginGroup();
            Icon(icon, tint);
            ImGui.SameLine();
            ImGui.TextColored(Muted, label);
            using (this.titleFont.Push())
                ImGui.TextColored(White, value);
            ImGui.TextColored(ImGuiColors.DalamudWhite2, l1);
            ImGui.TextColored(Muted, l2);
            ImGui.EndGroup();
            ImGui.PopClipRect();
        }

        Tile(0, FontAwesomeIcon.Eye, "IMAGE", image, imageLine1, imageLine2, Accent);
        Tile(1, FontAwesomeIcon.TachometerAlt, "ON SCREEN", fps, fpsLine1, fpsLine2, Good);
        Tile(2, FontAwesomeIcon.Bolt, "LATENCY", latency, latLine1, latLine2, Warn);
        ImGui.SetCursorScreenPos(start + new Vector2(0, height));
        ImGui.Dummy(new Vector2(ImGui.GetContentRegionAvail().X, 4 * S));
    }

    // ------------------------------------------------------------------ image quality

    private void DrawImage()
    {
        var cfg = this.Cfg;
        var ctl = this.plugin.Controller;
        this.Header(FontAwesomeIcon.Eye, "Image quality",
                    "DLSS renders the game at a lower resolution and rebuilds a sharp picture from it. FFXIV keeps changing that " +
                    "resolution while you play (it drops when the camera moves); Frame Perfect holds it where you set it.");

        Section("DLSS resolution");
        var s = ctl.Status;
        var changed = false;
        foreach (var (mode, label, text) in Modes)
        {
            if (ImGui.RadioButton($"{label}##mode", cfg.Mode == mode))
            {
                cfg.Mode = mode;
                changed = true;
            }

            ImGui.SameLine(190 * S);
            var size = s.OutputH != 0 ? RenderSize(mode, cfg.CustomScalePercent, s.OutputW, s.OutputH) : string.Empty;
            ImGui.TextColored(cfg.Mode == mode ? ImGuiColors.DalamudWhite2 : Muted, text + (size.Length > 0 && mode != DlssMode.GameDefault ? $"  ({size})" : string.Empty));
        }

        if (cfg.Mode == DlssMode.Custom)
        {
            ImGui.Indent(24 * S);
            ImGui.SetNextItemWidth(260 * S);
            var pct = cfg.CustomScalePercent;
            if (ImGui.SliderFloat("##custom", ref pct, 33f, 100f, "%.1f%% resolution"))
                cfg.CustomScalePercent = pct;
            if (ImGui.IsItemDeactivatedAfterEdit())
                changed = true;
            ImGui.Unindent(24 * S);
        }

        ImGui.Dummy(new Vector2(0, 4 * S));
        var lockRes = cfg.LockResolution;
        if (ImGui.Checkbox("Keep this resolution fixed", ref lockRes))
        {
            cfg.LockResolution = lockRes;
            changed = true;
        }

        ImGui.SameLine();
        ImGuiComponents.HelpMarker("Without it, the game lowers the resolution whenever the camera moves and the GPU is busy, " +
                                   "which makes the picture blurry exactly when you look around.");

        if (s.OutputH != 0)
            ImGui.TextColored(s.Stable ? Good : Warn,
                              $"Now: {s.RenderW}x{s.RenderH} upscaled to {s.OutputW}x{s.OutputH} ({100.0 * s.RenderH / s.OutputH:0.#}%)" +
                              (s.Stable ? ", steady" : ", changed a moment ago"));
        else
            ImGui.TextColored(Muted, "No DLSS frames yet (DLSS is off, or you're in a menu or loading screen).");

        if (changed)
        {
            cfg.Save();
            ctl.OnSettingsChanged();
        }

        Section("Sharpening");
        ImGui.SetNextItemWidth(260 * S);
        var sharpPct = cfg.Sharpness * 100f;
        if (ImGui.SliderFloat("##sharp", ref sharpPct, 0f, 100f, sharpPct < 0.5f ? "Off" : "%.0f%%"))
            cfg.Sharpness = Math.Clamp(sharpPct / 100f, 0f, 1f); // read every frame on the render thread
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            cfg.Save();
            ctl.OnSettingsChanged(rebuild: false);
        }

        ImGui.SameLine();
        ImGuiComponents.HelpMarker("AMD's contrast-adaptive sharpening on the DLSS image, before the UI is drawn (text and icons stay as they are). " +
                                   "DLSS stopped sharpening on its own in version 2.5.1. 30-50% suits most people.");

        Section("DLSS version");
        this.DrawDll();

        ImGui.Dummy(new Vector2(0, 4 * S));
        if (ImGui.CollapsingHeader("Advanced: DLSS model"))
        {
            ImGui.SetNextItemWidth(360 * S);
            var presetLabel = Array.Find(Presets, x => x.Preset == cfg.Preset).Label ?? cfg.Preset.ToString();
            if (ImGui.BeginCombo("##preset", presetLabel))
            {
                foreach (var (preset, label) in Presets)
                {
                    if (ImGui.Selectable(label, preset == cfg.Preset))
                    {
                        cfg.Preset = preset;
                        cfg.Save();
                        ctl.OnSettingsChanged();
                    }
                }

                ImGui.EndCombo();
            }

            ImGui.SameLine();
            ImGuiComponents.HelpMarker("Which neural network DLSS uses. NVIDIA picks the best one for the installed DLSS version and mode; " +
                                       "the game itself asks for an old one (C). Changing it makes the screen blink once.");
        }
    }

    private static string RenderSize(DlssMode mode, float customPct, uint outW, uint outH)
    {
        double r = mode switch
        {
            DlssMode.Dlaa => 1.0,
            DlssMode.Quality => 2.0 / 3.0,
            DlssMode.Balanced => 0.58,
            DlssMode.Performance => 0.5,
            DlssMode.UltraPerformance => 1.0 / 3.0,
            DlssMode.Custom => Math.Clamp(customPct, 33f, 100f) / 100.0,
            _ => 0,
        };
        return r > 0 ? $"{Math.Round(outW * r)}x{Math.Round(outH * r)}" : string.Empty;
    }

    private void DrawDll()
    {
        var u = this.plugin.Updater;
        var rt = this.plugin.Controller.Runtime;
        var installed = u.InstalledVersion();
        ImGui.TextUnformatted(installed.Length > 0 ? $"DLSS {installed} is in the game folder." : "No DLSS file in the game folder.");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Loaded right now: {rt.Upscaler}");
        if (u.Busy)
        {
            ImGui.TextColored(Muted, "Working...");
        }
        else
        {
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Sync, "Check for a newer DLSS"))
                u.CheckAsync();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Asks NVIDIA's official DLSS page on GitHub (github.com/NVIDIA/DLSS) for its latest release.\nNothing is downloaded until you click install.");
            if (u.UpdateAvailable)
            {
                ImGui.SameLine();
                if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Download, $"Install DLSS {u.LatestVersion} ({u.LatestSize / 1048576.0:0} MB)"))
                    u.InstallAsync();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Downloads nvngx_dlss.dll from NVIDIA, checks it against GitHub's hash and NVIDIA's signature,\n" +
                                     "keeps your current file as a backup (nvngx_dlss.dll.<version>.bak) and puts the new one in the game folder.\n" +
                                     "The game loads it the next time it starts.");
            }
        }

        if (u.State.Length > 0)
            ImGui.TextColored(u.State.StartsWith("Failed") ? Bad : u.RestartNeeded || u.UpdateAvailable ? Warn : Info, u.State);
        if (rt.Upscaler.Contains("override", StringComparison.OrdinalIgnoreCase))
            ImGui.TextColored(Info, "The NVIDIA app's DLSS override supplies DLSS right now, so the file in the game folder isn't used.");
    }

    // ------------------------------------------------------------------ smoothness

    private void DrawSmoothness()
    {
        var cfg = this.Cfg;
        var p = this.Pacing;
        this.Header(FontAwesomeIcon.TachometerAlt, "Smoothness",
                    "How evenly frames reach your screen and how quickly it reacts to you. Even frame times feel smoother than a " +
                    "higher, uneven frame rate.");

        var (shown, rendered) = p.CapNow();
        this.Card(Good, () =>
        {
            ImGui.TextColored(White, $"{this.plugin.ShownFps} frames a second on your screen");
            if (p.FrameMultiplier > 1)
                ImGui.TextWrapped($"The game renders {this.plugin.RenderedFps}; NVIDIA Smooth Motion generates one frame between every two.");
            ImGui.TextColored(Muted, shown > 0 ? $"Cap: {p.CapDescription}" : "No frame cap.");
            if (p.WindowHz > 0)
                ImGui.TextColored(Muted, $"Screen: {p.RefreshHz:0.##} Hz on {p.WindowMonitor}, " +
                                         (p.VrrForGame == true ? "G-Sync on" : p.VrrForGame == false ? "fixed refresh rate" : "G-Sync state unknown"));
        });

        Section("NVIDIA Reflex");
        foreach (var (mode, label) in new[] { (ReflexMode.Off, "Off"), (ReflexMode.On, "On (recommended)"), (ReflexMode.Boost, "On + Boost") })
        {
            if (ImGui.RadioButton($"{label}##reflex", cfg.Reflex == mode))
            {
                cfg.Reflex = mode;
                cfg.Save();
            }

            ImGui.SameLine();
        }

        ImGuiComponents.HelpMarker("Lowers input lag: the GPU doesn't queue up frames, and each frame starts as late as possible, " +
                                   "right before it's needed. Boost also keeps the GPU clocks up. Needs an NVIDIA card.");
        if (!p.ReflexAvailable && cfg.Reflex != ReflexMode.Off)
            ImGui.TextColored(Warn, $"Reflex isn't available: {p.ReflexState}");

        Section("Frame rate cap");
        var capMode = cfg.FrameCapAuto ? 0 : cfg.FrameCapFps > 0 ? 1 : 2;
        if (ImGui.RadioButton("Automatic (recommended)##cap", capMode == 0))
        {
            cfg.FrameCapAuto = true;
            cfg.Save();
        }

        ImGui.SameLine();
        if (ImGui.RadioButton("Custom##cap", capMode == 1))
        {
            cfg.FrameCapAuto = false;
            if (cfg.FrameCapFps <= 0)
                cfg.FrameCapFps = shown > 0 ? MathF.Round(shown) : 60;
            cfg.Save();
        }

        ImGui.SameLine();
        if (ImGui.RadioButton("Off##cap", capMode == 2))
        {
            cfg.FrameCapAuto = false;
            cfg.FrameCapFps = 0;
            cfg.Save();
        }

        ImGui.SameLine();
        ImGuiComponents.HelpMarker("The frame rate you see. Automatic picks it from your screen: with G-Sync a few fps under the refresh rate, " +
                                   "with a fixed refresh rate exactly the refresh rate, and with Smooth Motion the game renders half of it.\n\n" +
                                   "Frame Perfect's cap is far more precise than the game's own limit, which can only wait whole screen refreshes.");
        if (capMode == 1)
        {
            ImGui.SetNextItemWidth(260 * S);
            var cap = cfg.FrameCapFps;
            if (ImGui.SliderFloat("##capfps", ref cap, 20f, 240f, "%.0f fps on screen"))
                cfg.FrameCapFps = MathF.Round(cap);
            if (ImGui.IsItemDeactivatedAfterEdit())
                cfg.Save();
        }

        ImGui.TextColored(Muted, shown > 0 ? p.CapDescription : "No cap: the game runs as fast as it can.");

        Section("Frame timing");
        var rlock = cfg.RefreshLock;
        if (ImGui.Checkbox("Keep frames in step with the screen", ref rlock))
        {
            cfg.RefreshLock = rlock;
            cfg.Save();
        }

        ImGui.SameLine();
        ImGuiComponents.HelpMarker("Without G-Sync, Windows picks the frame for each screen refresh about 1 ms before it. A frame that arrives " +
                                   "in that last millisecond waits a whole refresh and the next one replaces it: a dropped frame, felt as a stutter.\n\n" +
                                   "This starts every frame at the same point of the refresh, so frames always arrive in time. It works with a fixed " +
                                   "refresh rate in borderless, with the automatic cap. Measured: 0 dropped frames, against 343 in 20 s without it.");
        ImGui.TextColored(p.RefreshLockActive ? Good : Muted, p.RefreshLockActive ? $"Active: every frame is timed to the {p.RefreshHz:0.##} Hz refresh." : $"Not active: {p.RefreshLockState}.");

        ImGui.Dummy(new Vector2(0, 2 * S));
        var fix = cfg.FixRefreshRate;
        if (ImGui.Checkbox("Use the refresh rate of the monitor the game is on", ref fix))
        {
            cfg.FixRefreshRate = fix;
            cfg.Save();
        }

        ImGui.SameLine();
        ImGuiComponents.HelpMarker("The game reads the refresh rate once, at startup, from your primary monitor, even when its window is on " +
                                   "another one. Its frame limit and dynamic resolution use that number.");
        if (p.WindowHz > 0)
            ImGui.TextColored(Muted, $"The game uses {p.GameHzNow} Hz; its monitor runs at {p.WindowHz} Hz.");

        Section("NVIDIA Smooth Motion");
        var profileOn = p.GsyncProfile?.SmoothMotionOn == true;
        if (p.FrameMultiplier > 1)
        {
            Icon(FontAwesomeIcon.CheckCircle, Good);
            ImGui.SameLine();
            ImGui.TextWrapped($"Running. The game renders {rendered:0.##} fps and you see {shown:0.##}. Frame Perfect halves the cap for you.");
        }
        else if (profileOn)
        {
            Icon(FontAwesomeIcon.InfoCircle, Info);
            ImGui.SameLine();
            ImGui.TextWrapped("Switched on in the NVIDIA app, but not running yet: restart the game (the driver loads it at startup).");
        }
        else
        {
            Icon(FontAwesomeIcon.InfoCircle, Muted);
            ImGui.SameLine();
            ImGui.TextWrapped("Off. Smooth Motion is NVIDIA's frame generation for any game: it doubles the frames you see (60 rendered, 120 shown).");
        }

        ImGui.TextColored(Muted, "Turn it on: NVIDIA app > Graphics > Program settings > Final Fantasy XIV > Smooth Motion, then restart the game. " +
                                 "Needs an RTX 40 or 50 card. It adds about one frame of input lag, and nameplates can smear a little in fast turns.");
    }

    // ------------------------------------------------------------------ game settings

    private void DrawGameSettings(List<SettingCheck> checks)
    {
        var cfg = this.Cfg;
        var opt = this.plugin.Optimizer;
        this.Header(FontAwesomeIcon.SlidersH, "Game settings",
                    "Frame Perfect sets the game options it depends on, so you don't have to dig through System Configuration. " +
                    "Your own values are saved first and come back when you untick an option or press Restore.");

        var auto = cfg.AutoGameSettings;
        if (ImGui.Checkbox("Set the best game options for me", ref auto))
        {
            cfg.AutoGameSettings = auto;
            cfg.Save();
        }

        if (opt.WaitingForMenu)
            ImGui.TextColored(Warn, "System Configuration is open: changes wait until you close it.");

        ImGui.Dummy(new Vector2(0, 4 * S));
        if (ImGui.BeginTable("##fp-game", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.PadOuterX))
        {
            ImGui.TableSetupColumn("Option", ImGuiTableColumnFlags.WidthStretch, 1.4f);
            ImGui.TableSetupColumn("Best", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("Now", ImGuiTableColumnFlags.WidthStretch, 1.2f);
            ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthStretch, 1.6f);
            ImGui.TableHeadersRow();
            foreach (var item in GameOptimizer.Items)
            {
                ImGui.PushID(item.Name);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.BeginDisabled(!cfg.AutoGameSettings);
                var managed = cfg.AutoGameSettings && opt.IsManaged(item.Name);
                var m = cfg.AutoGameSettings ? opt.IsManaged(item.Name) : false;
                if (ImGui.Checkbox($"{item.Label}##m", ref m))
                    opt.SetManaged(item.Name, m);
                ImGui.EndDisabled();
                ImGui.SameLine();
                ImGuiComponents.HelpMarker($"{item.Why}\n\nIn the game: {item.Path}.");

                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(item.BestText);

                var now = GameOptimizer.Read(item.Name);
                var want = opt.Wanted(item.Name);
                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(want != null && now == want ? Good : ImGuiColors.DalamudWhite2, GameOptimizer.ValueName(item.Name, now));

                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                if (!managed)
                    ImGui.TextColored(Muted, "Your choice");
                else if (want == null)
                    ImGui.TextColored(Muted, $"Left alone: {opt.WhyNot(item.Name)}");
                else if (now == want)
                    ImGui.TextColored(Good, "Set by Frame Perfect");
                else if (opt.WaitingForMenu)
                    ImGui.TextColored(Warn, "After System Configuration closes");
                else
                    ImGui.TextColored(Warn, "Setting...");
                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        ImGui.Dummy(new Vector2(0, 4 * S));
        ImGui.BeginDisabled(cfg.SavedGameSettings.Count == 0 && !cfg.AutoGameSettings);
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.UndoAlt, "Restore my original settings"))
            opt.RestoreAll();
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Puts back the values you had before Frame Perfect changed them, and stops setting them.\nDo this before you uninstall the plugin.");

        // everything else the plugin checks
        Section("Other checks");
        var shown = 0;
        foreach (var c in checks)
        {
            if (c.Option is "Graphics Upscaling" or "Radial Blur" || (c.Option == "Game frame rate limit" && c.Level == CheckLevel.Ok))
                continue;
            shown++;
            var (icon, color) = c.Level switch
            {
                CheckLevel.Ok => (FontAwesomeIcon.CheckCircle, Good),
                CheckLevel.Change => (FontAwesomeIcon.ExclamationCircle, Bad),
                CheckLevel.Recommend => (FontAwesomeIcon.ExclamationTriangle, Warn),
                _ => (FontAwesomeIcon.InfoCircle, Info),
            };
            Icon(icon, color);
            ImGui.SameLine();
            ImGui.BeginGroup();
            ImGui.TextColored(White, $"{c.Option}: {c.Current}");
            ImGui.PushStyleColor(ImGuiCol.Text, Muted);
            ImGui.TextWrapped(c.Advice);
            ImGui.PopStyleColor();
            ImGui.EndGroup();
            ImGui.Dummy(new Vector2(0, 2 * S));
        }

        if (shown == 0)
            ImGui.TextColored(Muted, "Nothing else to check yet (log in first).");

        Section("Frame Perfect");
        var notes = cfg.ChatNotices;
        if (ImGui.Checkbox("Show notifications", ref notes))
        {
            cfg.ChatNotices = notes;
            cfg.Save();
        }

        ImGui.SameLine();
        ImGuiComponents.HelpMarker("A short notification (bottom right) when Frame Perfect changes a game option, or when something needs you.");
        var bar = cfg.ShowServerInfoBar;
        if (ImGui.Checkbox("Show the frame rate in the server info bar", ref bar))
        {
            cfg.ShowServerInfoBar = bar;
            cfg.Save();
        }

        ImGui.SameLine();
        ImGuiComponents.HelpMarker("The frame rate you see, next to the clock at the top right. Hover it for details, click it to open this window.");
    }

    // ------------------------------------------------------------------ help

    private void DrawHelp()
    {
        var p = this.Pacing;
        this.Header(FontAwesomeIcon.LifeRing, "Help & fixes", "Common problems, and what to do about them.");

        var flickerRisk = p.VrrForGame == true && !p.Fullscreen;
        if (this.openFlickerHelp)
        {
            ImGui.SetNextItemOpen(true);
            this.openFlickerHelp = false;
        }

        if (ImGui.CollapsingHeader("The picture flickers in brightness" + (flickerRisk ? "  (likely on your setup)" : string.Empty)))
        {
            Indented(() =>
            {
                ImGui.TextWrapped("Short flashes, a little brighter or darker, mostly while the camera moves (worse with DLSS 5, or when the frame rate " +
                                  "changes). That's G-Sync in borderless, not DLSS. Fix it for FFXIV only (other games keep G-Sync):");
                var profile = p.GsyncProfile is { Status: 0 } gp ? gp.Name : "Final Fantasy XIV: A Realm Reborn";
                Step(1, "Right-click the desktop > NVIDIA Control Panel.");
                Step(2, $"Manage 3D settings > Program Settings > select \"{profile}\".");
                Step(3, "Monitor Technology: Fixed Refresh > Apply.");
                Step(4, "Done. Frame Perfect sees it and caps at exactly your refresh rate, with every frame in step with the screen.");
                ImGui.PushStyleColor(ImGuiCol.Text, Muted);
                ImGui.TextWrapped("Why: in borderless, Windows copies every FFXIV frame through its compositor (the game uses an old swap chain type " +
                                  "that Windows never upgrades), which shows about one frame in ten a little early or late. With G-Sync each of those is a " +
                                  "short change of refresh rate, and many screens (VA and mini-LED especially) show that as a change in brightness.");
                ImGui.PopStyleColor();
                ImGui.TextColored(p.VrrForGame == true ? Warn : p.VrrForGame == false ? Good : Muted,
                                  $"Your game now: G-Sync {(p.VrrForGame is { } v ? v ? "on" : "off" : "unknown")}{(p.Fullscreen ? ", exclusive full screen" : ", borderless")}.");
                if (p.VrrForGame == false)
                    ImGui.TextColored(Muted, "Going back to G-Sync later? Also press Restore on the game's profile in Program Settings, or G-Sync stays off.");
            });
        }

        if (ImGui.CollapsingHeader("I still see stutter"))
        {
            Indented(() =>
            {
                ImGui.TextWrapped("Check that the frame cap is Automatic (Smoothness page) and that Game settings shows the frame rate limit as set. " +
                                  "With a fixed refresh rate, \"Keep frames in step with the screen\" should say Active.");
                ImGui.TextWrapped("Hitches when new areas, players or effects load come from the game streaming data; no frame pacing can hide those.");
            });
        }

        if (ImGui.CollapsingHeader("The game feels slow to respond"))
        {
            Indented(() =>
            {
                ImGui.TextWrapped("Use NVIDIA Reflex On (or On + Boost) on the Smoothness page. Smooth Motion adds about one rendered frame of lag; " +
                                  "if that bothers you, turn it off in the NVIDIA app.");
            });
        }

        if (ImGui.CollapsingHeader("Smooth Motion doesn't turn on"))
        {
            Indented(() =>
            {
                ImGui.TextWrapped("It needs an RTX 40 or 50 card (RTX 40: driver 590.26 or newer). Switch it on in the NVIDIA app > Graphics > " +
                                  "Program settings > Final Fantasy XIV, then restart the game: the driver only loads it at startup.");
            });
        }

        if (ImGui.CollapsingHeader("Text or nameplates smear with Smooth Motion"))
        {
            Indented(() =>
            {
                ImGui.TextWrapped("Smooth Motion makes its frames from the finished image, UI included, so fast camera turns can blur nameplates " +
                                  "a little. It's NVIDIA's driver, not something a plugin can change. Turn Smooth Motion off if you'd rather not have it.");
            });
        }

        if (ImGui.CollapsingHeader("After a game update, something doesn't work"))
        {
            Indented(() =>
            {
                ImGui.TextWrapped(this.plugin.Controller.HooksOk
                                      ? "Everything Frame Perfect hooks into was found in this game version."
                                      : "A game update moved code Frame Perfect works with, so it stays out of the way. Update the plugin when a new version is out.");
            });
        }

        if (ImGui.CollapsingHeader("How do I undo everything?"))
        {
            Indented(() =>
            {
                ImGui.TextWrapped("Game settings > Restore my original settings puts back your own game options. The switch at the bottom left turns " +
                                  "Frame Perfect off. An installed DLSS update keeps the old file next to it as nvngx_dlss.dll.<version>.bak.");
            });
        }

        if (ImGui.CollapsingHeader("Chat commands"))
        {
            Indented(() =>
            {
                Command("/frameperfect", "open this window");
                Command("/frameperfect status", "everything in chat");
                Command("/frameperfect mode balanced", "dlaa, quality, balanced, performance, ultra, custom, game");
                Command("/frameperfect sharp 40", "sharpening 0-100");
                Command("/frameperfect reflex on", "off, on, boost");
                Command("/frameperfect cap auto", "auto, off, or a frame rate");
                Command("/frameperfect gamesettings restore", "put your game options back");
                Command("/frameperfect off", "turn Frame Perfect off (on to turn it back on)");
            });
        }
    }

    private static void Step(int n, string text)
    {
        ImGui.TextColored(Accent, $"{n}.");
        ImGui.SameLine();
        ImGui.TextWrapped(text);
    }

    private static void Command(string cmd, string what)
    {
        ImGui.TextColored(Accent, cmd);
        ImGui.SameLine(260 * S);
        ImGui.TextColored(Muted, what);
    }

    private static void Indented(Action body)
    {
        ImGui.Indent(12 * S);
        ImGui.Dummy(new Vector2(0, 2 * S));
        body();
        ImGui.Dummy(new Vector2(0, 4 * S));
        ImGui.Unindent(12 * S);
    }

    // ------------------------------------------------------------------ diagnostics

    private void DrawDiagnostics()
    {
        var cfg = this.Cfg;
        var ctl = this.plugin.Controller;
        this.Header(FontAwesomeIcon.Stethoscope, "Diagnostics", "Technical details, for bug reports. Nothing here needs changing.");

        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Copy, "Copy report"))
            ImGui.SetClipboardText(this.Report());
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Copies everything on this page, to paste into a bug report.");
        ImGui.SameLine();
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.FolderOpen, "Open log folder"))
        {
            var dir = Path.GetDirectoryName(this.plugin.Log.FilePath);
            if (dir != null)
                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }

        ImGui.SameLine();
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Redo, "Reapply DLSS"))
            ctl.ApplyNow("button 'Reapply DLSS'");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Makes the game rebuild its render targets and DLSS (the screen blinks once).\nNormally not needed: Frame Perfect reapplies by itself.");
        ImGui.SameLine();
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Eraser, "Reset DLSS history"))
            ctl.RequestHistoryReset("button 'Reset DLSS history'");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Tells DLSS the next frame is a scene cut (it and DLSS 5 restart their history).");

        var flash = cfg.FlashDetector;
        if (ImGui.Checkbox("Flash detector", ref flash))
        {
            cfg.FlashDetector = flash;
            cfg.Save();
            ctl.OnSettingsChanged(rebuild: false);
        }

        ImGui.SameLine();
        ImGuiComponents.HelpMarker("Measures every frame's brightness on the GPU and logs short light/dark blinks (FLASH lines), plus local flicker " +
                                   "and frame pacing every 5 s. Costs one tiny GPU dispatch per frame.");
        if (cfg.MvScale != MotionVectorScale.Game)
            ImGui.TextColored(Warn, "Motion vector scale is in diagnostic mode (render). Use /frameperfect mv game.");

        Section("Report");
        ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudWhite2);
        foreach (var line in this.ReportLines())
            ImGui.TextWrapped(line);
        ImGui.PopStyleColor();
    }

    private string Report()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Frame Perfect {this.plugin.Version}");
        foreach (var l in this.ReportLines())
            sb.AppendLine(l);
        return sb.ToString();
    }

    private IEnumerable<string> ReportLines()
    {
        var cfg = this.Cfg;
        var ctl = this.plugin.Controller;
        var p = this.Pacing;
        var s = ctl.Status;
        var rt = ctl.Runtime;
        yield return $"Settings: {ctl.DescribeSettings()} | Reflex {cfg.Reflex} | cap {p.CapDescription} | refresh lock {(cfg.RefreshLock ? "on" : "off")} ({p.RefreshLockState})";
        yield return $"Graphics card: {(GameOptimizer.GpuName.Length > 0 ? GameOptimizer.GpuName : "?")} | hooks {(ctl.HooksOk ? "ok" : "NOT FOUND")} | present hook {(p.HookOk ? "ok" : "NOT FOUND")}";
        yield return s.OutputH != 0
            ? $"DLSS: {s.RenderW}x{s.RenderH} -> {s.OutputW}x{s.OutputH} ({100.0 * s.RenderH / s.OutputH:0.0}%) | NGX mode {s.NgxMode} | {(s.Stable ? "stable" : "VARYING")} | range {s.RangeMinH}..{s.RangeMaxH} | {s.EvalsPerSecond:0} evaluate calls/s"
            : "DLSS: no frames yet";
        yield return $"Sharpening: {ctl.SharpenState}";
        var probe = ctl.Probe;
        yield return cfg.FlashDetector
            ? $"Flash detector: {probe.TotalFlashes} flashes (last {probe.LastFlash}) | local flicker {probe.LastLocalFlicker:0.00}/1000 | {probe.State}"
            : "Flash detector: off";
        yield return $"Upscaler DLL: {rt.Upscaler}";
        yield return $"DLSS 5 (NR) DLL: {rt.NeuralRendering} | RenoDX: runtime {rt.NrRuntimeVersion}, state {rt.NrState}, {rt.NrCostMs} ms GPU, flicker {rt.NrFlickerStops}";
        yield return $"Monitors ({p.MonitorCount}): {p.MonitorList}";
        yield return $"Window on {p.WindowMonitor} {p.WindowHz} Hz | primary {p.PrimaryMonitor} {p.PrimaryHz} Hz | game refresh value {p.GameHzNow} Hz (read at startup {p.GameHzOriginal} Hz) | DWM {p.DwmRefreshHz:0.###} Hz";
        yield return $"Game limiter: {FramePacing.LimitModeName(p.LimitMode, p.LimitFps)}{(p.Fullscreen ? " | exclusive full screen" : " | windowed/borderless")}";
        yield return $"Swap chain: {(p.SwapChainInfo.Length > 0 ? p.SwapChainInfo : "?")}";
        var (wo, woSource) = p.WindowedOptimizationsState;
        yield return $"Windows 'Optimizations for windowed games': {(wo == true ? "on" : wo == false ? "off" : "not set")} ({woSource}); FFXIV's blit-model swap chain is never upgraded (PresentMon)";
        yield return $"Presents: {(p.PresentStats.Length > 0 ? p.PresentStats : "?")}";
        var ss = p.LastSleepStatus;
        yield return $"Reflex: {p.ReflexState}" +
                     (ss.HasValue ? $" | low latency {ss.Value.LowLatency}, fullscreen G-Sync {ss.Value.FullscreenVrr}, control panel V-Sync {ss.Value.CplVsyncOn}, iFlip {ss.Value.FullscreenIFlip}, game sleep {ss.Value.UseGameSleep}" : string.Empty) +
                     (p.ReflexLatency.Length > 0 ? $" | latency {p.ReflexLatency}" : string.Empty);
        yield return $"G-Sync: {(p.GsyncSummary.Length > 0 ? p.GsyncSummary : "?")}";
        yield return $"Smooth Motion: {(p.SmoothMotionLoaded ? "running (NvPresent64.dll loaded)" : "not running")}, driver frame-gen multiplier {(p.DriverFgMultiplier > 1 ? $"{p.DriverFgMultiplier}x" : "none")}, " +
                     $"interval scale x{p.DriverIntervalScale} | G-Sync for the game: {(p.VrrForGame is { } v ? v ? "yes" : "no" : "?")}";
        var opt = this.plugin.Optimizer;
        var game = new List<string>();
        foreach (var item in GameOptimizer.Items)
            game.Add($"{item.Name}={GameOptimizer.Read(item.Name)?.ToString() ?? "?"}{(opt.IsManaged(item.Name) ? " (managed)" : string.Empty)}");
        yield return $"Game options: {string.Join(", ", game)} | saved originals: {(cfg.SavedGameSettings.Count > 0 ? string.Join(", ", cfg.SavedGameSettings) : "none")}";
        yield return $"Log: {this.plugin.Log.FilePath}";
    }

    // ------------------------------------------------------------------ building blocks

    private void Header(FontAwesomeIcon icon, string title, string text)
    {
        using (this.titleFont.Push())
        {
            ImGui.PushFont(UiBuilder.IconFont);
            ImGui.TextColored(Accent, icon.ToIconString());
            ImGui.PopFont();
            ImGui.SameLine();
            ImGui.TextColored(White, title);
        }

        ImGui.PushStyleColor(ImGuiCol.Text, Muted);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
        ImGui.Dummy(new Vector2(0, 4 * S));
    }

    private static void Section(string title)
    {
        ImGui.Dummy(new Vector2(0, 6 * S));
        ImGui.TextColored(Accent, title.ToUpperInvariant());
        var p = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddLine(p, p + new Vector2(ImGui.GetContentRegionAvail().X, 0), ImGui.GetColorU32(new Vector4(Accent.X, Accent.Y, Accent.Z, 0.35f)));
        ImGui.Dummy(new Vector2(0, 5 * S));
    }

    /// <summary>A rounded panel with a colored edge, as tall as what <paramref name="body"/> draws.</summary>
    private void Card(Vector4 color, Action body)
    {
        var dl = ImGui.GetWindowDrawList();
        var pad = 10 * S;
        var edge = 4 * S;
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;

        dl.ChannelsSplit(2);
        dl.ChannelsSetCurrent(1);
        ImGui.SetCursorScreenPos(start + new Vector2(pad + edge, pad));
        ImGui.BeginGroup();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width - (2 * pad) - edge);
        try
        {
            body();
        }
        finally
        {
            ImGui.PopTextWrapPos();
            ImGui.EndGroup();
            var end = new Vector2(start.X + width, ImGui.GetItemRectMax().Y + pad);
            dl.ChannelsSetCurrent(0);
            dl.AddRectFilled(start, end, ImGui.GetColorU32(new Vector4(color.X, color.Y, color.Z, 0.09f)), 6 * S);
            dl.AddRectFilled(start, new Vector2(start.X + edge, end.Y), ImGui.GetColorU32(color), 6 * S, ImDrawFlags.RoundCornersLeft);
            dl.ChannelsMerge();
            ImGui.SetCursorScreenPos(new Vector2(start.X, end.Y));
            ImGui.Dummy(new Vector2(width, 6 * S));
        }
    }

    private static void Icon(FontAwesomeIcon icon, Vector4 color)
    {
        ImGui.PushFont(UiBuilder.IconFont);
        ImGui.TextColored(color, icon.ToIconString());
        ImGui.PopFont();
    }
}
