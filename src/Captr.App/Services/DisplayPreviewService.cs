using System.Collections.Concurrent;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Captr.App.Services;

/// <summary>
/// Live display thumbnails for the status page via DXGI Desktop Duplication — the
/// in-process preview path SPEC §3 permits, independent of recording and allowed to
/// fail without consequence. Every failure returns null; a missing thumbnail is a
/// blank tile, never an error.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is an instance and not a static helper.</b> Creating a D3D11 device
/// and an output duplication is expensive, and Windows allows only a limited number
/// of duplications per output. Doing it per capture — which is what a static helper
/// invited — meant building and tearing down a GPU device every few seconds for
/// every display, leaving native objects to the finalizer queue and occasionally
/// hitting the duplication limit. Here each output's device and duplication are
/// created ONCE and reused, and <see cref="Release"/> disposes them the moment the
/// page stops being watched.
/// </para>
/// <para>
/// All capture happens on background threads, so the cache is a concurrent
/// dictionary and each entry is locked while in use — a duplication object is not
/// thread-safe.
/// </para>
/// </remarks>
public sealed class DisplayPreviewService : IDisposable
{
    /// <summary>How much the captured frame is shrunk. A 4K display becomes 480×270,
    /// which is more than the ~200px tile needs and keeps the copy cheap.</summary>
    private const int DownscaleFactor = 8;

    /// <summary>Milliseconds to wait for a new frame. Short: a desktop that has not
    /// changed simply keeps its previous thumbnail, which is correct AND free.</summary>
    private const int AcquireTimeoutMilliseconds = 60;

    private readonly ConcurrentDictionary<(int Adapter, int Output), OutputCapture> _captures = new();
    private bool _disposed;

    /// <summary>
    /// One frame of the given output as a frozen WPF bitmap, or null on any failure
    /// (display busy, device lost, duplication limit, nothing changed since last
    /// time). The caller keeps whatever it had.
    /// </summary>
    public BitmapSource? Capture(int adapterIndex, int outputIndexOnAdapter)
    {
        if (_disposed)
        {
            return null;
        }

        OutputCapture? capture = _captures.GetOrAdd(
            (adapterIndex, outputIndexOnAdapter),
            key => OutputCapture.TryCreate(key.Adapter, key.Output)!);

        if (capture is null)
        {
            return null;
        }

        BitmapSource? frame = capture.TryGrabFrame();
        if (frame is null && capture.IsBroken)
        {
            // A lost device never recovers; drop it so the next call builds a fresh
            // one instead of failing forever (this happens on GPU driver resets and
            // on resume from sleep).
            if (_captures.TryRemove((adapterIndex, outputIndexOnAdapter), out OutputCapture? broken))
            {
                broken.Dispose();
            }
        }

        return frame;
    }

    /// <summary>Disposes every cached device and duplication. The service stays
    /// usable — the next <see cref="Capture"/> rebuilds what it needs.</summary>
    public void Release()
    {
        foreach ((int Adapter, int Output) key in _captures.Keys)
        {
            if (_captures.TryRemove(key, out OutputCapture? capture))
            {
                capture.Dispose();
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Release();
    }

    /// <summary>One output's long-lived capture device and duplication.</summary>
    private sealed class OutputCapture : IDisposable
    {
        private readonly Lock _gate = new();
        private readonly ID3D11Device _device;
        private readonly IDXGIOutputDuplication _duplication;

        private OutputCapture(ID3D11Device device, IDXGIOutputDuplication duplication)
        {
            _device = device;
            _duplication = duplication;
        }

        /// <summary>True once the device is beyond recovery and should be discarded.</summary>
        public bool IsBroken { get; private set; }

        public static OutputCapture? TryCreate(int adapterIndex, int outputIndexOnAdapter)
        {
            try
            {
                using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
                if (!factory.EnumAdapters1((uint)adapterIndex, out IDXGIAdapter1? adapter).Success)
                {
                    return null;
                }

                using (adapter)
                {
                    if (!adapter!.EnumOutputs((uint)outputIndexOnAdapter, out IDXGIOutput? output).Success)
                    {
                        return null;
                    }

                    using (output)
                    using (IDXGIOutput1 output1 = output!.QueryInterface<IDXGIOutput1>())
                    {
                        D3D11.D3D11CreateDevice(
                            adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, null,
                            out ID3D11Device? device).CheckError();
                        return new OutputCapture(device!, output1.DuplicateOutput(device!));
                    }
                }
            }
            catch (Exception exception) when (exception is SharpGen.Runtime.SharpGenException or InvalidOperationException)
            {
                return null; // Preview is best-effort by specification.
            }
        }

        /// <summary>The newest frame, or null when nothing arrived in time.</summary>
        public TransformedBitmap? TryGrabFrame()
        {
            lock (_gate)
            {
                if (IsBroken)
                {
                    return null;
                }

                try
                {
                    if (!_duplication.AcquireNextFrame(
                            AcquireTimeoutMilliseconds, out OutduplFrameInfo _, out IDXGIResource? resource).Success)
                    {
                        return null; // Timeout: the desktop simply has not changed.
                    }

                    try
                    {
                        using ID3D11Texture2D screenTexture = resource!.QueryInterface<ID3D11Texture2D>();
                        return CopyToBitmap(screenTexture);
                    }
                    finally
                    {
                        // Releasing the frame is NOT optional: hold one and the next
                        // AcquireNextFrame fails forever. It must happen even when
                        // the copy above throws.
                        resource!.Dispose();
                        _duplication.ReleaseFrame();
                    }
                }
                catch (SharpGen.Runtime.SharpGenException)
                {
                    IsBroken = true;
                    return null;
                }
            }
        }

        private TransformedBitmap CopyToBitmap(ID3D11Texture2D screenTexture)
        {
            Texture2DDescription description = screenTexture.Description;
            var stagingDescription = description with
            {
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None,
            };

            using ID3D11Texture2D staging = _device.CreateTexture2D(stagingDescription);
            _device.ImmediateContext.CopyResource(staging, screenTexture);

            MappedSubresource mapped = _device.ImmediateContext.Map(staging, 0, MapMode.Read);
            try
            {
                var full = BitmapSource.Create(
                    (int)description.Width, (int)description.Height, 96, 96, PixelFormats.Bgra32, null,
                    mapped.DataPointer, (int)(mapped.RowPitch * description.Height), (int)mapped.RowPitch);
                var thumbnail = new TransformedBitmap(
                    full, new ScaleTransform(1.0 / DownscaleFactor, 1.0 / DownscaleFactor));

                // Frozen so the UI thread may use a bitmap produced here.
                thumbnail.Freeze();
                return thumbnail;
            }
            finally
            {
                _device.ImmediateContext.Unmap(staging, 0);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _duplication.Dispose();
                _device.Dispose();
                IsBroken = true;
            }
        }
    }
}
