using System.Collections.Concurrent;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace X4Calculator.Core.Data;

public sealed record X4EffectiveXmlResult(
    string VirtualPath,
    XDocument? Document,
    IReadOnlyList<X4XmlPatchDiagnostic> Diagnostics)
{
    public bool HasErrors => Diagnostics.Any(diagnostic =>
        diagnostic.Severity == X4XmlPatchDiagnosticSeverity.Error);
}

/// <summary>按导出清单中的 X4 内容包顺序物化指定虚拟路径的有效 XML。</summary>
public sealed class X4EffectiveDataProvider
{
    private sealed record CandidateSource(X4ManifestPackage Package, X4ManifestFile File);

    private readonly string _dataRoot;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<CandidateSource>> _candidatesByPath;
    private readonly IReadOnlyList<string> _candidatePathsInLoadOrder;
    private readonly ConcurrentDictionary<string, X4EffectiveXmlResult> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly X4XmlPatchEngine _patchEngine;

    public X4EffectiveDataProvider(string dataRoot, X4XmlPatchEngine? patchEngine = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _dataRoot = Path.GetFullPath(dataRoot);
        var manifest = ReadAndValidateManifest(_dataRoot);

        var packages = manifest.Packages!;
        var files = manifest.Files!;
        var candidates = new Dictionary<string, List<CandidateSource>>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in packages)
        {
            foreach (var file in files
                         .Where(file => file.PackageId.Equals(package.FolderId, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(file => file.PackageRelativePath, StringComparer.OrdinalIgnoreCase))
            {
                _ = X4VirtualPath.Normalize(file.StoredRelativePath, "GameData 清单");
                var packageRelativePath = X4VirtualPath.Normalize(file.PackageRelativePath, "GameData 清单");
                if (!packageRelativePath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;
                AddCandidate(candidates, packageRelativePath, new CandidateSource(package, file));
                if (package.Kind == X4ContentKind.BaseGame ||
                    file.SourceKind == X4DataFileSourceKind.SubstitutionCatalog)
                    continue;
                var mountedPath = GetExtensionMountPath(package.FolderId, packageRelativePath);
                if (!mountedPath.Equals(packageRelativePath, StringComparison.OrdinalIgnoreCase))
                    AddCandidate(candidates, mountedPath, new CandidateSource(package, file));
            }
        }
        _candidatesByPath = candidates.ToDictionary(
            item => item.Key,
            item => (IReadOnlyList<CandidateSource>)item.Value,
            StringComparer.OrdinalIgnoreCase);
        _candidatePathsInLoadOrder = candidates.Keys.ToList();
        _patchEngine = patchEngine ?? new X4XmlPatchEngine();
    }

    internal static X4DataManifest ReadAndValidateManifest(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var fullDataRoot = Path.GetFullPath(dataRoot);
        var manifestPath = Path.Combine(fullDataRoot, GameDataDirectory.ManifestFileName);
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException($"GameData 缺少完成清单：{manifestPath}", manifestPath);

        X4DataManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<X4DataManifest>(File.ReadAllText(manifestPath))
                ?? throw new InvalidDataException($"GameData 清单为空：{manifestPath}");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"GameData 清单格式无效：{manifestPath}", ex);
        }

        if (manifest.FormatVersion != GameDataDirectory.SupportedManifestVersion || manifest.PackageIds == null ||
            manifest.Packages == null || manifest.Files == null)
            throw new InvalidDataException(
                "GameData 清单不包含有效 XML 合并所需的内容包和文件来源；请重新导出游戏数据。");

        var packages = manifest.Packages;
        if (manifest.PackageIds.Any(string.IsNullOrWhiteSpace) || packages.Any(package => package is null))
            throw new InvalidDataException("GameData 清单包含空的内容包记录或 PackageIds 项。");
        if (packages.Any(package =>
                string.IsNullOrWhiteSpace(package.FolderId) ||
                string.IsNullOrWhiteSpace(package.ContentId) ||
                package.Dependencies == null))
            throw new InvalidDataException("GameData 清单包含空的目录 ID、内容 ID 或依赖列表。");
        foreach (var package in packages)
        {
            var normalizedFolderId = X4VirtualPath.Normalize(package.FolderId, "GameData 清单内容包 ID");
            if (normalizedFolderId.Contains('/'))
                throw new InvalidDataException($"GameData 清单内容包 ID 不是单一目录名：{package.FolderId}");
        }
        var duplicatePackage = packages.GroupBy(package => package.FolderId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicatePackage != null)
            throw new InvalidDataException($"GameData 清单包含重复内容包：{duplicatePackage.Key}");

        var packageIds = packages.Select(package => package.FolderId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (manifest.PackageIds.Count != packages.Count ||
            !manifest.PackageIds.Zip(packages, (id, package) =>
                    id.Equals(package.FolderId, StringComparison.OrdinalIgnoreCase))
                .All(equal => equal))
            throw new InvalidDataException("GameData 清单的 PackageIds 与 Packages 顺序不一致。");
        if (manifest.FileCount != manifest.Files.Count)
            throw new InvalidDataException(
                $"GameData 清单的 FileCount={manifest.FileCount}，实际文件记录={manifest.Files.Count}。");
        if (packages.Count == 0 || packages[0].Kind != X4ContentKind.BaseGame)
            throw new InvalidDataException("GameData 清单必须以基础游戏内容包开始。");
        if (packages.Count(package => package.Kind == X4ContentKind.BaseGame) != 1)
            throw new InvalidDataException("GameData 清单必须且只能包含一个基础游戏内容包。");

        if (manifest.Files.Any(file => file is null) || manifest.Files.Any(file =>
                string.IsNullOrWhiteSpace(file.PackageId) ||
                string.IsNullOrWhiteSpace(file.PackageRelativePath) ||
                string.IsNullOrWhiteSpace(file.StoredRelativePath)))
            throw new InvalidDataException("GameData 清单包含空的文件记录、包 ID 或相对路径。");

        var orphanFile = manifest.Files.FirstOrDefault(file => !packageIds.Contains(file.PackageId));
        if (orphanFile != null)
            throw new InvalidDataException(
                $"GameData 清单文件引用了不存在的内容包：{orphanFile.PackageId}/{orphanFile.PackageRelativePath}");

        var duplicateStoredPath = manifest.Files
            .GroupBy(file => X4VirtualPath.Normalize(file.StoredRelativePath, "GameData 清单"),
                StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateStoredPath != null)
            throw new InvalidDataException($"GameData 清单包含重复存储路径：{duplicateStoredPath.Key}");
        var duplicateLogicalPath = manifest.Files
            .GroupBy(file => (
                file.PackageId.ToLowerInvariant(),
                X4VirtualPath.Normalize(file.PackageRelativePath, "GameData 清单")))
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateLogicalPath != null)
            throw new InvalidDataException(
                $"GameData 清单包含同一内容包的重复逻辑路径：{duplicateLogicalPath.Key.Item1}/{duplicateLogicalPath.Key.Item2}");

        return manifest;
    }

