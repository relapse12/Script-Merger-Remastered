using WSCompiler.Core.Scripts;
using WSCompiler.Core.Scripts.Standalone;

namespace WSCompiler;

// Reads declarations and dependencies directly from compiled blob tables.
public static class BlobReferences
{
    public static IReadOnlyList<string> LinkedSymbols(string path)
        => LinkedSymbols(ScriptBlob.Open(path));
    public static IReadOnlyList<string> LinkedSymbols(byte[] data, string identity)
        => LinkedSymbols(ScriptBlob.FromBytes(data, identity));
    // Virtual calls encode the method name without its class. Keep possible owners for later resolution.
    // Blob type names also need checking against each possible declaration kind.
    private static IReadOnlyList<string> LinkedSymbols(ScriptBlob blob)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var declaration in blob.Classes)
            foreach (string owner in new[] { declaration.Name, declaration.Base, declaration.StateOf }.Where(name => name.Length > 0)) result.Add("owner:" + owner);
        foreach (var function in blob.AllFunctions.Where(function => function.Owner.Length > 0)) result.Add("owner:" + function.Owner);
        foreach (var type in blob.Types)
        {
            AddType(Type(type.Name));
        }
        foreach (var function in blob.FunctionRefs)
        {
            string owner = function.OwnerType >= 0 && function.OwnerType < blob.Types.Count ? ScriptTypeNames.Format(blob.Types[function.OwnerType].Name) : "";
            result.Add("function:" + (owner.Length == 0 ? "" : owner + ".") + function.Name);
        }
        foreach (var property in blob.ImportedProperties)
            if (property.OwnerType >= 0 && property.OwnerType < blob.Types.Count)
                result.Add("field:" + ScriptTypeNames.Format(blob.Types[property.OwnerType].Name) + "." + property.Name);
        foreach (var function in blob.AllFunctions)
            foreach (var instruction in Bytecode.Decode(function.Code))
                if (instruction.Op is Op.VirtualFunc or Op.VirtualParentFunc && instruction.Args.Length > 2)
                {
                    int index = instruction.Args[2];
                    if ((uint)index >= (uint)blob.Names.Length) throw new InvalidDataException("A compiled call has an invalid name reference.");
                    result.Add("method:" + blob.Names[index]);
                }
        foreach (var function in blob.Wrapped.Concat(blob.Replaced).Concat(blob.ReplacedGlobals))
            result.Add("function:" + (function.Owner.Length == 0 ? "" : function.Owner + ".") + function.Name);
        return result.ToArray();

        void AddType(TypeRef type)
        {
            result.Add("type:" + type.Name); result.Add("enum:" + type.Name);
            result.Add("owner:" + type.Name);
            foreach (var argument in type.Args) AddType(argument);
        }
    }

    // Compilation references need signatures only, so these functions have no bodies.
    public static SourceFile Read(string path)
        => Read(ScriptBlob.Open(path));
    public static SourceFile Read(byte[] data, string identity) => Read(ScriptBlob.FromBytes(data, identity));
    private static SourceFile Read(ScriptBlob blob)
    {
        var items = new List<Item>();
        foreach (var e in blob.Enums)
            items.Add(new EnumItem(new EnumDecl { Name = e.Name, Members = e.Members.Select(m => (m.Name, m.Value == int.MaxValue ? (long?)null : m.Value)).ToList() }));
        foreach (var s in blob.Structs)
            items.Add(new ClassItem(new ClassDecl { Name = s.Name, IsStruct = true, IsImport = (s.Flags & 2) != 0, Fields = s.Properties.Select(Field).ToList() }));
        foreach (var c in blob.Classes)
            items.Add(new ClassItem(new ClassDecl
            {
                Name = c.Name, Parent = string.IsNullOrEmpty(c.Base) ? null : c.Base,
                StateMachineOf = c.IsState ? c.StateOf : null,
                IsImport = (c.Flags & 2) != 0, IsAbstract = (c.Flags & 1) != 0, IsStateMachine = (c.Flags & 0x400) != 0,
                Fields = c.Properties.Select(Field).ToList(), Methods = c.Functions.Select(Function).ToList()
            }));
        foreach (var f in blob.Globals) items.Add(new FunctionItem(Function(f)));
        foreach (var f in blob.AddedMethods)
        {
            var declaration = Function(f);
            declaration.Annotation = new(AnnotationKind.AddMethod, f.Owner);
            items.Add(new FunctionItem(declaration));
        }
        foreach (var field in blob.AddedFields)
        {
            var declaration = Field(field);
            declaration.Annotation = new(AnnotationKind.AddField, field.Owner);
            items.Add(new FieldItem(declaration));
        }
        return new(blob.FilePath, items);
    }

    // Blob handle and pointer prefixes have no equivalent in source type names.
    public static TypeRef Type(string raw)
    {
        if (raw.StartsWith("handle:") || raw.StartsWith("ptr:")) return new(raw[(raw.IndexOf(':') + 1)..]);
        if (raw.StartsWith("array:")) return new("array", [Type(raw.Split(',', 3)[2])]);
        if (raw.StartsWith("map:"))
        {
            string rest = raw.Split(',', 3)[2];
            int split = TypeEnd(rest, 0);
            if (split >= rest.Length || rest[split] != ',') throw new InvalidDataException($"Invalid map reference type: {raw}");
            return new("map", [Type(rest[..split]), Type(rest[(split + 1)..])]);
        }
        return new(raw);
    }

    private static int TypeEnd(string text, int start)
    {
        if (text.AsSpan(start).StartsWith("array:") || text.AsSpan(start).StartsWith("map:"))
        {
            bool map = text.AsSpan(start).StartsWith("map:");
            int first = text.IndexOf(',', start), second = first < 0 ? -1 : text.IndexOf(',', first + 1);
            if (second < 0) throw new InvalidDataException("Invalid nested reference type");
            int end = TypeEnd(text, second + 1);
            return map && end < text.Length ? TypeEnd(text, end + 1) : end;
        }
        int separator = text.IndexOf(',', start);
        return separator < 0 ? text.Length : separator;
    }

    private static FieldDecl Field(ScriptProperty p) => new()
    {
        Names = [p.Name], Type = Type(p.Type), IsBinding = (p.Flags & 0x80000) != 0,
        Specifiers = new() { IsImport = (p.Flags & 0x4000) != 0, IsConst = (p.Flags & 2) != 0, IsSaved = (p.Flags & 0x8000) != 0 }
    };

    private static FunctionDecl Function(ScriptFunction f) => new()
    {
        Name = f.Name, Params = f.Params.Select(p => new Param(p.Name, Type(p.Type), (p.Flags & 0x200) != 0, (p.Flags & 0x400) != 0)).ToList(),
        ReturnType = f.Return.Count > 0 ? Type(f.Return[0].Type) : null,
        Specifiers = new()
        {
            IsImport = (f.Flags & 1) != 0 || (f.Flags & 0x200) != 0 && f.Code.Length == 0,
            IsFinal = (f.Flags & 0x10) != 0, IsEvent = (f.Flags & 0x20) != 0,
            IsLatent = (f.Flags & 0x40) != 0, IsEntry = (f.Flags & 0x80) != 0, IsTimer = (f.Flags & 0x400) != 0,
            IsExec = (f.Flags & 0x100) != 0, IsQuest = (f.Flags & 0x1000) != 0, IsCleanup = (f.Flags & 0x2000) != 0,
            IsStoryScene = (f.Flags & 0x800) != 0, IsReward = (f.Flags & 0x20000) != 0
        }
    };
}
