namespace WSCompiler.Core.Scripts.Standalone;

public sealed record CompileProblem( string? File, int Line, string Text );
