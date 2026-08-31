using System.Numerics;
using System.Text.Json;
using Robust.Shared.Maths;

namespace Robust.Shared3D;

public sealed class WorldDefinition3D
{
    public const int CurrentVersion = 1;

    public string Name { get; }
    public IReadOnlyList<Vector3> SpawnPoints { get; }
    public IReadOnlyList<WorldObjectDefinition3D> Objects { get; }
    public IReadOnlyList<Box3> CollisionBounds { get; }

    internal WorldDefinition3D(
        string name,
        IReadOnlyList<Vector3> spawnPoints,
        IReadOnlyList<WorldObjectDefinition3D> objects)
    {
        Name = name;
        SpawnPoints = spawnPoints;
        Objects = objects;
        CollisionBounds = objects
            .Where(static definition => definition.WorldCollider is not null)
            .Select(static definition => definition.WorldCollider!.Value)
            .ToArray();
    }

    public Vector3 GetPlayerSpawnPosition(int playerId)
    {
        if (playerId <= 0)
            throw new ArgumentOutOfRangeException(nameof(playerId));
        if (SpawnPoints.Count == 0)
            throw new InvalidOperationException("3D world has no spawn points.");

        return SpawnPoints[(playerId - 1) % SpawnPoints.Count];
    }
}

public sealed record WorldObjectDefinition3D(
    string Id,
    string? ModelPath,
    SpatialTransform Transform,
    Box3? LocalCollider,
    Box3? WorldCollider);

public static class WorldDefinition3DLoader
{
    public static WorldDefinition3D Load(ReadOnlySpan<byte> jsonUtf8)
    {
        var dto = JsonSerializer.Deserialize<WorldFileDto>(jsonUtf8)
                  ?? throw new InvalidOperationException("3D world definition is empty.");

        if (dto.Version != WorldDefinition3D.CurrentVersion)
        {
            throw new NotSupportedException(
                $"Unsupported 3D world version {dto.Version}; expected {WorldDefinition3D.CurrentVersion}.");
        }

        var name = string.IsNullOrWhiteSpace(dto.Name) ? "Unnamed 3D world" : dto.Name.Trim();
        if (dto.Spawns is null || dto.Spawns.Length == 0)
            throw new InvalidOperationException("3D world must define at least one spawn point.");

        var spawnPoints = dto.Spawns
            .Select((value, index) => ReadVector3(value, $"spawns[{index}]"))
            .ToArray();

        var definitions = dto.Objects ?? Array.Empty<WorldObjectDto>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var objects = new WorldObjectDefinition3D[definitions.Length];

        for (var i = 0; i < definitions.Length; i++)
        {
            var definition = definitions[i];
            if (string.IsNullOrWhiteSpace(definition.Id))
                throw new InvalidOperationException($"objects[{i}] has no id.");

            var id = definition.Id.Trim();
            if (!ids.Add(id))
                throw new InvalidOperationException($"Duplicate 3D world object id '{id}'.");

            var position = definition.Position is null
                ? Vector3.Zero
                : ReadVector3(definition.Position, $"objects[{i}].position");
            var rotationDegrees = definition.RotationDegrees is null
                ? Vector3.Zero
                : ReadVector3(definition.RotationDegrees, $"objects[{i}].rotationDegrees");
            var scale = definition.Scale is null
                ? Vector3.One
                : ReadVector3(definition.Scale, $"objects[{i}].scale");

            if (scale.X <= 0f || scale.Y <= 0f || scale.Z <= 0f)
                throw new InvalidOperationException($"objects[{i}].scale must be positive on every axis.");

            var rotation = CreateRotation(rotationDegrees);
            var transform = new SpatialTransform(position, rotation, scale);

            Box3? localCollider = null;
            Box3? worldCollider = null;
            if (definition.Collider is not null)
            {
                var center = definition.Collider.Center is null
                    ? Vector3.Zero
                    : ReadVector3(definition.Collider.Center, $"objects[{i}].collider.center");
                var size = ReadVector3(
                    definition.Collider.Size,
                    $"objects[{i}].collider.size");
                if (size.X <= 0f || size.Y <= 0f || size.Z <= 0f)
                {
                    throw new InvalidOperationException(
                        $"objects[{i}].collider.size must be positive on every axis.");
                }

                localCollider = Box3.CenteredAround(center, size);
                worldCollider = localCollider.Value.TransformedBounds(transform.Matrix);
            }

            var modelPath = string.IsNullOrWhiteSpace(definition.Model)
                ? null
                : definition.Model.Replace('\\', '/').Trim();

            objects[i] = new WorldObjectDefinition3D(
                id,
                modelPath,
                transform,
                localCollider,
                worldCollider);
        }

        return new WorldDefinition3D(name, spawnPoints, objects);
    }

    public static WorldDefinition3D Load(string json)
    {
        return Load(System.Text.Encoding.UTF8.GetBytes(json));
    }

    private static Vector3 ReadVector3(float[]? values, string field)
    {
        if (values is null || values.Length != 3)
            throw new InvalidOperationException($"{field} must contain exactly three numbers.");

        var vector = new Vector3(values[0], values[1], values[2]);
        if (!float.IsFinite(vector.X) || !float.IsFinite(vector.Y) || !float.IsFinite(vector.Z))
            throw new InvalidOperationException($"{field} contains a non-finite number.");
        return vector;
    }

    private static Quaternion CreateRotation(Vector3 degrees)
    {
        const float radiansPerDegree = MathF.PI / 180f;
        var x = Quaternion.CreateFromAxisAngle(Vector3.UnitX, degrees.X * radiansPerDegree);
        var y = Quaternion.CreateFromAxisAngle(Vector3.UnitY, degrees.Y * radiansPerDegree);
        var z = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, degrees.Z * radiansPerDegree);
        return Quaternion.Normalize(x * y * z);
    }

    private sealed class WorldFileDto
    {
        public int Version { get; set; }
        public string? Name { get; set; }
        public float[][]? Spawns { get; set; }
        public WorldObjectDto[]? Objects { get; set; }
    }

    private sealed class WorldObjectDto
    {
        public string? Id { get; set; }
        public string? Model { get; set; }
        public float[]? Position { get; set; }
        public float[]? RotationDegrees { get; set; }
        public float[]? Scale { get; set; }
        public ColliderDto? Collider { get; set; }
    }

    private sealed class ColliderDto
    {
        public float[]? Center { get; set; }
        public float[]? Size { get; set; }
    }
}
