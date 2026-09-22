using System.Runtime.InteropServices;
using static PptLock.NativeMethods;

namespace PptLock;

/// <summary>Uma tecla vista pelo hook global.</summary>
internal readonly record struct KeyEvent(int VirtualKey, int ScanCode, bool IsDown, bool IsExtended, bool IsInjected);

/// <summary>
/// Hook global WH_KEYBOARD_LL. Precisa ser instalado numa thread com loop de
/// mensagens (a thread da UI do WinForms). O callback tem que ser rápido: se
/// demorar mais que LowLevelHooksTimeout o Windows remove o hook sem avisar,
/// por isso aqui só se decide "engolir ou deixar passar"; o resto acontece fora.
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    /// <summary>Retorna true para engolir a tecla, false para deixá-la seguir.</summary>
    public delegate bool KeyHandler(KeyEvent e);

    private readonly KeyHandler _handler;
    // Referência mantida em campo para o GC não coletar o delegate usado pelo Windows.
    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hook;

    public KeyboardHook(KeyHandler handler)
    {
        _handler = handler;
        _proc = Callback;
    }

    public void Install()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
            throw new InvalidOperationException($"SetWindowsHookEx falhou (erro {Marshal.GetLastWin32Error()}).");
        Log.Info("Hook de teclado instalado.");
    }

    public void Dispose()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        Log.Info("Hook de teclado removido.");
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                int msg = (int)wParam;
                bool isDown = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
                bool isUp = msg is WM_KEYUP or WM_SYSKEYUP;
                if (isDown || isUp)
                {
                    var e = new KeyEvent((int)data.vkCode, (int)data.scanCode, isDown,
                        (data.flags & LLKHF_EXTENDED) != 0, (data.flags & LLKHF_INJECTED) != 0);
                    if (_handler(e)) return (IntPtr)1; // engole
                }
            }
            catch (Exception ex)
            {
                // Regra de ouro: qualquer erro deixa a tecla passar.
                Log.Error("Erro no hook de teclado", ex);
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }
}
