using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using OptLife.Core;

namespace OptLife.Modules.Emulation.Windows;

/// <summary>
/// The real machine: files on disk, the uninstall lists, GitHub's public release list (no account), downloads
/// unpacked with .NET (zip) or 7-Zip (7z), winget for the emulators published there.
/// </summary>
internal sealed class WindowsEmulationEnvironment : IEmulationEnvironment
{
    private const int SearchDepth = 4;
    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OptLife", "1.0"));
        return http;
    }

    public string? FindFile(string folder, string pattern)
    {
        if (!Directory.Exists(folder)) return null;
        var pending = new Queue<(string Dir, int Depth)>([(folder, 0)]);
        while (pending.Count > 0)
        {
            var (dir, depth) = pending.Dequeue();
            try
            {
                if (Directory.EnumerateFiles(dir, pattern).FirstOrDefault() is { } hit) return hit;
                if (depth < SearchDepth)
                    foreach (var sub in Directory.EnumerateDirectories(dir))
                        pending.Enqueue((sub, depth + 1));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // A folder that cannot be read: look elsewhere.
            }
        }
        return null;
    }

    public bool Exists(string pathPattern)
    {
        try
        {
            var dir = Path.GetDirectoryName(pathPattern);
            var name = Path.GetFileName(pathPattern);
            if (dir is null || !Directory.Exists(dir)) return false;
            return name.Contains('*') ? Directory.EnumerateFileSystemEntries(dir, name).Any() : File.Exists(pathPattern) || Directory.Exists(pathPattern);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException)
        {
            return false;
        }
    }

    public IReadOnlyList<string> InstalledNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hive, view) in new[] { (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32), (RegistryHive.CurrentUser, RegistryView.Registry64) })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var list = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                foreach (var sub in list?.GetSubKeyNames() ?? [])
                {
                    using var app = list!.OpenSubKey(sub);
                    if (app?.GetValue("DisplayName") is string { Length: > 0 } n) names.Add(n);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        }
        return [.. names];
    }

    public string? Version(string exe)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exe);
            var v = (info.ProductVersion ?? info.FileVersion)?.Trim();
            if (!string.IsNullOrEmpty(v) && v != "0.0.0.0") return v.Split('+')[0];
            // No version inside the program: many portable folders carry it in their name ("rpcs3-v0.0.37-18022-…").
            var folder = Path.GetFileName(Path.GetDirectoryName(exe)) ?? "";
            return Regex.Match(folder, @"v?(\d+\.\d+(\.\d+)?)") is { Success: true } m ? m.Groups[1].Value : null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    public async Task<Release?> Latest(string repo, string assetPattern, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{repo}/releases/latest", ct));
            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            var asset = doc.RootElement.GetProperty("assets").EnumerateArray()
                .FirstOrDefault(a => Regex.IsMatch(a.GetProperty("name").GetString() ?? "", assetPattern, RegexOptions.IgnoreCase));
            var url = asset.ValueKind == JsonValueKind.Object ? asset.GetProperty("browser_download_url").GetString() : null;
            // A tag without a version ("build-3bfac60") often has it in the file name ("rpcs3-v0.0.43-…"): that one can be compared.
            var assetName = asset.ValueKind == JsonValueKind.Object ? asset.GetProperty("name").GetString() ?? "" : "";
            if (!Regex.IsMatch(tag, @"\d+\.\d+") && Regex.Match(assetName, @"v?(\d+\.\d+(\.\d+)?)") is { Success: true } named) tag = "v" + named.Groups[1].Value;
            // A rolling tag ("latest", "continuous") says nothing: the file's date does.
            if (tag is "latest" or "continuous" && asset.ValueKind == JsonValueKind.Object && asset.TryGetProperty("updated_at", out var at))
                tag = $"{tag} {at.GetString()?[..10]}";
            return new Release(tag, url);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            return null; // Offline, or GitHub said no: the next check tries again.
        }
    }

    public async Task<ActionResult> DownloadInto(string url, string folder, CancellationToken ct)
    {
        var work = Path.Combine(Path.GetTempPath(), "optlife-emu-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var archive = Path.Combine(work, Path.GetFileName(new Uri(url).LocalPath));
            await using (var source = await Http.GetStreamAsync(url, ct))
            await using (var target = File.Create(archive))
                await source.CopyToAsync(target, ct);

            var unpacked = Path.Combine(work, "x");
            if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                ZipFile.ExtractToDirectory(archive, unpacked, overwriteFiles: true);
            else if (archive.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
            {
                if (SevenZip() is not { } sevenZip)
                    return new ActionResult(false, L.T("serve 7-Zip per aprire questo archivio (è in Prima configurazione)", "7-Zip is needed to open this archive (it is in First setup)"));
                using var p = Process.Start(new ProcessStartInfo(sevenZip, $"x -y \"-o{unpacked}\" \"{archive}\"") { CreateNoWindow = true, UseShellExecute = false })!;
                await p.WaitForExitAsync(ct);
                if (p.ExitCode != 0) return new ActionResult(false, L.T($"7-Zip ha dato errore {p.ExitCode}", $"7-Zip failed with {p.ExitCode}"));
            }
            else return new ActionResult(false, L.T("formato di archivio sconosciuto", "unknown archive format"));

            // Many archives hold one folder with everything inside: its content is what goes in place.
            var top = unpacked;
            while (Directory.GetFiles(top).Length == 0 && Directory.GetDirectories(top) is [var only]) top = only;
            CopyOver(top, folder);
            return new ActionResult(true, L.T($"Installato in {folder}.", $"Installed in {folder}."));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or Win32Exception)
        {
            return new ActionResult(false, ex.Message);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The program's files replace the old ones; what is only in the folder (settings, saves) stays.</summary>
    private static void CopyOver(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(from)) CopyOver(dir, Path.Combine(to, Path.GetFileName(dir)));
    }

    private static string? SevenZip()
    {
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            if (Path.Combine(root, "7-Zip", "7z.exe") is var exe && File.Exists(exe)) return exe;
        return null;
    }

    public async Task<ActionResult> Winget(string id, CancellationToken ct)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("winget",
                $"install --id {id} --exact --silent --source winget --accept-package-agreements --accept-source-agreements --disable-interactivity")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            _ = p.StandardOutput.ReadToEndAsync(ct);
            _ = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            return p.ExitCode is 0 or unchecked((int)0x8A15002B) or unchecked((int)0x8A150061) or unchecked((int)0x8A150109) or 3010
                ? new ActionResult(true, "")
                : new ActionResult(false, L.T($"winget: codice {p.ExitCode:X8}", $"winget: code {p.ExitCode:X8}"));
        }
        catch (Win32Exception)
        {
            return new ActionResult(false, L.T("winget non c'è: aggiorna \"Programma di installazione app\" dal Microsoft Store", "winget is missing: update \"App Installer\" from the Microsoft Store"));
        }
    }

    public ActionResult Open(string target)
    {
        try
        {
            // Through Explorer, so a browser or folder opens as the person even when OptLife runs as administrator.
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = false })?.Dispose();
            return new ActionResult(true, L.T($"Aperto: {target}", $"Opened: {target}"));
        }
        catch (Win32Exception ex)
        {
            return new ActionResult(false, ex.Message);
        }
    }

    public void CreateFolder(string path)
    {
        try { Directory.CreateDirectory(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
