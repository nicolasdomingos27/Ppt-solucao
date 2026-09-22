using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using static PptLock.NativeMethods;

namespace PptLock;

/// <summary>Identificação de um teclado (ou passador) visto pelo Raw Input.</summary>
internal sealed record DeviceInfo(string Path, string? VendorId, string? ProductId, string? Interface, string Name)
{
    private static readonly Regex VidRegex = new(@"VID[_&]([0-9A-F]{4,8})", RegexOptions.IgnoreCase);
    private static readonly Regex PidRegex = new(@"PID[_&]([0-9A-F]{4})", RegexOptions.IgnoreCase);
    private static readonly Regex MiRegex = new(@"MI_([0-9A-F]{2})", RegexOptions.IgnoreCase);

    public string Describe() =>
        VendorId is null ? Name : $"{Name} (VID {VendorId} / PID {ProductId})";

    /// <summary>Consulta o Windows sobre o aparelho por trás de um handle do Raw Input.</summary>
    public static DeviceInfo? Query(IntPtr hDevice)
    {
        if (hDevice == IntPtr.Zero) return null; // tecla sintética / sem aparelho
        string? path = GetDeviceName(hDevice);
        if (path is null) return null;

        // USB: "...VID_046D&PID_C52D&MI_00..."; Bluetooth: "..._VID&0002046d_PID&b01c...".
        string? vid = Match(VidRegex, path);
        if (vid is { Length: > 4 }) vid = vid[^4..];
        string? pid = Match(PidRegex, path);
        string? mi = Match(MiRegex, path);

        string name = GetProductName(path)
                      ?? (vid is null ? "Teclado sem identificação" : $"Dispositivo {vid}:{pid}");
        return new DeviceInfo(path, vid?.ToUpperInvariant(), pid?.ToUpperInvariant(), mi, name);
    }

    private static string? Match(Regex re, string s)
    {
        var m = re.Match(s);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string? GetDeviceName(IntPtr hDevice)
    {
        uint chars = 0;
        GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref chars);
        if (chars == 0) return null;
        var buffer = Marshal.AllocHGlobal((int)chars * 2);
        try
        {
            if (GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, buffer, ref chars) == uint.MaxValue) return null;
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Nome "bonito" do aparelho (ex.: "Logitech USB Receiver"); não exige admin.</summary>
    private static string? GetProductName(string path)
    {
        try
        {
            using var handle = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (handle.IsInvalid) return null;
            const int size = 512;
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                string? manufacturer = null, product = null;
                if (HidD_GetManufacturerString(handle, buffer, size)) manufacturer = Marshal.PtrToStringUni(buffer);
                if (HidD_GetProductString(handle, buffer, size)) product = Marshal.PtrToStringUni(buffer);
                if (string.IsNullOrWhiteSpace(product)) return null;
                return string.IsNullOrWhiteSpace(manufacturer) || product.Contains(manufacturer, StringComparison.OrdinalIgnoreCase)
                    ? product.Trim()
                    : $"{manufacturer.Trim()} {product.Trim()}";
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Responde "este handle do Raw Input é o passador configurado?". Guarda a
/// resposta por handle; o cache é limpo quando um aparelho é conectado ou
/// removido (o Windows dá um handle novo ao passador ao reconectar).
/// </summary>
internal sealed class PresenterMatcher
{
    private readonly Dictionary<IntPtr, bool> _cache = new();
    private PresenterConfig? _presenter;

    public PresenterMatcher(PresenterConfig? presenter) => _presenter = presenter;

    public PresenterConfig? Presenter
    {
        get => _presenter;
        set { _presenter = value; _cache.Clear(); }
    }

    public void Reset() => _cache.Clear();

    public bool IsPresenter(IntPtr hDevice)
    {
        if (_presenter is null || hDevice == IntPtr.Zero) return false;
        if (_cache.TryGetValue(hDevice, out bool cached)) return cached;
        var info = DeviceInfo.Query(hDevice);
        bool result = info is not null && _presenter.Matches(info);
        _cache[hDevice] = result;
        if (result) Log.Info($"Passador reconhecido: {info!.Describe()}");
        return result;
    }
}
