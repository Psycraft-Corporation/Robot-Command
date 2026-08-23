using System.Security.Cryptography;
using System.Text.Json;
using RobotCommand.Core;
using RobotCommand.Rendering.Meshes;

namespace RobotCommand.Services.Simulation;

/// <summary>Owns validation and managed storage for profile visual assets.</summary>
public sealed class GhostProfileAssetWorkflow : IGhostProfileAssetWorkflow
{
    private const long MaximumImageBytes = 16 * 1024 * 1024;
    private readonly GhostProfileWorkflow _profiles;
    private readonly string _assetRoot;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public GhostProfileAssetWorkflow(GhostProfileWorkflow profiles, string? baseDirectory = null)
    {
        _profiles = profiles;
        _assetRoot = Path.Combine(baseDirectory ?? AppContext.BaseDirectory, "data", "ghost-profile-assets");
        Directory.CreateDirectory(_assetRoot);
        _profiles.Changed += OnProfilesChanged;
        CleanupOrphans();
    }

    public event EventHandler? Changed;

    public GhostProfileAssetSnapshot? Find(string profileId) => _profiles.Find(profileId)?.Asset;

    public async Task<GhostProfileAssetSnapshot> ImportAsync(string profileId, string sourcePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) throw new ArgumentException("An asset file is required.", nameof(sourcePath));
        var profile = _profiles.Find(profileId) ?? throw new KeyNotFoundException($"Ghost profile '{profileId}' was not found.");
        if (profile.IsBuiltIn) throw new InvalidOperationException("The built-in Dracula profile is read-only and cannot have a visual asset.");

