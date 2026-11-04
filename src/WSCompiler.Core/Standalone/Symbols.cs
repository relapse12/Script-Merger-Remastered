
namespace WSCompiler.Core.Scripts.Standalone;

public enum TyKind { Void, Bool, Int, Float, Byte, String, Name, Null, Primitive, Enum, Struct, Class, Array, Map }

public sealed record Ty(TyKind Kind, string Name = "", Ty? Element = null, Ty? Key = null)
{
    public string? NativeName { get; init; }
    public static readonly Ty Void = new(TyKind.Void), Bool = new(TyKind.Bool), Int = new(TyKind.Int),
        Float = new(TyKind.Float), Byte = new(TyKind.Byte), String = new(TyKind.String), Name_ = new(TyKind.Name),
        Null = new(TyKind.Null);

    public static Ty Class(string name) => new(TyKind.Class, name);
    public static Ty Struct(string name) => new(TyKind.Struct, name);
    public static Ty Enum(string name) => new(TyKind.Enum, name);
    public static Ty Primitive(string name) => new(TyKind.Primitive, name);
    public static Ty ArrayOf(Ty element) => new(TyKind.Array, "", element);
    public static Ty MapOf(Ty key, Ty value) => new(TyKind.Map, "", value, key);

    public bool IsClass => Kind is TyKind.Class or TyKind.Null;

    public string RttiName => NativeName ?? (Kind switch
    {
        TyKind.Void => "void",
        TyKind.Bool => "Bool",
        TyKind.Int => "Int32",
        TyKind.Float => "Float",
        TyKind.Byte => "Uint8",
        TyKind.String => "String",
        TyKind.Name => "CName",
        TyKind.Null => "NULL",
        TyKind.Array => $"array:2,0,{Element!.RttiName}",
        TyKind.Map => $"map:2,0,{Key!.RttiName},{Element!.RttiName}",
        _ => Name,
    });

    public override string ToString() => Kind switch
    {
        TyKind.Array => $"array<{Element}>",
        TyKind.Map => $"map<{Key}, {Element}>",
        TyKind.Primitive or TyKind.Enum or TyKind.Struct or TyKind.Class => Name,
        _ => RttiName,
    };
}

public sealed record FieldInfo(string Name, Ty Type, string Owner, bool IsBindable = false, bool IsImport = false, bool IsConst = false);

public sealed record ParamInfo(string Name, Ty Type, bool IsOptional, bool IsOut);

public sealed class FunctionInfo
{
    public required string Name { get; init; }
    public string? Owner { get; init; }
    public required List<ParamInfo> Params { get; init; }
    public required Ty ReturnType { get; init; }
    public required FunctionSpecifiers Specifiers { get; init; }

    // Native signatures absent from RTTI pass arguments through unchanged.
    public bool IsOpaque { get; init; }

    // Globals and final methods bind statically; other methods dispatch at runtime.
    public bool IsFinalCall => Owner is null || Specifiers.IsFinal;
}

public sealed class ClassInfo
{
    public required string Name { get; init; }
    public required string BlobName { get; init; }
    public string? Parent { get; set; }
    public string? StateParentName { get; init; }
    public string ShortName { get; init; } = "";
    public bool IsStruct { get; init; }
    public string? StateMachine { get; init; }
    public int Rank { get; init; }
    public Dictionary<string, FieldInfo> Fields { get; } = [];
    public List<string> FieldOrder { get; } = [];
    public Dictionary<string, FunctionInfo> Methods { get; } = [];
}

public sealed class EnumInfo
{
    public required string Name { get; init; }
    public required List<(string Name, int Value)> Values { get; init; }
    public bool IsScript { get; init; }

    public int? ValueOf(string member)
    {
        foreach (var (name, value) in Values)
            if (name == member) return value;
        return null;
    }
}

