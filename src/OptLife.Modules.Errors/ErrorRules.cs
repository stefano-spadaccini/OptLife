using System.Globalization;
using System.Text.RegularExpressions;
using OptLife.Core;

namespace OptLife.Modules.Errors;

/// <summary>Decides which groups of events are worth showing as a problem. Pure: no I/O, no clock.</summary>
public static partial class ErrorRules
{
    private const int ManyTimes = 5;
    private const int ServiceThreshold = 3;
    private static CultureInfo Lang => L.Culture;

    private static Dictionary<string, string> CodeMeaning => new()
    {
        ["c0000005"] = L.T("accesso a memoria non valida", "invalid memory access"),
        ["c0000409"] = L.T("il programma si è fermato da solo dopo aver rilevato un errore interno", "the program stopped itself after detecting an internal error"),
        ["c0000374"] = L.T("memoria del programma danneggiata", "the program's memory got corrupted"),
        ["c000001d"] = L.T("istruzione non supportata dal processore", "instruction not supported by the processor"),
        ["c00000fd"] = L.T("esaurimento dello stack", "stack overflow"),
        ["c0000006"] = L.T("lettura da disco fallita", "disk read failed"),
        ["c0000135"] = L.T("manca un file necessario (DLL)", "a required file (DLL) is missing"),
        ["e06d7363"] = L.T("errore interno non gestito dal programma", "internal error not handled by the program"),
        ["80000003"] = L.T("interruzione di controllo interna", "internal breakpoint"),
    };

    public static List<NewFinding> Evaluate(IReadOnlyList<StoredEvent> window, int windowDays, int crashThreshold, int recurringThreshold)
    {
        var findings = new List<NewFinding>();
        // Events stored before the shutdown rule existed are still in the window: they are left out here too.
        var relevant = window.Where(e => e.GroupKey is not null &&
            !(e.Kind == ErrorKinds.Other && EventClassifier.HappenedDuringShutdown(e.Data.GetValueOrDefault("message"))));
        foreach (var group in relevant.GroupBy(e => e.GroupKey!))
        {
            var events = group.OrderByDescending(e => e.Time).ToList();
            // When it last happened: a problem marked as solved after that moment stays solved until it happens again.
            if (Describe(group.Key, events, windowDays, crashThreshold, recurringThreshold) is { } finding)
                findings.Add(finding with { LatestAt = events[0].Time });
        }
        return findings;
    }

