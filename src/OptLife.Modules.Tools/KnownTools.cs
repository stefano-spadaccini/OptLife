using OptLife.Core;

namespace OptLife.Modules.Tools;

/// <summary>
/// A program many PCs have that is worth a button on the Tools page: it shows only where it is found. Nothing here is personal:
/// the person's own scripts stay in their "tools" list.
/// </summary>
/// <param name="Places">Where its program usually is (with %ProgramFiles% and the like); the first that exists wins.</param>
/// <param name="Search">Folders looked through (two levels) for <paramref name="FileName"/>, for programs that are unzipped, not installed (DDU).</param>
/// <param name="Admin">It needs administrator rights: started with them only when its file cannot be changed by a non-administrator; otherwise Windows asks.</param>
public sealed record KnownTool(string Id, string Group, string Name, string Description, IReadOnlyList<string> Places,
    string? FileName = null, IReadOnlyList<string>? Search = null, bool Admin = false);

/// <summary>A cache folder of the graphics driver that can be emptied safely: the driver builds it again as games run.</summary>
public sealed record ShaderCache(string Path, long Bytes, int Items);

public static class KnownTools
{
    public const string ShaderCacheId = "nvidia-shader-cache";

    private static string Drivers => L.T("Driver video", "Graphics driver");
    private static string Audio => "Audio";
    private static string Monitor => L.T("Controllo del PC", "PC checks");

    public static IReadOnlyList<KnownTool> Catalog =>
    [
        new("ddu", Drivers, L.T("Pulizia driver (DDU)", "Driver cleanup (DDU)"),
            L.T("Display Driver Uninstaller: toglie del tutto il driver video, da usare quando un'installazione normale non basta. Scegli il riavvio in modalità provvisoria, poi \"Pulisci e riavvia\". Prima scarica il driver nuovo e stacca internet.",
                "Display Driver Uninstaller: removes the graphics driver completely, for when a normal install is not enough. Choose to restart in safe mode, then \"Clean and restart\". First download the new driver and unplug the internet."),
            [@"%ProgramFiles%\Display Driver Uninstaller\Display Driver Uninstaller.exe", @"%ProgramFiles(x86)%\Display Driver Uninstaller\Display Driver Uninstaller.exe"],
            "Display Driver Uninstaller.exe", [@"%USERPROFILE%\Downloads", @"%USERPROFILE%\Desktop", @"%USERPROFILE%\Documents"], Admin: true),
        new("nvidia-app", Drivers, "NVIDIA app",
            L.T("Driver, impostazioni dei giochi e registrazione della scheda NVIDIA.", "Drivers, game settings and recording for the NVIDIA card."),
            [@"%ProgramFiles%\NVIDIA Corporation\NVIDIA app\CEF\NVIDIA app.exe", @"%ProgramFiles%\NVIDIA Corporation\NVIDIA App\CEF\NVIDIA App.exe"]),
        new("amd-software", Drivers, "AMD Software: Adrenalin",
            L.T("Driver e impostazioni della scheda AMD.", "Drivers and settings of the AMD card."),
            [@"%ProgramFiles%\AMD\CNext\CNext\RadeonSoftware.exe"]),
        new("eq-selector", Audio, L.T("Equalizer APO: scegli i dispositivi", "Equalizer APO: choose devices"),
            L.T("Dove spunti l'uscita audio a cui Equalizer APO si attacca (dopo un aggiornamento di Windows a volte va rifatto). Poi riavvia il PC.",
                "Where you tick the audio output Equalizer APO attaches to (after a Windows update it sometimes needs redoing). Then restart the PC."),
            [@"%ProgramFiles%\EqualizerAPO\DeviceSelector.exe", @"%ProgramFiles%\EqualizerAPO\Configurator.exe"], Admin: true),
        new("eq-editor", Audio, L.T("Equalizer APO: modifica l'equalizzazione", "Equalizer APO: edit the equalisation"),
            L.T("L'editor della configurazione di Equalizer APO.", "Equalizer APO's configuration editor."),
            [@"%ProgramFiles%\EqualizerAPO\Editor.exe"]),
        new("afterburner", Monitor, "MSI Afterburner",
            L.T("Temperature, frequenze e curva delle ventole; da qui OptLife legge la temperatura del processore.", "Temperatures, clocks and the fan curve; OptLife reads the processor temperature from here."),
            [@"%ProgramFiles(x86)%\MSI Afterburner\MSIAfterburner.exe"], Admin: true),
        new("hwinfo", Monitor, "HWiNFO",
            L.T("Tutti i sensori del PC, uno per uno.", "Every sensor of the PC, one by one."),
            [@"%ProgramFiles%\HWiNFO64\HWiNFO64.EXE", @"%ProgramFiles%\HWiNFO64\HWiNFO64.exe"]),
        new("crystaldiskinfo", Monitor, "CrystalDiskInfo",
            L.T("La salute dei dischi (S.M.A.R.T.): ore di lavoro, settori rovinati, temperatura.", "Disk health (S.M.A.R.T.): hours of use, bad sectors, temperature."),
            [@"%ProgramFiles%\CrystalDiskInfo\DiskInfo64.exe", @"%ProgramFiles(x86)%\CrystalDiskInfo\DiskInfo64.exe"]),
    ];

