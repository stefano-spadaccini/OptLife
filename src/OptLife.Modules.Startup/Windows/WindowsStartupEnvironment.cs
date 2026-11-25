using System.Diagnostics;
using Microsoft.Win32;

namespace OptLife.Modules.Startup.Windows;

/// <summary>
/// The real machine, read the way Task Manager reads it. The on/off switch of a Run entry or of a Startup folder
/// file is a value under Explorer\StartupApproved (the entry itself is never touched); a sign-in task is switched
/// on and off in the Task Scheduler; a Store app has its own State. Entries registered for all users need
/// administrator rights to switch: the agent has them.
/// </summary>
internal sealed class WindowsStartupEnvironment : IStartupEnvironment
{
    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Run32 = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";
    private const string StoreTasks = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";

    private static RegistryKey Hive(string where) => where.StartsWith("HKLM", StringComparison.Ordinal)
        ? RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
        : RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);

    /// <summary>Where each Run key keeps its switches.</summary>
    private static (string Hive, string RunKey, string ApprovedKey) RunPlace(string where) => where switch
    {
        "HKLM" => ("HKLM", Run, $@"{Approved}\Run"),
        "HKLM32" => ("HKLM", Run32, $@"{Approved}\Run32"),
        _ => ("HKCU", Run, $@"{Approved}\Run"),
    };

    public IReadOnlyList<StartupItem> Read()
    {
        var items = new List<StartupItem>();
        foreach (var where in new[] { "HKCU", "HKLM", "HKLM32" }) ReadRun(where, items);
        ReadFolder(user: true, items);
        ReadFolder(user: false, items);
        ReadTasks(items);
        ReadStore(items);
        return [.. items.Select(Describe)];
    }

    /// <summary>
    /// Services set to start automatically whose program is not part of Windows (not under the Windows folder, not svchost).
    /// Read from the registry, like Services does; no rights needed.
    /// </summary>
    public IReadOnlyList<StartupService> Services()
    {
        var list = new List<StartupService>();
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var root = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (root is null) return list;
            foreach (var name in root.GetSubKeyNames())
            {
                try
                {
                    using var key = root.OpenSubKey(name);
                    // Start 2 = automatic (also delayed); Type 0x10/0x20 = a program, not a driver.
                    if (key?.GetValue("Start") is not int start || start != 2 || key.GetValue("Type") is not int type || (type & 0x30) == 0) continue;
                    // The program it starts: its own file, or (a launcher in System32) the one named in its arguments.
                    if (key.GetValue("ImagePath") is not string image || StartupRules.ServiceProgram(image, windows) is not { } exe || !File.Exists(exe)) continue;
                    var display = key.GetValue("DisplayName") is string d && !d.StartsWith('@') ? d : name;
                    var beside = new List<string> { Path.GetFileNameWithoutExtension(exe) };
                    try { beside.AddRange(Directory.EnumerateFiles(Path.GetDirectoryName(exe)!, "*.exe").Take(30).Select(p => Path.GetFileNameWithoutExtension(p))); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    string? program = null, publisher = null;
                    try
                    {
                        var info = FileVersionInfo.GetVersionInfo(exe);
                        program = new[] { info.FileDescription, info.ProductName }.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))?.Trim();
                        publisher = string.IsNullOrWhiteSpace(info.CompanyName) ? null : info.CompanyName.Trim();
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
                    list.Add(new StartupService(name, display, exe, string.Join(' ', beside) + " " + program, program, publisher));
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return list;
    }

    /// <summary>What the started file says about itself (description, maker), and whether it is still there.</summary>
    private static StartupItem Describe(StartupItem item)
    {
        if (item.Source == StartupSource.Store) return item;
        var path = item.Source == StartupSource.Folder ? LinkTarget(item.Command) : ExePath(item.Command);
        if (path is null) return item;
        if (!Path.IsPathRooted(path))
        {
            var system = Path.Combine(Environment.SystemDirectory, path);
            if (!File.Exists(system)) return item;
            path = system;
        }
        if (!File.Exists(path)) return item with { Missing = true };
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var program = new[] { info.FileDescription, info.ProductName }.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))?.Trim();
            return item with { Program = program, Publisher = string.IsNullOrWhiteSpace(info.CompanyName) ? null : info.CompanyName.Trim() };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return item;
        }
    }

    /// <summary>Where a shortcut in a Startup folder points; the file itself when it is not a shortcut.</summary>
    private static string? LinkTarget(string file)
    {
        if (!file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return file;
        object? shell = null;
        try
        {
            if (Type.GetTypeFromProgID("WScript.Shell") is not { } type || (shell = Activator.CreateInstance(type)) is null) return null;
            dynamic link = ((dynamic)shell).CreateShortcut(file);
            string target = link.TargetPath ?? "";
            return target.Length > 0 ? target : null; // Store apps have no file behind their shortcut.
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return null;
        }
        finally
        {
            if (shell is not null) System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
        }
    }

    private static void ReadRun(string where, List<StartupItem> items)
    {
        var (hive, runKey, approvedKey) = RunPlace(where);
        try
        {
            using var root = Hive(hive);
            using var key = root.OpenSubKey(runKey);
            if (key is null) return;
            using var approved = root.OpenSubKey(approvedKey);
            foreach (var name in key.GetValueNames().Where(n => n.Length > 0))
            {
                var command = key.GetValue(name)?.ToString() ?? "";
                items.Add(new StartupItem($"run:{where}:{name}", name, ExeName(command), command, StartupSource.Registry,
                    StartupRules.IsApproved(approved?.GetValue(name) as byte[]), hive == "HKLM"));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
    }

    private static void ReadFolder(bool user, List<StartupItem> items)
    {
        try
        {
            var folder = Environment.GetFolderPath(user ? Environment.SpecialFolder.Startup : Environment.SpecialFolder.CommonStartup);
            if (!Directory.Exists(folder)) return;
            using var root = Hive(user ? "HKCU" : "HKLM");
            using var approved = root.OpenSubKey($@"{Approved}\StartupFolder");
            foreach (var file in Directory.EnumerateFiles(folder).Where(f => !Path.GetFileName(f).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)))
            {
                var name = Path.GetFileName(file);
                items.Add(new StartupItem($"folder:{(user ? "user" : "common")}:{name}", Path.GetFileNameWithoutExtension(file), name, file, StartupSource.Folder,
                    StartupRules.IsApproved(approved?.GetValue(name) as byte[]), !user));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
    }

    /// <summary>Tasks that start at sign-in or at boot, outside Windows' own \Microsoft folder.</summary>
    private static void ReadTasks(List<StartupItem> items)
    {
        try
        {
            if (Type.GetTypeFromProgID("Schedule.Service") is not { } type || Activator.CreateInstance(type) is not { } service) return;
            dynamic scheduler = service;
            scheduler.Connect();
            Walk(scheduler.GetFolder("\\"), items);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
        }
    }

    private static void Walk(dynamic folder, List<StartupItem> items)
    {
        string path = folder.Path;
        if (path.StartsWith("\\Microsoft", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            foreach (dynamic task in folder.GetTasks(1)) // 1 = include hidden tasks.
            {
                try
                {
                    var atSignIn = false;
                    foreach (dynamic trigger in task.Definition.Triggers)
                        if ((int)trigger.Type is 8 or 9) atSignIn = true; // 8 = at boot, 9 = at sign-in.
                    if (!atSignIn) continue;
                    var command = "";
                    foreach (dynamic action in task.Definition.Actions)
                        if ((int)action.Type == 0) { command = $"{action.Path} {action.Arguments}".Trim(); break; } // 0 = start a program.
                    string name = task.Name;
                    items.Add(new StartupItem($"task:{task.Path}", name, $"{name} {ExeName(command)}", command, StartupSource.Task, (bool)task.Enabled, true));
                }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
                {
                    // A task that cannot be read is not one to judge.
                }
            }
            foreach (dynamic sub in folder.GetFolders(0)) Walk(sub, items);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Store apps that start at sign-in: one key per package family, one sub-key per start-up task, with its State.</summary>
    private static void ReadStore(List<StartupItem> items)
    {
        try
        {
            using var root = Hive("HKCU");
            using var apps = root.OpenSubKey(StoreTasks);
            if (apps is null) return;
            foreach (var family in apps.GetSubKeyNames())
            {
                using var app = apps.OpenSubKey(family);
                foreach (var taskId in app?.GetSubKeyNames() ?? [])
                {
                    using var task = app!.OpenSubKey(taskId);
                    if (task?.GetValue("State") is not int state) continue;
                    items.Add(new StartupItem($"store:{family}\\{taskId}", StoreName(family), $"{family} {taskId}", $"{family} → {taskId}",
                        StartupSource.Store, StartupRules.IsStoreEnabled(state)));
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
    }

    /// <summary>"SpotifyAB.SpotifyMusic_zpdnekdrzrea0" → "SpotifyMusic".</summary>
    internal static string StoreName(string family)
    {
        var name = family.Split('_')[0];
        var dot = name.LastIndexOf('.');
        return dot >= 0 && dot < name.Length - 1 ? name[(dot + 1)..] : name;
    }

    private static string? ExePath(string command) => StartupRules.ExePath(Environment.ExpandEnvironmentVariables(command));

    /// <summary>The program file of a command line, without its folder: what the entry is recognised by.</summary>
    internal static string ExeName(string command)
    {
        var path = ExePath(command) ?? "";
        try
        {
            return Path.GetFileName(path);
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    public ActionResult SetEnabled(StartupItem item, bool enabled)
    {
        var done = enabled ? L.T($"{item.Name} riacceso all'avvio.", $"{item.Name} turned back on at start-up.")
                           : L.T($"{item.Name} spento all'avvio. Resta installato e la voce resta: si riaccende col pulsante.", $"{item.Name} turned off at start-up. It stays installed and the entry stays: it can be turned back on.");
        try
        {
            var parts = item.Id.Split(':', 3);
            switch (parts[0])
            {
                case "run" when parts.Length == 3:
                {
                    var (hive, _, approvedKey) = RunPlace(parts[1]);
                    WriteApproved(hive, approvedKey, parts[2], enabled);
                    return new ActionResult(true, done);
                }
                case "folder" when parts.Length == 3:
                    WriteApproved(parts[1] == "user" ? "HKCU" : "HKLM", $@"{Approved}\StartupFolder", parts[2], enabled);
                    return new ActionResult(true, done);
                case "task":
                {
                    var type = Type.GetTypeFromProgID("Schedule.Service")!;
                    dynamic scheduler = Activator.CreateInstance(type)!;
                    scheduler.Connect();
                    var path = item.Id["task:".Length..];
                    var cut = path.LastIndexOf('\\');
                    dynamic folder = scheduler.GetFolder(cut <= 0 ? "\\" : path[..cut]);
                    dynamic task = folder.GetTask(path[(cut + 1)..]);
                    task.Enabled = enabled;
                    return new ActionResult(true, done);
                }
                case "store":
                {
                    var key = item.Id["store:".Length..];
                    using var root = Hive("HKCU");
                    using var task = root.OpenSubKey($@"{StoreTasks}\{key}", writable: true);
                    if (task is null) return new ActionResult(false, L.T("La voce dell'app non c'è più.", "The app's entry is gone."));
                    // 1 = switched off by the person: the app itself can no longer switch it back on.
                    task.SetValue("State", enabled ? 2 : 1, RegistryValueKind.DWord);
                    return new ActionResult(true, done);
                }
                default:
                    return new ActionResult(false, L.T("Voce di avvio non riconosciuta.", "Start-up entry not recognised."));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new ActionResult(false, L.T("Serve l'amministratore: è registrato per tutti gli utenti (o lancia OptLife da amministratore).",
                "Administrator rights needed: it is registered for all users (or run OptLife as administrator)."));
        }
        catch (Exception ex) when (ex is IOException or System.Runtime.InteropServices.COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return new ActionResult(false, L.T("Non riuscita: ", "Failed: ") + ex.Message);
        }
    }

    private static void WriteApproved(string hive, string keyPath, string name, bool enabled)
    {
        using var root = Hive(hive);
        using var key = root.CreateSubKey(keyPath, writable: true);
        key.SetValue(name, StartupRules.Approved(enabled, DateTimeOffset.UtcNow), RegistryValueKind.Binary);
    }
}
