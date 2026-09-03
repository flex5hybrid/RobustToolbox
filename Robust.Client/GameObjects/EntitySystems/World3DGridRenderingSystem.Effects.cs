using System;
using System.Collections.Generic;
using System.Numerics;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics3D;

namespace Robust.Client.GameObjects;

internal sealed partial class World3DGridOverlay
{
    private readonly Dictionary<MapId, List<LegacySpriteRender3D>> _legacySpriteCache = new();
    private TimeSpan _legacySpriteCacheTime = TimeSpan.MinValue;
    private Vector2 _legacySpriteCacheEye;

    private void AppendNative3DVisualEffects(MapId mapId, Vector2 eyeWorld, Vector2 billboardRight, Vector2 billboardForward)
    {
        AppendLegacySpriteEntities(mapId, eyeWorld, billboardRight, billboardForward);
        AppendDecals3D(mapId, eyeWorld);
        AppendParticles3D(mapId, eyeWorld, billboardRight, billboardForward);
    }

    /// <summary>
    /// Keeps ordinary SS14 visual entities visible during the 2D-to-3D migration. Build the legacy sprite list
    /// once per rendered frame and bucket it by map so stacked Z-levels do not rescan every SpriteComponent for
    /// every legacy MapId.
    /// </summary>
    private void AppendLegacySpriteEntities(MapId mapId, Vector2 eyeWorld, Vector2 billboardRight, Vector2 billboardForward)
    {
        EnsureLegacySpriteCache(eyeWorld);
        if (!_legacySpriteCache.TryGetValue(mapId, out var sprites))
            return;

        var eyeRotation = new Angle(-_firstPersonYaw);
        foreach (var entry in sprites)
        {
            TryAppendSpriteBillboard(
                entry.Sprite,
                entry.WorldRotation,
                eyeRotation,
                entry.Position,
                billboardRight,
                billboardForward);
        }
    }

    private void EnsureLegacySpriteCache(Vector2 eyeWorld)
    {
        var now = _timing.CurTime;
        if (_legacySpriteCacheTime == now &&
            Vector2.DistanceSquared(_legacySpriteCacheEye, eyeWorld) < 0.0001f)
        {
            return;
        }

        _legacySpriteCacheTime = now;
        _legacySpriteCacheEye = eyeWorld;
        foreach (var list in _legacySpriteCache.Values)
            list.Clear();

        var query = _entityManager.AllEntityQueryEnumerator<TransformComponent, SpriteComponent>();
        while (query.MoveNext(out var uid, out var transform, out var sprite))
        {
            if (transform.MapID == MapId.Nullspace ||
                uid == _localPlayer ||
                !sprite._visible ||
                (sprite._containerOccluded && !sprite.OverrideContainerOcclusion) ||
                _entityManager.HasComponent<MapGridComponent>(uid) ||
                (_entityManager.HasComponent<PhysicsComponent>(uid) &&
                 _entityManager.HasComponent<FixturesComponent>(uid)) ||
                _entityManager.HasComponent<Primitive3DComponent>(uid) ||
                _entityManager.HasComponent<Mesh3DComponent>(uid))
            {
                continue;
            }

            var worldPosition3D = _transform3DSystem.GetWorldPosition3D(uid, transform);
            if (MathF.Abs(worldPosition3D.X - eyeWorld.X) > RenderRadius ||
                MathF.Abs(worldPosition3D.Y - eyeWorld.Y) > RenderRadius)
            {
                continue;
            }

            var (_, worldRotation) = _transformSystem.GetWorldPositionRotation(transform);
            if (!_legacySpriteCache.TryGetValue(transform.MapID, out var sprites))
            {
                sprites = new List<LegacySpriteRender3D>(128);
                _legacySpriteCache.Add(transform.MapID, sprites);
            }

            sprites.Add(new LegacySpriteRender3D(sprite, worldRotation, worldPosition3D));
        }
    }

    private void AppendDecals3D(MapId mapId, Vector2 eyeWorld)
    {
        var query = _entityManager.AllEntityQueryEnumerator<TransformComponent, Transform3DComponent, Decal3DComponent>();
        while (query.MoveNext(out var uid, out var transform, out var transform3D, out var decal))
        {
            if (transform.MapID != mapId || !transform3D.IsAuthoritative || !decal.Visible ||
                decal.Size.X <= 0f || decal.Size.Y <= 0f)
                continue;

            var worldMatrix = _transform3DSystem.GetWorldMatrix3D(uid, transform);
            var center = Vector3.Transform(decal.Offset, worldMatrix);
            if (MathF.Abs(center.X - eyeWorld.X) > RenderRadius || MathF.Abs(center.Y - eyeWorld.Y) > RenderRadius)
                continue;

            var right = Vector3.TransformNormal(new Vector3(decal.Size.X * 0.5f, 0f, 0f), worldMatrix);
            var up = Vector3.TransformNormal(new Vector3(0f, decal.Size.Y * 0.5f, 0f), worldMatrix);
            var texture = TryResolveModelTexture(decal.Texture);
            var color = new Vector4(decal.Color.R, decal.Color.G, decal.Color.B, decal.Color.A);
            var destination = GetEffectBatch(texture, color.W < 0.999f || texture != 0);
            AddEffectQuad(destination, center - right - up, center + right - up, center + right + up, center - right + up, color);
        }
    }

