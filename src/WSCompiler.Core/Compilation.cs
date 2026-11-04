using System.Collections.Concurrent;
using System.IO.Compression;
using WSCompiler.Core;
using WSCompiler.Core.Scripts;
using WSCompiler.Core.Scripts.Standalone;

namespace WSCompiler;

public sealed record Diagnostic(string Severity, string Code, string? File, int Line, string Message);
public sealed record CompilationResult(byte[]? Blob, IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool Success => Blob is not null && !Diagnostics.Any(d => d.Severity == "error");
}

// Prepare these references once for repeated compiles. They supply declarations but contribute no emitted code.
public sealed class CompilationReferences
{
    internal readonly IReadOnlyList<SourceFile> Baseline, Dependencies;
    internal readonly IReadOnlyList<Diagnostic> Diagnostics;
    internal readonly Rtti Rtti;
    internal CompilationReferences(IReadOnlyList<SourceFile> baseline, IReadOnlyList<SourceFile> dependencies,
        IReadOnlyList<Diagnostic> diagnostics, Rtti rtti)
        => (Baseline, Dependencies, Diagnostics, Rtti) = (baseline, dependencies, diagnostics, rtti);
}

// Compiles scripts against vanilla, dependency and native declarations.
// I/O and syntax failures become diagnostics; cancellation propagates to the caller.
public static class Compiler
{
    public static CompilationResult Compile(string scripts, string vanilla, IReadOnlyList<string>? references = null,
        string? rttiPath = null, CancellationToken cancellation = default)
        => CompilePrepared(diagnostics => ReadScripts(scripts, false, diagnostics, cancellation),
            scripts, vanilla, references, rttiPath, cancellation);

    public static CompilationResult CompileSources(IReadOnlyList<SourceFile> sources, string vanilla,
        IReadOnlyList<string>? references = null, string? rttiPath = null, CancellationToken cancellation = default,
        IReadOnlyList<SourceFile>? referenceSources = null, CompilationReferences? preparedReferences = null)
        => CompilePrepared(_ => sources, "recovered sources", vanilla, references, rttiPath, cancellation, referenceSources, preparedReferences);

    // Reference scripts need only declarations, so skip their function bodies.
    public static CompilationReferences PrepareReferences(string vanilla, IReadOnlyList<string>? references = null,
        string? rttiPath = null, CancellationToken cancellation = default, IReadOnlyList<SourceFile>? referenceSources = null)
    {
        var diagnostics = new List<Diagnostic>();
        var baseline = ReadScripts(vanilla, true, diagnostics, cancellation);
        if (baseline.Count == 0) diagnostics.Add(new("error", "C002", vanilla, 0, "No vanilla reference scripts were found."));
        var dependencies = new List<SourceFile>(referenceSources ?? []);
        foreach (string path in references ?? [])
        {
            cancellation.ThrowIfCancellationRequested();
            if (Directory.Exists(path)) dependencies.AddRange(ReadScripts(path, true, diagnostics, cancellation));
            else if (Path.GetExtension(path).Equals(".rsblob", StringComparison.OrdinalIgnoreCase))
                dependencies.Add(BlobReferences.Read(path));
            else if (File.Exists(path) && Path.GetExtension(path).Equals(".ws", StringComparison.OrdinalIgnoreCase))
                dependencies.Add(new(path, Parser.ParseSource(TextDecoding.Decode(File.ReadAllBytes(path)), skipBodies: true)));
            else diagnostics.Add(new("error", "C003", path, 0, "Reference must be a script folder, .ws file, or .rsblob file."));
        }
        var rtti = diagnostics.Any(d => d.Severity == "error") ? new Rtti() : ReadRtti(rttiPath);
        return new(baseline, dependencies, diagnostics, rtti);
    }

    public static CompilationReferences PrepareReferences(IReadOnlyList<SourceFile> baseline,
        IReadOnlyList<SourceFile> dependencies, string? rttiPath = null)
    {
        var diagnostics = new List<Diagnostic>();
        if (baseline.Count == 0) diagnostics.Add(new("error", "C002", null, 0, "No vanilla reference scripts were found."));
        return new(baseline, dependencies, diagnostics, ReadRtti(rttiPath));
    }

