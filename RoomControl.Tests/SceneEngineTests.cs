using RoomControl.Core;
using RoomControl.Core.Commands;
using RoomControl.Core.Models;

namespace RoomControl.Tests;

public class SceneEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(SceneId.AllOn, "LightSetCommand:4,CurtainSetCommand:1")]
    [InlineData(SceneId.Reading, "LightSetCommand:6,AirconSetCommand:1,TvSetCommand:1")]
    [InlineData(SceneId.Sleep, "LightSetCommand:4,CurtainSetCommand:1,AirconSetCommand:1,TvSetCommand:1")]
    [InlineData(SceneId.NightLight, "LightSetCommand:5,CurtainSetCommand:1")]
    [InlineData(SceneId.Away, "LightSetCommand:4,TvSetCommand:1,AirconSetCommand:1,CurtainSetCommand:1,DoorSignSetCommand:1")]
    [InlineData(SceneId.DoNotDisturb, "DoorSignSetCommand:1")]
    public void Each_scene_expands_to_the_expected_device_command_set(SceneId scene, string expected)
    {
        var commands = SceneEngine.BuildSceneCommands(scene, TestSupport.Inventory(), Now);

        var actual = string.Join(",", commands
            .GroupBy(c => c.GetType().Name)
            .Select(g => $"{g.Key}:{g.Count()}")
            .OrderBy(s => s, StringComparer.Ordinal));
        var expectedSorted = string.Join(",", expected.Split(',').OrderBy(s => s, StringComparer.Ordinal));

        Assert.Equal(expectedSorted, actual);
        Assert.Equal(commands.Count, commands.Select(c => c.CommandId).Distinct().Count());
        Assert.All(commands, c => Assert.Equal("R101", c.RoomId));
    }

    [Fact]
    public void Away_scene_sets_aircon_back_instead_of_powering_off_and_enables_dnd()
    {
        var commands = SceneEngine.BuildSceneCommands(SceneId.Away, TestSupport.Inventory(), Now);

        var aircon = Assert.Single(commands.OfType<AirconSetCommand>());
        Assert.True(aircon.IsOn);
        Assert.Equal(28.0, aircon.TargetTemperatureC);

        Assert.All(commands.OfType<LightSetCommand>(), l => Assert.False(l.IsOn));
        Assert.False(Assert.Single(commands.OfType<TvSetCommand>()).IsOn);
        Assert.Equal(CurtainPosition.Closed, Assert.Single(commands.OfType<CurtainSetCommand>()).Position);
        Assert.Equal(DoorSignMode.DoNotDisturb, Assert.Single(commands.OfType<DoorSignSetCommand>()).Mode);
    }

    [Fact]
    public void Unknown_scene_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SceneEngine.BuildSceneCommands((SceneId)999, TestSupport.Inventory(), Now));
    }
}