    private void AppendParticles3D(MapId mapId, Vector2 eyeWorld, Vector2 billboardRight2D, Vector2 billboardForward2D)
    {
        var query = _entityManager.AllEntityQueryEnumerator<TransformComponent, Transform3DComponent, ParticleEmitter3DComponent>();
        var time = (float) _timing.CurTime.TotalSeconds;
        while (query.MoveNext(out var uid, out var transform, out var transform3D, out var emitter))
        {
            if (transform.MapID != mapId || !transform3D.IsAuthoritative || !emitter.Enabled ||
                emitter.Rate <= 0f || emitter.Lifetime <= 0f || emitter.MaxParticles <= 0)
                continue;

            var origin = _transform3DSystem.GetWorldPosition3D(uid, transform);
            if (MathF.Abs(origin.X - eyeWorld.X) > RenderRadius || MathF.Abs(origin.Y - eyeWorld.Y) > RenderRadius)
                continue;

            var rotation = _transform3DSystem.GetWorldRotation3D(uid, transform);
            var count = Math.Clamp((int) MathF.Ceiling(emitter.Rate * emitter.Lifetime), 1, emitter.MaxParticles);
            var texture = TryResolveModelTexture(emitter.Texture);
            var destination = GetEffectBatch(texture, true);
            var billboardRight = new Vector3(billboardRight2D, 0f);
            var billboardUp = Vector3.Normalize(Vector3.Cross(new Vector3(billboardForward2D, 0f), billboardRight));

            for (var i = 0; i < count; i++)
            {
                var phase = PositiveFraction(time / emitter.Lifetime + Hash01((uint) i + emitter.Seed));
                var age = phase * emitter.Lifetime;
                var random = new Vector3(
                    HashSigned((uint) i * 3u + emitter.Seed + 11u),
                    HashSigned((uint) i * 3u + emitter.Seed + 23u),
                    HashSigned((uint) i * 3u + emitter.Seed + 47u));
                var velocity = emitter.InitialVelocity + Vector3.Multiply(random, emitter.VelocityRandomness);
                var local = velocity * age + emitter.Acceleration * (0.5f * age * age);
                var center = origin + Vector3.Transform(local, rotation);
                var size = MathF.Max(0f, emitter.StartSize + (emitter.EndSize - emitter.StartSize) * phase) * 0.5f;
                var right = billboardRight * size;
                var up = billboardUp * size;
                var color = LerpColor(emitter.StartColor, emitter.EndColor, phase);
                AddEffectQuad(destination, center - right - up, center + right - up, center + right + up, center - right + up, color);
            }
        }
    }

    private List<float> GetEffectBatch(uint texture, bool transparent)
    {
        var batches = transparent ? _transparentModelVertices : _opaqueModelVertices;
        if (!batches.TryGetValue(texture, out var destination))
        {
            destination = new List<float>(512);
            batches.Add(texture, destination);
        }
        return destination;
    }

    private static void AddEffectQuad(List<float> destination, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector4 color)
    {
        AddVertex(destination, p0, color, new Vector2(0f, 1f));
        AddVertex(destination, p1, color, new Vector2(1f, 1f));
        AddVertex(destination, p2, color, new Vector2(1f, 0f));
        AddVertex(destination, p0, color, new Vector2(0f, 1f));
        AddVertex(destination, p2, color, new Vector2(1f, 0f));
        AddVertex(destination, p3, color, new Vector2(0f, 0f));
    }

    private static Vector4 LerpColor(Color first, Color second, float amount)
    {
        return new Vector4(
            first.R + (second.R - first.R) * amount,
            first.G + (second.G - first.G) * amount,
            first.B + (second.B - first.B) * amount,
            first.A + (second.A - first.A) * amount);
    }

    private static float PositiveFraction(float value) => value - MathF.Floor(value);
    private static float HashSigned(uint value) => Hash01(value) * 2f - 1f;
    private static float Hash01(uint value)
    {
        value ^= value >> 16;
        value *= 0x7FEB352Du;
        value ^= value >> 15;
        value *= 0x846CA68Bu;
        value ^= value >> 16;
        return (value & 0x00FFFFFFu) / 16777215f;
    }

    private readonly record struct LegacySpriteRender3D(
        SpriteComponent Sprite,
        Angle WorldRotation,
        Vector3 Position);
}
