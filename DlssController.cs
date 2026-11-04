using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using GraphicsConfig = FFXIVClientStructs.FFXIV.Client.Graphics.Render.GraphicsConfig;
using RenderManager = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Manager;
using RenderTargetManager = FFXIVClientStructs.FFXIV.Client.Graphics.Render.RenderTargetManager;

namespace FramePerfect;

/// <summary>
/// How the game drives DLSS (reverse engineered from ffxiv_dx11.exe 2026.10.16):
///
///  * When GraphicsConfig.GraphicsRezoUpscaleType (+0x54) == 2 (DLSS), dynamic resolution is forced on,
///    whatever the "dynamic resolution" option says.
///  * The DLSS quality mode comes from a hidden byte GraphicsConfig+0x55
///    (0 auto, 1 DLAA, 2 UltraQuality, 3 Quality, 4 Balanced, 5 Performance, 6 UltraPerformance).
///    "Auto" picks Performance at 4K, Quality at 1440p, DLAA below 1080p.
///  * DlssManager::SelectMode asks NGX for the optimal / dynamic-min / dynamic-max render size of that mode.
///  * RenderTargetManager calls a callback (RTM+0x730) to get that range: height max = NGX max,
///    height min = NGX min. Its controller (RTM+0x700..0x714) then walks the height between those bounds
///    in 9/18-line steps based on GPU time vs the frame budget, but only while the camera moves.
///    That is why zooming the camera makes the resolution drop.
///  * The DLSS feature is created with InWidth/InHeight = optimal and the preset hint hard-coded to C (3).
///
/// Fix: force the mode byte, then report min = max = optimal to the render target manager so its
/// controller can never leave the chosen size. Optionally override the preset hint at feature creation.
/// </summary>
internal sealed unsafe class DlssController : IDisposable
{
    // RVA 0x374590  bool DlssManager::SelectMode(DlssManager*, uint2* displaySize, bool)
    private const string SigSelectMode = "40 57 41 56 48 83 EC 38 F6 81 81 01 00 00 01 4C 8B F2 48 8B F9 75 1B E8 ?? ?? ?? ??";

    // RVA 0x352350  bool RtmDlssRange(uint2* displaySize, uint2* optimal, uint2* min, uint2* max) (pointer stored at RTM+0x730)
    private const string SigRangeCallback = "48 89 5C 24 08 48 89 74 24 10 48 89 7C 24 18 41 56 48 83 EC 20 48 8B 05 ?? ?? ?? ?? 49 8B F9 49 8B F0";

    // RVA 0x374CB0  bool DlssManager::CreateFeature(DlssManager*)
    private const string SigCreateFeature = "40 56 48 83 EC 20 F6 81 81 01 00 00 01 48 8B F1 75 08 32 C0 48 83 C4 20";

    // RVA 0x375640  bool DlssEvaluate(DlssEvalArgs*) - runs on the render thread every frame DLSS is used
    private const string SigEvaluate = "40 55 53 48 8D AC 24 58 FF FF FF 48 81 EC A8 01 00 00 48 8B 05 ?? ?? ?? ?? 48 8B D9";

    // GraphicsConfig
    private const int CfgDepthOfField = 0x26;
    private const int CfgRadialBlur = 0x28;
    private const int CfgDynEnable = 0x44;
    private const int CfgDynActive = 0x45;
    private const int CfgDynThreshold = 0x49;
    private const int CfgRezoScale = 0x4C; // float, 3D resolution scale slider / 100
    private const int CfgUpscaleType = 0x54; // 1 = FSR, 2 = DLSS
    private const int CfgDlssMode = 0x55;

    // DlssManager (0x188 bytes)
    private const int DlssDisplay = 0xA0;
    private const int DlssOptimal = 0xA8;
    private const int DlssMin = 0xB0;
    private const int DlssMax = 0xB8;
    private const int DlssPerfQuality = 0xC0;
    private const int DlssPreset = 0xC4;
    private const int DlssUseRezoScale = 0x180;
    private const int DlssFlags = 0x181;

    // RenderTargetManager
    private const int RtmAppliedH = 0x700;
    private const int RtmTargetH = 0x702;
    private const int RtmMaxH = 0x704;
    private const int RtmMinH = 0x706;
    private const int RtmGpuMs = 0x70C;
    private const int RtmFrameAvg = 0x710;
    private const int RtmScale = 0x71C;
    private const int RtmScaleX = 0x720;
    private const int RtmScaleY = 0x724;

    // DlssManager committed copy of the 0xA0 block (compared every SelectMode; a mismatch recreates the feature)
    private const int DlssCommittedDisplay = 0xCC;

