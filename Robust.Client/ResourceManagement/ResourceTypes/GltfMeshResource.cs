using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Threading;
using Robust.Shared.ContentPack;
using Robust.Shared.IoC;
using Robust.Shared.Utility;

namespace Robust.Client.ResourceManagement;

/// <summary>
/// Native glTF 2.0 mesh decoder. It supports JSON glTF and binary GLB containers, external or embedded buffers,
/// indexed triangle primitives, node hierarchies, and the core metallic/roughness material properties.
/// </summary>
public sealed class GltfMeshResource : BaseResource, IBaseResource
{
    private MeshSurface3D[] _surfaces = Array.Empty<MeshSurface3D>();
    private List<MeshSurface3D>[] _meshes = Array.Empty<List<MeshSurface3D>>();
    private Node[] _nodes = Array.Empty<Node>();
    private int[] _sceneRoots = Array.Empty<int>();
    private Dictionary<string, AnimationClip> _animations = new(StringComparer.Ordinal);

    public IReadOnlyList<MeshSurface3D> Surfaces => _surfaces;
    public IReadOnlyCollection<string> AnimationNames => _animations.Keys;
    public override ResPath? Fallback => null;
    static bool IBaseResource.CanBeRemoved => true;

    public override void Load(IDependencyCollection dependencies, ResPath path)
    {
        var resources = dependencies.Resolve<IResourceManager>();
        using var stream = resources.ContentFileRead(path);
        LoadDocument(resources, path, stream);
    }

    public override void Reload(IDependencyCollection dependencies, ResPath path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Load(dependencies, path);
    }

    private void LoadDocument(IResourceManager resources, ResPath path, Stream stream)
    {
        byte[] json;
        byte[]? binaryChunk = null;
        if (path.Extension.Equals("glb", StringComparison.OrdinalIgnoreCase))
            ReadGlb(stream, out json, out binaryChunk);
        else
        {
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            json = memory.ToArray();
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("asset", out var asset) ||
            !asset.TryGetProperty("version", out var version) ||
            !version.GetString()!.StartsWith("2", StringComparison.Ordinal))
            throw new InvalidDataException("Only glTF 2.x assets are supported.");

        var buffers = ReadBuffers(resources, path.Directory, root, binaryChunk);
        var views = ReadBufferViews(root);
        var accessors = ReadAccessors(root);
        var materials = ReadMaterials(root, path.Directory);
        _meshes = ReadMeshes(root, buffers, views, accessors, materials);
        _nodes = ReadNodes(root);
        _sceneRoots = _nodes.Length > 0 ? ReadSceneRoots(root, _nodes.Length) : Array.Empty<int>();
        _animations = ReadAnimations(root, buffers, views, accessors);
        var output = BuildSurfaces(null, 0f, true);

        if (output.Count == 0)
            throw new InvalidDataException("glTF resource contains no triangle mesh primitives.");
        _surfaces = output.ToArray();
    }

    /// <summary>
    /// Samples a named rigid-node animation and returns model-space surfaces for the current frame.
    /// Unknown or empty clip names return the bind pose.
    /// </summary>
    public IReadOnlyList<MeshSurface3D> Sample(string clip, float time, bool loop)
    {
        if (string.IsNullOrWhiteSpace(clip) || !_animations.TryGetValue(clip, out var animation))
            return _surfaces;
        return BuildSurfaces(animation, time, loop);
    }

    private List<MeshSurface3D> BuildSurfaces(AnimationClip? animation, float time, bool loop)
    {
        var output = new List<MeshSurface3D>();
        if (_nodes.Length == 0)
        {
            foreach (var mesh in _meshes)
                AppendTransformed(mesh, Matrix4x4.Identity, output);
            return output;
        }

        var poses = new NodePose[_nodes.Length];
        for (var i = 0; i < poses.Length; i++)
            poses[i] = new NodePose(_nodes[i].Translation, _nodes[i].Rotation, _nodes[i].Scale, _nodes[i].Matrix);
        if (animation is not null)
            animation.Sample(poses, time, loop);

        foreach (var node in _sceneRoots)
            AppendNode(node, Matrix4x4.Identity, poses, output, new HashSet<int>());
        return output;
    }

