using System.Text.Json;
using OptLife.Core;
using OptLife.Modules.Security;

namespace OptLife.Tests;

internal sealed class FakeSecurityEnvironment : ISecurityEnvironment
{
    public SecurityReport? Report { get; set; }
    public FirewallState? Fw { get; set; }
    public List<string> Disabled { get; } = [];
    public List<string> Features { get; } = [];
    public int Reads { get; private set; }

    public Task<SecurityReport?> Read(CancellationToken ct)
    {
        Reads++;
        return Task.FromResult(Report);
    }

    public FirewallState? Firewall() => Fw;

    public ActionResult DisableRules(IReadOnlyCollection<string> ids)
    {
        Disabled.AddRange(ids);
        Fw = Fw! with { Rules = [.. Fw.Rules.Select(r => ids.Contains(r.Id) ? r with { Enabled = false } : r)] };
        return new ActionResult(true, "ok");
    }

    public Task<ActionResult> DisableFeature(string feature, CancellationToken ct)
    {
        Features.Add(feature);
        Report = Report! with { Features = [.. Report.Features!.Where(f => f != feature)] };
        return Task.FromResult(new ActionResult(true, "ok"));
    }

    public ActionResult Open(string target) => new(true, "");
}

public sealed class SecurityTests : IDisposable
{
    private readonly Sandbox _box = new();
    private readonly FakeSecurityEnvironment _env = new();
    private readonly SecurityModule _module;

    private static readonly DefenderState Healthy = new(true, true, true, true, 0, 10);
    private static readonly FirewallRule Gone = new("aaa", "Old game", @"C:\Games\old.exe", true, true, true, Missing: true);
    private static readonly FirewallRule Here = new("bbb", "Steam", @"C:\Steam\steam.exe", true, true, true, Missing: false);

    public SecurityTests()
    {
        _module = new SecurityModule(_env);
        _env.Report = new SecurityReport(Healthy,
            [new LocalAccount("alex", true, true), new LocalAccount("Administrator", false, true), new LocalAccount("Guest", false, false)],
            ["Printing-PrintToPDFServices-Features"], [new OpenPort(445, "System"), new OpenPort(27036, "steam")]);
        _env.Fw = new FirewallState(true, true, true, [Gone, Here]);
    }

    public void Dispose() => _box.Dispose();

    private async Task Round(bool onDemand = false) => Assert.True(await _box.Runner.RunTaskAsync(_module, _module.Tasks.Single(), CancellationToken.None, onDemand));

    private IReadOnlyList<StoredFinding> Open() => [.. _box.Store.GetFindings().Where(f => f.Module == "security" && f.Status == FindingStatus.Open)];

    private IReadOnlyList<CheckItem> Board() =>
        CheckItem.FromData(_box.Store.QueryEvents(_box.Clock.Now.AddDays(-1), _box.Clock.Now.AddDays(1), module: "security", kind: SecurityModule.BoardKind).MaxBy(e => e.Id)!.Data);

    [Fact]
    public async Task A_healthy_pc_has_only_the_leftover_firewall_rules_to_report()
    {
        await Round();
        var f = Assert.Single(Open());
        Assert.Equal("firewall|orphans", f.Key);
        Assert.Equal("aaa", f.Fix!.Argument);
        Assert.Contains(Board(), i => i.Label.StartsWith("445") && i.Status == CheckStatus.Info); // Ports only shown until a list is written.
    }

    [Fact]
    public async Task A_built_in_account_left_off_reads_as_right_in_words_and_has_no_button()
    {
        await Round();
        var admin = Board().Single(i => i.Label == "Administrator");
        Assert.Equal(CheckStatus.Ok, admin.Status);
        Assert.Null(admin.ValueStatus); // The colour is the judgement, not "red because off".
        Assert.StartsWith("Spento — giusto così\n", admin.Value);
        Assert.Contains("amministratore nascosto", admin.Value);
        Assert.True(admin.Buttons is null or { Count: 0 });
        // Firewall on: nothing to open.
        Assert.All(Board().Where(i => i.Group == SecurityRules.FirewallGroup && i.Status == CheckStatus.Ok), i => Assert.True(i.Buttons is null or { Count: 0 }));
    }

