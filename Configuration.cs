using System;
using Dalamud.Configuration;

namespace XivDlssFix;

/// <summary>DLSS quality mode. Values 1..6 are the game's own GraphicsConfig+0x55 codes.</summary>
public enum DlssMode
{
    /// <summary>Don't touch anything; only observe and log (the game's dynamic behaviour).</summary>
    GameDefault = 0,
    Dlaa = 1,
    Quality = 3,
    Balanced = 4,
    Performance = 5,
    UltraPerformance = 6,
    /// <summary>Arbitrary percentage of the output resolution.</summary>
    Custom = 100,
}

/// <summary>NVSDK_NGX_DLSS_Hint_Render_Preset values passed when the DLSS feature is created.</summary>
public enum PresetOverride
{
    /// <summary>Keep what the game asks for (it hard-codes preset C).</summary>
    Game = -1,
    /// <summary>Let the loaded nvngx_dlss.dll pick its recommended model for the mode.</summary>
    NvidiaDefault = 0,
    F = 6,
    J = 10,
    K = 11,
    L = 12,
    M = 13,
}

/// <summary>What the game passes as DLSS InMVScaleX/Y.</summary>
public enum MotionVectorScale
{
    /// <summary>Keep the game's value: the display (swapchain) size, even when rendering below 100%.</summary>
    Game = 0,
    /// <summary>Use the render sub-rectangle size (correct if the game's vectors are viewport-relative).</summary>
    RenderSize = 1,
}

/// <summary>NVIDIA Reflex low-latency mode (NvAPI_D3D_SetSleepMode).</summary>
public enum ReflexMode
{
    Off = 0,
    On = 1,
    /// <summary>On, and keep GPU clocks up even when the GPU is mostly idle.</summary>
    Boost = 2,
}

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool Enabled { get; set; } = true;

    public DlssMode Mode { get; set; } = DlssMode.Balanced;

    /// <summary>Used when <see cref="Mode"/> is Custom. Percent of the output height/width (33..100).</summary>
    public float CustomScalePercent { get; set; } = 66.7f;

    /// <summary>Clamp the game's dynamic-resolution range to a single size (min = max = target).</summary>
    public bool LockResolution { get; set; } = true;

    public PresetOverride Preset { get; set; } = PresetOverride.NvidiaDefault;

    /// <summary>Diagnostic only. Test 2 (2026-11-04) showed the game's own value (display size) is the right one.</summary>
    public MotionVectorScale MvScale { get; set; } = MotionVectorScale.Game;

    /// <summary>CAS sharpening applied to the DLSS output (0 = off, 1 = strongest).</summary>
    public float Sharpness { get; set; } = 0f;

    /// <summary>Measure the brightness of every frame and log short light/dark blinks (FLASH lines).</summary>
    public bool FlashDetector { get; set; } = true;

    /// <summary>Tell in chat, after login and when it changes, whether the game's graphics options are right for the plugin.</summary>
    public bool ChatNotices { get; set; } = true;

    public ReflexMode Reflex { get; set; } = ReflexMode.Off;

    /// <summary>Driver frame cap through Reflex (NvAPI minimum frame interval), in fps. 0 = off.</summary>
    public float FrameCapFps { get; set; } = 0f;

    /// <summary>Give the game the refresh rate of the monitor its window is on (it reads the primary monitor once at startup).</summary>
    public bool FixRefreshRate { get; set; } = true;

    /// <summary>How often the periodic DLSS statistics line is written to the log.</summary>
    public int StatsIntervalSeconds { get; set; } = 5;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
