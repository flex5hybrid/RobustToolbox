using System;
using OpenToolkit.Graphics.OpenGL4;
using Robust.Client3D.Assets;

namespace Robust.Client3D.Graphics;

public sealed class GpuMesh3D : IDisposable
{
    private const int FloatsPerVertex = 8;
    private readonly uint _vertexArray;
    private readonly uint _vertexBuffer;
    private readonly uint _indexBuffer;
    private bool _disposed;

    public int IndexCount { get; }

    public unsafe GpuMesh3D(MeshData3D mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);

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

        GL.GenVertexArrays(1, out _vertexArray);
        GL.GenBuffers(1, out _vertexBuffer);
        GL.GenBuffers(1, out _indexBuffer);

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
        IndexCount = mesh.Indices.Length;
    }

    public void Draw()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        GL.BindVertexArray(_vertexArray);
        GL.DrawElements(PrimitiveType.Triangles, IndexCount, DrawElementsType.UnsignedInt, IntPtr.Zero);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        GL.DeleteBuffer(_indexBuffer);
        GL.DeleteBuffer(_vertexBuffer);
        GL.DeleteVertexArray(_vertexArray);
        _disposed = true;
    }
}
