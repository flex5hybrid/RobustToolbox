using System.Security.Cryptography;

namespace Robust.Shared3D;

public readonly record struct WorldResourceIdentity3D(
    string ResourcePath,
    string Sha256)
{
    public static WorldResourceIdentity3D Create(string resourcePath, ReadOnlySpan<byte> bytes)
    {
        if (string.IsNullOrWhiteSpace(resourcePath))
            throw new ArgumentException("World resource path cannot be empty.", nameof(resourcePath));

        var normalized = resourcePath.Replace('\\', '/').TrimStart('/');
        if (normalized.Split('/').Any(static part => part == ".."))
            throw new ArgumentException("World resource path cannot escape its resource root.", nameof(resourcePath));

        return new WorldResourceIdentity3D(
            normalized,
            Convert.ToHexString(SHA256.HashData(bytes)));
    }
}