    /// <summary>The shader cache button: only where the NVIDIA driver keeps one.</summary>
    public static KnownTool ShaderCacheTool => new(ShaderCacheId, Drivers, L.T("Svuota la cache degli shader NVIDIA", "Empty the NVIDIA shader cache"),
        L.T("Manda al Cestino la cache degli shader del driver: utile dopo un cambio di driver se un gioco scatta o si chiude. Il driver la ricrea, quindi la prima partita può caricare un po' di più. Mostra prima quanto pesa.",
            "Sends the driver's shader cache to the Recycle Bin: useful after a driver change when a game stutters or crashes. The driver builds it again, so the first game may load a little longer. It first shows how much it weighs."),
        []);

    /// <summary>Where the NVIDIA driver keeps its shader caches, for the person signed in.</summary>
    public static IReadOnlyList<string> ShaderCacheFolders =>
    [
        @"%LOCALAPPDATA%\NVIDIA\DXCache", @"%LOCALAPPDATA%\NVIDIA\GLCache", @"%LOCALAPPDATA%\NVIDIA\OptixCache",
        @"%USERPROFILE%\AppData\LocalLow\NVIDIA\PerDriverVersion\DXCache", @"%USERPROFILE%\AppData\LocalLow\NVIDIA\PerDriverVersion\GLCache",
    ];

    /// <summary>
    /// The known tools found on this PC, each with its file. Left out: those the person already has among their own tools (the same
    /// file or the same name) and those they hid.
    /// </summary>
    public static IReadOnlyList<(KnownTool Tool, string File)> Found(IToolsEnvironment env, IReadOnlyList<Tool> own, IReadOnlyList<string> hidden)
    {
        var list = new List<(KnownTool, string)>();
        foreach (var t in Catalog)
        {
            if (hidden.Contains(t.Id, StringComparer.OrdinalIgnoreCase)) continue;
            var file = t.Places.Select(Environment.ExpandEnvironmentVariables).FirstOrDefault(env.FileExists)
                       ?? (t.FileName is { } name && t.Search is { } search ? search.Select(s => env.FindFile(Environment.ExpandEnvironmentVariables(s), name)).FirstOrDefault(f => f is not null) : null);
            if (file is null) continue;
            if (own.Any(o => Environment.ExpandEnvironmentVariables(o.File).Equals(file, StringComparison.OrdinalIgnoreCase)
                             || Path.GetFileName(Environment.ExpandEnvironmentVariables(o.File)).Equals(Path.GetFileName(file), StringComparison.OrdinalIgnoreCase))) continue;
            list.Add((t, file));
        }
        return list;
    }

    public static IReadOnlyList<ActionChoice> CacheChoices(IReadOnlyList<ShaderCache> caches) =>
        [.. caches.Where(c => c.Items > 0).Select(c => new ActionChoice(c.Path, Environment.ExpandEnvironmentVariables(c.Path),
            L.T($"{c.Items} elementi, {Size(c.Bytes)}", $"{c.Items} items, {Size(c.Bytes)}"), Checked: true))];

    public static string Size(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{Math.Max(1, bytes >> 20)} MB";
}
