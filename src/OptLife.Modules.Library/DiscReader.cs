using System.Text;

namespace OptLife.Modules.Library;

/// <summary>What a PlayStation 2 disc says about itself: the program it starts and the code PCSX2 names it by.</summary>
/// <param name="Serial">As PCSX2 writes it: "SLES-82009".</param>
/// <param name="Crc">The CRC PCSX2 gives the started program (XOR of its 32-bit words), as 8 hex digits: "093E7D52".</param>
public sealed record Ps2Disc(string Serial, string? Crc, string BootFile);

/// <summary>What a PARAM.SFO holds that matters here.</summary>
/// <param name="Category">"DG" disc game, "HG" game installed on the console's disk, "GD" game data, "GP" patch.</param>
public sealed record SfoInfo(string TitleId, string Title, string Category);

/// <summary>What a GameCube or Wii disc header says: the 6-character id Dolphin names its files by, and the title.</summary>
public sealed record NintendoDisc(string Id, string Title, bool Wii);

/// <summary>
/// Reads only the few bytes needed from a disc image or a game's PARAM.SFO: no unpacking, no writing.
/// ISO 9660 images (.iso, 2048-byte sectors) and raw CD images (.bin, 2352-byte sectors) are both read.
/// </summary>
public static class DiscReader
{
    private const int Sector = 2048;

    /// <summary>The PS2 disc's serial and CRC; null when it is not a PS2 disc (no SYSTEM.CNF with BOOT2).</summary>
    public static Ps2Disc? ReadPs2(Stream image) => ReadPlayStation(image, ps2: true);

    /// <summary>The PS1 disc's serial (from BOOT in SYSTEM.CNF); null when the disc has none (some early games start PSX.EXE).</summary>
    public static string? ReadPs1Serial(Stream image) => ReadPlayStation(image, ps2: false)?.Serial;

    /// <summary>A file inside a disc image (.iso or raw .bin), at most <paramref name="max"/> bytes; null when it is not there.</summary>
    public static byte[]? ReadFile(Stream image, string path, int max = 1024 * 1024)
    {
        if (Layout(image) is not { } layout) return null;
        var iso = new Iso(image, layout.RawSize, layout.DataOffset);
        return iso.Find(path) is { IsDir: false } entry ? iso.Read(entry.Lba, Math.Min(entry.Size, max)) : null;
    }

    /// <summary>A PSP disc's id and title, from PSP_GAME\PARAM.SFO.</summary>
    public static SfoInfo? ReadPsp(Stream image) => ReadFile(image, @"PSP_GAME\PARAM.SFO", 64 * 1024) is { } sfo ? ReadSfo(sfo) : null;

    private static Ps2Disc? ReadPlayStation(Stream image, bool ps2)
    {
        if (Layout(image) is not { } layout) return null;
        var iso = new Iso(image, layout.RawSize, layout.DataOffset);
        if (iso.Find("SYSTEM.CNF") is not { } cnf) return null;
        var text = Encoding.ASCII.GetString(iso.Read(cnf.Lba, Math.Min(cnf.Size, 4096)));
        var boot = BootPath(text, ps2 ? "BOOT2" : "BOOT");
        if (boot is null) return null;
        var serial = SerialFrom(boot);
        string? crc = null;
        // The started program: "cdrom0:\SLES_820.09;1" or in a folder "cdrom0:\SKATE\SKATE2.ELF;1".
        if (ps2 && iso.Find(boot) is { } elf && elf.Size is > 0 and < 64 * 1024 * 1024)
            crc = ElfCrc(iso.Read(elf.Lba, elf.Size)).ToString("X8");
        return new Ps2Disc(serial, crc, boot);
    }