    // DlssEvalArgs (built at DlssManager+0xF8 by 0x374050, copied into the render command)
    private const int EvalOutputTexture = 0x08; // Kernel::Texture* of the DLSS output
    private const int EvalJitter = 0x28; // float2, from camera+0x278
    private const int EvalMvScale = 0x30; // float2, the game uses Device width/height (display size)
    private const int EvalDlss = 0x38;
    private const int EvalSubrect = 0x48; // uint2 render size (RTM+0x428)
    private const int EvalReset = 0x50; // byte

    // Kernel::Device
    private const int DevRequestResolutionChange = 0x7A;
    private const int DevWidth = 0x8C;
    private const int DevHeight = 0x90;

    private delegate byte SelectModeDelegate(nint dlss, uint* displaySize, byte flag);
    private delegate byte RangeCallbackDelegate(uint* displaySize, uint* optimal, uint* min, uint* max);
    private delegate byte CreateFeatureDelegate(nint dlss);
    private delegate byte EvaluateDelegate(nint args);

    private readonly Configuration config;
    private readonly FileLog log;

    private readonly Hook<SelectModeDelegate>? selectHook;
    private readonly Hook<RangeCallbackDelegate>? rangeHook;
    private readonly Hook<CreateFeatureDelegate>? createHook;
    private readonly Hook<EvaluateDelegate>? evalHook;

    private nint dlssManager;
    private byte? originalModeByte;
    private string lastSelectLine = string.Empty;
    private string lastRangeLine = string.Empty;
    private bool pendingApply = true;
    private DateTime nextStats = DateTime.Now.AddSeconds(5);
    private DateTime lastApplyRequest = DateTime.MinValue;
    private (byte, byte)? lastWatch;
    private long selectCalls;
    private long selectCallsAtApply;
    private DateTime? verifyApplyAt;
    private bool modulesLoggedWithDlss;
    private bool forceRecreate = true;
    private volatile bool refreshModules = true;
    private DateTime nextModuleRefresh = DateTime.MinValue;
    private readonly Sharpener sharpener;
    private readonly FrameProbe probe;
    private string pendingApplyReason = "startup";
    private string loggedToggles = string.Empty;
    private volatile bool historyResetPending;
    private string historyResetReason = string.Empty;

    public RuntimeInfo Runtime { get; }

    /// <summary>Set by the plugin; its present and Reflex lines are written with the periodic stats.</summary>
    public FramePacing? Pacing { get; set; }

    public string SharpenState => this.sharpener.State;

    public FrameProbe Probe => this.probe;

    public string ModeLabel => this.config.Mode switch
    {
        DlssMode.Dlaa => "DLAA",
        DlssMode.Quality => "Quality",
        DlssMode.Balanced => "Balanced",
        DlssMode.Performance => "Performance",
        DlssMode.UltraPerformance => "Ultra Performance",
        DlssMode.Custom => $"Custom {this.config.CustomScalePercent:0.#}%",
        _ => "game default",
    };

    private long resetCount;
    private float gameMvX, gameMvY, usedMvX, usedMvY;
    private float jitterMinX = float.MaxValue, jitterMaxX = float.MinValue, jitterMinY = float.MaxValue, jitterMaxY = float.MinValue;

    // render thread statistics
    private readonly object statsGate = new();
    private long evalCount;
    private long evalFail;
    private int sizeChanges;
    private int changeMessages;
    private uint curW, curH, outW, outH;
    private uint minW = uint.MaxValue, minH = uint.MaxValue, maxW, maxH;
    private DateTime statsStart = DateTime.Now;
    private long totalEvals;

    public bool HooksOk { get; }

    public DlssStatus Status { get; private set; } = new();

    public DlssController(Configuration config, FileLog log)
    {
        this.config = config;
        this.log = log;

        var module = Process.GetCurrentProcess().MainModule!;
        log.Info($"==== Frame Perfect start | exe={module.FileName} base=0x{module.BaseAddress:X} game={ReadGameVersion(module.FileName)}");
        this.Runtime = new RuntimeInfo(Path.GetDirectoryName(module.FileName)!);
        this.sharpener = new Sharpener(log);
        this.probe = new FrameProbe(log);

        this.selectHook = this.TryHook<SelectModeDelegate>("DlssManager::SelectMode", SigSelectMode, this.SelectModeDetour, module.BaseAddress);
        this.rangeHook = this.TryHook<RangeCallbackDelegate>("RTM DLSS range callback", SigRangeCallback, this.RangeDetour, module.BaseAddress);
        this.createHook = this.TryHook<CreateFeatureDelegate>("DlssManager::CreateFeature", SigCreateFeature, this.CreateFeatureDetour, module.BaseAddress);
        this.evalHook = this.TryHook<EvaluateDelegate>("DLSS evaluate", SigEvaluate, this.EvaluateDetour, module.BaseAddress);

        this.HooksOk = this.selectHook != null && this.rangeHook != null;
        if (!this.HooksOk)
            log.Error("Required hooks missing (game patched?). The plugin will only observe; nothing is changed.");

        this.LogEnvironment();
        this.LogConfig("startup");

        this.selectHook?.Enable();
        this.rangeHook?.Enable();
        this.createHook?.Enable();
        this.evalHook?.Enable();
    }

