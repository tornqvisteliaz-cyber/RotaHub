using RotaHub.Core;
using Xunit;

namespace RotaHub.Tests;

public class SimLocatorTests
{
    [Theory]
    [InlineData("msfs2024", "Steam", "steam://rungameid/2537590")]
    [InlineData("msfs2024", "Microsoft Store", @"shell:AppsFolder\Microsoft.Limitless_8wekyb3d8bbwe!App")]
    [InlineData("msfs2024", "Chosen by you", "steam://rungameid/2537590")]
    [InlineData("msfs2024", null, "steam://rungameid/2537590")]
    [InlineData("msfs2020", "Steam", "steam://rungameid/1250410")]
    [InlineData("msfs2020", "Microsoft Store", @"shell:AppsFolder\Microsoft.FlightSimulator_8wekyb3d8bbwe!App")]
    public void Launches_each_sim_through_its_own_store(string sim, string? edition, string expected)
    {
        Assert.Equal(expected, SimLocator.LaunchTarget(sim, edition));
    }
}
