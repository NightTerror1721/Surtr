#nullable enable

namespace Surtr.Interop
{
    /// <summary>
    /// Maps a C# <c>op_*</c> method to the Surtr operator name it corresponds to, or reports that it
    /// has no Surtr equivalent. The Surtr names follow <c>OperatorNames</c> in the compiler: an
    /// operator is declared under <c>op_</c> + its symbol (<c>op_+</c>, <c>op_==</c>, <c>op_[]</c>,
    /// <c>op_-u</c> for unary minus, <c>op_as$&lt;descriptor&gt;</c> for a conversion). Shared by the
    /// reflection scanner; the source generator mirrors it at compile time.
    /// </summary>
    public static class SurtrOperatorMapper
    {
        /// <summary>
        /// The Surtr method name for a C# operator, or <see langword="null"/> when the operator has no
        /// Surtr equivalent (it should be skipped with a warning).
        /// </summary>
        /// <param name="csharpOperatorName">The CLR method name, e.g. <c>op_Addition</c>.</param>
        /// <param name="parameterCount">Distinguishes unary from binary <c>-</c>.</param>
        /// <param name="returnDescriptor">The target descriptor, only used by <c>op_Explicit</c>.</param>
        public static string? Map(string csharpOperatorName, int parameterCount, string? returnDescriptor)
        {
            switch (csharpOperatorName)
            {
                case "op_Addition": return "op_+";
                case "op_Subtraction": return parameterCount == 1 ? "op_-u" : "op_-";
                case "op_Multiply": return "op_*";
                case "op_Division": return "op_/";
                case "op_Modulus": return "op_%";
                case "op_BitwiseAnd": return "op_&";
                case "op_BitwiseOr": return "op_|";
                case "op_ExclusiveOr": return "op_^";
                case "op_LeftShift": return "op_<<";
                case "op_RightShift": return "op_>>";
                case "op_UnsignedRightShift": return "op_>>>";
                case "op_UnaryNegation": return "op_-u";
                case "op_LogicalNot": return "op_!";
                case "op_OnesComplement": return "op_~";
                case "op_Increment": return "op_++";
                case "op_Decrement": return "op_--";
                case "op_Equality": return "op_==";
                case "op_Explicit": return "op_as$" + returnDescriptor;
                default: return null;
            }
        }

        /// <summary>Whether a C# method name is a user-defined operator.</summary>
        public static bool IsOperator(string methodName)
            => methodName.Length > 3 && methodName.StartsWith("op_", System.StringComparison.Ordinal);
    }
}
