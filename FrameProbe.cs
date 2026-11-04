using System;
using System.Diagnostics;
using System.Threading.Tasks;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace XivDlssFix;

/// <summary>
/// Flash detector: measures the mean brightness of the final 3D image (DLSS output, after DLSS 5 and the
/// sharpening, before the UI) every frame on the GPU, reads it back a few frames later without stalling,
/// and logs every short light/dark blink ("FLASH") and every long frame ("HITCH") with its time, so
/// flicker can be judged from the log instead of by eye.
///
/// A blink is a deviation of at least <see cref="FlashStops"/> from both neighbours that comes back within
/// 1-3 frames; a camera turn changes brightness steadily and does not count.
/// </summary>
internal sealed unsafe class FrameProbe : IDisposable
{
    // One group per screen tile: 8x8 samples spread over the tile -> (mean luminance, mean log2 luminance).
    private const string Hlsl = @"
Texture2D<float4> Src : register(t0);
RWTexture2D<float2> Dst : register(u0);
cbuffer P : register(b0) { uint W; uint H; uint TX; uint TY; };
groupshared float2 acc[64];

[numthreads(8, 8, 1)]
void main(uint3 g : SV_GroupID, uint3 t : SV_GroupThreadID, uint gi : SV_GroupIndex)
{
    uint sx = g.x * 8 + t.x;
    uint sy = g.y * 8 + t.y;
    uint2 p = uint2(((2 * sx + 1) * W) / (16 * TX), ((2 * sy + 1) * H) / (16 * TY));
    float3 c = max(Src.Load(int3(p, 0)).rgb, 0.0);
    float l = dot(c, float3(0.2126, 0.7152, 0.0722));
    acc[gi] = float2(l, log2(max(l, 1.0 / 4096.0)));
    GroupMemoryBarrierWithGroupSync();
    [unroll] for (uint s = 32; s > 0; s >>= 1)
    {
        if (gi < s) acc[gi] += acc[gi + s];
        GroupMemoryBarrierWithGroupSync();
    }
    if (gi == 0)
        Dst[g.xy] = acc[0] / 64.0;
}";

    private const uint TilesX = 64;
    private const uint TilesY = 36;
    private const int Tiles = (int)(TilesX * TilesY);

    /// <summary>A tile 'reverses' when it changes by at least this much one frame and back the next.</summary>
    public const double ReversalStops = 0.10;

    private const int Ring = 6;
    private const int History = 8;

    /// <summary>Minimum blink size, in stops (0.08 st ~ 6% of the whole image's brightness).</summary>
    public const double FlashStops = 0.08;

    private const int MaxFlashLinesPerWindow = 30;
    private const int WasStillDrawing = unchecked((int)0x887A000A); // DXGI_ERROR_WAS_STILL_DRAWING

    private readonly FileLog log;
    private readonly Task<byte[]> bytecode;
    private readonly object gate = new();

    private ID3D11Device* device;
    private ID3D11ComputeShader* shader;
    private ID3D11Buffer* constants;
    private ID3D11Texture2D* result;
    private ID3D11UnorderedAccessView* resultUav;
    private readonly ID3D11Texture2D*[] staging = new ID3D11Texture2D*[Ring];
    private readonly long[] slotFrame = new long[Ring];
    private readonly DateTime[] slotTime = new DateTime[Ring];
    private readonly float[] slotFrameMs = new float[Ring];
    private readonly bool[] slotPending = new bool[Ring];
    private ID3D11ShaderResourceView* outputSrv;
    private ID3D11Resource* output;
    private uint width, height;
    private bool disabled;
    private int errors;

    private long frame;
    private long lastOutputChangeFrame = -1000;
    private long lastTick;
    private double emaFrameMs;

    // readback history (render thread only)
    private readonly double[] hist = new double[History];
    private readonly long[] histFrame = new long[History];
    private readonly DateTime[] histTime = new DateTime[History];
    private readonly float[] histFrameMs = new float[History];
    private int histCount;
    private long lastSampleFrame = -1;
    private int cooldown;

    // per-tile history (render thread only)
    private float[] tileCur = new float[Tiles];
    private float[] tilePrev1 = new float[Tiles];
    private float[] tilePrev2 = new float[Tiles];
    private int tileHistory;
    private long lastTileFrame = -1;

    // window statistics (guarded by gate)
    private long wSamples, wLost, wFlashes, wHitches, wSteps, wOver;
    private double wMaxDelta, wSumSq;
    private int wFlashLines;
    private double lastMean, lastLogMean;
    private long lFrames, lReversals;
    private double lMotion, lAccel;
    private int lMaxTiles;

