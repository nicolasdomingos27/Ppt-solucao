namespace PptLock;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Duas instâncias = cada clique do passador avançaria dois slides.
        using var mutex = new Mutex(true, @"Local\PptLock.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            MessageBox.Show("O PPT Lock já está rodando (veja o ícone na bandeja).", "PPT Lock",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error("Exceção não tratada (UI)", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("Exceção não tratada", e.ExceptionObject as Exception);

        ApplicationConfiguration.Initialize();
        Log.Info($"PPT Lock iniciado. Pasta: {AppPaths.BaseDirectory}");

        try
        {
            Application.Run(new TrayContext());
        }
        finally
        {
            Log.Info("PPT Lock encerrado.");
            Log.Flush(TimeSpan.FromSeconds(1));
        }
    }
}
