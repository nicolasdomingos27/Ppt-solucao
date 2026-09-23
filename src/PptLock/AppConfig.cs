using System.Text.Json;
using System.Text.Json.Serialization;

namespace PptLock;

internal enum EscapeAction
{
    /// <summary>Esc do passador é engolido e nada acontece (padrão, mais seguro ao vivo).</summary>
    Ignore,
    /// <summary>Esc do passador encerra a apresentação.</summary>
    EndShow,
}

/// <summary>Passador salvo. Casamos pelo caminho exato ou, se mudou de porta USB, por VID/PID/interface.</summary>
internal sealed class PresenterConfig
{
    public string Name { get; set; } = "";
    public string DevicePath { get; set; } = "";
    public string? VendorId { get; set; }
    public string? ProductId { get; set; }
    public string? Interface { get; set; }

    public static PresenterConfig From(DeviceInfo d) => new()
    {
        Name = d.Name,
        DevicePath = d.Path,
        VendorId = d.VendorId,
        ProductId = d.ProductId,
        Interface = d.Interface,
    };

    public bool Matches(DeviceInfo d)
    {
        if (string.Equals(DevicePath, d.Path, StringComparison.OrdinalIgnoreCase)) return true;
        return VendorId is not null
               && string.Equals(VendorId, d.VendorId, StringComparison.OrdinalIgnoreCase)
               && string.Equals(ProductId, d.ProductId, StringComparison.OrdinalIgnoreCase)
               && string.Equals(Interface, d.Interface, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>config.json na mesma pasta do .exe (fica no pendrive).</summary>
internal sealed class AppConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string FilePath { get; } = Path.Combine(AppPaths.BaseDirectory, "config.json");

    public PresenterConfig? Presenter { get; set; }
    public EscapeAction EscapeAction { get; set; } = EscapeAction.Ignore;

    /// <summary>
    /// Modo reserva: as setas de QUALQUER teclado também passam slide,
    /// independente da janela em foco (para quando o passador falha).
    /// </summary>
    public bool KeyboardArrowsControlSlides { get; set; }

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), JsonOptions) ?? new AppConfig();
        }
        catch (Exception ex)
        {
            Log.Error("config.json inválido; usando configuração vazia", ex);
        }
        return new AppConfig();
    }

    public bool TrySave()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
            Log.Info("config.json salvo.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Não foi possível salvar config.json", ex);
            return false;
        }
    }
}
