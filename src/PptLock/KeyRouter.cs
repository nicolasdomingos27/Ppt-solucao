using static PptLock.NativeMethods;

namespace PptLock;

/// <summary>
/// Decide, para cada tecla, se ela vira comando do PowerPoint ou se segue
/// normalmente para a janela em foco. Só teclas do PASSADOR configurado viram
/// comando; o teclado do operador continua funcionando.
///
/// ======================= COMO A CORRELAÇÃO FUNCIONA =======================
///
/// Temos duas fontes de informação, cada uma com metade do que precisamos:
///
///   Hook WH_KEYBOARD_LL  -> PODE bloquear a tecla, mas NÃO sabe o aparelho.
///   Raw Input (WM_INPUT) -> SABE o aparelho, mas NÃO pode bloquear.
///
/// Problema de ordem: para cada tecla, o Windows chama o hook ANTES de postar
/// o WM_INPUT. Enquanto o hook roda, o WM_INPUT daquela tecla ainda não
/// existe, e não dá para esperar por ele dentro do hook: o Windows fica
/// parado aguardando o hook terminar e só depois posta o WM_INPUT.
///
/// Solução, "segura e decide":
///
///  1. No hook, se a tecla é candidata (tecla de passador + PowerPoint no
///     estado certo), ela é ENGOLIDA na hora e entra numa fila de pendentes
///     (_pending), com o momento em que chegou.
///
///  2. Alguns milissegundos depois chega o WM_INPUT. Ele é casado com o
///     primeiro pendente ainda sem aparelho que tenha a mesma tecla e o mesmo
///     sentido (apertar/soltar). A partir daí o pendente sabe se veio do
///     passador.
///
///  3. A fila é resolvida em ordem (FIFO), a partir do início:
///       - veio do passador     -> vira comando do PowerPoint (key-up é descartado);
///       - veio de outro teclado -> a tecla é REENVIADA com SendInput, e a
///         janela em foco a recebe normalmente, só alguns ms depois.
///     Se o WM_INPUT não chegar em RawWaitTimeoutMs, a tecla é tratada como
///     do operador e reenviada. Assim nenhuma tecla se perde, mesmo que o
///     Raw Input falhe.
///
///  4. Caso raro de ordem inversa (WM_INPUT antes do hook): o evento Raw fica
///     guardado por EarlyRawKeepMs em _earlyRaw, e o hook procura ali antes
///     de criar o pendente.
///
/// Teclas reenviadas chegam de novo ao hook marcadas como "injetadas" e
/// passam direto, então não há laço.
///
/// Soltar/apertar ficam pareados: se o "apertar" de uma tecla foi engolido,
/// o "soltar" dela também passa pela fila, e assim o operador recebe o par
/// completo e na ordem certa.
///
/// Tudo roda na thread da UI (hook, WM_INPUT e timer), então não há lock.
/// </summary>
internal sealed class KeyRouter : IDisposable
{
    private const int RawWaitTimeoutMs = 150;
    private const int EarlyRawKeepMs = 60;

    private sealed class Pending
    {
        public required KeyEvent Key { get; init; }
        public required bool Shift { get; init; }
        public required long Tick { get; init; }
        public bool? FromPresenter { get; set; }
    }

    private readonly PowerPointController _ppt;
    private readonly PresenterMatcher _matcher;
    private readonly Func<EscapeAction> _escapeAction;
    private readonly SynchronizationContext _ui;
    private readonly System.Windows.Forms.Timer _timer;

    private readonly LinkedList<Pending> _pending = new();
    private readonly List<RawKey> _earlyRaw = new();
    // Teclas cujo "apertar" engolimos e cujo "soltar" ainda não passou.
    private readonly HashSet<int> _held = new();

    public KeyRouter(PowerPointController ppt, PresenterMatcher matcher, Func<EscapeAction> escapeAction)
    {
        _ppt = ppt;
        _matcher = matcher;
        _escapeAction = escapeAction;
        _ui = SynchronizationContext.Current
              ?? throw new InvalidOperationException("KeyRouter precisa ser criado na thread da UI.");
        _timer = new System.Windows.Forms.Timer { Interval = 15 };
        _timer.Tick += (_, _) => Flush();
    }

    /// <summary>Liga/desliga a interceptação (sem passador configurado, pausado, etc.).</summary>
    public bool Enabled { get; set; }

    // ------------------------------------------------------------------ hook

    /// <summary>Chamado pelo hook. true = engolir a tecla.</summary>
    public bool OnHook(KeyEvent e)
    {
        // Nossas próprias reenviadas e as de outros programas passam direto.
        if (e.IsInjected) return false;

        bool held = _held.Contains(e.VirtualKey);
        if (e.IsDown)
        {
            if (!held && !IsCandidate(e.VirtualKey)) return false;
            _held.Add(e.VirtualKey); // auto-repeat de tecla segurada também cai aqui
        }
        else
        {
            if (!held) return false;
            _held.Remove(e.VirtualKey);
        }

        long now = Environment.TickCount64;
        var p = new Pending { Key = e, Shift = IsKeyDown(VK_SHIFT), Tick = now };

        int early = _earlyRaw.FindIndex(r => Matches(r, e) && now - r.Tick <= EarlyRawKeepMs);
        if (early >= 0)
        {
            p.FromPresenter = _matcher.IsPresenter(_earlyRaw[early].Device);
            _earlyRaw.RemoveAt(early);
            // Resolver fora do hook: SendInput dentro do hook não é seguro.
            _ui.Post(_ => Flush(), null);
        }

        _pending.AddLast(p);
        _timer.Enabled = true;
        return true;
    }

