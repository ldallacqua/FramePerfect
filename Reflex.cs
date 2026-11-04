using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace XivDlssFix;

/// <summary>
/// NVIDIA Reflex through NVAPI (nvapi64.dll ships with the driver). Works on D3D11.
/// Interface IDs and struct layouts are from NVIDIA's nvapi.h / nvapi_interface.h (github.com/NVIDIA/nvapi).
/// A struct's version is sizeof(struct) | (1 &lt;&lt; 16); a wrong size returns NVAPI_INCOMPATIBLE_STRUCT_VERSION (-9).
/// </summary>
internal sealed unsafe class Reflex : IDisposable
{
    public const int SimulationStart = 0;
    public const int SimulationEnd = 1;
    public const int RenderSubmitStart = 2;
    public const int RenderSubmitEnd = 3;
    public const int PresentStart = 4;
    public const int PresentEnd = 5;

    private const uint IdInitialize = 0x0150E828;
    private const uint IdSetSleepMode = 0xAC1CA9E0;
    private const uint IdGetSleepStatus = 0xAEF96CA1;
    private const uint IdSleep = 0x852CD1D2;
    private const uint IdSetLatencyMarker = 0xD9984C05;
    private const uint IdGetLatency = 0x1A587F9C;

    private const int SleepModeSize = 44;
    private const int SleepStatusSize = 136;
    private const int MarkerSize = 88;
    private const int LatencyResultSize = 15400;
    private const int FrameReportSize = 240;
    private const int FrameReports = 64;

    private readonly delegate* unmanaged<nint, byte*, int> setSleepMode;
    private readonly delegate* unmanaged<nint, byte*, int> getSleepStatus;
    private readonly delegate* unmanaged<nint, int> sleep;
    private readonly delegate* unmanaged<nint, byte*, int> setMarker;
    private readonly delegate* unmanaged<nint, byte*, int> getLatency;
    private readonly byte* latencyBuffer;
    private nint device; // AddRef'd ID3D11Device (IUnknown) handed to every call

    private Reflex(nint device, nint qi)
    {
        this.device = device;
        var query = (delegate* unmanaged<uint, nint>)qi;
        this.setSleepMode = (delegate* unmanaged<nint, byte*, int>)query(IdSetSleepMode);
        this.getSleepStatus = (delegate* unmanaged<nint, byte*, int>)query(IdGetSleepStatus);
        this.sleep = (delegate* unmanaged<nint, int>)query(IdSleep);
        this.setMarker = (delegate* unmanaged<nint, byte*, int>)query(IdSetLatencyMarker);
        this.getLatency = (delegate* unmanaged<nint, byte*, int>)query(IdGetLatency);
        this.latencyBuffer = (byte*)NativeMemory.AllocZeroed(LatencyResultSize);
    }

    public int LastSleepStatus { get; private set; }

    public int LastMarkerStatus { get; private set; }

    /// <summary>Loads NVAPI and resolves the Reflex entry points. <paramref name="d3d11Device"/> must be AddRef'd;
    /// the returned object owns that reference.</summary>
    public static Reflex? TryCreate(nint d3d11Device, out string error)
    {
        error = string.Empty;
        if (d3d11Device == 0)
        {
            error = "no D3D11 device";
            return null;
        }

        if (!NativeLibrary.TryLoad("nvapi64.dll", out var lib))
        {
            error = "nvapi64.dll not found (not an NVIDIA driver?)";
            return null;
        }

        if (!NativeLibrary.TryGetExport(lib, "nvapi_QueryInterface", out var qi))
        {
            error = "nvapi_QueryInterface export missing";
            return null;
        }

        var query = (delegate* unmanaged<uint, nint>)qi;
        var init = (delegate* unmanaged<int>)query(IdInitialize);
        if (init == null)
        {
            error = "NvAPI_Initialize missing";
            return null;
        }

        var st = init();
        if (st != 0)
        {
            error = $"NvAPI_Initialize returned {st}";
            return null;
        }

        foreach (var id in new[] { IdSetSleepMode, IdGetSleepStatus, IdSleep, IdSetLatencyMarker, IdGetLatency })
        {
            if (query(id) == 0)
            {
                error = $"NVAPI function 0x{id:X8} missing (driver too old for Reflex?)";
                return null;
            }
        }

        return new Reflex(d3d11Device, qi);
    }

