using System.Runtime.InteropServices;
using System.Text;

namespace OptLife.Modules.Setup.Windows;

/// <summary>
/// What the motherboard firmware says about the machine (the SMBIOS table): memory modules with
/// their rated and actual speed, board and BIOS version. Readable without administrator rights.
/// </summary>
internal static class Smbios
{
    public sealed record Structure(byte Type, byte[] Data, IReadOnlyList<string> Strings)
    {
        public int Word(int offset) => offset + 1 < Data.Length ? Data[offset] | Data[offset + 1] << 8 : 0;
        public long Dword(int offset) => offset + 3 < Data.Length ? BitConverter.ToUInt32(Data, offset) : 0;

        /// <summary>Text fields are stored as a 1-based position in the list of strings that follows the structure.</summary>
        public string Text(int offset)
        {
            if (offset >= Data.Length) return "";
            var index = Data[offset];
            return index >= 1 && index <= Strings.Count ? Strings[index - 1].Trim() : "";
        }
    }

    public static byte[]? ReadRaw()
    {
        const uint provider = 0x52534D42; // "RSMB"
        var size = GetSystemFirmwareTable(provider, 0, null, 0);
        if (size == 0) return null;
        var buffer = new byte[size];
        return GetSystemFirmwareTable(provider, 0, buffer, size) == size ? buffer : null;
    }

    /// <param name="raw">As Windows returns it: 8 bytes of header (the table length is the last 4), then the table.</param>
    public static IReadOnlyList<Structure> Parse(byte[] raw)
    {
        var list = new List<Structure>();
        if (raw.Length < 8) return list;
        var end = Math.Min(raw.Length, 8 + (int)BitConverter.ToUInt32(raw, 4));
        var pos = 8;
        while (pos + 4 <= end)
        {
            var type = raw[pos];
            var length = raw[pos + 1];
            if (length < 4 || pos + length > end) break;

            // The text of a structure follows it: zero-terminated strings, closed by one more zero.
            var strings = new List<string>();
            var p = pos + length;
            while (p < end && raw[p] != 0)
            {
                var q = p;
                while (q < end && raw[q] != 0) q++;
                strings.Add(Encoding.ASCII.GetString(raw, p, q - p));
                p = q + 1;
            }
            p += strings.Count == 0 ? 2 : 1;

            list.Add(new Structure(type, raw[pos..(pos + length)], strings));
            if (type == 127) break; // End-of-table marker.
            pos = p;
        }
        return list;
    }

    public static IReadOnlyList<MemoryModule> Memory(IReadOnlyList<Structure> table)
    {
        var modules = new List<MemoryModule>();
        foreach (var s in table.Where(s => s.Type == 17))
        {
            var size = s.Word(0x0C);
            if (size is 0 or 0xFFFF) continue; // Empty slot, or unknown.
            var megabytes = size == 0x7FFF ? (int)s.Dword(0x1C) : (size & 0x8000) != 0 ? (size & 0x7FFF) / 1024 : size;
            var kind = s.Data.Length > 0x12 ? s.Data[0x12] switch { 0x18 => "DDR3", 0x1A => "DDR4", 0x22 => "DDR5", _ => "" } : "";
            modules.Add(new MemoryModule(megabytes, kind, s.Word(0x15), s.Word(0x20), s.Text(0x17), s.Text(0x1A)));
        }
        return modules;
    }

    public static string? Board(IReadOnlyList<Structure> table) =>
        table.FirstOrDefault(s => s.Type == 2) is { } b && b.Text(0x05).Length > 0 ? $"{b.Text(0x04)} {b.Text(0x05)}".Trim() : null;

    /// <summary>The kind of case (SMBIOS type 3): 3 desktop, 9 laptop, 10 notebook…; null when the table has none.</summary>
    public static byte? Chassis(IReadOnlyList<Structure> table) =>
        table.FirstOrDefault(s => s.Type == 3) is { } c && c.Data.Length > 5 ? (byte)(c.Data[5] & 0x7F) : null;

    public static string? BiosVersion(IReadOnlyList<Structure> table) =>
        table.FirstOrDefault(s => s.Type == 0) is { } b && b.Text(0x05).Length > 0 ? b.Text(0x05) : null;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetSystemFirmwareTable(uint provider, uint id, [Out] byte[]? buffer, uint size);
}
