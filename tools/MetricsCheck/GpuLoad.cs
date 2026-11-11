using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MetricsCheck;

/// <summary>
/// A known GPU load: Direct3D 11 copies between four 8192 x 8192 RGBA8 textures of random data
/// (1 GiB of dedicated memory) on the default adapter, in batches with an idle gap as long as
/// each batch took, so the 3D engine sits near 50%. Clears were tried first, but drivers
/// fast-clear them and the engine barely registers the work.
/// </summary>
internal sealed unsafe class GpuLoad : IDisposable
{
    private const int TextureCount = 4;
    private const int Size = 8192;
    private const int CopiesPerBatch = 20;

    private readonly Thread _thread;
    private volatile bool _stop;

    private GpuLoad()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "GPU load" };
    }

    public int Batches { get; private set; }

    public string? Error { get; private set; }

    public static GpuLoad Start()
    {
        var load = new GpuLoad();
        load._thread.Start();
        return load;
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join();
    }

    private void Run()
    {
        nint device = 0, context = 0, query = 0;
        var textures = new nint[TextureCount];
        var pixels = (byte*)NativeMemory.Alloc((nuint)Size * Size * 4);
        try
        {
            Random.Shared.NextBytes(new Span<byte>(pixels, Size * Size * 4));
            Check(D3D11CreateDevice(0, 1 /* hardware */, 0, 0, 0, 0, 7 /* SDK version */, out device, out _, out context), "D3D11CreateDevice");
            var textureDesc = new Texture2DDesc
            {
                Width = Size,
                Height = Size,
                MipLevels = 1,
                ArraySize = 1,
                Format = 28, // DXGI_FORMAT_R8G8B8A8_UNORM
                SampleCount = 1,
                BindFlags = 0x20, // D3D11_BIND_RENDER_TARGET
            };
            var initialData = new SubresourceData { SysMem = (nint)pixels, SysMemPitch = Size * 4 };
            var createTexture = (delegate* unmanaged[Stdcall]<nint, Texture2DDesc*, SubresourceData*, nint*, int>)Slot(device, 5);
            for (var i = 0; i < TextureCount; i++)
            {
                nint texture;
                Check(createTexture(device, &textureDesc, &initialData, &texture), "CreateTexture2D");
                textures[i] = texture;
            }

            var queryDesc = stackalloc uint[2]; // D3D11_QUERY_EVENT, no flags
            nint q;
            Check(((delegate* unmanaged[Stdcall]<nint, uint*, nint*, int>)Slot(device, 24))(device, queryDesc, &q), "CreateQuery");
            query = q;

            var copy = (delegate* unmanaged[Stdcall]<nint, nint, nint, void>)Slot(context, 47);
            var end = (delegate* unmanaged[Stdcall]<nint, nint, void>)Slot(context, 28);
            var getData = (delegate* unmanaged[Stdcall]<nint, nint, void*, uint, uint, int>)Slot(context, 29);
            var flush = (delegate* unmanaged[Stdcall]<nint, void>)Slot(context, 111);
            while (!_stop)
            {
                var started = Stopwatch.GetTimestamp();
                for (var i = 0; i < CopiesPerBatch; i++)
                {
                    copy(context, textures[(i + 1) % TextureCount], textures[i % TextureCount]);
                }

                end(context, query);
                flush(context);
                int done;
                while (getData(context, query, &done, sizeof(int), 0) != 0 && !_stop)
                {
                    Thread.Yield();
                }

                Batches++;
                Thread.Sleep(Stopwatch.GetElapsedTime(started));
            }
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            Release(query);
            foreach (var texture in textures)
            {
                Release(texture);
            }

            Release(context);
            Release(device);
            NativeMemory.Free(pixels);
        }
    }

    /// <summary>A COM method's address: entry <paramref name="slot"/> of the object's vtable.</summary>
    private static nint Slot(nint comObject, int slot) => (*(nint**)comObject)[slot];

    private static void Release(nint comObject)
    {
        if (comObject != 0)
        {
            ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(comObject, 2))(comObject);
        }
    }

    private static void Check(int hr, string call)
    {
        if (hr < 0)
        {
            throw new InvalidOperationException($"{call} failed: 0x{hr:X8}");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Texture2DDesc
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public int Format;
        public uint SampleCount;
        public uint SampleQuality;
        public int Usage;
        public uint BindFlags;
        public uint CpuAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SubresourceData
    {
        public nint SysMem;
        public uint SysMemPitch;
        public uint SysMemSlicePitch;
    }

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags, nint featureLevels, uint featureLevelCount, uint sdkVersion, out nint device, out int featureLevel, out nint context);
}
