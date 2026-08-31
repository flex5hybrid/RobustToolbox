using System;
using System.IO;
using Robust.Shared3D;

namespace Robust.Client3D;

internal sealed record ClientWorldSession3D(
    WorldDefinition3D Definition,
    WorldResourceIdentity3D Identity,
    string FullPath)
{
    public static ClientWorldSession3D LoadAndVerify(
        string resourcePath,
        string expectedSha256)
    {
        var pathProbe = WorldResourceIdentity3D.Create(resourcePath, ReadOnlySpan<byte>.Empty);
        var fullPath = Path.Combine(
            AppContext.BaseDirectory,
            pathProbe.ResourcePath.Replace('/', Path.DirectorySeparatorChar));
        var bytes = File.ReadAllBytes(fullPath);
        var identity = WorldResourceIdentity3D.Create(resourcePath, bytes);

        if (!string.Equals(identity.Sha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"3D world hash mismatch for '{identity.ResourcePath}': " +
                $"server={expectedSha256}, client={identity.Sha256}.");
        }

        return new ClientWorldSession3D(
            WorldDefinition3DLoader.Load(bytes),
            identity,
            fullPath);
    }
}
