using RobotCommand.Services.Serial;
using Xunit;

namespace RobotCommand.Tests;

public sealed class SikRadioConfigurationTests
{
    [Fact]
    public void Validate_AcceptsCommonSiKSettings()
    {
        var service = new SikRadioConfigurationService(new UnusedFactory());
        var settings = new Dictionary<int, int>
        {
            [1] = 57,
            [2] = 64,
            [3] = 25,
            [4] = 20,
            [5] = 1,
            [6] = 1,
            [8] = 902000,
            [9] = 928000,
            [10] = 50,
            [11] = 100,
            [12] = 0
        };

        Assert.Empty(service.Validate(settings));
    }

    [Fact]
    public void Validate_RejectsUnsafeOrInconsistentSettings()
    {
        var service = new SikRadioConfigurationService(new UnusedFactory());
        var settings = new Dictionary<int, int> { [4] = 100, [8] = 928000, [9] = 902000 };

        var errors = service.Validate(settings);

        Assert.Contains(errors, item => item.Contains("S4", StringComparison.Ordinal));
        Assert.Contains(errors, item => item.Contains("minimum frequency", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class UnusedFactory : ISerialByteTransportFactory
    {
        public ISerialByteTransport Create() => throw new NotSupportedException();
    }
}
