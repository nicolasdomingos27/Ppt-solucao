using System.Diagnostics;

namespace BackgroundPresenter;

/// <summary>
/// Detecta programas de fabricantes de passador que leem o aparelho por
/// conta própria e geram as teclas por software (ex.: Logi Options+ com o
/// Logitech Spotlight). Serve só para dar nome amigável e dicas na tela; a
/// decisão de aceitar as teclas está no KeyRouter.
/// </summary>
internal static class VendorSoftware
{
    private static readonly (string Prefix, string Name)[] Known =
    {
        ("logioptionsplus", "Logi Options+"),
        ("logioptions", "Logitech Options"),
        ("logipresentation", "Logitech Presentation"),
    };

    /// <summary>Nome do software do fabricante rodando agora, ou null.</summary>
    public static string? DetectRunning()
    {
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    var name = process.ProcessName.ToLowerInvariant();
                    foreach (var (prefix, friendly) in Known)
                        if (name.StartsWith(prefix, StringComparison.Ordinal)) return friendly;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Não foi possível listar processos: {ex.Message}");
        }
        return null;
    }
}