    private Hook<T>? TryHook<T>(string name, string sig, T detour, nint moduleBase) where T : Delegate
    {
        try
        {
            if (!Plugin.SigScanner.TryScanText(sig, out var address))
            {
                this.log.Error($"hook {name}: signature NOT found");
                return null;
            }

            var hook = Plugin.GameInterop.HookFromAddress(address, detour);
            this.log.Info($"hook {name}: found at ffxiv_dx11.exe+0x{address - moduleBase:X}");
            return hook;
        }
        catch (Exception e)
        {
            this.log.Error($"hook {name}: failed: {e.Message}");
            return null;
        }
    }

    // ------------------------------------------------------------------ hooks

    private byte SelectModeDetour(nint dlss, uint* displaySize, byte flag)
    {
        this.dlssManager = dlss;
        Interlocked.Increment(ref this.selectCalls);
        var cfg = (byte*)GraphicsConfig.Instance();
        var active = this.config.Enabled && this.config.Mode != DlssMode.GameDefault && cfg != null && cfg[CfgUpscaleType] == 2;

        var swappedScale = false;
        var savedScale = 0f;
        byte modeBefore = cfg != null ? cfg[CfgDlssMode] : (byte)0;

        try
        {
            if (cfg != null && this.originalModeByte.HasValue && !active && cfg[CfgDlssMode] != this.originalModeByte.Value)
            {
                // plugin disabled / game-default mode: hand the game its own value back
                cfg[CfgDlssMode] = this.originalModeByte.Value;
                *(byte*)(dlss + DlssUseRezoScale) = 0;
            }

            if (this.forceRecreate && this.config.Enabled && cfg != null && cfg[CfgUpscaleType] == 2)
            {
                // Same trick the game uses in its own failure path: invalidate the committed block so
                // SelectMode sees a change, releases the feature and creates it again (with our preset).
                this.forceRecreate = false;
                *(uint*)(dlss + DlssCommittedDisplay) = 0xFFFFFFFF;
                this.log.Enqueue("forcing DLSS feature re-creation (startup / preset change)");
            }

            if (active)
            {
                this.originalModeByte ??= cfg[CfgDlssMode];
                cfg[CfgDlssMode] = this.EffectiveModeCode();
                var custom = this.config.Mode == DlssMode.Custom;
                *(byte*)(dlss + DlssUseRezoScale) = (byte)(custom ? 1 : 0);
                if (custom)
                {
                    savedScale = *(float*)(cfg + CfgRezoScale);
                    *(float*)(cfg + CfgRezoScale) = this.CustomScale;
                    swappedScale = true;
                }
            }
        }
        catch (Exception e)
        {
            this.log.Error($"SelectMode pre: {e}");
        }

        byte result;
        try
        {
            result = this.selectHook!.Original(dlss, displaySize, flag);
        }
        finally
        {
            if (swappedScale)
                *(float*)(cfg + CfgRezoScale) = savedScale;
        }

        try
        {
            var line = $"SelectMode -> {(result != 0 ? "ok" : "FAILED")} | display={Dim(displaySize)} cfgMode {modeBefore}->{(cfg != null ? cfg[CfgDlssMode] : 0)} " +
                       $"| NGX mode={PerfQualityName(*(int*)(dlss + DlssPerfQuality))} optimal={Dim((uint*)(dlss + DlssOptimal))} " +
                       $"min={Dim((uint*)(dlss + DlssMin))} max={Dim((uint*)(dlss + DlssMax))} useRezoScale={*(byte*)(dlss + DlssUseRezoScale)} " +
                       $"flags=0x{*(byte*)(dlss + DlssFlags):X2}" + (active ? string.Empty : " (pass-through)");
            if (line != this.lastSelectLine)
            {
                this.lastSelectLine = line;
                this.log.Enqueue(line);
            }

            if (result == 0 && active)
                this.log.Enqueue("WARNING: DLSS SelectMode failed while forcing a mode; the game may fall back to FSR.");
        }
        catch
        {
            // logging only
        }

        return result;
    }

