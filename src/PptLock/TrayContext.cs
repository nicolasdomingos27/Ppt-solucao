namespace PptLock;

/// <summary>Aplicação sem janela principal: só o ícone na bandeja.</summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly PowerPointController _ppt;
    private readonly KeyboardHook _hook;
    private readonly NotifyIcon _icon;

    public TrayContext()
    {
        _ppt = new PowerPointController();
        var router = new KeyRouter(_ppt);
        _hook = new KeyboardHook(router.Handle);
        _hook.Install();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Abrir log", null, (_, _) => OpenLog());
        menu.Items.Add("Sair", null, (_, _) => ExitThread());

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "PPT Lock (protótipo)",
            ContextMenuStrip = menu,
            Visible = true,
        };
    }

    private static void OpenLog()
    {
        try
        {
            if (!File.Exists(Log.FilePath)) File.WriteAllText(Log.FilePath, "");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Log.FilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Não foi possível abrir o log", ex);
        }
    }

    protected override void ExitThreadCore()
    {
        _hook.Dispose();
        _ppt.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        base.ExitThreadCore();
    }
}
