namespace BackgroundPresenter;

/// <summary>
/// Aplicação sem janela principal: só o ícone na bandeja.
///
/// Cores do ícone:
///   verde    - passador conectado e apresentação rodando (PowerPoint ou Sumatra)
///   amarelo  - passador conectado, nenhuma apresentação rodando
///   cinza    - pausado (Ctrl+Alt+P ou menu)
///   vermelho - passador não configurado ou não encontrado (USB desconectado)
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private const string AppName = "Background Presenter";

    private enum TrayState { Active, Ready, Paused, NoPresenter }

    private readonly AppConfig _config;
    private readonly PowerPointController _ppt;
    private readonly SumatraController _sumatra;
    private readonly RawInputListener _rawInput;
    private readonly PresenterMatcher _matcher;
    private readonly KeyRouter _router;
    private readonly KeyboardHook _hook;
    private readonly HotkeyWindow _hotkey;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _arrowsItem;
    private readonly System.Windows.Forms.Timer _statusTimer;
    private readonly System.Windows.Forms.Timer _deviceRecheck;

    private SetupForm? _setup;
    private bool _paused;
    private bool? _presenterConnected; // null = ainda não verificado
    private TrayState? _shownState;

    public TrayContext()
    {
        _config = AppConfig.Load();
        _ppt = new PowerPointController();
        _sumatra = new SumatraController();
        _matcher = new PresenterMatcher(_config.Presenter);

        _rawInput = new RawInputListener();
        _router = new KeyRouter(_ppt, _sumatra, _matcher, () => _config.EscapeAction,
            () => _config.KeyboardArrowsControlSlides);
        _rawInput.KeyReceived += _router.OnRaw;
        _rawInput.DevicesChanged += OnDevicesChanged;

        _hook = new KeyboardHook(_router.OnHook);
        _hook.Install();

        _hotkey = new HotkeyWindow();
        _hotkey.Pressed += TogglePause;

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem($"{AppName} {Application.ProductVersion.Split('+')[0]}")
        {
            Enabled = false,
            Image = AppIcons.App.ToBitmap(),
        });
        menu.Items.Add(new ToolStripSeparator());
        _pauseItem = new ToolStripMenuItem("Pausar", null, (_, _) => TogglePause())
        {
            ShortcutKeyDisplayString = _hotkey.IsRegistered ? "Ctrl+Alt+P" : "",
        };
        menu.Items.Add(_pauseItem);
        _arrowsItem = new ToolStripMenuItem("Setas do teclado também passam slide")
        {
            Checked = _config.KeyboardArrowsControlSlides,
            ToolTipText = "Modo reserva: se o passador falhar, as setas de qualquer teclado\n" +
                          "controlam a apresentação, mesmo com outra janela em foco.",
        };
        _arrowsItem.Click += (_, _) => ToggleArrows();
        menu.Items.Add(_arrowsItem);
        menu.Items.Add("Reconfigurar passador", null, (_, _) => ShowSetup());
        menu.Items.Add("Abrir log", null, (_, _) => OpenLog());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => ExitThread());

        _icon = new NotifyIcon
        {
            Icon = AppIcons.Red,
            Text = AppName,
            ContextMenuStrip = menu,
            Visible = true,
        };

        // Ao conectar o USB chegam vários avisos seguidos; confere uma vez só, logo depois.
        _deviceRecheck = new System.Windows.Forms.Timer { Interval = 700 };
        _deviceRecheck.Tick += (_, _) =>
        {
            _deviceRecheck.Stop();
            RefreshPresenterConnection();
        };

        _statusTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _statusTimer.Tick += (_, _) => UpdateTray();
        _statusTimer.Start();

        ApplyState();
        RefreshPresenterConnection();
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

    // ------------------------------------------------------------- estado

    private void ApplyState()
    {
        _matcher.Presenter = _config.Presenter;
        // O modo reserva funciona mesmo sem passador configurado.
        _router.Enabled = !_paused && _setup is null
                          && (_config.Presenter is not null || _config.KeyboardArrowsControlSlides);
        _pauseItem.Text = _paused ? "Retomar" : "Pausar";
        UpdateTray();
    }

    private void TogglePause()
    {
        _paused = !_paused;
        Log.Info(_paused ? "Pausado." : "Retomado.");
        ApplyState();
        _icon.ShowBalloonTip(2000, AppName,
            _paused ? "Pausado: o passador volta a funcionar só na janela em foco." : "Retomado.",
            ToolTipIcon.Info);
    }

    private void ToggleArrows()
    {
        _config.KeyboardArrowsControlSlides = !_config.KeyboardArrowsControlSlides;
        _arrowsItem.Checked = _config.KeyboardArrowsControlSlides;
        _config.TrySave();
        Log.Info(_config.KeyboardArrowsControlSlides
            ? "Modo reserva LIGADO: setas do teclado passam slide."
            : "Modo reserva DESLIGADO.");
        _icon.ShowBalloonTip(3000, AppName, _config.KeyboardArrowsControlSlides
            ? "Setas do teclado agora passam slide, de qualquer janela."
            : "Setas do teclado voltaram ao normal.", ToolTipIcon.Info);
        ApplyState();
    }

    private void OnDevicesChanged(bool arrived)
    {
        // Handles do Raw Input mudam quando o USB é reconectado; recalcula.
        _matcher.Reset();
        _deviceRecheck.Stop();
        _deviceRecheck.Start();
    }

    private void RefreshPresenterConnection()
    {
        bool connected = _matcher.IsConnected();
        if (connected == _presenterConnected) return;
        _presenterConnected = connected;
        if (_config.Presenter is null) return;
        Log.Info(connected ? "Passador conectado." : "Passador NÃO encontrado (desconectado?).");
        if (!connected)
        {
            _icon.ShowBalloonTip(4000, AppName,
                "Passador não encontrado. Confira o receptor USB." +
                (_config.KeyboardArrowsControlSlides ? "" : "\nDica: ligue \"Setas do teclado também passam slide\"."),
                ToolTipIcon.Warning);
        }
        UpdateTray();
    }

    // -------------------------------------------------------------- ícone

    private void UpdateTray()
    {
        bool showRunning = _ppt.IsSlideShowActive || _sumatra.IsPresenting;
        TrayState state =
            _paused ? TrayState.Paused
            : _config.Presenter is null || _presenterConnected != true ? TrayState.NoPresenter
            : showRunning ? TrayState.Active
            : TrayState.Ready;

        if (state != _shownState)
        {
            _icon.Icon = state switch
            {
                TrayState.Active => AppIcons.Green,
                TrayState.Ready => AppIcons.Yellow,
                TrayState.Paused => AppIcons.Gray,
                _ => AppIcons.Red,
            };
            _shownState = state;
        }

        string detail = state switch
        {
            TrayState.Paused => "Pausado — Ctrl+Alt+P para retomar",
            TrayState.NoPresenter when _config.Presenter is null => "Passador não configurado",
            TrayState.NoPresenter => "Passador não encontrado",
            _ => DescribeShow() ?? "Pronto — nenhuma apresentação rodando",
        };
        if (_config.KeyboardArrowsControlSlides && state != TrayState.Paused) detail += "\nSetas do teclado ligadas";
        SetTooltip($"{AppName}\n{detail}");
    }

    private string? DescribeShow()
    {
        if (_ppt.IsSlideShowActive)
        {
            var info = _ppt.ShowInfo;
            return info is null ? "Apresentação rodando" : $"Slide {info.Slide} de {info.Total} — {info.FileName}";
        }
        if (_sumatra.IsPresenting) return $"PDF em tela cheia — {_sumatra.DocumentName}";
        return null;
    }

    private void SetTooltip(string text)
    {
        const int max = 127; // limite do Windows para a dica da bandeja
        if (text.Length > max) text = text[..(max - 1)] + "…";
        if (_icon.Text != text) _icon.Text = text;
    }

    // --------------------------------------------------------- configuração

    private void ShowSetup()
    {
        if (_setup is not null)
        {
            _setup.Activate();
            return;
        }
        _setup = new SetupForm(_rawInput, _config);
        ApplyState(); // durante a configuração nada é interceptado
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
                        AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                _icon.ShowBalloonTip(3000, AppName, $"Passador configurado: {form.Result.Name}", ToolTipIcon.Info);
            }
            ApplyState();
            _presenterConnected = null;
            RefreshPresenterConnection();
        };
        _setup.Show();
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
        _statusTimer.Dispose();
        _deviceRecheck.Dispose();
        _setup?.Close();
        _hook.Dispose();
        _router.Dispose();
        _hotkey.Dispose();
        _rawInput.Dispose();
        _ppt.Dispose();
        _sumatra.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        base.ExitThreadCore();
    }
}
