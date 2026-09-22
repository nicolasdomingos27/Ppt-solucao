namespace PptLock;

internal static class AppPaths
{
    /// <summary>
    /// Pasta real do .exe (no pendrive). Em single-file, ProcessPath aponta para o
    /// .exe e não para a pasta temporária de extração.
    /// </summary>
    public static string BaseDirectory { get; } =
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
}
