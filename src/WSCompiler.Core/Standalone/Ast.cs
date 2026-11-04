namespace WSCompiler.Core.Scripts.Standalone;


// Shared syntax tree for parsed scripts and recovered blobs. Symbols resolves source TypeRef values to compiler types.
public sealed record TypeRef(string Name, List<TypeRef> Args)
{
    public TypeRef(string name) : this(name, []) { }
}

public enum Visibility { Default, Public, Protected, Private }

public enum AnnotationKind { AddField, AddMethod, WrapMethod, ReplaceMethod }

public sealed record Annotation(AnnotationKind Kind, string? Class, int Line = 0)
{
    // Diagnostic positions do not change declaration identity during a merge.
    public bool Equals(Annotation? other) => other is not null && Kind == other.Kind && Class == other.Class;
    public override int GetHashCode() => HashCode.Combine(Kind, Class);
}

public sealed class FieldSpecifiers
{
    public Visibility Visibility;
    public bool IsImport, IsEditable, IsSaved, IsConst, IsInlined, IsOptional;
}

public sealed class FieldDecl
{
    public required List<string> Names { get; init; }
    public required TypeRef Type { get; init; }
    public required FieldSpecifiers Specifiers { get; init; }
    public Annotation? Annotation { get; set; }
    public Expr? Initializer { get; init; }
    public int Line { get; init; }
    public bool IsBinding { get; init; }
    // Autobind target: single or a component name.
    public string? Binding { get; init; }
}

public sealed class FunctionSpecifiers
{
    public Visibility Visibility;
    public bool IsImport, IsFinal, IsLatent, IsEntry, IsEvent, IsTimer, IsExec, IsQuest, IsStoryScene, IsReward, IsCleanup;

    public FunctionSpecifiers Copy() => (FunctionSpecifiers)MemberwiseClone();
}

public sealed record Param(string Name, TypeRef Type, bool IsOut, bool IsOptional);

// Recovered CompiledFlags take precedence over source specifiers during assembly.
public sealed class FunctionDecl
{
    public required string Name { get; init; }
    public required FunctionSpecifiers Specifiers { get; init; }
    public required List<Param> Params { get; init; }
    public TypeRef? ReturnType { get; init; }
    public List<Stmt>? Body { get; init; }
    public Annotation? Annotation { get; set; }
    public int? CompiledFlags { get; set; }
    public int Line { get; init; }
    public bool UsesWrappedMethod { get; init; }
}

public sealed record DefaultAssignment(string Field, Expr Value);

// Defaults with no source form remain in BinaryDefaults and require compiled output.
public sealed class ClassDecl
{
    public string Name = "";
    public int Line;
    public bool HasAutoStateDefault;
    public string? Parent;
    public string? StateMachineOf;
    public bool IsImport, IsAbstract, IsStateMachine, IsStruct;
    public List<FieldDecl> Fields = [];
    public List<FunctionDecl> Methods = [];
    public List<DefaultAssignment> Defaults = [];
    public Dictionary<string, string> Hints = new(StringComparer.Ordinal);
    public List<ScriptDefault> BinaryDefaults = [];
    public bool RequiresBinaryDefaults;
}

public sealed class EnumDecl
{
    public required string Name { get; init; }
    public required List<(string Name, long? Value)> Members { get; init; }
    public List<int> Lines { get; init; } = [];
}

public abstract record Item;
public sealed record ClassItem(ClassDecl Decl) : Item;
public sealed record EnumItem(EnumDecl Decl) : Item;
public sealed record FunctionItem(FunctionDecl Decl) : Item;
public sealed record FieldItem(FieldDecl Decl) : Item;

public sealed record Stmt(StmtKind Kind, int Line);

public abstract record StmtKind;
public sealed record VarStmt(FieldDecl Decl) : StmtKind;
public sealed record ExprStmt(Expr Expr) : StmtKind;
public sealed record IfStmt(Expr Cond, Stmt Then, Stmt? Else) : StmtKind;
public sealed record WhileStmt(Expr Cond, Stmt Body) : StmtKind;
public sealed record DoWhileStmt(Stmt Body, Expr Cond) : StmtKind;
public sealed record ForStmt(Expr? Init, Expr? Cond, Expr? Step, Stmt Body) : StmtKind;
public sealed record SwitchStmt(Expr Value, List<SwitchItem> Items) : StmtKind;
public sealed record BreakStmt : StmtKind;
public sealed record ContinueStmt : StmtKind;
public sealed record ReturnStmt(Expr? Value) : StmtKind;
public sealed record DeleteStmt(Expr Value) : StmtKind;
public sealed record BlockStmt(List<Stmt> Statements) : StmtKind;
public sealed record EmptyStmt : StmtKind;

public abstract record SwitchItem;
public sealed record CaseItem(Expr Value) : SwitchItem;
public sealed record DefaultItem : SwitchItem;
public sealed record StmtItem(Stmt Stmt) : SwitchItem;

public sealed record Expr(ExprKind Kind, int Line);

public abstract record ExprKind;
public sealed record IntLit(long Value) : ExprKind;
public sealed record FloatLit(float Value, string Text) : ExprKind;
public sealed record StrLit(string Value) : ExprKind;
public sealed record NameLit(string Value) : ExprKind;
public sealed record BoolLit(bool Value) : ExprKind;
public sealed record NullLit : ExprKind;
public sealed record DefaultArray(List<Expr> Values) : ExprKind;
public sealed record Ident(string Name) : ExprKind;
public sealed record ThisExpr : ExprKind;
public sealed record SuperExpr : ExprKind;
public sealed record ParentExpr : ExprKind;
public sealed record VirtualParentExpr : ExprKind;
public sealed record Member(Expr Object, string Name) : ExprKind;
public sealed record Call(Expr Callee, List<Expr?> Args) : ExprKind;
public sealed record Index(Expr Array, Expr Idx) : ExprKind;
public sealed record Unary(string Op, Expr Operand) : ExprKind;
public sealed record Binary(string Op, Expr Left, Expr Right) : ExprKind;
public sealed record Assign(string Op, Expr Target, Expr Value) : ExprKind;
public sealed record Ternary(Expr Cond, Expr Then, Expr Else) : ExprKind;
public sealed record Cast(TypeRef Type, Expr Operand) : ExprKind;
public sealed record New(TypeRef Type, Expr? Owner) : ExprKind;
public sealed record Sequence(List<Expr> Values) : ExprKind;
public sealed record Postfix(string Op, Expr Operand) : ExprKind;
