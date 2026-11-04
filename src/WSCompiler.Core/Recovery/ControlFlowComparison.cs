using System.Globalization;
using System.Text.Json;
using WSCompiler.Core.Scripts;
using WSCompiler.Core.Scripts.Standalone;

namespace WSCompiler.Recovery;

// Normalizations records which representation differences were ignored during comparison.
public sealed record ComparedControlFlow(IReadOnlyList<string> Graph, IReadOnlyDictionary<string, int> Normalizations);

public sealed class ControlFlowContext
{
    internal sealed record Signature(string[] Parameters, string? ReturnType);
    private readonly Dictionary<string, Signature> functions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> parents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> machines = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> fields = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, Dictionary<long, string>> EnumValues;

    // Prefer script class and field declarations over native metadata.
    public ControlFlowContext(IEnumerable<SourceFile> declarations)
    {
        var files = declarations.ToArray();
        var rtti = Rtti.LoadBuiltIn();
        var nativeEnums = rtti.Enums.Select(value => new EnumDecl { Name = value.Name,
            Members = value.Options.Select(option => (option.Name, (long?)option.Value)).ToList() });
        EnumValues = EnumResolver.ReadEnums(nativeEnums.Concat(files.SelectMany(file => file.Items).OfType<EnumItem>().Select(item => item.Decl)));
        var symbols = new Symbols();
        symbols.AddTypeNames(files);
        symbols.AddRttiClasses(rtti);
        symbols.ResolveStateParents();
        foreach (var type in symbols.Classes.Values)
        {
            parents.TryAdd(type.Name, type.Parent ?? "");
            if (type.StateMachine is { } machine) machines.TryAdd(type.Name, machine);
        }
        foreach (var item in files.SelectMany(file => file.Items))
            switch (item)
            {
                case ClassItem type:
                    string owner = Symbols.ClassKey(type.Decl);
                    foreach (var field in type.Decl.Fields) Add(owner, field);
                    foreach (var method in type.Decl.Methods) Add(owner, method);
                    break;
                case FieldItem field: Add(field.Decl.Annotation?.Class ?? "", field.Decl); break;
                case FunctionItem function: Add(function.Decl.Annotation?.Class ?? "", function.Decl); break;
            }
        foreach (var type in rtti.Classes)
            foreach (var field in type.Properties)
                fields.TryAdd(type.Name + "." + field.Name, field.Type);
    }

    private static string TypeName(TypeRef type) => type.Name switch
    {
        "array" when type.Args.Count == 1 => "array:2,0," + TypeName(type.Args[0]),
        "map" when type.Args.Count == 2 => "map:2,0," + TypeName(type.Args[0]) + "," + TypeName(type.Args[1]),
        _ => type.Name
    };
    private void Add(string owner, FieldDecl field)
    {
        foreach (string name in field.Names) fields.TryAdd(owner + "." + name, TypeName(field.Type));
    }
    private void Add(string owner, FunctionDecl function) => functions.TryAdd((owner.Length == 0 ? "" : owner + ".") + function.Name,
        new(function.Params.Select(parameter => TypeName(parameter.Type)).ToArray(), function.ReturnType is { } type ? TypeName(type) : null));
    internal string? Machine(string owner) => machines.GetValueOrDefault(owner);
    internal string? Field(string owner, string name)
    {
        owner = ClassName(owner);
        for (int depth = 0; owner.Length > 0 && depth < 64; depth++)
        {
            if (fields.TryGetValue(owner + "." + name, out var type)) return type;
            owner = parents.GetValueOrDefault(owner) ?? "";
        }
        return null;
    }
    internal Signature? Find(string owner, string name)
    {
        owner = ClassName(owner);
        if (owner.Length == 0) return functions.GetValueOrDefault(name);
        for (int depth = 0; owner.Length > 0 && depth < 64; depth++)
        {
            if (functions.TryGetValue(owner + "." + name, out var function)) return function;
            owner = parents.GetValueOrDefault(owner) ?? "";
        }
        return null;
    }
    private static string ClassName(string type) => type.StartsWith("handle:", StringComparison.Ordinal) ? type[7..] : type;
}

