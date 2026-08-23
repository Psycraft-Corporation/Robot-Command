using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using RobotCommand.Core;

namespace RobotCommand.Rendering.Meshes;

public static class MeshAssetLoader
{
    public static async Task<MeshLoadResult> LoadAsync(string path, MeshLoadOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new MeshLoadOptions();
        var limits = options.EffectiveLimits;
        limits.Validate();
        var diagnostics = new List<MeshDiagnostic>();

        if (string.IsNullOrWhiteSpace(path))
            return Failure("PATH_EMPTY", "A mesh file path is required.");

        try
        {
            var fullPath = Path.GetFullPath(path);
            var info = new FileInfo(fullPath);
            if (!info.Exists) return Failure("FILE_NOT_FOUND", $"Mesh file '{path}' was not found.");
            if (info.Length > limits.MaxFileBytes)
                return Failure("FILE_TOO_LARGE", $"Mesh file is {info.Length:N0} bytes; the limit is {limits.MaxFileBytes:N0}.");

            var extension = info.Extension.ToLowerInvariant();
            return extension switch
            {
                ".obj" => await LoadObjAsync(fullPath, options, cancellationToken).ConfigureAwait(false),
                ".gltf" => await LoadGltfFileAsync(fullPath, options, cancellationToken).ConfigureAwait(false),
                ".glb" => await LoadGlbAsync(fullPath, options, cancellationToken).ConfigureAwait(false),
                _ => Failure("FORMAT_UNSUPPORTED", $"The '{extension}' mesh format is not supported. Use glTF, GLB, or OBJ.")
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (JsonException exception) { return Failure("JSON_INVALID", $"Mesh JSON is invalid: {exception.Message}"); }
        catch (Exception exception) { return Failure("LOAD_FAILED", $"Mesh loading failed: {exception.Message}"); }

        MeshLoadResult Failure(string code, string message)
            => new(null, [new(MeshDiagnosticSeverity.Error, code, message, path)]);
    }

    private static async Task<MeshLoadResult> LoadGltfFileAsync(string path, MeshLoadOptions options, CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        return await BuildGltfAsync(document.RootElement, Path.GetDirectoryName(path)!, null, Path.GetFileName(path), options, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<MeshLoadResult> LoadGlbAsync(string path, MeshLoadOptions options, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        if (bytes.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4)) != 0x46546C67)
            return new(null, [new(MeshDiagnosticSeverity.Error, "GLB_HEADER", "The GLB header is invalid.", path)]);
        var version = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4));
        if (version != 2) return new(null, [new(MeshDiagnosticSeverity.Error, "GLB_VERSION", "Only GLB version 2 is supported.", path)]);

        var offset = 12;
        byte[]? binary = null;
        string? json = null;
        while (offset + 8 <= bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4)));
            var kind = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
            offset += 8;
            if (length < 0 || offset + length > bytes.Length) return new(null, [new(MeshDiagnosticSeverity.Error, "GLB_CHUNK", "A GLB chunk extends beyond the file.", path)]);
            var chunk = bytes.AsSpan(offset, length).ToArray();
            if (kind == 0x4E4F534A) json = Encoding.UTF8.GetString(chunk).TrimEnd('\0', ' ', '\n', '\r', '\t');
            else if (kind == 0x004E4942) binary = chunk;
            offset += length;
        }
        if (json is null) return new(null, [new(MeshDiagnosticSeverity.Error, "GLB_JSON", "The GLB contains no JSON chunk.", path)]);
        using var document = JsonDocument.Parse(json);
        return await BuildGltfAsync(document.RootElement, Path.GetDirectoryName(path)!, binary, Path.GetFileName(path), options, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<MeshLoadResult> BuildGltfAsync(JsonElement root, string directory, byte[]? glbBinary, string sourceName, MeshLoadOptions options, CancellationToken cancellationToken)
    {
        var diagnostics = new List<MeshDiagnostic>();
        var limits = options.EffectiveLimits;
        var buffers = new List<byte[]>();
        if (!root.TryGetProperty("buffers", out var buffersElement) || buffersElement.ValueKind != JsonValueKind.Array)
            return new(null, [new(MeshDiagnosticSeverity.Error, "GLTF_BUFFERS", "The glTF document has no buffers.", sourceName)]);

        var bufferIndex = 0;
        foreach (var buffer in buffersElement.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] data;
            if (bufferIndex == 0 && glbBinary is not null && !buffer.TryGetProperty("uri", out _))
            {
                data = glbBinary;
            }
            else if (buffer.TryGetProperty("uri", out var uriElement))
            {
                var uri = uriElement.GetString() ?? string.Empty;
                if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    var comma = uri.IndexOf(',');
                    if (comma < 0) return new(null, [new(MeshDiagnosticSeverity.Error, "GLTF_URI", "A data URI is malformed.", sourceName)]);
                    try { data = Convert.FromBase64String(uri[(comma + 1)..]); }
                    catch (FormatException) { return new(null, [new(MeshDiagnosticSeverity.Error, "GLTF_BASE64", "A glTF data URI is not valid base64.", sourceName)]); }
                }
                else
                {
                    var external = ResolveExternalFile(directory, uri, diagnostics, sourceName);
                    if (external is null) return new(null, diagnostics);
                    data = await File.ReadAllBytesAsync(external, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                return new(null, [new(MeshDiagnosticSeverity.Error, "GLTF_BUFFER_URI", "A glTF buffer has neither embedded data nor a URI.", sourceName)]);
            }
            buffers.Add(data);
            bufferIndex++;
        }

        var bufferViews = root.TryGetProperty("bufferViews", out var views) && views.ValueKind == JsonValueKind.Array ? views.EnumerateArray().ToArray() : [];
        var accessors = root.TryGetProperty("accessors", out var accessorsElement) && accessorsElement.ValueKind == JsonValueKind.Array ? accessorsElement.EnumerateArray().ToArray() : [];
        var materials = ReadGltfMaterials(root, options, diagnostics, sourceName);
        var rawMeshes = new List<List<RawPrimitive>>();
        var totalVertices = 0;
        var totalIndices = 0;
        var totalTriangles = 0;
        if (root.TryGetProperty("meshes", out var meshesElement) && meshesElement.ValueKind == JsonValueKind.Array)
        {
            var meshNumber = 0;
            foreach (var mesh in meshesElement.EnumerateArray())
            {
                var primitives = new List<RawPrimitive>();
                if (mesh.TryGetProperty("primitives", out var primitiveElement) && primitiveElement.ValueKind == JsonValueKind.Array)
                {
                    var primitiveNumber = 0;
                    foreach (var primitive in primitiveElement.EnumerateArray())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!primitive.TryGetProperty("attributes", out var attributes) || !attributes.TryGetProperty("POSITION", out var positionAccessorElement))
                        {
                            diagnostics.Add(new(MeshDiagnosticSeverity.Error, "GLTF_POSITION", $"Mesh {meshNumber}, primitive {primitiveNumber} has no POSITION attribute.", sourceName));
                            primitiveNumber++;
                            continue;
                        }
                        var positionAccessor = positionAccessorElement.GetInt32();
                        var positions = ReadVector3Accessor(accessors, bufferViews, buffers, positionAccessor, diagnostics, sourceName);
                        if (positions is null) { primitiveNumber++; continue; }
                        totalVertices = checked(totalVertices + positions.Count);
                        if (positions.Count > limits.MaxVertices || totalVertices > limits.MaxVertices)
                        {
                            diagnostics.Add(new(MeshDiagnosticSeverity.Error, "VERTEX_LIMIT", $"Mesh {meshNumber}, primitive {primitiveNumber} exceeds the vertex limit.", sourceName));
                            primitiveNumber++;
                            continue;
                        }
                        var normals = attributes.TryGetProperty("NORMAL", out var normalElement)
                            ? ReadVector3Accessor(accessors, bufferViews, buffers, normalElement.GetInt32(), diagnostics, sourceName)
                            : null;
                        var texCoords = attributes.TryGetProperty("TEXCOORD_0", out var texElement)
                            ? ReadVector2Accessor(accessors, bufferViews, buffers, texElement.GetInt32(), diagnostics, sourceName)
                            : null;
                        var indices = primitive.TryGetProperty("indices", out var indexElement)
                            ? ReadIndexAccessor(accessors, bufferViews, buffers, indexElement.GetInt32(), diagnostics, sourceName)
                            : Enumerable.Range(0, positions.Count).ToArray();
                        if (indices is null) { primitiveNumber++; continue; }
                        var mode = primitive.TryGetProperty("mode", out var modeElement) ? modeElement.GetInt32() : 4;
                        var triangleIndices = ConvertPrimitiveMode(indices, mode, diagnostics, sourceName);
                        if (triangleIndices is null) { primitiveNumber++; continue; }
                        totalIndices = checked(totalIndices + triangleIndices.Length);
                        totalTriangles = checked(totalTriangles + triangleIndices.Length / 3);
                        if (triangleIndices.Length > limits.MaxIndices || totalIndices > limits.MaxIndices)
                        {
                            diagnostics.Add(new(MeshDiagnosticSeverity.Error, "INDEX_LIMIT", $"Mesh {meshNumber}, primitive {primitiveNumber} exceeds the index limit.", sourceName));
                            primitiveNumber++;
                            continue;
                        }
                        if (triangleIndices.Length / 3 > limits.MaxTriangles || totalTriangles > limits.MaxTriangles)
                        {
                            diagnostics.Add(new(MeshDiagnosticSeverity.Error, "TRIANGLE_LIMIT", $"Mesh {meshNumber}, primitive {primitiveNumber} exceeds the triangle limit.", sourceName));
                            primitiveNumber++;
                            continue;
                        }
                        if (triangleIndices.Any(index => index < 0 || index >= positions.Count))
                        {
                            diagnostics.Add(new(MeshDiagnosticSeverity.Error, "GLTF_INDEX_RANGE", $"Mesh {meshNumber}, primitive {primitiveNumber} contains an index outside its vertex range.", sourceName));
                            primitiveNumber++;
                            continue;
                        }
                        var vertices = positions.Select((position, index) => new MeshVertex(
                            position,
                            normals is not null && index < normals.Count ? MeshMath.Normalize(normals[index]) : new(0, 1, 0),
                            texCoords is not null && index < texCoords.Count ? texCoords[index] : MeshVector2.Zero,
                            normals is not null && index < normals.Count)).ToArray();
                        var materialIndex = primitive.TryGetProperty("material", out var materialElement) ? materialElement.GetInt32() : -1;
                        primitives.Add(new RawPrimitive($"mesh:{meshNumber}/primitive:{primitiveNumber}", vertices, triangleIndices, materialIndex));
                        primitiveNumber++;
                    }
                }
                rawMeshes.Add(primitives);
                meshNumber++;
            }
        }

        if (diagnostics.Any(item => item.Severity == MeshDiagnosticSeverity.Error)) return new(null, diagnostics);
        var nodes = new List<MeshNodeSnapshot>();
        var flattened = new List<MeshPrimitiveSnapshot>();
        if (root.TryGetProperty("nodes", out var nodesElement) && nodesElement.ValueKind == JsonValueKind.Array)
        {
            var nodeElements = nodesElement.EnumerateArray().ToArray();
            if (nodeElements.Length > limits.MaxNodes)
                return new(null, [new(MeshDiagnosticSeverity.Error, "NODE_LIMIT", "The glTF node limit was exceeded.", sourceName)]);
            var parents = Enumerable.Repeat<int?>(null, nodeElements.Length).ToArray();
            for (var i = 0; i < nodeElements.Length; i++)
                if (nodeElements[i].TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
                    foreach (var child in children.EnumerateArray()) if (child.GetInt32() >= 0 && child.GetInt32() < parents.Length) parents[child.GetInt32()] = i;
            for (var i = 0; i < nodeElements.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = nodeElements[i].TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? $"Node {i}" : $"Node {i}";
                var local = ReadNodeTransform(nodeElements[i]);
                var world = local;
                if (parents[i] is { } parent) world = local * ResolveWorld(parent, nodeElements, parents);
                var primitiveIndices = new List<int>();
                if (nodeElements[i].TryGetProperty("mesh", out var meshElement) && meshElement.GetInt32() >= 0 && meshElement.GetInt32() < rawMeshes.Count)
                    foreach (var primitive in rawMeshes[meshElement.GetInt32()])
                    {
                        primitiveIndices.Add(flattened.Count);
                        flattened.Add(ToSnapshot(primitive, $"{name}/{primitive.Id}", world, materials, diagnostics, sourceName));
                    }
                nodes.Add(new($"node:{i}", name, parents[i], local, primitiveIndices));
            }
        }
        else
        {
            for (var mesh = 0; mesh < rawMeshes.Count; mesh++)
            {
                var primitiveIndices = new List<int>();
                foreach (var primitive in rawMeshes[mesh])
                {
                    primitiveIndices.Add(flattened.Count);
                    flattened.Add(ToSnapshot(primitive, $"Mesh {mesh}/{primitive.Id}", Matrix4x4.Identity, materials, diagnostics, sourceName));
                }
                nodes.Add(new($"mesh:{mesh}", $"Mesh {mesh}", null, Matrix4x4.Identity, primitiveIndices));
            }
        }

        var bounds = CalculateBounds(flattened);
        if (flattened.Count == 0) diagnostics.Add(new(MeshDiagnosticSeverity.Error, "EMPTY_MESH", "The asset contains no renderable primitives.", sourceName));
        var asset = new MeshAssetSnapshot(sourceName, "glTF", "Robot Command right-handed ENU (X=East, Y=Up, Z=North)", flattened, nodes, materials, bounds, diagnostics);
        return new(asset, diagnostics);
    }

    private static MeshPrimitiveSnapshot ToSnapshot(RawPrimitive primitive, string nodePath, Matrix4x4 transform, List<MeshMaterialSnapshot> materials, List<MeshDiagnostic> diagnostics, string source)
    {
        var vertices = primitive.Vertices.ToArray();
        if (vertices.Any(item => !IsFinite(item.Position) || !IsFinite(item.Normal))) diagnostics.Add(new(MeshDiagnosticSeverity.Error, "NONFINITE_VERTEX", $"Primitive '{primitive.Id}' contains a non-finite vertex.", source));
        if (vertices.Any(item => !item.HasNormal)) vertices = GenerateNormals(vertices, primitive.Indices);
        var material = primitive.MaterialIndex >= 0 && primitive.MaterialIndex < materials.Count ? materials[primitive.MaterialIndex] : null;
        return new(primitive.Id, nodePath, vertices, primitive.Indices, material, transform);
    }

    private static MeshVertex[] GenerateNormals(MeshVertex[] vertices, IReadOnlyList<int> indices)
    {
        var sums = Enumerable.Repeat(new ThreeDVector3(0, 0, 0), vertices.Length).ToArray();
        for (var i = 0; i + 2 < indices.Count; i += 3)
        {
            var a = vertices[indices[i]].Position;
            var b = vertices[indices[i + 1]].Position;
            var c = vertices[indices[i + 2]].Position;
            var normal = MeshMath.Normalize(MeshMath.Cross(MeshMath.Subtract(b, a), MeshMath.Subtract(c, a)));
            sums[indices[i]] = MeshMath.Add(sums[indices[i]], normal);
            sums[indices[i + 1]] = MeshMath.Add(sums[indices[i + 1]], normal);
            sums[indices[i + 2]] = MeshMath.Add(sums[indices[i + 2]], normal);
        }
        return vertices.Select((vertex, index) => vertex.HasNormal ? vertex : vertex with { Normal = MeshMath.Normalize(sums[index]), HasNormal = true }).ToArray();
    }

    private static async Task<MeshLoadResult> LoadObjAsync(string path, MeshLoadOptions options, CancellationToken cancellationToken)
    {
        var diagnostics = new List<MeshDiagnostic>();
        var text = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        var positions = new List<ThreeDVector3>();
        var normals = new List<ThreeDVector3>();
        var texCoords = new List<MeshVector2>();
        var builders = new Dictionary<string, ObjBuilder>(StringComparer.Ordinal);
        var group = "default";
        var materialName = "default";
        var materialFiles = new List<string>();
        var lineNumber = 0;
        foreach (var rawLine in text.Split('\n'))
        {
            cancellationToken.ThrowIfCancellationRequested();
            lineNumber++;
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            try
            {
                switch (parts[0])
                {
                    case "v" when parts.Length >= 4: positions.Add(new(Parse(parts[1]), Parse(parts[2]), Parse(parts[3]))); break;
                    case "vn" when parts.Length >= 4: normals.Add(MeshMath.Normalize(new(Parse(parts[1]), Parse(parts[2]), Parse(parts[3])))); break;
                    case "vt" when parts.Length >= 3: texCoords.Add(new(Parse(parts[1]), Parse(parts[2]))); break;
                    case "g":
                    case "o": group = parts.Length > 1 ? parts[1] : "default"; break;
                    case "usemtl": materialName = parts.Length > 1 ? parts[1] : "default"; break;
                    case "mtllib" when parts.Length > 1: materialFiles.Add(parts[1]); break;
                    case "f" when parts.Length >= 4:
                        var builder = GetBuilder(builders, group, materialName);
                        var face = parts.Skip(1).Select(item => ParseObjIndex(item, positions.Count, texCoords.Count, normals.Count)).ToArray();
                        for (var i = 1; i + 1 < face.Length; i++) { builder.Add(face[0], positions, texCoords, normals); builder.Add(face[i], positions, texCoords, normals); builder.Add(face[i + 1], positions, texCoords, normals); }
                        break;
                }
            }
            catch (Exception exception) when (exception is FormatException or IndexOutOfRangeException or OverflowException)
            {
                diagnostics.Add(new(MeshDiagnosticSeverity.Error, "OBJ_LINE", $"Line {lineNumber} is invalid: {exception.Message}", path, lineNumber));
            }
            if (positions.Count > options.EffectiveLimits.MaxVertices) diagnostics.Add(new(MeshDiagnosticSeverity.Error, "VERTEX_LIMIT", "OBJ vertex limit exceeded.", path, lineNumber));
        }

        var materials = await LoadObjMaterialsAsync(path, materialFiles, options, diagnostics, cancellationToken).ConfigureAwait(false);
        var primitives = new List<MeshPrimitiveSnapshot>();
        var nodes = new List<MeshNodeSnapshot>();
        foreach (var pair in builders)
        {
            var builder = pair.Value;
            if (builder.Indices.Count == 0) continue;
            var vertices = builder.Vertices.ToArray();
            if (options.GenerateMissingNormals) vertices = GenerateNormals(vertices, builder.Indices);
            var material = materials.FirstOrDefault(item => item.Name == builder.MaterialName);
            primitives.Add(new($"obj:{pair.Key}", pair.Key, vertices, builder.Indices, material, Matrix4x4.Identity));
            nodes.Add(new($"obj:{pair.Key}", pair.Key, null, Matrix4x4.Identity, [primitives.Count - 1]));
        }
        var bounds = CalculateBounds(primitives);
        if (primitives.Count == 0) diagnostics.Add(new(MeshDiagnosticSeverity.Error, "EMPTY_MESH", "The OBJ contains no faces.", path));
        var indexCount = primitives.Sum(item => item.Indices.Count);
        var triangleCount = primitives.Sum(item => item.Indices.Count / 3);
        if (indexCount > options.EffectiveLimits.MaxIndices) diagnostics.Add(new(MeshDiagnosticSeverity.Error, "INDEX_LIMIT", "OBJ index limit exceeded.", path));
        if (triangleCount > options.EffectiveLimits.MaxTriangles) diagnostics.Add(new(MeshDiagnosticSeverity.Error, "TRIANGLE_LIMIT", "OBJ triangle limit exceeded.", path));
        if (diagnostics.Any(item => item.Severity == MeshDiagnosticSeverity.Error)) return new(null, diagnostics);
        var asset = new MeshAssetSnapshot(Path.GetFileName(path), "OBJ", "Robot Command right-handed ENU (X=East, Y=Up, Z=North)", primitives, nodes, materials, bounds, diagnostics);
        return new(asset, diagnostics);

        float Parse(string value) => float.Parse(value, CultureInfo.InvariantCulture);
    }

    private static async Task<IReadOnlyList<MeshMaterialSnapshot>> LoadObjMaterialsAsync(string objPath, IReadOnlyList<string> materialFiles, MeshLoadOptions options, List<MeshDiagnostic> diagnostics, CancellationToken cancellationToken)
    {
        var materials = new List<MeshMaterialSnapshot>();
        foreach (var materialFile in materialFiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var full = ResolveExternalFile(Path.GetDirectoryName(objPath)!, materialFile, diagnostics, objPath);
            if (full is null) continue;
            if (!File.Exists(full)) { diagnostics.Add(new(MeshDiagnosticSeverity.Warning, "MTL_MISSING", $"Material file '{materialFile}' was not found.", objPath)); continue; }
            var current = "default";
            var hasCurrentMaterial = false;
            var color = "#B8C7D9";
            var opacity = 1f;
            foreach (var raw in await File.ReadAllLinesAsync(full, cancellationToken).ConfigureAwait(false))
            {
                var parts = raw.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0 || parts[0].StartsWith('#')) continue;
                if (parts[0] == "newmtl")
                {
                    if (hasCurrentMaterial) materials.Add(new(current, color, opacity));
                    current = parts.Length > 1 ? parts[1] : "default";
                    color = "#B8C7D9";
                    opacity = 1;
                    hasCurrentMaterial = true;
                }
                else if (parts[0] == "Kd" && parts.Length >= 4) color = MeshMath.ColorHex(float.Parse(parts[1], CultureInfo.InvariantCulture), float.Parse(parts[2], CultureInfo.InvariantCulture), float.Parse(parts[3], CultureInfo.InvariantCulture));
                else if ((parts[0] == "d" || parts[0] == "Tr") && parts.Length >= 2) opacity = Math.Clamp(float.Parse(parts[1], CultureInfo.InvariantCulture), 0, 1);
            }
            if (hasCurrentMaterial) materials.Add(new(current, color, opacity));
        }
        return materials;
    }

    private static ObjBuilder GetBuilder(Dictionary<string, ObjBuilder> builders, string group, string material)
    {
        var key = $"{group}/{material}";
        if (!builders.TryGetValue(key, out var builder)) builders[key] = builder = new(material);
        return builder;
    }

    private static ObjIndex ParseObjIndex(string value, int vertexCount, int texCount, int normalCount)
    {
        var parts = value.Split('/');
        var v = ResolveIndex(int.Parse(parts[0], CultureInfo.InvariantCulture), vertexCount);
        var vt = parts.Length > 1 && parts[1].Length > 0 ? ResolveIndex(int.Parse(parts[1], CultureInfo.InvariantCulture), texCount) : -1;
        var vn = parts.Length > 2 && parts[2].Length > 0 ? ResolveIndex(int.Parse(parts[2], CultureInfo.InvariantCulture), normalCount) : -1;
        return new(v, vt, vn);
    }

    private static int ResolveIndex(int value, int count)
    {
        var index = value < 0 ? count + value : value - 1;
        if (index < 0 || index >= count) throw new InvalidDataException($"Index {value} is outside a collection of {count} items.");
        return index;
    }

    private static List<MeshMaterialSnapshot> ReadGltfMaterials(JsonElement root, MeshLoadOptions options, List<MeshDiagnostic> diagnostics, string source)
    {
        var result = new List<MeshMaterialSnapshot>();
        if (!options.PreserveMaterials || !root.TryGetProperty("materials", out var materials) || materials.ValueKind != JsonValueKind.Array) return result;
        foreach (var material in materials.EnumerateArray())
        {
            var name = material.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? $"Material {result.Count}" : $"Material {result.Count}";
            var color = "#B8C7D9";
            var opacity = 1f;
            if (material.TryGetProperty("pbrMetallicRoughness", out var pbr) && pbr.TryGetProperty("baseColorFactor", out var factor) && factor.ValueKind == JsonValueKind.Array)
            {
                var values = factor.EnumerateArray().Select(item => item.GetSingle()).ToArray();
                if (values.Length >= 4) { color = MeshMath.ColorHex(values[0], values[1], values[2]); opacity = Math.Clamp(values[3], 0, 1); }
            }
            result.Add(new(name, color, opacity));
        }
        return result;
    }

    private static List<ThreeDVector3>? ReadVector3Accessor(JsonElement[] accessors, JsonElement[] views, IReadOnlyList<byte[]> buffers, int accessorIndex, List<MeshDiagnostic> diagnostics, string source)
    {
        var values = ReadAccessor(accessors, views, buffers, accessorIndex, 3, diagnostics, source, out var count);
        return values is null ? null : Enumerable.Range(0, count).Select(index => new ThreeDVector3(values[index * 3], values[index * 3 + 1], values[index * 3 + 2])).ToList();
    }

    private static List<MeshVector2>? ReadVector2Accessor(JsonElement[] accessors, JsonElement[] views, IReadOnlyList<byte[]> buffers, int accessorIndex, List<MeshDiagnostic> diagnostics, string source)
    {
        var values = ReadAccessor(accessors, views, buffers, accessorIndex, 2, diagnostics, source, out var count);
        return values is null ? null : Enumerable.Range(0, count).Select(index => new MeshVector2(values[index * 2], values[index * 2 + 1])).ToList();
    }

    private static int[]? ReadIndexAccessor(JsonElement[] accessors, JsonElement[] views, IReadOnlyList<byte[]> buffers, int accessorIndex, List<MeshDiagnostic> diagnostics, string source)
    {
        var accessor = GetElement(accessors, accessorIndex, diagnostics, source, "accessor");
        if (accessor is null) return null;
        var component = accessor.Value.TryGetProperty("componentType", out var componentElement) ? componentElement.GetInt32() : 0;
        var values = ReadAccessor(accessors, views, buffers, accessorIndex, 1, diagnostics, source, out var count);
        if (values is null) return null;
        if (component is not (5121 or 5123 or 5125)) { diagnostics.Add(new(MeshDiagnosticSeverity.Error, "GLTF_INDEX_TYPE", "Only unsigned byte, unsigned short, and unsigned int indices are supported.", source)); return null; }
        return values.Select(value => (int)value).ToArray();
    }

    private static float[]? ReadAccessor(JsonElement[] accessors, JsonElement[] views, IReadOnlyList<byte[]> buffers, int accessorIndex, int expectedComponents, List<MeshDiagnostic> diagnostics, string source, out int count)
    {
        count = 0;
        var accessor = GetElement(accessors, accessorIndex, diagnostics, source, "accessor");
        if (accessor is null) return null;
        var type = accessor.Value.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
        var components = type switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, _ => 0 };
        if (components != expectedComponents) { diagnostics.Add(new(MeshDiagnosticSeverity.Error, "GLTF_ACCESSOR_TYPE", $"Accessor must be {expectedComponents} components.", source)); return null; }
        count = accessor.Value.GetProperty("count").GetInt32();
        if (count < 0) { diagnostics.Add(new(MeshDiagnosticSeverity.Error, "GLTF_ACCESSOR_COUNT", "Accessor count is invalid.", source)); return null; }
        if (!accessor.Value.TryGetProperty("bufferView", out var viewElement)) { diagnostics.Add(new(MeshDiagnosticSeverity.Error, "GLTF_ACCESSOR_VIEW", "Sparse or unbound accessors are not supported.", source)); return null; }
        var view = GetElement(views, viewElement.GetInt32(), diagnostics, source, "bufferView");
        if (view is null) return null;
        var bufferIndex = view.Value.GetProperty("buffer").GetInt32();
        if (bufferIndex < 0 || bufferIndex >= buffers.Count) { diagnostics.Add(new(MeshDiagnosticSeverity.Error, "GLTF_BUFFER_INDEX", "Accessor references an invalid buffer.", source)); return null; }
        var componentType = accessor.Value.GetProperty("componentType").GetInt32();
        var componentSize = componentType switch { 5120 or 5121 => 1, 5122 or 5123 => 2, 5125 or 5126 => 4, _ => 0 };
        if (componentSize == 0) { diagnostics.Add(new(MeshDiagnosticSeverity.Error, "GLTF_COMPONENT_TYPE", "The accessor component type is unsupported.", source)); return null; }
        var elementSize = checked(componentSize * components);
        var stride = view.Value.TryGetProperty("byteStride", out var strideElement) ? strideElement.GetInt32() : elementSize;
        var start = (view.Value.TryGetProperty("byteOffset", out var viewOffset) ? viewOffset.GetInt32() : 0) + (accessor.Value.TryGetProperty("byteOffset", out var accessorOffset) ? accessorOffset.GetInt32() : 0);
        var bytes = buffers[bufferIndex];
        if (start < 0 || count > 0 && start + (long)(count - 1) * stride + elementSize > bytes.Length) { diagnostics.Add(new(MeshDiagnosticSeverity.Error, "GLTF_ACCESSOR_RANGE", "Accessor data is outside its buffer.", source)); return null; }
        var output = new float[checked(count * components)];
        for (var i = 0; i < count; i++) for (var component = 0; component < components; component++)
        {
            var offset = checked(start + i * stride + component * componentSize);
            output[i * components + component] = ReadComponent(bytes.AsSpan(offset, componentSize), componentType, accessor.Value.TryGetProperty("normalized", out var normalized) && normalized.GetBoolean());
        }
        return output;
    }

    private static float ReadComponent(ReadOnlySpan<byte> bytes, int componentType, bool normalized)
    {
        return componentType switch
        {
            5120 => normalized ? Math.Max(-1, (sbyte)bytes[0] / 127f) : (sbyte)bytes[0],
            5121 => normalized ? bytes[0] / 255f : bytes[0],
            5122 => normalized ? Math.Max(-1, BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32767f) : BinaryPrimitives.ReadInt16LittleEndian(bytes),
            5123 => normalized ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) / 65535f : BinaryPrimitives.ReadUInt16LittleEndian(bytes),
            5125 => BinaryPrimitives.ReadUInt32LittleEndian(bytes),
            5126 => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes)),
            _ => throw new InvalidDataException("Unsupported glTF component type.")
        };
    }

    private static int[]? ConvertPrimitiveMode(int[] indices, int mode, List<MeshDiagnostic> diagnostics, string source)
    {
        if (mode == 4) return indices.Length % 3 == 0 ? indices : null;
        if (mode == 5)
        {
            var result = new List<int>();
            for (var i = 0; i + 2 < indices.Length; i++) { if ((i & 1) == 0) result.AddRange([indices[i], indices[i + 1], indices[i + 2]]); else result.AddRange([indices[i + 1], indices[i], indices[i + 2]]); }
            return result.ToArray();
        }
        if (mode == 6)
        {
            var result = new List<int>();
            for (var i = 1; i + 1 < indices.Length; i++) result.AddRange([indices[0], indices[i], indices[i + 1]]);
            return result.ToArray();
        }
        diagnostics.Add(new(MeshDiagnosticSeverity.Error, "GLTF_MODE", $"Primitive mode {mode} is unsupported; only triangles, triangle strips, and triangle fans are supported.", source));
        return null;
    }

    private static Matrix4x4 ReadNodeTransform(JsonElement node)
    {
        if (node.TryGetProperty("matrix", out var matrix) && matrix.ValueKind == JsonValueKind.Array)
        {
            var values = matrix.EnumerateArray().Select(item => item.GetSingle()).ToArray();
            if (values.Length == 16) return new(values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7], values[8], values[9], values[10], values[11], values[12], values[13], values[14], values[15]);
        }
        var translation = ReadArray(node, "translation", 3, [0, 0, 0]);
        var scale = ReadArray(node, "scale", 3, [1, 1, 1]);
        var rotation = ReadArray(node, "rotation", 4, [0, 0, 0, 1]);
        var quaternion = Quaternion.Normalize(new(rotation[0], rotation[1], rotation[2], rotation[3]));
        return Matrix4x4.CreateScale(scale[0], scale[1], scale[2]) * Matrix4x4.CreateFromQuaternion(quaternion) * Matrix4x4.CreateTranslation(translation[0], translation[1], translation[2]);
    }

    private static Matrix4x4 ResolveWorld(int index, JsonElement[] nodes, int?[] parents)
    {
        var local = ReadNodeTransform(nodes[index]);
        return parents[index] is { } parent ? local * ResolveWorld(parent, nodes, parents) : local;
    }

    private static float[] ReadArray(JsonElement element, string name, int count, float[] defaults)
    {
        if (!element.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array) return defaults;
        var values = array.EnumerateArray().Select(item => item.GetSingle()).ToArray();
        return values.Length == count ? values : defaults;
    }

    private static JsonElement? GetElement(JsonElement[] elements, int index, List<MeshDiagnostic> diagnostics, string source, string kind)
    {
        if (index < 0 || index >= elements.Length) { diagnostics.Add(new(MeshDiagnosticSeverity.Error, "GLTF_INDEX", $"The glTF {kind} index {index} is invalid.", source)); return null; }
        return elements[index];
    }

    private static string? ResolveExternalFile(string directory, string uri, List<MeshDiagnostic> diagnostics, string source)
    {
        if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return uri;
        var decoded = Uri.UnescapeDataString(uri.Replace('/', Path.DirectorySeparatorChar));
        var root = Path.GetFullPath(directory);
        var full = Path.GetFullPath(Path.Combine(root, decoded));
        var relative = Path.GetRelativePath(root, full);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            diagnostics.Add(new(MeshDiagnosticSeverity.Error, "EXTERNAL_PATH", $"External asset '{uri}' escapes the source directory.", source));
            return null;
        }
        if (!File.Exists(full)) { diagnostics.Add(new(MeshDiagnosticSeverity.Error, "EXTERNAL_MISSING", $"External asset '{uri}' was not found.", source)); return null; }
        return full;
    }

    private static MeshBounds CalculateBounds(IEnumerable<MeshPrimitiveSnapshot> primitives)
    {
        var min = new ThreeDVector3(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
        var max = new ThreeDVector3(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity);
        foreach (var primitive in primitives) foreach (var vertex in primitive.Vertices)
        {
            var point = MeshMath.Transform(vertex.Position, primitive.Transform);
            min = new(Math.Min(min.X, point.X), Math.Min(min.Y, point.Y), Math.Min(min.Z, point.Z));
            max = new(Math.Max(max.X, point.X), Math.Max(max.Y, point.Y), Math.Max(max.Z, point.Z));
        }
        return new(min, max);
    }

    private static bool IsFinite(ThreeDVector3 value) => double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);

    private sealed record RawPrimitive(string Id, IReadOnlyList<MeshVertex> Vertices, IReadOnlyList<int> Indices, int MaterialIndex);

    private readonly record struct ObjIndex(int Vertex, int TexCoord, int Normal);

    private sealed class ObjBuilder
    {
        private readonly Dictionary<ObjIndex, int> _indices = [];
        public ObjBuilder(string materialName) => MaterialName = materialName;
        public string MaterialName { get; }
        public List<MeshVertex> Vertices { get; } = [];
        public List<int> Indices { get; } = [];

        public void Add(ObjIndex key, List<ThreeDVector3> positions, List<MeshVector2> texCoords, List<ThreeDVector3> normals)
        {
            if (!_indices.TryGetValue(key, out var index))
            {
                index = Vertices.Count;
                _indices[key] = index;
                Vertices.Add(new(positions[key.Vertex], key.Normal >= 0 ? normals[key.Normal] : new(0, 1, 0), key.TexCoord >= 0 ? texCoords[key.TexCoord] : MeshVector2.Zero, key.Normal >= 0));
            }
            Indices.Add(index);
        }
    }
}
