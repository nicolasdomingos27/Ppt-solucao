namespace BackgroundPresenter;

/// <summary>Ícones embutidos no .exe (gerados por tools/make_icons.py).</summary>
internal static class AppIcons
{
    public static Icon App { get; } = Load("app.ico", new Size(32, 32));
    public static Icon Green { get; } = LoadTray("tray-green.ico");
    public static Icon Yellow { get; } = LoadTray("tray-yellow.ico");
    public static Icon Gray { get; } = LoadTray("tray-gray.ico");
    public static Icon Red { get; } = LoadTray("tray-red.ico");

    private static Icon LoadTray(string name) => Load(name, SystemInformation.SmallIconSize);

    private static Icon Load(string name, Size size)
    {
        try
        {
            using var stream = typeof(AppIcons).Assembly.GetManifestResourceStream(name);
            if (stream is not null) return new Icon(stream, size);
        }
        catch (Exception ex)
        {
            Log.Error($"Ícone {name} não carregado", ex);
        }
        return SystemIcons.Application;
    }
}
