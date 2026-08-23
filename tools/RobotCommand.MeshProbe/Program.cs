using System.Text.Json;
using RobotCommand.Rendering.Meshes;
using RobotCommand.Rendering.Veldrid;
using RobotCommand.Core;

if (args.Length == 0)
{
    PrintHelp();
    return 1;
}

try
{
    switch (args[0].ToLowerInvariant())
    {
        case "inspect": return await InspectAsync(args.Skip(1).ToArray());
        case "validate": return await ValidateAsync(args.Skip(1).ToArray());
        case "render": return await RenderAsync(args.Skip(1).ToArray());
        case "compare": return await CompareAsync(args.Skip(1).ToArray());
        case "hardware-probe": return await HardwareProbeAsync();
        case "help":
        case "--help":
        case "-h": PrintHelp(); return 0;
        default: Console.Error.WriteLine($"Unknown command '{args[0]}'."); PrintHelp(); return 2;
    }
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Mesh operation cancelled.");
    return 130;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Mesh probe failed: {exception.Message}");
    return 1;
}

static async Task<int> InspectAsync(string[] args)
{
    var result = await LoadAsync(args);
    PrintResult(result);
    if (result.Asset is not null)
    {
        var asset = result.Asset;
        Console.WriteLine($"Source: {asset.SourceName}");
        Console.WriteLine($"Format: {asset.Format}");
        Console.WriteLine($"Coordinate system: {asset.CoordinateSystem}");
        Console.WriteLine($"Vertices: {asset.VertexCount:N0}");
        Console.WriteLine($"Indices: {asset.IndexCount:N0}");
        Console.WriteLine($"Triangles: {asset.TriangleCount:N0}");
        Console.WriteLine($"Submeshes: {asset.Primitives.Count}");
        Console.WriteLine($"Nodes: {asset.Nodes.Count}");
        Console.WriteLine($"Materials: {asset.Materials.Count}");
        Console.WriteLine($"Bounds: {Format(asset.Bounds.Minimum)} to {Format(asset.Bounds.Maximum)}");
    }
    return result.Success ? 0 : 1;
}

static async Task<int> ValidateAsync(string[] args)
{
    var result = await LoadAsync(args);
    PrintResult(result);
    return result.Success ? 0 : 1;
}

static async Task<int> RenderAsync(string[] args)
{
    if (args.Length == 0) throw new ArgumentException("render requires an input mesh path.");
    var input = args[0];
    var output = Option(args, "--output") ?? Path.ChangeExtension(input, ".ppm");
    var mode = string.Equals(Option(args, "--mode"), "wireframe", StringComparison.OrdinalIgnoreCase) ? MeshRenderMode.Wireframe : MeshRenderMode.Solid;
    var result = await MeshAssetLoader.LoadAsync(input);
    PrintResult(result);
    if (!result.Success || result.Asset is null) return 1;
    MeshSoftwareRenderer.Render(result.Asset, new(640, 480, mode)).SavePpm(output);
    Console.WriteLine($"Wrote deterministic PPM preview: {Path.GetFullPath(output)}");
    return 0;
}

static async Task<int> CompareAsync(string[] args)
{
    if (args.Length < 2) throw new ArgumentException("compare requires two input mesh paths.");
    var left = await MeshAssetLoader.LoadAsync(args[0]);
    var right = await MeshAssetLoader.LoadAsync(args[1]);
    PrintResult(left);
    PrintResult(right);
    if (left.Asset is null || right.Asset is null) return 1;
    var leftSummary = JsonSerializer.Serialize(new { left.Asset.Format, left.Asset.VertexCount, left.Asset.IndexCount, left.Asset.TriangleCount, left.Asset.Bounds });
    var rightSummary = JsonSerializer.Serialize(new { right.Asset.Format, right.Asset.VertexCount, right.Asset.IndexCount, right.Asset.TriangleCount, right.Asset.Bounds });
    Console.WriteLine($"Equivalent normalized summaries: {string.Equals(leftSummary, rightSummary, StringComparison.Ordinal)}");
    Console.WriteLine($"A: {leftSummary}");
    Console.WriteLine($"B: {rightSummary}");
    return left.Success && right.Success ? 0 : 1;
}

static async Task<int> HardwareProbeAsync()
{
    await using var renderer = new VeldridRenderer();
    await renderer.InitializeAsync(ThreeDRenderBackendPolicy.Auto);
    Console.WriteLine(JsonSerializer.Serialize(renderer.Status, new JsonSerializerOptions { WriteIndented = true }));
    return renderer.Status.IsInitialized && renderer.Status.IsHardwareAccelerated ? 0 : 1;
}

static async Task<MeshLoadResult> LoadAsync(string[] args)
{
    if (args.Length == 0) throw new ArgumentException("An input mesh path is required.");
    return await MeshAssetLoader.LoadAsync(args[0]);
}

static void PrintResult(MeshLoadResult result)
{
    foreach (var diagnostic in result.Diagnostics)
        Console.WriteLine($"{diagnostic.Severity}: {diagnostic.Code}: {diagnostic.Message}");
}

static string? Option(string[] args, string name)
{
    var index = Array.FindIndex(args, item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static string Format(ThreeDVector3 value) => $"({value.X:0.###}, {value.Y:0.###}, {value.Z:0.###})";

static void PrintHelp()
{
    Console.WriteLine("Robot Command mesh probe (test-only)");
    Console.WriteLine("  inspect <file>                         Print normalized mesh metadata");
    Console.WriteLine("  validate <file>                        Validate without rendering");
    Console.WriteLine("  render <file> --output <file.ppm>     Render a deterministic CPU preview");
    Console.WriteLine("         [--mode solid|wireframe]");
    Console.WriteLine("  compare <file-a> <file-b>              Compare normalized summaries");
    Console.WriteLine("  hardware-probe                         Explicitly probe a Veldrid device");
}
