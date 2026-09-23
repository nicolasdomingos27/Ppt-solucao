namespace BackgroundPresenter;

/// <summary>
/// Tela de identificação do passador: "Aperte qualquer botão do passador".
/// Usa o mesmo Raw Input do app para saber de qual aparelho veio a tecla.
/// A tela ignora o teclado (só mouse), para um Enter/Espaço do passador
/// não clicar num botão sem querer.
/// </summary>
internal sealed class SetupForm : Form
{
    private readonly RawInputListener _rawInput;
    private readonly Label _status;
    private readonly Button _useButton;
    private readonly RadioButton _escIgnore;
    private readonly RadioButton _escEnd;
    private DeviceInfo? _captured;

    public PresenterConfig? Result { get; private set; }
    public EscapeAction EscapeChoice => _escEnd.Checked ? EscapeAction.EndShow : EscapeAction.Ignore;

    public SetupForm(RawInputListener rawInput, AppConfig current)
    {
        _rawInput = rawInput;

        Text = "Background Presenter — configurar passador";
        Icon = AppIcons.App;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);
        Font = new Font("Segoe UI", 10f);

        var layout = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Dock = DockStyle.Fill,
        };

        layout.Controls.Add(new Label
        {
            Text = "Aperte qualquer botão do passador",
            Font = new Font("Segoe UI", 16f, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8),
        });

        _status = new Label
        {
            Text = "Aguardando…",
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            Margin = new Padding(0, 0, 0, 16),
        };
        layout.Controls.Add(_status);

        var escBox = new GroupBox
        {
            Text = "Quando o passador enviar Esc",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8),
            Margin = new Padding(0, 0, 0, 16),
        };
        var escLayout = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            WrapContents = false,
            Dock = DockStyle.Fill,
        };
        _escIgnore = new RadioButton { Text = "Ignorar (recomendado em evento ao vivo)", AutoSize = true, TabStop = false };
        _escEnd = new RadioButton { Text = "Encerrar a apresentação", AutoSize = true, TabStop = false };
        (current.EscapeAction == EscapeAction.EndShow ? _escEnd : _escIgnore).Checked = true;
        escLayout.Controls.Add(_escIgnore);
        escLayout.Controls.Add(_escEnd);
        escBox.Controls.Add(escLayout);
        layout.Controls.Add(escBox);

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        _useButton = new Button { Text = "Usar este passador", AutoSize = true, Enabled = false, TabStop = false };
        _useButton.Click += (_, _) => Accept();
        var retry = new Button { Text = "Tentar de novo", AutoSize = true, TabStop = false };
        retry.Click += (_, _) => ResetCapture();
        var cancel = new Button { Text = "Cancelar", AutoSize = true, TabStop = false };
        cancel.Click += (_, _) => Close();
        buttons.Controls.AddRange(new Control[] { _useButton, retry, cancel });
        layout.Controls.Add(buttons);

        Controls.Add(layout);

        _rawInput.KeyReceived += OnRawKey;
        FormClosed += (_, _) => _rawInput.KeyReceived -= OnRawKey;
    }

    private void OnRawKey(RawKey key)
    {
        if (_captured is not null || key.IsUp) return;
        var info = DeviceInfo.Query(key.Device);
        if (info is null) return; // tecla sem aparelho (sintética)
        _captured = info;
        _status.Text = $"Detectado: {info.Describe()}\n\nTecla recebida: {(Keys)key.VirtualKey}\n\n" +
                       "Se foi o passador, clique em \"Usar este passador\". " +
                       "Se apertou o teclado sem querer, clique em \"Tentar de novo\".";
        _useButton.Enabled = true;
        Log.Info($"Configuração: tecla {(Keys)key.VirtualKey} de {info.Describe()} — {info.Path}");
    }

    private void ResetCapture()
    {
        _captured = null;
        _useButton.Enabled = false;
        _status.Text = "Aguardando…";
    }

    private void Accept()
    {
        if (_captured is null) return;
        Result = PresenterConfig.From(_captured);
        DialogResult = DialogResult.OK;
        Close();
    }

    // Ignora todo o teclado nesta tela (exceto Alt+F4).
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData) =>
        keyData == (Keys.Alt | Keys.F4) ? base.ProcessCmdKey(ref msg, keyData) : true;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
    }
}