// Binds script declarations and native metadata in the order chosen by Compiler.CompilePrepared.
public sealed class Symbols
{
    public Dictionary<string, ClassInfo> Classes { get; } = [];
    public Dictionary<string, EnumInfo> Enums { get; } = [];
    public Dictionary<string, string> EnumMembers { get; } = [];
    public Dictionary<string, FunctionInfo> Functions { get; } = [];
    public HashSet<string> SimpleTypes { get; } = [];

    public static string StateClassKey(string machine, string state) => $"{machine}State{state}";

    public static string ClassKey(ClassDecl decl) =>
        decl.StateMachineOf is { } machine ? StateClassKey(machine, decl.Name) : decl.Name;

    // Script enum declarations take precedence over native ones.
    public void AddEnums(IEnumerable<SourceFile> files, bool isScript)
    {
        foreach (var file in files)
            foreach (var item in file.Items.OfType<EnumItem>())
            {
                var decl = item.Decl;
                if (!isScript && Enums.ContainsKey(decl.Name)) continue;
                long next = 0;
                var values = new List<(string, int)>();
                foreach (var (name, value) in decl.Members)
                {
                    long v = value ?? next;
                    next = Math.Max(next, v + 1);
                    values.Add((name, (int)v));
                }
                foreach (var (member, _) in values) EnumMembers[member] = decl.Name;
                Enums[decl.Name] = new EnumInfo { Name = decl.Name, Values = values, IsScript = isScript };
            }
    }

    // Extensions preserve existing member values. Report conflicting explicit values through problem.
    public void ExtendEnums(IEnumerable<SourceFile> files, Action<SourceFile, int, string>? problem = null)
    {
        foreach (var file in files)
            foreach (var item in file.Items.OfType<EnumItem>())
            {
                var declaration = item.Decl;
                if (!Enums.TryGetValue(declaration.Name, out var original))
                {
                    AddEnums([new SourceFile(file.Path, [item])], true);
                    continue;
                }
                var values = original.Values.ToList();
                var occupied = values.Select(v => v.Value).ToHashSet();
                int next = 0;
                for (int index = 0; index < declaration.Members.Count; index++)
                {
                    var (name, explicitValue) = declaration.Members[index];
                    int line = index < declaration.Lines.Count ? declaration.Lines[index] : 1;
                    if (values.FirstOrDefault(v => v.Name == name) is var previous && previous.Name is not null)
                    {
                        if (problem is not null && explicitValue is { } given && given != previous.Value)
                            problem(file, line, $"Enumerator '{name}' in '{declaration.Name}' has an explicit value, but it does not match previous definition.");
                        continue;
                    }
                    if (problem is not null && explicitValue is not null)
                    {
                        problem(file, line, $"Enumerator '{name}' in '{declaration.Name}' has an explicit value, that is forbidden for new enumerators defined in enum extensions (mods) to prevent conflicts.");
                        continue;
                    }
                    int value;
                    if (explicitValue is { } number) value = checked((int)number);
                    else
                    {
                        while (occupied.Contains(next)) next = checked(next + 1);
                        value = next;
                    }
                    values.Add((name, value));
                    occupied.Add(value);
                }
                foreach (var (name, _) in values) EnumMembers[name] = declaration.Name;
                Enums[declaration.Name] = new EnumInfo { Name = declaration.Name, Values = values, IsScript = original.IsScript };
            }
    }

    // Register names before resolving members. Add project types first because the first definition wins.
    public void AddTypeNames(IEnumerable<SourceFile> files)
    {
        foreach (var file in files)
            foreach (var item in file.Items.OfType<ClassItem>())
            {
                var decl = item.Decl;
                string key = ClassKey(decl);
                if (Classes.ContainsKey(key)) continue;

                string? parent;
                if (decl.StateMachineOf is { } machine)
                    parent = decl.Parent is { } p ? StateClassKey(machine, p) : "CScriptableState";
                else if (decl.Parent is null && !decl.IsStruct && decl.Name is not ("IScriptable" or "ISerializable"))
                    parent = decl.Name == "CObject" ? "IScriptable" : "CObject";
                else
                    parent = decl.Parent;

                Classes[key] = new ClassInfo
                {
                    Name = key,
                    BlobName = key,
                    Parent = parent,
                    IsStruct = decl.IsStruct,
                    StateMachine = decl.StateMachineOf,
                    StateParentName = decl.StateMachineOf is null ? null : decl.Parent,
                    ShortName = decl.Name,
                    Rank = Classes.Count,
                };
            }
    }

