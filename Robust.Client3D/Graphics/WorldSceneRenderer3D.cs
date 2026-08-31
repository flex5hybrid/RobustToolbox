using System;
using System.Collections.Generic;
using System.IO;
using Robust.Client3D.Assets;
using Robust.Shared3D;

namespace Robust.Client3D.Graphics;

public sealed class WorldSceneRenderer3D : IDisposable
{
    private readonly Dictionary<string, GpuMesh3D> _meshes = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public WorldSceneRenderer3D(WorldDefinition3D definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        try
        {
            foreach (var worldObject in definition.Objects)
            {
                if (worldObject.ModelPath is null || _meshes.ContainsKey(worldObject.ModelPath))
                    continue;

                var asset = LoadMesh(worldObject.ModelPath);
                var gpuMesh = new GpuMesh3D(asset.Mesh, asset.Material);
                _meshes.Add(worldObject.ModelPath, gpuMesh);
                Console.WriteLine(
                    $"Loaded world mesh {worldObject.ModelPath}: vertices={asset.Mesh.Vertices.Length}; " +
                    $"triangles={asset.Mesh.Indices.Length / 3}; texture={(gpuMesh.HasBaseColorTexture ? "yes" : "no")}");
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public GpuMesh3D GetMesh(string modelPath)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WorldSceneRenderer3D));

        var normalized = WorldResourceIdentity3D.NormalizeResourcePath(modelPath);
        return _meshes.TryGetValue(normalized, out var mesh)
            ? mesh
            : throw new KeyNotFoundException($"World mesh '{normalized}' was not loaded.");
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        foreach (var mesh in _meshes.Values)
            mesh.Dispose();
        _meshes.Clear();
        _disposed = true;
    }

    private static LoadedMeshAsset3D LoadMesh(string resourcePath)
    {
        var normalized = WorldResourceIdentity3D.NormalizeResourcePath(resourcePath);
        var fullPath = WorldResourceIdentity3D.ResolveUnderRoot(AppContext.BaseDirectory, normalized);
        var bytes = File.ReadAllBytes(fullPath);
        var extension = Path.GetExtension(fullPath);

        byte[] ResolveExternalResource(string uri)
        {
            if (string.IsNullOrWhiteSpace(uri))
                throw new InvalidOperationException($"Model '{normalized}' references an empty external resource URI.");
            if (Uri.TryCreate(uri, UriKind.Absolute, out _))
                throw new NotSupportedException($"Model '{normalized}' cannot load an absolute external resource URI.");

            var modelDirectory = Path.GetDirectoryName(fullPath)
                                 ?? throw new InvalidOperationException($"Model '{normalized}' has no directory.");
            var relativeDirectory = Path.GetRelativePath(AppContext.BaseDirectory, modelDirectory)
                .Replace('\\', '/');
            var combinedResource = relativeDirectory == "."
                ? uri
                : $"{relativeDirectory}/{uri}";
            var resourceFullPath = WorldResourceIdentity3D.ResolveUnderRoot(AppContext.BaseDirectory, combinedResource);
            return File.ReadAllBytes(resourceFullPath);
        }

        if (extension.Equals(".gltf", StringComparison.OrdinalIgnoreCase))
        {
            return new LoadedMeshAsset3D(
                GltfStaticMeshLoader3D.Load(bytes, ResolveExternalResource),
                GltfMaterialLoader3D.Load(bytes, ResolveExternalResource));
        }

        if (extension.Equals(".glb", StringComparison.OrdinalIgnoreCase))
        {
            return new LoadedMeshAsset3D(
                GlbStaticMeshLoader3D.Load(bytes, ResolveExternalResource),
                MaterialData3D.Default);
        }

        throw new NotSupportedException(
            $"Unsupported 3D model extension '{extension}' for resource '{normalized}'.");
    }

    private readonly record struct LoadedMeshAsset3D(
        MeshData3D Mesh,
        MaterialData3D Material);
}
