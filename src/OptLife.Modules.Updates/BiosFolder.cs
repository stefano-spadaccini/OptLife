using System.Text;
using System.Text.RegularExpressions;

namespace OptLife.Modules.Updates;

/// <summary>What was in the BIOS folder when OptLife prepared it, and the version the PC had then.</summary>
/// <param name="Seen">The BIOS file seen there, and since when (a restart after this is when the update could have happened).</param>
public sealed record BiosPrepared(string VersionBefore, DateTimeOffset At, string? Seen = null, DateTimeOffset? SeenAt = null);

/// <summary>
/// The folder for updating the BIOS (C:\BIOS by default, or the root of a USB stick): OptLife creates it with a note that says the
/// board, the version, where to download, how to flash and which settings to put back afterwards. The person puts the file there;
/// OptLife recognises it, and after the restart says whether the BIOS was updated and offers to send the files to the Recycle Bin.
/// It never downloads, flashes or deletes a BIOS by itself.
/// </summary>
public static class BiosFolder
{
    /// <summary>
    /// The note's name. Fixed, in English, starting with "OptLife-": a folder holding a file named so was made by OptLife (a convention
    /// on the data other parts read, like "game.*"), so it is not reported as something new on the drive.
    /// </summary>
    public const string Note = "OptLife-README.txt";

    public const string Prepare = "bios-folder", Clean = "bios-folder-clean";

    /// <summary>The flashing tool of each maker, entered from the BIOS.</summary>
    public static string Tool(string maker) => maker.ToUpperInvariant() switch
    {
        var m when m.Contains("ASUS") => "EZ Flash",
        var m when m.Contains("MICRO-STAR") || m.StartsWith("MSI", StringComparison.Ordinal) => "M-Flash",
        var m when m.Contains("GIGABYTE") => "Q-Flash",
        var m when m.Contains("ASROCK") => "Instant Flash",
        _ => L.T("lo strumento di aggiornamento del BIOS", "the BIOS update tool"),
    };

    private static string Norm(string s) => new([.. s.ToUpperInvariant().Where(char.IsLetterOrDigit)]);

    /// <summary>A file that may be a BIOS image: not the note, not a document, not an archive still to unzip.</summary>
    public static bool LooksLikeBios(string name)
    {
        if (name.Equals(Note, StringComparison.OrdinalIgnoreCase)) return false;
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext is not (".txt" or ".pdf" or ".url" or ".lnk" or ".ini" or ".zip" or ".7z" or ".rar" or ".exe" or "");
    }

    /// <summary>"ROG-STRIX-B550-F-GAMING-ASUS-3645.CAP" → "3645"; "B550AORUSELITE.F15" → "F15"; null when the name says no version.</summary>
    public static string? FileVersion(string name)
    {
        var ext = Path.GetExtension(name).TrimStart('.');
        if (Regex.IsMatch(ext, @"^F\d{1,3}[a-z]?$", RegexOptions.IgnoreCase)) return ext.ToUpperInvariant();
        var digits = Regex.Matches(Path.GetFileNameWithoutExtension(name), @"(?<![A-Za-z0-9])(\d{3,5})(?![A-Za-z0-9])");
        return digits.Count > 0 ? digits[^1].Groups[1].Value : null;
    }

    /// <summary>True when the file names this board, false when it names another one, null when its name says nothing (MSI codes).</summary>
    public static bool? ForBoard(string name, string board)
    {
        var model = Norm(Regex.Replace(board, @"\s*\([^)]*\)", ""));
        var file = Norm(Path.GetFileNameWithoutExtension(name));
        if (model.Length >= 4 && file.Contains(model, StringComparison.Ordinal)) return true;
        // The chipset and its letter ("B550-F", "X670E"): the part that tells boards apart.
        var key = Regex.Match(board.ToUpperInvariant(), @"[ABHXZ]\d{3}[A-Z]?(-[A-Z])?");
        if (!key.Success) return null;
        if (file.Contains(Norm(key.Value), StringComparison.Ordinal)) return true;
        return Regex.IsMatch(file, @"[ABHXZ]\d{3}") ? false : null;
    }

    /// <summary>Whether the BIOS now runs the version of the file (its number in the version Windows reads).</summary>
    public static bool Installed(string currentVersion, string fileVersion) =>
        Norm(currentVersion).Contains(Norm(fileVersion), StringComparison.Ordinal);

