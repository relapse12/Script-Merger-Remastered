namespace WSCompiler.Core.Scripts.Standalone;

// Added fields are checked during extension processing; check the other declaration types here.
public static class UnknownTypes
{
    // loc-ignore-start: wcc_lite's own words
    public static List<CompileProblem> Check(Symbols symbols, IReadOnlyList<SourceFile> files)
    {
        var problems = new List<CompileProblem>();
        foreach (var file in files)
            foreach (var item in file.Items)
                switch (item)
                {
                    case ClassItem { Decl: var decl }:
                        foreach (var field in decl.Fields)
                            foreach (string name in field.Names)
                                if (Unknown(symbols, field.Type) is { } type)
                                {
                                    problems.Add(new(file.Path, field.Line, $"Unknown type '{type}' for property '{name}'."));
                                    problems.Add(new(file.Path, field.Line, $"Property '{decl.Name}::{name}' could not be created."));
                                }
                        foreach (var method in decl.Methods) CheckFunction(symbols, file.Path, method, problems);
                        break;
                    case FunctionItem { Decl: var function }:
                        CheckFunction(symbols, file.Path, function, problems);
                        break;
                }
        return problems;
    }

    public static IEnumerable<CompileProblem> CheckAddedField(Symbols symbols, string file, FieldDecl field, string cls)
    {
        if (Unknown(symbols, field.Type) is not { } type) yield break;
        foreach (string name in field.Names)
        {
            yield return new(file, field.Line, $"Unknown type '{type}' for property '{name}'.");
            yield return new(file, field.Line, $"Property '{cls}::{name}' could not be created.");
        }
    }

    private static void CheckFunction(Symbols symbols, string file, FunctionDecl function, List<CompileProblem> problems)
    {
        foreach (var p in function.Params)
            if (Unknown(symbols, p.Type) is { } type)
                problems.Add(new(file, function.Line, $"Unknown type '{type}' for property '{p.Name}'."));
        if (function.ReturnType is { } ret && Unknown(symbols, ret) is { } returned)
            problems.Add(new(file, function.Line, $"Unknown type '{returned}' for property '__return'."));
        if (function.Body is null) return;
        var locals = new List<FieldDecl>();
        Locals(function.Body, locals);
        foreach (var local in locals)
            if (Unknown(symbols, local.Type) is { } type)
                foreach (string name in local.Names)
                {
                    problems.Add(new(file, local.Line, $"Unknown type '{type}' for property '{name}'."));
                    problems.Add(new(file, local.Line, $"Could not create property for variable '{name}' in function '{function.Name}'."));
                }
    }
    // loc-ignore-end

    private static void Locals(IEnumerable<Stmt> statements, List<FieldDecl> output)
    {
        foreach (var s in statements)
            switch (s.Kind)
            {
                case VarStmt v: output.Add(v.Decl); break;
                case IfStmt i:
                    Locals([i.Then], output);
                    if (i.Else is not null) Locals([i.Else], output);
                    break;
                case WhileStmt w: Locals([w.Body], output); break;
                case DoWhileStmt d: Locals([d.Body], output); break;
                case ForStmt f: Locals([f.Body], output); break;
                case SwitchStmt sw: Locals(sw.Items.OfType<StmtItem>().Select(i => i.Stmt), output); break;
                case BlockStmt b: Locals(b.Statements, output); break;
            }
    }

    private static string? Unknown(Symbols symbols, TypeRef type)
    {
        // Preserve the REDkit diagnostic for unsupported collections.
        if (type.Name == "set" && type.Args.Count > 0) symbols.Resolve(type);
        if (type.Name == "array" && type.Args.Count == 1)
            return Unknown(symbols, type.Args[0]) is { } inner ? $"array:2,0,{inner}" : null;
        if (type.Name == "map" && type.Args.Count == 2)
            return Unknown(symbols, type.Args[0]) is not null || Unknown(symbols, type.Args[1]) is not null
                ? $"map:2,0,{Unknown(symbols, type.Args[0]) ?? type.Args[0].Name},{Unknown(symbols, type.Args[1]) ?? type.Args[1].Name}"
                : null;
        return Known(symbols, type.Name) ? null : type.Name;
    }

    private static bool Known(Symbols symbols, string name) => name switch
    {
        "void" or "bool" or "Bool" or "int" or "Int32" or "float" or "Float" or "byte" or "Uint8" or "string" or "String"
            or "name" or "CName" or "Uint64" or "Int8" or "Int16" or "Uint16" or "Uint32" or "Int64" or "Double" => true,
        _ => symbols.SimpleTypes.Contains(name) || symbols.Enums.ContainsKey(name) || symbols.Classes.ContainsKey(name),
    };
}