        var source = Path.GetFullPath(sourcePath);
        if (!File.Exists(source)) throw new FileNotFoundException("The visual asset file was not found.", sourcePath);
        var extension = Path.GetExtension(source).ToLowerInvariant();
        var kind = extension is ".png" or ".jpg" or ".jpeg" ? GhostProfileAssetKind.Image
            : extension is ".gltf" or ".glb" or ".obj" ? GhostProfileAssetKind.Mesh
            : throw new InvalidDataException("Visual assets must be PNG, JPEG, glTF, GLB, or OBJ files.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? staging = null;
        string? final = null;
        string? backup = null;
        try
        {
            staging = Path.Combine(_assetRoot, ".staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            var mainName = Path.GetFileName(source);
            var stagedMain = Path.Combine(staging, mainName);
            File.Copy(source, stagedMain, false);
            if (kind == GhostProfileAssetKind.Mesh) CopyDependencies(source, stagedMain, staging, extension, cancellationToken);

            var metadata = kind == GhostProfileAssetKind.Image
                ? ValidateImage(stagedMain, mainName)
                : await ValidateMeshAsync(stagedMain, mainName, cancellationToken).ConfigureAwait(false);

            var bytes = new FileInfo(stagedMain).Length;
            var hash = await ComputeHashAsync(stagedMain, cancellationToken).ConfigureAwait(false);
            var asset = metadata with
            {
                ByteLength = bytes,
                Sha256 = hash,
                ImportedAt = DateTimeOffset.UtcNow
            };

            final = Path.Combine(_assetRoot, profile.Id);
            backup = Path.Combine(_assetRoot, ".backup-" + Guid.NewGuid().ToString("N"));
            if (Directory.Exists(final)) Directory.Move(final, backup);
            Directory.Move(staging, final);
            staging = null;
            try
            {
                await _profiles.SetAssetAsync(profile.Id, asset, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (Directory.Exists(final)) Directory.Delete(final, true);
                if (Directory.Exists(backup)) Directory.Move(backup, final);
                backup = null;
                throw;
            }
            if (backup is not null && Directory.Exists(backup)) Directory.Delete(backup, true);
            Changed?.Invoke(this, EventArgs.Empty);
            return asset;
        }
        finally
        {
            if (staging is not null && Directory.Exists(staging)) Directory.Delete(staging, true);
            if (backup is not null && Directory.Exists(backup)) Directory.Delete(backup, true);
            _gate.Release();
        }
    }

    public async Task RemoveAsync(string profileId, CancellationToken cancellationToken = default)
    {
        var profile = _profiles.Find(profileId) ?? throw new KeyNotFoundException($"Ghost profile '{profileId}' was not found.");
        if (profile.IsBuiltIn) throw new InvalidOperationException("The built-in Dracula profile is read-only and cannot have a visual asset.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _profiles.ClearAssetAsync(profile.Id, cancellationToken).ConfigureAwait(false);
            var directory = Path.Combine(_assetRoot, profile.Id);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally { _gate.Release(); }
    }

    public Task<GhostProfileAssetReadHandle?> OpenReadAsync(string profileId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var profile = _profiles.Find(profileId);
        if (profile?.Asset is not { } asset) return Task.FromResult<GhostProfileAssetReadHandle?>(null);
        if (string.IsNullOrWhiteSpace(asset.FileName) || asset.FileName != Path.GetFileName(asset.FileName)) return Task.FromResult<GhostProfileAssetReadHandle?>(null);
        var directory = Path.Combine(_assetRoot, profile.Id);
        var path = Path.Combine(directory, asset.FileName);
        return Task.FromResult<GhostProfileAssetReadHandle?>(File.Exists(path)
            ? new GhostProfileAssetReadHandle(asset.FileName, asset.ContentType, path)
            : null);
    }

    private async Task<GhostProfileAssetSnapshot> ValidateMeshAsync(string path, string fileName, CancellationToken cancellationToken)
    {
        var result = await MeshAssetLoader.LoadAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Success || result.Asset is null)
        {
            var message = string.Join(" ", result.Diagnostics.Where(item => item.Severity == MeshDiagnosticSeverity.Error).Select(item => item.Message));
            throw new InvalidDataException(string.IsNullOrWhiteSpace(message) ? "The 3D asset could not be validated." : message);
        }
        var asset = result.Asset;
        return new(GhostProfileAssetKind.Mesh, fileName, ContentType(Path.GetExtension(path)), 0, string.Empty,
            DateTimeOffset.UtcNow, "Valid", result.Diagnostics.Select(item => item.Message).ToArray(),
            VertexCount: asset.VertexCount, TriangleCount: asset.TriangleCount, SubmeshCount: asset.Primitives.Count, Format: asset.Format);
    }

    private static GhostProfileAssetSnapshot ValidateImage(string path, string fileName)
    {
        var length = new FileInfo(path).Length;
        if (length <= 0 || length > MaximumImageBytes) throw new InvalidDataException("The image is empty or exceeds the 16 MB limit.");
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        int width;
        int height;
        if (extension == ".png")
        {
            var signature = reader.ReadBytes(8);
            if (signature.Length != 8 || !signature.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) throw new InvalidDataException("The PNG header is invalid.");
            var chunkLength = ReadBigEndianInt32(reader);
            var type = new string(reader.ReadChars(4));
            if (type != "IHDR" || chunkLength < 8) throw new InvalidDataException("The PNG does not contain a valid image header.");
            width = ReadBigEndianInt32(reader);
            height = ReadBigEndianInt32(reader);
        }
        else
        {
            if (reader.ReadByte() != 0xff || reader.ReadByte() != 0xd8) throw new InvalidDataException("The JPEG header is invalid.");
            (width, height) = ReadJpegSize(reader);
        }
        if (width <= 0 || height <= 0 || width > 32_768 || height > 32_768) throw new InvalidDataException("The image dimensions are invalid or too large.");
        return new(GhostProfileAssetKind.Image, fileName, ContentType(extension), 0, string.Empty, DateTimeOffset.UtcNow,
            Width: width, Height: height, Format: extension.TrimStart('.').ToUpperInvariant());
    }

    private static void CopyDependencies(string source, string stagedMain, string staging, string extension, CancellationToken cancellationToken)
    {
        var sourceDirectory = Path.GetDirectoryName(source)!;
        var references = extension == ".obj" ? ReadObjReferences(source) : ReadGltfReferences(source);
        foreach (var reference in references)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reference.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            var decoded = Uri.UnescapeDataString(reference.Replace('/', Path.DirectorySeparatorChar));
            var full = Path.GetFullPath(Path.Combine(sourceDirectory, decoded));
            var relative = Path.GetRelativePath(sourceDirectory, full);
            if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(full))
                throw new InvalidDataException($"Referenced dependency '{reference}' is missing or outside the source directory.");
            var destination = Path.GetFullPath(Path.Combine(staging, relative));
            if (!destination.StartsWith(Path.GetFullPath(staging) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Referenced dependency '{reference}' is unsafe.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(full, destination, false);
        }
    }

    private static IEnumerable<string> ReadObjReferences(string path)
        => File.ReadLines(path).Select(line => line.Trim()).Where(line => line.StartsWith("mtllib ", StringComparison.OrdinalIgnoreCase)).Select(line => line[7..].Trim()).Where(item => item.Length > 0);

    private static IEnumerable<string> ReadGltfReferences(string path)
    {
        if (Path.GetExtension(path).Equals(".glb", StringComparison.OrdinalIgnoreCase)) return [];
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var references = new List<string>();
        foreach (var element in document.RootElement.GetProperty("buffers").EnumerateArray()) if (element.TryGetProperty("uri", out var uri)) references.Add(uri.GetString() ?? string.Empty);
        if (document.RootElement.TryGetProperty("images", out var images)) foreach (var element in images.EnumerateArray()) if (element.TryGetProperty("uri", out var uri)) references.Add(uri.GetString() ?? string.Empty);
        return references;
    }

    private void OnProfilesChanged(object? sender, EventArgs e) => CleanupOrphans();

    private void CleanupOrphans()
    {
        if (!Directory.Exists(_assetRoot)) return;
        var known = _profiles.Profiles.Select(profile => profile.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in Directory.EnumerateDirectories(_assetRoot))
        {
            var name = Path.GetFileName(directory);
            if (name.StartsWith('.') || name.StartsWith(".staging-", StringComparison.Ordinal) || name.StartsWith(".backup-", StringComparison.Ordinal)) continue;
            if (!known.Contains(name)) try { Directory.Delete(directory, true); } catch { }
        }
    }

    private static async Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ContentType(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gltf" => "model/gltf+json",
        ".glb" => "model/gltf-binary",
        ".obj" => "model/obj",
        _ => "application/octet-stream"
    };

    private static int ReadBigEndianInt32(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(4);
        if (bytes.Length != 4) throw new InvalidDataException("The image header is truncated.");
        return (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];
    }

    private static (int Width, int Height) ReadJpegSize(BinaryReader reader)
    {
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            if (reader.ReadByte() != 0xff) continue;
            var marker = reader.ReadByte();
            while (marker == 0xff) marker = reader.ReadByte();
            if (marker is 0xd8 or 0xd9) continue;
            var length = (reader.ReadByte() << 8) | reader.ReadByte();
            if (length < 2 || reader.BaseStream.Position + length - 2 > reader.BaseStream.Length) break;
            if (marker is >= 0xc0 and <= 0xc3 or >= 0xc5 and <= 0xc7 or >= 0xc9 and <= 0xcb or >= 0xcd and <= 0xcf)
            {
                reader.ReadByte();
                var height = (reader.ReadByte() << 8) | reader.ReadByte();
                var width = (reader.ReadByte() << 8) | reader.ReadByte();
                return (width, height);
            }
            reader.ReadBytes(length - 2);
        }
        throw new InvalidDataException("The JPEG does not contain a valid image frame.");
    }
}
