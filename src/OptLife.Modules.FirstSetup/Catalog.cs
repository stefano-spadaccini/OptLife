using OptLife.Core;

namespace OptLife.Modules.FirstSetup;

public enum Vendor { Unknown, Amd, Intel, Nvidia }

/// <summary>What decides which drivers a PC needs.</summary>
public sealed record Hardware(Vendor Cpu, IReadOnlyList<Vendor> Gpus)
{
    public bool HasGpu(Vendor v) => Gpus.Contains(v);
}

/// <summary>What the person uses the PC for: each one adds its own step.</summary>
public enum Use { All, Music, Games, Study, Entertainment, Emulation, Aesthetics, Creativity }

/// <param name="Id">Stable name, used in settings ("skip") and as the action argument.</param>
/// <param name="Step">Index into <see cref="Catalog.Steps"/>: the order things are installed in.</param>
/// <param name="Winget">Package id for winget; null when only the official site has it (or it is a Windows page).</param>
/// <param name="Url">Official page: download, or the Windows settings page for things done by hand.</param>
/// <param name="Tier">Essential (everyone needs it), Recommended (ticked, can be left out) or Optional (not ticked).</param>
/// <param name="Match">How to recognise it among installed programs: part of the name, or "=name" for the whole name.
/// Empty when it cannot be seen as a program (Windows Update).</param>
public sealed record App(
    string Id, int Step, string Name, string Why, string? Winget, string Url, Tier Tier,
    IReadOnlyList<string> Match, Use Use = Use.All, Func<Hardware, bool>? When = null, Use? RaisedBy = null)
{
    /// <summary>Ticked at first in the install list, and shown as missing (not just possible) when it is not there.</summary>
    public bool Essential => Tier != Tier.Optional;
}

/// <summary>How much a new PC needs an app; the panel's words are "Essenziale", "Consigliata", "Facoltativa".</summary>
public enum Tier { Essential, Recommended, Optional }

/// <summary>
/// The first-setup list for a brand-new PC, in a sensible order: Windows and drivers first (they may need
/// a restart and everything else sits on them), then the basics, the tools OptLife reads, and one step per use.
/// Pure: what is installed comes from outside.
/// </summary>
public static class Catalog
{
    public static string[] Steps =>
    [
        L.T("Windows e driver", "Windows and drivers"),
        L.T("Programmi di base", "Everyday essentials"),
        "Browser",
        L.T("Monitoraggio e utilità", "Monitoring and utilities"),
        L.T("Intrattenimento", "Entertainment"),
        L.T("Produzione musicale", "Music production"),
        "Gaming",
        L.T("Emulazione", "Emulation"),
        L.T("Studio", "Study"),
        L.T("Extra per interessi", "Extras by interest"),
    ];

    public const string WindowsUpdate = "windows-update";

