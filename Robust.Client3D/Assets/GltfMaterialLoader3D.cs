using System;
using System.Numerics;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Robust.Client3D.Assets;

public static class GltfMaterialLoader3D
{
    public static MaterialData3D Load(
        ReadOnlySpan<byte> jsonUtf8,
        Func<string, byte[]>? externalResourceResolver = null)
    {
        using var document = JsonDocument.Parse(jsonUtf8.ToArray());
        var root = document.RootElement;

        if (!root.TryGetProperty("meshes", out var meshes) || meshes.ValueKind != JsonValueKind.Array || meshes.GetArrayLength() == 0)
            return MaterialData3D.Default;

        var mesh = meshes[0];
        if (!mesh.TryGetProperty("primitives", out var primitives) || primitives.ValueKind != JsonValueKind.Array || primitives.GetArrayLength() == 0)
            return MaterialData3D.Default;

        var primitive = primitives[0];
        if (!primitive.TryGetProperty("material", out var materialElement))
            return MaterialData3D.Default;

        if (!root.TryGetProperty("materials", out var materials) || materials.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("glTF primitive references a material but the document has no materials array.");

        var materialIndex = materialElement.GetInt32();
        if ((uint) materialIndex >= (uint) materials.GetArrayLength())
            throw new InvalidOperationException($"glTF material index {materialIndex} is out of range.");

        var material = materials[materialIndex];
        if (!material.TryGetProperty("pbrMetallicRoughness", out var pbr))
            return MaterialData3D.Default;

        var baseColorFactor = ReadBaseColorFactor(pbr);
        var baseColorTexture = ReadBaseColorTexture(root, pbr, externalResourceResolver);
        return new MaterialData3D(baseColorFactor, baseColorTexture);
    }

    public static MaterialData3D Load(
        string json,
        Func<string, byte[]>? externalResourceResolver = null)
    {
        return Load(System.Text.Encoding.UTF8.GetBytes(json), externalResourceResolver);
    }

    private static Vector4 ReadBaseColorFactor(JsonElement pbr)
    {
        if (!pbr.TryGetProperty("baseColorFactor", out var factor))
            return Vector4.One;
        if (factor.ValueKind != JsonValueKind.Array || factor.GetArrayLength() != 4)
            throw new InvalidOperationException("glTF baseColorFactor must contain exactly four numbers.");

        var value = new Vector4(
            factor[0].GetSingle(),
            factor[1].GetSingle(),
            factor[2].GetSingle(),
            factor[3].GetSingle());
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) ||
            !float.IsFinite(value.Z) || !float.IsFinite(value.W))
        {
            throw new InvalidOperationException("glTF baseColorFactor contains a non-finite number.");
        }

        return value;
    }

    private static TextureImageData3D? ReadBaseColorTexture(
        JsonElement root,
        JsonElement pbr,
        Func<string, byte[]>? externalResourceResolver)
    {
        if (!pbr.TryGetProperty("baseColorTexture", out var baseColorTexture))
            return null;

        var textureIndex = baseColorTexture.GetProperty("index").GetInt32();
        var textures = GetRequiredArray(root, "textures");
        if ((uint) textureIndex >= (uint) textures.GetArrayLength())
            throw new InvalidOperationException($"glTF texture index {textureIndex} is out of range.");

        var texture = textures[textureIndex];
        var imageIndex = texture.GetProperty("source").GetInt32();
        var images = GetRequiredArray(root, "images");
        if ((uint) imageIndex >= (uint) images.GetArrayLength())
            throw new InvalidOperationException($"glTF image index {imageIndex} is out of range.");

        var image = images[imageIndex];
        if (!image.TryGetProperty("uri", out var uriElement))
        {
            throw new NotSupportedException(
                "bufferView-backed glTF images are not supported by the bootstrap material loader yet.");
        }

        var uri = uriElement.GetString()
                  ?? throw new InvalidOperationException("glTF image URI is null.");
        var encoded = uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            ? DecodeDataUri(uri)
            : externalResourceResolver?.Invoke(uri)
              ?? throw new InvalidOperationException($"No resolver was supplied for external glTF image '{uri}'.");

        using var decoded = Image.Load<Rgba32>(encoded);
        var pixels = new byte[checked(decoded.Width * decoded.Height * 4)];
        decoded.CopyPixelDataTo(pixels);
        return new TextureImageData3D(decoded.Width, decoded.Height, pixels);
    }

    private static JsonElement GetRequiredArray(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"glTF document is missing required array '{property}'.");
        return array;
    }

    private static byte[] DecodeDataUri(string uri)
    {
        var comma = uri.IndexOf(',');
        if (comma < 0)
            throw new InvalidOperationException("Malformed glTF data URI.");

        var metadata = uri[..comma];
        if (!metadata.EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Only base64 glTF image data URIs are supported.");

        return Convert.FromBase64String(uri[(comma + 1)..]);
    }
}