// Compare reachable statements independently of jump layout and known encoding differences.
// Reject unsupported patterns; matching graphs establish equivalence only under these comparison rules.
public static class ControlFlowComparison
{
    private static readonly ControlFlowContext Empty = new([]);
    public static ComparedControlFlow Read(ScriptBlob blob, ScriptFunction function, ControlFlowContext? context = null,
        IReadOnlyDictionary<string, string>? typeCorrections = null)
        => new Reader(blob, function, context ?? Empty, typeCorrections).Read();

    private sealed class Reader(ScriptBlob blob, ScriptFunction function, ControlFlowContext context, IReadOnlyDictionary<string, string>? typeCorrections)
    {
        private sealed record Node(int Start, string Label, int[] Targets, bool Forward = false);
        private sealed record Expression(string Text, int End, bool Empty = false, long? Number = null, string? Enum = null, string? Type = null,
            Expression? Skipped = null, string? Logic = null, IReadOnlyList<Expression>? Terms = null, IReadOnlyList<Expression>? Conditional = null, int StorageBits = 0);
        private readonly List<Instruction> code = Bytecode.Decode(function.Code);
        private readonly Dictionary<string, int> changes = new(StringComparer.Ordinal);
        private readonly Dictionary<int, Node> nodes = [];
        private readonly Dictionary<int, Expression> cases = [];
        private readonly Dictionary<int, string> switches = [];
        private int position;
        private int depth;
        private string? receiver;
        private EnumResolver? enums;
        private string Owner => function.Class is { IsState: true } state ? Symbols.StateClassKey(state.StateOf, state.Name) : function.Class?.Name ?? function.Owner;

        // Number nodes in traversal order so physical bytecode layout does not affect the comparison.
        internal ComparedControlFlow Read()
        {
            if ((function.Flags & 1) != 0)
                return new(["native:" + BlobComparison.FunctionIdentity(function)],
                    function.Code.Length == 0 ? new Dictionary<string, int>() : new Dictionary<string, int> { ["nonexecutable-import-payload"] = 1 });
            while (position < code.Count)
            {
                var instruction = code[position++];
                int start = instruction.Rt;
                Node node;
                switch (instruction.Op)
                {
                    case Op.Line or Op.Target or Op.Nop:
                        node = new(start, "", [instruction.RtEnd], true);
                        if (instruction.Op == Op.Nop) Changed("statement-noop");
                        break;
                    case Op.Jump:
                        node = new(start, "", [instruction.JumpTarget], true);
                        Changed("jump-forwarding");
                        break;
                    case Op.JumpIfFalse:
                        var condition = Expr();
                        node = new(start, "if:" + condition.Text, [condition.End, instruction.JumpTarget]);
                        break;
                    case Op.Return:
                        var value = Expr();
                        if (function.Return.Count > 0) value = AsEnum(value, function.Return[0].Type);
                        bool noValue = value.Empty && function.Return.Count == 0 && (function.Flags & 0x20) == 0;
                        node = new(start, noValue ? "end" : "return:" + value.Text, []);
                        break;
                    case Op.Switch:
                        var selector = Expr();
                        switches[start] = blob.Types[instruction.Arg(0)].Name;
                        node = new(start, "switch:" + Type(instruction.Arg(0)) + ":" + selector.Text, [instruction.RtEnd + instruction.Arg(1)]);
                        break;
                    case Op.SwitchLabel:
                        var label = Expr();
                        cases[start] = label;
                        if (label.End != instruction.RtEnd + instruction.Arg(1)) throw Invalid("Switch-label body boundary changed.");
                        node = new(start, "case:" + label.Text, [label.End, instruction.Rt + 3 + instruction.Arg(0)]);
                        break;
                    case Op.SwitchDefault:
                        node = new(start, "default", [instruction.RtEnd]);
                        break;
                    default:
                        position--;
                        var expression = Expr();
                        node = new(start, "expression:" + expression.Text, [expression.End]);
                        break;
                }
                nodes.Add(start, node);
            }
            if (code.Count > 0 && code[^1].RtEnd != function.RuntimeSize) throw Invalid("The function runtime boundary is inconsistent.");
            nodes[function.RuntimeSize] = new(function.RuntimeSize, "end", []);
            foreach (var selection in switches)
            {
                int at = Resolve(nodes[selection.Key].Targets.Single());
                var visited = new HashSet<int>();
                while (visited.Add(at) && cases.TryGetValue(at, out var label))
                {
                    nodes[at] = nodes[at] with { Label = "case:" + AsEnum(label, selection.Value).Text };
                    at = Resolve(nodes[at].Targets[1]);
                }
            }
            int entry = Resolve(0);
            var ids = new Dictionary<int, int> { [entry] = 0 };
            var pending = new Queue<int>([entry]);
            var result = new List<string>();
            while (pending.TryDequeue(out int at))
            {
                var node = nodes[at];
                var targets = new List<int>();
                foreach (int target in node.Targets)
                {
                    int resolved = Resolve(target);
                    if (!ids.TryGetValue(resolved, out int id))
                    {
                        ids.Add(resolved, id = ids.Count);
                        pending.Enqueue(resolved);
                    }
                    targets.Add(id);
                }
                result.Add(JsonSerializer.Serialize(new { node.Label, targets }));
            }
            Changed("unreachable-statements", nodes.Values.Count(node => !node.Forward && !ids.ContainsKey(node.Start)));
            return new(result, changes);
        }