    // frame pacing (what a G-Sync/VRR display follows)
    private readonly System.Collections.Generic.List<float> frameTimes = new(1024);
    private long frameSwings;
    private float prevFrameMs;

    public FrameProbe(FileLog log)
    {
        this.log = log;
        this.bytecode = Task.Run(() => D3DUtil.CompileCompute(Hlsl));
    }

    public string State { get; private set; } = "waiting for DLSS";

    public long TotalFlashes { get; private set; }

    public string LastFlash { get; private set; } = "none";

    /// <summary>Strength the sharpening actually applied this frame (for the FLASH context).</summary>
    public float SharpenNow { get; set; }

    /// <summary>Render thread, right after the DLSS evaluate (and the sharpening).</summary>
    public void Measure(nint gameOutputTexture)
    {
        var now = Stopwatch.GetTimestamp();
        var frameMs = this.lastTick == 0 ? 0f : (float)((now - this.lastTick) * 1000.0 / Stopwatch.Frequency);
        this.lastTick = now;
        this.frame++;
        this.TrackFrameTime(frameMs);

        if (this.disabled || gameOutputTexture == 0 || !this.bytecode.IsCompleted)
            return;

        ID3D11Resource* res = null;
        ID3D11DeviceContext* ctx = null;
        try
        {
            res = D3DUtil.ResolveResource(gameOutputTexture);
            if (res == null || !this.EnsureResources(res))
                return;

            this.device->GetImmediateContext(&ctx);
            if (ctx == null)
                return;

            var state = default(D3DUtil.ComputeState);
            state.Save(ctx);
            ctx->CSSetShader(this.shader, null, 0);
            var srv = this.outputSrv;
            ctx->CSSetShaderResources(0, 1, &srv);
            var uav = this.resultUav;
            ctx->CSSetUnorderedAccessViews(0, 1, &uav, null);
            var cb = this.constants;
            ctx->CSSetConstantBuffers(0, 1, &cb);
            ctx->Dispatch(TilesX, TilesY, 1);
            state.Restore(ctx);

            // read back every finished slot, oldest first (slot w was written Ring frames ago), without waiting
            var w = (int)(this.frame % Ring);
            for (var k = 0; k < Ring; k++)
            {
                var r = (w + k) % Ring;
                if (!this.slotPending[r])
                    continue;
                D3D11_MAPPED_SUBRESOURCE map;
                var hr = ctx->Map((ID3D11Resource*)this.staging[r], 0, D3D11_MAP.D3D11_MAP_READ, (uint)D3D11_MAP_FLAG.D3D11_MAP_FLAG_DO_NOT_WAIT, &map);
                if (hr.Value == WasStillDrawing)
                    break;
                if (FAILED(hr))
                {
                    this.slotPending[r] = false;
                    continue;
                }

                double sumMean = 0, sumLog = 0;
                for (var y = 0; y < TilesY; y++)
                {
                    var row = (float*)((byte*)map.pData + (y * map.RowPitch));
                    for (var x = 0; x < TilesX; x++)
                    {
                        sumMean += row[2 * x];
                        var lg = row[(2 * x) + 1];
                        sumLog += lg;
                        this.tileCur[(y * TilesX) + x] = lg;
                    }
                }

                ctx->Unmap((ID3D11Resource*)this.staging[r], 0);
                this.slotPending[r] = false;
                this.OnTiles(this.slotFrame[r]);
                this.OnSample(this.slotFrame[r], this.slotTime[r], this.slotFrameMs[r], (float)(sumMean / Tiles), (float)(sumLog / Tiles));
            }

            if (this.slotPending[w])
            {
                lock (this.gate) this.wLost++;
            }

            ctx->CopyResource((ID3D11Resource*)this.staging[w], (ID3D11Resource*)this.result);
            this.slotPending[w] = true;
            this.slotFrame[w] = this.frame;
            this.slotTime[w] = DateTime.Now;
            this.slotFrameMs[w] = frameMs;
            this.State = $"active ({TilesX}x{TilesY} tiles)";
        }
        catch (Exception e)
        {
            this.Fail(e.Message);
        }
        finally
        {
            if (ctx != null) ctx->Release();
            if (res != null) res->Release();
        }
    }

