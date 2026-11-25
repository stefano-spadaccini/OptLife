using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace OptLife.Modules.Security.Windows;

/// <summary>
/// The real machine. Defender, local accounts, optional features and listening ports come from one PowerShell run (the cmdlets
/// Windows ships; the features need administrator rights, the agent has them); the firewall from its COM interface (HNetCfg.FwPolicy2),
/// which also turns rules off. Read every few hours: a PowerShell run costs about a second of processor.
/// </summary>
internal sealed class WindowsSecurityEnvironment : ISecurityEnvironment
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan FeatureTimeout = TimeSpan.FromMinutes(10);

    private const string Script = """
        $ErrorActionPreference = 'SilentlyContinue'
        $r = [ordered]@{}
        $m = Get-MpComputerStatus
        if ($m) { $r.defender = [ordered]@{ service = [bool]$m.AMServiceEnabled; antivirus = [bool]$m.AntivirusEnabled; realTime = [bool]$m.RealTimeProtectionEnabled; tamper = [bool]$m.IsTamperProtected; signatureAge = [int]$m.AntivirusSignatureAge; fullScanAge = [long]$m.FullScanAge } }
        $u = Get-LocalUser
        if ($u) { $r.accounts = @($u | ForEach-Object { [ordered]@{ name = $_.Name; enabled = [bool]$_.Enabled; passwordRequired = [bool]$_.PasswordRequired } }) }
        $f = Get-WindowsOptionalFeature -Online
        if ($f) { $r.features = @($f | Where-Object { [string]$_.State -eq 'Enabled' } | ForEach-Object { $_.FeatureName }) }
        $names = @{}; Get-Process | ForEach-Object { $names[[int]$_.Id] = $_.ProcessName }
        $l = Get-NetTCPConnection -State Listen
        if ($l) { $r.ports = @($l | Where-Object { $_.LocalAddress -in '0.0.0.0', '::' } | ForEach-Object { [pscustomobject]@{ port = [int]$_.LocalPort; process = [string]$names[[int]$_.OwningProcess] } } | Sort-Object port, process -Unique) }
        $r | ConvertTo-Json -Depth 4 -Compress
        """;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private sealed record Raw(DefenderState? Defender, List<LocalAccount>? Accounts, List<string>? Features, List<OpenPort>? Ports);

    public async Task<SecurityReport?> Read(CancellationToken ct)
    {
        var (code, output) = await PowerShell(Script, ReadTimeout, ct);
        if (code != 0 || output.Trim().Length == 0) return null;
        try
        {
            var raw = JsonSerializer.Deserialize<Raw>(output.Trim(), Json);
            return raw is null ? null : new SecurityReport(raw.Defender, raw.Accounts, raw.Features, raw.Ports);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<ActionResult> DisableFeature(string feature, CancellationToken ct)
    {
        if (!feature.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.')) return new ActionResult(false, L.T("Nome non valido.", "Invalid name."));
        var (code, output) = await PowerShell($"$ErrorActionPreference='Stop'; $x = Disable-WindowsOptionalFeature -Online -FeatureName '{feature}' -NoRestart; if ($x.RestartNeeded) {{ 'restart' }}", FeatureTimeout, ct);
        return code == 0
            ? new ActionResult(true, L.T($"{feature} spenta.", $"{feature} turned off.") + (output.Contains("restart") ? L.T(" Vale dal prossimo riavvio.", " It takes effect at the next restart.") : ""),
                $"Enable-WindowsOptionalFeature -Online -FeatureName {feature}")
            : new ActionResult(false, L.T("Non riuscita (serve OptLife da amministratore): ", "Failed (needs OptLife running as administrator): ") + output.Trim());
    }

    private static async Task<(int Code, string Output)> PowerShell(string script, TimeSpan limit, CancellationToken ct)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var start = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        try
        {
            using var p = Process.Start(start);
            if (p is null) return (-1, "");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(limit);
            var output = p.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = p.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await p.WaitForExitAsync(timeout.Token);
                var text = await output;
                return (p.ExitCode, p.ExitCode == 0 ? text : await error);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return (-1, L.T("troppo tempo", "took too long"));
            }
        }
        catch (Win32Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    // ── Firewall ─────────────────────────────────────────────────────────────

    public FirewallState? Firewall()
    {
        object? policy = null;
        try
        {
            if (Type.GetTypeFromProgID("HNetCfg.FwPolicy2") is not { } type || (policy = Activator.CreateInstance(type)) is null) return null;
            dynamic fw = policy;
            bool? Profile(int p)
            {
                try { return (bool)fw.FirewallEnabled[p]; }
                catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { return null; }
            }
            var rules = new List<FirewallRule>();
            foreach (dynamic r in fw.Rules)
            {
                try
                {
                    string program = r.ApplicationName ?? "";
                    if (program.Length == 0) continue;
                    string name = r.Name ?? "";
                    var inbound = (int)r.Direction == 1;
                    string protocol = ((int)r.Protocol).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    string ports = (inbound ? r.LocalPorts : r.RemotePorts) ?? "";
                    var path = Environment.ExpandEnvironmentVariables(program);
                    // Only plain file paths can be told missing; packaged apps live where a check could fail for lack of rights.
                    var missing = Path.IsPathRooted(path) && !path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)
                                  && !path.Equals("System", StringComparison.OrdinalIgnoreCase) && !File.Exists(path);
                    rules.Add(new FirewallRule(FirewallRule.MakeId(name, program, inbound, protocol, ports), name, program, inbound, (int)r.Action == 1, (bool)r.Enabled, missing));
                }
                catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or ArgumentException)
                {
                    // A rule that cannot be read is left alone.
                }
            }
            return new FirewallState(Profile(1), Profile(2), Profile(4), rules);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return null;
        }
        finally
        {
            if (policy is not null) Marshal.ReleaseComObject(policy);
        }
    }

    public ActionResult DisableRules(IReadOnlyCollection<string> ids)
    {
        object? policy = null;
        var done = new List<string>();
        var failed = 0;
        try
        {
            if (Type.GetTypeFromProgID("HNetCfg.FwPolicy2") is not { } type || (policy = Activator.CreateInstance(type)) is null)
                return new ActionResult(false, L.T("Firewall non raggiungibile.", "Firewall not reachable."));
            dynamic fw = policy;
            var wanted = ids.ToHashSet();
            foreach (dynamic r in fw.Rules)
            {
                try
                {
                    string program = r.ApplicationName ?? "";
                    if (program.Length == 0) continue;
                    string name = r.Name ?? "";
                    var inbound = (int)r.Direction == 1;
                    string protocol = ((int)r.Protocol).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    string ports = (inbound ? r.LocalPorts : r.RemotePorts) ?? "";
                    if (!wanted.Contains(FirewallRule.MakeId(name, program, inbound, protocol, ports))) continue;
                    r.Enabled = false;
                    done.Add(name);
                }
                catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
                {
                    failed++;
                }
            }
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return new ActionResult(false, L.T("Non riuscita (serve OptLife da amministratore): ", "Failed (needs OptLife running as administrator): ") + ex.Message);
        }
        finally
        {
            if (policy is not null) Marshal.ReleaseComObject(policy);
        }
        if (done.Count == 0 && failed > 0)
            return new ActionResult(false, L.T("Windows non le ha lasciate spegnere: serve OptLife da amministratore.", "Windows did not let them be turned off: OptLife needs to run as administrator."));
        return new ActionResult(failed == 0, L.T($"{done.Count} regole spente (non cancellate)", $"{done.Count} rules turned off (not deleted)") +
            (failed > 0 ? L.T($", {failed} non riuscite.", $", {failed} failed.") : "."),
            L.T("Windows Defender Firewall → Impostazioni avanzate → Regole: tasto destro → Abilita regola.", "Windows Defender Firewall → Advanced settings → Rules: right click → Enable rule."));
    }

    public ActionResult Open(string target)
    {
        try
        {
            // Through Explorer, so it opens as the person even when OptLife runs as administrator.
            Process.Start(new ProcessStartInfo("explorer.exe", target == SecurityRules.FeaturesPage ? "shell:::{67718415-c450-4f3c-bf8a-b487642dc39b}" : $"\"{target}\"") { UseShellExecute = false })?.Dispose();
            return new ActionResult(true, L.T($"Aperto: {target}", $"Opened: {target}"));
        }
        catch (Win32Exception ex)
        {
            return new ActionResult(false, ex.Message);
        }
    }
}