        // Keep a cycle of jumps as an empty loop; collapsing it would incorrectly turn looping into fallthrough.
        private int Resolve(int target)
        {
            var visited = new HashSet<int>();
            while (true)
            {
                if (!nodes.TryGetValue(target, out var node)) throw Invalid("A control-flow edge does not address a statement boundary.");
                if (!node.Forward) return target;
                if (!visited.Add(target))
                {
                    nodes[target] = new(target, "empty-loop", [target]);
                    return target;
                }
                target = node.Targets.Single();
            }
        }

        private Expression Expr()
        {
            if (++depth > 256) throw Invalid("Expression nesting exceeds the comparison limit.");
            try { return ExpressionCore(); }
            finally { depth--; }
        }

        // Reject unknown expression forms so unsupported code cannot silently compare equal.
        private Expression ExpressionCore()
        {
            while (position < code.Count && code[position].Op is Op.Line or Op.Target) position++;
            if (position >= code.Count) throw Invalid("An expression ends unexpectedly.");
            var instruction = code[position++];
            string head = instruction.Op.ToString();
            var children = new List<string>();
            int end = instruction.RtEnd;
            long? number = null;
            string? enumeration = null, type = null;
            Expression? skipped = null;
            string? logic = null;
            IReadOnlyList<Expression>? terms = null;
            IReadOnlyList<Expression>? conditional = null;
            int storageBits = 0;
            Expression Child() { var value = Expr(); children.Add(value.Text); end = value.End; return value; }
            switch (instruction.Op)
            {
                case Op.Nop: return new("omitted", end, true);
                case Op.IntZero or Op.IntOne or Op.IntConst:
                    number = instruction.Op == Op.IntZero ? 0 : instruction.Op == Op.IntOne ? 1 : instruction.Arg(0);
                    head = "integer:" + number.Value.ToString(CultureInfo.InvariantCulture);
                    break;
                case Op.ShortConst or Op.ByteConst:
                    number = instruction.Arg(0);
                    if (instruction.Op == Op.ByteConst) storageBits = 8;
                    head += ":" + instruction.Arg(0).ToString(CultureInfo.InvariantCulture);
                    break;
                case Op.NameConst or Op.LocalVar or Op.ParamVar:
                    head += ":" + Name(instruction.Arg(0));
                    type = (instruction.Op == Op.LocalVar ? function.Locals : instruction.Op == Op.ParamVar ? function.Params : [])
                        .FirstOrDefault(property => property.Name == Name(instruction.Arg(0)))?.Type;
                    break;
                case Op.ObjectVar or Op.BindableVar or Op.DefaultVar:
                    head += ":" + Name(instruction.Arg(0)) + ":" + Type(instruction.Arg(1));
                    type = PropertyType(instruction.Arg(1), Name(instruction.Arg(0)));
                    break;
                case Op.StringConst: head += ":" + JsonSerializer.Serialize(instruction.Text); break;
                case Op.FloatConst: head += ":" + BitConverter.SingleToUInt32Bits(instruction.Float).ToString("X8"); break;
                case Op.This: type = Owner; break;
                case Op.TheGame: type = "CR4Game"; break;
                case Op.ThePlayer: type = "CR4Player"; break;
                case Op.TheCamera: type = "CCameraDirector"; break;
                case Op.TheSound: type = "CScriptSoundSystem"; break;
                case Op.TheDebug: type = "CDebugAttributesManager"; break;
                case Op.TheTimer: type = "CTimerScriptKeyword"; break;
                case Op.TheInput: type = "CInputManager"; break;
                case Op.TheTelemetry: type = "CR4TelemetryScriptProxy"; break;
                case Op.TheHud: type = "CR4Hud"; break;
                case Op.Null or Op.BoolTrue or Op.BoolFalse: break;
                case Op.Parent: type = context.Machine(Owner) ?? function.Class?.StateOf; Child(); break;
                case Op.Delete: Child(); break;
                case Op.Assign:
                    var target = Child();
                    var assigned = AsEnum(Child(), target.Type);
                    children[1] = assigned.Text;
                    break;
                case Op.StructMember:
                    head += ":" + Name(instruction.Arg(0)) + ":" + Type(instruction.Arg(1));
                    type = PropertyType(instruction.Arg(1), Name(instruction.Arg(0)));
                    Child(); break;
                case Op.Context:
                    var instance = Child();
                    string? previous = receiver;
                    receiver = instance.Type;
                    type = Child().Type;
                    receiver = previous;
                    Boundary(instruction.Rt + 3 + (ushort)instruction.Arg(0), end);
                    break;
                case Op.Skip:
                    skipped = Child(); Boundary(instruction.JumpTarget, end); break;
                case Op.Conditional:
                    var test = Child(); var whenTrue = Child(); Boundary(instruction.Rt + 3 + instruction.Arg(0), end);
                    var whenFalse = Child(); Boundary(instruction.Rt + 5 + instruction.Arg(1), end);
                    conditional = new[] { test, whenTrue, whenFalse };
                    type = whenTrue.Type == whenFalse.Type ? whenTrue.Type : null;
                    break;
                case Op.Constructor:
                    head += ":" + instruction.Arg(0) + ":" + Type(instruction.Arg(1));
                    type = blob.Types[instruction.Arg(1)].Name;
                    if (instruction.Arg(0) is < 0 or > 256) throw Invalid("Invalid constructor argument count.");
                    for (int count = 0; count < instruction.Arg(0); count++) Child();
                    break;
                case Op.FinalFunc or Op.VirtualFunc or Op.VirtualParentFunc or Op.EntryFunc:
                    string method = instruction.Op == Op.FinalFunc && instruction.Arg(2) > 0 ? blob.FunctionRefs[instruction.Arg(2) - 1].Name
                        : instruction.Op == Op.FinalFunc ? function.Name : Name(instruction.Arg(instruction.Op == Op.EntryFunc ? 1 : 2));
                    if (method == "@self") method = function.Name;
                    string callOwner = instruction.Op == Op.FinalFunc && instruction.Arg(2) > 0
                        ? blob.FunctionRefs[instruction.Arg(2) - 1].OwnerType is var ownerIndex && ownerIndex >= 0 ? blob.Types[ownerIndex].Name : ""
                        : receiver ?? Owner;
                    var signature = instruction.Op == Op.FinalFunc && instruction.Arg(2) < -1 ? null
                        : context.Find(callOwner, method == "@self" ? function.Name : method);
                    type = signature?.ReturnType;
                    receiver = null;
                    head += ":" + (instruction.Op == Op.FinalFunc ? Function(instruction.Arg(2)) : method);
                    var arguments = new List<Expression>();
                    while (position < code.Count && code[position].Op != Op.ParamEnd) arguments.Add(Expr());
                    if (position >= code.Count) throw Invalid("An argument list has no ParamEnd.");
                    end = code[position++].RtEnd;
                    Boundary(instruction.Rt + 3 + (ushort)instruction.Arg(0), end);
                    for (int index = 0; index < Math.Min(arguments.Count, signature?.Parameters.Length ?? 0); index++)
                        arguments[index] = AsEnum(arguments[index], signature!.Parameters[index]);
                    while (arguments.Count > 0 && arguments[^1].Empty) { arguments.RemoveAt(arguments.Count - 1); Changed("trailing-argument-placeholder"); }
                    if (instruction.Op == Op.FinalFunc && instruction.Arg(2) == -Operators.IndexOf("Neg_Int32") - 2
                        && arguments is [{ Number: { } integer }] && integer is >= int.MinValue and <= int.MaxValue && integer != int.MinValue)
                    {
                        Changed("negative-integer-constant");
                        return new(Text("integer:" + -integer, []), end, Number: -integer, Type: "Int32");
                    }
                    if (instruction.Op == Op.FinalFunc && arguments.Count == 2 && arguments[1].Skipped is { } right
                        && new[] { "LogicAnd_Bool_Bool", "LogicOr_Bool_Bool" }.FirstOrDefault(name => -Operators.IndexOf(name) - 2 == instruction.Arg(2)) is { } logical)
                    {
                        logic = logical;
                        terms = (arguments[0].Logic == logical ? arguments[0].Terms! : [arguments[0]])
                            .Concat(right.Logic == logical ? right.Terms! : [right]).ToArray();
                        head = "short-circuit:" + logical;
                        arguments = terms.ToList();
                        Changed("short-circuit-grouping");
                    }
                    children.AddRange(arguments.Select(value => value.Text));
                    break;
                case Op.TestEqual or Op.TestNotEqual:
                    head += ":" + Type(instruction.Arg(0)); Child(); Child(); break;
                case Op.New:
                    head += ":" + Type(instruction.Arg(0)); type = blob.Types[instruction.Arg(0)].Name; Child(); break;
                case Op.IntToEnum:
                    head += ":" + Type(instruction.Arg(0));
                    var operand = Child();
                    enumeration = blob.Types[instruction.Arg(0)].Name;
                    type = enumeration;
                    var normalized = AsEnum(operand with { StorageBits = 0 }, enumeration);
                    if (normalized.Enum == enumeration) return normalized with { End = end };
                    break;
                case Op.EnumToInt:
                    head += ":" + Type(instruction.Arg(0));
                    var converted = Child();
                    string enumName = blob.Types[instruction.Arg(0)].Name;
                    long? convertedNumber = converted.Number is { } encoded ? DecodeEnumConstant(enumName, encoded, converted.StorageBits) : null;
                    if (convertedNumber is { } constant && KnownEnumValue(enumName, constant)
                        && (converted.Enum is null || converted.Enum == enumName))
                    {
                        number = constant;
                        head = "integer:" + number.Value.ToString(CultureInfo.InvariantCulture);
                        children.Clear();
                        Changed("enum-constant-conversion");
                    }
                    break;
                case Op.DynamicCast:
                    type = blob.Types[instruction.Arg(0)].Name;
                    head += ":" + Type(instruction.Arg(0)); Child(); break;
                case Op.EnumToString:
                    head += ":" + Type(instruction.Arg(0)); Child(); break;
                case >= Op.BoolToByte and <= Op.ObjectToString:
                    Child(); break;
                case >= Op.ArrayClear and <= Op.ArrayElement:
                    head += ":" + Type(instruction.Arg(0));
                    Child();
                    string? element = ArrayElementType(blob.Types[instruction.Arg(0)].Name);
                    if (instruction.Op is not (Op.ArrayClear or Op.ArraySize or Op.ArrayPopBack or Op.ArrayLast))
                    {
                        var argument = Child();
                        if (instruction.Op is Op.ArrayFindFirst or Op.ArrayFindFirstFast or Op.ArrayFindLast or Op.ArrayFindLastFast
                            or Op.ArrayContains or Op.ArrayContainsFast or Op.ArrayPushBack or Op.ArrayRemove or Op.ArrayRemoveFast)
                            children[1] = AsEnum(argument, element).Text;
                    }
                    if (instruction.Op == Op.ArrayInsert)
                    {
                        var inserted = Child();
                        children[2] = AsEnum(inserted, element).Text;
                    }
                    if (instruction.Op is Op.ArrayLast or Op.ArrayElement) type = element;
                    break;
                case >= Op.StaticArraySize and <= Op.StaticArrayElement:
                    var array = Child();
                    string? staticElement = ArrayElementType(array.Type);
                    if (instruction.Op is not (Op.StaticArraySize or Op.StaticArrayLast))
                    {
                        var argument = Child();
                        if (instruction.Op != Op.StaticArrayElement) children[1] = AsEnum(argument, staticElement).Text;
                    }
                    if (instruction.Op is Op.StaticArrayLast or Op.StaticArrayElement) type = staticElement;
                    break;
                case Op.MapClear or Op.MapSize or Op.MapContains or Op.MapErase or Op.MapElement or Op.MapKey or Op.MapTryGet:
                    head += ":" + Type(instruction.Arg(0)); Child();
                    var map = MapTypes(blob.Types[instruction.Arg(0)].Name);
                    if (instruction.Op is not (Op.MapClear or Op.MapSize))
                    {
                        var key = Child();
                        if (instruction.Op != Op.MapKey) children[1] = AsEnum(key, map.Key).Text;
                    }
                    if (instruction.Op == Op.MapTryGet) Child();
                    type = instruction.Op == Op.MapElement ? map.Value : instruction.Op == Op.MapKey ? map.Key : null;
                    break;
                default: throw Invalid("Unqualified expression opcode: " + instruction.Op);
            }
            return new(Text(head, children), end, Number: number, Enum: enumeration, Type: type, Skipped: skipped, Logic: logic, Terms: terms, Conditional: conditional, StorageBits: storageBits);
        }