    public void ResolveStateParents()
    {
        foreach (var info in Classes.Values)
        {
            if (info.StateMachine is not { } machine) continue;
            // A state can inherit its namesake from the base state machine even without an explicit extends.
            string wanted = info.StateParentName ?? info.ShortName;
            string? current = machine;
            for (int depth = 0; current is not null && depth <= 64; depth++)
            {
                string key = StateClassKey(current, wanted);
                if (key != info.Name && Classes.ContainsKey(key))
                {
                    info.Parent = key;
                    break;
                }
                current = Classes.TryGetValue(current, out var up) ? up.Parent : null;
            }
        }
    }

    public void AddMembers(IEnumerable<SourceFile> files, Action<SourceFile, string>? problem = null)
    {
        foreach (var file in files)
        {
            foreach (var item in file.Items)
            {
                try
                {
                    AddMembersOf(item);
                }
                catch (CompileException ex)
                {
                    problem?.Invoke(file, ex.Message);
                }
            }
        }
    }

    // Replacements and wrappers use existing declarations; only @addMethod introduces a method here.
    private void AddMembersOf(Item item)
    {
        switch (item)
        {
            case ClassItem { Decl: var decl }:
            {
                string key = ClassKey(decl);
                foreach (var field in decl.Fields)
                {
                    var ty = Resolve(field.Type);
                    foreach (string name in field.Names) AddField(key, name, ty, field.IsBinding, field.Specifiers.IsImport, field.Specifiers.IsConst || field.IsBinding);
                }
                foreach (var method in decl.Methods)
                {
                    var info = FunctionInfoOf(method, key);
                    Classes[key].Methods.TryAdd(method.Name, info);
                }
                break;
            }
            case FunctionItem { Decl: var decl }:
            {
                var annotation = decl.Annotation;
                if (annotation is null)
                    Functions.TryAdd(decl.Name, FunctionInfoOf(decl, null));
                else if (annotation is { Kind: AnnotationKind.AddMethod, Class: { } cls })
                {
                    if (!Classes.TryGetValue(cls, out var target)) throw new CompileException($"@addMethod: unknown class {cls}");
                    target.Methods[decl.Name] = FunctionInfoOf(decl, cls);
                }
                break;
            }
            case FieldItem { Decl: var field }:
            {
                if (field.Annotation?.Class is not { } cls) break;
                var ty = Resolve(field.Type);
                foreach (string name in field.Names) AddField(cls, name, ty, isConst: field.Specifiers.IsConst);
                break;
            }
        }
    }

    public void AddRttiEnums(Rtti rtti)
    {
        foreach (var native in rtti.Enums)
        {
            if (Enums.ContainsKey(native.Name)) continue;
            foreach (var (member, _) in native.Options) EnumMembers.TryAdd(member, native.Name);
            Enums[native.Name] = new EnumInfo { Name = native.Name, Values = [.. native.Options], IsScript = false };
        }
    }

    // Native metadata fills gaps left by script declarations.
    public void AddRttiClasses(Rtti rtti)
    {
        foreach (string simple in rtti.SimpleTypes) SimpleTypes.Add(simple);
        foreach (var native in rtti.Classes)
        {
            if (Classes.ContainsKey(native.Name)) continue;
            Classes[native.Name] = new ClassInfo
            {
                Name = native.Name,
                BlobName = native.Name,
                Parent = native.Base,
                IsStruct = native.IsStruct,
                Rank = Classes.Count,
            };
        }
    }

