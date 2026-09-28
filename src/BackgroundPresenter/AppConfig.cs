using System.Text.Json;
using System.Text.Json.Serialization;

namespace BackgroundPresenter;

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
    /// <summary>
    /// "Aparelho" especial: teclas geradas pelo software do fabricante (ex.:
    /// Logi Options+ com o Spotlight). Elas chegam como teclas injetadas, sem
    /// aparelho físico associado.
    /// </summary>
    public const string SoftwareSourcePath = "software";

    [JsonIgnore]
    public bool IsSoftwareSource => DevicePath == SoftwareSourcePath;

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
        if (IsSoftwareSource) return false; // só casa com teclas injetadas, veja KeyRouter
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

    /// <summary>Passadores aceitos (pode haver mais de um: reserva, Spotlight com e sem Logi Options+).</summary>
    public List<PresenterConfig> Presenters { get; set; } = new();

    /// <summary>Formato antigo (um só passador): lido e convertido para <see cref="Presenters"/>.</summary>
    [JsonPropertyName("presenter")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PresenterConfig? LegacyPresenter
    {
        get => null;
        set
        {
            if (value is not null && !Presenters.Exists(p => p.DevicePath == value.DevicePath)) Presenters.Add(value);
        }
    }

    [JsonIgnore]
    public bool HasPresenter => Presenters.Count > 0;

    [JsonIgnore]
    public string PresenterNames => string.Join(" + ", Presenters.Select(p => p.Name));
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
