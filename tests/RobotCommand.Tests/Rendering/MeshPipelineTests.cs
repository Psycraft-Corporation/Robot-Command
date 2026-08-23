using System.Numerics;
using System.Text;
using System.Text.Json;
using RobotCommand.Rendering.Meshes;
using Xunit;

namespace RobotCommand.Tests;

public sealed class MeshPipelineTests
{
    [Fact]
    public async Task ObjLoaderSupportsNegativeIndicesGroupsMaterialsAndGeneratedNormals()
    {
        using var fixture = new TempMeshFixture();
        await File.WriteAllTextAsync(fixture.MtlPath, "newmtl blue\nKd 0.1 0.2 0.8\n");
        await File.WriteAllTextAsync(fixture.ObjPath, "mtllib triangle.mtl\ng panel\nusemtl blue\nv 0 0 0\nv 1 0 0\nv 0 1 0\nf -3 -2 -1\n");

        var result = await MeshAssetLoader.LoadAsync(fixture.ObjPath);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.NotNull(result.Asset);
        var asset = result.Asset!;
        Assert.Equal("OBJ", asset.Format);
        Assert.Equal(3, asset.VertexCount);
        Assert.Equal(3, asset.IndexCount);
        Assert.Equal(1, asset.TriangleCount);
        Assert.Equal("#1A33CC", Assert.Single(asset.Materials).BaseColorHex);
        Assert.All(asset.Primitives.SelectMany(item => item.Vertices), vertex => Assert.True(vertex.HasNormal));
        Assert.Equal(1, Assert.Single(asset.Nodes).PrimitiveIndices.Count);
    }

    [Fact]
    public async Task GltfLoaderReadsEmbeddedBufferAndNodeTransform()
    {
        using var fixture = new TempMeshFixture();
        var binary = new byte[36];
        WriteFloat(binary, 0, 0); WriteFloat(binary, 4, 0); WriteFloat(binary, 8, 0);
        WriteFloat(binary, 12, 1); WriteFloat(binary, 16, 0); WriteFloat(binary, 20, 0);
        WriteFloat(binary, 24, 0); WriteFloat(binary, 28, 1); WriteFloat(binary, 32, 0);
        var uri = "data:application/octet-stream;base64," + Convert.ToBase64String(binary);
        var json = "{\"asset\":{\"version\":\"2.0\"},\"buffers\":[{\"byteLength\":36,\"uri\":\"" + uri + "\"}],\"bufferViews\":[{\"buffer\":0,\"byteOffset\":0,\"byteLength\":36}],\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"count\":3,\"type\":\"VEC3\"}],\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0}}]}],\"nodes\":[{\"name\":\"translated\",\"mesh\":0,\"translation\":[2,3,4]}]}";
        await File.WriteAllTextAsync(fixture.GltfPath, json);

        var result = await MeshAssetLoader.LoadAsync(fixture.GltfPath);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.NotNull(result.Asset);
        var asset = result.Asset!;
        Assert.Equal("glTF", asset.Format);
        Assert.Equal(3, asset.VertexCount);
        Assert.Equal(1, asset.TriangleCount);
        Assert.Equal(2, asset.Bounds.Minimum.X, 3);
        Assert.Equal(3, asset.Bounds.Minimum.Y, 3);
        Assert.Equal(4, asset.Bounds.Minimum.Z, 3);
        Assert.Equal("translated", Assert.Single(asset.Nodes).Name);
    }

    [Fact]
    public async Task GlbLoaderReadsBinaryChunk()
    {
        using var fixture = new TempMeshFixture();
        var binary = new byte[36];
        WriteFloat(binary, 0, 0); WriteFloat(binary, 4, 0); WriteFloat(binary, 8, 0);
        WriteFloat(binary, 12, 1); WriteFloat(binary, 16, 0); WriteFloat(binary, 20, 0);
        WriteFloat(binary, 24, 0); WriteFloat(binary, 28, 1); WriteFloat(binary, 32, 0);
        var json = "{\"asset\":{\"version\":\"2.0\"},\"buffers\":[{\"byteLength\":36}],\"bufferViews\":[{\"buffer\":0,\"byteLength\":36}],\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"count\":3,\"type\":\"VEC3\"}],\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0}}]}]}";
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        var paddedJson = Pad(jsonBytes, 4, 0x20);
        var paddedBinary = Pad(binary, 4, 0);
        using (var stream = File.Create(fixture.GlbPath))
        {
            WriteUInt(stream, 0x46546C67); WriteUInt(stream, 2); WriteUInt(stream, (uint)(12 + 8 + paddedJson.Length + 8 + paddedBinary.Length));
            WriteUInt(stream, (uint)paddedJson.Length); WriteUInt(stream, 0x4E4F534A); stream.Write(paddedJson);
            WriteUInt(stream, (uint)paddedBinary.Length); WriteUInt(stream, 0x004E4942); stream.Write(paddedBinary);
        }

        var result = await MeshAssetLoader.LoadAsync(fixture.GlbPath);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.NotNull(result.Asset);
        Assert.Equal("glTF", result.Asset!.Format);
    }

