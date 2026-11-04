using System;
using System.Diagnostics;
using System.Threading.Tasks;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace FramePerfect;

/// <summary>
/// Contrast Adaptive Sharpening (AMD CAS) run on the DLSS output texture right after the game's DLSS
/// evaluate, i.e. on the 3D scene before the UI is drawn. DLSS itself ignores its sharpness parameter
/// since 2.5.1, so this replaces it. Runs on the render thread, on the device's immediate context,
/// saving and restoring every compute-stage binding it touches.
///
/// v4: the shader is compiled on a worker thread at plugin start (no render-thread stall on first use),
/// and the strength glides to the slider value instead of jumping (no pop when it is switched on/off).
/// </summary>
internal sealed unsafe class Sharpener : IDisposable
{
    private const string Hlsl = @"
Texture2D<float4> Src : register(t0);
RWTexture2D<float4> Dst : register(u0);
cbuffer Params : register(b0) { float Sharp; float Contrast; uint W; uint H; };

float3 L(int2 p) { return Src.Load(int3(clamp(p, int2(0, 0), int2(W - 1, H - 1)), 0)).rgb; }

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= W || id.y >= H) return;
    int2 p = int2(id.xy);
    float4 e4 = Src.Load(int3(p, 0));
    float3 a = L(p + int2(-1, -1)), b = L(p + int2(0, -1)), c = L(p + int2(1, -1));
    float3 d = L(p + int2(-1,  0)), e = e4.rgb,              f = L(p + int2(1,  0));
    float3 g = L(p + int2(-1,  1)), h = L(p + int2(0,  1)), i = L(p + int2(1,  1));

    // CAS adapts on [0,1]; only the copies used to compute the amount are clamped.
    float3 sa = saturate(a), sb = saturate(b), sc = saturate(c), sd = saturate(d), se = saturate(e);
    float3 sf = saturate(f), sg = saturate(g), sh = saturate(h), si = saturate(i);
    float3 mn = min(min(min(sd, se), min(sf, sb)), sh);
    mn += min(mn, min(min(sa, sc), min(sg, si)));
    float3 mx = max(max(max(sd, se), max(sf, sb)), sh);
    mx += max(mx, max(max(sa, sc), max(sg, si)));
    float3 amp = saturate(min(mn, 2.0 - mx) / max(mx, 1e-5));
    amp = rsqrt(max(amp, 1e-5));
    float peak = -3.0 * Contrast + 8.0;
    float3 w = -1.0 / (amp * peak);
    float3 rcpW = 1.0 / (4.0 * w + 1.0);
    float3 o = max(((b + d + f + h) * w + e) * rcpW, 0.0);
    Dst[p] = float4(lerp(e, o, Sharp), e4.a);
}";

    /// <summary>Strength units per second the applied value moves toward the slider (0 -> 100% in ~0.3 s).</summary>
    private const float RampPerSecond = 3f;

    private readonly FileLog log;
    private readonly Task<byte[]> bytecode;

    private ID3D11Device* device;
    private ID3D11ComputeShader* shader;
    private ID3D11Buffer* constants;
    private ID3D11Texture2D* copy;
    private ID3D11ShaderResourceView* copySrv;
    private ID3D11UnorderedAccessView* outputUav;
    private ID3D11Resource* output; // AddRef'd, to notice when the game recreates its target
    private uint width;
    private uint height;
    private DXGI_FORMAT format;
    private bool disabled;
    private int errors;

    private float current;
    private long lastTick;

    private long applied;
    private double cpuMsTotal;

    public Sharpener(FileLog log)
    {
        this.log = log;
        this.bytecode = Task.Run(() => D3DUtil.CompileCompute(Hlsl));
    }

    public string State { get; private set; } = "off";

    /// <summary>The strength actually applied on the last frame (after the glide).</summary>
    public float Current => this.current;

    /// <summary>Called on the render thread right after the DLSS evaluate, every frame. <paramref name="target"/> is the
    /// slider value (0 = off). <paramref name="gameOutputTexture"/> is the game's Kernel::Texture* for the DLSS output.</summary>
    public void Apply(nint gameOutputTexture, float target)
    {
        var now = Stopwatch.GetTimestamp();
        var dt = this.lastTick == 0 ? 0f : (float)Math.Min(0.1, (now - this.lastTick) / (double)Stopwatch.Frequency);
        this.lastTick = now;
        target = Math.Clamp(target, 0f, 1f);
        var step = RampPerSecond * dt;
        this.current = this.current < target ? Math.Min(target, this.current + step) : Math.Max(target, this.current - step);

        if (this.current <= 0.001f)
        {
            if (this.State.StartsWith("active"))
                this.State = "off";
            return;
        }

        if (this.disabled || gameOutputTexture == 0)
            return;

        if (!this.bytecode.IsCompleted)
        {
            this.State = "compiling shader...";
            return; // never wait on the render thread
        }

        var sw = Stopwatch.StartNew();
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

            var p = stackalloc float[4];
            p[0] = Math.Clamp(this.current * 2f, 0f, 1f); // blend
            p[1] = Math.Clamp(this.current, 0f, 1f); // contrast adaptation
            ((uint*)p)[2] = this.width;
            ((uint*)p)[3] = this.height;
            ctx->UpdateSubresource((ID3D11Resource*)this.constants, 0, null, p, 0, 0);

            var state = default(D3DUtil.ComputeState);
            state.Save(ctx);

            ctx->CopySubresourceRegion((ID3D11Resource*)this.copy, 0, 0, 0, 0, res, 0, null);
            ctx->CSSetShader(this.shader, null, 0);
            var srv = this.copySrv;
            ctx->CSSetShaderResources(0, 1, &srv);
            var uav = this.outputUav;
            ctx->CSSetUnorderedAccessViews(0, 1, &uav, null);
            var cb = this.constants;
            ctx->CSSetConstantBuffers(0, 1, &cb);
            ctx->Dispatch((this.width + 7) / 8, (this.height + 7) / 8, 1);

            state.Restore(ctx);

            this.applied++;
            this.cpuMsTotal += sw.Elapsed.TotalMilliseconds;
            this.State = $"active {this.width}x{this.height} {this.format}";
        }
        finally
        {
            if (ctx != null) ctx->Release();
            if (res != null) res->Release();
        }
    }

    /// <summary>Returns applied-count and average CPU ms since the last call (for the stats line).</summary>
    public (long Count, double AvgCpuMs) TakeStats()
    {
        var r = (this.applied, this.applied > 0 ? this.cpuMsTotal / this.applied : 0);
        this.applied = 0;
        this.cpuMsTotal = 0;
        return r;
    }

    private bool EnsureResources(ID3D11Resource* res)
    {
        if (this.output == res && this.copy != null)
            return true;

        ID3D11Texture2D* tex = null;
        if (FAILED(res->QueryInterface(__uuidof<ID3D11Texture2D>(), (void**)&tex)) || tex == null)
            return this.Fail("DLSS output is not a Texture2D");

        D3D11_TEXTURE2D_DESC desc;
        tex->GetDesc(&desc);
        tex->Release();

        if ((desc.BindFlags & (uint)D3D11_BIND_FLAG.D3D11_BIND_UNORDERED_ACCESS) == 0)
            return this.Fail($"DLSS output has no UAV bind flag (bind=0x{desc.BindFlags:X})");

        this.ReleaseTargets();

        if (this.device == null)
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

            this.log.Enqueue("sharpen: CAS compute shader created (bytecode compiled at startup)");
        }

        var copyDesc = desc;
        copyDesc.BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE;
        copyDesc.Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT;
        copyDesc.CPUAccessFlags = 0;
        copyDesc.MiscFlags = 0;
        copyDesc.MipLevels = 1;
        copyDesc.ArraySize = 1;
        ID3D11Texture2D* c = null;
        var hr = this.device->CreateTexture2D(&copyDesc, null, &c);
        if (FAILED(hr))
            return this.Fail($"CreateTexture2D(copy) failed 0x{hr.Value:X8}");
        this.copy = c;

        ID3D11ShaderResourceView* s = null;
        hr = this.device->CreateShaderResourceView((ID3D11Resource*)c, null, &s);
        if (FAILED(hr))
            return this.Fail($"CreateShaderResourceView failed 0x{hr.Value:X8}");
        this.copySrv = s;

        ID3D11UnorderedAccessView* u = null;
        hr = this.device->CreateUnorderedAccessView(res, null, &u);
        if (FAILED(hr))
            return this.Fail($"CreateUnorderedAccessView(output) failed 0x{hr.Value:X8}");
        this.outputUav = u;

        res->AddRef();
        this.output = res;
        this.width = desc.Width;
        this.height = desc.Height;
        this.format = desc.Format;
        this.log.Enqueue($"sharpen: resources for DLSS output {desc.Width}x{desc.Height} {desc.Format} bind=0x{desc.BindFlags:X}");
        return true;
    }

    private bool Fail(string why)
    {
        this.errors++;
        this.log.Enqueue($"sharpen ERROR: {why}" + (this.errors >= 3 ? " (disabled after 3 errors)" : string.Empty));
        this.State = $"error: {why}";
        if (this.errors >= 3)
            this.disabled = true;
        this.ReleaseTargets();
        return false;
    }

    private void ReleaseTargets()
    {
        if (this.outputUav != null) { this.outputUav->Release(); this.outputUav = null; }
        if (this.copySrv != null) { this.copySrv->Release(); this.copySrv = null; }
        if (this.copy != null) { this.copy->Release(); this.copy = null; }
        if (this.output != null) { this.output->Release(); this.output = null; }
    }

    public void Dispose()
    {
        this.disabled = true;
        this.ReleaseTargets();
        if (this.constants != null) { this.constants->Release(); this.constants = null; }
        if (this.shader != null) { this.shader->Release(); this.shader = null; }
        if (this.device != null) { this.device->Release(); this.device = null; }
    }
}
