namespace WSCompiler.Core.Scripts.Standalone;

// Check missing inherited methods before applying annotations.
public static class IncompleteClasses
{
    // RTTI omits the abstract flag for these native classes.
    private static readonly HashSet<string> EngineAbstract = ["CCommonGame"];

    public static List<CompileProblem> Check(Symbols symbols, IReadOnlyList<SourceFile> checkedFiles, IEnumerable<SourceFile> allFiles)
    {
        // Add project declarations first so references cannot replace them.
        var decls = new Dictionary<string, ClassDecl>();
        foreach (var file in checkedFiles.Concat(allFiles))
            foreach (var item in file.Items)
                if (item is ClassItem { Decl: var d }) decls.TryAdd(Symbols.ClassKey(d), d);

        var warnings = new List<CompileProblem>();
        foreach (var file in checkedFiles)
            foreach (var item in file.Items)
            {
                if (item is not ClassItem { Decl: var decl } || decl.IsAbstract || decl.IsStruct || EngineAbstract.Contains(decl.Name)) continue;
                string key = Symbols.ClassKey(decl);
                var missing = Missing(symbols, decls, key);
                if (missing.Count == 0) continue;
                int line = Math.Max(decl.Line, 1);
                // loc-ignore-start: wcc_lite's own words
                warnings.Add(new(file.Path, line, $"Class {decl.Name} is not marked as abstract, but has missing functions!"
                    + string.Concat(missing.Select(m => $"\n - '{m.Function}' in base class '{m.Owner}'"))));
                // loc-ignore-end
            }
        return warnings;
    }

    private static List<(string Function, string Owner)> Missing(Symbols symbols, Dictionary<string, ClassDecl> decls, string key)
    {
        var bodied = new HashSet<string>();
        var byClass = new List<List<(string, string)>>();
        if (decls.TryGetValue(key, out var own))
            foreach (var f in own.Methods)
                if (f.Body is not null || f.Specifiers.IsImport) bodied.Add(f.Name);
        string? current = symbols.Classes.GetValueOrDefault(key)?.Parent;
        for (int depth = 0; current is not null && depth <= 64; depth++)
        {
            if (!decls.TryGetValue(current, out var decl)) break;
            var missing = new List<(string, string)>();
            byClass.Add(missing);
            foreach (var f in decl.Methods)
            {
                if (f.Body is null && !f.Specifiers.IsImport && !bodied.Contains(f.Name))
                    missing.Add((f.Name, decl.Name));
                bodied.Add(f.Name);
            }
            current = symbols.Classes.GetValueOrDefault(current)?.Parent;
        }
        byClass.Reverse();
        return [.. byClass.SelectMany(m => m)];
    }
}
