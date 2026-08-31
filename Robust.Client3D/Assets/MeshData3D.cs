using System;
using System.Numerics;

namespace Robust.Client3D.Assets;

public readonly record struct MeshVertex3D(
    Vector3 Position,
    Vector3 Normal,
    Vector2 TexCoord);

public sealed class MeshData3D
{
    public MeshVertex3D[] Vertices { get; }
    public uint[] Indices { get; }

    public MeshData3D(MeshVertex3D[] vertices, uint[] indices)
    {
        Vertices = vertices ?? throw new ArgumentNullException(nameof(vertices));
        Indices = indices ?? throw new ArgumentNullException(nameof(indices));

        if (Vertices.Length == 0)
            throw new ArgumentException("A 3D mesh must contain at least one vertex.", nameof(vertices));
        if (Indices.Length == 0 || Indices.Length % 3 != 0)
            throw new ArgumentException("A triangle mesh must contain a non-empty index list divisible by three.", nameof(indices));

        foreach (var index in Indices)
        {
            if (index >= Vertices.Length)
                throw new ArgumentOutOfRangeException(nameof(indices), index, "Mesh index points outside the vertex array.");
        }
    }
}
