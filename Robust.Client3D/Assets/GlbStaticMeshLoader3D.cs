using System;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;

namespace Robust.Client3D.Assets;

public static class GlbStaticMeshLoader3D
{
    private const uint Magic = 0x46546C67;
    private const uint Version = 2;
    private const uint JsonChunk = 0x4E4F534A;
    private const uint BinChunk = 0x004E4942;
    private const int HeaderSize = 12;
    private const int ChunkHeaderSize = 8;

    public static MeshData3D Load(
        ReadOnlySpan<byte> glb,
        Func<string, byte[]>? externalBufferResolver = null)
    {
        if (glb.Length < HeaderSize + ChunkHeaderSize)
            throw new InvalidOperationException("GLB container is too short.");

        if (BinaryPrimitives.ReadUInt32LittleEndian(glb) != Magic)
            throw new InvalidOperationException("GLB magic is invalid.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(glb[4..]) != Version)
            throw new NotSupportedException("Only GLB version 2 is supported.");

        var declaredLength = BinaryPrimitives.ReadUInt32LittleEndian(glb[8..]);
        if (declaredLength != (uint) glb.Length)
            throw new InvalidOperationException(
                $"GLB declared length {declaredLength} does not match actual length {glb.Length}.");

        var offset = HeaderSize;
        byte[]? json = null;
        byte[]? binary = null;
        var chunkIndex = 0;

        while (offset < glb.Length)
        {
            if (offset + ChunkHeaderSize > glb.Length)
                throw new InvalidOperationException("GLB chunk header is truncated.");

            var chunkLength = checked((int) BinaryPrimitives.ReadUInt32LittleEndian(glb[offset..]));
            var chunkType = BinaryPrimitives.ReadUInt32LittleEndian(glb[(offset + 4)..]);
            offset += ChunkHeaderSize;

            if (offset > glb.Length - chunkLength)
                throw new InvalidOperationException("GLB chunk points outside the container.");
            if ((chunkLength & 3) != 0)
                throw new InvalidOperationException("GLB chunks must be padded to a four-byte boundary.");

            var chunk = glb.Slice(offset, chunkLength);
            if (chunkIndex == 0 && chunkType != JsonChunk)
                throw new InvalidOperationException("The first GLB chunk must be JSON.");

            switch (chunkType)
            {
                case JsonChunk:
                    if (json is not null)
                        throw new InvalidOperationException("GLB container contains multiple JSON chunks.");
                    json = chunk.ToArray();
                    break;

                case BinChunk:
                    if (binary is not null)
                        throw new InvalidOperationException("GLB container contains multiple BIN chunks.");
                    binary = chunk.ToArray();
                    break;
            }

            offset += chunkLength;
            chunkIndex++;
        }

        if (json is null)
            throw new InvalidOperationException("GLB container does not contain a JSON chunk.");

        if (binary is null)
            return GltfStaticMeshLoader3D.Load(json, externalBufferResolver);

        var root = JsonNode.Parse(Encoding.UTF8.GetString(json))
                   ?? throw new InvalidOperationException("GLB JSON chunk is empty.");
        var buffers = root["buffers"]?.AsArray()
                      ?? throw new InvalidOperationException("GLB JSON does not define buffers.");

        JsonObject? embeddedBuffer = null;
        foreach (var bufferNode in buffers)
        {
            var buffer = bufferNode?.AsObject()
                         ?? throw new InvalidOperationException("GLB buffer declaration is invalid.");
            if (buffer["uri"] is not null)
                continue;

            if (embeddedBuffer is not null)
                throw new NotSupportedException("Only one URI-less GLB buffer is supported.");
            embeddedBuffer = buffer;
        }

        if (embeddedBuffer is null)
            throw new InvalidOperationException("GLB BIN chunk has no matching URI-less buffer declaration.");

        embeddedBuffer["uri"] = $"data:application/octet-stream;base64,{Convert.ToBase64String(binary)}";
        return GltfStaticMeshLoader3D.Load(root.ToJsonString(), externalBufferResolver);
    }
}
