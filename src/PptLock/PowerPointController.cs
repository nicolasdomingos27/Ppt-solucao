using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace PptLock;

internal enum SlideAction
{
    Next,
    Previous,
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
    private volatile bool _slideShowActive;
    private string? _lastPollError; // evita repetir o mesmo erro no log a cada poll

    public PowerPointController()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "PptLock.COM" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>Último estado conhecido (atualizado a cada ~400 ms).</summary>
    public bool IsSlideShowActive => _slideShowActive;

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
        bool active;
        try
        {
            var app = com.GetPowerPoint();
            active = app is not null && (int)com.Track(app.SlideShowWindows).Count > 0;
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
            active = false;
        }

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
                if (view is null)
                {
                    Log.Warn($"{action}: nenhuma apresentação em andamento.");
                    return;
                }
                switch (action)
                {
                    case SlideAction.Next: view.Next(); break;
                    case SlideAction.Previous: view.Previous(); break;
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

    private static dynamic? GetSlideShowView(ComScope com)
    {
        var app = com.GetPowerPoint();
        if (app is null) return null;
        var windows = com.Track(app.SlideShowWindows);
        if ((int)windows.Count == 0) return null;
        var window = com.Track(windows.Item(1));
        return com.Track(window.View);
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
