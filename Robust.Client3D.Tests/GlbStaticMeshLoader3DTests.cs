using System;
using System.IO;
using System.Numerics;
using System.Text;
using NUnit.Framework;
using Robust.Client3D.Assets;

namespace Robust.Client3D.Tests;

[TestFixture]
public sealed class GlbStaticMeshLoader3DTests
{
    [Test]
    public void LoadsEmbeddedBinaryTriangle()
    {
        var binary = new byte[44];
        var offset = 0;
        WriteVector3(binary, ref offset, new Vector3(0f, 0f, 0f));
        WriteVector3(binary, ref offset, new Vector3(1f, 0f, 0f));
        WriteVector3(binary, ref offset, new Vector3(0f, 1f, 0f));
        WriteUnsignedShort(binary, ref offset, 0);
        WriteUnsignedShort(binary, ref offset, 1);
        WriteUnsignedShort(binary, ref offset, 2);

        const string json = """
        {
          "asset": { "version": "2.0" },
          "buffers": [ { "byteLength": 42 } ],
          "bufferViews": [
            { "buffer": 0, "byteOffset": 0, "byteLength": 36 },
            { "buffer": 0, "byteOffset": 36, "byteLength": 6 }
          ],
          "accessors": [
            { "bufferView": 0, "componentType": 5126, "count": 3, "type": "VEC3" },
            { "bufferView": 1, "componentType": 5123, "count": 3, "type": "SCALAR" }
          ],
          "meshes": [
            {
              "primitives": [
                {
                  "attributes": { "POSITION": 0 },
                  "indices": 1,
                  "mode": 4
                }
              ]
            }
          ]
        }
        """;

        var glb = BuildGlb(json, binary);
        var mesh = GlbStaticMeshLoader3D.Load(glb);

        Assert.That(mesh.Vertices, Has.Length.EqualTo(3));
        Assert.That(mesh.Indices, Is.EqualTo(new uint[] { 0, 1, 2 }));
        Assert.That(mesh.Vertices[1].Position, Is.EqualTo(new Vector3(1f, 0f, 0f)));
        Assert.That(mesh.Vertices[2].Position, Is.EqualTo(new Vector3(0f, 1f, 0f)));
    }

    [Test]
    public void RejectsIncorrectContainerLength()
    {
        var glb = BuildGlb(
            """
            {
              "asset": { "version": "2.0" },
              "buffers": [],
              "bufferViews": [],
              "accessors": [],
              "meshes": []
            }
            """,
            Array.Empty<byte>());

        glb[8] ^= 1;
        Assert.That(
            () => GlbStaticMeshLoader3D.Load(glb),
            Throws.TypeOf<InvalidOperationException>());
    }

    private static byte[] BuildGlb(string json, byte[] binary)
    {
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        var paddedJsonLength = Align4(jsonBytes.Length);
        var paddedBinaryLength = Align4(binary.Length);
        var totalLength = 12 + 8 + paddedJsonLength + 8 + paddedBinaryLength;

        using var stream = new MemoryStream(totalLength);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(0x46546C67u);
        writer.Write(2u);
        writer.Write((uint) totalLength);
        writer.Write((uint) paddedJsonLength);
        writer.Write(0x4E4F534Au);
        writer.Write(jsonBytes);
        for (var i = jsonBytes.Length; i < paddedJsonLength; i++)
            writer.Write((byte) 0x20);

        writer.Write((uint) paddedBinaryLength);
        writer.Write(0x004E4942u);
        writer.Write(binary);
        for (var i = binary.Length; i < paddedBinaryLength; i++)
            writer.Write((byte) 0);

        writer.Flush();
        return stream.ToArray();
    }

    private static int Align4(int value)
    {
        return (value + 3) & ~3;
    }

    private static void WriteVector3(byte[] buffer, ref int offset, Vector3 value)
    {
        WriteFloat(buffer, ref offset, value.X);
        WriteFloat(buffer, ref offset, value.Y);
        WriteFloat(buffer, ref offset, value.Z);
    }

    private static void WriteFloat(byte[] buffer, ref int offset, float value)
    {
        BitConverter.GetBytes(value).CopyTo(buffer, offset);
        offset += sizeof(float);
    }

    private static void WriteUnsignedShort(byte[] buffer, ref int offset, ushort value)
    {
        BitConverter.GetBytes(value).CopyTo(buffer, offset);
        offset += sizeof(ushort);
    }
}
