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
/// Duas regras do Windows definem a solução:
///   (a) o hook é chamado ANTES de o Raw Input ser gerado;
///   (b) se o hook bloqueia um evento, o Raw Input desse evento NUNCA é
///       gerado. Bloquear e esperar o WM_INPUT do mesmo evento não funciona:
///       ele não chega. Esse era o erro da primeira versão da Etapa 2.
///
/// Mas cada toque tem DOIS eventos: apertar (down) e soltar (up). Então:
///
///  1. DOWN de tecla candidata (tecla de passador + PowerPoint no estado
///     certo): o hook BLOQUEIA e cria um pendente na fila (_pending).
///     Repetições automáticas (tecla segurada) também são bloqueadas.
///
///  2. UP da mesma tecla: o hook DEIXA PASSAR. Como não foi bloqueado, o
///     Windows gera o WM_INPUT dele, que diz de qual aparelho veio. Um key-up
///     solto na janela em foco é inofensivo (programas agem no down).
///
///  3. Chega o WM_INPUT do UP: casamos com o pendente mais antigo da mesma
///     tecla que já viu o UP. Aí sabemos a origem:
///       - passador      -> comando do PowerPoint;
///       - outro teclado -> reenviamos o par down+up com SendInput, e a
///         janela em foco recebe a tecla normalmente.
///     O custo é que a ação acontece ao SOLTAR o botão (dezenas de ms
///     depois), o que é imperceptível num passador.
///
///  4. Segurança (nenhuma tecla se perde):
///       - UP visto, mas sem WM_INPUT em RawAfterUpTimeoutMs -> é tratada
///         como tecla do operador e reenviada;
///       - tecla segurada por mais de HoldTimeoutMs sem soltar (operador
///         segurando a seta, por exemplo) -> é reenviada como do operador, e
///         as repetições e o UP dela passam direto até soltar.
///
/// A fila é resolvida em ordem (FIFO) para as teclas reenviadas chegarem na
/// ordem em que foram digitadas. Teclas reenviadas voltam ao hook marcadas
/// como "injetadas" e passam direto, sem laço.
///
/// Tudo roda na thread da UI (hook, WM_INPUT e timer), então não há lock.
/// </summary>
internal sealed class KeyRouter : IDisposable
{
    private const int RawAfterUpTimeoutMs = 120;
    private const int HoldTimeoutMs = 500;

    private sealed class Pending
    {
        public required KeyEvent Down { get; init; }
        public required bool Shift { get; init; }
        public required long Tick { get; init; }
        public KeyEvent? Up { get; set; }
        public long UpTick { get; set; }
        public bool? FromPresenter { get; set; }
    }

    private readonly PowerPointController _ppt;
    private readonly SumatraController _sumatra;
    private readonly PresenterMatcher _matcher;
    private readonly Func<EscapeAction> _escapeAction;
    private readonly System.Windows.Forms.Timer _timer;

    private readonly LinkedList<Pending> _pending = new();
    // Teclas seguradas além do HoldTimeoutMs: já devolvidas ao operador, então
    // repetições e o UP passam direto até ela ser solta.
    private readonly HashSet<int> _passThrough = new();

    public KeyRouter(PowerPointController ppt, SumatraController sumatra, PresenterMatcher matcher,
        Func<EscapeAction> escapeAction)
    {
        _ppt = ppt;
        _sumatra = sumatra;
        _matcher = matcher;
        _escapeAction = escapeAction;
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
        int vk = e.VirtualKey;

        if (_passThrough.Contains(vk))
        {
            if (!e.IsDown) _passThrough.Remove(vk);
            return false;
        }

        var waitingUp = FindWaitingUp(vk);

        if (!e.IsDown)
        {
            // Deixa o UP passar: é ele que gera o WM_INPUT com o aparelho.
            if (waitingUp is not null)
            {
                waitingUp.Up = e;
                waitingUp.UpTick = Environment.TickCount64;
            }
            return false;
        }

        if (waitingUp is not null) return true; // repetição automática: segura junto

        if (!IsCandidate(vk)) return false;

        _pending.AddLast(new Pending { Down = e, Shift = IsKeyDown(VK_SHIFT), Tick = Environment.TickCount64 });
        _timer.Enabled = true;
        return true;
    }

