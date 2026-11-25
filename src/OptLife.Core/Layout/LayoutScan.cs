namespace OptLife.Core.Layout;

/// <summary>The launchers and stores that keep their own libraries: their games are moved from inside them, never by hand.</summary>
public enum Launcher { None, Steam, Epic, BattleNet, Ea, Xbox, Riot, Ubisoft }

/// <summary>What the machine tells about one folder at the top of a drive.</summary>
/// <param name="HasProgram">An .exe within a few levels: a program or a game, as opposed to documents or data.</param>
/// <param name="LooksLikeGame">Big, or with the files games carry (game engine, Steam API, big archives).</param>
public sealed record FolderFacts(string Path, Launcher Launcher, bool HasProgram, bool LooksLikeGame, long Bytes);

/// <summary>Something outside the folder that points inside it: an installed program, a service, a start-up entry.</summary>
public sealed record Reference(RefKind Kind, string Name, string Path);

public enum RefKind { Installed, Service, Startup }

/// <summary>
/// Safe = OptLife moves it alone. Assisted = OptLife moves it and updates or tells the launcher (Epic by itself; Battle.net and
/// Ubisoft with one click of the person). Risky = only guided steps (reinstall). Launcher = a library, left where it is. Data = not moved.
/// </summary>
public enum Risk { Safe, Assisted, Risky, Launcher, Data }

/// <summary>A folder outside the layout, with what it is and whether moving it would break something.</summary>
/// <param name="Destination">Where it belongs in the layout (only for safe ones).</param>
public sealed record OutsideItem(string Path, Risk Risk, string What, string Why, string? Destination);

/// <summary>Reads what is outside the layout and judges whether it can be moved without breaking anything.</summary>
public static class LayoutScan
{
    /// <summary>Folders at the top of a drive that belong to Windows or a store, never "out of place".</summary>
    public static readonly string[] SystemNames =
    [
        "Windows", "Program Files", "Program Files (x86)", "ProgramData", "Users", "Recovery", "PerfLogs", "Documents and Settings", "Config.Msi",
        "System Volume Information", "$Recycle.Bin", "$RECYCLE.BIN", "$WinREAgent", "$SysReset", "$Windows.~BT", "$Windows.~WS", "OneDriveTemp",
        "Intel", "AMD", "NVIDIA", "inetpub", "Drivers", "MSOCache", "WindowsApps", "WpSystem", "XboxGames", "ModifiableWindowsApps", "Programmi",
    ];

    public static string LauncherName(Launcher l) => l switch
    {
        Launcher.Steam => "Steam", Launcher.Epic => "Epic Games", Launcher.BattleNet => "Battle.net", Launcher.Ea => "EA app",
        Launcher.Xbox => L.T("app Xbox / Microsoft Store", "Xbox app / Microsoft Store"), Launcher.Riot => "Riot Client", Launcher.Ubisoft => "Ubisoft Connect", _ => "",
    };

    /// <summary>How to move a game the right way, from its own launcher.</summary>
    public static string HowToMove(Launcher l) => l switch
    {
        Launcher.Steam => L.T("Steam → Impostazioni → Archiviazione: scegli il gioco e premi Sposta (crea prima una libreria dove vuoi).",
                              "Steam → Settings → Storage: pick the game and press Move (first add a library where you want it)."),
        Launcher.Epic => L.T("Epic non ha \"Sposta\": chiudi Epic, sposta la cartella a mano, poi in Epic fai Installa sulla nuova cartella: riconosce i file e non riscarica.",
                             "Epic has no \"Move\": close Epic, move the folder by hand, then in Epic press Install on the new folder: it recognises the files and does not download again."),
        Launcher.BattleNet => L.T("Chiudi Battle.net, sposta la cartella a mano, poi nel launcher scegli il gioco → \"Individua il gioco\" e indica la nuova cartella.",
                                  "Close Battle.net, move the folder by hand, then in the launcher pick the game → \"Locate the game\" and point to the new folder."),
        Launcher.Ea => L.T("Dall'app EA: Libreria → il gioco → Gestisci → Sposta.", "From the EA app: Library → the game → Manage → Move."),
        Launcher.Xbox => L.T("Impostazioni di Windows → App → App installate → il gioco → Sposta (o dall'app Xbox: Gestisci → File → Sposta).",
                             "Windows Settings → Apps → Installed apps → the game → Move (or from the Xbox app: Manage → Files → Move)."),
        Launcher.Riot => L.T("I giochi Riot non si spostano: si disinstallano e si reinstallano scegliendo la cartella nuova.", "Riot games cannot be moved: uninstall and reinstall them choosing the new folder."),
        Launcher.Ubisoft => L.T("Ubisoft Connect: chiudilo, sposta la cartella, poi nella pagina del gioco usa \"Individua il gioco installato\".", "Ubisoft Connect: close it, move the folder, then on the game's page use \"Locate installed game\"."),
        _ => "",
    };

