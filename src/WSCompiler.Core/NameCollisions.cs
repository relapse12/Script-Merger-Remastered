using System.Text;
using WSCompiler.Core.Scripts.Standalone;

namespace WSCompiler;

// Warns when two names share the engine's 32-bit name hash, since the engine could then confuse them.
public static class NameCollisions
{
    // FNV-1a over the UTF-8 bytes.
    public static uint Hash(string name)
    {
        uint hash = 2166136261;
        foreach (byte value in Encoding.UTF8.GetBytes(name)) hash = unchecked((hash ^ value) * 16777619);
        return hash;
    }

    // Report only collisions involving names emitted by this blob.
    public static IEnumerable<Diagnostic> Find(byte[] blob, Symbols symbols)
    {
        var names = new Dictionary<uint, HashSet<string>>();
        void Add(string name)
        {
            uint hash = Hash(name);
            if (!names.TryGetValue(hash, out var bucket)) names[hash] = bucket = new(StringComparer.Ordinal);
            bucket.Add(name);
        }
        foreach (string name in symbols.Classes.Keys.Concat(symbols.Enums.Keys).Concat(symbols.EnumMembers.Keys).Concat(symbols.Functions.Keys)) Add(name);
        foreach (var cls in symbols.Classes.Values)
            foreach (string name in cls.Methods.Keys.Concat(cls.Fields.Keys)) Add(name);
        int offset = BitConverter.ToInt32(blob, blob.Length - 4);
        var table = new WSCompiler.Core.Scripts.BlobReader(blob, offset);
        int count = table.U32();
        var emitted = new HashSet<uint>();
        for (int i = 0; i < count; i++) { string name = table.Str(); Add(name); emitted.Add(Hash(name)); }
        foreach (uint hash in emitted.Order())
            if (names[hash].Count > 1)
                yield return new("warning", "N001", null, 0, $"Engine name hash collision 0x{hash:X8}: {string.Join(" / ", names[hash].Order(StringComparer.Ordinal))}. Use distinct names to avoid the engine treating them as the same name.");
    }
}
