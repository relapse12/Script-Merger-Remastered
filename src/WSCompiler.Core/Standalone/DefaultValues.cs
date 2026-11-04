using System.Globalization;

namespace WSCompiler.Core.Scripts.Standalone;

// Check default-value conversions against REDkit behavior, including warnings for malformed Boolean prefixes.
public static class DefaultValues
{
    public static (List<CompileProblem> Errors, List<CompileProblem> Warnings) Check(Symbols symbols, IReadOnlyList<SourceFile> files)
    {
        var errors = new List<CompileProblem>();
        var warnings = new List<CompileProblem>();
        foreach (var file in files)
            foreach (var item in file.Items)
            {
                if (item is not ClassItem { Decl: var decl }) continue;
                string cls = Symbols.ClassKey(decl);
                foreach (var d in decl.Defaults)
                {
                    // autoState selects the initial state; it has no field declaration.
                    if (d.Field == "autoState") continue;
                    int line = Math.Max(d.Value.Line, 1);
                    // loc-ignore-start: wcc_lite's own words, as its compile messages are
                    if (FindField(symbols, cls, d.Field) is not { } field)
                    {
                        // RTTI can omit native field declarations.
                        if (symbols.Classes.ContainsKey(cls)) errors.Add(new(file.Path, line, $"Property '{d.Field}' does not exist in '{decl.Name}'"));
                        continue;
                    }
                    CheckValue(field.Type, d.Value);
                    void CheckValue(Ty type, Expr value)
                    {
                        if (value.Kind is DefaultArray array)
                        {
                            if (type.Kind != TyKind.Array) errors.Add(new(file.Path, line, "Unable to parse value"));
                            else foreach (var child in array.Values) CheckValue(type.Element!, child);
                            return;
                        }
                        if (Text(value.Kind) is not { } text) return;
                        if (type.IsClass && value.Kind is NullLit)
                            errors.Add(new(file.Path, line, "Unknown class 'NULL'"));
                        else if (Problem(symbols, type, text) is { } problem)
                            errors.Add(new(file.Path, line, $"Unable to parse value: {problem}"));
                        else if (type.Kind == TyKind.Bool && !IsNumber(text)
                            && !text.Equals("true", StringComparison.OrdinalIgnoreCase) && !text.Equals("false", StringComparison.OrdinalIgnoreCase))
                            warnings.Add(new(file.Path, line, $"'{text}' isn't true or false. wcc_lite takes it, as it reads only how a bool's value starts, but it's likely a typo."));
                    }
                    // loc-ignore-end
                }
            }
        return (errors, warnings);
    }

    public static List<CompileProblem> AutoStates(Symbols symbols, IReadOnlyList<SourceFile> checkedFiles, IEnumerable<SourceFile> allFiles)
    {
        var decls = new Dictionary<string, ClassDecl>();
        foreach (var file in checkedFiles.Concat(allFiles))
            foreach (var item in file.Items)
                if (item is ClassItem { Decl: var d }) decls.TryAdd(Symbols.ClassKey(d), d);
        bool HasAutoState(string? key)
        {
            for (int depth = 0; key is not null && depth <= 64; depth++)
            {
                if (!decls.TryGetValue(key, out var decl)) return false;
                if (decl.HasAutoStateDefault || decl.Defaults.Any(d => d.Field == "autoState")) return true;
                key = symbols.Classes.GetValueOrDefault(key)?.Parent;
            }
            return false;
        }
        var warnings = new List<CompileProblem>();
        foreach (var file in checkedFiles)
            foreach (var item in file.Items)
                if (item is ClassItem { Decl: { IsStateMachine: true } decl } && !HasAutoState(Symbols.ClassKey(decl)))
                    warnings.Add(new(file.Path, Math.Max(decl.Line, 1), $"State machine '{decl.Name}' has no autostate.")); // loc-ignore: wcc_lite's words
        return warnings;
    }

    private static FieldInfo? FindField(Symbols symbols, string owner, string path)
    {
        FieldInfo? field = null;
        foreach (string name in path.Split('.'))
        {
            field = symbols.FindField(owner, name);
            if (field is null) return null;
            owner = field.Type.Name;
        }
        return field;
    }

    private static string? Text(ExprKind value) => value switch
    {
        IntLit i => i.Value.ToString(CultureInfo.InvariantCulture),
        FloatLit f => f.Text,
        StrLit s => s.Value,
        NameLit n => n.Value,
        BoolLit b => b.Value ? "true" : "false",
        Ident id => id.Name,
        Unary { Op: "-", Operand.Kind: var operand } when Text(operand) is { } inner => "-" + inner,
        NullLit => "NULL",
        _ => null,
    };

    private static string? Problem(Symbols symbols, Ty type, string text) => type.Kind switch
    {
        TyKind.Enum when symbols.Enums.TryGetValue(type.Name, out var info) && info.ValueOf(text) is null =>
            $"{type.Name} has no '{text}'.", // loc-ignore
        TyKind.Int or TyKind.Byte or TyKind.Float when !IsNumber(text) => $"'{text}' isn't a number.", // loc-ignore
        TyKind.Bool when !IsNumber(text) && !text.StartsWith("true", StringComparison.OrdinalIgnoreCase)
            && !text.StartsWith("false", StringComparison.OrdinalIgnoreCase) => $"'{text}' isn't true or false.", // loc-ignore
        _ => null,
    };

    // REDkit accepts numeric prefixes, including values such as 5x.
    private static bool IsNumber(string text)
    {
        var s = text.AsSpan().TrimStart();
        if (s.Length > 0 && s[0] is '-' or '+') s = s[1..];
        if (s.Length > 0 && s[0] == '.') s = s[1..];
        return s.Length > 0 && char.IsAsciiDigit(s[0]);
    }
}
