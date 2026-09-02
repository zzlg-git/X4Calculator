using X4Calculator.Core.Models;

namespace X4Calculator.Core.Parsing;

/// <summary>发现 Documents/Egosoft/X4/&lt;数字用户 ID&gt;/save 下的 X4 存档。</summary>
public sealed class SavegameLocator
{
    private readonly string _documentsPath;
    private readonly Func<string, string?>? _locationNameResolver;

    public SavegameLocator(
        string? documentsPath = null,
        Func<string, string?>? locationNameResolver = null)
    {
        _documentsPath = documentsPath ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        _locationNameResolver = locationNameResolver;
    }

    public IReadOnlyList<SavegameFileInfo> FindDefaultSaves()
    {
        var x4Path = Path.Combine(_documentsPath, "Egosoft", "X4");
        if (!Directory.Exists(x4Path)) return Array.Empty<SavegameFileInfo>();

        try
        {
            return Directory.EnumerateDirectories(x4Path)
                .Where(path => Path.GetFileName(path).All(char.IsDigit))
                .Select(path => Path.Combine(path, "save"))
                .Where(Directory.Exists)
                .SelectMany(path => Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly))
                .Where(path => string.Equals(Path.GetExtension(path), ".gz", StringComparison.OrdinalIgnoreCase))
                .Select(path => new FileInfo(path))
                .Select(CreateSavegameFileInfo)
                .OrderByDescending(file => file.LastWriteTime)
                .ThenBy(file => file.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (IOException)
        {
            return Array.Empty<SavegameFileInfo>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<SavegameFileInfo>();
        }
    }

    private SavegameFileInfo CreateSavegameFileInfo(FileInfo file)
    {
        var metadata = SavegameMetadataReader.TryRead(file.FullName);
        var locationName = metadata?.PlayerLocationReference is { } locationReference
            ? _locationNameResolver?.Invoke(locationReference)
            : null;
        return new SavegameFileInfo(
            file.Name,
            file.FullName,
            file.LastWriteTime,
            metadata?.SaveName,
            locationName);
    }
}
