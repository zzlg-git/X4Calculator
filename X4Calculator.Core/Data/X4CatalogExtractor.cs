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

public enum X4DataFileSourceKind
{
    BaseCatalog,
    ExtensionCatalog,
    SubstitutionCatalog,
    LooseFile
}

public sealed record X4ContentDependency(string Id, bool Optional);

public sealed record X4ContentPackage(
    string Id,
    string DisplayName,
    string DirectoryPath,
    X4ContentKind Kind,
    IReadOnlyList<string> CatalogPaths,
    string ContentId = "",
    string Version = "",
    IReadOnlyList<X4ContentDependency>? Dependencies = null,
    bool HasLooseFiles = false)
{
    public string EffectiveContentId => string.IsNullOrWhiteSpace(ContentId) ? Id : ContentId;
    public IReadOnlyList<X4ContentDependency> EffectiveDependencies => Dependencies ?? [];
}

public sealed record X4CatalogEntry(
    string RelativePath,
    string PackageId,
    string PackageRelativePath,
    X4DataFileSourceKind SourceKind,
    string CatalogPath,
    string DataPath,
    long Offset,
    long Size,
    string ExpectedMd5);

public sealed record X4LooseFile(
    string RelativePath,
    string PackageId,
    string PackageRelativePath,
    string SourcePath,
    long Size);

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
    long TotalBytes,
    IReadOnlyList<X4ManifestPackage>? Packages = null,
    IReadOnlyList<X4ManifestFile>? Files = null);

public sealed record X4ManifestPackage(
    string FolderId,
    string ContentId,
    string DisplayName,
    string Version,
    X4ContentKind Kind,
    IReadOnlyList<X4ContentDependency> Dependencies);

public sealed record X4ManifestFile(
    string PackageId,
    string PackageRelativePath,
    string StoredRelativePath,
    X4DataFileSourceKind SourceKind,
    long Size,
    string? Md5);