    private bool IsCandidate(int vk)
    {
        if (!Enabled) return false;
        // Com Ctrl/Alt/Win pressionados é atalho do operador (ex.: Ctrl+Seta).
        if (IsKeyDown(VK_CONTROL) || IsKeyDown(VK_MENU) || IsKeyDown(VK_LWIN) || IsKeyDown(VK_RWIN))
            return false;
        if (!KeyMap.IsPresenterKey(vk)) return false;
        return KeyMap.WorksWithoutSlideShow(vk) ? _ppt.HasPresentation : _ppt.IsSlideShowActive;
    }

    // ------------------------------------------------------------- Raw Input

    public void OnRaw(RawKey r)
    {
        if (!KeyMap.IsPresenterKey(r.VirtualKey)) return;

        for (var node = _pending.First; node is not null; node = node.Next)
        {
            var p = node.Value;
            if (p.FromPresenter is null && Matches(r, p.Key))
            {
                p.FromPresenter = _matcher.IsPresenter(r.Device);
                Flush();
                return;
            }
        }

        // Nenhum pendente esperando por ele: guarda caso o hook venha depois.
        long now = r.Tick;
        _earlyRaw.RemoveAll(x => now - x.Tick > EarlyRawKeepMs);
        _earlyRaw.Add(r);
    }

    private static bool Matches(RawKey r, KeyEvent e) => r.VirtualKey == e.VirtualKey && r.IsUp == !e.IsDown;

    // --------------------------------------------------------------- resolve

    private void Flush()
    {
        long now = Environment.TickCount64;
        while (_pending.First is { } node)
        {
            var p = node.Value;
            if (p.FromPresenter is null)
            {
                if (now - p.Tick < RawWaitTimeoutMs) break; // ainda esperando o WM_INPUT
                Log.Warn($"Sem Raw Input para {(Keys)p.Key.VirtualKey}; devolvendo a tecla.");
                p.FromPresenter = false;
            }
            _pending.RemoveFirst();

            try
            {
                if (p.FromPresenter == true)
                {
                    if (p.Key.IsDown) Execute(p);
                }
                else
                {
                    Replay(p.Key);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Erro ao resolver tecla", ex);
            }
        }
        _timer.Enabled = _pending.Count > 0;
    }

    private void Execute(Pending p)
    {
        var action = KeyMap.ToAction(p.Key.VirtualKey, p.Shift, _escapeAction());
        if (action is null)
        {
            Log.Info($"Passador: {(Keys)p.Key.VirtualKey} ignorado.");
            return;
        }
        _ppt.Enqueue(action.Value);
    }

    /// <summary>Devolve a tecla do operador para a janela em foco.</summary>
    private static void Replay(KeyEvent e)
    {
        uint flags = 0;
        if (e.IsExtended) flags |= KEYEVENTF_EXTENDEDKEY;
        if (!e.IsDown) flags |= KEYEVENTF_KEYUP;
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT { wVk = (ushort)e.VirtualKey, wScan = (ushort)e.ScanCode, dwFlags = flags },
            },
        };
        if (SendInput(1, new[] { input }, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>()) != 1)
            Log.Error($"SendInput falhou para {(Keys)e.VirtualKey} (erro {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}).");
    }

    public void Dispose()
    {
        // Sai devolvendo tudo que estiver pendente.
        foreach (var p in _pending) p.FromPresenter ??= false;
        Flush();
        _timer.Dispose();
    }
}

/// <summary>Tabela de teclas do passador e o que cada uma faz.</summary>
internal static class KeyMap
{
    public static bool IsPresenterKey(int vk) => (Keys)vk is
        Keys.PageDown or Keys.PageUp or Keys.Right or Keys.Left or Keys.Down or Keys.Up or
        Keys.Space or Keys.Enter or Keys.B or Keys.W or Keys.OemPeriod or Keys.F5 or Keys.Escape;

    /// <summary>F5 serve para iniciar a apresentação, então vale mesmo sem show rodando.</summary>
    public static bool WorksWithoutSlideShow(int vk) => (Keys)vk == Keys.F5;

    public static SlideAction? ToAction(int vk, bool shift, EscapeAction escape) => (Keys)vk switch
    {
        Keys.PageDown or Keys.Right or Keys.Down or Keys.Space or Keys.Enter => SlideAction.Next,
        Keys.PageUp or Keys.Left or Keys.Up => SlideAction.Previous,
        Keys.B or Keys.OemPeriod => SlideAction.ToggleBlack,
        Keys.W => SlideAction.ToggleWhite,
        Keys.F5 => shift ? SlideAction.StartFromCurrent : SlideAction.StartFromBeginning,
        Keys.Escape => escape == EscapeAction.EndShow ? SlideAction.EndShow : null,
        _ => null,
    };
}
