namespace X4Calculator.UI.ViewModels;

/// <summary>空间站运输使用的仓储类型槽位。</summary>
public enum TransportStorageType
{
    Container,
    Solid,
    Liquid,
    Condensate
}

/// <summary>空间站运输使用的舰船尺寸档位。</summary>
public enum TransportShipSize
{
    Large,
    Small
}

/// <summary>
/// 单个全局运输舰船配置。只保存稳定的游戏数据 ID；使用方从当前 GameDataDB 解析实体。
/// 驾驶员星级是配置的一部分，即使界面始终提供默认值也必须随槽位保存。
/// </summary>
public sealed record TransportShipConfiguration(
    string ShipId,
    string ThrusterId,
    string EngineId,
    int PilotingStars);

public sealed class TransportShipConfigurationChangedEventArgs : EventArgs
{
    public TransportShipConfigurationChangedEventArgs(
        TransportStorageType storageType,
        TransportShipSize shipSize,
        TransportShipConfiguration configuration)
    {
        StorageType = storageType;
        ShipSize = shipSize;
        Configuration = configuration;
    }

    public TransportStorageType StorageType { get; }
    public TransportShipSize ShipSize { get; }
    public TransportShipConfiguration Configuration { get; }
}

/// <summary>
/// 当前应用会话共享的八槽运输舰船配置仓库：四种仓储 × 大型/小型舰船。
/// </summary>
public sealed class TransportShipConfigurationStore
{
    private readonly Dictionary<(TransportStorageType Storage, TransportShipSize Size), TransportShipConfiguration>
        _configurations = new();

    public event EventHandler<TransportShipConfigurationChangedEventArgs>? ConfigurationChanged;

    public IReadOnlyDictionary<(TransportStorageType Storage, TransportShipSize Size), TransportShipConfiguration>
        Configurations => _configurations;

    public TransportShipConfiguration? Get(TransportStorageType storageType, TransportShipSize shipSize) =>
        _configurations.GetValueOrDefault((storageType, shipSize));

    public bool TryGet(
        TransportStorageType storageType,
        TransportShipSize shipSize,
        out TransportShipConfiguration configuration) =>
        _configurations.TryGetValue((storageType, shipSize), out configuration!);

    public void Set(
        TransportStorageType storageType,
        TransportShipSize shipSize,
        TransportShipConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _configurations[(storageType, shipSize)] = configuration;
        ConfigurationChanged?.Invoke(
            this,
            new TransportShipConfigurationChangedEventArgs(storageType, shipSize, configuration));
    }

    public static bool TryParseStorageTag(string? tag, out TransportStorageType storageType)
    {
        storageType = tag?.ToLowerInvariant() switch
        {
            "container" => TransportStorageType.Container,
            "solid" => TransportStorageType.Solid,
            "liquid" => TransportStorageType.Liquid,
            "condensate" => TransportStorageType.Condensate,
            _ => default
        };
        return tag is not null && tag.Equals(StorageTag(storageType), StringComparison.OrdinalIgnoreCase);
    }

    public static string StorageTag(TransportStorageType storageType) => storageType switch
    {
        TransportStorageType.Container => "container",
        TransportStorageType.Solid => "solid",
        TransportStorageType.Liquid => "liquid",
        TransportStorageType.Condensate => "condensate",
        _ => throw new ArgumentOutOfRangeException(nameof(storageType))
    };
}
