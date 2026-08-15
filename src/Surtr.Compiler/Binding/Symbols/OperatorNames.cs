#nullable enable

using Surtr.Compiler.Syntax;
using System;

namespace Surtr.Compiler.Binding.Symbols
{
    /// <summary>
    /// The names an overloaded operator (§5.6) is declared under in the method table.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An operator has to become an ordinary name, because a method is looked up by name plus
    /// parameters and nothing downstream knows what a `+` is. The name is the symbol itself behind
    /// <see cref="Prefix"/>, which is what makes it **unspellable**: an identifier is
    /// <c>letter|_</c> then <c>letterOrDigit|_</c>, so no user declaration can collide with
    /// <c>op_+</c> and no prefix has to be reserved to keep it that way. It also reads directly in
    /// a disassembly, where <c>op_Addition</c> would have to be translated back.
    /// </para>
    /// <para>
    /// The conversion form is the one that cannot be named from the token alone: <c>operator as</c>
    /// is overloaded on its <em>target</em> type, and a signature key is name plus parameters,
    /// deliberately excluding the return. The target therefore joins the name, but only at emit —
    /// see <c>DescriptorEmitter.EmitMethodName</c> — because naming it here would need a descriptor
    /// and the binder does not deal in those.
    /// </para>
    /// </remarks>
    public static class OperatorNames
    {
        /// <summary>What every operator's name starts with.</summary>
        public const string Prefix = "op_";

        /// <summary>The name every <c>operator as</c> is declared under, before its target is appended.</summary>
        public const string Conversion = Prefix + "as";

        /// <summary>Separates a conversion's name from the descriptor of the type it converts to.</summary>
        public const char ConversionTargetSeparator = '$';

        /// <summary>
        /// Marks the unary form of an operator whose symbol also has a binary one, which today is
        /// only <c>-</c>.
        /// </summary>
        /// <remarks>
        /// Cosmetic, not load-bearing: §5.6 tells the two forms apart by arity, and so does a
        /// signature key, since one takes a parameter and the other takes two. The suffix exists so
        /// a disassembly line says which without the reader counting parameters.
        /// </remarks>
        public const string UnarySuffix = "u";

        /// <summary>The name an overload of <paramref name="op"/> is declared under.</summary>
        /// <param name="op">
        /// The overloaded token. <see cref="TokenType.KeywordAs"/> means the conversion form and
        /// <see cref="TokenType.LeftBracket"/> the indexer, matching what the parser records.
        /// </param>
        /// <param name="parameterCount">
        /// How many parameters were declared, which is what separates unary <c>-</c> from binary.
        /// </param>
        /// <exception cref="ArgumentException"><paramref name="op"/> is not overloadable.</exception>
        public static string For(TokenType op, int parameterCount)
        {
            if (!TryGetSymbol(op, out string symbol))
                throw new ArgumentException($"'{op}' is not an overloadable operator.", nameof(op));

            if (op == TokenType.KeywordAs)
                return Conversion;

            // Only `-` carries both arities, so this stays one comparison rather than a table.
            if (op == TokenType.Minus && parameterCount == 1)
                return Prefix + symbol + UnarySuffix;

            return Prefix + symbol;
        }

        /// <summary>The source spelling of an overloadable operator.</summary>
        /// <returns><see langword="false"/> if <paramref name="op"/> cannot be overloaded.</returns>
        public static bool TryGetSymbol(TokenType op, out string symbol)
        {
            switch (op)
            {
                case TokenType.Plus: symbol = "+"; return true;
                case TokenType.Minus: symbol = "-"; return true;
                case TokenType.Star: symbol = "*"; return true;
                case TokenType.Slash: symbol = "/"; return true;
                case TokenType.Percent: symbol = "%"; return true;
                case TokenType.Ampersand: symbol = "&"; return true;
                case TokenType.Pipe: symbol = "|"; return true;
                case TokenType.Caret: symbol = "^"; return true;
                case TokenType.ShiftLeft: symbol = "<<"; return true;
                case TokenType.ShiftRight: symbol = ">>"; return true;
                case TokenType.UnsignedShiftRight: symbol = ">>>"; return true;
                case TokenType.LogicalNot: symbol = "!"; return true;
                case TokenType.Tilde: symbol = "~"; return true;
                case TokenType.Increment: symbol = "++"; return true;
                case TokenType.Decrement: symbol = "--"; return true;
                case TokenType.Equal: symbol = "=="; return true;
                case TokenType.Spaceship: symbol = "<=>"; return true;
                case TokenType.LeftBracket: symbol = "[]"; return true;
                case TokenType.KeywordAs: symbol = "as"; return true;

                default:
                    symbol = string.Empty;
                    return false;
            }
        }

        /// <summary>Whether a method name was produced by this class.</summary>
        public static bool IsOperatorName(string name)
            => name.Length > Prefix.Length && name.StartsWith(Prefix, StringComparison.Ordinal);
    }
}
