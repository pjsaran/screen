using System.Globalization;

using Vortice.DXGI;

using Windows.Win32;
using Windows.Win32.Devices.Display;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;

namespace Captr.Core.Displays;

/// <summary>
/// The one class that talks to display hardware. Joins two API worlds to produce
/// <see cref="DisplayInfo"/> snapshots: DXGI (via Vortice) for output indices and
/// desktop geometry, and <c>QueryDisplayConfig</c> (via CsWin32) for the stable
/// EDID-derived device path, friendly name, refresh rate, and the display number
/// Windows Settings shows. The join key is the GDI device name (<c>\\.\DISPLAY3</c>),
/// the only identifier both worlds expose. If this class is wrong, Captr records the
/// wrong monitor — the failure SPEC §5 is written to prevent.
/// </summary>
public sealed class DisplayEnumerator : IDisplayEnumerator
{
    /// <inheritdoc />
    public IReadOnlyList<DisplayInfo> Enumerate()
    {
        Dictionary<string, WindowsDisplayConfig> configByGdiName = QueryWindowsDisplayConfig();
        var displays = new List<DisplayInfo>();

        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        // ddagrab's output_idx is a FLAT index across all adapters in enumeration
        // order (it walks adapters and counts every output). Our DxgiOutputIndex must
        // match that numbering exactly or capture grabs the wrong screen — verified
        // by the Display-trait integration test against real hardware.
        int flatOutputIndex = 0;

        for (uint adapterIndex = 0;
             factory.EnumAdapters1(adapterIndex, out IDXGIAdapter1? adapter).Success;
             adapterIndex++)
        {
            using (adapter)
            {
                for (uint outputIndex = 0;
                     adapter!.EnumOutputs(outputIndex, out IDXGIOutput? output).Success;
                     outputIndex++)
                {
                    using (output)
                    {
                        OutputDescription description = output!.Description;
                        if (!description.AttachedToDesktop)
                        {
                            continue;
                        }

                        string gdiName = description.DeviceName;
                        if (configByGdiName.TryGetValue(gdiName, out WindowsDisplayConfig? config))
                        {
                            displays.Add(BuildDisplayInfo(description, config, (int)adapterIndex, (int)outputIndex, flatOutputIndex));
                        }

                        // The flat index advances for every attached output, matched
                        // or not, to stay aligned with ddagrab's own counting.
                        flatOutputIndex++;
                    }
                }
            }
        }

        return displays;
    }

    private static DisplayInfo BuildDisplayInfo(
        OutputDescription description,
        WindowsDisplayConfig config,
        int adapterIndex,
        int outputIndexOnAdapter,
        int flatOutputIndex)
    {
        int width = description.DesktopCoordinates.Right - description.DesktopCoordinates.Left;
        int height = description.DesktopCoordinates.Bottom - description.DesktopCoordinates.Top;

        return new DisplayInfo
        {
            StableId = config.MonitorDevicePath,
            WindowsDisplayNumber = config.WindowsDisplayNumber,
            FriendlyName = config.FriendlyName,
            DxgiOutputIndex = flatOutputIndex,
            DxgiAdapterIndex = adapterIndex,
            DxgiOutputIndexOnAdapter = outputIndexOnAdapter,
            Width = width,
            Height = height,
            VirtualX = description.DesktopCoordinates.Left,
            VirtualY = description.DesktopCoordinates.Top,
            DpiScale = QueryDpiScale(description.Monitor),
            RefreshRateHz = config.RefreshRateHz,
        };
    }

    /// <summary>What the Windows display-configuration API knows about one GDI
    /// source, keyed by its GDI device name.</summary>
    private sealed record WindowsDisplayConfig(
        string MonitorDevicePath,
        string FriendlyName,
        int WindowsDisplayNumber,
        double RefreshRateHz);

