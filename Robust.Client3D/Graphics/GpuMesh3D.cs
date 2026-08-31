using System;
using System.Numerics;
using OpenToolkit.Graphics.OpenGL4;
using Robust.Client3D.Assets;

namespace Robust.Client3D.Graphics;

public sealed class GpuMesh3D : IDisposable
{
    private const int FloatsPerVertex = 8;
    private readonly uint _vertexArray;
    private readonly uint _vertexBuffer;
    private readonly uint _indexBuffer;
    private readonly uint _baseColorTexture;
    private bool _disposed;

    public int IndexCount { get; }
    public Vector4 BaseColorFactor { get; }
    public bool HasBaseColorTexture => _baseColorTexture != 0;

    public unsafe GpuMesh3D(MeshData3D mesh, MaterialData3D? material = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        material ??= MaterialData3D.Default;
        BaseColorFactor = material.BaseColorFactor;

        var packedVertices = new float[mesh.Vertices.Length * FloatsPerVertex];
        for (var i = 0; i < mesh.Vertices.Length; i++)
        {
            var vertex = mesh.Vertices[i];
            var offset = i * FloatsPerVertex;
            packedVertices[offset] = vertex.Position.X;
            packedVertices[offset + 1] = vertex.Position.Y;
            packedVertices[offset + 2] = vertex.Position.Z;
            packedVertices[offset + 3] = vertex.Normal.X;
            packedVertices[offset + 4] = vertex.Normal.Y;
            packedVertices[offset + 5] = vertex.Normal.Z;
            packedVertices[offset + 6] = vertex.TexCoord.X;
            packedVertices[offset + 7] = vertex.TexCoord.Y;
        }

        GL.GenVertexArrays(1, out uint vertexArray);
        GL.GenBuffers(1, out uint vertexBuffer);
        GL.GenBuffers(1, out uint indexBuffer);
        _vertexArray = vertexArray;
        _vertexBuffer = vertexBuffer;
        _indexBuffer = indexBuffer;

        GL.BindVertexArray(_vertexArray);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);
        fixed (float* vertexPointer = packedVertices)
        {
            GL.BufferData(
                BufferTarget.ArrayBuffer,
                packedVertices.Length * sizeof(float),
                (IntPtr) vertexPointer,
                BufferUsageHint.StaticDraw);
        }

        GL.BindBuffer(BufferTarget.ElementArrayBuffer, _indexBuffer);
        fixed (uint* indexPointer = mesh.Indices)
        {
            GL.BufferData(
                BufferTarget.ElementArrayBuffer,
                mesh.Indices.Length * sizeof(uint),
                (IntPtr) indexPointer,
                BufferUsageHint.StaticDraw);
        }

        const int stride = FloatsPerVertex * sizeof(float);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, 6 * sizeof(float));
        GL.EnableVertexAttribArray(2);
        GL.BindVertexArray(0);

        if (material.BaseColorTexture is not null)
            _baseColorTexture = CreateTexture(material.BaseColorTexture);

        IndexCount = mesh.Indices.Length;
    }

    public void BindBaseColorTexture()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(GpuMesh3D));

        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, _baseColorTexture);
    }

    public void Draw()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(GpuMesh3D));

        GL.BindVertexArray(_vertexArray);
        GL.DrawElements(PrimitiveType.Triangles, IndexCount, DrawElementsType.UnsignedInt, IntPtr.Zero);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        if (_baseColorTexture != 0)
            GL.DeleteTexture(_baseColorTexture);
        GL.DeleteBuffer(_indexBuffer);
        GL.DeleteBuffer(_vertexBuffer);
        GL.DeleteVertexArray(_vertexArray);
        _disposed = true;
    }

    private static unsafe uint CreateTexture(TextureImageData3D image)
    {
        GL.GenTextures(1, out uint texture);
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureMinFilter,
            (int) TextureMinFilter.Nearest);
        GL.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureMagFilter,
            (int) TextureMagFilter.Nearest);
        GL.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureWrapS,
            (int) TextureWrapMode.Repeat);
        GL.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureWrapT,
            (int) TextureWrapMode.Repeat);

        fixed (byte* pixelPointer = image.RgbaPixels)
        {
            GL.TexImage2D(
                TextureTarget.Texture2D,
                0,
                PixelInternalFormat.Rgba,
                image.Width,
                image.Height,
                0,
                PixelFormat.Rgba,
                PixelType.UnsignedByte,
                (IntPtr) pixelPointer);
        }

        GL.BindTexture(TextureTarget.Texture2D, 0);
        return texture;
    }
}