    public static IReadOnlyList<App> All =>
    [
        // ── 1. Windows and drivers: before anything else, restarting when asked ──
        new(WindowsUpdate, 0, "Windows Update",
            L.T("Per primo, finché non resta più niente da installare (anche gli aggiornamenti facoltativi dei driver). Riavvia ogni volta che lo chiede.",
                "First of all, until nothing is left to install (optional driver updates too). Restart every time it asks."),
            null, "ms-settings:windowsupdate", Tier.Essential, []),
        new("amd-chipset", 0, L.T("Driver chipset AMD", "AMD chipset driver"),
            L.T("Fa lavorare bene processore e scheda madre (risparmio energetico, USB, frequenze). Sul sito: scegli il tuo chipset (es. B650, X570) e scarica \"AMD Chipset Software\".",
                "Makes the processor and motherboard work properly (power saving, USB, clocks). On the site: pick your chipset (e.g. B650, X570) and download \"AMD Chipset Software\"."),
            null, "https://www.amd.com/en/support/download/drivers.html", Tier.Essential, ["AMD Chipset Software"], When: h => h.Cpu == Vendor.Amd),
        new("intel-dsa", 0, "Intel Driver & Support Assistant",
            L.T("Trova e installa i driver Intel giusti per questo PC (chipset, rete, Wi-Fi, grafica integrata).",
                "Finds and installs the right Intel drivers for this PC (chipset, network, Wi-Fi, integrated graphics)."),
            "Intel.IntelDriverAndSupportAssistant", "https://www.intel.com/content/www/us/en/support/detect.html", Tier.Essential,
            ["Driver & Support Assistant"], When: h => h.Cpu == Vendor.Intel || h.HasGpu(Vendor.Intel)),
        new("nvidia-app", 0, L.T("NVIDIA app (driver video)", "NVIDIA app (graphics driver)"),
            L.T("Installa e aggiorna il driver della scheda video. Scegli l'installazione normale (\"Express\"), non quella personalizzata, e riavvia dopo.",
                "Installs and updates the graphics driver. Choose the normal (\"Express\") install, not the custom one, and restart afterwards."),
            null, "https://www.nvidia.com/en-us/software/nvidia-app/", Tier.Essential, ["NVIDIA app"], When: h => h.HasGpu(Vendor.Nvidia)),
        new("amd-adrenalin", 0, L.T("AMD Software: Adrenalin (driver video)", "AMD Software: Adrenalin (graphics driver)"),
            L.T("Driver della scheda video Radeon. Sul sito: \"Download Windows Drivers\" con il rilevamento automatico, poi riavvia.",
                "Driver for the Radeon graphics card. On the site: \"Download Windows Drivers\" with auto-detect, then restart."),
            null, "https://www.amd.com/en/support/download/drivers.html", Tier.Essential, ["AMD Software"], When: h => h.HasGpu(Vendor.Amd)),

        // ── 2. Essentials (researched 23 nov 2026: one browser proposed, a password manager only recommended) ──
        new("firefox", 2, "Firefox",
            L.T("Il browser consigliato: l'unico con un motore tutto suo (non Chrome), attento alla privacy. Edge c'è già in Windows; uno basta.",
                "The recommended browser: the only one with an engine of its own (not Chrome), mindful of privacy. Edge is already in Windows; one is enough."),
            "Mozilla.Firefox", "https://www.mozilla.org/firefox/", Tier.Recommended, ["Mozilla Firefox"]),
        new("brave", 2, "Brave",
            L.T("In alternativa a Firefox: compatibile con i siti fatti per Chrome e blocca da sé pubblicità e tracciatori. Le sue funzioni cripto e IA si possono spegnere.",
                "Instead of Firefox: works with sites made for Chrome and blocks ads and trackers by itself. Its crypto and AI features can be switched off."),
            "Brave.Brave", "https://brave.com/download/", Tier.Optional, ["Brave"]),
        new("vivaldi", 2, "Vivaldi",
            L.T("Basato su Chromium come Chrome, molto personalizzabile (schede verticali, pannelli) e senza tracciamento; per chi vuole tante funzioni.",
                "Chromium-based like Chrome, very customisable (vertical tabs, panels) and without tracking; for those who want many features."),
            "Vivaldi.Vivaldi", "https://vivaldi.com/download/", Tier.Optional, ["Vivaldi"]),
        new("chrome", 2, "Google Chrome",
            L.T("Il più compatibile, ma il più pesante in memoria e manda i dati a Google. Solo se ti serve.",
                "The most compatible, but the heaviest on memory and it sends data to Google. Only if you need it."),
            "Google.Chrome", "https://www.google.com/chrome/", Tier.Optional, ["Google Chrome"]),
        new("opera-gx", 2, "Opera GX",
            L.T("Browser per giocatori con limiti di RAM e CPU; pieno di extra e pubblicità dei partner: meno consigliato.",
                "A gamers' browser with RAM and CPU limits; full of extras and partner promotions: less recommended."),
            "Opera.OperaGX", "https://www.opera.com/gx", Tier.Optional, ["Opera GX"]),
        new("bitwarden", 1, "Bitwarden",
            L.T("Gestore delle password gratuito e open source: una password diversa per ogni sito senza doverle ricordare. Da fare prima di creare gli account.",
                "Free, open-source password manager: a different password for every site without remembering them. Set it up before creating accounts."),
            "Bitwarden.Bitwarden", "https://bitwarden.com/download/", Tier.Recommended, ["Bitwarden"]),
        new("proton-pass", 1, "Proton Pass",
            L.T("In alternativa a Bitwarden: gratuito, open source, molto attento alla privacy.", "Instead of Bitwarden: free, open source, very mindful of privacy."),
            "Proton.ProtonPass", "https://proton.me/pass/download", Tier.Optional, ["Proton Pass"]),
        new("7zip", 1, "7-Zip", L.T("Apre ogni tipo di archivio (.zip, .rar, .7z).", "Opens every kind of archive (.zip, .rar, .7z)."),
            "7zip.7zip", "https://www.7-zip.org/", Tier.Essential, ["7-Zip"]),
        new("nanazip", 1, "NanaZip",
            L.T("Come 7-Zip, con l'aspetto e il menu di Windows 11. Uno dei due basta.", "Like 7-Zip, with the Windows 11 look and menu. One of the two is enough."),
            "M2Team.NanaZip", "https://github.com/M2Team/NanaZip", Tier.Optional, ["NanaZip"]),
        new("vlc", 1, "VLC", L.T("Riproduce qualsiasi video o audio.", "Plays any video or audio file."),
            "VideoLAN.VLC", "https://www.videolan.org/vlc/", Tier.Recommended, ["VLC media player"]),
        new("everything", 3, "Everything", L.T("Trova qualunque file del PC all'istante, mentre scrivi il nome.", "Finds any file on the PC instantly, as you type its name."),
            "voidtools.Everything", "https://www.voidtools.com/", Tier.Recommended, ["Everything"]),
        new("powertoys", 1, "PowerToys",
            L.T("Strumenti di Microsoft: finestre affiancate a zone, rinomina in blocco, selettore colori e altro. Quelli che non usi si spengono.",
                "Microsoft's tools: zoned window layouts, bulk rename, colour picker and more. The ones you do not use can be switched off."),
            "Microsoft.PowerToys", "https://learn.microsoft.com/windows/powertoys/install", Tier.Recommended, ["PowerToys"]),

        // ── 3. What OptLife reads to see the PC (recommended when they matter for the chosen uses) ──
        new("afterburner", 3, "MSI Afterburner",
            L.T("Temperatura e consumo del processore e della scheda video: OptLife li legge da qui (va lasciato aperto, si avvia con Windows).",
                "Processor and graphics card temperature and power: OptLife reads them from here (leave it running, it starts with Windows)."),
            "Guru3D.Afterburner", "https://www.msi.com/Landing/afterburner/graphics-cards", Tier.Optional, ["MSI Afterburner"], RaisedBy: Use.Games),
        new("rtss", 3, "RivaTuner Statistics Server",
            L.T("Conta i frame dei giochi: senza, OptLife non vede FPS e scatti.", "Counts the frames of games: without it OptLife cannot see FPS and stutters."),
            "Guru3D.RTSS", "https://www.guru3d.com/download/rtss-rivatuner-statistics-server-download/", Tier.Optional, ["RivaTuner Statistics Server"], RaisedBy: Use.Games),
        new("hwinfo", 3, "HWiNFO",
            L.T("Tutti i sensori del PC in dettaglio, per quando c'è qualcosa da capire.", "Every sensor of the PC in detail, for when something needs looking into."),
            "REALiX.HWiNFO", "https://www.hwinfo.com/download/", Tier.Recommended, ["HWiNFO"]),
        new("latencymon", 3, "LatencyMon",
            L.T("Prima di registrare: dice se qualche driver blocca il PC per troppo tempo, la causa di crepitii e interruzioni nell'audio. Lancialo 5 minuti a PC acceso.",
                "Before recording: tells whether a driver stalls the PC for too long, the cause of crackles and dropouts in audio. Run it for 5 minutes."),
            "Resplendence.LatencyMon", "https://www.resplendence.com/latencymon", Tier.Optional, ["LatencyMon"], RaisedBy: Use.Music),

        // ── 4. Music (free Cakewalk by BandLab is gone since 2024: its successors need a BandLab account) ──
        new("lmms", 5, "LMMS",
            L.T("Programma per fare musica (DAW) gratuito e senza account, ottimo per beat ed elettronica. Non registra la voce o gli strumenti: per quelli REAPER o Audacity.",
                "Free music-making program (DAW), no account, great for beats and electronic music. It does not record voice or instruments: use REAPER or Audacity for that."),
            "LMMS.LMMS", "https://lmms.io/download", Tier.Recommended, ["LMMS"], Use.Music),
        new("reaper", 5, "REAPER",
            L.T("DAW completa e leggerissima, registra anche voce e strumenti; prova gratuita di 60 giorni, poi una licenza economica (60 $).",
                "Complete, very light DAW that also records voice and instruments; 60-day free trial, then a cheap licence ($60)."),
            "Cockos.REAPER", "https://www.reaper.fm/download.php", Tier.Recommended, ["REAPER"], Use.Music),
        new("cakewalk", 5, "Cakewalk (Sonar / Next)",
            L.T("Le DAW di BandLab: hanno un piano gratuito con qualche limite e serve un account BandLab. Si scaricano dal sito.",
                "BandLab's DAWs: they have a free plan with some limits and need a BandLab account. Downloaded from the site."),
            null, "https://www.cakewalk.com/", Tier.Optional, ["Cakewalk"], Use.Music),
        new("waveform", 5, "Waveform Free",
            L.T("DAW gratuita di Tracktion senza limiti di tracce; si scarica dal sito con un account gratuito.",
                "Tracktion's free DAW with no track limit; downloaded from the site with a free account."),
            null, "https://www.tracktion.com/products/waveform-free", Tier.Optional, ["Waveform"], Use.Music),
        new("fl-studio", 5, "FL Studio",
            L.T("La DAW più usata per beat ed elettronica; versione di prova gratuita senza scadenza.",
                "The most used DAW for beats and electronic music; free trial with no time limit."),
            null, "https://www.image-line.com/fl-studio-download/", Tier.Optional, ["FL Studio"], Use.Music),
        new("ableton", 5, "Ableton Live",
            L.T("DAW molto usata dal vivo e per l'elettronica; prova gratuita di 30 giorni.",
                "DAW widely used live and for electronic music; 30-day free trial."),
            null, "https://www.ableton.com/en/trial/", Tier.Optional, ["Ableton Live"], Use.Music),
        new("audacity", 5, "Audacity", L.T("Registrare e tagliare audio al volo.", "Quick audio recording and editing."),
            "Audacity.Audacity", "https://www.audacityteam.org/download/", Tier.Recommended, ["Audacity"], Use.Music),
        new("asio4all", 5, "ASIO4ALL",
            L.T("Solo se NON hai una scheda audio esterna: abbassa il ritardo dell'audio integrato. Con una scheda audio usa il driver del suo produttore.",
                "Only if you have NO external audio interface: lowers the delay of built-in audio. With an interface, use its maker's driver."),
            "MichaelTippach.ASIO4ALL", "https://asio4all.org/", Tier.Optional, ["ASIO4ALL"], Use.Music),
        new("spotify", 4, "Spotify", L.T("Musica in streaming.", "Music streaming."),
            "Spotify.Spotify", "https://www.spotify.com/download/windows/", Tier.Recommended, ["Spotify"], Use.Entertainment),

        // ── 5. Gaming ──
        new("steam", 6, "Steam", L.T("Il negozio e launcher principale dei giochi per PC.", "The main PC game store and launcher."),
            "Valve.Steam", "https://store.steampowered.com/about/", Tier.Essential, ["=Steam"], Use.Games),
        new("discord", 6, "Discord", L.T("Chat vocale e testo con gli amici.", "Voice and text chat with friends."),
            "Discord.Discord", "https://discord.com/download", Tier.Recommended, ["=Discord"], Use.Games),
        new("epic", 6, "Epic Games Launcher", L.T("Giochi Epic e i giochi gratuiti di ogni settimana.", "Epic games and the weekly free games."),
            "EpicGames.EpicGamesLauncher", "https://store.epicgames.com/download", Tier.Optional, ["Epic Games Launcher"], Use.Games),
        new("battlenet", 6, "Battle.net", L.T("Giochi Blizzard (Overwatch, Diablo, WoW).", "Blizzard games (Overwatch, Diablo, WoW)."),
            "Blizzard.BattleNet", "https://download.battle.net/", Tier.Optional, ["Battle.net"], Use.Games),
        new("ea-app", 6, "EA app", L.T("Giochi EA (EA FC, Battlefield, Apex).", "EA games (EA FC, Battlefield, Apex)."),
            "ElectronicArts.EADesktop", "https://www.ea.com/ea-app", Tier.Optional, ["EA app"], Use.Games),
        new("ubisoft", 6, "Ubisoft Connect", L.T("Giochi Ubisoft.", "Ubisoft games."),
            "Ubisoft.Connect", "https://ubisoftconnect.com/", Tier.Optional, ["Ubisoft Connect"], Use.Games),
        new("obs", 6, "OBS Studio", L.T("Registrare o trasmettere lo schermo.", "Record or stream the screen."),
            "OBSProject.OBSStudio", "https://obsproject.com/download", Tier.Optional, ["OBS Studio"], Use.Games),

        // ── 6. Study ──
        new("libreoffice", 8, "LibreOffice",
            L.T("Documenti, fogli di calcolo e presentazioni, gratis. Se la scuola ti dà Microsoft 365, usa quello.",
                "Documents, spreadsheets and presentations, free. If your school gives you Microsoft 365, use that instead."),
            "TheDocumentFoundation.LibreOffice", "https://www.libreoffice.org/download/", Tier.Recommended, ["LibreOffice"], Use.Study),
        new("office", 8, "Microsoft 365",
            L.T("Word, Excel, PowerPoint: spesso gratis con l'account della scuola. Entra su office.com con quell'account e scarica da lì.",
                "Word, Excel, PowerPoint: often free with a school account. Sign in at office.com with it and download from there."),
            null, "https://www.office.com/", Tier.Optional, ["Microsoft 365", "Microsoft Office"], Use.Study),
        new("obsidian", 8, "Obsidian", L.T("Appunti collegati tra loro, salvati come file sul PC; gratis per uso personale.", "Linked notes, saved as files on your PC; free for personal use."),
            "Obsidian.Obsidian", "https://obsidian.md/download", Tier.Recommended, ["Obsidian"], Use.Study),
        new("anki", 8, "Anki",
            L.T("Flashcard a ripetizione dilazionata: il modo più efficace per memorizzare per gli esami. Gratis e open source.",
                "Spaced-repetition flashcards: the most effective way to memorise for exams. Free and open source."),
            "Anki.Anki", "https://apps.ankiweb.net/", Tier.Recommended, ["Anki"], Use.Study),
        new("zotero", 8, "Zotero",
            L.T("Raccoglie le fonti e scrive da solo bibliografia e citazioni (tesine, tesi). Gratis e open source.",
                "Collects your sources and writes the bibliography and citations by itself (essays, theses). Free and open source."),
            "DigitalScholar.Zotero", "https://www.zotero.org/download/", Tier.Recommended, ["Zotero"], Use.Study),
        new("sumatra", 8, "SumatraPDF", L.T("Lettore PDF ed e-book leggerissimo.", "Very light PDF and e-book reader."),
            "SumatraPDF.SumatraPDF", "https://www.sumatrapdfreader.org/download-free-pdf-viewer", Tier.Recommended, ["SumatraPDF"], Use.Study),
        new("notion", 8, "Notion", L.T("Appunti e organizzazione online; piano Plus gratis con un'e-mail scolastica.", "Online notes and planning; the Plus plan is free with a school e-mail."),
            "Notion.Notion", "https://www.notion.com/desktop", Tier.Optional, ["Notion"], Use.Study),
        new("zoom", 8, "Zoom", L.T("Lezioni e riunioni online.", "Online lessons and meetings."),
            "Zoom.Zoom", "https://zoom.us/download", Tier.Optional, ["Zoom"], Use.Study),

        // ── Entertainment: music and video streaming (the Store apps are installed from the Store) ──
        new("apple-music", 4, "Apple Music", L.T("Musica in streaming di Apple, app ufficiale dal Microsoft Store.", "Apple's music streaming, official app from the Microsoft Store."),
            null, "https://apps.microsoft.com/detail/9PFHDD62MXS1", Tier.Optional, [], Use.Entertainment),
        new("netflix", 4, "Netflix", L.T("App ufficiale dal Microsoft Store (scarica i film per vederli senza rete).", "Official app from the Microsoft Store (downloads films to watch offline)."),
            null, "https://apps.microsoft.com/detail/9WZDNCRFJ3TJ", Tier.Optional, [], Use.Entertainment),
        new("prime-video", 4, "Prime Video", L.T("App ufficiale dal Microsoft Store.", "Official app from the Microsoft Store."),
            null, "https://apps.microsoft.com/detail/9P6RC76MSMMJ", Tier.Optional, [], Use.Entertainment),
        new("disney-plus", 4, "Disney+", L.T("App ufficiale dal Microsoft Store.", "Official app from the Microsoft Store."),
            null, "https://apps.microsoft.com/detail/9NXQXXLFST89", Tier.Optional, [], Use.Entertainment),
        new("apple-tv", 4, "Apple TV", L.T("Film e serie di Apple TV+, app ufficiale dal Microsoft Store.", "Apple TV+ films and series, official app from the Microsoft Store."),
            null, "https://apps.microsoft.com/detail/9NM4T8B9JQZ1", Tier.Optional, [], Use.Entertainment),
        new("crunchyroll", 4, "Crunchyroll", L.T("Anime in streaming, app ufficiale dal Microsoft Store.", "Anime streaming, official app from the Microsoft Store."),
            null, "https://apps.microsoft.com/detail/9WZDNCRFJ15T", Tier.Optional, [], Use.Entertainment),
        new("deezer", 4, "Deezer", L.T("Musica in streaming, app ufficiale dal Microsoft Store.", "Music streaming, official app from the Microsoft Store."),
            null, "https://apps.microsoft.com/detail/9NBLGGH6J7VV", Tier.Optional, [], Use.Entertainment),
        new("amazon-music", 4, "Amazon Music", L.T("Musica in streaming di Amazon, app ufficiale dal Microsoft Store.", "Amazon's music streaming, official app from the Microsoft Store."),
            null, "https://apps.microsoft.com/detail/9NMS233VM4Z9", Tier.Optional, [], Use.Entertainment),
        new("tidal", 4, "TIDAL", L.T("Musica in streaming ad alta qualità, app ufficiale dal Microsoft Store.", "High-quality music streaming, official app from the Microsoft Store."),
            null, "https://apps.microsoft.com/detail/9NNCB5BS59PH", Tier.Optional, [], Use.Entertainment),
        new("youtube", 4, "YouTube", L.T("Non c'è un'app ufficiale: apri il sito in Edge o Chrome e installalo come app (menu ⋯ → App → Installa), così ha la sua icona e la sua finestra.",
                "There is no official app: open the site in Edge or Chrome and install it as an app (⋯ menu → Apps → Install), so it gets its own icon and window."),
            null, "https://www.youtube.com/", Tier.Optional, [], Use.Entertainment),
        new("twitch", 4, "Twitch", L.T("Dirette di giochi e altro. L'app per Windows non esiste più: installa il sito come app da Edge o Chrome (menu ⋯ → App → Installa).",
                "Live streams of games and more. The Windows app no longer exists: install the site as an app from Edge or Chrome (⋯ menu → Apps → Install)."),
            null, "https://www.twitch.tv/", Tier.Optional, [], Use.Entertainment),
        new("plex", 4, "Plex", L.T("Guarda i tuoi film e la tua musica da un server Plex (anche un NAS), più canali gratuiti.", "Watch your own films and music from a Plex server (a NAS too), plus free channels."),
            "Plex.Plex", "https://www.plex.tv/media-server-downloads/#plex-app", Tier.Optional, ["Plex"], Use.Entertainment),
        new("kodi", 4, "Kodi", L.T("Centro multimediale per i tuoi film, serie e musica, comodo anche col telecomando o il controller.", "Media centre for your own films, series and music, handy with a remote or a controller too."),
            "XBMCFoundation.Kodi", "https://kodi.tv/download/windows/", Tier.Optional, ["Kodi"], Use.Entertainment),

        // ── Emulation: one program for the retro consoles; each modern console's emulator is on OptLife's Emulation page ──
        new("retroarch", 7, "RetroArch",
            L.T("Un programma per tutte le console retro (NES, SNES, Game Boy, Mega Drive…). Gli emulatori di PlayStation, Switch, GameCube e le altre si installano dalla pagina Giochi → Emulazione di OptLife.",
                "One program for all retro consoles (NES, SNES, Game Boy, Mega Drive…). The PlayStation, Switch, GameCube and other emulators are installed from OptLife's Games → Emulation page."),
            "Libretro.RetroArch", "https://www.retroarch.com/?page=platforms", Tier.Recommended, ["RetroArch"], Use.Emulation),

        // ── Extras, as "shells" by interest: each one only when that interest is chosen ──
        new("fluentflyout", 9, "FluentFlyout",
            L.T("Riquadro moderno per musica e volume al posto di quello di Windows (open source); ha un'opzione per non disturbare i giochi a schermo intero. Si installa dal Microsoft Store.",
                "A modern media and volume flyout instead of Windows' own (open source); it has an option not to disturb full-screen games. Installed from the Microsoft Store."),
            null, "https://apps.microsoft.com/detail/9N45NSM4TNBP", Tier.Recommended, ["FluentFlyout"], Use.Aesthetics),
        new("wallpaper-engine", 9, "Wallpaper Engine",
            L.T("Sfondi animati, anche dalla comunità; si mette in pausa da solo quando giochi. Su Steam, a pagamento.",
                "Animated wallpapers, also from the community; pauses by itself while you play. On Steam, paid."),
            null, "https://store.steampowered.com/app/431960/", Tier.Optional, ["Wallpaper Engine"], Use.Aesthetics),
        new("translucenttb", 9, "TranslucentTB",
            L.T("Barra delle applicazioni trasparente o sfocata. Gratis e open source.", "Transparent or blurred taskbar. Free and open source."),
            "CharlesMilette.TranslucentTB", "https://github.com/TranslucentTB/TranslucentTB", Tier.Recommended, ["TranslucentTB"], Use.Aesthetics),
        new("rainmeter", 9, "Rainmeter",
            L.T("Widget sul desktop (orologio, meteo, sensori) con migliaia di temi.", "Desktop widgets (clock, weather, sensors) with thousands of skins."),
            "Rainmeter.Rainmeter", "https://www.rainmeter.net/", Tier.Optional, ["Rainmeter"], Use.Aesthetics),
        new("gimp", 9, "GIMP", L.T("Fotoritocco gratuito e completo.", "Free, complete photo editing."),
            "GIMP.GIMP.3", "https://www.gimp.org/downloads/", Tier.Recommended, ["GIMP"], Use.Creativity),
        new("krita", 9, "Krita", L.T("Disegno e pittura digitale, gratis.", "Free digital drawing and painting."),
            "KDE.Krita", "https://krita.org/en/download/", Tier.Recommended, ["Krita"], Use.Creativity),
        new("inkscape", 9, "Inkscape", L.T("Grafica vettoriale (loghi, illustrazioni), gratis.", "Vector graphics (logos, illustrations), free."),
            "Inkscape.Inkscape", "https://inkscape.org/release/", Tier.Optional, ["Inkscape"], Use.Creativity),
        new("blender", 9, "Blender", L.T("Modellazione e animazione 3D, gratis e professionale.", "3D modelling and animation, free and professional."),
            "BlenderFoundation.Blender", "https://www.blender.org/download/", Tier.Optional, ["Blender"], Use.Creativity),
    ];

