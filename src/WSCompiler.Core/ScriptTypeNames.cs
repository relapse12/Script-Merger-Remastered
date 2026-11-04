namespace WSCompiler.Core.Scripts;

// Converts blob type strings such as "array:2,0,Int32" to script syntax such as "array<int>".
public static class ScriptTypeNames
{
    public static string Format(string type)
    {
        if (type.StartsWith("map:", StringComparison.Ordinal))
        {
            string[] parts = type.Split(',');
            int position = 0;
            return Read(parts, ref position);
        }
        if (type.StartsWith("array:", StringComparison.Ordinal))
        {
            int comma = type.IndexOf(',', type.IndexOf(',') + 1);
            return $"array<{Format(comma > 0 ? type[(comma + 1)..] : type[6..])}>";
        }
        foreach (var prefix in new[] { "handle:", "ptr:", "soft:" })
            if (type.StartsWith(prefix, StringComparison.Ordinal)) return Format(type[prefix.Length..]);
        return type switch
        {
            "Int32" => "int", "Float" => "float", "Bool" => "bool", "String" => "string", "CName" => "name", "Uint8" => "byte", _ => type
        };
    }
    private static string Read(string[] parts, ref int position)
    {
        string value = parts[position++];
        if (value is "map:2" or "array:2")
        {
            if (position >= parts.Length || parts[position++] != "0") throw new InvalidDataException("Invalid container type metadata.");
            string first = Read(parts, ref position);
            return value == "array:2" ? $"array<{first}>" : $"map<{first}, {Read(parts, ref position)}>";
        }
        return Format(value);
    }
}