    private Pending? FindWaitingUp(int vk)
    {
        for (var node = _pending.First; node is not null; node = node.Next)
            if (node.Value.Down.VirtualKey == vk && node.Value.Up is null) return node.Value;
        return null;
    }

    private bool IsCandidate(int vk)
    {
        if (!Enabled) return false;
        // Com Ctrl/Alt/Win pressionados é atalho do operador (ex.: Ctrl+Seta).
        if (IsKeyDown(VK_CONTROL) || IsKeyDown(VK_MENU) || IsKeyDown(VK_LWIN) || IsKeyDown(VK_RWIN))
            return false;
        if (!KeyMap.IsPresenterKey(vk)) return false;
        if (_ppt.IsSlideShowActive || _sumatra.IsPresenting) return true;
        return KeyMap.WorksWithoutSlideShow(vk) && _ppt.HasPresentation;
    }

    // ------------------------------------------------------------- Raw Input

    public void OnRaw(RawKey r)
    {
        if (!r.IsUp) return;
        for (var node = _pending.First; node is not null; node = node.Next)
        {
            var p = node.Value;
            if (p.Up is not null && p.FromPresenter is null && p.Down.VirtualKey == r.VirtualKey)
            {
                p.FromPresenter = _matcher.IsPresenter(r.Device);
                Flush();
                return;
            }
        }
    }

    // --------------------------------------------------------------- resolve

    private void Flush()
    {
        long now = Environment.TickCount64;
        while (_pending.First is { } node)
        {
            var p = node.Value;
            if (p.FromPresenter is null)
            {
                if (p.Up is not null)
                {
                    if (now - p.UpTick < RawAfterUpTimeoutMs) break; // esperando o WM_INPUT
                    Log.Warn($"Sem Raw Input para {(Keys)p.Down.VirtualKey}; devolvendo a tecla.");
                    p.FromPresenter = false;
                }
                else
                {
                    if (now - p.Tick < HoldTimeoutMs) break; // ainda apertada
                    // Segurada demais: devolve o DOWN agora; o resto passa direto.
                    _pending.RemoveFirst();
                    _passThrough.Add(p.Down.VirtualKey);
                    Log.Info($"{(Keys)p.Down.VirtualKey} segurada; tratada como teclado do operador.");
                    Replay(p.Down);
                    continue;
                }
            }
            _pending.RemoveFirst();

            try
            {
                if (p.FromPresenter == true)
                {
                    Execute(p);
                }
                else
                {
                    // O UP original já passou; reenvia o par completo.
                    Replay(p.Down);
                    Replay(p.Up!.Value);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Erro ao resolver tecla", ex);
            }
        }
        _timer.Enabled = _pending.Count > 0;
    }

    /// <summary>
    /// Prioridade: apresentação do PowerPoint em andamento > Sumatra em tela
    /// cheia > (só F5) iniciar a apresentação do PowerPoint.
    /// </summary>
    private void Execute(Pending p)
    {
        if (!_ppt.IsSlideShowActive && _sumatra.IsPresenting)
        {
            ExecuteSumatra(p);
            return;
        }
        var action = KeyMap.ToAction(p.Down.VirtualKey, p.Shift, _escapeAction());
        if (action is null)
        {
            Log.Info($"Passador: {(Keys)p.Down.VirtualKey} ignorado.");
            return;
        }
        Log.Info($"Passador: {(Keys)p.Down.VirtualKey} -> {action}");
        _ppt.Enqueue(action.Value);
    }

    private void ExecuteSumatra(Pending p)
    {
        var vk = (Keys)p.Down.VirtualKey;
        // F5 no Sumatra liga/desliga o modo apresentação: ao vivo, sairia da tela cheia.
        if (vk == Keys.F5 || (vk == Keys.Escape && _escapeAction() != EscapeAction.EndShow))
        {
            Log.Info($"Passador: {vk} ignorado no Sumatra.");
            return;
        }
        if (!_sumatra.SendKey(p.Down) && vk != Keys.Escape)
        {
            // Sumatra sumiu entre a detecção e agora: não perder o toque.
            Replay(p.Down);
            Replay(p.Up!.Value);
        }
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
        foreach (var p in _pending)
        {
            Replay(p.Down);
            if (p.Up is { } up) Replay(up);
        }
        _pending.Clear();
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
