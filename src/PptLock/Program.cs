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
            Log.WriteNow("Exceção não tratada", e.ExceptionObject as Exception);

        ApplicationConfiguration.Initialize();
        Log.Info($"PPT Lock {Application.ProductVersion} iniciado. Pasta: {AppPaths.BaseDirectory}");

        if (AppPaths.LooksLikeInsideZip())
        {
            MessageBox.Show(
                "Parece que o PPT Lock foi aberto de dentro do arquivo .zip.\n\n" +
                "Assim ele não consegue guardar a configuração nem o log. Feche, extraia o .zip " +
                "(botão direito > Extrair tudo) e abra o PptLock.exe da pasta extraída.",
                "PPT Lock", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
                MessageBox.Show($"O PPT Lock não conseguiu iniciar:\n\n{ex.Message}\n\nDetalhes em:\n{Log.FilePath}",
                    "PPT Lock", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            Application.Run(context);
        }
        finally
        {
            Log.Info("PPT Lock encerrado.");
            Log.Flush(TimeSpan.FromSeconds(1));
        }
    }
}
