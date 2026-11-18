using Ascon.Pilot.DataClasses;
using PilotMigration.Pilot;

namespace PilotMigration.Tests;

public class ClusterReadinessTests
{
    [Theory]
    [InlineData(DatabaseState.Launched, true)]
    [InlineData(DatabaseState.Initializing, false)]
    [InlineData(DatabaseState.Indexing, false)]
    [InlineData(DatabaseState.Broken, false)]
    [InlineData(DatabaseState.Stopped, false)]
    public void BothConfiguredDatabasesMustBeLaunched(DatabaseState secondState, bool ready)
    {
        AdminDatabaseInfo[] databases =
        [
            new() { Name = "pilot-prod", State = DatabaseState.Launched },
            new() { Name = "pilot-dev", State = secondState }
        ];

        Assert.Equal(ready, ClusterReadiness.AllLaunched(["pilot-prod", "PILOT-DEV"], databases));
        Assert.False(ClusterReadiness.AllLaunched(["pilot-prod", "pilot-missing"], databases));
        Assert.False(ClusterReadiness.AllLaunched([], databases));
    }
}