        // Use the expected enum type to resolve numeric constants. Only known values can be normalized this way.
        private Expression AsEnum(Expression expression, string? type)
        {
            if (type is null || expression.Enum is not null && expression.Enum != type) return expression;
            if (expression.Conditional is { Count: 3 } parts)
            {
                var first = AsEnum(parts[1], type);
                var second = AsEnum(parts[2], type);
                if (first.Number is null || second.Number is null || first.Enum != type || second.Enum != type) return expression;
                Changed("typed-enum-conditional");
                return expression with { Text = Text("Conditional", [parts[0].Text, first.Text, second.Text]), Enum = type, Type = type,
                    Conditional = new[] { parts[0], first, second } };
            }
            if (expression.Number is not { } encoded) return expression;
            long number = DecodeEnumConstant(type, encoded, expression.StorageBits);
            if (!KnownEnumValue(type, number)) return expression;
            Changed("typed-enum-constant");
            return expression with { Text = Text("enum:" + type + ":" + number, []), Number = number, Enum = type, Type = type, StorageBits = 0 };
        }
        private string? PropertyType(int owner, string name)
        {
            string type = blob.Types[owner].Name;
            return blob.Classes.FirstOrDefault(value => value.Name == type)?.Properties.FirstOrDefault(value => value.Name == name)?.Type
                ?? blob.Structs.FirstOrDefault(value => value.Name == type)?.Properties.FirstOrDefault(value => value.Name == name)?.Type
                ?? blob.ImportedProperties.Where(value => value.OwnerType == owner && value.Name == name)
                    .Select(value => blob.Types[value.Type].Name).FirstOrDefault()
                ?? blob.AddedFields.FirstOrDefault(value => value.Owner == type && value.Name == name)?.Type
                ?? context.Field(type, name);
        }
        private bool KnownEnumValue(string type, long number) => (enums ??= new EnumResolver(blob, context.EnumValues)).Find(type, number) is not null;
        private long DecodeEnumConstant(string type, long number, int storageBits) => (enums ??= new EnumResolver(blob, context.EnumValues)).DecodeConstant(type, number, storageBits);
        private static string? ArrayElementType(string? type) => type is not null && type.StartsWith("array:", StringComparison.Ordinal)
            ? type.Split(',', 3).ElementAtOrDefault(2) : null;
        // Map types can nest, so find the end of each complete type before splitting key and value.
        private static (string Key, string Value) MapTypes(string type)
        {
            string[] parts = type.Split(',');
            int at = 0;
            string Read(int depth)
            {
                if (depth > 64 || at >= parts.Length) throw Invalid("Invalid nested map type.");
                int start = at;
                string part = parts[at++];
                if (part.StartsWith("map:", StringComparison.Ordinal) || part.StartsWith("array:", StringComparison.Ordinal))
                {
                    if (at >= parts.Length || parts[at++] != "0") throw Invalid("Invalid container type metadata.");
                    Read(depth + 1);
                    if (part.StartsWith("map:", StringComparison.Ordinal)) Read(depth + 1);
                }
                return string.Join(",", parts[start..at]);
            }
            if (!type.StartsWith("map:", StringComparison.Ordinal) || parts.Length < 4 || parts[1] != "0") throw Invalid("Invalid map type.");
            at = 2;
            string key = Read(0), value = Read(0);
            if (at != parts.Length) throw Invalid("Trailing map type metadata.");
            return (key, value);
        }
        // Include lengths so distinct expression trees cannot produce an ambiguous text representation.
        private static string Text(string head, IReadOnlyList<string> children) => head.Length + ":" + head + ":" + children.Count
            + "[" + string.Concat(children.Select(child => child.Length + ":" + child)) + "]";

        private static void Boundary(int expected, int actual)
        {
            if (expected != actual) throw Invalid("An expression skip does not match its parsed boundary: " + expected + " != " + actual);
        }
        private string Type(int index) => index >= 0 && index < blob.Types.Count
            ? (typeCorrections?.GetValueOrDefault(blob.Types[index].Name) ?? blob.Types[index].Name) + "#" + blob.Types[index].Kind
            : throw Invalid("Invalid type reference.");
        private string Name(int index) => (uint)index < (uint)blob.Names.Length ? blob.Names[index] : throw Invalid("Invalid name reference.");
        private string Function(int index)
        {
            if (index <= 0) return index.ToString(CultureInfo.InvariantCulture);
            if (index > blob.FunctionRefs.Count) throw Invalid("Invalid function reference.");
            var value = blob.FunctionRefs[index - 1];
            return (value.OwnerType < 0 ? "global" : Type(value.OwnerType)) + "." + value.Name;
        }
        private void Changed(string name, int count = 1) { if (count > 0) changes[name] = changes.GetValueOrDefault(name) + count; }
        private static InvalidDataException Invalid(string message) => new(message);
    }
}
