using OptLife.Core;
using OptLife.Modules.Drivers;

namespace OptLife.Tests;

public class DriversTests
{
    // Lines in the shape of real NVIDIA logs (15/10/2026 and 18/11/2026).
    private const string DriverOk = """
          4.058 |    DEBUG: [NVI2.NVInstaller] 1797@CNVInstaller::InternalCalculate : Phase "checkIfCleanInstall" has estimated progress amount of 3.
         52.876 |    DEBUG: [NVI2.NVInstaller] 3026@CNVInstaller::InternalPerformInstallPackagePhases : Condition "IsCleanInstall" disabled phase "deleteOTACache".
         52.881 |    ERROR: [NVI2.NVServiceAction] 1075@CNVServiceAction::ControlService : Failed to open service Exception {0x80070424}.
         53.000 |     INFO: [NVI2.NVInstaller] 2905@CNVInstaller::InternalPerformInstallPackagePhases : Default full progress title is "Installing NVIDIA Graphics Driver...".
         60.000 |     INFO: [NVI2.NVInstaller] 2905@CNVInstaller::InternalPerformInstallPackagePhases : Default full progress title is "Installing NVIDIA HD Audio Driver...".
         70.105 |     INFO: [NVI2.Installation] 1330@CInstallationPostInstall::DecideFinishForms : All packages installed successfully.
         72.858 |    DEBUG: [NVI2.GFCTelemetryFeedback] 253@CGFCTelemetryFeedback::BuildRebootRequiredBlock : Reboot not required.
        """;

    private const string AppUpdate = """
         14.863 |     INFO: [NVI2.NVInstaller] 2905@CNVInstaller::InternalPerformInstallPackagePhases : Default full progress title is "Installing NVIDIA FrameView SDK...".
         16.105 |     INFO: [NVI2.Installation] 1330@CInstallationPostInstall::DecideFinishForms : All packages installed successfully.
        """;

    private const string CleanFailed = """
          3.000 |    DEBUG: [NVI2.NVInstaller] 3026@CNVInstaller::InternalPerformInstallPackagePhases : Condition "IsCleanInstall" enabled phase "deleteOTACache".
         10.000 |     INFO: [NVI2.NVInstaller] 2905@CNVInstaller::InternalPerformInstallPackagePhases : Default full progress title is "Installing NVIDIA Graphics Driver...".
         40.000 |    ERROR: [NVI2.NVDevicePhase] 512@CNVDevicePhase::InvokePhase : Device install failed with 0xE0000203.
         40.100 |    ERROR: [NVI2.NVDevicePhase] 512@CNVDevicePhase::InvokePhase : Device install failed with 0xE0000203.
        """;

    private const string NeedsReboot = """
         10.000 |     INFO: [NVI2.NVInstaller] 2905@CNVInstaller::InternalPerformInstallPackagePhases : Default full progress title is "Installing NVIDIA Graphics Driver...".
         70.105 |     INFO: [NVI2.Installation] 1330@CInstallationPostInstall::DecideFinishForms : All packages installed successfully.
         72.858 |    DEBUG: [NVI2.GFCTelemetryFeedback] 253@CGFCTelemetryFeedback::BuildRebootRequiredBlock : Reboot required.
        """;

    [Fact]
    public void A_log_says_what_was_installed_and_how_it_ended()
    {
        var driver = InstallRules.Read(DriverOk);
        Assert.Equal(["Graphics Driver", "HD Audio Driver"], driver.Components);
        Assert.True(driver.TouchedDriver);
        Assert.True(driver.Succeeded);
        Assert.False(driver.RebootRequired); // "Reboot not required" is not a reboot.
        Assert.False(driver.Clean);

        var app = InstallRules.Read(AppUpdate);
        Assert.False(app.TouchedDriver);
        Assert.True(app.Succeeded);

        var failed = InstallRules.Read(CleanFailed);
        Assert.False(failed.Succeeded);
        Assert.True(failed.Clean);
        Assert.Equal(["Device install failed with 0xE0000203."], failed.Errors);
    }

    private sealed class Setup : IDisposable
    {
        public Sandbox Box { get; } = new();
        public string Log { get; }
        public DateTimeOffset Boot { get; set; }
        public DriversModule Module { get; }

        public Setup()
        {
            Boot = Box.Clock.GetLocalNow().AddDays(-1); // Restarted yesterday, by the test's own clock.
            Log = Path.Combine(Path.GetTempPath(), $"optlife-nv-{Guid.NewGuid():N}.log");
            Module = new DriversModule(() => Boot);
            Box.Config.UpdateModule("drivers", c => c.Settings["installerLogs"] = System.Text.Json.JsonSerializer.SerializeToElement(new[] { Log }));
        }

        /// <summary>The installer writes its log; a few minutes later the module looks.</summary>
        public async Task Install(string text)
        {
            File.WriteAllText(Log, text);
            File.SetLastWriteTime(Log, Box.Clock.GetLocalNow().LocalDateTime);
            Box.Clock.Now += TimeSpan.FromMinutes(5);
            Assert.True(await Box.Runner.RunTaskAsync(Module, Module.Tasks[0], CancellationToken.None), Box.Runner.ReadStatus(Module, Module.Tasks[0]).LastError);
        }

        public void Dispose() => File.Delete(Log);
    }

    [Fact]
    public async Task A_failed_driver_install_is_a_problem_until_one_succeeds_and_its_log_is_kept()
    {
        using var s = new Setup();

        await s.Install(CleanFailed);
        var problem = Assert.Single(s.Box.Store.GetFindings());
        Assert.Equal("L'ultima installazione del driver video non è riuscita", problem.Title);
        Assert.Contains("DDU", problem.Advice);
        Assert.Equal(FindingArea.Setup, problem.Area);
        var copy = Assert.Single(s.Box.Store.QueryEvents(s.Box.Clock.Now.AddDays(-1), s.Box.Clock.Now, kind: DriversModule.InstallKind)).Data["copy"];
        Assert.Equal(CleanFailed, File.ReadAllText(copy)); // The next run of the installer will overwrite the original.

        await s.Install(AppUpdate); // An app update says nothing about the driver.
        Assert.Single(s.Box.Store.GetFindings());

        await s.Install(DriverOk);
        Assert.Empty(s.Box.Store.GetFindings());
    }

    [Fact]
    public async Task A_driver_waiting_for_a_restart_is_a_problem_until_the_pc_restarts()
    {
        using var s = new Setup();

        await s.Install(NeedsReboot);
        Assert.Equal("Il driver video aspetta un riavvio", Assert.Single(s.Box.Store.GetFindings()).Title);

        s.Boot = s.Box.Clock.GetLocalNow(); // Restarted after the install.
        Assert.True(await s.Box.Runner.RunTaskAsync(s.Module, s.Module.Tasks[0], CancellationToken.None));
        Assert.Empty(s.Box.Store.GetFindings());
    }

    [Fact]
    public async Task A_log_still_being_written_is_left_alone()
    {
        using var s = new Setup();
        File.WriteAllText(s.Log, CleanFailed);
        File.SetLastWriteTime(s.Log, s.Box.Clock.GetLocalNow().LocalDateTime); // Just written: the installer may still be running.

        Assert.True(await s.Box.Runner.RunTaskAsync(s.Module, s.Module.Tasks[0], CancellationToken.None));

        Assert.Empty(s.Box.Store.QueryEvents(s.Box.Clock.Now.AddDays(-1), s.Box.Clock.Now.AddDays(1), kind: DriversModule.InstallKind));
    }
}
