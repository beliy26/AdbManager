using System.IO.Compression;
using System.Text;
using AdbManager.Services;

namespace AdbManager.Apk;

/// <summary>
/// APK на устройстве, из которого читаются отдельные файлы без скачивания всего архива:
/// сначала конец файла с оглавлением ZIP, затем нужные записи через <c>dd</c> по смещениям.
/// </summary>
internal sealed class RemoteApk
{
    private const int BlockSize = 4096;
    private const int TailSize = 65557; // EOCD (22 байта) + максимальный комментарий ZIP

    private sealed record Entry(string Name, int Method, long CompressedSize, long LocalOffset, int NameLength, int ExtraLength);

    private readonly AdbService _adb;
    private readonly string _serial;
    private readonly string _path;
    private readonly long _size;
    private readonly Dictionary<string, Entry> _entries;
    private readonly Dictionary<string, byte[]> _cache = [];

    private RemoteApk(AdbService adb, string serial, string path, long size, Dictionary<string, Entry> entries)
    {
        _adb = adb;
        _serial = serial;
        _path = path;
        _size = size;
        _entries = entries;
    }

    public bool Contains(string name) => _entries.ContainsKey(name);

    public static async Task<RemoteApk> OpenAsync(AdbService adb, string serial, string path, CancellationToken ct)
    {
        var q = Quote(path);
        var raw = await adb.ExecOutAsync(serial, $"stat -c %s {q} && tail -c {TailSize} {q}", ct);

        int nl = Array.IndexOf(raw, (byte)'\n');
        if (nl <= 0 || nl > 20 || !long.TryParse(Encoding.ASCII.GetString(raw, 0, nl).Trim(), out var size))
            throw new IOException(Res.Get("ErrorReadApk") + path);

        var tail = raw.AsSpan(nl + 1).ToArray();
        if (tail.Length != Math.Min(size, TailSize))
            throw new IOException(Res.Get("ErrorApkIncomplete") + path);

        int eocd = -1;
        for (int i = tail.Length - 22; i >= 0; i--)
            if (Le.U32(tail, i) == 0x06054b50) { eocd = i; break; }
        if (eocd < 0) throw new InvalidDataException(Res.Get("ErrorZipNoDirectory") + path);

        long cdSize = Le.U32(tail, eocd + 12);
        long cdOffset = Le.U32(tail, eocd + 16);
        if (cdOffset == 0xFFFFFFFF) throw new NotSupportedException(Res.Get("ErrorZip64"));

        long tailStart = size - tail.Length;
        byte[] cd = cdOffset >= tailStart
            ? tail.AsSpan((int)(cdOffset - tailStart), (int)cdSize).ToArray()
            : (await ReadRangesAsync(adb, serial, path, size, [(cdOffset, cdSize)], ct))[0];

        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        int p = 0;
        while (p + 46 <= cd.Length && Le.U32(cd, p) == 0x02014b50)
        {
            int nameLen = Le.U16(cd, p + 28);
            int extraLen = Le.U16(cd, p + 30);
            int commentLen = Le.U16(cd, p + 32);
            var name = Encoding.UTF8.GetString(cd, p + 46, nameLen);
            entries[name] = new Entry(name, Le.U16(cd, p + 10), Le.U32(cd, p + 20), Le.U32(cd, p + 42), nameLen, extraLen);
            p += 46 + nameLen + extraLen + commentLen;
        }

        return new RemoteApk(adb, serial, path, size, entries);
    }

    public async Task<byte[]?> ReadAsync(string name, CancellationToken ct) =>
        (await ReadAsync([name], ct)).GetValueOrDefault(name);

    /// <summary>Читает несколько файлов одним вызовом adb.</summary>
    public async Task<Dictionary<string, byte[]>> ReadAsync(IReadOnlyCollection<string> names, CancellationToken ct)
    {
        var result = new Dictionary<string, byte[]>();
        var todo = new List<Entry>();
        foreach (var name in names)
        {
            if (_cache.TryGetValue(name, out var cached)) result[name] = cached;
            else if (_entries.TryGetValue(name, out var e)) todo.Add(e);
        }
        if (todo.Count == 0) return result;

        // Длина локального extra может отличаться от центрального — берём с запасом.
        var chunks = await ReadRangesAsync(_adb, _serial, _path, _size,
            todo.Select(e => (e.LocalOffset, 30L + e.NameLength + e.ExtraLength + e.CompressedSize + 512)).ToList(), ct);

        for (int i = 0; i < todo.Count; i++)
        {
            var e = todo[i];
            var chunk = chunks[i];
            if (chunk.Length < 30 || Le.U32(chunk, 0) != 0x04034b50)
                throw new InvalidDataException(Res.Get("ErrorZipLocalHeader") + e.Name);

            int dataStart = 30 + Le.U16(chunk, 26) + Le.U16(chunk, 28);
            byte[] compressed = dataStart + e.CompressedSize <= chunk.Length
                ? chunk.AsSpan(dataStart, (int)e.CompressedSize).ToArray()
                : (await ReadRangesAsync(_adb, _serial, _path, _size, [(e.LocalOffset + dataStart, e.CompressedSize)], ct))[0];

            var data = Inflate(compressed, e.Method);
            _cache[e.Name] = data;
            result[e.Name] = data;
        }
        return result;
    }

    private static byte[] Inflate(byte[] data, int method)
    {
        if (method == 0) return data;
        if (method != 8) throw new NotSupportedException(Res.Format("ErrorZipMethod", method));

        using var input = new MemoryStream(data);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>Читает диапазоны байтов файла: по одной команде dd на диапазон, всё в одном вызове adb.</summary>
    private static async Task<List<byte[]>> ReadRangesAsync(
        AdbService adb, string serial, string path, long size, IReadOnlyList<(long Offset, long Length)> ranges, CancellationToken ct)
    {
        var commands = new List<string>();
        var parts = new List<(int StartInBlock, int Length, long Expected)>();
        foreach (var (offset, length) in ranges)
        {
            long len = Math.Min(length, size - offset);
            if (len < 0) throw new InvalidDataException(Res.Get("ErrorOffsetOutOfFile"));
            long skip = offset / BlockSize;
            int startInBlock = (int)(offset % BlockSize);
            long count = (startInBlock + len + BlockSize - 1) / BlockSize;
            long expected = Math.Min(count * BlockSize, size - skip * BlockSize);

            commands.Add($"dd if={Quote(path)} bs={BlockSize} skip={skip} count={count} 2>/dev/null");
            parts.Add((startInBlock, (int)len, expected));
        }

        var raw = await adb.ExecOutAsync(serial, string.Join("; ", commands), ct);
        if (raw.Length != parts.Sum(p => p.Expected))
            throw new IOException(Res.Format("ErrorBytesExpected", parts.Sum(p => p.Expected), raw.Length));

        var result = new List<byte[]>();
        long pos = 0;
        foreach (var (startInBlock, length, expected) in parts)
        {
            result.Add(raw.AsSpan((int)(pos + startInBlock), length).ToArray());
            pos += expected;
        }
        return result;
    }

    private static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";
}