    /// <summary>The list for this PC and these uses, in installation order.</summary>
    public static IReadOnlyList<App> For(Hardware hardware, IReadOnlySet<Use> uses, IReadOnlyCollection<string> skip) =>
        [.. All.Where(a => (a.When?.Invoke(hardware) ?? true) && !skip.Contains(a.Id, StringComparer.OrdinalIgnoreCase))
            .Select(a => a.Use != Use.All && !uses.Contains(a.Use) ? a with { Tier = Tier.Optional }
                : a.RaisedBy is { } u && uses.Contains(u) && a.Tier == Tier.Optional ? a with { Tier = Tier.Recommended } : a)
            .OrderBy(a => a.Step)];

    /// <summary>What kind of program it is, to read the list at a glance ("DAW", "Launcher").</summary>
    public static string KindOf(App app) => app.Id switch
    {
        WindowsUpdate => L.T("Aggiornamenti", "Updates"),
        "amd-chipset" or "intel-dsa" or "nvidia-app" or "amd-adrenalin" => "Driver",
        "bitwarden" or "proton-pass" => L.T("Password", "Passwords"),
        "7zip" or "nanazip" => L.T("Archivi", "Archives"),
        "vlc" => L.T("Video", "Video"),
        "firefox" or "brave" or "chrome" or "vivaldi" or "opera-gx" => "Browser",
        "everything" => L.T("Ricerca file", "File search"),
        "afterburner" or "rtss" or "hwinfo" => L.T("Sensori", "Sensors"),
        "latencymon" => L.T("Diagnosi audio", "Audio diagnosis"),
        "reaper" or "lmms" or "cakewalk" or "waveform" or "fl-studio" or "ableton" => "DAW",
        "asio4all" => L.T("Driver audio", "Audio driver"),
        "audacity" => L.T("Editor audio", "Audio editor"),
        "spotify" or "apple-music" or "deezer" or "amazon-music" or "tidal" => L.T("Musica", "Music"),
        "netflix" or "prime-video" or "disney-plus" or "apple-tv" or "crunchyroll" => L.T("Film e serie", "Films and series"),
        "youtube" or "twitch" => L.T("Video e dirette", "Videos and live streams"),
        "plex" or "kodi" => L.T("Centro multimediale", "Media centre"),
        "retroarch" => L.T("Emulatore", "Emulator"),
        "wallpaper-engine" or "translucenttb" or "rainmeter" or "fluentflyout" => L.T("Estetica", "Aesthetics"),
        "gimp" or "krita" or "inkscape" or "blender" => L.T("Creatività", "Creativity"),
        "steam" or "epic" or "battlenet" or "ea-app" or "ubisoft" => "Launcher",
        "discord" => "Chat",
        "obs" => L.T("Registrazione", "Recording"),
        "libreoffice" or "office" => L.T("Ufficio", "Office"),
        "obsidian" or "notion" => L.T("Appunti", "Notes"),
        "anki" => "Flashcard",
        "zotero" => L.T("Bibliografia", "References"),
        "zoom" => L.T("Videochiamate", "Video calls"),
        "sumatra" => "PDF",
        _ => L.T("Utilità", "Utility"),
    };

