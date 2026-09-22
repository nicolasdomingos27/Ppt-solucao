namespace PptLock;

/// <summary>Aplicação sem janela principal: só o ícone na bandeja.</summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly AppConfig _config;
    private readonly PowerPointController _ppt;
    private readonly RawInputListener _rawInput;
    private readonly PresenterMatcher _matcher;
    private readonly KeyRouter _router;
    private readonly KeyboardHook _hook;
    private readonly NotifyIcon _icon;
    private SetupForm? _setup;

    public TrayContext()
    {
        _config = AppConfig.Load();
        _ppt = new PowerPointController();
        _matcher = new PresenterMatcher(_config.Presenter);

        _rawInput = new RawInputListener();
        _router = new KeyRouter(_ppt, _matcher, () => _config.EscapeAction);
        _rawInput.KeyReceived += _router.OnRaw;
        _rawInput.DevicesChanged += OnDevicesChanged;

        _hook = new KeyboardHook(_router.OnHook);
        _hook.Install();

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem($"PPT Lock versão {Application.ProductVersion.Split('+')[0]}") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Reconfigurar passador", null, (_, _) => ShowSetup());
        menu.Items.Add("Abrir log", null, (_, _) => OpenLog());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => ExitThread());

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => ShowSetup();

        ApplyPresenter();
        if (_config.Presenter is null)
        {
            Log.Info("Nenhum passador configurado; abrindo tela de configuração.");
            ShowSetup();
        }
        else
        {
            Log.Info($"Passador configurado: {_config.Presenter.Name} (VID {_config.Presenter.VendorId} / PID {_config.Presenter.ProductId})");
        }
    }

    private void ApplyPresenter()
    {
        _matcher.Presenter = _config.Presenter;
        _router.Enabled = _config.Presenter is not null && _setup is null;
        var text = _config.Presenter is null ? "PPT Lock — passador não configurado" : $"PPT Lock — {_config.Presenter.Name}";
        _icon.Text = text.Length > 63 ? text[..63] : text; // limite do Windows
    }

    private void ShowSetup()
    {
        if (_setup is not null)
        {
            _setup.Activate();
            return;
        }
        _setup = new SetupForm(_rawInput, _config);
        _router.Enabled = false; // durante a configuração nada é interceptado
        _setup.FormClosed += (_, _) =>
        {
            var form = _setup;
            _setup = null;
            if (form.DialogResult == DialogResult.OK && form.Result is not null)
            {
                _config.Presenter = form.Result;
                _config.EscapeAction = form.EscapeChoice;
                if (!_config.TrySave())
                {
                    MessageBox.Show(
                        "Não foi possível salvar o config.json na pasta do programa (pendrive protegido contra gravação?).\n" +
                        "O passador vai funcionar agora, mas será pedido de novo na próxima vez.",
                        "PPT Lock", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                _icon.ShowBalloonTip(3000, "PPT Lock", $"Passador configurado: {form.Result.Name}", ToolTipIcon.Info);
            }
            ApplyPresenter();
        };
        _setup.Show();
    }

    private void OnDevicesChanged(bool arrived)
    {
        // Handles do Raw Input mudam quando o USB é reconectado; recalcula.
        _matcher.Reset();
        Log.Info(arrived ? "Teclado/dispositivo conectado." : "Teclado/dispositivo removido.");
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
        _setup?.Close();
        _hook.Dispose();
        _router.Dispose();
        _rawInput.Dispose();
        _ppt.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        base.ExitThreadCore();
    }
}
