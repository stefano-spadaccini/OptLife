using System.Diagnostics;

namespace OptLife.Tests;

public sealed class ProtectionTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "optlife-tests", "protection-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void A_file_the_person_made_is_one_a_non_administrator_can_change()
    {
        var script = Path.Combine(_dir, "job.ps1");
        File.WriteAllText(script, "Write-Host hi");
        // Made by whoever runs the tests (not the system, not the Administrators group): they could change it.
        Assert.NotNull(OptLife.Core.Protection.WritableByOthers(script));
    }

    [Fact]
    public void A_file_of_windows_itself_is_not()
    {
        var notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        Assert.Null(OptLife.Core.Protection.WritableByOthers(notepad));
    }

    [Fact]
    public void A_path_through_a_junction_is_seen_as_a_link()
    {
        var target = Directory.CreateDirectory(Path.Combine(_dir, "target")).FullName;
        var link = Path.Combine(_dir, "link");
        // A junction needs no administrator rights: exactly what a user could make.
        using (var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false }))
            p!.WaitForExit(10_000);
        Assert.True(Directory.Exists(link));
        Assert.True(OptLife.Core.Protection.ThroughLink(Path.Combine(link, "file.txt")));
        Assert.False(OptLife.Core.Protection.ThroughLink(Path.Combine(target, "file.txt")));
        Directory.Delete(link);
    }

    [Fact]
    public void Only_a_copy_under_program_files_is_an_installed_one()
    {
        Assert.True(OptLife.Core.Hosting.OptLifeRuntime.InProgramFiles(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OptLife")));
        Assert.False(OptLife.Core.Hosting.OptLifeRuntime.InProgramFiles(@"C:\Software\OptLife"));
        Assert.False(OptLife.Core.Hosting.OptLifeRuntime.InProgramFiles(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + "Old\\OptLife"));
    }
}
