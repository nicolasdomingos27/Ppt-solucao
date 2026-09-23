namespace BackgroundPresenter;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Duas instâncias = cada clique do passador avançaria dois slides.
        using var mutex = new Mutex(true, @"Local\BackgroundPresenter.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            MessageBox.Show("O Background Presenter já está rodando (veja o ícone na bandeja).", "Background Presenter",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        // Versões antigas se chamavam "PPT Lock": rodando junto, cada clique contaria duas vezes.
        using var legacy = new Mutex(false, @"Local\PptLock.SingleInstance", out bool legacyFree);
        if (!legacyFree)
        {
            MessageBox.Show("Uma versão antiga (PPT Lock) ainda está aberta.\n\n" +
                            "Feche-a antes: botão direito no ícone dela, perto do relógio, e depois Sair.",
                "Background Presenter", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error("Exceção não tratada (UI)", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.WriteNow("Exceção não tratada", e.ExceptionObject as Exception);

        ApplicationConfiguration.Initialize();
        Log.Info($"Background Presenter {Application.ProductVersion} iniciado. Pasta: {AppPaths.BaseDirectory}");

        if (AppPaths.LooksLikeInsideZip())
        {
            MessageBox.Show(
                "Parece que o Background Presenter foi aberto de dentro do arquivo .zip.\n\n" +
                "Assim ele não consegue guardar a configuração nem o log. Feche, extraia o .zip " +
                "(botão direito > Extrair tudo) e abra o BackgroundPresenter.exe da pasta extraída.",
                "Background Presenter", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        try
        {
            TrayContext context;
            try
            {
                context = new TrayContext();
            }
            catch (Exception ex)
            {
                Log.WriteNow("Falha ao iniciar", ex);
                MessageBox.Show($"O Background Presenter não conseguiu iniciar:\n\n{ex.Message}\n\nDetalhes em:\n{Log.FilePath}",
                    "Background Presenter", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            Application.Run(context);
        }
        finally
        {
            Log.Info("Background Presenter encerrado.");
            Log.Flush(TimeSpan.FromSeconds(1));
        }
    }
}