    private byte RangeDetour(uint* displaySize, uint* optimal, uint* min, uint* max)
    {
        var result = this.rangeHook!.Original(displaySize, optimal, min, max);

        try
        {
            var origMin = Dim(min);
            var origMax = Dim(max);
            var locked = false;

            if (result != 0 && this.config.Enabled && this.config.Mode != DlssMode.GameDefault && this.config.LockResolution &&
                optimal[0] != 0 && optimal[1] != 0)
            {
                // The RTM derives width = display.W * height / display.H. Make sure that never exceeds the width
                // the DLSS feature was created with.
                var h = optimal[1];
                if (displaySize[1] != 0)
                {
                    while (h > 1 && (ulong)displaySize[0] * h / displaySize[1] > optimal[0])
                        h--;
                }

                min[0] = max[0] = optimal[0];
                min[1] = max[1] = h;
                locked = true;
            }

            // outputs are uninitialised stack memory when the callback fails (FSR), so don't print them then
            var line = result == 0
                ? $"RTM range -> no DLSS (FSR or DLSS unavailable) | display={Dim(displaySize)}"
                : $"RTM range -> ok | display={Dim(displaySize)} optimal={Dim(optimal)} NGX min={origMin} max={origMax}" +
                  (locked ? $" => LOCKED min=max={Dim(max)}" : " (game dynamic range)");
            if (line != this.lastRangeLine)
            {
                this.lastRangeLine = line;
                this.log.Enqueue(line);
            }
        }
        catch
        {
            // logging only
        }

        return result;
    }

    private byte CreateFeatureDetour(nint dlss)
    {
        var originalPreset = *(int*)(dlss + DlssPreset);
        var swapped = false;
        if (this.config.Enabled && this.config.Preset != PresetOverride.Game)
        {
            *(int*)(dlss + DlssPreset) = (int)this.config.Preset;
            swapped = true;
        }

        try
        {
            this.refreshModules = true;
            this.log.Enqueue($"CreateFeature: render={Dim((uint*)(dlss + DlssOptimal))} output={Dim((uint*)(dlss + DlssDisplay))} " +
                             $"mode={PerfQualityName(*(int*)(dlss + DlssPerfQuality))} preset {PresetName(originalPreset)} -> {PresetName(*(int*)(dlss + DlssPreset))}");
            return this.createHook!.Original(dlss);
        }
        finally
        {
            if (swapped)
                *(int*)(dlss + DlssPreset) = originalPreset;
        }
    }

    /// <summary>Set the DLSS Reset flag on the next evaluate: DLSS drops its history for one frame, and the
    /// RenoDX DLSS5 addon treats it as a scene cut (Neural Rendering history and stabilizer start over).</summary>
    public void RequestHistoryReset(string reason)
    {
        this.historyResetReason = reason;
        this.historyResetPending = true;
    }

    private byte EvaluateDetour(nint args)
    {
        float gmx = 0, gmy = 0;
        if (this.historyResetPending)
        {
            this.historyResetPending = false;
            try
            {
                *(byte*)(args + EvalReset) = 1;
                this.log.Enqueue($"forced DLSS history reset ({this.historyResetReason}); DLSS 5 sees it as a scene cut");
            }
            catch
            {
                // never block the frame
            }
        }

        try
        {
            gmx = *(float*)(args + EvalMvScale);
            gmy = *(float*)(args + EvalMvScale + 4);
            if (this.config.Enabled && this.config.MvScale == MotionVectorScale.RenderSize)
            {
                var sw = *(uint*)(args + EvalSubrect);
                var sh = *(uint*)(args + EvalSubrect + 4);
                if (sw != 0 && sh != 0)
                {
                    // keep the game's sign convention, only change the magnitude
                    *(float*)(args + EvalMvScale) = gmx < 0 ? -sw : sw;
                    *(float*)(args + EvalMvScale + 4) = gmy < 0 ? -sh : sh;
                }
            }
        }
        catch
        {
            // never block the frame
        }

        var result = this.evalHook!.Original(args);

        if (result != 0)
        {
            var output = *(nint*)(args + EvalOutputTexture);
            try
            {
                // called every frame so the strength can glide to 0 as well as up
                this.sharpener.Apply(output, this.config.Enabled ? this.config.Sharpness : 0f);
            }
            catch (Exception e)
            {
                this.log.Enqueue($"sharpen exception: {e.Message}");
            }

            if (this.config.FlashDetector)
            {
                try
                {
                    this.probe.SharpenNow = this.sharpener.Current;
                    this.probe.Measure(output);
                }
                catch (Exception e)
                {
                    this.log.Enqueue($"flash probe exception: {e.Message}");
                }
            }
        }

        try
        {
            var w = *(uint*)(args + EvalSubrect);
            var h = *(uint*)(args + EvalSubrect + 4);
            var jx = *(float*)(args + EvalJitter);
            var jy = *(float*)(args + EvalJitter + 4);
            var reset = *(byte*)(args + EvalReset);
            var umx = *(float*)(args + EvalMvScale);
            var umy = *(float*)(args + EvalMvScale + 4);
            var dlss = *(nint*)(args + EvalDlss);
            uint ow = 0, oh = 0;
            if (dlss != 0)
            {
                ow = *(uint*)(dlss + DlssDisplay);
                oh = *(uint*)(dlss + DlssDisplay + 4);
            }

            lock (this.statsGate)
            {
                this.evalCount++;
                this.totalEvals++;
                if (result == 0)
                    this.evalFail++;
                if (reset != 0)
                    this.resetCount++;
                this.gameMvX = gmx;
                this.gameMvY = gmy;
                this.usedMvX = umx;
                this.usedMvY = umy;
                this.jitterMinX = Math.Min(this.jitterMinX, jx);
                this.jitterMaxX = Math.Max(this.jitterMaxX, jx);
                this.jitterMinY = Math.Min(this.jitterMinY, jy);
                this.jitterMaxY = Math.Max(this.jitterMaxY, jy);

                if ((w != this.curW || h != this.curH) && this.curH != 0)
                {
                    this.sizeChanges++;
                    if (this.changeMessages++ < 20)
                        this.log.Enqueue($"DLSS input size changed: {this.curW}x{this.curH} -> {w}x{h} (output {ow}x{oh})");
                }

                this.curW = w;
                this.curH = h;
                this.outW = ow;
                this.outH = oh;
                if (h < this.minH || (h == this.minH && w < this.minW)) { this.minW = w; this.minH = h; }
                if (h > this.maxH || (h == this.maxH && w > this.maxW)) { this.maxW = w; this.maxH = h; }
            }
        }
        catch
        {
            // statistics only
        }

        return result;
    }

