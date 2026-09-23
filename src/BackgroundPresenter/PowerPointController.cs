using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace BackgroundPresenter;

/// <summary>Slide atual da apresentação em andamento, para a dica do ícone.</summary>
internal sealed record SlideShowInfo(int Slide, int Total, string FileName);

internal enum SlideAction
{
    Next,
    Previous,
    ToggleBlack,
    ToggleWhite,
    StartFromBeginning,
    StartFromCurrent,
    EndShow,
}

/// <summary>
/// Fala com o PowerPoint via COM (late binding com dynamic, sem depender da
/// versão do Office). Todo acesso COM acontece numa única thread STA dedicada:
/// - o hook de teclado nunca espera por chamadas entre processos;
/// - um poll periódico mantém <see cref="IsSlideShowActive"/> atualizado, e é
///   esse valor em cache que o hook consulta para decidir se engole a tecla.
///
/// Não guardamos referência ao PowerPoint entre operações: cada operação pega
/// o objeto ativo na ROT e solta tudo no final. Assim, se o usuário fechar o
/// PowerPoint, o processo não fica preso por nossa causa, e quando ele for
/// reaberto a próxima operação já encontra a nova instância.
/// </summary>
internal sealed class PowerPointController : IDisposable
{
    private const int PollIntervalMs = 400;

    // HRESULTs de "PowerPoint ocupado" (diálogo aberto, editando etc.).
    private const int RPC_E_CALL_REJECTED = unchecked((int)0x80010001);
    private const int RPC_E_SERVERCALL_RETRYLATER = unchecked((int)0x8001010A);

    private readonly BlockingCollection<SlideAction> _queue = new();
    private readonly Thread _thread;
    // PpSlideShowState
    private const int ppSlideShowRunning = 1;
    private const int ppSlideShowBlackScreen = 3;
    private const int ppSlideShowWhiteScreen = 4;
    // PpSlideShowRangeType
    private const int ppShowSlideRange = 2;

    private volatile bool _slideShowActive;
    private volatile bool _hasPresentation;
    private volatile SlideShowInfo? _showInfo;
    private string? _lastPollError; // evita repetir o mesmo erro no log a cada poll

    public PowerPointController()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "BackgroundPresenter.COM" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>Último estado conhecido (atualizado a cada ~400 ms).</summary>
    public bool IsSlideShowActive => _slideShowActive;

    /// <summary>PowerPoint aberto com ao menos uma apresentação (para F5 funcionar).</summary>
    public bool HasPresentation => _hasPresentation;

    /// <summary>Slide X de Y da apresentação em andamento (null se não houver).</summary>
    public SlideShowInfo? ShowInfo => _showInfo;

