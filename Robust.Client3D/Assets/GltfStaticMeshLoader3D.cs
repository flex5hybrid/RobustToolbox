using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace Robust.Client3D.Assets;

public static class GltfStaticMeshLoader3D
{
    private const int ComponentByte = 5121;
    private const int ComponentUnsignedShort = 5123;
    private const int ComponentUnsignedInt = 5125;
    private const int ComponentFloat = 5126;
    private const int PrimitiveTriangles = 4;

    public static MeshData3D Load(
        ReadOnlySpan<byte> jsonUtf8,
        Func<string, byte[]>? externalBufferResolver = null)
    {
        using var document = JsonDocument.Parse(jsonUtf8);
        var root = document.RootElement;

        var buffers = LoadBuffers(root, externalBufferResolver);
        var bufferViews = ReadArray(root, "bufferViews");
        var accessors = ReadArray(root, "accessors");
        var meshes = ReadArray(root, "meshes");
        if (meshes.GetArrayLength() == 0)
            throw new InvalidOperationException("glTF file contains no meshes.");

        var primitives = ReadArray(meshes[0], "primitives");
        if (primitives.GetArrayLength() == 0)
            throw new InvalidOperationException("glTF mesh contains no primitives.");

        var primitive = primitives[0];
        var mode = primitive.TryGetProperty("mode", out var modeElement)
            ? modeElement.GetInt32()
            : PrimitiveTriangles;
        if (mode != PrimitiveTriangles)
            throw new NotSupportedException($"Only TRIANGLES glTF primitives are supported, got mode {mode}.");

        var attributes = primitive.GetProperty("attributes");
        if (!attributes.TryGetProperty("POSITION", out var positionAccessorElement))
            throw new InvalidOperationException("glTF primitive does not contain POSITION data.");

        var positions = ReadVector3Accessor(
            positionAccessorElement.GetInt32(),
            buffers,
            bufferViews,
            accessors,
            "POSITION");
        var normals = attributes.TryGetProperty("NORMAL", out var normalAccessorElement)
            ? ReadVector3Accessor(normalAccessorElement.GetInt32(), buffers, bufferViews, accessors, "NORMAL")
            : new Vector3[positions.Length];
        var texCoords = attributes.TryGetProperty("TEXCOORD_0", out var uvAccessorElement)
            ? ReadVector2Accessor(uvAccessorElement.GetInt32(), buffers, bufferViews, accessors, "TEXCOORD_0")
            : new Vector2[positions.Length];

        if (normals.Length != positions.Length || texCoords.Length != positions.Length)
            throw new InvalidOperationException("glTF vertex attribute accessors have mismatched element counts.");

        uint[] indices;
        if (primitive.TryGetProperty("indices", out var indicesAccessorElement))
        {
            indices = ReadIndexAccessor(indicesAccessorElement.GetInt32(), buffers, bufferViews, accessors);
        }
        else
        {
            if (positions.Length % 3 != 0)
                throw new InvalidOperationException("Non-indexed TRIANGLES primitive vertex count must be divisible by three.");

            indices = new uint[positions.Length];
            for (var i = 0; i < indices.Length; i++)
                indices[i] = (uint) i;
        }

        var vertices = new MeshVertex3D[positions.Length];
        for (var i = 0; i < vertices.Length; i++)
            vertices[i] = new MeshVertex3D(positions[i], normals[i], texCoords[i]);

        return new MeshData3D(vertices, indices);
    }

    public static MeshData3D Load(string json, Func<string, byte[]>? externalBufferResolver = null)
    {
        return Load(Encoding.UTF8.GetBytes(json), externalBufferResolver);
    }

    private static byte[][] LoadBuffers(JsonElement root, Func<string, byte[]>? externalBufferResolver)
    {
        var bufferElements = ReadArray(root, "buffers");
        var buffers = new byte[bufferElements.GetArrayLength()][];

        for (var i = 0; i < buffers.Length; i++)
        {
            var element = bufferElements[i];
            if (!element.TryGetProperty("uri", out var uriElement))
                throw new NotSupportedException("Binary .glb buffers are not supported by the bootstrap glTF loader yet.");

            var uri = uriElement.GetString() ?? throw new InvalidOperationException("glTF buffer URI is null.");
            buffers[i] = uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                ? DecodeDataUri(uri)
                : externalBufferResolver?.Invoke(uri)
                  ?? throw new InvalidOperationException($"No resolver was supplied for external glTF buffer '{uri}'.");

            if (element.TryGetProperty("byteLength", out var byteLengthElement) &&
                buffers[i].Length < byteLengthElement.GetInt32())
            {
                throw new InvalidOperationException($"glTF buffer {i} is shorter than its declared byteLength.");
            }
        }

        return buffers;
    }

    private static Vector3[] ReadVector3Accessor(
        int accessorIndex,
        byte[][] buffers,
        JsonElement bufferViews,
        JsonElement accessors,
        string semantic)
    {
        var accessor = accessors[accessorIndex];
        ValidateAccessor(accessor, ComponentFloat, "VEC3", semantic);
        var count = accessor.GetProperty("count").GetInt32();
        var result = new Vector3[count];
        var slice = ResolveAccessor(accessor, 12, buffers, bufferViews);

        for (var i = 0; i < count; i++)
        {
            var offset = slice.Offset + i * slice.Stride;
            result[i] = new Vector3(
                ReadFloat(slice.Data, offset),
                ReadFloat(slice.Data, offset + 4),
                ReadFloat(slice.Data, offset + 8));
        }

        return result;
    }

