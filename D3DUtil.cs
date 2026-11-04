using System;
using System.Text;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace XivDlssFix;

/// <summary>Small D3D11 helpers shared by the post-DLSS passes (sharpening, flash probe).</summary>
internal static unsafe class D3DUtil
{
    /// <summary>The game's Kernel::Texture* -> its ID3D11Resource (AddRef'd, caller releases). Same path the
    /// game uses in its DLSS evaluate: Texture+0x80 -> view* -> ID3D11View::GetResource.</summary>
    public static ID3D11Resource* ResolveResource(nint gameTexture)
    {
        if (gameTexture == 0)
            return null;
        var viewSlot = *(nint*)(gameTexture + 0x80);
        if (viewSlot == 0)
            return null;
        var view = *(ID3D11View**)viewSlot;
        if (view == null)
            return null;
        ID3D11Resource* res = null;
        view->GetResource(&res);
        return res;
    }

    /// <summary>Compiles cs_5_0 HLSL to bytecode. Needs no device, so it can run on a worker thread.</summary>
    public static byte[] CompileCompute(string hlsl)
    {
        ID3DBlob* code = null;
        ID3DBlob* errors = null;
        var src = Encoding.ASCII.GetBytes(hlsl);
        var entry = Encoding.ASCII.GetBytes("main\0");
        var target = Encoding.ASCII.GetBytes("cs_5_0\0");
        HRESULT hr;
        fixed (byte* pSrc = src)
        fixed (byte* pEntry = entry)
        fixed (byte* pTarget = target)
        {
            hr = D3DCompile(pSrc, (nuint)src.Length, null, null, null, (sbyte*)pEntry, (sbyte*)pTarget,
                            D3DCOMPILE.D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &code, &errors);
        }

        try
        {
            if (FAILED(hr) || code == null)
            {
                var msg = errors != null ? new string((sbyte*)errors->GetBufferPointer(), 0, (int)errors->GetBufferSize()) : string.Empty;
                throw new InvalidOperationException($"shader compile failed 0x{hr.Value:X8}: {msg}");
            }

            var bytes = new byte[(int)code->GetBufferSize()];
            new ReadOnlySpan<byte>(code->GetBufferPointer(), bytes.Length).CopyTo(bytes);
            return bytes;
        }
        finally
        {
            if (code != null) code->Release();
            if (errors != null) errors->Release();
        }
    }

    public static ID3D11ComputeShader* CreateCompute(ID3D11Device* device, byte[] bytecode)
    {
        ID3D11ComputeShader* cs = null;
        fixed (byte* p = bytecode)
        {
            var hr = device->CreateComputeShader(p, (nuint)bytecode.Length, null, &cs);
            if (FAILED(hr))
                throw new InvalidOperationException($"CreateComputeShader failed 0x{hr.Value:X8}");
        }

        return cs;
    }

    public static ID3D11Buffer* CreateConstants(ID3D11Device* device, uint size)
    {
        var bd = new D3D11_BUFFER_DESC
        {
            ByteWidth = size,
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER,
        };
        ID3D11Buffer* b = null;
        var hr = device->CreateBuffer(&bd, null, &b);
        if (FAILED(hr))
            throw new InvalidOperationException($"CreateBuffer(constants) failed 0x{hr.Value:X8}");
        return b;
    }

    /// <summary>The compute-stage bindings a pass touches (shader, t0, u0, b0), saved so the game's state
    /// is put back exactly as it was.</summary>
    public struct ComputeState
    {
        private const int MaxInstances = 256;
        private ID3D11ComputeShader* shader;
        private fixed ulong instances[MaxInstances];
        private uint instanceCount;
        private ID3D11ShaderResourceView* srv;
        private ID3D11UnorderedAccessView* uav;
        private ID3D11Buffer* cb;

        public void Save(ID3D11DeviceContext* ctx)
        {
            uint count = MaxInstances;
            fixed (ulong* inst = this.instances)
            fixed (ID3D11ComputeShader** s = &this.shader)
                ctx->CSGetShader(s, (ID3D11ClassInstance**)inst, &count);
            this.instanceCount = count;
            fixed (ID3D11ShaderResourceView** v = &this.srv)
                ctx->CSGetShaderResources(0, 1, v);
            fixed (ID3D11UnorderedAccessView** u = &this.uav)
                ctx->CSGetUnorderedAccessViews(0, 1, u);
            fixed (ID3D11Buffer** b = &this.cb)
                ctx->CSGetConstantBuffers(0, 1, b);
        }

        public void Restore(ID3D11DeviceContext* ctx)
        {
            ID3D11ShaderResourceView* nullSrv = null;
            ID3D11UnorderedAccessView* nullUav = null;
            ctx->CSSetShaderResources(0, 1, &nullSrv);
            ctx->CSSetUnorderedAccessViews(0, 1, &nullUav, null);

            fixed (ulong* inst = this.instances)
            {
                ctx->CSSetShader(this.shader, this.instanceCount > 0 ? (ID3D11ClassInstance**)inst : null, this.instanceCount);
                var v = this.srv;
                ctx->CSSetShaderResources(0, 1, &v);
                var u = this.uav;
                ctx->CSSetUnorderedAccessViews(0, 1, &u, null);
                var b = this.cb;
                ctx->CSSetConstantBuffers(0, 1, &b);

                if (this.shader != null) this.shader->Release();
                for (uint k = 0; k < this.instanceCount; k++)
                    if (inst[k] != 0) ((ID3D11ClassInstance*)inst[k])->Release();
            }

            if (this.srv != null) this.srv->Release();
            if (this.uav != null) this.uav->Release();
            if (this.cb != null) this.cb->Release();
            this = default;
        }
    }
}