    // Imported fields use their native types and retain their declaring ancestor as owner.
    public void AddRttiProperties(Rtti rtti)
    {
        var declared = rtti.Classes
            .GroupBy(c => c.Name)
            .ToDictionary(g => g.Key, g => (Base: g.First().Base, Properties: g.First().Properties.Select(p => p.Name).ToHashSet()));

        bool Inherited(string cls, string property)
        {
            string? current = declared.GetValueOrDefault(cls).Base;
            int guard = 0;
            while (current is not null && guard++ < 64)
            {
                if (!declared.TryGetValue(current, out var info)) return false;
                if (info.Properties.Contains(property)) return true;
                current = info.Base;
            }
            return false;
        }

        foreach (var native in rtti.Classes)
            foreach (var (name, typeName) in native.Properties)
            {
                if (Inherited(native.Name, name)) continue;
                if (Classes.GetValueOrDefault(native.Name)?.Parent is { } parent && FindField(parent, name) is not null) continue;
                var actual = ResolveRtti(typeName);
                if (Classes.TryGetValue(native.Name, out var owner) && owner.Fields.TryGetValue(name, out var previous)
                    && previous.IsImport && actual.NativeName is not null && SameShape(previous.Type, actual))
                    owner.Fields[name] = previous with { Type = actual };
                else AddField(native.Name, name, actual);
            }
    }

    private static bool SameShape(Ty left, Ty right) => left.Kind == right.Kind && left.Name == right.Name
        && (left.Element is null ? right.Element is null : right.Element is not null && SameShape(left.Element, right.Element))
        && (left.Key is null ? right.Key is null : right.Key is not null && SameShape(left.Key, right.Key));

    // Missing native signatures need opaque declarations; argument types cannot be checked for those functions.
    public void AddRttiFunctions(Rtti rtti)
    {
        foreach (string name in rtti.GlobalFunctions)
            Functions.TryAdd(name, Opaque(name, null));

        foreach (var native in rtti.Classes)
        {
            if (!Classes.TryGetValue(native.Name, out var info)) continue;
            foreach (string name in native.Methods)
                if (FindMethod(native.Name, name) is null)
                    info.Methods[name] = Opaque(name, native.Name);
        }
    }

    private static FunctionInfo Opaque(string name, string? owner) => new()
    {
        Name = name,
        Owner = owner,
        Params = [],
        ReturnType = Ty.Void,
        Specifiers = new FunctionSpecifiers(),
        IsOpaque = true,
    };

    // Carry unresolved native types as primitives so they can still appear in declarations.
    private Ty ResolveRtti(string name)
    {
        if (name.StartsWith("array:"))
        {
            string[] parts = name["array:".Length..].Split(',', 3);
            return Ty.ArrayOf(ResolveRtti(parts.Length > 2 ? parts[2] : name["array:".Length..])) with { NativeName = name };
        }
        if (name.StartsWith("map:"))
        {
            string[] parts = name["map:".Length..].Split(',', 4);
            return parts.Length == 4 ? Ty.MapOf(ResolveRtti(parts[2]), ResolveRtti(parts[3])) : Ty.Primitive(name);
        }
        if (name.StartsWith("handle:")) return Ty.Class(name["handle:".Length..]);
        if (name.StartsWith("ptr:")) return Ty.Class(name["ptr:".Length..]);

        try
        {
            var ty = Resolve(new TypeRef(name));
            return ty.Kind == TyKind.Class && !Classes.ContainsKey(ty.Name) ? Ty.Primitive(ty.Name) : ty;
        }
        catch (CompileException)
        {
            return Ty.Primitive(name);
        }
    }

    private void AddField(string cls, string name, Ty ty, bool bindable = false, bool isImport = false, bool isConst = false)
    {
        if (!Classes.TryGetValue(cls, out var info) || info.Fields.ContainsKey(name)) return;
        info.FieldOrder.Add(name);
        info.Fields[name] = new FieldInfo(name, ty, cls, bindable, isImport, isConst);
    }

