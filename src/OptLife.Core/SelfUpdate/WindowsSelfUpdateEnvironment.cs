using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace OptLife.Core.SelfUpdate;

/// <summary>The real machine: GitHub over HTTPS (no account), the file's SHA-256 and Authenticode signature, and the installer started on its own.</summary>
public sealed class WindowsSelfUpdateEnvironment : ISelfUpdateEnvironment
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // GitHub asks every client for a name: a generic one, nothing about the person.
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"OptLife-Updater/{SelfUpdateRules.Current.ToString(3)}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public async Task<string?> GetText(string url, CancellationToken ct)
    {
        try
        {
            using var answer = await Http.GetAsync(url, ct);
            return answer.IsSuccessStatusCode ? await answer.Content.ReadAsStringAsync(ct) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return null;
        }
    }

    public async Task<bool> Download(string url, string path, Action<long, long?> progress, CancellationToken ct)
    {
        try
        {
            using var answer = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!answer.IsSuccessStatusCode) return false;
            var total = answer.Content.Headers.ContentLength;
            await using var from = await answer.Content.ReadAsStreamAsync(ct);
            await using var to = File.Create(path);
            var buffer = new byte[1 << 16];
            long got = 0, said = 0;
            int read;
            while ((read = await from.ReadAsync(buffer, ct)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, read), ct);
                got += read;
                if (got - said >= 4 << 20) { progress(got, total); said = got; }
            }
            progress(got, total);
            return total is null || got == total;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return false;
        }
    }

    public string Sha256(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
    }

    public bool? SignatureValid(string path)
    {
        var file = new WinTrustFileInfo { cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(), pcwszFilePath = path };
        var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(file, filePtr, false);
            var data = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(), dwUIChoice = 2 /* WTD_UI_NONE */, fdwRevocationChecks = 0,
                dwUnionChoice = 1 /* WTD_CHOICE_FILE */, pFile = filePtr, dwStateAction = 0, dwProvFlags = 0x00000080, /* WTD_CACHE_ONLY_URL_RETRIEVAL */
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            var result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            const int NoSignature = unchecked((int)0x800B0100); // TRUST_E_NOSIGNATURE
            return result == 0 ? true : result == NoSignature ? null : false;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(filePtr);
        }
    }

    /// <summary>
    /// Started outside the agent's job (the scheduled task's): the installer stops the agent, and must not go with it.
    /// From a process that is not administrator, Windows asks for the rights (the installer needs them).
    /// </summary>
    public ActionResult Launch(string installer, string arguments)
    {
        var info = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
        const uint BreakawayFromJob = 0x01000000, NoWindow = 0x08000000;
        var command = $"\"{installer}\" {arguments}";
        foreach (var flags in new[] { BreakawayFromJob | NoWindow, NoWindow })
        {
            if (CreateProcess(installer, command, IntPtr.Zero, IntPtr.Zero, false, flags, IntPtr.Zero, Path.GetDirectoryName(installer), ref info, out var process))
            {
                CloseHandle(process.hThread);
                CloseHandle(process.hProcess);
                return new ActionResult(true, "");
            }
            var error = Marshal.GetLastWin32Error();
            if (error == 740) break; // ERROR_ELEVATION_REQUIRED: not administrator, ask Windows below.
        }
        try
        {
            using var p = Process.Start(new ProcessStartInfo(installer, arguments) { UseShellExecute = true, Verb = "runas" });
            return new ActionResult(p is not null, p is null ? L.T("L'installatore non è partito.", "The installer did not start.") : "");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new ActionResult(false, ex.NativeErrorCode == 1223 ? L.T("Permesso di amministratore negato: niente installato.", "Administrator permission refused: nothing installed.") : ex.Message);
        }
    }

    public bool PanelRunning()
    {
        var panels = Process.GetProcessesByName("optlife-panel");
        foreach (var p in panels) p.Dispose();
        return panels.Length > 0;
    }

    public ActionResult Open(string url)
    {
        try
        {
            // Through Explorer: the browser opens as the person, not as administrator.
            using var _ = Process.Start(new ProcessStartInfo("explorer.exe", $"\"{url}\"") { UseShellExecute = false });
            return new ActionResult(true, L.T("Pagina aperta nel browser.", "Page opened in the browser."));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new ActionResult(false, ex.Message);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData, pSIPClientData;
        public uint dwUIChoice, fdwRevocationChecks, dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData, pwszURLReference;
        public uint dwProvFlags, dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WinTrustData data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string application, string commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint flags, IntPtr environment, string? directory, ref StartupInfo startup, out ProcessInformation info);

    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
