using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Robust.Shared3D;

public readonly record struct WorldResourceIdentity3D(
    string ResourcePath,
    string Sha256)
{
    public static WorldResourceIdentity3D Create(string resourcePath, ReadOnlySpan<byte> bytes)
    {
        var normalized = NormalizeResourcePath(resourcePath);
        return new WorldResourceIdentity3D(
            normalized,
            Convert.ToHexString(SHA256.HashData(bytes)));
    }

    public static string NormalizeResourcePath(string resourcePath)
    {
        if (string.IsNullOrWhiteSpace(resourcePath))
            throw new ArgumentException("Resource path cannot be empty.", nameof(resourcePath));

        var trimmed = resourcePath.Trim();
        if (trimmed.IndexOf('\0') >= 0)
            throw new ArgumentException("Resource path cannot contain NUL characters.", nameof(resourcePath));
        if (Path.IsPathRooted(trimmed) || trimmed.StartsWith('/') || trimmed.StartsWith('\\'))
            throw new ArgumentException("Resource path must be relative to its resource root.", nameof(resourcePath));
        if (trimmed.Contains(':'))
            throw new ArgumentException("Resource path cannot contain a drive or URI scheme.", nameof(resourcePath));

        var parts = trimmed
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(static part => part is "." or ".."))
            throw new ArgumentException("Resource path cannot escape its resource root.", nameof(resourcePath));

        return string.Join('/', parts);
    }

    public static string ResolveUnderRoot(string resourceRoot, string resourcePath)
    {
        if (string.IsNullOrWhiteSpace(resourceRoot))
            throw new ArgumentException("Resource root cannot be empty.", nameof(resourceRoot));

        var normalized = NormalizeResourcePath(resourcePath);
        var root = Path.GetFullPath(resourceRoot);
        var fullPath = Path.GetFullPath(Path.Combine(
            root,
            normalized.Replace('/', Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootPrefix, comparison))
            throw new ArgumentException("Resolved resource path escaped its resource root.", nameof(resourcePath));

        return fullPath;
    }
}