    public FunctionInfo FunctionInfoOf(FunctionDecl decl, string? owner)
    {
        try
        {
            var ps = decl.Params.Select(p => new ParamInfo(p.Name, Resolve(p.Type), p.IsOptional, p.IsOut)).ToList();
            var ret = decl.ReturnType is { } rt ? Resolve(rt) : decl.Specifiers.IsEvent ? Ty.Bool : Ty.Void;
            return new FunctionInfo { Name = decl.Name, Owner = owner, Params = ps, ReturnType = ret, Specifiers = decl.Specifiers };
        }
        catch (CompileException ex)
        {
            throw new CompileException($"{decl.Name}: {ex.Message}");
        }
    }

    public Ty Resolve(TypeRef type)
    {
        if (type.Name == "set" && type.Args.Count > 0)
            throw new CompileException("Generic set<T> is not supported by the qualified Remastered 5.x toolchain. Keep this source for a toolchain that supports it.");
        if (type.Name == "array")
        {
            if (type.Args.Count == 0) throw new CompileException("array without an element type");
            return Ty.ArrayOf(Resolve(type.Args[0]));
        }
        if (type.Name == "map")
        {
            if (type.Args.Count != 2) throw new CompileException("map requires a key type and a value type");
            return Ty.MapOf(Resolve(type.Args[0]), Resolve(type.Args[1]));
        }
        switch (type.Name)
        {
            case "void": return Ty.Void;
            case "bool" or "Bool": return Ty.Bool;
            case "int" or "Int32": return Ty.Int;
            case "float" or "Float": return Ty.Float;
            case "byte" or "Uint8": return Ty.Byte;
            case "string" or "String": return Ty.String;
            case "name" or "CName": return Ty.Name_;
            case "Uint64" or "Int8" or "Int16" or "Uint16" or "Uint32" or "Int64" or "Double":
                return Ty.Primitive(type.Name);
        }
        if (SimpleTypes.Contains(type.Name) && !Classes.ContainsKey(type.Name)) return Ty.Primitive(type.Name);
        if (Enums.ContainsKey(type.Name)) return Ty.Enum(type.Name);
        if (Classes.TryGetValue(type.Name, out var cls)) return cls.IsStruct ? Ty.Struct(type.Name) : Ty.Class(type.Name);
        return type.Args.Count == 0 ? Ty.Class(type.Name) : Ty.Primitive(type.Name);
    }

    // Find imported fields on their native declaring class.
    public FieldInfo? FindField(string cls, string name)
    {
        FieldInfo? found = null;
        string? current = cls;
        for (int depth = 0; current is not null && depth <= 64; depth++)
        {
            if (!Classes.TryGetValue(current, out var info)) break;
            if (info.Fields.TryGetValue(name, out var field))
            {
                if (!field.IsImport) return field;
                found ??= field;
                if (found != field) found = field;
            }
            current = info.Parent;
        }
        return found;
    }

    public FunctionInfo? FindMethod(string cls, string name)
    {
        string? current = cls;
        for (int depth = 0; current is not null && depth <= 64; depth++)
        {
            if (!Classes.TryGetValue(current, out var info)) return null;
            if (info.Methods.TryGetValue(name, out var method)) return method;
            current = info.Parent;
        }
        return null;
    }

    public bool IsSubclass(string cls, string ancestor)
    {
        string? current = cls;
        for (int depth = 0; current is not null && depth <= 64; depth++)
        {
            if (current == ancestor) return true;
            current = Classes.TryGetValue(current, out var info) ? info.Parent : null;
        }
        return false;
    }

    public string BlobName(string cls) => Classes.TryGetValue(cls, out var info) ? info.BlobName : cls;
}

public class CompileException(string message, int line = 0) : Exception(message)
{
    public int Line { get; } = line;
}

public sealed record SourceFile(string Path, List<Item> Items);

public sealed class CompileErrorsException(List<CompileException> errors) : CompileException(errors[0].Message, errors[0].Line)
{
    public List<CompileException> Errors { get; } = errors;
}
