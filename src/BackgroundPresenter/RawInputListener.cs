using System.Runtime.InteropServices;
using static BackgroundPresenter.NativeMethods;

namespace BackgroundPresenter;

/// <summary>Uma tecla vista pelo Raw Input: sabe de qual aparelho veio, mas não pode bloqueá-la.</summary>
internal readonly record struct RawKey(IntPtr Device, int VirtualKey, bool IsUp, long Tick);

/// <summary>
/// Janela invisível (message-only) registrada no Raw Input para todos os
/// teclados, inclusive quando o app não está em foco (RIDEV_INPUTSINK).
/// Também recebe avisos de aparelho conectado/removido (RIDEV_DEVNOTIFY).
/// </summary>
internal sealed class RawInputListener : NativeWindow, IDisposable
{
    private static readonly IntPtr HWND_MESSAGE = new(-3);
    private static readonly uint HeaderSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();

    private IntPtr _buffer;
    private uint _bufferSize;

    public event Action<RawKey>? KeyReceived;
    public event Action<bool>? DevicesChanged; // true = conectado, false = removido

    public RawInputListener()
    {
        CreateHandle(new CreateParams { Parent = HWND_MESSAGE, Caption = "BackgroundPresenter.RawInput" });
        var devices = new[]
        {
            new RAWINPUTDEVICE
            {
                usUsagePage = 0x01, // Generic Desktop
                usUsage = 0x06,     // Keyboard
                dwFlags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY,
                hwndTarget = Handle,
            },
        };
        if (!RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
            throw new InvalidOperationException($"RegisterRawInputDevices falhou (erro {Marshal.GetLastWin32Error()}).");
        Log.Info("Raw Input registrado.");
    }

    protected override void WndProc(ref Message m)
    {
        try
        {
            if (m.Msg == WM_INPUT) HandleInput(m.LParam);
            else if (m.Msg == WM_INPUT_DEVICE_CHANGE) DevicesChanged?.Invoke((int)m.WParam == GIDC_ARRIVAL);
        }
        catch (Exception ex)
        {
            Log.Error("Erro ao processar Raw Input", ex);
        }
        base.WndProc(ref m); // WM_INPUT exige DefWindowProc para liberar o buffer do sistema
    }

    private void HandleInput(IntPtr hRawInput)
    {
        uint size = 0;
        GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, HeaderSize);
        if (size == 0) return;
        if (size > _bufferSize)
        {
            if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
            _buffer = Marshal.AllocHGlobal((int)size);
            _bufferSize = size;
        }
        if (GetRawInputData(hRawInput, RID_INPUT, _buffer, ref size, HeaderSize) != size) return;

        var header = Marshal.PtrToStructure<RAWINPUTHEADER>(_buffer);
        if (header.dwType != RIM_TYPEKEYBOARD) return;
        var kb = Marshal.PtrToStructure<RAWKEYBOARD>(_buffer + (int)HeaderSize);
        if (kb.VKey == 0 || kb.VKey == 0xFF) return; // teclas "falsas" geradas pelo driver

        KeyReceived?.Invoke(new RawKey(header.hDevice, kb.VKey, (kb.Flags & RI_KEY_BREAK) != 0, Environment.TickCount64));
    }

    public void Dispose()
    {
        DestroyHandle();
        if (_buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
        }
    }
}
