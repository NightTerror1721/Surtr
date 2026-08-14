#nullable enable

using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System;
using System.Globalization;

namespace Surtr.Run
{
    /// <summary>
    /// Turns the plain-text arguments a shell hands over into <see cref="SurtrValue"/>s a call site
    /// can use.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A CLI argument is text with no declared type, so the only honest source of a parameter's
    /// type is the metadata the invoked method already carries - there is no overload resolution
    /// here (`Language-Syntax.md` §3.5 is the compiler's), only "does this text fit the one
    /// parameter it has to fit".
    /// </para>
    /// <para>
    /// Only what plain text can unambiguously become is supported: the four primitives, a string,
    /// and the literal <c>null</c> for anything that can hold one. An array, a dict, a tuple, a
    /// closure or an object instance has no text form, so a parameter of one of those types is
    /// reported rather than guessed at - the same choice §5.6 makes about an unspecified compound
    /// assignment through an indexer: refusing is better than semantics nobody asked for.
    /// </para>
    /// </remarks>
    internal static class ArgumentBinding
    {
        /// <summary>What went wrong converting one argument.</summary>
        internal sealed class BindingException : Exception
        {
            public BindingException(string message) : base(message)
            {
            }
        }

        /// <summary>
        /// Converts one piece of text against a parameter's declared type.
        /// </summary>
        /// <exception cref="BindingException">The text does not fit, or the type has no text form.</exception>
        internal static SurtrValue Convert(SurtrRuntime runtime, SurtrTypeHandle parameterType, string name, string text)
        {
            var reference = parameterType.Reference;

            // The absent tag for a nullable primitive and a null reference for everything else are
            // both spelled the same way at the command line - there is one "nothing" a shell can
            // write, however the value ends up represented underneath.
            if (string.Equals(text, "null", StringComparison.Ordinal))
            {
                if (reference.IsNullablePrimitive)
                    return SurtrValue.CreateAbsent(reference.TypeCode);

                if (reference.TypeCode is not (SurtrValueTypeCode.Integer or SurtrValueTypeCode.Float
                    or SurtrValueTypeCode.Boolean or SurtrValueTypeCode.Character))
                {
                    return SurtrValue.Null;
                }

                throw new BindingException(
                    $"argument '{name}' is '{reference.ToDisplayString()}', which cannot hold null.");
            }

            switch (reference.TypeCode)
            {
                case SurtrValueTypeCode.Integer:
                    if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i))
                        return SurtrValue.CreateInt(i);

                    throw new BindingException($"argument '{name}' expects an int; '{text}' is not one.");

                case SurtrValueTypeCode.Float:
                    if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double f))
                        return SurtrValue.CreateFloat(f);

                    throw new BindingException($"argument '{name}' expects a float; '{text}' is not one.");

                case SurtrValueTypeCode.Boolean:
                    if (bool.TryParse(text, out bool b))
                        return SurtrValue.CreateBool(b);

                    throw new BindingException($"argument '{name}' expects a bool ('true' or 'false'); '{text}' is not one.");

                case SurtrValueTypeCode.Character:
                    if (text.Length == 1)
                        return SurtrValue.CreateChar(text[0]);

                    throw new BindingException(
                        $"argument '{name}' expects a single char; '{text}' has {text.Length} characters.");

                case SurtrValueTypeCode.String:
                    return SurtrValue.CreateReference(runtime.NewString(text).GetSurtrReference());

                default:
                    throw new BindingException(
                        $"argument '{name}' has type '{reference.ToDisplayString()}', which cannot be built from text; "
                            + "only int, float, bool, char, string and 'null' are supported from the command line.");
            }
        }
    }
}
