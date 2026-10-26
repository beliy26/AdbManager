using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using AdbManager.Services;

namespace AdbManager.Apk;

internal static class Le
{
    public static ushort U16(byte[] d, int o) => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o, 2));
    public static uint U32(byte[] d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o, 4));
    public static int I32(byte[] d, int o) => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o, 4));
}

/// <summary>Типы значений Res_value (frameworks/base/libs/androidfw/ResourceTypes.h).</summary>
internal static class ResValueType
{
    public const byte Null = 0x00;
    public const byte Reference = 0x01;
    public const byte Attribute = 0x02;
    public const byte String = 0x03;
    public const byte Float = 0x04;
    public const byte Dimension = 0x05;
    public const byte Fraction = 0x06;
    public const byte DynamicReference = 0x07;
    public const byte IntDec = 0x10;
    public const byte IntHex = 0x11;
    public const byte IntBoolean = 0x12;
    public const byte ColorFirst = 0x1c;
    public const byte ColorLast = 0x1f;

    public static bool IsReference(byte t) => t is Reference or DynamicReference;
    public static bool IsColor(byte t) => t is >= ColorFirst and <= ColorLast;

    /// <summary>Распаковка complex-значения (dimension / fraction).</summary>
    public static float Complex(uint c)
    {
        ReadOnlySpan<float> mults = [1f / (1 << 8), 1f / (1 << 15), 1f / (1 << 23), 1f / (1L << 31)];
        return (int)(c & 0xFFFFFF00) * mults[(int)((c >> 4) & 3)];
    }
}

/// <summary>Идентификаторы атрибутов android:* — на случай, если имена атрибутов вырезаны обфускатором.</summary>
internal static class Attrs
{
    public const uint Label = 0x01010001;
    public const uint Icon = 0x01010002;
    public const uint Name = 0x01010003;
    public const uint Src = 0x01010119;
    public const uint Drawable = 0x01010199;
    public const uint Color = 0x010101a5;
    public const uint InsetLeft = 0x010101b7;
    public const uint Alpha = 0x0101031f;
    public const uint ViewportWidth = 0x01010402;
    public const uint ViewportHeight = 0x01010403;
    public const uint FillColor = 0x01010404;
    public const uint PathData = 0x01010405;
    public const uint StrokeColor = 0x01010406;
    public const uint StrokeWidth = 0x01010407;
    public const uint RoundIcon = 0x0101052c;
}

/// <summary>Пул строк ResStringPool (UTF-8 или UTF-16).</summary>
internal sealed class StringPool
{
    private readonly byte[] _d;
    private readonly int _offsetsStart;
    private readonly int _stringsStart;
    private readonly bool _utf8;
    private readonly string?[] _cache;

    public int Count { get; }

    public StringPool(byte[] d, int chunk)
    {
        _d = d;
        Count = Le.I32(d, chunk + 8);
        _utf8 = (Le.U32(d, chunk + 16) & 0x100) != 0;
        _stringsStart = chunk + Le.I32(d, chunk + 20);
        _offsetsStart = chunk + Le.U16(d, chunk + 2);
        _cache = new string?[Count];
    }

    public string Get(int i)
    {
        if ((uint)i >= (uint)Count) return "";
        if (_cache[i] is { } cached) return cached;

        int p = _stringsStart + Le.I32(_d, _offsetsStart + i * 4);
        string s;
        if (_utf8)
        {
            ReadLen8(ref p); // длина в UTF-16 символах — не нужна
            int n = ReadLen8(ref p);
            s = Encoding.UTF8.GetString(_d, p, n);
        }
        else
        {
            int n = Le.U16(_d, p);
            p += 2;
            if ((n & 0x8000) != 0)
            {
                n = ((n & 0x7FFF) << 16) | Le.U16(_d, p);
                p += 2;
            }
            s = Encoding.Unicode.GetString(_d, p, n * 2);
        }
        return _cache[i] = s;
    }

    private int ReadLen8(ref int p)
    {
        int n = _d[p++];
        if ((n & 0x80) != 0) n = ((n & 0x7F) << 8) | _d[p++];
        return n;
    }
}

// ======================= Бинарный XML (AndroidManifest.xml, res/*.xml) =======================

internal sealed record AxmlAttr(string Name, uint ResId, string? Raw, byte Type, uint Data)
{
    public bool IsReference => ResValueType.IsReference(Type);
    public bool IsColor => ResValueType.IsColor(Type);

