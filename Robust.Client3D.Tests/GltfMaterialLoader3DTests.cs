using System.Numerics;
using NUnit.Framework;
using Robust.Client3D.Assets;

namespace Robust.Client3D.Tests;

[TestFixture]
public sealed class GltfMaterialLoader3DTests
{
    [Test]
    public void LoadsBaseColorFactorAndDataUriTexture()
    {
        const string json = """
        {
          "meshes": [
            { "primitives": [ { "material": 0 } ] }
          ],
          "materials": [
            {
              "pbrMetallicRoughness": {
                "baseColorFactor": [0.5, 0.75, 1.0, 0.8],
                "baseColorTexture": { "index": 0 }
              }
            }
          ],
          "textures": [ { "source": 0 } ],
          "images": [
            {
              "uri": "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAQAAAAECAYAAACp8Z5+AAAAJElEQVR4nGN82KT8P/qIPsNSm4sM0Uf0GZiQOUttLjIwElQBAIv9Ga2uq/9wAAAAAElFTkSuQmCC"
            }
          ]
        }
        """;

        var material = GltfMaterialLoader3D.Load(json);

        Assert.That(material.BaseColorFactor.X, Is.EqualTo(0.5f).Within(0.0001f));
        Assert.That(material.BaseColorFactor.Y, Is.EqualTo(0.75f).Within(0.0001f));
        Assert.That(material.BaseColorFactor.Z, Is.EqualTo(1f).Within(0.0001f));
        Assert.That(material.BaseColorFactor.W, Is.EqualTo(0.8f).Within(0.0001f));
        Assert.That(material.BaseColorTexture, Is.Not.Null);
        Assert.That(material.BaseColorTexture!.Width, Is.EqualTo(4));
        Assert.That(material.BaseColorTexture.Height, Is.EqualTo(4));
        Assert.That(material.BaseColorTexture.RgbaPixels, Has.Length.EqualTo(64));
    }

    [Test]
    public void MissingMaterialUsesDefault()
    {
        const string json = """
        {
          "meshes": [
            { "primitives": [ {} ] }
          ]
        }
        """;

        var material = GltfMaterialLoader3D.Load(json);

        Assert.That(material.BaseColorFactor, Is.EqualTo(Vector4.One));
        Assert.That(material.BaseColorTexture, Is.Null);
    }
}