    private static void ReadGlb(Stream stream, out byte[] json, out byte[]? binary)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, true);
        if (reader.ReadUInt32() != 0x46546C67)
            throw new InvalidDataException("GLB header has an invalid magic value.");
        if (reader.ReadUInt32() != 2)
            throw new InvalidDataException("Only GLB version 2 is supported.");
        var totalLength = reader.ReadUInt32();
        if (totalLength < 20 || totalLength > stream.Length)
            throw new InvalidDataException("GLB header has an invalid length.");

        json = Array.Empty<byte>();
        binary = null;
        while (stream.Position + 8 <= totalLength)
        {
            var length = reader.ReadUInt32();
            var type = reader.ReadUInt32();
            if (length > totalLength - stream.Position)
                throw new InvalidDataException("GLB chunk extends past the declared file length.");
            var data = reader.ReadBytes(checked((int) length));
            if (type == 0x4E4F534A)
                json = data;
            else if (type == 0x004E4942)
                binary = data;
        }

        if (json.Length == 0)
            throw new InvalidDataException("GLB does not contain a JSON chunk.");
    }

    private static byte[][] ReadBuffers(IResourceManager resources, ResPath directory, JsonElement root, byte[]? binary)
    {
        if (!root.TryGetProperty("buffers", out var elements))
            return Array.Empty<byte[]>();

        var buffers = new byte[elements.GetArrayLength()][];
        for (var i = 0; i < buffers.Length; i++)
        {
            var element = elements[i];
            if (!element.TryGetProperty("uri", out var uriElement))
            {
                buffers[i] = binary ?? throw new InvalidDataException("glTF buffer has no URI and the container has no BIN chunk.");
                continue;
            }

            var uri = uriElement.GetString() ?? throw new InvalidDataException("glTF buffer URI is null.");
            if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = uri.IndexOf(',');
                if (comma < 0 || !uri[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Only base64 data URIs are supported for glTF buffers.");
                buffers[i] = Convert.FromBase64String(uri[(comma + 1)..]);
            }
            else
            {
                using var source = resources.ContentFileRead(directory / Uri.UnescapeDataString(uri));
                using var memory = new MemoryStream();
                source.CopyTo(memory);
                buffers[i] = memory.ToArray();
            }

            if (element.TryGetProperty("byteLength", out var byteLength) && buffers[i].Length < byteLength.GetInt32())
                throw new InvalidDataException($"glTF buffer {i} is shorter than its declared byteLength.");
        }

        return buffers;
    }

    private static BufferView[] ReadBufferViews(JsonElement root)
    {
        if (!root.TryGetProperty("bufferViews", out var elements))
            return Array.Empty<BufferView>();
        var result = new BufferView[elements.GetArrayLength()];
        for (var i = 0; i < result.Length; i++)
        {
            var element = elements[i];
            result[i] = new BufferView(
                element.GetProperty("buffer").GetInt32(),
                GetInt(element, "byteOffset"),
                element.GetProperty("byteLength").GetInt32(),
                GetInt(element, "byteStride"));
        }
        return result;
    }

    private static Accessor[] ReadAccessors(JsonElement root)
    {
        if (!root.TryGetProperty("accessors", out var elements))
            return Array.Empty<Accessor>();
        var result = new Accessor[elements.GetArrayLength()];
        for (var i = 0; i < result.Length; i++)
        {
            var element = elements[i];
            if (element.TryGetProperty("sparse", out _))
                throw new InvalidDataException("Sparse glTF accessors are not supported yet.");
            result[i] = new Accessor(
                element.GetProperty("bufferView").GetInt32(),
                GetInt(element, "byteOffset"),
                element.GetProperty("componentType").GetInt32(),
                element.GetProperty("count").GetInt32(),
                element.GetProperty("type").GetString() ?? "SCALAR",
                element.TryGetProperty("normalized", out var normalized) && normalized.GetBoolean());
        }
        return result;
    }

    private static Material[] ReadMaterials(JsonElement root, ResPath directory)
    {
        if (!root.TryGetProperty("materials", out var elements))
            return Array.Empty<Material>();
        var imagePaths = ReadImagePaths(root, directory);
        var textureSources = ReadTextureSources(root);
        var result = new Material[elements.GetArrayLength()];
        for (var i = 0; i < result.Length; i++)
        {
            var element = elements[i];
            var color = Vector4.One;
            var roughness = 1f;
            var metallic = 1f;
            string? texture = null;
            if (element.TryGetProperty("pbrMetallicRoughness", out var pbr))
            {
                color = GetVector4(pbr, "baseColorFactor", Vector4.One);
                roughness = GetFloat(pbr, "roughnessFactor", 1f);
                metallic = GetFloat(pbr, "metallicFactor", 1f);
                if (pbr.TryGetProperty("baseColorTexture", out var textureInfo))
                {
                    var textureIndex = textureInfo.GetProperty("index").GetInt32();
                    if ((uint) textureIndex < (uint) textureSources.Length)
                    {
                        var source = textureSources[textureIndex];
                        if ((uint) source < (uint) imagePaths.Length)
                            texture = imagePaths[source];
                    }
                }
            }

            var emissive = GetVector3(element, "emissiveFactor", Vector3.Zero);
            var doubleSided = element.TryGetProperty("doubleSided", out var doubleSidedElement) && doubleSidedElement.GetBoolean();
            var blend = element.TryGetProperty("alphaMode", out var alphaMode) && alphaMode.GetString() == "BLEND";
            result[i] = new Material(texture, color, emissive, roughness, metallic, doubleSided, blend);
        }
        return result;
    }

    private static string?[] ReadImagePaths(JsonElement root, ResPath directory)
    {
        if (!root.TryGetProperty("images", out var images))
            return Array.Empty<string?>();
        var result = new string?[images.GetArrayLength()];
        for (var i = 0; i < result.Length; i++)
        {
            if (!images[i].TryGetProperty("uri", out var uri))
                continue;
            var value = uri.GetString();
            if (!string.IsNullOrWhiteSpace(value) && !value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                result[i] = (directory / Uri.UnescapeDataString(value)).ToString();
        }
        return result;
    }

    private static int[] ReadTextureSources(JsonElement root)
    {
        if (!root.TryGetProperty("textures", out var textures))
            return Array.Empty<int>();
        var result = new int[textures.GetArrayLength()];
        for (var i = 0; i < result.Length; i++)
            result[i] = textures[i].TryGetProperty("source", out var source) ? source.GetInt32() : -1;
        return result;
    }

    private static List<MeshSurface3D>[] ReadMeshes(
        JsonElement root,
        byte[][] buffers,
        BufferView[] views,
        Accessor[] accessors,
        Material[] materials)
    {
        if (!root.TryGetProperty("meshes", out var meshElements))
            return Array.Empty<List<MeshSurface3D>>();
        var result = new List<MeshSurface3D>[meshElements.GetArrayLength()];
        for (var meshIndex = 0; meshIndex < result.Length; meshIndex++)
        {
            var surfaces = new List<MeshSurface3D>();
            foreach (var primitive in meshElements[meshIndex].GetProperty("primitives").EnumerateArray())
            {
                if (GetInt(primitive, "mode", 4) != 4)
                    continue;
                var attributes = primitive.GetProperty("attributes");
                var positions = ReadVector3Accessor(attributes.GetProperty("POSITION").GetInt32(), buffers, views, accessors);
                var normals = attributes.TryGetProperty("NORMAL", out var normalAccessor)
                    ? ReadVector3Accessor(normalAccessor.GetInt32(), buffers, views, accessors)
                    : null;
                var uvs = attributes.TryGetProperty("TEXCOORD_0", out var uvAccessor)
                    ? ReadVector2Accessor(uvAccessor.GetInt32(), buffers, views, accessors)
                    : null;
                var indices = primitive.TryGetProperty("indices", out var indexAccessor)
                    ? ReadIndexAccessor(indexAccessor.GetInt32(), buffers, views, accessors)
                    : SequentialIndices(positions.Length);
                var vertices = ExpandTriangles(positions, normals, uvs, indices);
                var materialIndex = GetInt(primitive, "material", -1);
                var material = (uint) materialIndex < (uint) materials.Length ? materials[materialIndex] : Material.Default;
                surfaces.Add(new MeshSurface3D(
                    vertices,
                    material.AlbedoTexture,
                    material.BaseColor,
                    material.Emissive,
                    material.Roughness,
                    material.Metallic,
                    material.DoubleSided,
                    material.Blend));
            }
            result[meshIndex] = surfaces;
        }
        return result;
    }

    private static Dictionary<string, AnimationClip> ReadAnimations(
        JsonElement root,
        byte[][] buffers,
        BufferView[] views,
        Accessor[] accessors)
    {
        var result = new Dictionary<string, AnimationClip>(StringComparer.Ordinal);
        if (!root.TryGetProperty("animations", out var animations))
            return result;

        var animationIndex = 0;
        foreach (var animation in animations.EnumerateArray())
        {
            var name = animation.TryGetProperty("name", out var nameElement) && !string.IsNullOrWhiteSpace(nameElement.GetString())
                ? nameElement.GetString()!
                : $"animation_{animationIndex}";
            var tracks = new List<AnimationTrack>();
            var samplers = animation.GetProperty("samplers");
            foreach (var channel in animation.GetProperty("channels").EnumerateArray())
            {
                var target = channel.GetProperty("target");
                var node = target.GetProperty("node").GetInt32();
                var path = target.GetProperty("path").GetString();
                if (path is not ("translation" or "rotation" or "scale"))
                    continue;
                var samplerIndex = channel.GetProperty("sampler").GetInt32();
                if ((uint) samplerIndex >= (uint) samplers.GetArrayLength())
                    throw new InvalidDataException("glTF animation channel references an invalid sampler.");
                var sampler = samplers[samplerIndex];
                var interpolation = sampler.TryGetProperty("interpolation", out var interpolationElement)
                    ? interpolationElement.GetString() ?? "LINEAR"
                    : "LINEAR";
                if (interpolation == "CUBICSPLINE")
                    continue;
                var times = ReadFloatAccessor(sampler.GetProperty("input").GetInt32(), buffers, views, accessors);
                var outputIndex = sampler.GetProperty("output").GetInt32();
                if (path == "rotation")
                {
                    var values = ReadVector4Accessor(outputIndex, buffers, views, accessors);
                    tracks.Add(AnimationTrack.Rotation(node, times, values, interpolation == "STEP"));
                }
                else
                {
                    var values = ReadVector3Accessor(outputIndex, buffers, views, accessors);
                    tracks.Add(AnimationTrack.Vector(node, path == "translation" ? TrackPath.Translation : TrackPath.Scale, times, values, interpolation == "STEP"));
                }
            }
            result[name] = new AnimationClip(tracks.ToArray());
            animationIndex++;
        }
        return result;
    }

    private void AppendNode(
        int nodeIndex,
        Matrix4x4 parent,
        NodePose[] poses,
        List<MeshSurface3D> output,
        HashSet<int> ancestry)
    {
        if ((uint) nodeIndex >= (uint) poses.Length || !ancestry.Add(nodeIndex))
            return;
        var world = poses[nodeIndex].Matrix * parent;
        var node = _nodes[nodeIndex];
        if ((uint) node.Mesh < (uint) _meshes.Length)
            AppendTransformed(_meshes[node.Mesh], world, output);
        foreach (var child in node.Children)
            AppendNode(child, world, poses, output, ancestry);
        ancestry.Remove(nodeIndex);
    }

    private static void AppendTransformed(IEnumerable<MeshSurface3D> surfaces, Matrix4x4 transform, List<MeshSurface3D> output)
    {
        Matrix4x4.Invert(transform, out var inverse);
        var normalMatrix = Matrix4x4.Transpose(inverse);
        foreach (var surface in surfaces)
        {
            var vertices = new MeshVertex3D[surface.Vertices.Length];
            for (var i = 0; i < vertices.Length; i++)
            {
                var source = surface.Vertices[i];
                var position = ConvertCoordinates(Vector3.Transform(source.Position, transform));
                var normal = source.Normal is { } sourceNormal
                    ? ConvertCoordinates(Vector3.TransformNormal(sourceNormal, normalMatrix))
                    : null;
                if (normal is { } value && value.LengthSquared() > 1e-8f)
                    normal = Vector3.Normalize(value);
                vertices[i] = new MeshVertex3D(position, normal, source.Uv);
            }
            output.Add(surface with { Vertices = vertices });
        }
    }

    private static Node[] ReadNodes(JsonElement root)
    {
        if (!root.TryGetProperty("nodes", out var elements))
            return Array.Empty<Node>();
        var result = new Node[elements.GetArrayLength()];
        for (var nodeIndex = 0; nodeIndex < result.Length; nodeIndex++)
        {
            var element = elements[nodeIndex];
            var matrix = ReadNodeMatrix(element);
            var translation = GetVector3(element, "translation", Vector3.Zero);
            var scale = GetVector3(element, "scale", Vector3.One);
            var rotationValue = GetVector4(element, "rotation", new Vector4(0f, 0f, 0f, 1f));
            var rotation = Quaternion.Normalize(new Quaternion(rotationValue.X, rotationValue.Y, rotationValue.Z, rotationValue.W));
            var mesh = GetInt(element, "mesh", -1);
            var children = Array.Empty<int>();
            if (element.TryGetProperty("children", out var childElements))
            {
                children = new int[childElements.GetArrayLength()];
                var childIndex = 0;
                foreach (var child in childElements.EnumerateArray())
                    children[childIndex++] = child.GetInt32();
            }
            result[nodeIndex] = new Node(mesh, children, translation, rotation, scale, matrix);
        }
        return result;
    }

    private static Matrix4x4? ReadNodeMatrix(JsonElement node)
    {
        if (node.TryGetProperty("matrix", out var matrix))
        {
            var m = new float[16];
            var i = 0;
            foreach (var value in matrix.EnumerateArray())
                m[i++] = value.GetSingle();
            if (i != 16)
                throw new InvalidDataException("glTF node matrix must contain 16 elements.");
            return new Matrix4x4(
                m[0], m[1], m[2], m[3],
                m[4], m[5], m[6], m[7],
                m[8], m[9], m[10], m[11],
                m[12], m[13], m[14], m[15]);
        }

        return null;
    }

    private static int[] ReadSceneRoots(JsonElement root, int nodeCount)
    {
        if (root.TryGetProperty("scenes", out var scenes) && scenes.GetArrayLength() > 0)
        {
            var sceneIndex = GetInt(root, "scene");
            if ((uint) sceneIndex < (uint) scenes.GetArrayLength() && scenes[sceneIndex].TryGetProperty("nodes", out var roots))
            {
                var result = new int[roots.GetArrayLength()];
                var index = 0;
                foreach (var node in roots.EnumerateArray())
                    result[index++] = node.GetInt32();
                return result;
            }
        }

        var children = new HashSet<int>();
        foreach (var node in root.GetProperty("nodes").EnumerateArray())
        {
            if (!node.TryGetProperty("children", out var childElements))
                continue;
            foreach (var child in childElements.EnumerateArray())
                children.Add(child.GetInt32());
        }

        var fallback = new List<int>();
        for (var i = 0; i < nodeCount; i++)
        {
            if (!children.Contains(i))
                fallback.Add(i);
        }
        return fallback.ToArray();
    }

    private static MeshVertex3D[] ExpandTriangles(Vector3[] positions, Vector3[]? normals, Vector2[]? uvs, int[] indices)
    {
        var count = indices.Length - indices.Length % 3;
        var vertices = new MeshVertex3D[count];
        for (var triangle = 0; triangle < count; triangle += 3)
        {
            var ia = ValidateIndex(indices[triangle], positions.Length);
            var ib = ValidateIndex(indices[triangle + 1], positions.Length);
            var ic = ValidateIndex(indices[triangle + 2], positions.Length);
            var faceNormal = Vector3.Cross(positions[ib] - positions[ia], positions[ic] - positions[ia]);
            faceNormal = faceNormal.LengthSquared() > 1e-8f ? Vector3.Normalize(faceNormal) : Vector3.UnitY;
            vertices[triangle] = MakeVertex(ia, positions, normals, uvs, faceNormal);
            vertices[triangle + 1] = MakeVertex(ib, positions, normals, uvs, faceNormal);
            vertices[triangle + 2] = MakeVertex(ic, positions, normals, uvs, faceNormal);
        }
        return vertices;
    }

    private static MeshVertex3D MakeVertex(int index, Vector3[] positions, Vector3[]? normals, Vector2[]? uvs, Vector3 faceNormal)
    {
        var normal = normals is not null && index < normals.Length ? normals[index] : faceNormal;
        var uv = uvs is not null && index < uvs.Length ? new Vector2(uvs[index].X, 1f - uvs[index].Y) : Vector2.Zero;
        return new MeshVertex3D(positions[index], normal, uv);
    }

    private static float[] ReadFloatAccessor(int index, byte[][] buffers, BufferView[] views, Accessor[] accessors)
    {
        var accessor = GetAccessor(index, "SCALAR", accessors);
        var result = new float[accessor.Count];
        for (var i = 0; i < result.Length; i++)
            result[i] = ReadComponent(accessor, i, 0, buffers, views);
        return result;
    }

    private static Vector4[] ReadVector4Accessor(int index, byte[][] buffers, BufferView[] views, Accessor[] accessors)
    {
        var accessor = GetAccessor(index, "VEC4", accessors);
        var result = new Vector4[accessor.Count];
        for (var i = 0; i < result.Length; i++)
            result[i] = new Vector4(
                ReadComponent(accessor, i, 0, buffers, views),
                ReadComponent(accessor, i, 1, buffers, views),
                ReadComponent(accessor, i, 2, buffers, views),
                ReadComponent(accessor, i, 3, buffers, views));
        return result;
    }

    private static Vector3[] ReadVector3Accessor(int index, byte[][] buffers, BufferView[] views, Accessor[] accessors)
    {
        var accessor = GetAccessor(index, "VEC3", accessors);
        var result = new Vector3[accessor.Count];
        for (var i = 0; i < result.Length; i++)
            result[i] = new Vector3(
                ReadComponent(accessor, i, 0, buffers, views),
                ReadComponent(accessor, i, 1, buffers, views),
                ReadComponent(accessor, i, 2, buffers, views));
        return result;
    }

    private static Vector2[] ReadVector2Accessor(int index, byte[][] buffers, BufferView[] views, Accessor[] accessors)
    {
        var accessor = GetAccessor(index, "VEC2", accessors);
        var result = new Vector2[accessor.Count];
        for (var i = 0; i < result.Length; i++)
            result[i] = new Vector2(
                ReadComponent(accessor, i, 0, buffers, views),
                ReadComponent(accessor, i, 1, buffers, views));
        return result;
    }

    private static int[] ReadIndexAccessor(int index, byte[][] buffers, BufferView[] views, Accessor[] accessors)
    {
        var accessor = GetAccessor(index, "SCALAR", accessors);
        var result = new int[accessor.Count];
        for (var i = 0; i < result.Length; i++)
            result[i] = checked((int) ReadUnsignedComponent(accessor, i, buffers, views));
        return result;
    }

    private static float ReadComponent(Accessor accessor, int element, int component, byte[][] buffers, BufferView[] views)
    {
        var bytes = GetElement(accessor, element, component, buffers, views);
        return accessor.ComponentType switch
        {
            5120 => accessor.Normalized ? Math.Max((sbyte) bytes[0] / 127f, -1f) : (sbyte) bytes[0],
            5121 => accessor.Normalized ? bytes[0] / 255f : bytes[0],
            5122 => accessor.Normalized ? Math.Max(BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32767f, -1f) : BinaryPrimitives.ReadInt16LittleEndian(bytes),
            5123 => accessor.Normalized ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) / 65535f : BinaryPrimitives.ReadUInt16LittleEndian(bytes),
            5125 => BinaryPrimitives.ReadUInt32LittleEndian(bytes),
            5126 => BinaryPrimitives.ReadSingleLittleEndian(bytes),
            _ => throw new InvalidDataException($"Unsupported glTF component type {accessor.ComponentType}.")
        };
    }

    private static uint ReadUnsignedComponent(Accessor accessor, int element, byte[][] buffers, BufferView[] views)
    {
        var bytes = GetElement(accessor, element, 0, buffers, views);
        return accessor.ComponentType switch
        {
            5121 => bytes[0],
            5123 => BinaryPrimitives.ReadUInt16LittleEndian(bytes),
            5125 => BinaryPrimitives.ReadUInt32LittleEndian(bytes),
            _ => throw new InvalidDataException($"Unsupported glTF index component type {accessor.ComponentType}.")
        };
    }

    private static ReadOnlySpan<byte> GetElement(Accessor accessor, int element, int component, byte[][] buffers, BufferView[] views)
    {
        if ((uint) accessor.BufferView >= (uint) views.Length)
            throw new InvalidDataException("glTF accessor references an invalid bufferView.");
        var view = views[accessor.BufferView];
        if ((uint) view.Buffer >= (uint) buffers.Length)
            throw new InvalidDataException("glTF bufferView references an invalid buffer.");
        var componentSize = ComponentSize(accessor.ComponentType);
        var componentCount = ComponentCount(accessor.Type);
        var stride = view.ByteStride > 0 ? view.ByteStride : componentSize * componentCount;
        var offset = checked(view.ByteOffset + accessor.ByteOffset + element * stride + component * componentSize);
        var buffer = buffers[view.Buffer];
        if (offset < 0 || offset + componentSize > buffer.Length || offset + componentSize > view.ByteOffset + view.ByteLength)
            throw new InvalidDataException("glTF accessor reads outside its bufferView.");
        return buffer.AsSpan(offset, componentSize);
    }

    private static Accessor GetAccessor(int index, string type, Accessor[] accessors)
    {
        if ((uint) index >= (uint) accessors.Length)
            throw new InvalidDataException("glTF primitive references an invalid accessor.");
        var accessor = accessors[index];
        if (accessor.Type != type)
            throw new InvalidDataException($"Expected a {type} glTF accessor, got {accessor.Type}.");
        return accessor;
    }

    private static int ComponentSize(int type) => type switch
    {
        5120 or 5121 => 1,
        5122 or 5123 => 2,
        5125 or 5126 => 4,
        _ => throw new InvalidDataException($"Unsupported glTF component type {type}.")
    };

    private static int ComponentCount(string type) => type switch
    {
        "SCALAR" => 1,
        "VEC2" => 2,
        "VEC3" => 3,
        "VEC4" => 4,
        _ => throw new InvalidDataException($"Unsupported glTF accessor type {type}.")
    };

    private static int[] SequentialIndices(int count)
    {
        var result = new int[count];
        for (var i = 0; i < count; i++)
            result[i] = i;
        return result;
    }

    private static int ValidateIndex(int index, int count)
    {
        if ((uint) index >= (uint) count)
            throw new InvalidDataException($"glTF vertex index {index} is outside {count} positions.");
        return index;
    }

    private static Vector3 ConvertCoordinates(Vector3 value) => new(value.X, -value.Z, value.Y);
    private static int GetInt(JsonElement element, string name, int fallback = 0) =>
        element.TryGetProperty(name, out var property) ? property.GetInt32() : fallback;
    private static float GetFloat(JsonElement element, string name, float fallback) =>
        element.TryGetProperty(name, out var property) ? property.GetSingle() : fallback;

    private static Vector3 GetVector3(JsonElement element, string name, Vector3 fallback)
    {
        if (!element.TryGetProperty(name, out var property) || property.GetArrayLength() < 3)
            return fallback;
        return new Vector3(property[0].GetSingle(), property[1].GetSingle(), property[2].GetSingle());
    }

    private static Vector4 GetVector4(JsonElement element, string name, Vector4 fallback)
    {
        if (!element.TryGetProperty(name, out var property) || property.GetArrayLength() < 4)
            return fallback;
        return new Vector4(property[0].GetSingle(), property[1].GetSingle(), property[2].GetSingle(), property[3].GetSingle());
    }

    private readonly record struct BufferView(int Buffer, int ByteOffset, int ByteLength, int ByteStride);
    private readonly record struct Accessor(int BufferView, int ByteOffset, int ComponentType, int Count, string Type, bool Normalized);
    private readonly record struct Material(
        string? AlbedoTexture,
        Vector4 BaseColor,
        Vector3 Emissive,
        float Roughness,
        float Metallic,
        bool DoubleSided,
        bool Blend)
    {
        public static readonly Material Default = new(null, Vector4.One, Vector3.Zero, 1f, 1f, false, false);
    }

    private readonly record struct Node(
        int Mesh,
        int[] Children,
        Vector3 Translation,
        Quaternion Rotation,
        Vector3 Scale,
        Matrix4x4? Matrix);

    private struct NodePose
    {
        public Vector3 Translation;
        public Quaternion Rotation;
        public Vector3 Scale;
        public Matrix4x4? MatrixOverride;

        public readonly Matrix4x4 Matrix => MatrixOverride ??
            Matrix4x4.CreateScale(Scale) *
            Matrix4x4.CreateFromQuaternion(Rotation) *
            Matrix4x4.CreateTranslation(Translation);

        public NodePose(Vector3 translation, Quaternion rotation, Vector3 scale, Matrix4x4? matrix)
        {
            Translation = translation;
            Rotation = rotation;
            Scale = scale;
            MatrixOverride = matrix;
        }
    }

    private enum TrackPath : byte
    {
        Translation,
        Rotation,
        Scale,
    }

    private sealed class AnimationClip
    {
        private readonly AnimationTrack[] _tracks;
        private readonly float _duration;

        public AnimationClip(AnimationTrack[] tracks)
        {
            _tracks = tracks;
            foreach (var track in tracks)
                _duration = MathF.Max(_duration, track.Duration);
        }

        public void Sample(NodePose[] poses, float time, bool loop)
        {
            if (_duration > 0f)
            {
                time = loop
                    ? time - MathF.Floor(time / _duration) * _duration
                    : Math.Clamp(time, 0f, _duration);
            }
            foreach (var track in _tracks)
                track.Sample(poses, time);
        }
    }

    private sealed class AnimationTrack
    {
        private readonly int _node;
        private readonly TrackPath _path;
        private readonly float[] _times;
        private readonly Vector4[] _values;
        private readonly bool _step;

        public float Duration => _times.Length > 0 ? _times[^1] : 0f;

        private AnimationTrack(int node, TrackPath path, float[] times, Vector4[] values, bool step)
        {
            _node = node;
            _path = path;
            _times = times;
            _values = values;
            _step = step;
        }

        public static AnimationTrack Vector(int node, TrackPath path, float[] times, Vector3[] values, bool step)
        {
            var converted = new Vector4[values.Length];
            for (var i = 0; i < values.Length; i++)
                converted[i] = new Vector4(values[i], 0f);
            return new AnimationTrack(node, path, times, converted, step);
        }

        public static AnimationTrack Rotation(int node, float[] times, Vector4[] values, bool step)
        {
            return new AnimationTrack(node, TrackPath.Rotation, times, values, step);
        }

        public void Sample(NodePose[] poses, float time)
        {
            if ((uint) _node >= (uint) poses.Length || _times.Length == 0 || _values.Length != _times.Length)
                return;
            var next = 0;
            while (next < _times.Length && _times[next] < time)
                next++;
            if (next >= _times.Length)
                next = _times.Length - 1;
            var previous = Math.Max(0, next - 1);
            var duration = _times[next] - _times[previous];
            var amount = _step || duration <= 1e-8f ? 0f : Math.Clamp((time - _times[previous]) / duration, 0f, 1f);
            var pose = poses[_node];
            pose.MatrixOverride = null;

            if (_path == TrackPath.Rotation)
            {
                var first = Quaternion.Normalize(new Quaternion(_values[previous].X, _values[previous].Y, _values[previous].Z, _values[previous].W));
                var second = Quaternion.Normalize(new Quaternion(_values[next].X, _values[next].Y, _values[next].Z, _values[next].W));
                pose.Rotation = Quaternion.Slerp(first, second, amount);
            }
            else
            {
                var value = Vector4.Lerp(_values[previous], _values[next], amount);
                var vector = new Vector3(value.X, value.Y, value.Z);
                if (_path == TrackPath.Translation)
                    pose.Translation = vector;
                else
                    pose.Scale = vector;
            }
            poses[_node] = pose;
        }
    }
}
