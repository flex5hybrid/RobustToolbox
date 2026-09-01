using System.Globalization;
using System.Numerics;
using Robust.Shared.IoC;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Validation;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Serialization.TypeSerializers.Interfaces;
using Robust.Shared.Utility;

namespace Robust.Shared.Serialization.TypeSerializers.Implementations;

/// <summary>
/// Serializes a quaternion as X,Y,Z,W. Values are normalized when read so prototype and map data cannot
/// inject a scaling component into a spatial rotation.
/// </summary>
[TypeSerializer]
public sealed class QuaternionSerializer : ITypeSerializer<Quaternion, ValueDataNode>, ITypeCopyCreator<Quaternion>
{
    public Quaternion Read(
        ISerializationManager serializationManager,
        ValueDataNode node,
        IDependencyCollection dependencies,
        SerializationHookContext hookCtx,
        ISerializationContext? context = null,
        ISerializationManager.InstantiationDelegate<Quaternion>? instanceProvider = null)
    {
        if (!VectorSerializerUtility.TryParseArgs(node.Value, 4, out var args))
            throw new InvalidMappingException($"Could not parse {nameof(Quaternion)}: '{node.Value}'");

        var value = new Quaternion(
            float.Parse(args[0], CultureInfo.InvariantCulture),
            float.Parse(args[1], CultureInfo.InvariantCulture),
            float.Parse(args[2], CultureInfo.InvariantCulture),
            float.Parse(args[3], CultureInfo.InvariantCulture));

        return IsFinite(value) && value.LengthSquared() >= 1e-8f
            ? Quaternion.Normalize(value)
            : Quaternion.Identity;
    }

    public ValidationNode Validate(
        ISerializationManager serializationManager,
        ValueDataNode node,
        IDependencyCollection dependencies,
        ISerializationContext? context = null)
    {
        if (!VectorSerializerUtility.TryParseArgs(node.Value, 4, out var args))
            return new ErrorNode(node, "Failed parsing values for Quaternion.");

        if (!float.TryParse(args[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var x) ||
            !float.TryParse(args[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var y) ||
            !float.TryParse(args[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var z) ||
            !float.TryParse(args[3], NumberStyles.Any, CultureInfo.InvariantCulture, out var w))
        {
            return new ErrorNode(node, "Failed parsing values for Quaternion.");
        }

        var value = new Quaternion(x, y, z, w);
        return IsFinite(value) && value.LengthSquared() >= 1e-8f
            ? new ValidatedValueNode(node)
            : new ErrorNode(node, "Quaternion must be finite and non-zero.");
    }

    public DataNode Write(
        ISerializationManager serializationManager,
        Quaternion value,
        IDependencyCollection dependencies,
        bool alwaysWrite = false,
        ISerializationContext? context = null)
    {
        return new ValueDataNode(
            $"{value.X.ToString(CultureInfo.InvariantCulture)}," +
            $"{value.Y.ToString(CultureInfo.InvariantCulture)}," +
            $"{value.Z.ToString(CultureInfo.InvariantCulture)}," +
            value.W.ToString(CultureInfo.InvariantCulture));
    }

    public Quaternion CreateCopy(
        ISerializationManager serializationManager,
        Quaternion source,
        IDependencyCollection dependencies,
        SerializationHookContext hookCtx,
        ISerializationContext? context = null)
    {
        return source;
    }

    private static bool IsFinite(Quaternion value)
    {
        return float.IsFinite(value.X) &&
               float.IsFinite(value.Y) &&
               float.IsFinite(value.Z) &&
               float.IsFinite(value.W);
    }
}
