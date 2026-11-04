namespace WSCompiler.Core.Scripts.Standalone;

public static class Duplicates
{
    public static IReadOnlyList<CompileProblem> Check(IReadOnlyList<SourceFile> files)
    {
        var declarations = new Dictionary<string, string>(StringComparer.Ordinal);
        var problems = new List<CompileProblem>();
        foreach (var file in files)
            foreach (var item in file.Items)
            {
                string? key = item switch
                {
                    ClassItem { Decl.IsImport: false } value => "type:" + Symbols.ClassKey(value.Decl),
                    FunctionItem { Decl.Annotation: null, Decl.Specifiers.IsImport: false } value => "function:" + value.Decl.Name,
                    _ => null
                };
                if (key is not null && !declarations.TryAdd(key, file.Path))
                    problems.Add(new(file.Path, 1, $"Duplicate declaration '{key[(key.IndexOf(':') + 1)..]}' (also in {declarations[key]})."));
            }
        return problems;
    }
}
