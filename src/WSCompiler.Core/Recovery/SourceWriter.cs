using System.Globalization;
using System.Text;
using Decompiler = WSCompiler.Core.Scripts.Decompiler;
using WSCompiler.Core.Scripts.Standalone;
using Index = WSCompiler.Core.Scripts.Standalone.Index;

namespace WSCompiler.Recovery;

// Use one consistent source format for merge comparisons and declaration identities.
public static class SourceWriter
{
    public static string Write(IEnumerable<Item> declarations)
    {
        var writer = new Writer();
        foreach (var item in declarations)
            writer.Item(item);
        return writer.Text.ToString().TrimEnd() + "\n";
    }

    public static string Type(TypeRef type) => type.Name
        + (type.Args.Count == 0 ? "" : "<" + string.Join(", ", type.Args.Select(Type)) + ">");

    // Parenthesize operators explicitly so writing an expression preserves its precedence.
    public static string Expression(Expr expression) => expression.Kind switch
    {
        IntLit value => value.Value.ToString(CultureInfo.InvariantCulture),
        FloatLit value => Decompiler.FormatFloat(value.Value) + "f",
        StrLit value => Quote(value.Value, '"'),
        NameLit value => Quote(value.Value, '\''),
        BoolLit value => value.Value ? "true" : "false",
        NullLit => "NULL",
        DefaultArray value => "{ " + string.Join(", ", value.Values.Select(Expression)) + " }",
        Ident value => value.Name,
        ThisExpr => "this",
        SuperExpr => "super",
        ParentExpr => "parent",
        VirtualParentExpr => "virtual_parent",
        Member value => $"{Postfix(value.Object)}.{value.Name}",
        Call value => $"{Postfix(value.Callee)}({string.Join(", ", value.Args.Select(argument => argument is null ? "" : Expression(argument)))})",
        Index value => $"{Postfix(value.Array)}[{Expression(value.Idx)}]",
        Unary value => $"({value.Op}{Postfix(value.Operand)})",
        Binary value => $"({Expression(value.Left)} {value.Op} {Expression(value.Right)})",
        Assign value => $"{Expression(value.Target)} {value.Op} {Expression(value.Value)}",
        Ternary value => $"({Expression(value.Cond)} ? {Expression(value.Then)} : {Expression(value.Else)})",
        Cast value => $"({Type(value.Type)})({Expression(value.Operand)})",
        New value => $"new {Type(value.Type)}{(value.Owner is null ? "" : " in " + Expression(value.Owner))}",
        Sequence value => string.Join(", ", value.Values.Select(Expression)),
        Postfix value => $"({Expression(value.Operand)}{value.Op})",
        _ => throw new NotSupportedException($"Unsupported recovered expression {expression.Kind.GetType().Name}.")
    };

    private static string Postfix(Expr value) => value.Kind is Cast or New or Assign ? "(" + Expression(value) + ")" : Expression(value);

    private static string DefaultValue(Expr value)
    {
        // Default scalar values accept signed literals; parentheses introduce struct field assignments.
        return value.Kind is Unary { Op: "-", Operand: var operand } && operand.Kind is IntLit or FloatLit
            ? "-" + Expression(operand)
            : Expression(value);
    }

    private static string Quote(string text, char delimiter) => delimiter
        + text.Replace(delimiter.ToString(), "\\" + delimiter) + delimiter;

    private sealed class Writer
    {
        public StringBuilder Text { get; } = new();
        private int depth;
        private void Line(string value = "") => Text.Append(' ', depth * 4).Append(value).Append('\n');

        public void Item(Item item)
        {
            switch (item)
            {
                case ClassItem value:
                    Class(value.Decl);
                    break;
                case EnumItem value:
                    Line($"enum {value.Decl.Name}");
                    Line("{");
                    depth++;
                    foreach (var (name, number) in value.Decl.Members)
                        Line(name + (number is null ? "" : " = " + number.Value.ToString(CultureInfo.InvariantCulture)) + ",");
                    depth--;
                    Line("}");
                    break;
                case FunctionItem value:
                    Function(value.Decl);
                    break;
                case FieldItem value:
                    Field(value.Decl);
                    break;
                default:
                    throw new NotSupportedException($"Unsupported declaration {item.GetType().Name}.");
            }
            Line();
        }