    public X4EffectiveXmlResult LoadXml(string virtualPath)
    {
        var normalizedVirtualPath = X4VirtualPath.Normalize(virtualPath, "X4 虚拟路径");
        var cached = _cache.GetOrAdd(normalizedVirtualPath, LoadXmlCore);
        return new X4EffectiveXmlResult(
            cached.VirtualPath,
            cached.Document == null ? null : new XDocument(cached.Document),
            cached.Diagnostics);
    }

    public IReadOnlyList<string> EnumerateCandidateXmlPaths(string? prefix = null)
    {
        var normalizedPrefix = string.IsNullOrWhiteSpace(prefix)
            ? null
            : X4VirtualPath.Normalize(prefix, "X4 虚拟路径").TrimEnd('/') + "/";
        return _candidatesByPath.Keys
            .Where(path => normalizedPrefix == null ||
                           path.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<string> EnumerateCandidateXmlPathsInLoadOrder(string? prefix = null)
    {
        var normalizedPrefix = string.IsNullOrWhiteSpace(prefix)
            ? null
            : X4VirtualPath.Normalize(prefix, "X4 虚拟路径").TrimEnd('/') + "/";
        return _candidatePathsInLoadOrder
            .Where(path => normalizedPrefix == null ||
                           path.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public bool ContainsCandidateXmlPath(string virtualPath) =>
        _candidatesByPath.ContainsKey(X4VirtualPath.Normalize(virtualPath, "X4 虚拟路径"));

    private X4EffectiveXmlResult LoadXmlCore(string normalizedVirtualPath)
    {
        var diagnostics = new List<X4XmlPatchDiagnostic>();
        XDocument? effectiveDocument = null;

        if (_candidatesByPath.TryGetValue(normalizedVirtualPath, out var candidates))
        {
            foreach (var candidate in candidates)
            {
                var package = candidate.Package;
                var file = candidate.File;
                var packageRelativePath = X4VirtualPath.Normalize(
                    file.PackageRelativePath, "GameData 清单");

                XDocument document;
                try
                {
                    document = XDocument.Load(ResolveStoredPath(file.StoredRelativePath));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
                {
                    diagnostics.Add(new X4XmlPatchDiagnostic(
                        X4XmlPatchDiagnosticSeverity.Error,
                        package.FolderId,
                        file.StoredRelativePath,
                        "load",
                        null,
                        $"无法读取 XML：{ex.Message}"));
                    continue;
                }

                if (package.Kind == X4ContentKind.BaseGame)
                {
                    if (packageRelativePath.Equals(normalizedVirtualPath, StringComparison.OrdinalIgnoreCase))
                        effectiveDocument = document;
                    continue;
                }

                if (file.SourceKind == X4DataFileSourceKind.SubstitutionCatalog)
                {
                    if (packageRelativePath.Equals(normalizedVirtualPath, StringComparison.OrdinalIgnoreCase))
                        effectiveDocument = document;
                    continue;
                }

                if (document.Root?.Name.LocalName == "diff")
                {
                    var targetPath = packageRelativePath;
                    if (!targetPath.Equals(normalizedVirtualPath, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (effectiveDocument == null)
                    {
                        diagnostics.Add(new X4XmlPatchDiagnostic(
                            X4XmlPatchDiagnosticSeverity.Error,
                            package.FolderId,
                            file.StoredRelativePath,
                            "diff",
                            null,
                            $"补丁目标不存在：{targetPath}"));
                        continue;
                    }

                    try
                    {
                        var patchResult = _patchEngine.Apply(
                            effectiveDocument,
                            document,
                            package.FolderId,
                            file.StoredRelativePath);
                        effectiveDocument = patchResult.Document;
                        diagnostics.AddRange(patchResult.Diagnostics);
                    }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException)
                    {
                        diagnostics.Add(new X4XmlPatchDiagnostic(
                            X4XmlPatchDiagnosticSeverity.Error,
                            package.FolderId,
                            file.StoredRelativePath,
                            "diff",
                            null,
                            $"应用 XML 补丁失败：{ex.Message}"));
                    }
                    continue;
                }

                var mountedPath = GetExtensionMountPath(package.FolderId, packageRelativePath);
                if (mountedPath.Equals(normalizedVirtualPath, StringComparison.OrdinalIgnoreCase))
                    effectiveDocument = document;
            }
        }

        if (effectiveDocument == null)
        {
            diagnostics.Add(new X4XmlPatchDiagnostic(
                X4XmlPatchDiagnosticSeverity.Error,
                X4CatalogExtractor.BaseGamePackageId,
                normalizedVirtualPath,
                "load",
                null,
                $"有效 XML 路径不存在：{normalizedVirtualPath}"));
        }

        return new X4EffectiveXmlResult(normalizedVirtualPath, effectiveDocument, diagnostics);
    }

    private static void AddCandidate(
        IDictionary<string, List<CandidateSource>> candidates,
        string path,
        CandidateSource source)
    {
        if (!candidates.TryGetValue(path, out var list))
        {
            list = [];
            candidates[path] = list;
        }
        list.Add(source);
    }

    private static string GetExtensionMountPath(string packageId, string packageRelativePath) =>
        packageRelativePath.StartsWith("extensions/", StringComparison.OrdinalIgnoreCase)
            ? packageRelativePath
            : $"extensions/{packageId}/{packageRelativePath}";

    private string ResolveStoredPath(string storedRelativePath)
    {
        var normalized = X4VirtualPath.Normalize(storedRelativePath, "GameData 清单");
        var systemPath = normalized.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(_dataRoot, systemPath));
        var rootPrefix = _dataRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"GameData 清单路径越界：{storedRelativePath}");
        return fullPath;
    }
}