    // Stop after each failed stage to avoid cascading errors. Emit only project code.
    private static CompilationResult CompilePrepared(Func<List<Diagnostic>, IReadOnlyList<SourceFile>> readProject,
        string scripts, string vanilla, IReadOnlyList<string>? references, string? rttiPath, CancellationToken cancellation,
        IReadOnlyList<SourceFile>? referenceSources = null, CompilationReferences? preparedReferences = null)
    {
        var diagnostics = new List<Diagnostic>();
        try
        {
            var project = readProject(diagnostics);
            diagnostics.AddRange(Duplicates.Check(project).Select(problem => new Diagnostic("error", "C009", problem.File, problem.Line, problem.Text)));
            if (!project.Any(f => f.Items.Count > 0)) diagnostics.Add(new("error", "C001", scripts, 0, "No WitcherScript declarations were found."));
            var environment = preparedReferences ?? PrepareReferences(vanilla, references, rttiPath, cancellation, referenceSources);
            diagnostics.AddRange(environment.Diagnostics);
            if (diagnostics.Any(d => d.Severity == "error")) return new(null, diagnostics);

            var baseline = environment.Baseline;
            var dependencies = environment.Dependencies;
            var rtti = environment.Rtti;
            var symbols = new Symbols();
            // Project enums can only extend what vanilla and the dependencies already define, so they come last.
            symbols.AddEnums(baseline, true);
            symbols.AddRttiEnums(rtti);
            symbols.ExtendEnums(dependencies);
            symbols.ExtendEnums(project, (file, line, message) => diagnostics.Add(new("error", "C010", file.Path, line, message)));
            if (diagnostics.Any(d => d.Severity == "error")) return new(null, diagnostics);
            symbols.AddTypeNames(project);
            symbols.AddTypeNames(baseline);
            symbols.AddTypeNames(dependencies);
            // Keep omitted base classes available for lookup without emitting them in the patch.
            var referenceParents = baseline.Concat(dependencies).SelectMany(file => file.Items).OfType<ClassItem>()
                .Where(item => item.Decl.Parent is not null).GroupBy(item => Symbols.ClassKey(item.Decl))
                .ToDictionary(group => group.Key, group => group.First().Decl.Parent!);
            foreach (var item in project.SelectMany(file => file.Items).OfType<ClassItem>())
                if (item.Decl.Parent is null && item.Decl.StateMachineOf is null && !item.Decl.IsStruct
                    && referenceParents.TryGetValue(Symbols.ClassKey(item.Decl), out var parent) && parent is not null)
                    symbols.Classes[Symbols.ClassKey(item.Decl)].Parent = parent;
            symbols.AddRttiClasses(rtti);
            symbols.ResolveStateParents();
            diagnostics.AddRange(UnknownTypes.Check(symbols, project).Select(p => new Diagnostic("error", "C011", p.File, p.Line, p.Text)));
            if (diagnostics.Any(d => d.Severity == "error")) return new(null, diagnostics);
            symbols.AddMembers(project, (file, message) => diagnostics.Add(new("error", "C004", file.Path, 1, message)));
            // Vanilla reference issues are warnings. Dependency issues are errors because project code may bind to them.
            symbols.AddMembers(baseline, (file, message) => diagnostics.Add(new("warning", "R002", file.Path, 1, message)));
            symbols.AddMembers(dependencies, (file, message) => diagnostics.Add(new("error", "R003", file.Path, 1, message)));
            symbols.AddRttiProperties(rtti);
            symbols.AddRttiFunctions(rtti);
            if (diagnostics.Any(d => d.Severity == "error")) return new(null, diagnostics);
            cancellation.ThrowIfCancellationRequested();
            diagnostics.AddRange(Extensions.Check(symbols, project).Select(p => new Diagnostic("error", "C012", p.File, p.Line, p.Text)));
            if (diagnostics.Any(d => d.Severity == "error")) return new(null, diagnostics);
            var (defaultErrors, defaultWarnings) = DefaultValues.Check(symbols, project);
            diagnostics.AddRange(defaultErrors.Select(p => new Diagnostic("error", "C013", p.File, p.Line, p.Text)));
            if (diagnostics.Any(d => d.Severity == "error")) return new(null, diagnostics);
            var warnings = IncompleteClasses.Check(symbols, project, baseline.Concat(dependencies))
                .Concat(DefaultValues.AutoStates(symbols, project, baseline.Concat(dependencies))).Concat(defaultWarnings).ToList();
            byte[] blob;
            try { blob = Assembler.Assemble(symbols, project, warnings); }
            finally { diagnostics.AddRange(warnings.Select(p => new Diagnostic("warning", "W001", p.File, p.Line, p.Text))); }
            cancellation.ThrowIfCancellationRequested();
            // Check after emission because the blob's name table determines which collisions matter.
            diagnostics.AddRange(NameCollisions.Find(blob, symbols));
            return new(blob, diagnostics);
        }
        catch (CompileFailedException ex)
        {
            diagnostics.AddRange(ex.Problems.Select(p => new Diagnostic("error", "C005", p.File, p.Line, p.Text)));
        }
        catch (ScriptSyntaxException ex) { diagnostics.Add(new("error", "C006", null, ex.Line, ex.Message)); }
        catch (CompileException ex) { diagnostics.Add(new("error", "C007", null, ex.Line, ex.Message)); }
        catch (Exception ex) when (ex is OverflowException or FormatException)
        { diagnostics.Add(new("error", "C008", null, 0, ex.Message)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        { diagnostics.Add(new("error", "IO001", null, 0, ex.Message)); }
        return new(null, diagnostics);
    }

    // Keep a stable path order despite parallel parsing. A failed reference parse warns and omits that file.
    private static List<SourceFile> ReadScripts(string directory, bool declarationsOnly, List<Diagnostic> diagnostics, CancellationToken cancel)
    {
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException($"Script directory not found: {directory}");
        string[] paths = Directory.GetFiles(directory, "*.ws", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var files = new SourceFile?[paths.Length];
        var problems = new ConcurrentQueue<Diagnostic>();
        Parallel.For(0, paths.Length, new ParallelOptions { CancellationToken = cancel }, i =>
        {
            string relative = Path.GetRelativePath(directory, paths[i]);
            try { files[i] = new(relative, Parser.ParseSource(TextDecoding.Decode(File.ReadAllBytes(paths[i])), skipBodies: declarationsOnly)); }
            catch (ScriptSyntaxException ex) { problems.Enqueue(new(declarationsOnly ? "warning" : "error", declarationsOnly ? "R001" : "C006", paths[i], ex.Line, ex.Message)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { problems.Enqueue(new("error", "IO002", paths[i], 0, ex.Message)); }
        });
        diagnostics.AddRange(problems.OrderBy(p => p.File, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Line));
        return files.OfType<SourceFile>().ToList();
    }

    private static Rtti ReadRtti(string? path)
    {
        if (path is null) return Rtti.LoadBuiltIn();
        if (Path.GetExtension(path).Equals(".xml", StringComparison.OrdinalIgnoreCase)) return Rtti.LoadDump(path);
        using var file = File.OpenRead(path);
        using Stream stream = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new GZipStream(file, CompressionMode.Decompress) : file;
        using var reader = new StreamReader(stream);
        return Rtti.LoadSlim(reader);
    }
}
