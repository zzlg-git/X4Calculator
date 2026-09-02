namespace X4Calculator.Core.Data;

/// <summary>定义应用可直接读取的扁平 GameData 目录契约。</summary>
public static class GameDataDirectory
{
    private static readonly string[] RequiredRelativeFiles =
    [
        "libraries/wares.xml",
        "libraries/modules.xml",
        "t/0001-l044.xml",
        "t/0001-l086.xml",
        "maps/xu_ep2_universe/galaxy.xml",
        "maps/xu_ep2_universe/clusters.xml",
        "maps/xu_ep2_universe/sectors.xml",
        "maps/xu_ep2_universe/zones.xml"
    ];

    public const string ManifestFileName = "x4calculator-manifest.json";

    public static bool IsUsable(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        Directory.Exists(path) &&
        File.Exists(Path.Combine(path, ManifestFileName)) &&
        HasRequiredFiles(path);

    public static void EnsureUsable(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        EnsureRequiredFiles(fullPath);
        if (!File.Exists(Path.Combine(fullPath, ManifestFileName)))
            throw new DirectoryNotFoundException(
                $"GameData 缺少完成清单 {ManifestFileName}：{fullPath}");
    }

    public static void EnsureRequiredFiles(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var missing = RequiredRelativeFiles
            .Where(relativePath => !File.Exists(Path.Combine(
                fullPath, relativePath.Replace('/', Path.DirectorySeparatorChar))))
            .ToList();
        if (missing.Count > 0)
            throw new DirectoryNotFoundException(
                $"GameData 数据不完整：{fullPath}；缺少 {string.Join("、", missing)}");
    }

    private static bool HasRequiredFiles(string path) => RequiredRelativeFiles.All(relativePath =>
        File.Exists(Path.Combine(path, relativePath.Replace('/', Path.DirectorySeparatorChar))));
}