    /// <summary>Enfileira uma ação; retorna na hora (seguro para chamar do hook).</summary>
    public void Enqueue(SlideAction action) => _queue.TryAdd(action);

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(2));
    }

    private void Run()
    {
        long nextPoll = 0;
        while (!_queue.IsCompleted)
        {
            try
            {
                if (_queue.TryTake(out var action, PollIntervalMs))
                    Execute(action);

                if (Environment.TickCount64 >= nextPoll)
                {
                    Poll();
                    nextPoll = Environment.TickCount64 + PollIntervalMs;
                }
            }
            catch (InvalidOperationException) when (_queue.IsCompleted)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error("Erro inesperado na thread COM", ex);
            }
        }
    }

    private void Poll()
    {
        using var com = new ComScope();
        bool active, hasPresentation;
        try
        {
            var app = com.GetPowerPoint();
            hasPresentation = app is not null && (int)com.Track(app.Presentations).Count > 0;
            active = hasPresentation && (int)com.Track(app!.SlideShowWindows).Count > 0;
            _showInfo = active ? ReadShowInfo(com) ?? _showInfo : null;
            _lastPollError = null;
        }
        catch (COMException ex) when (IsBusy(ex))
        {
            return; // ocupado: mantém o último estado conhecido
        }
        catch (Exception ex)
        {
            if (ex.Message != _lastPollError) Log.Warn($"Falha ao consultar PowerPoint: {ex.Message}");
            _lastPollError = ex.Message;
            active = hasPresentation = false;
            _showInfo = null;
        }

        _hasPresentation = hasPresentation;

        if (active != _slideShowActive)
        {
            _slideShowActive = active;
            Log.Info(active ? "Apresentação em andamento detectada." : "Nenhuma apresentação em andamento.");
        }
    }

    private void Execute(SlideAction action)
    {
        // Algumas tentativas curtas caso o PowerPoint esteja momentaneamente ocupado.
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            using var com = new ComScope();
            try
            {
                var view = GetSlideShowView(com);
                if (action is SlideAction.StartFromBeginning or SlideAction.StartFromCurrent)
                {
                    if (view is not null)
                    {
                        // Já está apresentando: F5 acidental não pode voltar ao slide 1 ao vivo.
                        Log.Info($"{action} ignorado: apresentação já em andamento.");
                        return;
                    }
                    StartShow(com, fromCurrent: action == SlideAction.StartFromCurrent);
                    return;
                }
                if (view is null)
                {
                    Log.Warn($"{action}: nenhuma apresentação em andamento.");
                    return;
                }
                switch (action)
                {
                    case SlideAction.Next: view.Next(); break;
                    case SlideAction.Previous: view.Previous(); break;
                    case SlideAction.ToggleBlack: ToggleScreen(view, ppSlideShowBlackScreen); break;
                    case SlideAction.ToggleWhite: ToggleScreen(view, ppSlideShowWhiteScreen); break;
                    case SlideAction.EndShow: view.Exit(); break;
                }
                Log.Info($"{action} executado.");
                return;
            }
            catch (COMException ex) when (IsBusy(ex) && attempt < 5)
            {
                Thread.Sleep(40);
            }
            catch (Exception ex)
            {
                Log.Error($"{action} falhou", ex);
                return;
            }
        }
    }

    /// <summary>
    /// View da apresentação em andamento. Com mais de uma rodando, usa a última
    /// iniciada (a mais recente na coleção SlideShowWindows).
    /// </summary>
    private static dynamic? GetSlideShowView(ComScope com)
    {
        var app = com.GetPowerPoint();
        if (app is null) return null;
        var windows = com.Track(app.SlideShowWindows);
        int count = (int)windows.Count;
        if (count == 0) return null;
        var window = com.Track(windows.Item(count));
        return com.Track(window.View);
    }

    private static SlideShowInfo? ReadShowInfo(ComScope com)
    {
        try
        {
            var app = com.GetPowerPoint();
            if (app is null) return null;
            var windows = com.Track(app.SlideShowWindows);
            var window = com.Track(windows.Item((int)windows.Count));
            var presentation = com.Track(window.Presentation);
            int total = (int)com.Track(presentation.Slides).Count;
            var view = com.Track(window.View);
            var slide = com.Track(view.Slide);
            return new SlideShowInfo((int)slide.SlideIndex, total, (string)presentation.Name);
        }
        catch (Exception)
        {
            return null; // ex.: tela preta do fim da apresentação não tem "slide"
        }
    }

    private static void ToggleScreen(dynamic view, int screenState)
    {
        view.State = (int)view.State == screenState ? ppSlideShowRunning : screenState;
    }

    /// <summary>
    /// F5 / Shift+F5. Usa um intervalo temporário nas configurações da
    /// apresentação e depois restaura o que o usuário tinha (inclusive o
    /// "Saved", para o PowerPoint não perguntar se quer salvar ao fechar).
    /// </summary>
    private static void StartShow(ComScope com, bool fromCurrent)
    {
        var app = com.GetPowerPoint();
        if (app is null) return;
        var presentation = GetTargetPresentation(com, app);
        if (presentation is null)
        {
            Log.Warn("F5: nenhuma apresentação aberta.");
            return;
        }

        int total = (int)com.Track(presentation.Slides).Count;
        if (total == 0) return;
        int start = fromCurrent ? Math.Clamp(GetCurrentSlideIndex(com, presentation), 1, total) : 1;

        var settings = com.Track(presentation.SlideShowSettings);
        int oldSaved = (int)presentation.Saved;
        int oldRange = (int)settings.RangeType;
        int oldStart = (int)settings.StartingSlide;
        int oldEnd = (int)settings.EndingSlide;

        settings.RangeType = ppShowSlideRange;
        settings.StartingSlide = start;
        settings.EndingSlide = total;
        try
        {
            com.Track(settings.Run());
            Log.Info($"Apresentação iniciada no slide {start} de {total}.");
        }
        finally
        {
            try
            {
                settings.StartingSlide = oldStart;
                settings.EndingSlide = oldEnd;
                settings.RangeType = oldRange;
                presentation.Saved = oldSaved;
            }
            catch (Exception ex)
            {
                Log.Warn($"Não foi possível restaurar as configurações da apresentação: {ex.Message}");
            }
        }
    }

    private static dynamic? GetTargetPresentation(ComScope com, dynamic app)
    {
        var presentations = com.Track(app.Presentations);
        if ((int)presentations.Count == 0) return null;
        try
        {
            return com.Track(app.ActivePresentation);
        }
        catch (COMException)
        {
            return com.Track(presentations.Item(1)); // sem janela ativa
        }
    }

    private static int GetCurrentSlideIndex(ComScope com, dynamic presentation)
    {
        try
        {
            var windows = com.Track(presentation.Windows);
            if ((int)windows.Count == 0) return 1;
            var window = com.Track(windows.Item(1));
            var view = com.Track(window.View);
            var slide = com.Track(view.Slide);
            return (int)slide.SlideIndex;
        }
        catch (Exception)
        {
            return 1; // modos de exibição sem "slide atual" (ex.: estrutura de tópicos)
        }
    }

    private static bool IsBusy(COMException ex) =>
        ex.HResult is RPC_E_CALL_REJECTED or RPC_E_SERVERCALL_RETRYLATER;

    /// <summary>
    /// Guarda todos os objetos COM obtidos numa operação e libera todos no
    /// Dispose, para não deixar referências penduradas no PowerPoint.
    /// </summary>
    private sealed class ComScope : IDisposable
    {
        private readonly List<object> _objects = new();

        public dynamic? GetPowerPoint()
        {
            if (NativeMethods.CLSIDFromProgID("PowerPoint.Application", out var clsid) != 0)
                return null; // Office não instalado
            if (NativeMethods.GetActiveObject(ref clsid, IntPtr.Zero, out var app) != 0 || app is null)
                return null; // PowerPoint não está aberto
            return Track(app);
        }

        public dynamic Track(object obj)
        {
            _objects.Add(obj);
            return obj;
        }

        public void Dispose()
        {
            for (int i = _objects.Count - 1; i >= 0; i--)
            {
                try
                {
                    if (Marshal.IsComObject(_objects[i])) Marshal.FinalReleaseComObject(_objects[i]);
                }
                catch
                {
                    // já liberado / servidor caiu: nada a fazer
                }
            }
            _objects.Clear();
        }
    }
}