    private void TrackFrameTime(float frameMs)
    {
        if (frameMs <= 0)
            return;

        lock (this.gate)
        {
            if (this.frameTimes.Count < 4096)
                this.frameTimes.Add(frameMs);
            if (this.prevFrameMs > 0 && Math.Abs(frameMs - this.prevFrameMs) > 0.15f * this.prevFrameMs)
                this.frameSwings++;
            this.prevFrameMs = frameMs;
        }

        // Only judge hitches while the game runs smoothly (not in the background / menus at 15-30 fps).
        if (this.emaFrameMs > 0 && this.emaFrameMs < 25 && frameMs > 50 && frameMs > 3 * this.emaFrameMs)
        {
            lock (this.gate)
            {
                this.wHitches++;
                if (this.wFlashLines++ < MaxFlashLinesPerWindow)
                    this.log.Enqueue($"HITCH: frame took {frameMs:0} ms (normal ~{this.emaFrameMs:0.0} ms)");
            }
        }

        // a single 1 s pause must not reset the baseline
        this.emaFrameMs = this.emaFrameMs <= 0 ? frameMs : this.emaFrameMs + (0.05 * (Math.Min(frameMs, 100) - this.emaFrameMs));
    }

    /// <summary>Local flicker: per tile, how much it moves (camera motion), its second difference, and how many
    /// tiles brighten-then-darken (or the reverse) by at least <see cref="ReversalStops"/> on consecutive frames.</summary>
    private void OnTiles(long f)
    {
        if (f != this.lastTileFrame + 1)
            this.tileHistory = 0;
        this.lastTileFrame = f;

        if (this.tileHistory >= 2)
        {
            double motion = 0, accel = 0;
            var reversing = 0;
            for (var i = 0; i < Tiles; i++)
            {
                var d1 = this.tileCur[i] - this.tilePrev1[i];
                var d0 = this.tilePrev1[i] - this.tilePrev2[i];
                motion += Math.Abs(d1);
                accel += Math.Abs(d1 - d0);
                if (Math.Abs(d1) >= ReversalStops && Math.Abs(d0) >= ReversalStops && (d1 > 0) != (d0 > 0))
                    reversing++;
            }

            lock (this.gate)
            {
                this.lFrames++;
                this.lMotion += motion / Tiles;
                this.lAccel += accel / Tiles;
                this.lReversals += reversing;
                this.lMaxTiles = Math.Max(this.lMaxTiles, reversing);
            }
        }

        // rotate: prev2 <- prev1 <- cur
        (this.tilePrev2, this.tilePrev1, this.tileCur) = (this.tilePrev1, this.tileCur, this.tilePrev2);
        if (this.tileHistory < 2)
            this.tileHistory++;
    }

    /// <summary>Reversing tiles per 1000 tile-frames in the current window (for the UI).</summary>
    public double LastLocalFlicker { get; private set; }

    private void OnSample(long f, DateTime t, float frameMs, float mean, float logMean)
    {
        if (f != this.lastSampleFrame + 1)
            this.histCount = 0; // gap: don't compare across it
        this.lastSampleFrame = f;

        var idx = this.histCount < History ? this.histCount++ : this.Shift();
        this.hist[idx] = logMean;
        this.histFrame[idx] = f;
        this.histTime[idx] = t;
        this.histFrameMs[idx] = frameMs;

        lock (this.gate)
        {
            this.wSamples++;
            this.lastMean = mean;
            this.lastLogMean = logMean;
            if (this.histCount >= 2)
            {
                var d = Math.Abs(this.hist[this.histCount - 1] - this.hist[this.histCount - 2]);
                this.wMaxDelta = Math.Max(this.wMaxDelta, d);
                this.wSumSq += d * d;
                if (d >= 0.05)
                    this.wOver++;
            }
        }

        if (this.cooldown > 0)
        {
            this.cooldown--;
            return;
        }

        this.DetectBlink();
    }

    private int Shift()
    {
        Array.Copy(this.hist, 1, this.hist, 0, History - 1);
        Array.Copy(this.histFrame, 1, this.histFrame, 0, History - 1);
        Array.Copy(this.histTime, 1, this.histTime, 0, History - 1);
        Array.Copy(this.histFrameMs, 1, this.histFrameMs, 0, History - 1);
        return History - 1;
    }

