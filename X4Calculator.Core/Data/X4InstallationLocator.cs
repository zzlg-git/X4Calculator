using Microsoft.Win32;

namespace X4Calculator.Core.Data;

/// <summary>在本机商店安装位置中发现可读取的 X4: Foundations 游戏目录。</summary>
public sealed class X4InstallationLocator
{
    public const string SteamAppId = "392160";

    private readonly IReadOnlyList<string>? _candidateDirectories;
    private readonly IReadOnlyList<string>? _steamRoots;

    /// <param name="candidateDirectories">
    /// 直接检查的候选目录。传入非 <see langword="null"/> 值会替代系统常见目录发现，便于确定性测试。
    /// </param>
    /// <param name="steamRoots">
    /// Steam 安装根目录。传入非 <see langword="null"/> 值会替代注册表及常见 Steam 根目录发现。
    /// </param>
    public X4InstallationLocator(
        IEnumerable<string>? candidateDirectories = null,
        IEnumerable<string>? steamRoots = null)
    {
        _candidateDirectories = candidateDirectories?.ToList();
        _steamRoots = steamRoots?.ToList();
    }

    /// <summary>返回全部有效安装目录；相同目录只返回一次。</summary>
    public IReadOnlyList<string> FindInstallations()
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in EnumerateCandidates())
        {
            var normalized = TryNormalizePath(candidate);
            if (normalized == null || !seen.Add(normalized) || !IsGameDirectory(normalized)) continue;
            results.Add(normalized);
        }

        return results;
    }

    /// <summary>返回第一个有效安装目录，未找到时返回 <see langword="null"/>。</summary>
    public string? FindInstallation() => FindInstallations().FirstOrDefault();

    /// <summary>验证目录根层是否直接包含至少一组配对的 CAT/DAT。</summary>
    public static bool IsGameDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return false;

        try
        {
            return Directory.EnumerateFiles(directory, "*.cat", SearchOption.TopDirectoryOnly)
                .Where(path => !path.EndsWith("_sig.cat", StringComparison.OrdinalIgnoreCase))
                .Any(path => File.Exists(Path.ChangeExtension(path, ".dat")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private IEnumerable<string> EnumerateCandidates()
    {
        foreach (var steamRoot in _steamRoots ?? DiscoverSteamRoots())
        foreach (var installation in DiscoverSteamInstallations(steamRoot))
            yield return installation;

        foreach (var candidate in _candidateDirectories ?? DiscoverCommonStoreCandidates())
            yield return candidate;
    }

    private static IEnumerable<string> DiscoverSteamInstallations(string steamRoot)
    {
        var normalizedRoot = TryNormalizePath(steamRoot);
        if (normalizedRoot == null) yield break;

        var libraries = new List<string> { normalizedRoot };
        var libraryFoldersPath = Path.Combine(normalizedRoot, "steamapps", "libraryfolders.vdf");
        if (File.Exists(libraryFoldersPath))
        {
            VdfObject? root = null;
            try
            {
                root = VdfParser.Parse(File.ReadAllText(libraryFoldersPath));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // 一个损坏或不可读的 Steam 配置不应阻止继续检查默认库及其他商店目录。
            }

            if (root?.TryGetObject("libraryfolders", out var folders) == true)
            {
                foreach (var entry in folders.Values.Values)
                {
                    if (entry is string oldStylePath)
                    {
                        libraries.Add(oldStylePath);
                        continue;
                    }

                    if (entry is not VdfObject folder || !folder.TryGetString("path", out var path)) continue;
                    if (folder.TryGetObject("apps", out var apps) && apps.Values.ContainsKey(SteamAppId))
                        libraries.Add(path);
                    else if (File.Exists(Path.Combine(path, "steamapps", $"appmanifest_{SteamAppId}.acf")))
                        libraries.Add(path);
                }
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in libraries)
        {
            var normalizedLibrary = TryNormalizePath(library);
            if (normalizedLibrary == null || !seen.Add(normalizedLibrary)) continue;

            var manifestPath = Path.Combine(
                normalizedLibrary, "steamapps", $"appmanifest_{SteamAppId}.acf");
            var installDirectoryName = ReadSteamInstallDirectoryName(manifestPath) ?? "X4 Foundations";
            yield return Path.Combine(normalizedLibrary, "steamapps", "common", installDirectoryName);
        }
    }

    private static string? ReadSteamInstallDirectoryName(string manifestPath)
    {
        if (!File.Exists(manifestPath)) return null;
        try
        {
            var manifest = VdfParser.Parse(File.ReadAllText(manifestPath));
            return manifest.TryGetObject("AppState", out var appState) &&
                   appState.TryGetString("installdir", out var installDirectory) &&
                   !string.IsNullOrWhiteSpace(installDirectory) &&
                   installDirectory.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0
                ? installDirectory
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> DiscoverSteamRoots()
    {
        var roots = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            AddRegistryValue(roots, RegistryHive.CurrentUser, RegistryView.Default,
                @"Software\Valve\Steam", "SteamPath");
            AddRegistryValue(roots, RegistryHive.CurrentUser, RegistryView.Default,
                @"Software\Valve\Steam", "InstallPath");
            AddRegistryValue(roots, RegistryHive.LocalMachine, RegistryView.Registry64,
                @"Software\Valve\Steam", "InstallPath");
            AddRegistryValue(roots, RegistryHive.LocalMachine, RegistryView.Registry32,
                @"Software\Valve\Steam", "InstallPath");
        }

        AddIfPresent(roots, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        AddIfPresent(roots, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"));
        return roots;
    }

    private static IReadOnlyList<string> DiscoverCommonStoreCandidates()
    {
        var candidates = new List<string>();
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        AddIfPresent(candidates, Path.Combine(programFiles, "GOG Galaxy", "Games", "X4 Foundations"));
        AddIfPresent(candidates, Path.Combine(programFilesX86, "GOG Galaxy", "Games", "X4 Foundations"));
        AddIfPresent(candidates, Path.Combine(programFiles, "GOG Games", "X4 Foundations"));
        AddIfPresent(candidates, Path.Combine(programFilesX86, "GOG Games", "X4 Foundations"));
        AddIfPresent(candidates, Path.Combine(programFiles, "Epic Games", "X4Foundations"));
        AddIfPresent(candidates, Path.Combine(programFiles, "Epic Games", "X4 Foundations"));

        foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.Fixed))
        {
            AddIfPresent(candidates, Path.Combine(drive.RootDirectory.FullName, "GOG Games", "X4 Foundations"));
            AddIfPresent(candidates, Path.Combine(drive.RootDirectory.FullName, "Epic Games", "X4Foundations"));
            AddIfPresent(candidates, Path.Combine(drive.RootDirectory.FullName, "Epic Games", "X4 Foundations"));
        }

        return candidates;
    }

    private static void AddRegistryValue(
        ICollection<string> destinations,
        RegistryHive hive,
        RegistryView view,
        string keyPath,
        string valueName)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(keyPath);
            if (key?.GetValue(valueName) is string value) AddIfPresent(destinations, value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // 注册表项不可读时继续尝试其他来源。
        }
    }

    private static void AddIfPresent(ICollection<string> destinations, string path)
    {
        if (!string.IsNullOrWhiteSpace(path)) destinations.Add(path);
    }

    private static string? TryNormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private sealed class VdfObject
    {
        public Dictionary<string, object> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool TryGetObject(string key, out VdfObject value)
        {
            if (Values.TryGetValue(key, out var candidate) && candidate is VdfObject objectValue)
            {
                value = objectValue;
                return true;
            }

            value = null!;
            return false;
        }

        public bool TryGetString(string key, out string value)
        {
            if (Values.TryGetValue(key, out var candidate) && candidate is string stringValue)
            {
                value = stringValue;
                return true;
            }

            value = string.Empty;
            return false;
        }
    }

    private static class VdfParser
    {
        public static VdfObject Parse(string text)
        {
            var tokens = Tokenize(text).ToList();
            var index = 0;
            var result = ParseObject(tokens, ref index, requiresClosingBrace: false);
            if (index != tokens.Count) throw new InvalidDataException("VDF 包含多余内容。");
            return result;
        }

        private static VdfObject ParseObject(IReadOnlyList<string> tokens, ref int index, bool requiresClosingBrace)
        {
            var result = new VdfObject();
            while (index < tokens.Count)
            {
                if (tokens[index] == "}")
                {
                    if (!requiresClosingBrace) throw new InvalidDataException("VDF 包含多余右括号。");
                    index++;
                    return result;
                }

                var key = tokens[index++];
                if (key == "{" || index >= tokens.Count)
                    throw new InvalidDataException("VDF 键值结构无效。");

                if (tokens[index] == "{")
                {
                    index++;
                    result.Values[key] = ParseObject(tokens, ref index, requiresClosingBrace: true);
                }
                else if (tokens[index] != "}")
                {
                    result.Values[key] = tokens[index++];
                }
                else
                {
                    throw new InvalidDataException("VDF 缺少值。");
                }
            }

            if (requiresClosingBrace) throw new InvalidDataException("VDF 缺少右括号。");
            return result;
        }

        private static IEnumerable<string> Tokenize(string text)
        {
            var index = 0;
            while (index < text.Length)
            {
                if (char.IsWhiteSpace(text[index]))
                {
                    index++;
                    continue;
                }

                if (text[index] is '{' or '}')
                {
                    yield return text[index++].ToString();
                    continue;
                }

                if (text[index] != '"') throw new InvalidDataException("VDF 包含未加引号的内容。");
                index++;
                var value = new System.Text.StringBuilder();
                var closed = false;
                while (index < text.Length)
                {
                    var character = text[index++];
                    if (character == '"')
                    {
                        closed = true;
                        break;
                    }

                    if (character == '\\' && index < text.Length && text[index] is '\\' or '"')
                        character = text[index++];
                    value.Append(character);
                }

                if (!closed) throw new InvalidDataException("VDF 字符串未闭合。");
                yield return value.ToString();
            }
        }
    }
}
