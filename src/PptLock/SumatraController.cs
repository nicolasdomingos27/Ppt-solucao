using static PptLock.NativeMethods;

namespace PptLock;

/// <summary>
/// Controle do Sumatra PDF. Ele não tem uma API como o COM do PowerPoint,
/// então entregamos a tecla direto na janela dele com PostMessage
/// (WM_KEYDOWN/WM_KEYUP). Isso funciona mesmo com outra janela em foco,
/// porque a mensagem vai para a fila da janela do Sumatra, e não para a
/// janela em foco. As teclas passam pelo próprio loop de mensagens do
/// Sumatra, então valem os atalhos dele (Seta, PageDown, B, W, ponto…).
///
/// Só consideramos "apresentando" um Sumatra em tela cheia ou em modo
/// apresentação (a janela cobre o monitor inteiro). Um PDF aberto numa
/// janela comum não é afetado.
///
/// Tudo roda na thread da UI: são chamadas baratas, sem COM.
/// </summary>
internal sealed class SumatraController : IDisposable
{
    private const string FrameClass = "SUMATRA_PDF_FRAME";
    private const string CanvasClass = "SUMATRA_PDF_CANVAS";

    private readonly System.Windows.Forms.Timer _poll;
    private IntPtr _frame;

    public SumatraController()
    {
        _poll = new System.Windows.Forms.Timer { Interval = 400 };
        _poll.Tick += (_, _) => Poll();
        _poll.Start();
        Poll();
    }

    /// <summary>Há um Sumatra em tela cheia/modo apresentação.</summary>
    public bool IsPresenting => _frame != IntPtr.Zero;

    private void Poll()
    {
        try
        {
            var frame = FindPresentingFrame();
            if ((frame != IntPtr.Zero) != IsPresenting)
                Log.Info(frame != IntPtr.Zero ? "Sumatra PDF em tela cheia detectado." : "Sumatra PDF saiu da tela cheia.");
            _frame = frame;
        }
        catch (Exception ex)
        {
            Log.Error("Erro ao procurar o Sumatra PDF", ex);
            _frame = IntPtr.Zero;
        }
    }

    /// <summary>EnumWindows vai do topo para baixo: se houver mais de um, fica o que está por cima.</summary>
    private static IntPtr FindPresentingFrame()
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            if (GetClassName(hwnd) == FrameClass && IsWindowVisible(hwnd) && !IsIconic(hwnd) && CoversMonitor(hwnd))
            {
                found = hwnd;
                return false; // para a busca
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static bool CoversMonitor(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var r)) return false;
        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref info)) return false;
        var m = info.rcMonitor;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }

    /// <summary>A área do documento (canvas) visível; com abas, só a da aba ativa está visível.</summary>
    private static IntPtr FindCanvas(IntPtr frame)
    {
        IntPtr found = IntPtr.Zero;
        EnumChildWindows(frame, (hwnd, _) =>
        {
            if (GetClassName(hwnd) == CanvasClass && IsWindowVisible(hwnd))
            {
                found = hwnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Entrega um toque (apertar + soltar) da tecla na janela do Sumatra.</summary>
    public bool SendKey(KeyEvent key)
    {
        var frame = _frame;
        if (frame == IntPtr.Zero || !IsWindow(frame)) return false;
        var target = FindCanvas(frame);
        if (target == IntPtr.Zero) target = frame;

        // lParam de WM_KEYDOWN/UP: repetição=1, scan code, bit 24 = tecla estendida,
        // bits 30/31 = estado anterior/transição (ligados no UP).
        long baseParam = 1 | ((long)(key.ScanCode & 0xFF) << 16) | (key.IsExtended ? 1L << 24 : 0);
        long upParam = baseParam | (1L << 30) | (1L << 31);
        bool ok = PostMessage(target, WM_KEYDOWN, (IntPtr)key.VirtualKey, (IntPtr)baseParam)
                  && PostMessage(target, WM_KEYUP, (IntPtr)key.VirtualKey, (IntPtr)upParam);
        if (ok) Log.Info($"Sumatra: {(Keys)key.VirtualKey} enviado.");
        else Log.Error($"Não foi possível enviar {(Keys)key.VirtualKey} ao Sumatra (erro {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}).");
        return ok;
    }

    public void Dispose() => _poll.Dispose();
}
