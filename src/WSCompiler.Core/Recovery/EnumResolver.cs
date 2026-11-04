using System.IO;
using WSCompiler.Core.Scripts.Standalone;

namespace WSCompiler.Core.Scripts;

// Resolve enum numbers using the selected game's declarations, since extensions take the first unused value in that context.
public sealed partial class EnumResolver
{
    private static readonly Dictionary<string, Dictionary<string, Dictionary<long, string>>> VanillaCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly object CacheLock = new();

    private readonly Dictionary<string, Dictionary<long, string>> _local = new(StringComparer.Ordinal);
    private readonly HashSet<string> automatic = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<long, string>> _vanilla;

    public EnumResolver(ScriptBlob blob, IEnumerable<string>? selectedGameScripts = null)
        : this(blob, selectedGameScripts is not null ? ReadEnums(selectedGameScripts) :
            GameScripts.FindFolder(blob.FilePath) is { } directory ? LoadVanilla(directory) : [])
    {
    }

    public EnumResolver(ScriptBlob blob, Dictionary<string, Dictionary<long, string>> selectedGameEnums)
    {
        _vanilla = selectedGameEnums;
        foreach (var e in blob.Enums)
        {
            _local[e.Name] = Number(e, _vanilla.GetValueOrDefault(e.Name));
            if (e.Members.Any(member => member.Value == int.MaxValue)) automatic.Add(e.Name);
        }
    }

    private static Dictionary<long, string> Number(ScriptEnum e, Dictionary<long, string>? vanilla)
    {
        var map = vanilla is null ? new Dictionary<long, string>() : new Dictionary<long, string>(vanilla);
        var existing = map.Values.ToHashSet(StringComparer.Ordinal);
        long next = 0;
        foreach (var (name, value) in e.Members)
        {
            if (existing.Contains(name)) continue;
            long number;
            if (value != int.MaxValue) number = value;
            else if (vanilla is null) number = next;
            else
            {
                while (map.ContainsKey(next)) next = checked(next + 1);
                number = next;
            }
            map.TryAdd(number, name);
            existing.Add(name);
            next = Math.Max(next, checked(number + 1));
        }
        return map;
    }

    public bool IsExtension(ScriptEnum e) =>
        _vanilla.TryGetValue(e.Name, out var v) && v.Count > 0 && !v.Values.All(n => e.Members.Any(m => m.Name == n));

    public bool HasGameScripts => _vanilla.Count > 0;
    public bool HasAutomaticMembers(string enumType) => automatic.Contains(enumType);
    // Recovery must reject unresolved automatic numbering because missing context can change the enum's meaning.
    public bool RequireAutomaticBindings { get; init; }

    public string? Find(string enumType, long value)
    {
        if (_local.TryGetValue(enumType, out var map) && map.TryGetValue(value, out var n)) return n;
        if (_vanilla.TryGetValue(enumType, out map) && map.TryGetValue(value, out n)) return n;
        if (RequireAutomaticBindings && automatic.Contains(enumType))
            throw new InvalidDataException("The original automatic enum numbering context is unavailable: " + enumType + " = " + value + ".");
        return null;
    }

    // Compact script-enum bytes are signed. Apply sign extension only to that encoding.
    public long DecodeConstant(string enumType, long value, int storageBits)
    {
        if (storageBits != 8 || value is < 128 or > 255) return value;
        var values = _local.GetValueOrDefault(enumType) ?? _vanilla.GetValueOrDefault(enumType);
        long signed = (sbyte)(byte)value;
        return values is not null && values.ContainsKey(signed) && values.Keys.All(number => number is >= sbyte.MinValue and <= sbyte.MaxValue)
            ? signed : value;
    }

    private static Dictionary<string, Dictionary<long, string>> LoadVanilla(string dir)
    {
        lock (CacheLock)
        {
            if (VanillaCache.TryGetValue(dir, out var cached)) return cached;
            Dictionary<string, Dictionary<long, string>> result;
            try
            {
                result = ReadEnums(Directory.EnumerateFiles(dir, "*.ws", SearchOption.AllDirectories).Select(File.ReadAllText));
            }
            catch (IOException)
            {
                result = [];
            }
            VanillaCache[dir] = result;
            return result;
        }
    }

    public static Dictionary<string, Dictionary<long, string>> ReadEnums(IEnumerable<string> sources)
        => ReadEnums(sources.SelectMany(source => Parser.ParseSource(source, skipBodies: true)).OfType<EnumItem>().Select(item => item.Decl));

    // Later declarations extend earlier ones. Assign unnumbered members the first remaining values.
    public static Dictionary<string, Dictionary<long, string>> ReadEnums(IEnumerable<EnumDecl> declarations)
    {
        var result = new Dictionary<string, Dictionary<long, string>>(StringComparer.Ordinal);
        foreach (var declaration in declarations)
        {
            bool extension = result.TryGetValue(declaration.Name, out var map);
            if (!extension) result[declaration.Name] = map = [];
            var names = map!.Values.ToHashSet(StringComparer.Ordinal);
            long next = 0;
            foreach (var (name, number) in declaration.Members)
            {
                if (!names.Add(name)) continue;
                long value;
                if (number is { } explicitValue) value = explicitValue;
                else if (extension) { value = 0; while (map.ContainsKey(value)) value = checked(value + 1); }
                else value = next;
                map.TryAdd(value, name);
                next = Math.Max(next, checked(value + 1));
            }
        }
        return result;
    }
}