    /// <summary>The note, in the language of the moment.</summary>
    /// <param name="settings">The BIOS settings as Windows reads them now ("Secure Boot = on"): what to set again after the update.</param>
    public static string Readme(BiosInfo b, string? page, IReadOnlyList<string> settings, DateTimeOffset now)
    {
        var text = new StringBuilder();
        text.AppendLine(L.T("Cartella per l'aggiornamento del BIOS, preparata da OptLife", "Folder for the BIOS update, prepared by OptLife"));
        text.AppendLine(now.ToLocalTime().ToString("d MMMM yyyy HH:mm", L.Culture));
        text.AppendLine();
        text.AppendLine(L.T($"Scheda madre: {b.BoardMaker} {b.Board}", $"Motherboard: {b.BoardMaker} {b.Board}"));
        text.AppendLine(L.T($"BIOS adesso: {b.Version}", $"BIOS now: {b.Version}") + (b.Date is { } d ? L.T($" del {d.ToString("d MMM yyyy", L.Culture)}", $" from {d.ToString("d MMM yyyy", L.Culture)}") : ""));
        if (page is not null) text.AppendLine(L.T($"Pagina ufficiale: {page}", $"Official page: {page}"));
        text.AppendLine();
        text.AppendLine(L.T("1. Scarica il BIOS dalla pagina ufficiale (solo da lì) e metti il file in questa cartella. Se è uno zip, estrailo qui.",
            "1. Download the BIOS from the official page (only there) and put the file in this folder. If it is a zip, extract it here."));
        if (b.BoardMaker.Contains("ASUS", StringComparison.OrdinalIgnoreCase))
            text.AppendLine(L.T("   ASUS: con EZ Flash dal BIOS il file va bene così. Solo per USB BIOS FlashBack (il pulsante sul retro, a PC spento) va rinominato con BIOSRenamer, che è nello zip.",
                "   ASUS: with EZ Flash from the BIOS the file is fine as it is. Only for USB BIOS FlashBack (the button at the back, PC off) it must be renamed with BIOSRenamer, which is in the zip."));
        text.AppendLine(L.T($"2. Riavvia nel BIOS (OptLife: Strumenti → Riavvia nel BIOS) e apri {Tool(b.BoardMaker)}: scegli il file in questo disco.",
            $"2. Restart into the BIOS (OptLife: Tools → Restart into the BIOS) and open {Tool(b.BoardMaker)}: pick the file on this drive."));
        text.AppendLine(L.T("3. Non spegnere e non togliere la corrente finché non ha finito: il PC si riavvia da solo, a volte più volte.",
            "3. Do not switch off or cut the power until it has finished: the PC restarts by itself, sometimes more than once."));
        text.AppendLine(L.T("4. L'aggiornamento riporta le impostazioni del BIOS a quelle di fabbrica. Rimetti queste, come erano prima:",
            "4. The update puts the BIOS settings back to factory values. Set these again, as they were before:"));
        foreach (var s in settings) text.AppendLine("   - " + s);
        if (settings.Count == 0)
            text.AppendLine(L.T("   - il profilo della memoria (XMP, DOCP o EXPO) e ciò che avevi cambiato tu", "   - the memory profile (XMP, DOCP or EXPO) and what you had changed yourself"));
        text.AppendLine();
        text.AppendLine(L.T("Dopo il riavvio OptLife controlla la versione e, se è quella nuova, propone di mandare i file di questa cartella nel Cestino.",
            "After the restart OptLife checks the version and, if it is the new one, offers to send this folder's files to the Recycle Bin."));
        return text.ToString();
    }

