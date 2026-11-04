using WSCompiler.Core.Scripts;
using WSCompiler.Core.Scripts.Standalone;

namespace WSCompiler.Recovery;

// Some compiled metadata has no source form and must remain compiled. Unsupported content cannot be merged safely.
public enum RecoveryRequirement { None, CompiledOutput, Unsupported }

public sealed record RecoveryIssue(string Symbol, RecoveryRequirement Requirement, string Message);

public sealed record RecoveredFile(string Path, string Text, IReadOnlyList<Item> Declarations);

// An unsupported recovery issue leaves Files empty.
public sealed record RecoveredModule(
    string Identity,
    ScriptBlob Blob,
    IReadOnlyList<RecoveredFile> Files,
    IReadOnlyList<RecoveryIssue> Issues)
{
    public bool CanMerge => Issues.All(issue => issue.Requirement != RecoveryRequirement.Unsupported);
    public bool RequiresCompilation => Issues.Any(issue => issue.Requirement == RecoveryRequirement.CompiledOutput);
    public IReadOnlyList<SourceFile> Sources => Files.Select(file => new SourceFile(file.Path, file.Declarations.ToList())).ToArray();
}

// Recover source and restore compiled defaults and flags that source text cannot represent.
// BlobComparison and ControlFlowComparison separately check recompiled output.
public static class BlobDecompiler
{
    public static RecoveredModule Recover(byte[] data, string identity, IEnumerable<string> referenceScripts)
        => Recover(data, identity, EnumResolver.ReadEnums(referenceScripts));

