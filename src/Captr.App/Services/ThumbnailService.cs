using System.Windows.Media;
using System.Windows.Media.Imaging;

using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Captr.App.Services;

/// <summary>
/// Live display thumbnails for the status page via one-shot DXGI Desktop
/// Duplication — the in-process preview path SPEC §3 permits, independent of
/// recording and allowed to fail without consequence. Every failure returns null;
/// a missing thumbnail is a blank tile, never an error.
/// </summary>
public static class ThumbnailService
{
    /// <summary>
    /// Grabs one frame of the given output as a WPF bitmap, downscaled by
    /// <paramref name="scale"/>. Null on any failure (display busy, device lost,
    /// duplication limit) — the caller shows a placeholder.
    /// </summary>
    public static BitmapSource? CaptureThumbnail(int adapterIndex, int outputIndexOnAdapter, int scale = 8)
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
                    return GrabFrame(adapter, output1, scale);
                }
            }
        }
        catch (Exception exception) when (exception is SharpGen.Runtime.SharpGenException or InvalidOperationException)
        {
            return null; // Preview is best-effort by specification.
        }
    }

    private static TransformedBitmap? GrabFrame(IDXGIAdapter1 adapter, IDXGIOutput1 output, int scale)
    {
        D3D11.D3D11CreateDevice(
            adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, null,
            out ID3D11Device? device).CheckError();
        using (device)
        using (IDXGIOutputDuplication duplication = output.DuplicateOutput(device!))
        {
            // First AcquireNextFrame often returns only pointer updates; try a few
            // times for a real frame.
            for (int attempt = 0; attempt < 4; attempt++)
            {
                if (!duplication.AcquireNextFrame(100, out OutduplFrameInfo frameInfo, out IDXGIResource? resource).Success)
                {
                    return null;
                }

                using (resource)
                {
                    if (frameInfo.LastPresentTime == 0 && attempt < 3)
                    {
                        duplication.ReleaseFrame();
                        continue;
                    }

                    using ID3D11Texture2D screenTexture = resource!.QueryInterface<ID3D11Texture2D>();
                    TransformedBitmap bitmap = CopyToBitmap(device!, screenTexture, scale);
                    duplication.ReleaseFrame();
                    return bitmap;
                }
            }

            return null;
        }
    }

    private static TransformedBitmap CopyToBitmap(ID3D11Device device, ID3D11Texture2D screenTexture, int scale)
    {
        Texture2DDescription description = screenTexture.Description;
        var stagingDescription = description with
        {
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        };

        using ID3D11Texture2D staging = device.CreateTexture2D(stagingDescription);
        device.ImmediateContext.CopyResource(staging, screenTexture);

        MappedSubresource mapped = device.ImmediateContext.Map(staging, 0, MapMode.Read);
        try
        {
            int width = (int)description.Width;
            int height = (int)description.Height;
            var full = BitmapSource.Create(
                width, height, 96, 96, PixelFormats.Bgra32, null,
                mapped.DataPointer, (int)(mapped.RowPitch * description.Height), (int)mapped.RowPitch);
            var thumbnail = new TransformedBitmap(full, new ScaleTransform(1.0 / scale, 1.0 / scale));
            thumbnail.Freeze(); // Cross-thread use by the UI.
            return thumbnail;
        }
        finally
        {
            device.ImmediateContext.Unmap(staging, 0);
        }
    }
}
