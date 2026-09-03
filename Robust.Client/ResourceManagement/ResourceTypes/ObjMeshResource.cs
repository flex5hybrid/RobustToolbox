using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using Robust.Shared.ContentPack;
using Robust.Shared.IoC;
using Robust.Shared.Utility;

namespace Robust.Client.ResourceManagement;

/// <summary>
/// Dependency-free Wavefront OBJ decoder for native 3D content. Faces of any size are triangulated as fans.
/// The decoded representation is immutable and suitable for instancing through entity transforms.
/// </summary>
public sealed class ObjMeshResource : BaseResource, IBaseResource
{
    private MeshVertex3D[] _vertices = Array.Empty<MeshVertex3D>();

    public ReadOnlySpan<MeshVertex3D> Vertices => _vertices;
    public override ResPath? Fallback => null;
    static bool IBaseResource.CanBeRemoved => true;

    public override void Load(IDependencyCollection dependencies, ResPath path)
    {
        var resourceManager = dependencies.Resolve<IResourceManager>();
        using var stream = resourceManager.ContentFileRead(path);
        using var reader = new StreamReader(stream);
        _vertices = Decode(reader);
    }

    internal static MeshVertex3D[] Decode(TextReader reader)
    {
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var textureCoordinates = new List<Vector2>();
        var output = new List<MeshVertex3D>();
        string? line;

        while ((line = reader.ReadLine()) is not null)
        {
            var comment = line.IndexOf('#');
            if (comment >= 0)
                line = line[..comment];

            var parts = line.Split((char[]?) null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                continue;

            switch (parts[0])
            {
                case "v" when parts.Length >= 4:
                    positions.Add(new Vector3(Parse(parts[1]), Parse(parts[2]), Parse(parts[3])));
                    break;
                case "vn" when parts.Length >= 4:
                {
                    var normal = new Vector3(Parse(parts[1]), Parse(parts[2]), Parse(parts[3]));
                    normals.Add(normal.LengthSquared() > 1e-8f ? Vector3.Normalize(normal) : Vector3.UnitZ);
                    break;
                }
                case "vt" when parts.Length >= 3:
                    textureCoordinates.Add(new Vector2(Parse(parts[1]), 1f - Parse(parts[2])));
                    break;
                case "f" when parts.Length >= 4:
                    AppendFace(parts, positions, normals, textureCoordinates, output);
                    break;
            }
        }

        if (output.Count == 0)
            throw new InvalidDataException("OBJ resource contains no renderable triangles.");

        return output.ToArray();
    }

    private static void AppendFace(
        string[] parts,
        List<Vector3> positions,
        List<Vector3> normals,
        List<Vector2> textureCoordinates,
        List<MeshVertex3D> output)
    {
        var face = new MeshVertex3D[parts.Length - 1];
        for (var i = 1; i < parts.Length; i++)
            face[i - 1] = ParseVertex(parts[i], positions, normals, textureCoordinates);

        for (var i = 1; i < face.Length - 1; i++)
        {
            var a = face[0];
            var b = face[i];
            var c = face[i + 1];
            var faceNormal = Vector3.Cross(b.Position - a.Position, c.Position - a.Position);
            faceNormal = faceNormal.LengthSquared() > 1e-8f ? Vector3.Normalize(faceNormal) : Vector3.UnitZ;
            output.Add(a with { Normal = a.Normal ?? faceNormal });
            output.Add(b with { Normal = b.Normal ?? faceNormal });
            output.Add(c with { Normal = c.Normal ?? faceNormal });
        }
    }

    private static MeshVertex3D ParseVertex(
        string token,
        List<Vector3> positions,
        List<Vector3> normals,
        List<Vector2> textureCoordinates)
    {
        var indices = token.Split('/');
        if (indices.Length == 0 || string.IsNullOrWhiteSpace(indices[0]))
            throw new InvalidDataException($"OBJ face vertex '{token}' has no position index.");

        var position = positions[ResolveIndex(indices[0], positions.Count)];
        var uv = indices.Length > 1 && !string.IsNullOrWhiteSpace(indices[1])
            ? textureCoordinates[ResolveIndex(indices[1], textureCoordinates.Count)]
            : Vector2.Zero;
        Vector3? normal = indices.Length > 2 && !string.IsNullOrWhiteSpace(indices[2])
            ? normals[ResolveIndex(indices[2], normals.Count)]
            : null;
        return new MeshVertex3D(position, normal, uv);
    }

    private static int ResolveIndex(string value, int count)
    {
        var index = int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
        index = index > 0 ? index - 1 : count + index;
        if ((uint) index >= (uint) count)
            throw new InvalidDataException($"OBJ index {value} is outside a collection of {count} elements.");
        return index;
    }

    private static float Parse(string value)
    {
        return float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}

public readonly record struct MeshVertex3D(Vector3 Position, Vector3? Normal, Vector2 Uv);