    [Fact]
    public async Task GltfLoaderResolvesExternalBuffer()
    {
        using var fixture = new TempMeshFixture();
        var binary = new byte[36];
        WriteFloat(binary, 0, 0); WriteFloat(binary, 4, 0); WriteFloat(binary, 8, 0);
        WriteFloat(binary, 12, 1); WriteFloat(binary, 16, 0); WriteFloat(binary, 20, 0);
        WriteFloat(binary, 24, 0); WriteFloat(binary, 28, 1); WriteFloat(binary, 32, 0);
        await File.WriteAllBytesAsync(fixture.BufferPath, binary);
        var json = "{\"asset\":{\"version\":\"2.0\"},\"buffers\":[{\"byteLength\":36,\"uri\":\"triangle.bin\"}],\"bufferViews\":[{\"buffer\":0,\"byteLength\":36}],\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"count\":3,\"type\":\"VEC3\"}],\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0}}]}]}";
        await File.WriteAllTextAsync(fixture.GltfPath, json);

        var result = await MeshAssetLoader.LoadAsync(fixture.GltfPath);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal(3, result.Asset!.VertexCount);
    }

    [Fact]
    public async Task LoaderRejectsResourceLimitAndMalformedInput()
    {
        using var fixture = new TempMeshFixture();
        await File.WriteAllTextAsync(fixture.ObjPath, "v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n");
        var limited = await MeshAssetLoader.LoadAsync(fixture.ObjPath, new MeshLoadOptions(new MeshLoadLimits(MaxVertices: 2)));
        Assert.Contains(limited.Diagnostics, item => item.Code == "VERTEX_LIMIT");

        await File.WriteAllTextAsync(fixture.GltfPath, "{ not valid json");
        var malformed = await MeshAssetLoader.LoadAsync(fixture.GltfPath);
        Assert.Contains(malformed.Diagnostics, item => item.Code == "JSON_INVALID");
    }

    [Fact]
    public async Task LoaderHonorsCancellation()
    {
        using var fixture = new TempMeshFixture();
        await File.WriteAllTextAsync(fixture.ObjPath, "v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MeshAssetLoader.LoadAsync(fixture.ObjPath, cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task SoftwareRenderIsDeterministicAndSupportsWireframe()
    {
        using var fixture = new TempMeshFixture();
        await File.WriteAllTextAsync(fixture.ObjPath, "v -1 -1 0\nv 1 -1 0\nv 0 1 0\nf 1 2 3\n");
        var loaded = await MeshAssetLoader.LoadAsync(fixture.ObjPath);
        Assert.NotNull(loaded.Asset);
        var asset = loaded.Asset!;
        var solidA = MeshSoftwareRenderer.Render(asset, new(96, 72));
        var solidB = MeshSoftwareRenderer.Render(asset, new(96, 72));
        var wire = MeshSoftwareRenderer.Render(asset, new(96, 72, MeshRenderMode.Wireframe));
        Assert.Equal(solidA.Rgb24, solidB.Rgb24);
        Assert.NotEqual(solidA.Rgb24, wire.Rgb24);
        Assert.Contains(solidA.Rgb24, value => value != 0x0B);
    }

    private static void WriteFloat(byte[] bytes, int offset, float value) => BitConverter.GetBytes(value).CopyTo(bytes, offset);
    private static byte[] Pad(byte[] bytes, int alignment, byte value) => bytes.Concat(Enumerable.Repeat(value, (alignment - bytes.Length % alignment) % alignment)).ToArray();
    private static void WriteUInt(Stream stream, uint value) => stream.Write(BitConverter.GetBytes(value));

    private sealed class TempMeshFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "robot-command-mesh-tests", Guid.NewGuid().ToString("N"));
        public TempMeshFixture() { Directory.CreateDirectory(_directory); }
        public string ObjPath => Path.Combine(_directory, "triangle.obj");
        public string MtlPath => Path.Combine(_directory, "triangle.mtl");
        public string GltfPath => Path.Combine(_directory, "triangle.gltf");
        public string GlbPath => Path.Combine(_directory, "triangle.glb");
        public string BufferPath => Path.Combine(_directory, "triangle.bin");
        public void Dispose() { try { Directory.Delete(_directory, true); } catch { } }
    }
}
