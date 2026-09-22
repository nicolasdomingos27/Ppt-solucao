using static PptLock.NativeMethods;

namespace PptLock;

/// <summary>
/// Decide, para cada tecla vista pelo hook, se ela vira comando do PowerPoint
/// (e é engolida) ou se segue normalmente para a janela em foco.
///
/// ETAPA 1 (protótipo): ainda não distingue o passador do teclado normal.
/// Enquanto houver apresentação em andamento, as teclas de navegação de
/// QUALQUER teclado são desviadas para o PowerPoint.
///
/// Roda na thread da UI (a mesma do hook), então não precisa de lock.
/// </summary>
internal sealed class KeyRouter
{
    private readonly PowerPointController _ppt;

    // Teclas cujo "down" foi engolido; o "up" correspondente também é engolido
    // para a janela em foco não receber um key-up órfão.
    private readonly HashSet<int> _swallowed = new();

    public KeyRouter(PowerPointController ppt) => _ppt = ppt;

    public bool Handle(KeyEvent e)
    {
        // Teclas injetadas por software (SendInput de outros apps ou nosso) passam.
        if (e.IsInjected) return false;

        if (!e.IsDown) return _swallowed.Remove(e.VirtualKey);

        var action = Map(e.VirtualKey);
        if (action is null) return false;

        // Auto-repeat de uma tecla que já engolimos: continua engolindo.
        bool repeat = _swallowed.Contains(e.VirtualKey);

        if (!repeat)
        {
            // Com Ctrl/Alt/Win pressionados é atalho do operador, não passador.
            if (IsKeyDown(VK_CONTROL) || IsKeyDown(VK_MENU) || IsKeyDown(VK_LWIN) || IsKeyDown(VK_RWIN))
                return false;
            if (!_ppt.IsSlideShowActive) return false;
            _swallowed.Add(e.VirtualKey);
        }

        if (_ppt.IsSlideShowActive) _ppt.Enqueue(action.Value);
        return true;
    }

    private static SlideAction? Map(int vk) => (Keys)vk switch
    {
        Keys.PageDown or Keys.Right or Keys.Down or Keys.Space or Keys.Enter => SlideAction.Next,
        Keys.PageUp or Keys.Left or Keys.Up => SlideAction.Previous,
        _ => null,
    };
}