    /// <summary>"BOOT2 = cdrom0:\SLES_820.09;1" → "SLES_820.09" (folders kept with '\').</summary>
    public static string? BootPath(string systemCnf, string key = "BOOT2")
    {
        foreach (var raw in systemCnf.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith(key, StringComparison.OrdinalIgnoreCase)) continue;
            var eq = line.IndexOf('=');
            // "BOOT" must not take "BOOT2": only spaces between the key and '='.
            if (eq < 0 || line[key.Length..eq].Trim().Length > 0) continue;
            var value = line[(eq + 1)..].Trim();
            var colon = value.IndexOf(':');
            if (colon >= 0) value = value[(colon + 1)..];
            value = value.TrimStart('\\', '/');
            var semi = value.IndexOf(';');
            if (semi >= 0) value = value[..semi];
            return value.Length > 0 ? value : null;
        }
        return null;
    }

    /// <summary>"SLES_820.09" → "SLES-82009", the way PCSX2 names the game; a program with another name keeps it, in capitals.</summary>
    public static string SerialFrom(string bootPath)
    {
        var name = bootPath.Replace('/', '\\').Split('\\')[^1].ToUpperInvariant();
        return name.Replace('_', '-').Replace(".", "");
    }

    /// <summary>The CRC PCSX2 gives a program: all its 32-bit words XORed together.</summary>
    public static uint ElfCrc(ReadOnlySpan<byte> elf)
    {
        uint crc = 0;
        for (var i = 0; i + 4 <= elf.Length; i += 4)
            crc ^= BitConverter.ToUInt32(elf.Slice(i, 4));
        return crc;
    }

    /// <summary>Sector size and where the 2048 data bytes start in each: (2048, 0) for .iso, (2352, 24 or 16) for raw CD images.</summary>
    private static (int RawSize, int DataOffset)? Layout(Stream s)
    {
        foreach (var (raw, offset) in new[] { (2048, 0), (2352, 24), (2352, 16) })
        {
            var at = 16L * raw + offset + 1;
            if (s.Length < at + 5) continue;
            s.Position = at;
            var id = new byte[5];
            if (s.Read(id, 0, 5) == 5 && Encoding.ASCII.GetString(id) == "CD001") return (raw, offset);
        }
        return null;
    }

    private readonly record struct Entry(long Lba, int Size, bool IsDir);

    /// <summary>Just enough of ISO 9660 to find a file by path.</summary>
    private sealed class Iso(Stream s, int rawSize, int dataOffset)
    {
        public byte[] Read(long lba, int size)
        {
            var result = new byte[size];
            var done = 0;
            while (done < size)
            {
                var sector = lba + done / Sector;
                var within = done % Sector;
                s.Position = sector * rawSize + dataOffset + within;
                var take = Math.Min(Sector - within, size - done);
                var got = s.Read(result, done, take);
                if (got <= 0) break;
                done += got;
            }
            return result;
        }

        public Entry? Find(string path)
        {
            var pvd = Read(16, Sector);
            var dir = Record(pvd, 156);
            if (dir is null) return null;
            var current = dir.Value;
            foreach (var part in path.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!current.IsDir) return null;
                var next = InDir(current, part);
                if (next is null) return null;
                current = next.Value;
            }
            return current;
        }

        private Entry? InDir(Entry dir, string name)
        {
            var data = Read(dir.Lba, Math.Min(dir.Size, 1024 * 1024));
            var pos = 0;
            while (pos < data.Length)
            {
                int len = data[pos];
                if (len == 0)
                {
                    // Records never cross a sector: go to the next one.
                    pos = (pos / Sector + 1) * Sector;
                    continue;
                }
                if (pos + len > data.Length) break;
                int nameLen = data[pos + 32];
                if (nameLen > 0 && pos + 33 + nameLen <= data.Length)
                {
                    var entryName = Encoding.ASCII.GetString(data, pos + 33, nameLen);
                    var semi = entryName.IndexOf(';');
                    if (semi >= 0) entryName = entryName[..semi];
                    if (entryName.Equals(name, StringComparison.OrdinalIgnoreCase) && Record(data, pos) is { } found) return found;
                }
                pos += len;
            }
            return null;
        }

        private static Entry? Record(byte[] data, int at)
        {
            if (at + 34 > data.Length) return null;
            var lba = BitConverter.ToUInt32(data, at + 2);
            var size = BitConverter.ToUInt32(data, at + 10);
            var flags = data[at + 25];
            return new Entry(lba, (int)Math.Min(size, int.MaxValue), (flags & 2) != 0);
        }
    }

    /// <summary>TITLE_ID, TITLE and CATEGORY from a PARAM.SFO; null when the file is not one.</summary>
    public static SfoInfo? ReadSfo(ReadOnlySpan<byte> sfo)
    {
        if (sfo.Length < 20 || sfo[0] != 0 || sfo[1] != (byte)'P' || sfo[2] != (byte)'S' || sfo[3] != (byte)'F') return null;
        var keyTable = (int)BitConverter.ToUInt32(sfo.Slice(8, 4));
        var dataTable = (int)BitConverter.ToUInt32(sfo.Slice(12, 4));
        var count = (int)BitConverter.ToUInt32(sfo.Slice(16, 4));
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < count && 20 + i * 16 + 16 <= sfo.Length; i++)
        {
            var e = sfo.Slice(20 + i * 16, 16);
            var keyOffset = BitConverter.ToUInt16(e[..2]);
            var format = BitConverter.ToUInt16(e.Slice(2, 2));
            var length = (int)BitConverter.ToUInt32(e.Slice(4, 4));
            var dataOffset = (int)BitConverter.ToUInt32(e.Slice(12, 4));
            var key = Zstring(sfo, keyTable + keyOffset);
            // 0x0204 = UTF-8 text with its zero, 0x0004 = UTF-8 without; numbers (0x0404) are not needed.
            if (format is not (0x0204 or 0x0004)) continue;
            var start = dataTable + dataOffset;
            if (start < 0 || start + length > sfo.Length) continue;
            values[key] = Encoding.UTF8.GetString(sfo.Slice(start, length)).TrimEnd('\0').Trim();
        }
        // PS3 and Vita name the game TITLE_ID, PSP discs DISC_ID.
        var id = values.GetValueOrDefault("TITLE_ID") is { Length: > 0 } t ? t : values.GetValueOrDefault("DISC_ID");
        return id is { Length: > 0 }
            ? new SfoInfo(id, values.GetValueOrDefault("TITLE", id), values.GetValueOrDefault("CATEGORY", ""))
            : null;
    }

    /// <summary>
    /// A GameCube or Wii disc's id ("GALE01") and title, from the disc header: plain images (.iso, .gcm), RVZ/WIA (header copy at
    /// 0x58), WBFS (0x200) and CISO (0x8000). GCZ is compressed from the start: null, the file's name is used.
    /// </summary>
    public static NintendoDisc? ReadNintendo(Stream image)
    {
        var head = new byte[8];
        image.Position = 0;
        if (image.Read(head, 0, 4) < 4) return null;
        var magic = Encoding.ASCII.GetString(head, 0, 4);
        long at = magic switch
        {
            "RVZ\u0001" or "WIA\u0001" => 0x58,
            "WBFS" => 0x200,
            "CISO" => 0x8000,
            _ => 0,
        };
        var header = new byte[0x80];
        if (image.Length < at + header.Length) return null;
        image.Position = at;
        if (image.Read(header, 0, header.Length) < header.Length) return null;
        var wii = BitConverter.ToUInt32(header, 0x18) == 0xA39E1C5D;
        var gc = BitConverter.ToUInt32(header, 0x1C) == 0x3D9F33C2;
        if (!wii && !gc) return null;
        var id = Encoding.ASCII.GetString(header, 0, 6);
        if (!id.All(char.IsAsciiLetterOrDigit)) return null;
        var end = 0x20;
        while (end < header.Length && header[end] != 0) end++;
        var title = Encoding.Latin1.GetString(header, 0x20, end - 0x20).Trim();
        return new NintendoDisc(id, title.Length > 0 ? title : id, wii);
    }

    /// <summary>
    /// A Switch package's title id: an .nsp is a PFS0 archive whose ticket is named after the rights id, title id first
    /// ("0100F2C0115B6000000000000000000X.tik"). Null for other files (.xci and compressed ones are named by their file).
    /// </summary>
    public static string? SwitchTitleId(Stream s)
    {
        var head = new byte[16];
        s.Position = 0;
        if (s.Read(head, 0, 16) < 16 || Encoding.ASCII.GetString(head, 0, 4) != "PFS0") return null;
        var count = BitConverter.ToInt32(head, 4);
        var stringsSize = BitConverter.ToInt32(head, 8);
        if (count is <= 0 or > 1000 || stringsSize is <= 0 or > 1 << 20) return null;
        var strings = new byte[stringsSize];
        s.Position = 16 + count * 24L;
        if (s.Read(strings, 0, stringsSize) < stringsSize) return null;
        foreach (var name in Encoding.ASCII.GetString(strings).Split('\0'))
            if (name.EndsWith(".tik", StringComparison.OrdinalIgnoreCase) && name.Length >= 20 && name[..16].All(Uri.IsHexDigit))
                return name[..16].ToUpperInvariant();
        return null;
    }

    /// <summary>
    /// A 3DS game's 4-letter code ("AMKE") from its product code ("CTR-P-AMKE") in the NCCH header: at the start of a .cxi, or of
    /// the first partition of a .3ds/.cci (NCSD).
    /// </summary>
    public static string? N3dsCode(Stream s)
    {
        foreach (var at in new long[] { 0, 0x4000 })
        {
            var header = new byte[0x200];
            if (s.Length < at + header.Length) continue;
            s.Position = at;
            if (s.Read(header, 0, header.Length) < header.Length || Encoding.ASCII.GetString(header, 0x100, 4) != "NCCH") continue;
            var product = Encoding.ASCII.GetString(header, 0x150, 16).TrimEnd('\0', ' ');
            var code = product.Split('-').Last();
            return code.Length == 4 && code.All(char.IsAsciiLetterOrDigit) ? code : null;
        }
        return null;
    }

    /// <summary>A DS cartridge's 4-letter game code and its own short title, from the header.</summary>
    public static (string Code, string Title)? DsHeader(Stream s)
    {
        var header = new byte[0x10];
        s.Position = 0;
        if (s.Read(header, 0, header.Length) < header.Length) return null;
        var code = Encoding.ASCII.GetString(header, 0x0C, 4);
        if (!code.All(char.IsAsciiLetterOrDigit)) return null;
        return (code, Encoding.ASCII.GetString(header, 0, 12).TrimEnd('\0', ' '));
    }

    /// <summary>The first data file a .cue sheet names ("FILE \"Game (Disc 1).bin\" BINARY"), or null.</summary>
    public static string? CueFirstFile(string cue)
    {
        foreach (var raw in cue.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("FILE ", StringComparison.OrdinalIgnoreCase)) continue;
            var start = line.IndexOf('"');
            var end = start >= 0 ? line.IndexOf('"', start + 1) : -1;
            if (end > start) return line[(start + 1)..end];
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 ? parts[1] : null;
        }
        return null;
    }

    private static string Zstring(ReadOnlySpan<byte> data, int at)
    {
        if (at < 0 || at >= data.Length) return "";
        var end = at;
        while (end < data.Length && data[end] != 0) end++;
        return Encoding.ASCII.GetString(data[at..end]);
    }
}
