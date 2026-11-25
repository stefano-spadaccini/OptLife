using System.Reflection;
using System.Text.Json;

namespace OptLife.Core.SelfUpdate;

/// <summary>A release of OptLife as GitHub describes it: the version, what is new, the installer and its published checksum.</summary>
public sealed record Release(Version Version, string Tag, string Notes, string Page, string? InstallerName, string? InstallerUrl, string? SumsUrl);

/// <summary>The rules of OptLife's own updates, without the network: reading a release, comparing versions, the checksum.</summary>
public static class SelfUpdateRules
{
    /// <summary>The version this copy of OptLife is ("0.1.0"; any "+commit" or "-beta" tail left out).</summary>
    public static Version Current
    {
        get
        {
            var text = typeof(SelfUpdateRules).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                       ?? typeof(SelfUpdateRules).Assembly.GetName().Version?.ToString() ?? "0.0.0";
            return ParseVersion(text) ?? new Version(0, 0, 0);
        }
    }

    /// <summary>The GitHub repository this build looks for updates in ("owner/name"), set when the release was built; empty for a build of one's own.</summary>
    public static string BuiltRepository =>
        typeof(SelfUpdateRules).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "OptLifeRepository")?.Value ?? "";

    /// <summary>"owner/name", nothing else: the address is built from it, so nothing else may slip in.</summary>
    public static bool ValidRepository(string repo) =>
        System.Text.RegularExpressions.Regex.IsMatch(repo, @"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})/[A-Za-z0-9._-]{1,100}$");

    public static string LatestUrl(string repo) => $"https://api.github.com/repos/{repo}/releases/latest";

    /// <summary>"v1.2.3", "1.2", "1.2.3-beta+abc" → 1.2.3; null for anything else.</summary>
    public static Version? ParseVersion(string text)
    {
        var t = text.Trim().TrimStart('v', 'V');
        var end = t.IndexOfAny(['-', '+', ' ']);
        if (end >= 0) t = t[..end];
        if (!Version.TryParse(t, out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
    }

    /// <summary>The release from GitHub's answer; null when it is not one (no tag that reads as a version).</summary>
    public static Release? ParseRelease(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
            var tag = r.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            if (ParseVersion(tag) is not { } version) return null;
            string? installer = null, installerUrl = null, sums = null;
            if (r.TryGetProperty("assets", out var assets))
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.GetProperty("name").GetString() ?? "";
                    var url = a.GetProperty("browser_download_url").GetString();
                    if (name.StartsWith("OptLife-Setup-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        (installer, installerUrl) = (name, url);
                    else if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)) sums = url;
                }
            return new Release(version, tag, r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
                r.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "", installer, installerUrl, sums);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return null;
        }
    }

    /// <summary>The checksum published for a file, from a "sha256sum" list ("&lt;hash&gt;  &lt;name&gt;" per line; "*name" too).</summary>
    public static string? ExpectedHash(string sums, string fileName)
    {
        foreach (var line in sums.Split('\n'))
        {
            var parts = line.Trim().Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].Trim().TrimStart('*').Equals(fileName, StringComparison.OrdinalIgnoreCase)
                && System.Text.RegularExpressions.Regex.IsMatch(parts[0], "^[0-9a-fA-F]{64}$"))
                return parts[0].ToLowerInvariant();
        }
        return null;
    }

    /// <summary>The release notes in a few lines for the page: no Markdown marks, at most <paramref name="max"/> characters.</summary>
    public static string Notes(string body, int max = 1200)
    {
        var lines = body.Replace("\r", "").Split('\n')
            .Select(l => l.Trim().TrimStart('#').Trim().Replace("**", "").Replace("`", ""))
            .Where(l => l.Length > 0).ToList();
        var text = string.Join('\n', lines);
        return text.Length <= max ? text : text[..max].TrimEnd() + "…";
    }

    /// <summary>Only a file name OptLife itself would write: no folders, nothing odd.</summary>
    public static bool SafeFileName(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(name, @"^OptLife-Setup-[0-9A-Za-z.\-]+\.exe$");
}
