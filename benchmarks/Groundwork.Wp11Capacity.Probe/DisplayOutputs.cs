using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

unsafe sealed partial class Gpu
{
    internal readonly record struct DisplayOutput(uint Vendor, string Adapter, string DeviceName,
        int Left, int Top, int Right, int Bottom, nint Monitor);

    internal readonly record struct DisplayPath(string DeviceName, uint SourceVendor, uint TargetVendor, string Technology);

    internal static DisplayPath[] QueryDisplayPaths()
    {
        const uint onlyActive = 2;
        uint pathCount, modeCount;
        var result = GetDisplayConfigBufferSizes(onlyActive, &pathCount, &modeCount);
        if (result != 0)
            throw new InvalidOperationException($"GetDisplayConfigBufferSizes returned {result}.");
        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
        fixed (DISPLAYCONFIG_PATH_INFO* pathPtr = paths)
        fixed (DISPLAYCONFIG_MODE_INFO* modePtr = modes)
            result = QueryDisplayConfig(onlyActive, &pathCount, pathPtr, &modeCount, modePtr, null);
        if (result != 0)
            throw new InvalidOperationException($"QueryDisplayConfig returned {result}.");

        return paths.Take((int)pathCount).Select(path =>
        {
            DISPLAYCONFIG_SOURCE_DEVICE_NAME name = default;
            name.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
            name.header.size = (uint)sizeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME);
            name.header.adapterId = path.sourceInfo.adapterId;
            name.header.id = path.sourceInfo.id;
            var code = DisplayConfigGetDeviceInfo(&name.header);
            if (code != 0)
                throw new InvalidOperationException($"DisplayConfig source name returned {code}.");
            return new DisplayPath(new string(name.viewGdiDeviceName).TrimEnd('\0'),
                AdapterVendor(path.sourceInfo.adapterId), AdapterVendor(path.targetInfo.adapterId),
                path.targetInfo.outputTechnology.ToString());
        }).ToArray();

        static uint AdapterVendor(LUID luid)
        {
            DISPLAYCONFIG_ADAPTER_NAME request = default;
            request.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_ADAPTER_NAME;
            request.header.size = (uint)sizeof(DISPLAYCONFIG_ADAPTER_NAME);
            request.header.adapterId = luid;
            var code = DisplayConfigGetDeviceInfo(&request.header);
            if (code != 0)
                throw new InvalidOperationException($"DisplayConfig adapter name returned {code}.");
            var path = new string(request.adapterDevicePath);
            var index = path.IndexOf("VEN_", StringComparison.OrdinalIgnoreCase);
            return index < 0 ? 0u : Convert.ToUInt32(path.Substring(index + 4, 4), 16);
        }
    }

    internal static DisplayOutput[] EnumerateOutputs()
    {
        IDXGIFactory4* factory = null;
        Check(CreateFactory(0, __uuidof<IDXGIFactory4>(), (void**)&factory), "display factory");
        try
        {
            var outputs = new List<DisplayOutput>();
            for (uint index = 0; ; index++)
            {
                IDXGIAdapter1* adapter = null;
                var result = factory->EnumAdapters1(index, &adapter);
                if (result.Value == unchecked((int)0x887A0002))
                    break;
                Check(result, "enumerate display adapters");
                try
                {
                    DXGI_ADAPTER_DESC1 description;
                    Check(adapter->GetDesc1(&description), "display adapter description");
                    for (uint outputIndex = 0; ; outputIndex++)
                    {
                        IDXGIOutput* output = null;
                        result = adapter->EnumOutputs(outputIndex, &output);
                        if (result.Value == unchecked((int)0x887A0002))
                            break;
                        Check(result, "enumerate display outputs");
                        try
                        {
                            DXGI_OUTPUT_DESC desc;
                            Check(output->GetDesc(&desc), "display output description");
                            if (desc.AttachedToDesktop)
                            {
                                var rect = desc.DesktopCoordinates;
                                outputs.Add(new(description.VendorId, new string(description.Description).TrimEnd('\0'),
                                    new string(desc.DeviceName).TrimEnd('\0'), rect.left, rect.top, rect.right, rect.bottom,
                                    (nint)desc.Monitor));
                            }
                        }
                        finally { Release(output); }
                    }
                }
                finally { Release(adapter); }
            }
            return outputs.ToArray();
        }
        finally { Release(factory); }
    }

    internal sealed partial class Surface
    {
        internal DisplayOutput? Output { get; private set; }
        internal DisplayPath? ScanOut { get; private set; }

        private void SelectOutput(uint vendor)
        {
            var paths = QueryDisplayPaths().Where(x => x.TargetVendor == vendor).ToArray();
            var external = paths.Where(x => !x.Technology.EndsWith("_INTERNAL", StringComparison.Ordinal)).ToArray();
            var choices = external.Length > 0 ? external : paths;
            if (choices.Length != 1)
                throw new InvalidOperationException($"Expected one usable scan-out on vendor {vendor:X}, found {choices.Length}.");
            ScanOut = choices[0];
            Output = EnumerateOutputs().Single(x => x.DeviceName == choices[0].DeviceName);
        }

        internal void VerifyDisplayPath()
        {
            if (ScanOut is { } path && !QueryDisplayPaths().Contains(path))
                throw new InvalidOperationException("The Windows display path changed during the run.");
        }

        private void VerifyOutput(DisplayOutput expected)
        {
            IDXGIOutput* output = null;
            Check(_chain->GetContainingOutput(&output), "containing swapchain output");
            try
            {
                DXGI_OUTPUT_DESC desc;
                Check(output->GetDesc(&desc), "containing output description");
                RECT window;
                if (!GetWindowRect(_window, &window) || (nint)desc.Monitor != expected.Monitor ||
                    window.left < expected.Left || window.top < expected.Top ||
                    window.right > expected.Right || window.bottom > expected.Bottom)
                    throw new InvalidOperationException($"Benchmark window left {expected.DeviceName}.");
            }
            finally { Release(output); }
        }
    }
}
