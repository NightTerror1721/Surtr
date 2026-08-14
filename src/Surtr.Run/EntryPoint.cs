#nullable enable

using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Surtr.Run
{
    /// <summary>
    /// Finds a module-level function by name, binds command-line text to its parameters, and
    /// describes what it returns.
    /// </summary>
    /// <remarks>
    /// <c>Language-Syntax.md</c> §2.5 is explicit that Surtr has no <c>main</c>: a host loads a
    /// module and invokes whatever it wants, by name, through <c>SurtrRuntime.Invoke</c>. A CLI
    /// runner is exactly that host, so naming the function to call is something every invocation has
    /// to say - there is no convention to fall back on. Only a <em>module-level</em> function is
    /// reachable this way; calling a method on a class would also need an instance to call it on,
    /// which nothing at the command line can construct.
    /// </remarks>
    internal static class EntryPoint
    {
        /// <summary>What went wrong finding or calling a function.</summary>
        internal sealed class InvocationException : Exception
        {
            public InvocationException(string message) : base(message)
            {
            }
        }

        /// <summary>
        /// Picks the one overload of <paramref name="function"/> that <paramref name="argCount"/>
        /// arguments can call.
        /// </summary>
        /// <remarks>
        /// Deliberately not §3.5's overload resolution: this binds no parameter default, since doing
        /// that honestly needs a folded constant per parameter and there is no compile step here to
        /// have produced one. An overload with a default still matches - the caller just has to
        /// write every value out, defaulted ones included - and only the count of what was written
        /// decides which overload that is.
        /// </remarks>
        /// <exception cref="InvocationException">No overload fits, or more than one does.</exception>
        internal static SurtrMethodInfo Resolve(SurtrModule module, string function, int argCount)
        {
            if (!module.TryGetMethods(function, out var overloads))
            {
                throw new InvocationException(
                    $"'{module.Path}' declares no module-level function called '{function}'.");
            }

            var candidates = new List<SurtrMethodInfo>();
            foreach (var method in overloads)
            {
                bool fits = method.HasVarargs
                    ? argCount >= method.ParameterCount - 1
                    : argCount == method.ParameterCount;

                if (fits)
                    candidates.Add(method);
            }

            if (candidates.Count == 0)
            {
                throw new InvocationException(
                    $"'{function}' takes {argCount} argument(s) in no declared overload; declared: "
                        + string.Join(", ", overloads.Select(Shape)) + ".");
            }

            if (candidates.Count > 1)
            {
                throw new InvocationException(
                    $"'{function}' is ambiguous with {argCount} argument(s): "
                        + string.Join(", ", candidates.Select(Shape)) + ".");
            }

            return candidates[0];
        }

        /// <summary>
        /// Converts <paramref name="arguments"/> against <paramref name="method"/>'s declared
        /// parameters, packing any surplus into its varargs array (§3.5).
        /// </summary>
        /// <exception cref="ArgumentBinding.BindingException">A piece of text does not fit its parameter.</exception>
        internal static SurtrValue[] Bind(SurtrRuntime runtime, SurtrMethodInfo method, IReadOnlyList<string> arguments)
        {
            var parameters = method.Parameters.ToArray();
            var bound = new SurtrValue[parameters.Length];

            int fixedCount = method.HasVarargs ? parameters.Length - 1 : parameters.Length;

            for (int i = 0; i < fixedCount; i++)
                bound[i] = ArgumentBinding.Convert(runtime, parameters[i].ParameterType, parameters[i].Name, arguments[i]);

            if (!method.HasVarargs)
                return bound;

            var element = parameters[^1];
            var array = runtime.NewArray(SurtrClassReference.Array(element.ParameterType.Reference), arguments.Count - fixedCount);

            for (int i = fixedCount; i < arguments.Count; i++)
                array.Add(ArgumentBinding.Convert(runtime, element.ParameterType, element.Name, arguments[i]));

            bound[^1] = SurtrValue.CreateReference(array.GetSurtrReference());
            return bound;
        }

        /// <summary>
        /// Describes what a call returned, or <see langword="null"/> for a <c>void</c> method - the
        /// one case with nothing to print.
        /// </summary>
        internal static string? Describe(SurtrRuntime runtime, SurtrMethodInfo method, SurtrValue value)
            => method.ReturnType.Reference.TypeCode == SurtrValueTypeCode.Void ? null : Describe(runtime, value);

        /// <summary>
        /// Renders a value the way string interpolation would: primitives directly, everything else
        /// through its own <c>toString()</c> - the method every built-in already declares
        /// (`CLAUDE.md`'s "the built-in classes"), and a plain class picks up only if it wrote one
        /// itself, since there is no root type for it to inherit one from.
        /// </summary>
        private static string Describe(SurtrRuntime runtime, SurtrValue value)
        {
            if (value.IsInt) return value.AsInt.ToString(CultureInfo.InvariantCulture);
            if (value.IsFloat) return value.AsFloat.ToString(CultureInfo.InvariantCulture);
            if (value.IsBool) return value.AsBool ? "true" : "false";
            if (value.IsChar) return value.AsChar.ToString();
            if (value.IsAbsent) return "null";

            if (!value.IsReference)
                return value.Raw.ToString(CultureInfo.InvariantCulture);

            if (value.IsNullReference)
                return "null";

            if (runtime.Resolve<SurtrString>(value) is SurtrString text)
                return text.Text;

            var instance = runtime.Resolve<SurtrObject>(value);
            if (instance is null)
                return "<unresolved reference>";

            if (TryFindToString(instance.GetClass(), out var toString))
                return Describe(runtime, runtime.Invoke(toString, value));

            return $"<{instance.GetClass().SelfReference.ToDisplayString()}>";
        }

        private static bool TryFindToString(SurtrClass @class, out SurtrMethodInfo method)
        {
            if (@class.TryGetMethods("toString", out var overloads))
            {
                foreach (var candidate in overloads)
                {
                    if (!candidate.IsStatic && candidate.ParameterCount == 0)
                    {
                        method = candidate;
                        return true;
                    }
                }
            }

            method = null!;
            return false;
        }

        /// <summary>A function's shape, for a diagnostic or a listing: <c>name(int, string...)</c>.</summary>
        internal static string Shape(SurtrMethodInfo method)
        {
            var builder = new StringBuilder(method.Name).Append('(');
            var parameters = method.Parameters;

            for (int i = 0; i < parameters.Length; i++)
            {
                if (i > 0)
                    builder.Append(", ");

                builder.Append(parameters[i].ParameterType.Reference.ToDisplayString());

                if (parameters[i].IsVarargs)
                    builder.Append("...");
            }

            return builder.Append(')').ToString();
        }
    }
}