    private static NewFinding? Describe(string key, List<StoredEvent> events, int days, int crashThreshold, int recurringThreshold)
    {
        var latest = events[0];
        // Windows writes two or three records for a single service failure (did not start + timed out):
        // counting records would double the number, so those are counted per minute.
        var n = latest.Kind == ErrorKinds.Service
            ? events.Select(e => e.Time.UtcTicks / TimeSpan.TicksPerMinute).Distinct().Count()
            : events.Count;
        var times = n == 1 ? L.T("1 volta", "once") : L.T($"{n} volte", $"{n} times");
        var when = L.T("Ultima volta: ", "Last time: ") + latest.Time.ToString("d MMMM, HH:mm", Lang) + ".";
        string Data(string name) => latest.Data.GetValueOrDefault(name, "");
        Severity Escalating(Severity normal) => n >= ManyTimes ? normal + 1 : normal;

        switch (latest.Kind)
        {
            case ErrorKinds.AppCrash when n >= crashThreshold:
            {
                var (detail, advice) = ExplainCrashes(events);
                return new NewFinding(key, Escalating(Severity.Medium),
                    L.T($"{Data("exe")} è andato in crash {times} negli ultimi {days} giorni", $"{Data("exe")} crashed {times} in the last {days} days"), $"{detail} {when}", advice);
            }

            case ErrorKinds.AppHang when n >= crashThreshold:
                return new NewFinding(key, Escalating(Severity.Low),
                    L.T($"{Data("exe")} si è bloccato {times} negli ultimi {days} giorni", $"{Data("exe")} hung {times} in the last {days} days"),
                    L.T("Il programma ha smesso di rispondere ed è stato chiuso. ", "The program stopped responding and was closed. ") + when,
                    L.T("Se succede sotto carico, guarda memoria libera e temperature in quel momento.", "If it happens under load, look at free memory and temperatures at that moment."));

            case ErrorKinds.BlueScreen:
                return new NewFinding(key, Severity.High,
                    L.T($"Schermata blu {times} negli ultimi {days} giorni (codice {Data("code")})", $"Blue screen {times} in the last {days} days (code {Data("code")})"),
                    L.T("Windows si è fermato per un errore grave. ", "Windows stopped because of a serious error. ") + when,
                    L.T("Una schermata blu isolata può capitare; se si ripete con lo stesso codice la causa è quasi sempre un driver o l'hardware.", "A single blue screen can happen; if it repeats with the same code the cause is almost always a driver or the hardware."));

            case ErrorKinds.UnexpectedShutdown:
                return new NewFinding(key, Escalating(Severity.Medium),
                    L.T($"Il PC si è spento in modo anomalo {times} negli ultimi {days} giorni", $"The PC shut down abnormally {times} in the last {days} days"),
                    L.T("Windows non è stato chiuso correttamente. ", "Windows was not shut down properly. ") + when,
                    L.T("Se non è stato un blackout o uno spegnimento forzato da te, controlla alimentazione e temperature.", "If it was not a power cut or a forced shutdown by you, check the power supply and temperatures."));

            case ErrorKinds.Hardware:
                return new NewFinding(key, Severity.High,
                    L.T($"Errori hardware: {times} negli ultimi {days} giorni", $"Hardware errors: {times} in the last {days} days"),
                    $"{latest.Title}. {when}",
                    L.T("Errori di questo tipo indicano di solito instabilità di processore o memoria (profilo RAM, tensioni, temperature).", "Errors of this kind usually mean processor or memory instability (RAM profile, voltages, temperatures)."));

            case ErrorKinds.Disk:
                return new NewFinding(key, Severity.High,
                    L.T($"Errori su disco: {times} negli ultimi {days} giorni", $"Disk errors: {times} in the last {days} days"),
                    $"{latest.Title}. {when}",
                    L.T("Verifica che i backup siano recenti prima di qualsiasi altra cosa, poi controlla lo stato di salute del disco.", "Check that the backups are recent before anything else, then check the disk's health."));

            case ErrorKinds.Gpu:
                return new NewFinding(key, Escalating(Severity.Medium),
                    L.T($"Problemi della scheda video: {times} negli ultimi {days} giorni", $"Graphics card problems: {times} in the last {days} days"),
                    $"{latest.Title}. {when}",
                    L.T("Se capita in gioco, prova un driver video diverso e controlla temperature e limiti di potenza impostati.", "If it happens in game, try a different graphics driver and check temperatures and the power limits set."));

            case ErrorKinds.Service when n >= ServiceThreshold:
                return new NewFinding(key, Severity.Low,
                    L.T($"Il servizio \"{Data("service")}\" dà errore di continuo ({times} in {days} giorni)", $"The \"{Data("service")}\" service keeps failing ({times} in {days} days)"),
                    $"{latest.Title}. {when}",
                    L.T("Se appartiene a un programma che non usi più, conviene disinstallarlo o disattivare il servizio.", "If it belongs to a program you no longer use, uninstall it or disable the service."));

            case ErrorKinds.Other when n >= recurringThreshold:
                return new NewFinding(key, Severity.Info,
                    L.T($"Errore ricorrente di {Data("provider")} ({times} in {days} giorni)", $"Recurring {Data("provider")} error ({times} in {days} days)"),
                    $"{latest.Title} {when}",
                    L.T("Spesso è rumore innocuo: se non noti problemi puoi ignorarlo, e non verrà più mostrato.", "Often harmless noise: if you notice no problems you can ignore it, and it will not be shown again."));

            default:
                return null;
        }
    }

    private static (string Detail, string Advice) ExplainCrashes(List<StoredEvent> events)
    {
        var n = events.Count;
        var byCause = events
            .GroupBy(e => (Module: e.Data.GetValueOrDefault("faultModule", ""), Code: e.Data.GetValueOrDefault("code", "")))
            .OrderByDescending(g => g.Count())
            .ToList();
        var (module, code) = byCause[0].Key;
        var share = byCause[0].Count();
        var meaning = CodeMeaning.TryGetValue(code, out var m) ? $": {m}" : "";
        var exe = events[0].Data.GetValueOrDefault("exe", "");

        var detail = share == n
            ? L.T($"Sempre nello stesso punto: componente {module} (codice {code}{meaning}).", $"Always in the same place: component {module} (code {code}{meaning}).")
            : L.T($"{share} volte su {n} nel componente {module} (codice {code}{meaning}); le altre in punti diversi.", $"{share} times out of {n} in component {module} (code {code}{meaning}); the others in different places.");

        string advice;
        if (byCause.Count >= 3 && share * 2 <= n)
            advice = L.T("Gli errori cadono in componenti sempre diversi: è più probabile un problema di memoria o di sistema che del programma.", "The errors land in ever different components: a memory or system problem is more likely than one of the program.");
        else if (GraphicsModule().IsMatch(module))
            advice = L.T("Il componente coinvolto fa parte della grafica: prova un driver video diverso o un'altra modalità di rendering.", "The component involved is part of the graphics: try a different graphics driver or another rendering mode.");
        else if (module.Equals(exe, StringComparison.OrdinalIgnoreCase))
            advice = L.T("L'errore nasce nel programma stesso: aggiornalo o verifica l'integrità dei suoi file.", "The error comes from the program itself: update it or verify its files.");
        else
            advice = L.T($"Il componente che fallisce è {module}: aggiornare o reinstallare il programma a cui appartiene di solito risolve.", $"The failing component is {module}: updating or reinstalling the program it belongs to usually fixes it.");

        return (detail, advice);
    }

    [GeneratedRegex(@"^(nv|amd|ati|ig\d|igd|d3d|dxgi|vulkan|opengl|rendersystem)", RegexOptions.IgnoreCase)]
    private static partial Regex GraphicsModule();
}