    [Fact]
    public async Task Leftover_rules_are_turned_off_and_a_rule_whose_program_is_there_never_is()
    {
        await Round();
        var action = _module.Actions.Single(a => a.Id == SecurityRules.DisableRules);
        Assert.Equal(["aaa"], _box.Runner.ChoicesOf(_module, action, out _).Select(c => c.Id));
        var result = await _box.Runner.RunActionAsync(_module, action, "test", true, "aaa;bbb", CancellationToken.None);
        Assert.True(result.Ok);
        Assert.Equal(["aaa"], _env.Disabled);
        Assert.Empty(Open());
    }

    [Fact]
    public async Task Defender_problems_are_reported_by_weight()
    {
        _env.Report = _env.Report! with { Defender = new DefenderState(true, true, false, false, 9, 783) };
        await Round();
        var open = Open();
        Assert.Equal(Severity.High, open.Single(f => f.Key == "defender_realtime").Severity);
        Assert.Equal(Severity.Medium, open.Single(f => f.Key == "defender_signatures").Severity);
        Assert.Contains("783", open.Single(f => f.Key == "defender_fullscan").Title);
        Assert.Contains(open, f => f.Key == "defender_tamper");
    }

    [Fact]
    public async Task Accounts_guest_and_unknown_ones_are_judged()
    {
        _env.Report = _env.Report! with { Accounts = [new LocalAccount("alex", true, true), new LocalAccount("Guest", true, false), new LocalAccount("stranger", true, true)] };
        _box.Config.UpdateModule("security", m => m.Settings["knownAccounts"] = JsonSerializer.SerializeToElement(new[] { "alex" }));
        await Round();
        var open = Open();
        Assert.Equal(Severity.Medium, open.Single(f => f.Key == "account|guest").Severity);
        Assert.Equal(Severity.Low, open.Single(f => f.Key == "account|stranger").Severity);
        Assert.DoesNotContain(open, f => f.Key == "account|alex");
    }

    [Fact]
    public async Task A_listed_feature_that_is_on_can_be_turned_off_and_nothing_else_can()
    {
        _env.Report = _env.Report! with { Features = ["SMB1Protocol", "Printing-PrintToPDFServices-Features"] };
        await Round();
        var f = Open().Single(f => f.Key == "feature|smb1protocol");
        var action = _module.Actions.Single(a => a.Id == SecurityRules.DisableFeature);
        Assert.True((await _box.Runner.RunActionAsync(_module, action, "test", true, f.Fix!.Argument, CancellationToken.None)).Ok);
        Assert.False((await _box.Runner.RunActionAsync(_module, action, "test", true, "Printing-PrintToPDFServices-Features", CancellationToken.None)).Ok);
        Assert.Equal(["SMB1Protocol"], _env.Features);
        Assert.DoesNotContain(Open(), x => x.Key.StartsWith("feature|"));
    }

    [Fact]
    public async Task Ports_are_judged_only_once_allowed_ones_are_written()
    {
        _box.Config.UpdateModule("security", m => m.Settings["allowedPorts"] = JsonSerializer.SerializeToElement(new[] { "445", "steam" }));
        _env.Report = _env.Report! with { Ports = [new OpenPort(445, "System"), new OpenPort(27036, "steam"), new OpenPort(5040, "mystery")] };
        await Round();
        Assert.Single(Open(), f => f.Key.StartsWith("port|"));
        Assert.Contains(Open(), f => f.Key == "port|5040|mystery");
    }

    [Fact]
    public async Task The_slow_reading_happens_every_few_hours_unless_asked()
    {
        await Round();
        _box.Clock.Now += TimeSpan.FromHours(1);
        await Round();
        Assert.Equal(1, _env.Reads);
        await Round(onDemand: true);
        Assert.Equal(2, _env.Reads);
    }

    [Fact]
    public void Only_windows_security_settings_and_features_can_be_opened()
    {
        Assert.True(SecurityRules.SafeTarget(SecurityRules.DefenderPage));
        Assert.True(SecurityRules.SafeTarget(SecurityRules.FeaturesPage));
        Assert.False(SecurityRules.SafeTarget(@"C:\Windows\System32\cmd.exe"));
        Assert.Equal(FirewallRule.MakeId("a", "b", true, "6", "80"), FirewallRule.MakeId("a", "b", true, "6", "80"));
        Assert.NotEqual(FirewallRule.MakeId("a", "b", true, "6", "80"), FirewallRule.MakeId("a", "b", false, "6", "80"));
    }
}