    /// <summary>The line of the Updates page about the folder, and the state to keep.</summary>
    /// <param name="files">The names in the folder; null when the folder is not there.</param>
    /// <param name="bootedAt">When Windows last started: a restart after the file was seen is when the update could have happened.</param>
    public static (CheckItem Item, BiosPrepared? State) Board(BiosInfo b, string folder, IReadOnlyList<string>? files, BiosPrepared? prepared, DateTimeOffset now, DateTimeOffset? bootedAt)
    {
        var group = UpdatesRules.DriversGroup;
        var label = L.T("Cartella per aggiornare il BIOS", "Folder to update the BIOS");
        var prepare = new CheckButton(Prepare, null, L.T("PREPARA LA CARTELLA", "PREPARE THE FOLDER"));
        if (files is null)
            return (new CheckItem(group, label, L.T($"Non preparata\nCrea {folder} con una nota: modello, versione, dove scaricare, come aggiornare e le impostazioni da rimettere dopo. OptLife non scarica mai il BIOS.",
                $"Not prepared\nCreates {folder} with a note: model, version, where to download, how to update and the settings to put back afterwards. OptLife never downloads the BIOS."),
                CheckStatus.Info, StatusText: "", Buttons: [prepare]), prepared);

        var image = files.Where(LooksLikeBios).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        var archive = files.FirstOrDefault(f => Path.GetExtension(f).ToLowerInvariant() is ".zip" or ".7z" or ".rar");
        if (image is null)
            return (new CheckItem(group, label, (archive is not null
                    ? L.T($"In {folder} c'è {archive}: estrailo qui, il BIOS non legge gli archivi.", $"{folder} holds {archive}: extract it here, the BIOS does not read archives.")
                    : L.T($"Pronta, vuota\nMetti in {folder} il file scaricato dalla pagina ufficiale.", $"Ready, empty\nPut in {folder} the file downloaded from the official page.")),
                archive is not null ? CheckStatus.Watch : CheckStatus.Info, StatusText: "", Buttons: [prepare]), prepared);

        var seen = prepared is { Seen: { } s } && s.Equals(image, StringComparison.OrdinalIgnoreCase)
            ? prepared
            : (prepared ?? new BiosPrepared(b.Version, now)) with { Seen = image, SeenAt = now };
        var version = FileVersion(image);
        var mine = ForBoard(image, b.Board);
        var clean = new CheckButton(Clean, null, L.T("FILE NEL CESTINO", "FILES TO THE BIN"));
        if (version is not null && Installed(b.Version, version))
            return (new CheckItem(group, label, L.T($"Aggiornato: il BIOS è alla {b.Version}, quella del file {image}\nI file nella cartella non servono più: il pulsante li manda nel Cestino (la nota resta).",
                $"Updated: the BIOS is at {b.Version}, the version of {image}\nThe files in the folder are no longer needed: the button sends them to the Recycle Bin (the note stays)."),
                CheckStatus.Ok, StatusText: L.T("AGGIORNATO", "UPDATED"), Buttons: [clean]), seen);
        if (mine == false)
            return (new CheckItem(group, label, L.T($"{image}: il nome non è della tua scheda ({b.Board})\nControlla di aver scaricato il file giusto: un BIOS di un'altra scheda non va usato.",
                $"{image}: the name is not your board's ({b.Board})\nCheck you downloaded the right file: a BIOS for another board must not be used."),
                CheckStatus.Wrong, StatusText: L.T("CONTROLLA", "CHECK")), seen);
        // A restart since the file was there, and still the old version: the update did not happen (or did not finish).
        if (bootedAt is { } boot && seen.SeenAt is { } since && boot > since && Norm(b.Version) == Norm(seen.VersionBefore))
            return (new CheckItem(group, label, L.T($"Il BIOS è ancora alla {b.Version}: l'aggiornamento non risulta fatto\nRientra nel BIOS e usa {Tool(b.BoardMaker)} con {image}. Se si è fermato a metà, non spegnere: riprova lo strumento.",
                $"The BIOS is still at {b.Version}: the update does not show as done\nGo back into the BIOS and use {Tool(b.BoardMaker)} with {image}. If it stopped halfway, do not switch off: try the tool again."),
                CheckStatus.Watch, StatusText: L.T("NON FATTO", "NOT DONE"), Buttons: [new CheckButton(UpdatesRules.RestartBios, null, L.T("RIAVVIA NEL BIOS", "RESTART INTO BIOS"))]), seen);
        return (new CheckItem(group, label, L.T($"Pronto: {image}", $"Ready: {image}") + (version is null ? "" : L.T($" (versione {version})", $" (version {version})")) +
                L.T($"\nRiavvia nel BIOS e usa {Tool(b.BoardMaker)}. Dopo, OptLife controlla la versione.", $"\nRestart into the BIOS and use {Tool(b.BoardMaker)}. Afterwards OptLife checks the version.") +
                (mine is null ? L.T(" Il nome del file non dice per che scheda è: controllalo sulla pagina ufficiale.", " The file name does not say which board it is for: check it on the official page.") : ""),
            CheckStatus.Info, StatusText: L.T("PRONTO", "READY"), Buttons: [new CheckButton(UpdatesRules.RestartBios, null, L.T("RIAVVIA NEL BIOS", "RESTART INTO BIOS"))]), seen);
    }
}