    public int? Int => Type is ResValueType.IntDec or ResValueType.IntHex or ResValueType.IntBoolean
        ? (int)Data
        : int.TryParse(Raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;

    public float? Float => Type switch
    {
        ResValueType.Float => BitConverter.Int32BitsToSingle((int)Data),
        ResValueType.Dimension or ResValueType.Fraction => ResValueType.Complex(Data),
        ResValueType.IntDec or ResValueType.IntHex => (int)Data,
        _ => float.TryParse(Raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : null,
    };
}

internal sealed class AxmlElement(string name)
{
    public string Name { get; } = name;
    public List<AxmlAttr> Attributes { get; } = [];
    public List<AxmlElement> Children { get; } = [];

    public AxmlElement? Child(string name) => Children.FirstOrDefault(c => c.Name == name);

    /// <summary>Ищет атрибут по имени; по id — только если имена атрибутов вырезаны.</summary>
    public AxmlAttr? Attr(string name, uint resId = 0) =>
        Attributes.FirstOrDefault(a => a.Name == name)
        ?? (resId == 0 ? null : Attributes.FirstOrDefault(a => a.Name.Length == 0 && a.ResId == resId));
}

internal static class AxmlParser
{
    public static AxmlElement Parse(byte[] d)
    {
        if (d.Length < 8 || Le.U16(d, 0) != 0x0003)
            throw new InvalidDataException(Res.Get("ErrorNotBinaryXml"));

        StringPool? pool = null;
        uint[] resIds = [];
        AxmlElement? root = null;
        var stack = new Stack<AxmlElement>();

        int end = Math.Min(Le.I32(d, 4), d.Length);
        int pos = Le.U16(d, 2);
        while (pos + 8 <= end)
        {
            int type = Le.U16(d, pos);
            int headerSize = Le.U16(d, pos + 2);
            int size = Le.I32(d, pos + 4);
            if (size <= 0) break;

            switch (type)
            {
                case 0x0001:
                    pool = new StringPool(d, pos);
                    break;
                case 0x0180:
                    resIds = new uint[(size - headerSize) / 4];
                    for (int i = 0; i < resIds.Length; i++) resIds[i] = Le.U32(d, pos + headerSize + i * 4);
                    break;
                case 0x0102 when pool is not null:
                {
                    int ext = pos + headerSize;
                    var el = new AxmlElement(pool.Get(Le.I32(d, ext + 4)));
                    int attrStart = Le.U16(d, ext + 8);
                    int attrSize = Le.U16(d, ext + 10);
                    int attrCount = Le.U16(d, ext + 12);
                    for (int i = 0; i < attrCount; i++)
                    {
                        int a = ext + attrStart + i * attrSize;
                        int nameIdx = Le.I32(d, a + 4);
                        int rawIdx = Le.I32(d, a + 8);
                        el.Attributes.Add(new AxmlAttr(
                            pool.Get(nameIdx),
                            (uint)nameIdx < (uint)resIds.Length ? resIds[nameIdx] : 0,
                            rawIdx >= 0 ? pool.Get(rawIdx) : null,
                            d[a + 15],
                            Le.U32(d, a + 16)));
                    }

                    if (stack.TryPeek(out var parent)) parent.Children.Add(el);
                    else root ??= el;
                    stack.Push(el);
                    break;
                }
                case 0x0103:
                    stack.TryPop(out _);
                    break;
            }
            pos += size;
        }

        return root ?? throw new InvalidDataException(Res.Get("ErrorEmptyXml"));
    }
}

// ======================= Таблица ресурсов (resources.arsc) =======================

internal readonly record struct ResConfig(int Density, string Language, string Country, int Sdk, bool Night);

internal readonly record struct ResEntry(ResConfig Config, byte Type, uint Data)
{
    public bool IsReference => ResValueType.IsReference(Type);
    public bool IsColor => ResValueType.IsColor(Type);
}

internal sealed class ResTable
{
    private readonly byte[] _d;
    private readonly StringPool? _strings;
    // id пакета → id типа → смещения чанков ResTable_type
    private readonly Dictionary<int, Dictionary<int, List<int>>> _packages = [];

    public ResTable(byte[] d)
    {
        if (d.Length < 12 || Le.U16(d, 0) != 0x0002)
            throw new InvalidDataException(Res.Get("ErrorNotResourcesArsc"));
        _d = d;

        int end = Math.Min(Le.I32(d, 4), d.Length);
        int pos = Le.U16(d, 2);
        while (pos + 8 <= end)
        {
            int type = Le.U16(d, pos);
            int size = Le.I32(d, pos + 4);
            if (size <= 0) break;
            if (type == 0x0001 && _strings is null) _strings = new StringPool(d, pos);
            else if (type == 0x0200) ParsePackage(pos, Math.Min(pos + size, end));
            pos += size;
        }
    }

    private void ParsePackage(int start, int end)
    {
        var types = new Dictionary<int, List<int>>();
        _packages[Le.I32(_d, start + 8)] = types;

        int pos = start + Le.U16(_d, start + 2);
        while (pos + 8 <= end)
        {
            int type = Le.U16(_d, pos);
            int size = Le.I32(_d, pos + 4);
            if (size <= 0) break;
            if (type == 0x0201)
            {
                int typeId = _d[pos + 8];
                if (!types.TryGetValue(typeId, out var list)) types[typeId] = list = [];
                list.Add(pos);
            }
            pos += size;
        }
    }

    public string GetString(uint index) => _strings?.Get((int)index) ?? "";

    /// <summary>Все значения ресурса во всех конфигурациях (плотность, язык и т.д.).</summary>
    public List<ResEntry> Lookup(uint resId)
    {
        var result = new List<ResEntry>();
        if (!_packages.TryGetValue((int)(resId >> 24), out var types))
            return result; // например, ресурсы фреймворка android (0x01xxxxxx)
        if (!types.TryGetValue((int)((resId >> 16) & 0xFF), out var chunks)) return result;

        int index = (int)(resId & 0xFFFF);
        foreach (var c in chunks)
        {
            try
            {
                if (ReadEntry(c, index) is { } e) result.Add(e);
            }
            catch (ArgumentOutOfRangeException) { /* битый чанк — пропускаем */ }
        }
        return result;
    }

    private ResEntry? ReadEntry(int c, int index)
    {
        int headerSize = Le.U16(_d, c + 2);
        int flags = _d[c + 9];
        int entryCount = Le.I32(_d, c + 12);
        int entriesStart = Le.I32(_d, c + 16);
        int offsetsStart = c + headerSize;

        long offset = -1;
        if ((flags & 0x01) != 0) // sparse: пары (индекс u16, смещение/4 u16)
        {
            for (int i = 0; i < entryCount; i++)
            {
                int p = offsetsStart + i * 4;
                if (Le.U16(_d, p) == index) { offset = Le.U16(_d, p + 2) * 4L; break; }
            }
        }
        else if ((flags & 0x02) != 0) // offset16
        {
            if (index >= entryCount) return null;
            int v = Le.U16(_d, offsetsStart + index * 2);
            if (v != 0xFFFF) offset = v * 4L;
        }
        else
        {
            if (index >= entryCount) return null;
            uint v = Le.U32(_d, offsetsStart + index * 4);
            if (v != 0xFFFFFFFF) offset = v;
        }
        if (offset < 0) return null;

        int entry = c + entriesStart + (int)offset;
        int entryFlags = Le.U16(_d, entry + 2);
        byte type;
        uint data;
        if ((entryFlags & 0x0008) != 0) // compact entry
        {
            type = (byte)(entryFlags >> 8);
            data = Le.U32(_d, entry + 4);
        }
        else if ((entryFlags & 0x0001) != 0) // complex (стили, массивы) — не нужны
        {
            return null;
        }
        else
        {
            int value = entry + Le.U16(_d, entry);
            type = _d[value + 3];
            data = Le.U32(_d, value + 4);
        }

        return new ResEntry(ReadConfig(c + 20), type, data);
    }

    private ResConfig ReadConfig(int p)
    {
        int size = Le.I32(_d, p);
        string Chars(int o) => _d[o] == 0 || (_d[o] & 0x80) != 0
            ? ""
            : Encoding.ASCII.GetString(_d, o, _d[o + 1] == 0 ? 1 : 2);

        return new ResConfig(
            Density: size >= 16 ? Le.U16(_d, p + 14) : 0,
            Language: size >= 12 ? Chars(p + 8) : "",
            Country: size >= 12 ? Chars(p + 10) : "",
            Sdk: size >= 28 ? Le.U16(_d, p + 24) : 0,
            Night: size >= 32 && (_d[p + 29] & 0x30) == 0x20);
    }
}