        private void Class(ClassDecl value)
        {
            string declaration = (value.IsImport ? "import " : "") + (value.IsAbstract ? "abstract " : "")
                + (value.StateMachineOf is { } machine ? $"state {value.Name} in {machine}"
                    : (value.IsStateMachine ? "statemachine " : "") + (value.IsStruct ? "struct " : "class ") + value.Name);
            if (value.Parent is not null)
                declaration += " extends " + value.Parent;
            Line(declaration);
            Line("{");
            depth++;
            foreach (var field in value.Fields)
                Field(field);
            foreach (var assignment in value.Defaults)
                Line($"default {assignment.Field} = {DefaultValue(assignment.Value)};");
            foreach (var hint in value.Hints)
                Line($"hint {hint.Key} = {Quote(hint.Value, '"')};");
            foreach (var function in value.Methods)
            {
                Line();
                Function(function);
            }
            depth--;
            Line("}");
        }

        private static string Access(Visibility visibility) => visibility switch
        {
            Visibility.Public => "public ",
            Visibility.Private => "private ",
            Visibility.Protected => "protected ",
            _ => ""
        };

        private void Annotation(Annotation? annotation)
        {
            if (annotation is null)
                return;
            string name = annotation.Kind switch
            {
                AnnotationKind.AddField => "addField",
                AnnotationKind.AddMethod => "addMethod",
                AnnotationKind.WrapMethod => "wrapMethod",
                AnnotationKind.ReplaceMethod => "replaceMethod",
                _ => throw new NotSupportedException()
            };
            Line(string.IsNullOrEmpty(annotation.Class) ? $"@{name}" : $"@{name}({annotation.Class})");
        }

        private void Field(FieldDecl value)
        {
            Annotation(value.Annotation);
            var flags = value.Specifiers;
            string modifiers = (flags.IsImport ? "import " : "") + Access(flags.Visibility)
                + (flags.IsEditable ? "editable " : "") + (flags.IsSaved ? "saved " : "")
                + (flags.IsConst && !value.IsBinding ? "const " : "") + (flags.IsInlined ? "inlined " : "");
            if (value.IsBinding && flags.IsOptional)
                modifiers += "optional ";
            string initializer = value.IsBinding ? value.Binding is null ? "" : " = "
                + (value.Binding.StartsWith('$') ? value.Binding[1..] : Quote(value.Binding, '"'))
                : value.Initializer is null ? "" : " = " + Expression(value.Initializer);
            Line($"{modifiers}{(value.IsBinding ? "autobind" : "var")} {string.Join(", ", value.Names)} : {Type(value.Type)}{initializer};");
        }

        private void Function(FunctionDecl value)
        {
            Annotation(value.Annotation);
            var flags = value.Specifiers;
            bool inherited = value.Annotation?.Kind is AnnotationKind.WrapMethod or AnnotationKind.ReplaceMethod;
            string modifiers = inherited ? "" : (flags.IsImport ? "import " : "") + Access(flags.Visibility)
                + (flags.IsFinal ? "final " : "") + (flags.IsEntry ? "entry " : "")
                + (flags.IsLatent ? "latent " : "") + (flags.IsExec ? "exec " : "")
                + (flags.IsQuest ? "quest " : "") + (flags.IsTimer ? "timer " : "")
                + (flags.IsStoryScene ? "storyscene " : "") + (flags.IsCleanup ? "cleanup " : "")
                + (flags.IsReward ? "reward " : "");
            string parameters = string.Join(", ", value.Params.Select(parameter => (parameter.IsOut ? "out " : "")
                + (parameter.IsOptional ? "optional " : "") + $"{parameter.Name} : {Type(parameter.Type)}"));
            string result = value.ReturnType is null || flags.IsEvent ? "" : " : " + Type(value.ReturnType);
            string signature = $"{modifiers}{(flags.IsEvent && !inherited ? "event" : "function")} {value.Name}({parameters}){result}";
            if (value.Body is null)
            {
                Line(signature + ";");
                return;
            }
            Line(signature);
            Block(value.Body);
        }

        private void Block(IEnumerable<Stmt> statements)
        {
            Line("{");
            depth++;
            foreach (var statement in statements)
                Statement(statement);
            depth--;
            Line("}");
        }

        private void Branch(Stmt statement)
        {
            if (statement.Kind is BlockStmt block)
                Block(block.Statements);
            else
                Block([statement]);
        }

        // A switch's final label needs a following statement; add a break when necessary.
        private void Statement(Stmt statement)
        {
            switch (statement.Kind)
            {
                case VarStmt value:
                    Field(value.Decl);
                    break;
                case ExprStmt value:
                    Line(Expression(value.Expr) + ";");
                    break;
                case IfStmt value:
                    Line($"if ({Expression(value.Cond)})");
                    Branch(value.Then);
                    if (value.Else is not null)
                    {
                        Line("else");
                        Branch(value.Else);
                    }
                    break;
                case WhileStmt value:
                    Line($"while ({Expression(value.Cond)})");
                    Branch(value.Body);
                    break;
                case DoWhileStmt value:
                    Line("do");
                    Branch(value.Body);
                    Line($"while ({Expression(value.Cond)});");
                    break;
                case ForStmt value:
                    string E(Expr? expression) => expression is null ? "" : Expression(expression);
                    Line($"for ({E(value.Init)}; {E(value.Cond)}; {E(value.Step)})");
                    Branch(value.Body);
                    break;
                case SwitchStmt value:
                    Line($"switch ({Expression(value.Value)})");
                    Line("{");
                    depth++;
                    foreach (var part in value.Items)
                        switch (part)
                        {
                            case CaseItem item:
                                Line($"case {Expression(item.Value)}:");
                                break;
                            case DefaultItem:
                                Line("default:");
                                break;
                            case StmtItem item:
                                depth++;
                                Statement(item.Stmt);
                                depth--;
                                break;
                        }
                    if (value.Items.LastOrDefault() is CaseItem or DefaultItem)
                    {
                        depth++;
                        Line("break;");
                        depth--;
                    }
                    depth--;
                    Line("}");
                    break;
                case ReturnStmt value:
                    Line("return" + (value.Value is null ? "" : " " + Expression(value.Value)) + ";");
                    break;
                case DeleteStmt value:
                    Line("delete " + Expression(value.Value) + ";");
                    break;
                case BreakStmt:
                    Line("break;");
                    break;
                case ContinueStmt:
                    Line("continue;");
                    break;
                case BlockStmt value:
                    Block(value.Statements);
                    break;
                case EmptyStmt:
                    Line(";");
                    break;
                default:
                    throw new NotSupportedException($"Unsupported statement {statement.Kind.GetType().Name}.");
            }
        }
    }
}