    private static Vector2[] ReadVector2Accessor(
        int accessorIndex,
        byte[][] buffers,
        JsonElement bufferViews,
        JsonElement accessors,
        string semantic)
    {
        var accessor = accessors[accessorIndex];
        ValidateAccessor(accessor, ComponentFloat, "VEC2", semantic);
        var count = accessor.GetProperty("count").GetInt32();
        var result = new Vector2[count];
        var slice = ResolveAccessor(accessor, 8, buffers, bufferViews);

        for (var i = 0; i < count; i++)
        {
            var offset = slice.Offset + i * slice.Stride;
            result[i] = new Vector2(
                ReadFloat(slice.Data, offset),
                ReadFloat(slice.Data, offset + 4));
        }

        return result;
    }

    private static uint[] ReadIndexAccessor(
        int accessorIndex,
        byte[][] buffers,
        JsonElement bufferViews,
        JsonElement accessors)
    {
        var accessor = accessors[accessorIndex];
        if (accessor.GetProperty("type").GetString() != "SCALAR")
            throw new InvalidOperationException("glTF index accessor must use SCALAR type.");

        var componentType = accessor.GetProperty("componentType").GetInt32();
        var elementSize = componentType switch
        {
            ComponentByte => 1,
            ComponentUnsignedShort => 2,
            ComponentUnsignedInt => 4,
            _ => throw new NotSupportedException($"Unsupported glTF index componentType {componentType}."),
        };

        var count = accessor.GetProperty("count").GetInt32();
        var result = new uint[count];
        var slice = ResolveAccessor(accessor, elementSize, buffers, bufferViews);

        for (var i = 0; i < count; i++)
        {
            var offset = slice.Offset + i * slice.Stride;
            result[i] = componentType switch
            {
                ComponentByte => slice.Data[offset],
                ComponentUnsignedShort => BinaryPrimitives.ReadUInt16LittleEndian(slice.Data.AsSpan(offset, 2)),
                ComponentUnsignedInt => BinaryPrimitives.ReadUInt32LittleEndian(slice.Data.AsSpan(offset, 4)),
                _ => 0,
            };
        }

        return result;
    }

    private static AccessorSlice ResolveAccessor(
        JsonElement accessor,
        int packedElementSize,
        byte[][] buffers,
        JsonElement bufferViews)
    {
        if (accessor.TryGetProperty("sparse", out _))
            throw new NotSupportedException("Sparse glTF accessors are not supported yet.");

        var viewIndex = accessor.GetProperty("bufferView").GetInt32();
        var view = bufferViews[viewIndex];
        var bufferIndex = view.GetProperty("buffer").GetInt32();
        var data = buffers[bufferIndex];
        var viewOffset = view.TryGetProperty("byteOffset", out var viewOffsetElement)
            ? viewOffsetElement.GetInt32()
            : 0;
        var accessorOffset = accessor.TryGetProperty("byteOffset", out var accessorOffsetElement)
            ? accessorOffsetElement.GetInt32()
            : 0;
        var stride = view.TryGetProperty("byteStride", out var strideElement)
            ? strideElement.GetInt32()
            : packedElementSize;

        if (stride < packedElementSize)
            throw new InvalidOperationException("glTF bufferView byteStride is smaller than the accessor element size.");

        var count = accessor.GetProperty("count").GetInt32();
        var firstOffset = checked(viewOffset + accessorOffset);
        var requiredLength = count == 0
            ? firstOffset
            : checked(firstOffset + (count - 1) * stride + packedElementSize);
        if (firstOffset < 0 || requiredLength > data.Length)
            throw new InvalidOperationException("glTF accessor points outside its buffer.");

        return new AccessorSlice(data, firstOffset, stride);
    }

    private static void ValidateAccessor(JsonElement accessor, int componentType, string type, string semantic)
    {
        if (accessor.GetProperty("componentType").GetInt32() != componentType ||
            accessor.GetProperty("type").GetString() != type)
        {
            throw new NotSupportedException(
                $"{semantic} must use componentType {componentType} and type {type} in the bootstrap loader.");
        }
    }

    private static float ReadFloat(byte[] data, int offset)
    {
        var bits = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
        return BitConverter.Int32BitsToSingle(bits);
    }

    private static byte[] DecodeDataUri(string uri)
    {
        var comma = uri.IndexOf(',');
        if (comma < 0)
            throw new InvalidOperationException("Malformed glTF data URI.");

        var metadata = uri[..comma];
        if (!metadata.EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Only base64 glTF data URIs are supported.");

        return Convert.FromBase64String(uri[(comma + 1)..]);
    }

    private static JsonElement ReadArray(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"glTF document is missing required array '{property}'.");
        return value;
    }

    private readonly record struct AccessorSlice(byte[] Data, int Offset, int Stride);
}