    // ------------------------------------------------------------------ framework tick

    public void Update()
    {
        // The DLSS mode is only re-selected when the render targets are rebuilt, so watch for the
        // situations where the game would otherwise keep a stale size (lobby -> in game, FSR -> DLSS).
        var cfg = (byte*)GraphicsConfig.Instance();
        if (cfg != null)
        {
            var watch = (cfg[CfgUpscaleType], cfg[CfgDynActive]);
            if (this.lastWatch.HasValue && this.lastWatch.Value != watch)
            {
                this.log.Info($"game state changed: upscaleType {this.lastWatch.Value.Item1}->{watch.Item1}, dynActive {this.lastWatch.Value.Item2}->{watch.Item2}");
                if (this.config.Enabled && this.config.Mode != DlssMode.GameDefault)
                    this.ApplyNow("game state changed (lobby/zone or upscaler switch)");
            }

            this.lastWatch = watch;
        }

        if (this.pendingApply && this.HooksOk && (DateTime.Now - this.lastApplyRequest).TotalSeconds > 1)
        {
            if (this.RequestResolutionChange(this.pendingApplyReason))
            {
                this.pendingApply = false;
                this.selectCallsAtApply = Interlocked.Read(ref this.selectCalls);
                this.verifyApplyAt = DateTime.Now.AddSeconds(3);
            }
        }

        if (this.verifyApplyAt.HasValue && DateTime.Now >= this.verifyApplyAt.Value)
        {
            this.verifyApplyAt = null;
            if (Interlocked.Read(ref this.selectCalls) == this.selectCallsAtApply && cfg != null && cfg[CfgUpscaleType] == 2)
            {
                // Plan B: make the game's own dynamic-resolution controller fire its render-target rebuild event
                // (RTM+0x700 != RTM+0x702) while the device's full-rebuild flag is set, so the rebuild takes the
                // full path that re-queries DLSS.
                var rtm = (byte*)RenderTargetManager.Instance();
                var device = (byte*)Device.Instance();
                if (rtm != null && device != null && *(ushort*)(rtm + RtmAppliedH) > 1)
                {
                    device[DevRequestResolutionChange] = 1;
                    *(ushort*)(rtm + RtmTargetH) = (ushort)(*(ushort*)(rtm + RtmAppliedH) - 1);
                    this.log.Warn("resolution-change request did not reach DLSS SelectMode within 3s; nudged the RTM controller (plan B)");
                }
                else
                {
                    this.log.Warn("resolution-change request did not reach DLSS SelectMode within 3s (RTM not ready)");
                }
            }
        }

        if (this.refreshModules || DateTime.Now >= this.nextModuleRefresh)
        {
            this.refreshModules = false;
            this.nextModuleRefresh = DateTime.Now.AddSeconds(30);
            var changedLine = this.Runtime.RefreshModules();
            if (changedLine != null)
                this.log.Info(changedLine);
        }

        if (DateTime.Now >= this.nextStats)
        {
            this.nextStats = DateTime.Now.AddSeconds(Math.Clamp(this.config.StatsIntervalSeconds, 1, 300));
            this.WriteStats();
        }

        this.log.Flush();
    }