    private void DetectBlink()
    {
        var n = this.histCount - 1;
        for (var k = 2; k <= 4 && n - k >= 0; k++)
        {
            var a = this.hist[n - k];
            var b = this.hist[n];
            if (Math.Abs(b - a) > FlashStops / 2)
                continue; // didn't come back: a step, not a blink

            var baseline = (a + b) / 2;
            double peak = 0;
            var sign = 0;
            var ok = true;
            for (var i = n - k + 1; i < n; i++)
            {
                var dev = this.hist[i] - baseline;
                var s = Math.Sign(dev);
                if (Math.Abs(dev) < FlashStops || (sign != 0 && s != sign))
                {
                    ok = false;
                    break;
                }

                sign = s;
                if (Math.Abs(dev) > Math.Abs(peak))
                    peak = dev;
            }

            if (!ok)
                continue;

            var first = n - k + 1;
            var frames = k - 1;
            var hitchNear = false;
            for (var i = first; i <= n; i++)
                hitchNear |= this.histFrameMs[i] > 50;
            var sinceRebuild = this.histFrame[first] - this.lastOutputChangeFrame;
            var context = $"sharpen={this.SharpenNow * 100:0}%" +
                          (hitchNear ? " | next to a HITCH" : string.Empty) +
                          (sinceRebuild is >= 0 and < 30 ? $" | {sinceRebuild} frames after the game rebuilt its render targets" : string.Empty);
            var msg = $"FLASH: {(peak > 0 ? "brighter" : "darker")} by {Math.Abs(peak):0.00} st for {frames} frame(s) " +
                      $"(base {baseline:0.00} log2) | {context}";
            this.TotalFlashes++;
            this.LastFlash = $"{this.histTime[first]:HH:mm:ss} {(peak > 0 ? "+" : "-")}{Math.Abs(peak):0.00} st";
            lock (this.gate)
            {
                this.wFlashes++;
                if (this.wFlashLines++ < MaxFlashLinesPerWindow)
                    this.log.EnqueueAt(this.histTime[first], msg);
            }

            this.cooldown = k;
            return;
        }

        // a large one-way jump (scene cut, menu) is counted separately
        if (n >= 1 && Math.Abs(this.hist[n] - this.hist[n - 1]) >= 0.5)
        {
            lock (this.gate) this.wSteps++;
        }
    }

    public string TakeStatsLine()
    {
        lock (this.gate)
        {
            var rms = this.wSamples > 1 ? Math.Sqrt(this.wSumSq / (this.wSamples - 1)) : 0;
            var line = $"FLASH probe: {this.State} | samples={this.wSamples} lost={this.wLost} | brightness={this.lastMean:0.000} ({this.lastLogMean:0.00} log2) | " +
                       $"frame-to-frame max={this.wMaxDelta:0.000} st rms={rms:0.0000} st >=0.05st:{this.wOver} | blinks={this.wFlashes} jumps={this.wSteps} hitches={this.wHitches} | total blinks={this.TotalFlashes}";
            var perMille = this.lFrames > 0 ? 1000.0 * this.lReversals / (this.lFrames * (double)Tiles) : 0;
            this.LastLocalFlicker = perMille;
            line += $"\n             LOCAL flicker: frames={this.lFrames} motion={(this.lFrames > 0 ? this.lMotion / this.lFrames : 0):0.0000} st/frame " +
                    $"accel={(this.lFrames > 0 ? this.lAccel / this.lFrames : 0):0.0000} st | reversing tiles={perMille:0.00} per 1000 (max {this.lMaxTiles} of {Tiles} in one frame)";
            if (this.frameTimes.Count > 1)
            {
                this.frameTimes.Sort();
                float P(double q) => this.frameTimes[Math.Min(this.frameTimes.Count - 1, (int)(q * this.frameTimes.Count))];
                var n = this.frameTimes.Count;
                line += $"\n             FRAMES (pacing seen by G-Sync): n={n} ms p05={P(0.05):0.0} p50={P(0.5):0.0} p95={P(0.95):0.0} p99={P(0.99):0.0} | " +
                        $"swings >15% frame-to-frame={100.0 * this.frameSwings / (n - 1):0.0}% | ~{1000.0 / P(0.5):0} Hz typical";
            }

            this.frameTimes.Clear();
            this.frameSwings = 0;
            this.wSamples = this.wLost = this.wFlashes = this.wHitches = this.wSteps = this.wOver = 0;
            this.wMaxDelta = this.wSumSq = 0;
            this.wFlashLines = 0;
            this.lFrames = this.lReversals = 0;
            this.lMotion = this.lAccel = 0;
            this.lMaxTiles = 0;
            return line;
        }
    }

