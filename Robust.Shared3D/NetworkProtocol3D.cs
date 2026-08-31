using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Robust.Shared3D;

public static class NetworkProtocol3D
{
    public const string Hello = "hello";
    public const string Welcome = "welcome";
    public const string Input = "input";
    public const string Snapshot = "snapshot";

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        IncludeFields = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    public static string? ReadType(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("type", out var type)
            ? type.GetString()
            : null;
    }

    public static T Deserialize<T>(string json)
    {
        return JsonSerializer.Deserialize<T>(json, JsonOptions)
               ?? throw new InvalidDataException($"Failed to deserialize {typeof(T).Name}.");
    }
}

public sealed record ClientHello3D(string Type, string Name)
{
    public static ClientHello3D Create(string name) => new(NetworkProtocol3D.Hello, name);
}

public sealed record ServerWelcome3D(string Type, int PlayerId, long ServerTick, PlayerSnapshot3D Player)
{
    public static ServerWelcome3D Create(int playerId, long serverTick, PlayerSnapshot3D player) =>
        new(NetworkProtocol3D.Welcome, playerId, serverTick, player);
}

public sealed record InputCommand3D(
    string Type,
    int Sequence,
    float MoveX,
    float MoveY,
    float Yaw,
    bool Jump)
{
    public static InputCommand3D Create(int sequence, float moveX, float moveY, float yaw, bool jump) =>
        new(NetworkProtocol3D.Input, sequence, moveX, moveY, yaw, jump);

    public CharacterInput3D ToCharacterInput(bool jumpOverride) => new(MoveX, MoveY, Yaw, jumpOverride);
}

public sealed record PlayerSnapshot3D(
    int PlayerId,
    string Name,
    Vector3 Position,
    Vector3 Velocity,
    float Yaw,
    bool Grounded);

public sealed record WorldSnapshot3D(
    string Type,
    long ServerTick,
    int AcknowledgedSequence,
    PlayerSnapshot3D[] Players)
{
    public static WorldSnapshot3D Create(long serverTick, int acknowledgedSequence, PlayerSnapshot3D[] players) =>
        new(NetworkProtocol3D.Snapshot, serverTick, acknowledgedSequence, players);
}