    /// <summary>Re-run the game's render-target/DLSS setup so a new mode takes effect immediately.
    /// The game recreates its render targets, so the screen blinks once.</summary>
    public void ApplyNow(string reason)
    {
        this.pendingApplyReason = this.pendingApply && this.pendingApplyReason != reason ? $"{this.pendingApplyReason} + {reason}" : reason;
        this.pendingApply = true;
    }

    private bool RequestResolutionChange(string reason)
    {
        var device = (byte*)Device.Instance();
        if (device == null || RenderTargetManager.Instance() == null)
            return false;

        this.lastApplyRequest = DateTime.Now;
        device[DevRequestResolutionChange] = 1;
        this.log.Info($"Requested render-target rebuild ({reason}); device size={*(uint*)(device + DevWidth)}x{*(uint*)(device + DevHeight)}");
        return true;
    }

    private void WriteStats()
    {
        long count, fails, total, resets;
        int changes;
        uint cw, ch, ow, oh, mnW, mnH, mxW, mxH;
        float gmx, gmy, umx, umy, jx0, jx1, jy0, jy1;
        double seconds;
        lock (this.statsGate)
        {
            count = this.evalCount;
            fails = this.evalFail;
            resets = this.resetCount;
            changes = this.sizeChanges;
            total = this.totalEvals;
            cw = this.curW; ch = this.curH; ow = this.outW; oh = this.outH;
            mnW = this.minW; mnH = this.minH; mxW = this.maxW; mxH = this.maxH;
            gmx = this.gameMvX; gmy = this.gameMvY; umx = this.usedMvX; umy = this.usedMvY;
            jx0 = this.jitterMinX; jx1 = this.jitterMaxX; jy0 = this.jitterMinY; jy1 = this.jitterMaxY;
            seconds = Math.Max(0.001, (DateTime.Now - this.statsStart).TotalSeconds);
            this.evalCount = 0;
            this.evalFail = 0;
            this.resetCount = 0;
            this.sizeChanges = 0;
            this.changeMessages = 0;
            this.minW = this.minH = uint.MaxValue;
            this.maxW = this.maxH = 0;
            this.jitterMinX = this.jitterMinY = float.MaxValue;
            this.jitterMaxX = this.jitterMaxY = float.MinValue;
            this.statsStart = DateTime.Now;
        }

        var cfg = (byte*)GraphicsConfig.Instance();
        var rtm = (byte*)RenderTargetManager.Instance();

        var status = new DlssStatus
        {
            HooksOk = this.HooksOk,
            EvalsPerSecond = count / seconds,
            TotalEvals = total,
            RenderW = cw,
            RenderH = ch,
            OutputW = ow,
            OutputH = oh,
            Stable = count > 0 && changes == 0 && mnH == mxH && mnW == mxW,
            UpscaleType = cfg != null ? cfg[CfgUpscaleType] : (byte)0,
            ModeByte = cfg != null ? cfg[CfgDlssMode] : (byte)0,
            NgxMode = this.dlssManager != 0 ? PerfQualityName(*(int*)(this.dlssManager + DlssPerfQuality)) : "?",
            RangeMinH = rtm != null ? *(ushort*)(rtm + RtmMinH) : (ushort)0,
            RangeMaxH = rtm != null ? *(ushort*)(rtm + RtmMaxH) : (ushort)0,
            RadialBlur = cfg != null && cfg[CfgRadialBlur] != 0,
            DepthOfField = cfg != null && cfg[CfgDepthOfField] != 0,
        };
        this.Status = status;
        this.Runtime.RefreshFromReShadeLogAsync(); // results show up in the next stats line

        var toggles = this.Runtime.NrToggles;
        if (toggles != this.loggedToggles)
        {
            var added = toggles.StartsWith(this.loggedToggles) ? toggles[this.loggedToggles.Length..].TrimStart(',', ' ') : toggles;
            this.loggedToggles = toggles;
            if (added.Length > 0)
                this.log.Info($"NR (RenoDX) F6 toggles from ReShade.log: {added}");
        }

        var sb = new StringBuilder();
        if (count > 0)
        {
            var pct = oh != 0 ? 100.0 * ch / oh : 0;
            sb.Append($"STATS {seconds:0.0}s: evals={count} ({count / seconds:0.0}/s) fail={fails} | DLSS in={cw}x{ch} ");
            sb.Append($"[min {mnW}x{mnH}, max {mxW}x{mxH}, changes {changes}] -> out={ow}x{oh} | scale={pct:0.0}% | ");
            sb.Append(status.Stable ? "STABLE" : "VARYING");
        }
        else
        {
            sb.Append($"STATS {seconds:0.0}s: no DLSS evaluate calls (DLSS not active, menu/loading, or upscaler is FSR)");
        }

        this.log.Info(sb.ToString());
        if (count > 0)
        {
            this.log.Info($"  EVAL: resets={resets} mvScale game=({gmx:0.#},{gmy:0.#}) sent=({umx:0.#},{umy:0.#}) [{this.config.MvScale}] " +
                          $"jitter x[{jx0:0.000}..{jx1:0.000}] y[{jy0:0.000}..{jy1:0.000}]");
            var (sharpCount, sharpCpu) = this.sharpener.TakeStats();
            this.log.Info($"  SHARPEN: strength={this.config.Sharpness:0.00} (applied now {this.sharpener.Current:0.00}) frames={sharpCount} cpu={sharpCpu:0.000}ms/frame state={this.sharpener.State}");
            if (this.config.FlashDetector)
            {
                foreach (var l in this.probe.TakeStatsLine().Split('\n'))
                    this.log.Info("  " + l.Trim());
            }
            this.log.Info($"  DLL: upscaler={this.Runtime.Upscaler} | NR={this.Runtime.NeuralRendering} (runtime {this.Runtime.NrRuntimeVersion})");
            this.log.Info($"  NR (RenoDX): state={this.Runtime.NrState} gpu={this.Runtime.NrCostMs}ms flicker={this.Runtime.NrFlickerStops} " +
                          $"stabilizer restarts={this.Runtime.NrStabilizerRestarts} contract={this.Runtime.NrContractSummary}");
        }

        this.Pacing?.WriteStats();

        if (count > 0 && !this.modulesLoggedWithDlss)
        {
            this.modulesLoggedWithDlss = true;
            this.log.Info("first DLSS frames seen; loaded modules now:");
            this.LogModules();
        }

        if (rtm != null)
        {
            this.log.Info($"  RTM: appliedH={*(ushort*)(rtm + RtmAppliedH)} targetH={*(ushort*)(rtm + RtmTargetH)} " +
                          $"rangeH=[{*(ushort*)(rtm + RtmMinH)}..{*(ushort*)(rtm + RtmMaxH)}] gpuMs={*(float*)(rtm + RtmGpuMs):0.00} " +
                          $"frameAvgMs={*(float*)(rtm + RtmFrameAvg):0.00} scale={*(float*)(rtm + RtmScale):0.000} " +
                          $"rezo=({*(float*)(rtm + RtmScaleX):0.000},{*(float*)(rtm + RtmScaleY):0.000})");
        }

        if (cfg != null)
        {
            this.log.Info($"  CFG: upscaleType={cfg[CfgUpscaleType]} ({(cfg[CfgUpscaleType] == 2 ? "DLSS" : cfg[CfgUpscaleType] == 1 ? "FSR" : "?")}) " +
                          $"dlssModeByte={cfg[CfgDlssMode]} dynEnable={cfg[CfgDynEnable]} dynActive={cfg[CfgDynActive]} " +
                          $"dynThreshold={cfg[CfgDynThreshold]} rezoScale={*(float*)(cfg + CfgRezoScale):0.00} radialBlur={cfg[CfgRadialBlur]} " +
                          $"depthOfField={cfg[CfgDepthOfField]} | plugin: {this.DescribeSettings()}");
        }
    }

