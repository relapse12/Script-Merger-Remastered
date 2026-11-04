using System.Globalization;
using System.Text.Json;
using WSCompiler.Core.Scripts;

namespace WSCompiler.Recovery;

// Compares compiled instructions after resolving table indices and offsets.
// ControlFlowComparison handles further differences in how the same flow was compiled.
public static class BlobComparison
{
    // Matching bodies are interchangeable only when their signatures, flags and locals also match.
    public static string FunctionContract(ScriptFunction function) => JsonSerializer.Serialize(new
    {
        Identity = FunctionIdentity(function), function.Flags,
        Return = function.Return.Select(Property), Parameters = function.Params.Select(Property),
        Locals = function.Locals.Select(Property)
    });

    private static object Property(ScriptProperty property) => new
    {
        property.Name, property.Type, property.Flags, property.Hint, property.Binding
    };

    public static string FunctionIdentity(ScriptFunction function) =>
        (function.Class is { IsState: true } state ? state.StateOf + "::" + state.Name : function.Class?.Name ?? function.Owner)
        + ":" + function.Kind + ":" + function.Name + "(" + string.Join(",", function.Params.Select(parameter => parameter.Type)) + ")";

    // Resolve table entries and jumps before comparison. Ignore line markers and compiler-generated call-size offsets.
    public static IReadOnlyList<string> Instructions(ScriptBlob blob, ScriptFunction function)
    {
        var decoded = Bytecode.Decode(function.Code);
        var instructions = decoded.Where(item => item.Op is not (Op.Line or Op.Target)).ToArray();
        string Target(int offset)
        {
            int index = Array.FindIndex(instructions, instruction => instruction.Rt >= offset);
            return (index < 0 ? instructions.Length : index).ToString(CultureInfo.InvariantCulture);
        }
        string Type(int index) => index >= 0 && index < blob.Types.Count
            ? blob.Types[index].Name + "#" + blob.Types[index].Kind : "invalid:" + index;
        string Function(int index)
        {
            if (index <= 0) return index.ToString(CultureInfo.InvariantCulture);
            var reference = blob.FunctionRefs[index - 1];
            return Type(reference.OwnerType) + "." + reference.Name;
        }
        return instructions.Select(instruction =>
        {
            string format = Bytecode.OperandFormat(instruction.Op);
            var operands = new List<string>();
            for (int index = 0; index < instruction.Args.Length; index++)
            {
                if ((instruction.Op is Op.FinalFunc or Op.VirtualFunc or Op.VirtualParentFunc) && index < 2) continue;
                if (instruction.Op == Op.EntryFunc && index == 0) continue;
                int value = instruction.Args[index];
                string operand = format[index] switch
                {
                    'n' => blob.Names[value],
                    't' => Type(value),
                    'f' => Function(value),
                    'h' when instruction.Op == Op.Switch => Target(instruction.RtEnd + value),
                    'h' when instruction.Op == Op.SwitchLabel => Target((index == 0 ? instruction.Rt + 3 : instruction.RtEnd) + value),
                    'h' when instruction.Op is Op.Jump or Op.JumpIfFalse or Op.Context or Op.Skip => Target(instruction.Rt + 3 + value),
                    _ => value.ToString(CultureInfo.InvariantCulture)
                };
                operands.Add(operand);
            }
            if (instruction.Text is not null) operands.Add(JsonSerializer.Serialize(instruction.Text));
            if (instruction.Op == Op.FloatConst) operands.Add(BitConverter.SingleToUInt32Bits(instruction.Float).ToString("X8"));
            return instruction.Op + ":" + string.Join("|", operands);
        }).ToArray();
    }
}