    public static RecoveredModule Recover(byte[] data, string identity, Dictionary<string, Dictionary<long, string>> referenceEnums)
    {
        var blob = ScriptBlob.FromBytes(data, identity);
        var resolver = new EnumResolver(blob, referenceEnums) { RequireAutomaticBindings = true };
        var issues = new List<RecoveryIssue>();
        var decoder = new Decompiler(blob, resolver);
        foreach (var function in blob.AllFunctions.Where(function => function.Code.Length > 0 && (function.Flags & 1) == 0))
        {
            string symbol = (function.Class?.Name ?? function.Owner) + "." + function.Name;
            try
            {
                foreach (var instruction in Bytecode.Decode(function.Code))
                {
                    if (instruction.Op == Op.StringConst) CheckLiteral(instruction.Text ?? "", '"');
                    if (instruction.Op == Op.NameConst) CheckLiteral(blob.Names[instruction.Arg(0)], '\'');
                }
                decoder.DecompileBody(function, 0);
                if (!decoder.IsBodyComplete)
                    issues.Add(new(symbol, RecoveryRequirement.Unsupported, "The function's control flow could not be fully recovered."));
            }
            catch (Exception exception) when (exception is InvalidDataException or NotSupportedException or
                InvalidOperationException or ArgumentException or OverflowException or IndexOutOfRangeException)
            {
                issues.Add(new(symbol, RecoveryRequirement.Unsupported, exception.Message));
            }
        }

        foreach (var declaration in blob.Classes)
        {
            if ((declaration.Flags & ~(1 | 2 | 0x400)) != 0)
                issues.Add(new(declaration.Name, RecoveryRequirement.Unsupported, "The class contains unsupported declaration flags."));
            CheckDefaults(declaration.Name, declaration.Defaults, declaration.Properties);
        }
        foreach (var declaration in blob.Structs)
        {
            if ((declaration.Flags & ~2) != 0) issues.Add(new(declaration.Name, RecoveryRequirement.Unsupported, "The struct contains unsupported declaration flags."));
            CheckDefaults(declaration.Name, declaration.Defaults, declaration.Properties);
        }
        foreach (var property in blob.Classes.SelectMany(value => value.Properties).Concat(blob.Structs.SelectMany(value => value.Properties)).Concat(blob.AddedFields))
            if ((property.Flags & ~0x1fc02f) != 0 || blob.AddedFields.Contains(property) && ((property.Flags & 0x80000) != 0 || property.Hint.Length > 0))
                issues.Add(new(property.Name, RecoveryRequirement.Unsupported, "The field contains metadata that cannot be faithfully exported."));
        foreach (var function in blob.AllFunctions)
            if ((function.Flags & ~0x1ffff3) != 0)
                issues.Add(new(function.Name, RecoveryRequirement.Unsupported, "The function contains unsupported declaration flags."));

        var files = new List<RecoveredFile>();
        if (issues.Any(issue => issue.Requirement == RecoveryRequirement.Unsupported)) return new(identity, blob, files, issues);
        // all.ws repeats the individual documents, so skip it here.
        foreach (var document in ScriptWriter.Write(blob, identity, resolver, executableDefaults: true).Skip(1))
        {
            try
            {
                var declarations = Parser.ParseSource(document.Text, skipBodies: false);
                foreach (var declaration in declarations.OfType<ClassItem>().Select(item => item.Decl))
                {
                    IReadOnlyList<ScriptDefault> defaults = declaration.IsStruct
                        ? blob.Structs.First(value => value.Name == declaration.Name).Defaults
                        : blob.Classes.First(value => value.Name == declaration.Name && value.StateOf == (declaration.StateMachineOf ?? "")).Defaults;
                    declaration.BinaryDefaults = defaults.ToList();
                    declaration.RequiresBinaryDefaults = issues.Any(issue => issue.Symbol == declaration.Name && issue.Requirement == RecoveryRequirement.CompiledOutput);
                }
                files.Add(new(document.Path, document.Text, declarations));
            }
            catch (ScriptSyntaxException exception)
            {
                issues.Add(new(document.Path, RecoveryRequirement.Unsupported, exception.Message));
            }
        }
        // Match declarations back to their compiled functions to recover flags omitted from source text.
        var functions = blob.AllFunctions.GroupBy(function => (function.Kind, function.Owner, function.Name))
            .ToDictionary(group => group.Key, group => new Queue<ScriptFunction>(group));
        foreach (var item in files.SelectMany(file => file.Declarations).OfType<FunctionItem>().Where(item => item.Decl.Annotation is not null))
        {
            var declaration = item.Decl;
            var annotation = declaration.Annotation!;
            var kind = annotation.Kind switch
            {
                AnnotationKind.AddMethod => FunctionKind.AddMethod,
                AnnotationKind.WrapMethod => FunctionKind.Wrap,
                _ => string.IsNullOrEmpty(annotation.Class) ? FunctionKind.ReplaceGlobal : FunctionKind.Replace
            };
            if (!functions.TryGetValue((kind, annotation.Class ?? "", declaration.Name), out var matches) || matches.Count == 0)
                throw new InvalidDataException("The recovered annotation has no matching compiled declaration.");
            int flags = matches.Dequeue().Flags;
            declaration.CompiledFlags = flags;
            var specifiers = declaration.Specifiers;
            specifiers.Visibility = (flags & 0x4000) != 0 ? Visibility.Private : (flags & 0x8000) != 0 ? Visibility.Protected
                : (flags & 0x10000) != 0 ? Visibility.Public : Visibility.Default;
            specifiers.IsImport = (flags & 1) != 0;
            specifiers.IsFinal = (flags & 0x10) != 0;
            specifiers.IsEvent = (flags & 0x20) != 0;
            specifiers.IsLatent = (flags & 0x40) != 0;
            specifiers.IsEntry = (flags & 0x80) != 0;
            specifiers.IsExec = (flags & 0x100) != 0;
            specifiers.IsTimer = (flags & 0x400) != 0;
            specifiers.IsStoryScene = (flags & 0x800) != 0;
            specifiers.IsQuest = (flags & 0x1000) != 0;
            specifiers.IsCleanup = (flags & 0x2000) != 0;
            specifiers.IsReward = (flags & 0x20000) != 0;
        }
        return new(identity, blob, files, issues);

        // Reject literals that would change value when written and parsed again.
        static void CheckLiteral(string value, char delimiter)
        {
            try
            {
                var tokens = Lexer.Tokenize(Decompiler.Quote(value, delimiter));
                if (tokens.Count != 2 || tokens[0].Text != value) throw new InvalidDataException("A compiled literal cannot be preserved in source.");
            }
            catch (ScriptSyntaxException error) { throw new InvalidDataException("A compiled literal cannot be preserved in source.", error); }
        }

        // Keep defaults with no source syntax as metadata and require compiled output.
        void CheckDefaults(string owner, IReadOnlyList<ScriptDefault> defaults, IReadOnlyList<ScriptProperty> properties)
        {
            if (defaults.Any(value => ContainsArray(value.Children)))
                issues.Add(new(owner, RecoveryRequirement.CompiledOutput,
                    "Array defaults require preserved compiled metadata."));
            else if (defaults.Any(value => !ScriptWriter.CanWriteDefault(value, properties)))
                issues.Add(new(owner, RecoveryRequirement.CompiledOutput,
                    "These defaults require preserved compiled metadata."));
        }
    }

    // Array elements are the nodes with empty keys.
    private static bool ContainsArray(IReadOnlyList<ScriptDefaultNode> values) =>
        values.Count > 0 && values.All(value => value.Key.Length == 0)
        || values.Any(value => ContainsArray(value.Children));
}