    // ------------------------------------------------------------------ helpers

    public float CustomScale => Math.Clamp(this.config.CustomScalePercent, 33f, 100f) / 100f;

    private byte EffectiveModeCode()
    {
        if (this.config.Mode != DlssMode.Custom)
            return (byte)this.config.Mode;

        // pick the NGX quality mode closest to the custom ratio (never UltraQuality: unsupported by DLSS 3.5+)
        var s = this.CustomScale;
        if (s >= 0.95f) return (byte)DlssMode.Dlaa;
        if (s >= 0.62f) return (byte)DlssMode.Quality;
        if (s >= 0.54f) return (byte)DlssMode.Balanced;
        if (s >= 0.42f) return (byte)DlssMode.Performance;
        return (byte)DlssMode.UltraPerformance;
    }

    public string DescribeSettings()
    {
        if (!this.config.Enabled)
            return "disabled";
        var mode = this.config.Mode == DlssMode.Custom ? $"Custom {this.config.CustomScalePercent:0.#}%" : this.config.Mode.ToString();
        return $"mode={mode} lock={(this.config.LockResolution ? "on" : "off")} preset={this.config.Preset} mv={this.config.MvScale} sharp={this.config.Sharpness:0.00}";
    }

    private void LogConfig(string reason) => this.log.Info($"settings ({reason}): {this.DescribeSettings()}");

