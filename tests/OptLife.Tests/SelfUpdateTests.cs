using System.Text.Json;
using OptLife.Core.SelfUpdate;

namespace OptLife.Tests;

internal sealed class FakeSelfUpdateEnvironment : ISelfUpdateEnvironment
{
    public Dictionary<string, string> Texts { get; } = [];
    public byte[] File { get; set; } = "installer"u8.ToArray();
    public List<(string Path, string Args)> Launched { get; } = [];
    public bool Panel { get; set; }
    public bool? Signature { get; set; }

    public Task<string?> GetText(string url, CancellationToken ct) => Task.FromResult(Texts.GetValueOrDefault(url));

    public Task<bool> Download(string url, string path, Action<long, long?> progress, CancellationToken ct)
    {
        System.IO.File.WriteAllBytes(path, File);
        return Task.FromResult(true);
    }

    public string Sha256(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.IO.File.ReadAllBytes(path))).ToLowerInvariant();
    public bool? SignatureValid(string path) => Signature;
    public ActionResult Launch(string installer, string arguments) { Launched.Add((installer, arguments)); return new ActionResult(true, ""); }
    public bool PanelRunning() => Panel;
    public ActionResult Open(string url) => new(true, url);
}

public sealed class SelfUpdateTests : IDisposable
{
    private const string Repo = "someone/OptLife";
    private readonly Sandbox _box = new();
    private readonly FakeSelfUpdateEnvironment _env = new();
    private readonly SelfUpdateModule _module;

    public SelfUpdateTests()
    {
        _module = new SelfUpdateModule(_env);
        _box.Config.UpdateModule("optlife", m => m.Settings["repository"] = JsonSerializer.SerializeToElement(Repo));
    }

    public void Dispose() => _box.Dispose();

    private static string ReleaseJson(string tag) => JsonSerializer.Serialize(new
    {
        tag_name = tag, draft = false, body = "## Added\n- **Faster** start", html_url = "https://github.com/someone/OptLife/releases/tag/" + tag,
        assets = new[]
        {
            new { name = $"OptLife-Setup-{tag.TrimStart('v')}.exe", browser_download_url = "https://github.com/dl/setup.exe" },
            new { name = "SHA256SUMS.txt", browser_download_url = "https://github.com/dl/sums" },
        },
    });

    private string Hash(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

    private async Task Check() => Assert.True(await _box.Runner.RunTaskAsync(_module, _module.Tasks.Single(), CancellationToken.None, onDemand: true),
        _box.Runner.ReadStatus(_module, _module.Tasks.Single()).LastError);

    private Task<ActionResult> Install() =>
        _box.Runner.RunActionAsync(_module, _module.Actions.Single(a => a.Id == SelfUpdateModule.Install), "test", true, null, CancellationToken.None);

    [Fact]
    public void Versions_tags_and_checksums_are_read_strictly()
    {
        Assert.Equal(new Version(1, 2, 0), SelfUpdateRules.ParseVersion("v1.2"));
        Assert.Equal(new Version(0, 3, 1), SelfUpdateRules.ParseVersion("0.3.1-beta+abc"));
        Assert.Null(SelfUpdateRules.ParseVersion("latest"));
        Assert.Equal(new string('a', 64), SelfUpdateRules.ExpectedHash($"{new string('A', 64)}  OptLife-Setup-1.0.0.exe\n", "OptLife-Setup-1.0.0.exe"));
        Assert.Null(SelfUpdateRules.ExpectedHash("abc  OptLife-Setup-1.0.0.exe", "OptLife-Setup-1.0.0.exe"));
        Assert.True(SelfUpdateRules.ValidRepository("someone/OptLife"));
        Assert.False(SelfUpdateRules.ValidRepository("someone/OptLife/../x"));
        Assert.False(SelfUpdateRules.SafeFileName(@"..\evil.exe"));
        var release = SelfUpdateRules.ParseRelease(ReleaseJson("v9.0.0"))!;
        Assert.Equal(("OptLife-Setup-9.0.0.exe", "https://github.com/dl/sums"), (release.InstallerName, release.SumsUrl));
        Assert.Equal("Added\n- Faster start", SelfUpdateRules.Notes(release.Notes));
    }

    [Fact]
    public async Task A_newer_release_is_shown_and_installed_only_with_the_published_checksum()
    {
        _env.Texts[SelfUpdateRules.LatestUrl(Repo)] = ReleaseJson("v9.0.0");
        _env.Texts["https://github.com/dl/sums"] = $"{Hash("something else"u8.ToArray())}  OptLife-Setup-9.0.0.exe";
        await Check();
        Assert.Contains(_box.Store.GetFindings(), f => f.Module == "optlife" && f.Title.Contains("9.0.0"));

        var refused = await Install();
        Assert.False(refused.Ok);
        Assert.Contains("SHA-256", refused.Detail);
        Assert.Empty(_env.Launched);

        _env.Texts["https://github.com/dl/sums"] = $"{Hash(_env.File)}  OptLife-Setup-9.0.0.exe";
        _env.Panel = true;
        var done = await Install();
        Assert.True(done.Ok, done.Detail);
        var (path, args) = Assert.Single(_env.Launched);
        Assert.EndsWith("OptLife-Setup-9.0.0.exe", path);
        Assert.Contains("/VERYSILENT", args);
        Assert.Contains("/OPENPANEL=1", args); // The panel the installer closes opens again.
    }

    [Fact]
    public async Task A_broken_signature_or_the_same_version_installs_nothing()
    {
        _env.Texts[SelfUpdateRules.LatestUrl(Repo)] = ReleaseJson("v9.0.0");
        _env.Texts["https://github.com/dl/sums"] = $"{Hash(_env.File)}  OptLife-Setup-9.0.0.exe";
        _env.Signature = false;
        await Check();
        Assert.False((await Install()).Ok);
        Assert.Empty(_env.Launched);

        _env.Texts[SelfUpdateRules.LatestUrl(Repo)] = ReleaseJson("v0.0.1");
        await Check();
        Assert.DoesNotContain(_box.Store.GetFindings(), f => f.Module == "optlife" && f.Status == FindingStatus.Open);
        Assert.False((await Install()).Ok);
    }
}
