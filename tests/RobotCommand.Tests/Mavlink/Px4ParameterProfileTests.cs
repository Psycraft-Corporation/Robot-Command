using Microsoft.Extensions.Logging.Abstractions;
using RobotCommand.Services.Mavlink;
using Xunit;

namespace RobotCommand.Tests.Mavlink;

public sealed class Px4ParameterProfileTests
{
    private static readonly char[] Fields = new[] { 'A', 'R', 'M', '\0' };

    [Fact]
    public void MavlinkPacket_TextDecodesCharacterArrays()
    {
        var packet = new MavlinkPacket(2, 0, 1, 1, 22,
            new Dictionary<string, object> { ["param_id"] = Fields },
            DateTimeOffset.UtcNow);

        Assert.Equal("ARM", packet.Text("param_id"));
    }

    [Fact]
    public void QgcFile_ParsesCommentsAndCanonicalRoundTrips()
    {
        var codec = new Px4ParameterFileCodec();
        var document = codec.Parse("# saved by QGC\r\n1 1 MPC_XY_VEL_MAX 12.5 9\r\n1 1 MPC_Z_VEL_MAX_UP 3 9\r\n");

        Assert.Equal(2, document.Parameters.Count);
        Assert.Equal("MPC_XY_VEL_MAX", document.Parameters[0].Name);
        Assert.Equal((byte)9, document.Parameters[0].Type);
        Assert.Contains("# saved by QGC", codec.Serialize(document));
        Assert.Equal(document.Parameters, codec.Parse(codec.Serialize(document)).Parameters);
    }

    [Fact]
    public void QgcFile_RejectsMalformedRowsAndDuplicates()
    {
        var codec = new Px4ParameterFileCodec();
        Assert.Throws<FormatException>(() => codec.Parse("1 1 NAME 1"));
        Assert.Throws<FormatException>(() => codec.Parse("1 1 NAME not-a-number 9"));
        Assert.Throws<FormatException>(() => codec.Parse("1 1 NAME 1 9\n1 1 NAME 2 9"));
    }

    [Fact]
    public void ArduPilotParamFile_ParsesCompactRowsIntoSharedDocument()
    {
        var codec = new Px4ParameterFileCodec();
        var document = codec.Parse("# ArduPilot export\nARMING_CHECK,1\nWPNAV_SPEED,500\n");

        Assert.Equal(["ARMING_CHECK", "WPNAV_SPEED"], document.Parameters.Select(item => item.Name).ToArray());
        Assert.All(document.Parameters, item =>
        {
            Assert.Equal((byte)1, item.VehicleId);
            Assert.Equal((byte)1, item.ComponentId);
            Assert.Equal((byte)9, item.Type);
        });
    }

    [Fact]
    public async Task ProfileStore_ImportsAndExportsManagedLibrary()
    {
        var root = Path.Combine(Path.GetTempPath(), "robot-command-px4-params-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var codec = new Px4ParameterFileCodec();
            var store = new Px4ParameterProfileStore(root, codec, NullLogger<Px4ParameterProfileStore>.Instance);
            var source = Path.Combine(root, "incoming.params");
            await File.WriteAllTextAsync(source, "# test\n1 1 COM_ARMABLE 1 9\n");

            var imported = await store.ImportAsync(source);
            Assert.Single(store.Profiles);
            Assert.Equal("COM_ARMABLE", imported.Document.Parameters[0].Name);
            Assert.Equal("incoming.params", imported.SourceFileName);

            var reloadedStore = new Px4ParameterProfileStore(root, codec, NullLogger<Px4ParameterProfileStore>.Instance);
            await reloadedStore.RefreshAsync();
            Assert.Equal("incoming.params", Assert.Single(reloadedStore.Profiles).SourceFileName);

            var exported = Path.Combine(root, "exported.params");
            await store.ExportAsync(imported.Id, exported);
            Assert.Contains("COM_ARMABLE", await File.ReadAllTextAsync(exported));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
