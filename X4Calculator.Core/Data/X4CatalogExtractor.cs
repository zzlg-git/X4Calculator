using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace X4Calculator.Core.Data;

public enum X4ContentKind
{
    BaseGame,
    OfficialDlc,
    Mod
}

public sealed record X4ContentPackage(
    string Id,
    string DisplayName,
    string DirectoryPath,
    X4ContentKind Kind,
    IReadOnlyList<string> CatalogPaths);

public sealed record X4CatalogEntry(
    string RelativePath,
    string CatalogPath,
    string DataPath,
    long Offset,
    long Size,
    string ExpectedMd5);

public sealed record X4LooseFile(string RelativePath, string SourcePath, long Size);

public sealed record X4ExtractionPlan(
    string GameDirectory,
    IReadOnlyList<X4ContentPackage> Packages,
    IReadOnlyList<X4CatalogEntry> Entries,
    IReadOnlyList<X4LooseFile> LooseFiles,
    long TotalBytes);

public sealed record X4ExtractionProgress(
    int CompletedFiles,
    int TotalFiles,
    long CompletedBytes,
    long TotalBytes,
    string CurrentFile);

public sealed record X4DataManifest(
    int FormatVersion,
    DateTimeOffset CompletedAtUtc,
    IReadOnlyList<string> PackageIds,
    int FileCount,
    long TotalBytes);