    /// <summary>NvAPI_D3D_SetSleepMode. The settings persist on the device, so only call this when they change.</summary>
    public int SetSleepMode(bool lowLatency, bool boost, uint minimumIntervalUs)
    {
        var p = stackalloc byte[SleepModeSize];
        new Span<byte>(p, SleepModeSize).Clear();
        *(uint*)p = SleepModeSize | (1u << 16);
        p[4] = (byte)(lowLatency ? 1 : 0);
        p[5] = (byte)(boost ? 1 : 0);
        *(uint*)(p + 8) = minimumIntervalUs;
        return this.setSleepMode(this.device, p);
    }

    /// <summary>NvAPI_D3D_Sleep: call exactly once per frame, at the start of the frame (before input/simulation).</summary>
    public int Sleep()
    {
        var st = this.sleep(this.device);
        this.LastSleepStatus = st;
        return st;
    }

    public void Marker(ulong frameId, int type)
    {
        var p = stackalloc byte[MarkerSize];
        new Span<byte>(p, MarkerSize).Clear();
        *(uint*)p = MarkerSize | (1u << 16);
        *(ulong*)(p + 8) = frameId;
        *(int*)(p + 16) = type;
        this.LastMarkerStatus = this.setMarker(this.device, p);
    }

    public readonly record struct SleepStatus(int Result, bool LowLatency, bool FullscreenVrr, bool CplVsyncOn, uint SleepIntervalUs, bool UseGameSleep, bool FullscreenIFlip, byte FgMultiplier);

    public SleepStatus GetSleepStatus()
    {
        var p = stackalloc byte[SleepStatusSize];
        new Span<byte>(p, SleepStatusSize).Clear();
        *(uint*)p = SleepStatusSize | (1u << 16);
        var st = this.getSleepStatus(this.device, p);
        return new SleepStatus(st, p[4] != 0, p[5] != 0, p[6] != 0, *(uint*)(p + 8), p[12] != 0, p[13] != 0, p[14]); // p[14] = fgMultiplier: latest frame generation multiplier the driver used
    }

    public readonly record struct FrameReport(ulong FrameId, ulong SimStart, ulong PresentStart, ulong PresentEnd, ulong DriverStart, ulong OsQueueStart, ulong GpuRenderStart, ulong GpuRenderEnd, uint GpuActiveUs, uint GpuFrameUs);

    /// <summary>NvAPI_D3D_GetLatency: the last 64 frames (needs markers with increasing frame IDs; all zero until ~90 frames).</summary>
    public int GetLatency(List<FrameReport> into)
    {
        into.Clear();
        var p = this.latencyBuffer;
        new Span<byte>(p, LatencyResultSize).Clear();
        *(uint*)p = LatencyResultSize | (1u << 16);
        var st = this.getLatency(this.device, p);
        if (st != 0)
            return st;
        for (var i = 0; i < FrameReports; i++)
        {
            var r = (ulong*)(p + 8 + (i * FrameReportSize));
            if (r[0] == 0)
                continue;
            into.Add(new FrameReport(r[0], r[2], r[6], r[7], r[8], r[10], r[12], r[13], *(uint*)(r + 14), *((uint*)(r + 14) + 1)));
        }

        return 0;
    }

    public void Dispose()
    {
        try
        {
            this.SetSleepMode(false, false, 0);
        }
        catch
        {
            // best effort
        }

        if (this.device != 0)
        {
            Marshal.Release(this.device);
            this.device = 0;
        }

        NativeMemory.Free(this.latencyBuffer);
    }
}