    /// <summary>
    /// Safe = a portable program or game (nothing outside points inside it): it can be moved and its shortcuts and OptLife's own
    /// settings follow. Risky = installed, a service, started with Windows, or a launcher's game: moving by hand breaks it.
    /// Data = no program inside: documents or files of some program; not moved by OptLife.
    /// </summary>
    /// <summary>
    /// What a move will do, in words, before it starts: size, how long (instant on the same drive), the free room of the destination
    /// before and after; a warning when the destination would be left almost full.
    /// </summary>
    public static (string Text, bool LowRoom) Preview(long bytes, bool sameDrive, string toDrive, long free, long total)
    {
        static string Size(long b) => b >= 1L << 30 ? $"{b / (double)(1L << 30):0.0} GB" : $"{Math.Max(1, b >> 20)} MB";
        var after = sameDrive ? free : free - bytes;
        var time = sameDrive ? L.T("istantaneo", "instant")
            : L.T($"circa {Math.Max(1, (int)Math.Ceiling(bytes / 150e6 / 60))} min", $"about {Math.Max(1, (int)Math.Ceiling(bytes / 150e6 / 60))} min");
        // Almost full: under a tenth of the drive or 20 GB, whichever is smaller.
        var low = !sameDrive && total > 0 && after < Math.Min(total / 10, 20L << 30);
        var text = L.T($"{Size(bytes)} · {time} · {toDrive} {Size(free)} liberi → {Size(Math.Max(0, after))} dopo",
                       $"{Size(bytes)} · {time} · {toDrive} {Size(free)} free → {Size(Math.Max(0, after))} after");
        if (low) text += L.T(" · il disco resterebbe quasi pieno", " · the drive would be left almost full");
        return (text, low);
    }