/// <summary>扫描并解包 X4 的 CAT/DAT；只产生 X4Calculator 需要的 XML 数据。</summary>
public sealed class X4CatalogExtractor
{
    public const string BaseGamePackageId = "base-game";
    private const int BufferSize = 128 * 1024;
    private static readonly Regex CatalogLineRegex = new(
        @"^(?<path>.+?)\s+(?<size>\d+)\s+(?<timestamp>\d+)\s+(?<md5>[0-9a-fA-F]{32})\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public IReadOnlyList<X4ContentPackage> DiscoverContent(string gameDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        var normalizedGameDirectory = Path.GetFullPath(gameDirectory);
        if (!Directory.Exists(normalizedGameDirectory))
            throw new DirectoryNotFoundException($"X4 游戏目录不存在：{normalizedGameDirectory}");

        var packages = new List<X4ContentPackage>();
        var baseCatalogs = FindCatalogs(normalizedGameDirectory);
        if (baseCatalogs.Count == 0)
            throw new InvalidDataException($"目录中未找到 X4 基础游戏 CAT：{normalizedGameDirectory}");

        packages.Add(new X4ContentPackage(
            BaseGamePackageId,
            "X4: Foundations 原版游戏",
            normalizedGameDirectory,
            X4ContentKind.BaseGame,
            baseCatalogs));

        var extensionsDirectory = Path.Combine(normalizedGameDirectory, "extensions");
        if (!Directory.Exists(extensionsDirectory)) return packages;

        foreach (var extensionDirectory in Directory.GetDirectories(extensionsDirectory)
                     .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
        {
            var catalogs = FindCatalogs(extensionDirectory);
            if (catalogs.Count == 0) continue;

            var id = Path.GetFileName(extensionDirectory);
            var kind = id.StartsWith("ego_dlc_", StringComparison.OrdinalIgnoreCase)
                ? X4ContentKind.OfficialDlc
                : X4ContentKind.Mod;
            packages.Add(new X4ContentPackage(
                id,
                ReadExtensionDisplayName(extensionDirectory, id),
                extensionDirectory,
                kind,
                catalogs));
        }

        return packages;
    }

    public X4ExtractionPlan CreatePlan(
        string gameDirectory,
        IEnumerable<string> selectedPackageIds)
    {
        ArgumentNullException.ThrowIfNull(selectedPackageIds);
        var selectedIds = selectedPackageIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var discoveredPackages = DiscoverContent(gameDirectory);
        var packages = discoveredPackages
            .Where(package => selectedIds.Contains(package.Id))
            .ToList();
        var unknownIds = selectedIds
            .Where(id => discoveredPackages.All(package =>
                !package.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (unknownIds.Count > 0)
            throw new InvalidOperationException($"选择中包含不存在的内容包：{string.Join("、", unknownIds)}");
        if (packages.Count == 0)
            throw new InvalidOperationException("没有选择任何可解包的游戏内容。");
        if (packages.All(package => package.Kind != X4ContentKind.BaseGame))
            throw new InvalidOperationException("解包计划必须包含原版游戏数据。");

        var resolvedEntries = new Dictionary<string, X4CatalogEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in packages)
        {
            var prefix = package.Kind == X4ContentKind.BaseGame
                ? string.Empty
                : $"extensions/{package.Id}/";

            foreach (var catalogPath in package.CatalogPaths)
                MergeCatalog(catalogPath, prefix, resolvedEntries);
        }

        var entries = resolvedEntries.Values
            .Where(entry => entry.RelativePath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var looseFiles = BuildLooseFiles(packages);
        return new X4ExtractionPlan(
            Path.GetFullPath(gameDirectory),
            packages,
            entries,
            looseFiles,
            checked(entries.Sum(entry => entry.Size) + looseFiles.Sum(file => file.Size)));
    }

    public async Task ExtractAsync(
        X4ExtractionPlan plan,
        string destinationDirectory,
        IProgress<X4ExtractionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        var destinationRoot = Path.GetFullPath(destinationDirectory);
        var parentDirectory = Directory.GetParent(destinationRoot)?.FullName
            ?? throw new InvalidOperationException($"无法确定 GameData 父目录：{destinationRoot}");
        Directory.CreateDirectory(parentDirectory);
        var destinationName = Path.GetFileName(destinationRoot);
        var operationId = Guid.NewGuid().ToString("N");
        var stagingRoot = Path.Combine(parentDirectory, $".{destinationName}.staging-{operationId}");
        var backupRoot = Path.Combine(parentDirectory, $".{destinationName}.backup-{operationId}");
        Directory.CreateDirectory(stagingRoot);

        var oldMoved = false;
        var committed = false;
        try
        {
            await ExtractToDirectoryAsync(plan, stagingRoot, progress, cancellationToken);
            GameDataDirectory.EnsureRequiredFiles(stagingRoot);
            var manifest = new X4DataManifest(
                1,
                DateTimeOffset.UtcNow,
                plan.Packages.Select(package => package.Id).ToList(),
                plan.Entries.Count + plan.LooseFiles.Count,
                plan.TotalBytes);
            await File.WriteAllTextAsync(
                Path.Combine(stagingRoot, GameDataDirectory.ManifestFileName),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken);

            if (Directory.Exists(destinationRoot))
            {
                Directory.Move(destinationRoot, backupRoot);
                oldMoved = true;
            }

            try
            {
                Directory.Move(stagingRoot, destinationRoot);
                committed = true;
            }
            catch
            {
                if (oldMoved && !Directory.Exists(destinationRoot))
                    Directory.Move(backupRoot, destinationRoot);
                throw;
            }
        }
        finally
        {
            if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, recursive: true);
            if (committed && oldMoved && Directory.Exists(backupRoot))
            {
                try
                {
                    Directory.Delete(backupRoot, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 新数据已原子提交；保留无法清理的受控备份，不能回滚成功结果。
                }
            }
        }
    }

    private static async Task ExtractToDirectoryAsync(
        X4ExtractionPlan plan,
        string destinationRoot,
        IProgress<X4ExtractionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var completedFiles = 0;
        long completedBytes = 0;
        var totalFiles = plan.Entries.Count + plan.LooseFiles.Count;

        foreach (var entry in plan.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outputPath = ResolveSafeOutputPath(destinationRoot, entry.RelativePath);
            var outputParentDirectory = Path.GetDirectoryName(outputPath)
                ?? throw new InvalidDataException($"无法解析输出目录：{entry.RelativePath}");
            Directory.CreateDirectory(outputParentDirectory);

            var temporaryPath = outputPath + $".tmp-{Guid.NewGuid():N}";
            try
            {
                await ExtractEntryAsync(entry, temporaryPath, cancellationToken);
                File.Move(temporaryPath, outputPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }

            completedFiles++;
            completedBytes += entry.Size;
            progress?.Report(new X4ExtractionProgress(
                completedFiles,
                totalFiles,
                completedBytes,
                plan.TotalBytes,
                entry.RelativePath));
        }

        foreach (var looseFile in plan.LooseFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outputPath = ResolveSafeOutputPath(destinationRoot, looseFile.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await using (var source = new FileStream(
                             looseFile.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                             BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(
                             outputPath, FileMode.Create, FileAccess.Write, FileShare.None,
                             BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await source.CopyToAsync(destination, cancellationToken);

            completedFiles++;
            completedBytes += looseFile.Size;
            progress?.Report(new X4ExtractionProgress(
                completedFiles,
                totalFiles,
                completedBytes,
                plan.TotalBytes,
                looseFile.RelativePath));
        }
    }

    private static IReadOnlyList<string> FindCatalogs(string directory)
    {
        var catalogs = Directory.GetFiles(directory, "*.cat", SearchOption.TopDirectoryOnly)
            .Where(path => !path.EndsWith("_sig.cat", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToList();
        var missingData = catalogs.FirstOrDefault(path => !File.Exists(Path.ChangeExtension(path, ".dat")));
        if (missingData != null)
            throw new InvalidDataException($"CAT 缺少配对 DAT：{missingData}");
        return catalogs;
    }

    private static string ReadExtensionDisplayName(string extensionDirectory, string fallback)
    {
        var contentPath = Path.Combine(extensionDirectory, "content.xml");
        if (!File.Exists(contentPath)) return fallback;

        try
        {
            var name = XDocument.Load(contentPath).Root?.Attribute("name")?.Value?.Trim();
            return string.IsNullOrWhiteSpace(name) ? fallback : name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return fallback;
        }
    }

    private static IReadOnlyList<X4LooseFile> BuildLooseFiles(IEnumerable<X4ContentPackage> packages)
    {
        var looseFiles = new List<X4LooseFile>();
        foreach (var package in packages)
        {
            var fileName = package.Kind == X4ContentKind.BaseGame ? "version.dat" : "content.xml";
            var sourcePath = Path.Combine(package.DirectoryPath, fileName);
            if (!File.Exists(sourcePath)) continue;
            var relativePath = package.Kind == X4ContentKind.BaseGame
                ? fileName
                : $"extensions/{package.Id}/{fileName}";
            looseFiles.Add(new X4LooseFile(
                NormalizeCatalogPath(relativePath),
                sourcePath,
                new FileInfo(sourcePath).Length));
        }
        return looseFiles;
    }

    private static void MergeCatalog(
        string catalogPath,
        string relativePrefix,
        IDictionary<string, X4CatalogEntry> resolvedEntries)
    {
        var dataPath = Path.ChangeExtension(catalogPath, ".dat");
        if (!File.Exists(dataPath))
            throw new FileNotFoundException($"CAT 缺少对应 DAT：{catalogPath}", dataPath);

        long offset = 0;
        foreach (var line in File.ReadLines(catalogPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var match = CatalogLineRegex.Match(line);
            if (!match.Success ||
                !long.TryParse(match.Groups["size"].Value, out var size) || size < 0 ||
                !long.TryParse(match.Groups["timestamp"].Value, out _))
                throw new InvalidDataException($"CAT 条目格式无效：{catalogPath}：{line}");

            var catalogRelativePath = match.Groups["path"].Value;
            var normalizedRelativePath = NormalizeCatalogPath(relativePrefix + catalogRelativePath);
            if (size == 0)
            {
                resolvedEntries.Remove(normalizedRelativePath);
                continue;
            }

            resolvedEntries[normalizedRelativePath] = new X4CatalogEntry(
                normalizedRelativePath,
                catalogPath,
                dataPath,
                offset,
                size,
                match.Groups["md5"].Value);
            offset = checked(offset + size);
        }

        var dataLength = new FileInfo(dataPath).Length;
        if (offset > dataLength)
            throw new InvalidDataException($"CAT 声明的数据长度超过 DAT：{catalogPath}");
    }

    private static string NormalizeCatalogPath(string path)
    {
        if (Path.IsPathRooted(path) || path.StartsWith("\\\\", StringComparison.Ordinal))
            throw new InvalidDataException($"CAT 包含不安全路径：{path}");
        var normalized = path.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized[2..];
        normalized = normalized.TrimStart('/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(IsUnsafeWindowsPathSegment))
            throw new InvalidDataException($"CAT 包含不安全路径：{path}");
        return string.Join('/', segments).ToLowerInvariant();
    }

    private static bool IsUnsafeWindowsPathSegment(string segment)
    {
        if (segment is "." or ".." || segment.Contains(':') ||
            segment.EndsWith('.') || segment.EndsWith(' ') ||
            segment.IndexOfAny(['<', '>', '"', '|', '?', '*', '\0']) >= 0)
            return true;

        var deviceName = segment.Split('.')[0];
        return deviceName.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
               deviceName.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
               deviceName.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
               deviceName.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
               Enumerable.Range(1, 9).Any(number =>
                   deviceName.Equals($"COM{number}", StringComparison.OrdinalIgnoreCase) ||
                   deviceName.Equals($"LPT{number}", StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveSafeOutputPath(string destinationRoot, string relativePath)
    {
        var relativeSystemPath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var outputPath = Path.GetFullPath(Path.Combine(destinationRoot, relativeSystemPath));
        var rootPrefix = destinationRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!outputPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"CAT 输出路径越界：{relativePath}");
        return outputPath;
    }

    private static async Task ExtractEntryAsync(
        X4CatalogEntry entry,
        string outputPath,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            entry.DataPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        source.Seek(entry.Offset, SeekOrigin.Begin);
        await using var destination = new FileStream(
            outputPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            var remaining = entry.Size;
            while (remaining > 0)
            {
                var requested = (int)Math.Min(buffer.Length, remaining);
                var read = await source.ReadAsync(buffer.AsMemory(0, requested), cancellationToken);
                if (read == 0)
                    throw new EndOfStreamException($"DAT 数据不足：{entry.RelativePath}");
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                hash.AppendData(buffer, 0, read);
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (entry.ExpectedMd5.Length == 32)
        {
            var actualMd5 = Convert.ToHexString(hash.GetHashAndReset());
            if (!actualMd5.Equals(entry.ExpectedMd5, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"MD5 校验失败：{entry.RelativePath}，CAT={entry.ExpectedMd5}，实际={actualMd5}");
        }
    }
}