    public static string TierWord(Tier t) => t switch
    {
        Tier.Essential => L.T("Essenziale", "Essential"),
        Tier.Recommended => L.T("Consigliata", "Recommended"),
        _ => L.T("Facoltativa", "Optional"),
    };

    /// <summary>True when one of the installed program names matches the app.</summary>
    public static bool IsInstalled(App app, IReadOnlyCollection<string> installedNames) =>
        app.Match.Any(m => m.StartsWith('=')
            ? installedNames.Any(n => n.Equals(m[1..], StringComparison.OrdinalIgnoreCase))
            : installedNames.Any(n => n.Contains(m, StringComparison.OrdinalIgnoreCase)));

    /// <summary>How the PC looks to the person, as the first lines of the page.</summary>
    public static string Describe(Hardware h)
    {
        static string Name(Vendor v) => v switch { Vendor.Amd => "AMD", Vendor.Intel => "Intel", Vendor.Nvidia => "NVIDIA", _ => L.T("non riconosciuto", "not recognised") };
        var gpus = h.Gpus.Count == 0 ? L.T("non riconosciuta", "not recognised") : string.Join(" + ", h.Gpus.Select(Name));
        return L.T($"Processore {Name(h.Cpu)}, scheda video {gpus}", $"{Name(h.Cpu)} processor, {gpus} graphics");
    }
}
