namespace X4Calculator.Core.Models;

/// <summary>默认存档目录中可供用户选择的 gzip 存档。</summary>
public sealed record SavegameFileInfo(
    string FileName,
    string FullPath,
    DateTime LastWriteTime,
    string? SaveName = null,
    string? LocationName = null)
{
    /// <summary>按游戏存档页规则生成的最终显示名。</summary>
    public string GameDisplayName
    {
        get
        {
            if (!IsGeneratedSlotName(SaveName)) return SaveName!;
            if (string.IsNullOrWhiteSpace(LocationName)) return "—";
            if (FileName.StartsWith("quicksave", StringComparison.OrdinalIgnoreCase))
                return $"{LocationName} (快速存档)";
            if (FileName.StartsWith("autosave_", StringComparison.OrdinalIgnoreCase))
                return $"{LocationName} (自动保存)";
            return LocationName;
        }
    }

    private static bool IsGeneratedSlotName(string? name) =>
        string.IsNullOrWhiteSpace(name) ||
        (name.Length == 4 && name[0] == '#' && name.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0);
}