/// <summary>扫描 X4 内容包并导出 CAT/DAT 或松散 XML；只保留计算器需要的数据类型。</summary>
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
            baseCatalogs,
            BaseGamePackageId,
            HasLooseFiles: File.Exists(Path.Combine(normalizedGameDirectory, "version.dat"))));

        var extensionsDirectory = Path.Combine(normalizedGameDirectory, "extensions");
        if (!Directory.Exists(extensionsDirectory)) return packages;

        foreach (var extensionDirectory in Directory.GetDirectories(extensionsDirectory)
                     .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
        {
            var catalogs = FindCatalogs(extensionDirectory);
            var metadata = ReadExtensionMetadata(extensionDirectory);
            var hasLooseFiles = HasLooseDataFiles(extensionDirectory, X4ContentKind.Mod);
            if (catalogs.Count == 0 && !hasLooseFiles) continue;

            var id = Path.GetFileName(extensionDirectory);
            var kind = id.StartsWith("ego_dlc_", StringComparison.OrdinalIgnoreCase)
                ? X4ContentKind.OfficialDlc
                : X4ContentKind.Mod;
            packages.Add(new X4ContentPackage(
                id,
                metadata.DisplayName ?? id,
                extensionDirectory,
                kind,
                catalogs,
                metadata.ContentId ?? id,
                metadata.Version ?? string.Empty,
                metadata.Dependencies,
                hasLooseFiles));
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
        var selectedPackages = discoveredPackages
            .Where(package => selectedIds.Contains(package.Id))
            .ToList();
        var unknownIds = selectedIds
            .Where(id => discoveredPackages.All(package =>
                !package.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (unknownIds.Count > 0)
            throw new InvalidOperationException($"选择中包含不存在的内容包：{string.Join("、", unknownIds)}");
        if (selectedPackages.Count == 0)
            throw new InvalidOperationException("没有选择任何可解包的游戏内容。");
        if (selectedPackages.All(package => package.Kind != X4ContentKind.BaseGame))
            throw new InvalidOperationException("解包计划必须包含原版游戏数据。");
        var packages = OrderPackages(selectedPackages);

        var resolvedEntries = new Dictionary<string, X4CatalogEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in packages)
        {
            var prefix = package.Kind == X4ContentKind.BaseGame
                ? string.Empty
                : $"extensions/{package.Id}/";

            foreach (var catalogPath in package.CatalogPaths)
            {
                var sourceKind = package.Kind == X4ContentKind.BaseGame
                    ? X4DataFileSourceKind.BaseCatalog
                    : Path.GetFileName(catalogPath).StartsWith("subst_", StringComparison.OrdinalIgnoreCase)
                        ? X4DataFileSourceKind.SubstitutionCatalog
                        : X4DataFileSourceKind.ExtensionCatalog;
                MergeCatalog(catalogPath, package.Id, prefix, sourceKind, resolvedEntries);
            }
        }

        var looseFiles = BuildLooseFiles(packages);
        foreach (var looseFile in looseFiles)
            resolvedEntries.Remove(looseFile.RelativePath);
        var entries = resolvedEntries.Values
            .Where(entry => entry.RelativePath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
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
            var packageOrder = plan.Packages
                .Select((package, index) => (package.Id, index))
                .ToDictionary(item => item.Id, item => item.index, StringComparer.OrdinalIgnoreCase);
            var manifestFiles = plan.Entries.Select(entry => new X4ManifestFile(
                    entry.PackageId,
                    entry.PackageRelativePath,
                    entry.RelativePath,
                    entry.SourceKind,
                    entry.Size,
                    entry.ExpectedMd5))
                .Concat(plan.LooseFiles.Select(file => new X4ManifestFile(
                    file.PackageId,
                    file.PackageRelativePath,
                    file.RelativePath,
                    X4DataFileSourceKind.LooseFile,
                    file.Size,
                    null)))
                .OrderBy(file => packageOrder[file.PackageId])
                .ThenBy(file => file.PackageRelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var manifest = new X4DataManifest(
                2,
                DateTimeOffset.UtcNow,
                plan.Packages.Select(package => package.Id).ToList(),
                plan.Entries.Count + plan.LooseFiles.Count,
                plan.TotalBytes,
                plan.Packages.Select(package => new X4ManifestPackage(
                    package.Id,
                    package.EffectiveContentId,
                    package.DisplayName,
                    package.Version,
                    package.Kind,
                    package.EffectiveDependencies)).ToList(),
                manifestFiles);
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

    private sealed record ExtensionMetadata(
        string? ContentId,
        string? DisplayName,
        string? Version,
        IReadOnlyList<X4ContentDependency> Dependencies);

    private static ExtensionMetadata ReadExtensionMetadata(string extensionDirectory)
    {
        var contentPath = Path.Combine(extensionDirectory, "content.xml");
        if (!File.Exists(contentPath)) return new(null, null, null, []);

        try
        {
            var root = XDocument.Load(contentPath).Root;
            if (root == null) return new(null, null, null, []);
            var dependencies = root.Elements("dependency")
                .Select(element => new
                {
                    Id = element.Attribute("id")?.Value.Trim(),
                    Optional = IsTrue(element.Attribute("optional")?.Value)
                })
                .Where(item => !string.IsNullOrWhiteSpace(item.Id))
                .Select(item => new X4ContentDependency(item.Id!, item.Optional))
                .ToList();
            return new(
                NullIfWhiteSpace(root.Attribute("id")?.Value),
                NullIfWhiteSpace(root.Attribute("name")?.Value),
                NullIfWhiteSpace(root.Attribute("version")?.Value),
                dependencies);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return new(null, null, null, []);
        }
    }

    private static IReadOnlyList<X4ContentPackage> OrderPackages(
        IReadOnlyList<X4ContentPackage> selectedPackages)
    {
        var basePackage = selectedPackages.Single(package => package.Kind == X4ContentKind.BaseGame);
        var extensions = selectedPackages
            .Where(package => package.Kind != X4ContentKind.BaseGame)
            .ToList();
        var byIdentity = new Dictionary<string, X4ContentPackage>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in extensions)
        {
            foreach (var identity in new[] { package.Id, package.EffectiveContentId }.Distinct(
                         StringComparer.OrdinalIgnoreCase))
            {
                if (byIdentity.TryGetValue(identity, out var duplicate) && duplicate != package)
                    throw new InvalidOperationException(
                        $"内容包标识冲突：{identity} 同时属于 {duplicate.Id} 和 {package.Id}。");
                byIdentity[identity] = package;
            }
        }

        foreach (var package in extensions)
        {
            var missing = package.EffectiveDependencies
                .Where(dependency => !dependency.Optional && !byIdentity.ContainsKey(dependency.Id))
                .Select(dependency => dependency.Id)
                .ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException(
                    $"内容包 {package.DisplayName} 缺少已选择的必需依赖：{string.Join("、", missing)}");
        }

        var discoveryOrder = extensions
            .Select((package, index) => (package, index))
            .ToDictionary(item => item.package.Id, item => item.index, StringComparer.OrdinalIgnoreCase);
        var indegree = extensions.ToDictionary(
            package => package.Id,
            _ => 0,
            StringComparer.OrdinalIgnoreCase);
        var dependents = extensions.ToDictionary(
            package => package.Id,
            _ => new List<X4ContentPackage>(),
            StringComparer.OrdinalIgnoreCase);

        foreach (var package in extensions)
        {
            foreach (var dependency in package.EffectiveDependencies)
            {
                if (!byIdentity.TryGetValue(dependency.Id, out var dependencyPackage) ||
                    dependencyPackage.Id.Equals(package.Id, StringComparison.OrdinalIgnoreCase))
                    continue;
                indegree[package.Id]++;
                dependents[dependencyPackage.Id].Add(package);
            }
        }

        var ready = new PriorityQueue<X4ContentPackage, int>();
        foreach (var package in extensions.Where(package => indegree[package.Id] == 0))
            ready.Enqueue(package, discoveryOrder[package.Id]);

        var ordered = new List<X4ContentPackage> { basePackage };
        while (ready.TryDequeue(out var package, out _))
        {
            ordered.Add(package);
            foreach (var dependent in dependents[package.Id])
            {
                indegree[dependent.Id]--;
                if (indegree[dependent.Id] == 0)
                    ready.Enqueue(dependent, discoveryOrder[dependent.Id]);
            }
        }

        if (ordered.Count != selectedPackages.Count)
        {
            var cycle = extensions.Where(package => indegree[package.Id] > 0)
                .Select(package => package.Id);
            throw new InvalidOperationException($"内容包依赖形成循环：{string.Join("、", cycle)}");
        }
        return ordered;
    }

    private static bool HasLooseDataFiles(string directory, X4ContentKind kind) =>
        EnumerateLooseSourceFiles(directory, kind).Any();

    private static IReadOnlyList<X4LooseFile> BuildLooseFiles(IEnumerable<X4ContentPackage> packages)
    {
        var looseFiles = new List<X4LooseFile>();
        foreach (var package in packages)
        {
            foreach (var sourcePath in EnumerateLooseSourceFiles(package.DirectoryPath, package.Kind))
            {
                var packageRelativePath = NormalizeCatalogPath(
                    Path.GetRelativePath(package.DirectoryPath, sourcePath));
                var storedRelativePath = package.Kind == X4ContentKind.BaseGame
                    ? packageRelativePath
                    : NormalizeCatalogPath($"extensions/{package.Id}/{packageRelativePath}");
                looseFiles.Add(new X4LooseFile(
                    storedRelativePath,
                    package.Id,
                    packageRelativePath,
                    sourcePath,
                    new FileInfo(sourcePath).Length));
            }
        }
        return looseFiles
            .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> EnumerateLooseSourceFiles(string directory, X4ContentKind kind)
    {
        if (kind == X4ContentKind.BaseGame)
        {
            var versionPath = Path.Combine(directory, "version.dat");
            if (File.Exists(versionPath)) yield return versionPath;
            yield break;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        foreach (var path in Directory.EnumerateFiles(directory, "*.xml", options))
            yield return Path.GetFullPath(path);
    }

    private static void MergeCatalog(
        string catalogPath,
        string packageId,
        string relativePrefix,
        X4DataFileSourceKind sourceKind,
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
            var normalizedPackageRelativePath = NormalizeCatalogPath(catalogRelativePath);
            var normalizedRelativePath = NormalizeCatalogPath(relativePrefix + normalizedPackageRelativePath);
            if (size == 0)
            {
                resolvedEntries.Remove(normalizedRelativePath);
                continue;
            }

            resolvedEntries[normalizedRelativePath] = new X4CatalogEntry(
                normalizedRelativePath,
                packageId,
                normalizedPackageRelativePath,
                sourceKind,
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

    private static bool IsTrue(string? value) =>
        value is not null &&
        (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1");

    private static string? NullIfWhiteSpace(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static string NormalizeCatalogPath(string path) => X4VirtualPath.Normalize(path, "CAT");

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
