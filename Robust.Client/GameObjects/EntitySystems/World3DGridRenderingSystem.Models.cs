using System;
using System.Collections.Generic;
using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Utility;

namespace Robust.Client.GameObjects;

internal sealed partial class World3DGridOverlay
{
    private readonly Dictionary<uint, List<float>> _opaqueModelVertices = new();
    private readonly Dictionary<uint, List<float>> _transparentModelVertices = new();
    private readonly HashSet<EntityUid> _renderedMeshEntities = new();

    private readonly record struct ModelBatch(uint TextureHandle, float[] OpaqueVertices, float[] TransparentVertices);

    private void ClearModelBatches()
    {
        _opaqueModelVertices.Clear();
        _transparentModelVertices.Clear();
        _renderedMeshEntities.Clear();
    }

    private ModelBatch[] SnapshotModelBatches()
    {
        if (_opaqueModelVertices.Count == 0 && _transparentModelVertices.Count == 0)
            return Array.Empty<ModelBatch>();

        var handles = new HashSet<uint>(_opaqueModelVertices.Keys);
        handles.UnionWith(_transparentModelVertices.Keys);
        var batches = new ModelBatch[handles.Count];
        var index = 0;
        foreach (var handle in handles)
        {
            batches[index++] = new ModelBatch(
                handle,
                _opaqueModelVertices.TryGetValue(handle, out var opaque) ? opaque.ToArray() : Array.Empty<float>(),
                _transparentModelVertices.TryGetValue(handle, out var transparent) ? transparent.ToArray() : Array.Empty<float>());
        }

        return batches;
    }

    private void DrawOpaqueModelBatches(ModelBatch[] batches)
    {
        foreach (var batch in batches)
            DrawVertexData(batch.OpaqueVertices, batch.TextureHandle != 0, batch.TextureHandle);
    }

    private void DrawTransparentModelBatches(ModelBatch[] batches)
    {
        foreach (var batch in batches)
            DrawVertexData(batch.TransparentVertices, batch.TextureHandle != 0, batch.TextureHandle);
    }

    private void AppendNative3DModels(
        MapId mapId,
        Vector2 eyeWorld,
        ref int staticEntityCount,
        ref int movingEntityCount)
    {
        var query = _entityManager.AllEntityQueryEnumerator<TransformComponent, Transform3DComponent, Mesh3DComponent>();
        while (query.MoveNext(out var uid, out var transform, out var transform3D, out var mesh))
        {
            if (transform.MapID != mapId ||
                !transform3D.IsAuthoritative ||
                !mesh.Visible ||
                uid == _localPlayer ||
                string.IsNullOrWhiteSpace(mesh.Mesh))
                continue;

            var position = _transform3DSystem.GetWorldPosition3D(uid, transform);
            if (MathF.Abs(position.X - eyeWorld.X) > RenderRadius ||
                MathF.Abs(position.Y - eyeWorld.Y) > RenderRadius ||
                !TryAppendObjMesh(uid, transform, mesh))
                continue;

            _renderedMeshEntities.Add(uid);
            if (_entityManager.TryGetComponent(uid, out PhysicsBody3DComponent? body) &&
                body.BodyType != PhysicsBodyType3D.Static)
                movingEntityCount++;
            else
                staticEntityCount++;
        }
    }

    private bool TryAppendObjMesh(EntityUid uid, TransformComponent transform, Mesh3DComponent component)
    {
        if (!component.Mesh.EndsWith(".obj", StringComparison.OrdinalIgnoreCase))
            return false;

        ObjMeshResource resource;
        try
        {
            if (!_resourceCache.TryGetResource(new ResPath(component.Mesh), out resource))
                return false;
        }
        catch
        {
            return false;
        }

        var sourceVertices = resource.Vertices;
        if (sourceVertices.Length == 0)
            return false;

        var textureHandle = TryResolveModelTexture(component.AlbedoTexture);
        var alpha = Math.Clamp(component.Tint.A, 0f, 1f);
        var batches = alpha < 0.999f ? _transparentModelVertices : _opaqueModelVertices;
        if (!batches.TryGetValue(textureHandle, out var destination))
        {
            destination = new List<float>(sourceVertices.Length * FloatsPerVertex);
            batches.Add(textureHandle, destination);
        }

        var worldMatrix = _transform3DSystem.GetWorldMatrix3D(uid, transform);
        var worldRotation = _transform3DSystem.GetWorldRotation3D(uid, transform);
        var albedo = new Vector3(component.Tint.R, component.Tint.G, component.Tint.B);
        var emissive = new Vector3(component.Emissive.R, component.Emissive.G, component.Emissive.B) * component.Emissive.A;

        for (var i = 0; i + 2 < sourceVertices.Length; i += 3)
        {
            var a = sourceVertices[i];
            var b = sourceVertices[i + 1];
            var c = sourceVertices[i + 2];
            var worldA = Vector3.Transform(a.Position, worldMatrix);
            var worldB = Vector3.Transform(b.Position, worldMatrix);
            var worldC = Vector3.Transform(c.Position, worldMatrix);
            var localNormal = a.Normal ?? Vector3.Cross(b.Position - a.Position, c.Position - a.Position);
            var normal = Vector3.Transform(localNormal, worldRotation);
            normal = normal.LengthSquared() > 1e-8f ? Vector3.Normalize(normal) : Vector3.UnitZ;
            var illumination = component.ReceiveLights
                ? ShadeSurface3D(uid, transform.MapID, (worldA + worldB + worldC) / 3f, normal)
                : Vector3.One;
            var lit = Vector3.Min(Vector3.Multiply(albedo, illumination) + emissive, Vector3.One);
            var color = new Vector4(lit, alpha);

            AddVertex(destination, worldA, color, a.Uv);
            AddVertex(destination, worldB, color, b.Uv);
            AddVertex(destination, worldC, color, c.Uv);

            if (component.DoubleSided)
            {
                AddVertex(destination, worldC, color, c.Uv);
                AddVertex(destination, worldB, color, b.Uv);
                AddVertex(destination, worldA, color, a.Uv);
            }
        }

        return true;
    }

    private uint TryResolveModelTexture(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return 0;

        try
        {
            if (!_resourceCache.TryGetResource<TextureResource>(new ResPath(path), out var resource))
                return 0;
            return _spriteTextureHandles.TryGetValue(resource.Texture, out var handle) ? handle : 0;
        }
        catch
        {
            return 0;
        }
    }
}