    /// <param name="rebuild">false for settings that apply per frame (motion-vector scale).</param>
    public void OnSettingsChanged(bool rebuild = true)
    {
        this.LogConfig("changed");
        if (!rebuild)
            return;
        this.lastSelectLine = this.lastRangeLine = string.Empty;
        this.forceRecreate = true;
        this.ApplyNow("settings changed");
    }

    private void LogEnvironment()
    {
        try
        {
            this.log.Info($"pointers: GraphicsConfig=0x{(nint)GraphicsConfig.Instance():X} RenderTargetManager=0x{(nint)RenderTargetManager.Instance():X} " +
                          $"Render.Manager=0x{(nint)RenderManager.Instance():X} Device=0x{(nint)Device.Instance():X}");
            var rm = (byte*)RenderManager.Instance();
            if (rm != null)
                this.log.Info($"Render.Manager+0x4220 (DlssManager) = 0x{*(nint*)(rm + 0x4220):X}");
        }
        catch (Exception e)
        {
            this.log.Warn($"pointer probe failed: {e.Message}");
        }

        this.LogModules();
    }

    private void LogModules()
    {
        try
        {
            foreach (ProcessModule m in Process.GetCurrentProcess().Modules)
            {
                var name = m.ModuleName.ToLowerInvariant();
                if (name is "nvngx_dlss.dll" or "nvngx_dlssnr.dll" or "_nvngx.dll" or "dxgi.dll" or "nvngx.dll" ||
                    name.EndsWith(".addon64") || name.Contains("reshade"))
                {
                    var v = m.FileVersionInfo;
                    this.log.Info($"module {m.ModuleName}: {v.FileVersion} {v.ProductName} @ {m.FileName}");
                }
            }
        }
        catch (Exception e)
        {
            this.log.Warn($"module probe failed: {e.Message}");
        }
    }

    private static string ReadGameVersion(string exePath)
    {
        try
        {
            var ver = Path.Combine(Path.GetDirectoryName(exePath)!, "ffxivgame.ver");
            return File.Exists(ver) ? File.ReadAllText(ver).Trim() : "?";
        }
        catch
        {
            return "?";
        }
    }

    private static string Dim(uint* p) => p == null ? "null" : $"{p[0]}x{p[1]}";

    public static string PerfQualityName(int v) => v switch
    {
        0 => "Performance",
        1 => "Balanced",
        2 => "Quality",
        3 => "UltraPerformance",
        4 => "UltraQuality",
        5 => "DLAA",
        _ => $"#{v}",
    };

    private static string PresetName(int v) => v switch
    {
        0 => "Default",
        >= 1 and <= 15 => ((char)('A' + v - 1)).ToString(),
        _ => $"#{v}",
    };

    public void Dispose()
    {
        this.selectHook?.Dispose();
        this.rangeHook?.Dispose();
        this.createHook?.Dispose();
        this.evalHook?.Dispose();

        // let an in-flight render-thread evaluate finish before freeing the sharpening resources
        Thread.Sleep(100);
        this.sharpener.Dispose();
        this.probe.Dispose();

        try
        {
            var cfg = (byte*)GraphicsConfig.Instance();
            if (cfg != null && this.originalModeByte.HasValue)
                cfg[CfgDlssMode] = this.originalModeByte.Value;
            if (this.dlssManager != 0)
                *(byte*)(this.dlssManager + DlssUseRezoScale) = 0;
            this.RequestResolutionChange("plugin unloaded, restoring game behaviour");
        }
        catch (Exception e)
        {
            this.log.Warn($"restore on dispose failed: {e.Message}");
        }

        this.log.Info("==== Frame Perfect stopped");
    }
}

internal sealed class DlssStatus
{
    public bool HooksOk { get; init; }
    public double EvalsPerSecond { get; init; }
    public long TotalEvals { get; init; }
    public uint RenderW { get; init; }
    public uint RenderH { get; init; }
    public uint OutputW { get; init; }
    public uint OutputH { get; init; }
    public bool Stable { get; init; }
    public byte UpscaleType { get; init; }
    public byte ModeByte { get; init; }
    public string NgxMode { get; init; } = "?";
    public ushort RangeMinH { get; init; }
    public ushort RangeMaxH { get; init; }
    public bool RadialBlur { get; init; }
    public bool DepthOfField { get; init; }
}