    public static OutsideItem Judge(FolderFacts f, IReadOnlyList<Reference> refs, LayoutChoices choices)
    {
        var name = Path.GetFileName(f.Path.TrimEnd('\\'));
        if (f.Launcher is Launcher.Steam or Launcher.Xbox && !f.HasProgram || f.Launcher == Launcher.Steam)
            return new OutsideItem(f.Path, Risk.Launcher, L.T($"Libreria di {LauncherName(f.Launcher)}", $"{LauncherName(f.Launcher)} library"),
                L.T("I giochi qui li gestisce il launcher: resta dov'è. ", "The launcher manages the games here: it stays where it is. ") + HowToMove(f.Launcher), null);
        if (f.Launcher is Launcher.Epic or Launcher.BattleNet or Launcher.Ubisoft)
            return new OutsideItem(f.Path, Risk.Assisted, L.T($"Gioco di {LauncherName(f.Launcher)}", $"{LauncherName(f.Launcher)} game"),
                f.Launcher == Launcher.Epic
                    ? L.T("OptLife lo sposta e aggiorna da solo i registri di Epic (dopo averne fatto una copia); poi in Epic premi Verifica sul gioco.",
                          "OptLife moves it and updates Epic's records by itself (after copying them); then press Verify on the game in Epic.")
                    : L.T($"OptLife lo sposta; poi un clic tuo: {HowToMove(f.Launcher)}", $"OptLife moves it; then one click of yours: {HowToMove(f.Launcher)}"),
                Path.Combine(LayoutPaths.Expand(choices.Games), name));
        if (f.Launcher != Launcher.None)
            return new OutsideItem(f.Path, Risk.Risky, L.T($"Gioco di {LauncherName(f.Launcher)}", $"{LauncherName(f.Launcher)} game"),
                L.T("Spostato a mano, il launcher non lo trova più e lo riscarica. ", "Moved by hand, the launcher no longer finds it and downloads it again. ") + HowToMove(f.Launcher), null);

        var inside = refs.Where(r => LayoutPaths.Within(r.Path, f.Path)).ToList();
        if (inside.FirstOrDefault(r => r.Kind == RefKind.Service) is { } service)
            return new OutsideItem(f.Path, Risk.Risky, L.T($"Programma con un servizio di Windows ({service.Name})", $"Program with a Windows service ({service.Name})"),
                L.T("Windows avvia il servizio da questo percorso: spostandolo, il servizio non parte più. Si sposta disinstallandolo e reinstallandolo nella cartella nuova.",
                    "Windows starts the service from this path: moved, the service no longer starts. Uninstall it and reinstall it into the new folder."), null);
        if (inside.FirstOrDefault(r => r.Kind == RefKind.Installed) is { } installed)
            return new OutsideItem(f.Path, Risk.Risky, L.T($"Programma installato ({installed.Name})", $"Installed program ({installed.Name})"),
                L.T("Windows sa che è qui (disinstallazione, aggiornamenti, registro): spostato a mano smette di aggiornarsi o non si disinstalla più. Disinstallalo e reinstallalo scegliendo la cartella nuova.",
                    "Windows knows it is here (uninstall, updates, registry): moved by hand it stops updating or can no longer be uninstalled. Uninstall it and reinstall it choosing the new folder."), null);
        if (inside.FirstOrDefault(r => r.Kind == RefKind.Startup) is { } startup)
            return new OutsideItem(f.Path, Risk.Risky, L.T($"Parte con Windows ({startup.Name})", $"Starts with Windows ({startup.Name})"),
                L.T("La voce di avvio punta qui: dopo uno spostamento non partirebbe più. Spegni l'avvio automatico nel programma, spostalo, poi riaccendilo.",
                    "The start-up entry points here: after a move it would no longer start. Turn off its start-up in the program, move it, then turn it back on."), null);
        if (!f.HasProgram)
            return new OutsideItem(f.Path, Risk.Data, L.T("Dati o documenti", "Data or documents"),
                L.T("Non contiene programmi: OptLife non la sposta. Se è al posto sbagliato spostala tu, dopo aver controllato che nessun programma la usi (copie, sincronizzazione, progetti).",
                    "It holds no programs: OptLife does not move it. If it is in the wrong place move it yourself, after checking no program uses it (backups, sync, projects)."), null);

        var game = f.LooksLikeGame;
        var destination = Path.Combine(LayoutPaths.Expand(game ? $@"{choices.Games}\Repack" : choices.Software), name);
        return new OutsideItem(f.Path, Risk.Safe, game ? L.T("Gioco portatile", "Portable game") : L.T("Programma portatile", "Portable program"),
            L.T("Nessun programma installato, servizio o voce di avvio punta qui: si può spostare. OptLife aggiorna i collegamenti (Desktop, Hub, Start) e le sue impostazioni che lo nominano, e lo spostamento si può annullare.",
                "No installed program, service or start-up entry points here: it can be moved. OptLife updates the shortcuts (Desktop, Hub, Start) and its own settings that name it, and the move can be undone."),
            destination);
    }
}

/// <summary>Epic's records name each game's folder: after a move they must name the new one.</summary>
public static class LayoutEpic
{
    /// <summary>The folder's path replaced in the text, in the JSON form Epic writes ("E:\\\\Game"), and in the plain one.</summary>
    public static string Rebase(string text, string from, string to)
    {
        static string Json(string p) => p.TrimEnd('\\').Replace("\\", "\\\\");
        var updated = text;
        foreach (var (a, b) in new[] { (Json(from), Json(to)), (from.TrimEnd('\\'), to.TrimEnd('\\')), (from.TrimEnd('\\').Replace('\\', '/'), to.TrimEnd('\\').Replace('\\', '/')) })
            updated = System.Text.RegularExpressions.Regex.Replace(updated, System.Text.RegularExpressions.Regex.Escape(a) + @"(?=[\\/""]|$)", b.Replace("$", "$$"),
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Multiline);
        return updated;
    }
}