    /// <summary>
    /// Asks <c>QueryDisplayConfig</c> for every active display path and resolves each
    /// to (stable device path, friendly name, Settings display number, refresh rate).
    /// </summary>
    private static unsafe Dictionary<string, WindowsDisplayConfig> QueryWindowsDisplayConfig()
    {
        var result = new Dictionary<string, WindowsDisplayConfig>(StringComparer.OrdinalIgnoreCase);

        // The buffer sizes can grow between the size query and the query itself
        // (a monitor plugged in at exactly the wrong moment) — retry on the
        // documented ERROR_INSUFFICIENT_BUFFER in that case.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint pathCount = 0;
            uint modeCount = 0;
            if (PInvoke.GetDisplayConfigBufferSizes(
                    QUERY_DISPLAY_CONFIG_FLAGS.QDC_ONLY_ACTIVE_PATHS, &pathCount, &modeCount)
                != WIN32_ERROR.ERROR_SUCCESS)
            {
                return result;
            }

            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];

            // The 5-argument overload passes a null topology id, which is REQUIRED
            // with QDC_ONLY_ACTIVE_PATHS.
            WIN32_ERROR error = PInvoke.QueryDisplayConfig(
                QUERY_DISPLAY_CONFIG_FLAGS.QDC_ONLY_ACTIVE_PATHS,
                ref pathCount, paths, ref modeCount, modes);

            if (error == WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER)
            {
                continue;
            }

            if (error != WIN32_ERROR.ERROR_SUCCESS)
            {
                return result;
            }

            foreach (DISPLAYCONFIG_PATH_INFO path in paths.Take((int)pathCount))
            {
                AddPath(result, path);
            }

            return result;
        }

        return result;
    }

    private static unsafe void AddPath(
        Dictionary<string, WindowsDisplayConfig> result, DISPLAYCONFIG_PATH_INFO path)
    {
        // Source side: the GDI device name (\\.\DISPLAYn) — our join key to DXGI and
        // the origin of the number Windows Settings shows.
        var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                size = (uint)sizeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME),
                adapterId = path.sourceInfo.adapterId,
                id = path.sourceInfo.id,
            },
        };
        if (PInvoke.DisplayConfigGetDeviceInfo(&source.header) != 0)
        {
            return;
        }

        // Target side: the EDID-derived monitor device path (our stable identity)
        // and the human-readable monitor name.
        var target = new DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                size = (uint)sizeof(DISPLAYCONFIG_TARGET_DEVICE_NAME),
                adapterId = path.targetInfo.adapterId,
                id = path.targetInfo.id,
            },
        };
        if (PInvoke.DisplayConfigGetDeviceInfo(&target.header) != 0)
        {
            return;
        }

        string gdiName = source.viewGdiDeviceName.ToString();
        double refreshHz = path.targetInfo.refreshRate.Denominator == 0
            ? 0
            : path.targetInfo.refreshRate.Numerator / (double)path.targetInfo.refreshRate.Denominator;

        result[gdiName] = new WindowsDisplayConfig(
            MonitorDevicePath: target.monitorDevicePath.ToString(),
            FriendlyName: target.monitorFriendlyDeviceName.ToString(),
            WindowsDisplayNumber: ParseDisplayNumber(gdiName),
            RefreshRateHz: refreshHz);
    }

    /// <summary>Extracts 3 from <c>\\.\DISPLAY3</c> — the ordinal Windows Settings
    /// presents as the display number.</summary>
    private static int ParseDisplayNumber(string gdiDeviceName)
    {
        int digitsStart = gdiDeviceName.Length;
        while (digitsStart > 0 && char.IsAsciiDigit(gdiDeviceName[digitsStart - 1]))
        {
            digitsStart--;
        }

        return digitsStart < gdiDeviceName.Length
            ? int.Parse(gdiDeviceName[digitsStart..], CultureInfo.InvariantCulture)
            : 0;
    }

    private static double QueryDpiScale(nint monitorHandle)
    {
        if (monitorHandle == 0)
        {
            return 1.0;
        }

        return PInvoke.GetDpiForMonitor(
                   new HMONITOR(monitorHandle), MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI,
                   out uint dpiX, out _).Succeeded
            ? dpiX / 96.0
            : 1.0;
    }
}
