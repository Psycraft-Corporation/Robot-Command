using RobotCommand.Core;
using RobotCommand.Models;
using RobotCommand.Services;
using RobotCommand.Services.Workflows;
using Xunit;

namespace RobotCommand.Tests;

public sealed class FinalPresentationWorkflowTests
{
    [Fact]
    public async Task PreferencesWorkflow_UpdatesAndProjectsGlobalPreferences()
    {
        var settings = new MemoryApplicationSettings(new AppUiSettings());
        var units = new MemoryUnitSettings(new AppUnitSettings());
        var workflow = new ApplicationPreferencesWorkflow(settings, units);
        var changes = 0;
        workflow.Changed += (_, _) => changes++;

        await workflow.SetLanguageAsync("fr", TestContext.Current.CancellationToken);
        await workflow.SetBrightModeAsync(true, TestContext.Current.CancellationToken);
        await workflow.SetUnitsAsync("Feet", "Feet", "Acres", "Knots", "Fahrenheit", TestContext.Current.CancellationToken);

        Assert.Equal("fr", workflow.Current.Language);
        Assert.True(workflow.Current.BrightModeEnabled);
        Assert.Equal("Feet", workflow.Current.HorizontalDistance);
        Assert.Equal("Acres", workflow.Current.Area);
        Assert.Equal("Knots", workflow.Current.Speed);
        Assert.Equal("Fahrenheit", workflow.Current.Temperature);
        Assert.True(changes >= 3);
    }

    [Fact]
    public async Task PreferencesWorkflow_RejectsInvalidDisplayUnits()
    {
        var workflow = new ApplicationPreferencesWorkflow(
            new MemoryApplicationSettings(new AppUiSettings()),
            new MemoryUnitSettings(new AppUnitSettings()));

        await Assert.ThrowsAsync<ArgumentException>(() => workflow.SetUnitsAsync(
            "Parsecs", "Meters", "SquareMeters", "MetersPerSecond", "Celsius",
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void PresentationWorkflowContracts_AreFrontendNeutral()
    {
        var assembly = typeof(IEvidenceWorkflow).Assembly;

        Assert.Equal("RobotCommand.Core", assembly.GetName().Name);
        Assert.Empty(assembly.GetReferencedAssemblies().Where(reference =>
            reference.Name?.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase) == true));
        Assert.NotNull(typeof(IEvidenceWorkflow).GetEvent(nameof(IEvidenceWorkflow.Changed)));
        Assert.NotNull(typeof(IMediaWorkflow).GetProperty(nameof(IMediaWorkflow.Current)));
        Assert.NotNull(typeof(IMapLibraryWorkflow).GetMethod(nameof(IMapLibraryWorkflow.ImportAsync)));
        Assert.NotNull(typeof(ITeamObserverClientWorkflow).GetMethod(nameof(ITeamObserverClientWorkflow.ConnectAsync)));
    }

    private sealed class MemoryApplicationSettings(AppUiSettings initial) : IApplicationSettingsService
    {
        public AppUiSettings Current { get; private set; } = initial;
        public event EventHandler? Changed;
        public Task SetBrightModeEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
        {
            Current = Current with { BrightModeEnabled = enabled };
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
        public Task SetLanguageAsync(string language, CancellationToken cancellationToken = default)
        {
            Current = Current with { Language = language };
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
        public Task SetUnitsAsync(AppUnitSettings units, CancellationToken cancellationToken = default)
        {
            Current = Current with { Units = units };
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
        public Task SetOperateInspectorWidthAsync(double width, CancellationToken cancellationToken = default)
        {
            Current = Current with { OperateInspectorWidth = width };
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryUnitSettings(AppUnitSettings initial) : IUnitSettingsService
    {
        public AppUnitSettings Current { get; private set; } = initial;
        public event EventHandler? Changed;
        public Task SetAsync(AppUnitSettings settings, CancellationToken cancellationToken = default)
        {
            Current = settings;
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
    }
}
