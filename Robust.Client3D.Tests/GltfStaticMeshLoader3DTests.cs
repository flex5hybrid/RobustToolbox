using System;
using System.Collections.Generic;
using System.Numerics;
using NUnit.Framework;
using Robust.Client3D.Assets;

namespace Robust.Client3D.Tests;

[TestFixture]
public sealed class GltfStaticMeshLoader3DTests
{
    [Test]
    public void LoadsIndexedTriangleWithNormalsAndUvsFromDataUri()
    {
        var bytes = new List<byte>();

        AppendVector3(bytes, new Vector3(0f, 0f, 0f));
        AppendVector3(bytes, new Vector3(1f, 0f, 0f));
        AppendVector3(bytes, new Vector3(0f, 1f, 0f));

        for (var i = 0; i < 3; i++)
            AppendVector3(bytes, Vector3.UnitZ);

        AppendVector2(bytes, new Vector2(0f, 0f));
        AppendVector2(bytes, new Vector2(1f, 0f));
        AppendVector2(bytes, new Vector2(0f, 1f));

        AppendUnsignedShort(bytes, 0);
        AppendUnsignedShort(bytes, 1);
        AppendUnsignedShort(bytes, 2);

        var base64 = Convert.ToBase64String(bytes.ToArray());
        var json = $$"""
        {
          "asset": { "version": "2.0" },
          "buffers": [
            { "uri": "data:application/octet-stream;base64,{{base64}}", "byteLength": {{bytes.Count}} }
          ],
          "bufferViews": [
            { "buffer": 0, "byteOffset": 0, "byteLength": 36 },
            { "buffer": 0, "byteOffset": 36, "byteLength": 36 },
            { "buffer": 0, "byteOffset": 72, "byteLength": 24 },
            { "buffer": 0, "byteOffset": 96, "byteLength": 6 }
          ],
          "accessors": [
            { "bufferView": 0, "componentType": 5126, "count": 3, "type": "VEC3" },
            { "bufferView": 1, "componentType": 5126, "count": 3, "type": "VEC3" },
            { "bufferView": 2, "componentType": 5126, "count": 3, "type": "VEC2" },
            { "bufferView": 3, "componentType": 5123, "count": 3, "type": "SCALAR" }
          ],
          "meshes": [
            {
              "primitives": [
                {
                  "attributes": { "POSITION": 0, "NORMAL": 1, "TEXCOORD_0": 2 },
                  "indices": 3,
                  "mode": 4
                }
              ]
            }
          ]
        }
        """;

        var mesh = GltfStaticMeshLoader3D.Load(json);

        Assert.That(mesh.Vertices, Has.Length.EqualTo(3));
        Assert.That(mesh.Indices, Is.EqualTo(new uint[] { 0, 1, 2 }));
        Assert.That(mesh.Vertices[1].Position, Is.EqualTo(new Vector3(1f, 0f, 0f)));
        Assert.That(mesh.Vertices[2].Normal, Is.EqualTo(Vector3.UnitZ));
        Assert.That(mesh.Vertices[2].TexCoord, Is.EqualTo(new Vector2(0f, 1f)));
    }

    [Test]
    public void RejectsNonTrianglePrimitiveMode()
    {
        const string json = """
        {
          "asset": { "version": "2.0" },
          "buffers": [],
          "bufferViews": [],
          "accessors": [],
          "meshes": [
            { "primitives": [ { "mode": 1, "attributes": { "POSITION": 0 } } ] }
          ]
        }
        """;

        Assert.That(
            () => GltfStaticMeshLoader3D.Load(json),
            Throws.TypeOf<NotSupportedException>());
    }

    private static void AppendVector3(List<byte> bytes, Vector3 value)
    {
        AppendFloat(bytes, value.X);
        AppendFloat(bytes, value.Y);
        AppendFloat(bytes, value.Z);
    }

    private static void AppendVector2(List<byte> bytes, Vector2 value)
    {
        AppendFloat(bytes, value.X);
        AppendFloat(bytes, value.Y);
    }

    private static void AppendFloat(List<byte> bytes, float value)
    {
        bytes.AddRange(BitConverter.GetBytes(value));
    }

    private static void AppendUnsignedShort(List<byte> bytes, ushort value)
    {
        bytes.AddRange(BitConverter.GetBytes(value));
    }
}
