using System.Runtime.InteropServices;
using static BackgroundPresenter.NativeMethods;

namespace BackgroundPresenter;

/// <summary>Atalho global Ctrl+Alt+P (pausar/retomar), via RegisterHotKey.</summary>
internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private static readonly IntPtr HWND_MESSAGE = new(-3);
    private const int HotkeyId = 1;

    public event Action? Pressed;
    public bool IsRegistered { get; }

    public HotkeyWindow()
    {
        CreateHandle(new CreateParams { Parent = HWND_MESSAGE, Caption = "BackgroundPresenter.Hotkey" });
        IsRegistered = RegisterHotKey(Handle, HotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)Keys.P);
        if (IsRegistered) Log.Info("Atalho Ctrl+Alt+P registrado.");
        else Log.Warn($"Ctrl+Alt+P já é usado por outro programa (erro {Marshal.GetLastWin32Error()}).");
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && (int)m.WParam == HotkeyId) Pressed?.Invoke();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (IsRegistered) UnregisterHotKey(Handle, HotkeyId);
        DestroyHandle();
    }
}