    private bool EnsureResources(ID3D11Resource* res)
    {
        if (this.output == res && this.outputSrv != null)
            return true;

        ID3D11Texture2D* tex = null;
        if (FAILED(res->QueryInterface(__uuidof<ID3D11Texture2D>(), (void**)&tex)) || tex == null)
            return this.Fail("DLSS output is not a Texture2D");
        D3D11_TEXTURE2D_DESC desc;
        tex->GetDesc(&desc);
        tex->Release();
        if ((desc.BindFlags & (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE) == 0)
            return this.Fail($"DLSS output has no SRV bind flag (bind=0x{desc.BindFlags:X})");

        if (this.outputSrv != null) { this.outputSrv->Release(); this.outputSrv = null; }
        if (this.output != null) { this.output->Release(); this.output = null; }

        if (this.device == null && !this.CreateDeviceObjects(res))
            return false;

        ID3D11ShaderResourceView* s = null;
        var hr = this.device->CreateShaderResourceView(res, null, &s);
        if (FAILED(hr))
            return this.Fail($"CreateShaderResourceView(output) failed 0x{hr.Value:X8}");
        this.outputSrv = s;
        res->AddRef();
        this.output = res;
        this.width = desc.Width;
        this.height = desc.Height;
        this.lastOutputChangeFrame = this.frame;
        this.histCount = 0;

        ID3D11DeviceContext* ctx = null;
        this.device->GetImmediateContext(&ctx);
        if (ctx != null)
        {
            var p = stackalloc uint[4] { this.width, this.height, TilesX, TilesY };
            ctx->UpdateSubresource((ID3D11Resource*)this.constants, 0, null, p, 0, 0);
            ctx->Release();
        }

        return true;
    }

    private bool CreateDeviceObjects(ID3D11Resource* res)
    {
        ID3D11Device* dev = null;
        res->GetDevice(&dev);
        if (dev == null)
            return this.Fail("GetDevice returned null");
        this.device = dev;
        try
        {
            this.shader = D3DUtil.CreateCompute(dev, this.bytecode.Result);
            this.constants = D3DUtil.CreateConstants(dev, 16);
        }
        catch (Exception e)
        {
            return this.Fail(e.InnerException?.Message ?? e.Message);
        }

        var td = new D3D11_TEXTURE2D_DESC
        {
            Width = TilesX,
            Height = TilesY,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_R32G32_FLOAT,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_UNORDERED_ACCESS,
        };
        ID3D11Texture2D* t = null;
        var hr = dev->CreateTexture2D(&td, null, &t);
        if (FAILED(hr))
            return this.Fail($"CreateTexture2D(result) failed 0x{hr.Value:X8}");
        this.result = t;
        ID3D11UnorderedAccessView* u = null;
        hr = dev->CreateUnorderedAccessView((ID3D11Resource*)t, null, &u);
        if (FAILED(hr))
            return this.Fail($"CreateUnorderedAccessView(result) failed 0x{hr.Value:X8}");
        this.resultUav = u;

        td.Usage = D3D11_USAGE.D3D11_USAGE_STAGING;
        td.BindFlags = 0;
        td.CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ;
        for (var i = 0; i < Ring; i++)
        {
            ID3D11Texture2D* st = null;
            hr = dev->CreateTexture2D(&td, null, &st);
            if (FAILED(hr))
                return this.Fail($"CreateTexture2D(staging) failed 0x{hr.Value:X8}");
            this.staging[i] = st;
        }

        this.log.Enqueue($"flash probe: brightness probe created ({TilesX}x{TilesY} tiles x 64 samples per frame, async readback)");
        return true;
    }

    private bool Fail(string why)
    {
        this.errors++;
        this.log.Enqueue($"flash probe ERROR: {why}" + (this.errors >= 3 ? " (disabled after 3 errors)" : string.Empty));
        this.State = $"error: {why}";
        if (this.errors >= 3)
            this.disabled = true;
        return false;
    }

    public void Dispose()
    {
        this.disabled = true;
        if (this.outputSrv != null) { this.outputSrv->Release(); this.outputSrv = null; }
        if (this.output != null) { this.output->Release(); this.output = null; }
        for (var i = 0; i < Ring; i++)
            if (this.staging[i] != null) { this.staging[i]->Release(); this.staging[i] = null; }
        if (this.resultUav != null) { this.resultUav->Release(); this.resultUav = null; }
        if (this.result != null) { this.result->Release(); this.result = null; }
        if (this.constants != null) { this.constants->Release(); this.constants = null; }
        if (this.shader != null) { this.shader->Release(); this.shader = null; }
        if (this.device != null) { this.device->Release(); this.device = null; }
    }
}
