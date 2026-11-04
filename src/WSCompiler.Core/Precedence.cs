namespace WSCompiler.Core.Scripts;

// Controls parentheses when writing decompiled expressions.
internal static class Precedence
{
    public const int Assign = 10, Or = 20, And = 30, BitOr = 43, BitXor = 44, BitAnd = 45, Equality = 50,
        Relational = 60, Additive = 70, Multiplicative = 80, Unary = 90, Primary = 100;
}
