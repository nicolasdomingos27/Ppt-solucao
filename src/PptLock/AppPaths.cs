namespace PptLock;

internal static class AppPaths
{
    /// <summary>
    /// Pasta real do .exe (no pendrive). Em single-file, ProcessPath aponta para o
    /// .exe e não para a pasta temporária de extração.
    /// </summary>
    public static string BaseDirectory { get; } =
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    /// <summary>
    /// O Explorer deixa abrir um .exe de dentro do .zip; ele roda de uma pasta
    /// temporária que some depois, levando o config.json e o log junto.
    /// </summary>
    public static bool LooksLikeInsideZip()
    {
        try
        {
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\');
            return BaseDirectory.StartsWith(temp, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
