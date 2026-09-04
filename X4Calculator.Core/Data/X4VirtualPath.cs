namespace X4Calculator.Core.Data;

internal static class X4VirtualPath
{
    public static string Normalize(string path, string sourceLabel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Path.IsPathRooted(path) || path.StartsWith("\\\\", StringComparison.Ordinal))
            throw new InvalidDataException($"{sourceLabel} 包含绝对路径：{path}");
        var normalized = path.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized[2..];
        normalized = normalized.TrimStart('/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(IsUnsafeWindowsPathSegment))
            throw new InvalidDataException($"{sourceLabel} 包含不安全路径：{path}");
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
}
