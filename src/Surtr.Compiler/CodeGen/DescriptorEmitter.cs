#nullable enable

using Surtr.Compiler.Binding.Symbols;
using Surtr.Runtime.Classes;
using System;
using System.Collections.Generic;
using System.Text;

namespace Surtr.Compiler.CodeGen
{
    /// <summary>
    /// Turns a bound <see cref="TypeSymbol"/> into the <see cref="SurtrClassReference"/> descriptor
    /// the runtime stores. This is the compiler's only gate to the runtime's type encoding.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It lives in <c>CodeGen</c> rather than in <c>Binding</c> on purpose. A descriptor is
    /// canonical because it throws information away — reference nullability, which alias was
    /// written, that a <c>value class</c> was involved, and (for a type parameter) which parameter
    /// it was. Every one of those is something the type checker needs, so anything in
    /// <c>Binding</c> reaching for a descriptor is a mistake this layering makes visible.
    /// </para>
    /// <para>
    /// Three things are erased here and nowhere earlier:
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// <description>
    /// <b>Reference nullability.</b> A reference is its payload and null is already representable,
    /// so <c>Foo?</c> and <c>Foo</c> are one descriptor. Only a nullable <em>primitive</em> gets
    /// <c>?</c>, because there the null state is a distinct value representation.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// <b>Value classes.</b> A <c>value class</c> is erased to the field it wraps wherever its type
    /// is statically known (§2.9). Where it must become a reference — an erased slot, an
    /// <c>unknown</c>, an interface-typed slot — the boxed form is a real class, which is what
    /// <see cref="EmitBoxedForm"/> names.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// <b>A generic method's type parameters.</b> <c>G&lt;n&gt;</c> means "the declaring
    /// <em>type</em>'s n-th parameter"; nothing indexes into a method's own list, so those emit as
    /// the plain erased symbol.
    /// </description>
    /// </item>
    /// </list>
    /// <para>
    /// Aliases never arrive here: they resolve to their target during binding and no symbol
    /// survives to be emitted.
    /// </para>
    /// </remarks>
    public sealed class DescriptorEmitter
    {
        // Types are interned, so a descriptor computed once is valid for every later mention of
        // the same type. Emission walks the same handful of types repeatedly.
        private readonly Dictionary<TypeSymbol, SurtrClassReference> _cache =
            new Dictionary<TypeSymbol, SurtrClassReference>();

        // The boxed form of a value class, likewise stable for the whole emission.
        private readonly Dictionary<NamedTypeSymbol, SurtrClassReference> _boxedForms =
            new Dictionary<NamedTypeSymbol, SurtrClassReference>();

        /// <summary>The descriptor for a type, as the runtime will store it.</summary>
        /// <exception cref="InvalidOperationException">
        /// The type is an error type, or a <c>value class</c> whose wrapped field was never bound.
        /// Either means emission started on a compilation that should have stopped at its
        /// diagnostics.
        /// </exception>
        public SurtrClassReference Emit(TypeSymbol type)
        {
            if (_cache.TryGetValue(type, out var cached))
                return cached;

            var builder = new StringBuilder();
            Append(builder, type);

            var reference = SurtrClassReference.FromDescriptor(builder.ToString());
            _cache.Add(type, reference);
            return reference;
        }

        /// <summary>The descriptor for a type, as a string.</summary>
        public string EmitDescriptor(TypeSymbol type) => Emit(type).Descriptor;

        /// <summary>
        /// The name a method is declared under in the method table.
        /// </summary>
        /// <remarks>
        /// This is <see cref="MethodSymbol.Name"/> for everything except a conversion. An
        /// <c>operator as</c> is overloaded on its target type, which a signature key cannot see
        /// because it is name plus parameters and excludes the return — so the target's descriptor
        /// joins the name here, and the runtime needs to know nothing about conversions at all.
        /// </remarks>
        public string EmitMethodName(MethodSymbol method)
        {
            if (!method.IsConversion)
                return method.Name;

            return method.Name + OperatorNames.ConversionTargetSeparator + EmitDescriptor(method.ReturnType);
        }

        /// <summary>
        /// The descriptor naming a <c>value class</c> as a real class rather than as the field it
        /// wraps — what a boxed one is an instance of.
        /// </summary>
        /// <exception cref="ArgumentException"><paramref name="valueClass"/> is not a value class.</exception>
        public SurtrClassReference EmitBoxedForm(NamedTypeSymbol valueClass)
        {
            if (valueClass.TypeKind != TypeSymbolKind.ValueClass)
                throw new ArgumentException("Only a value class has a boxed form distinct from its descriptor.", nameof(valueClass));

            if (_boxedForms.TryGetValue(valueClass, out var cached))
                return cached;

            var builder = new StringBuilder();
            AppendNamed(builder, valueClass, SurtrClassReference.SymbolObject);
            return _boxedForms[valueClass] = SurtrClassReference.FromDescriptor(builder.ToString());
        }

        private void Append(StringBuilder builder, TypeSymbol type)
        {
            switch (type.TypeKind)
            {
                case TypeSymbolKind.Array:
                    builder.Append(SurtrClassReference.SymbolArray);
                    Append(builder, ((ArrayTypeSymbol)type).ElementType);
                    return;

                case TypeSymbolKind.Dictionary:
                {
                    var dictionary = (DictionaryTypeSymbol)type;
                    builder.Append(SurtrClassReference.SymbolDictionary);
                    Append(builder, dictionary.KeyType);
                    Append(builder, dictionary.ValueType);
                    return;
                }

                case TypeSymbolKind.Tuple:
                {
                    var tuple = (TupleTypeSymbol)type;
                    builder.Append(SurtrClassReference.SymbolTuple).Append(SurtrClassReference.ListOpen);
                    for (int i = 0; i < tuple.ElementTypes.Count; i++)
                        Append(builder, tuple.ElementTypes[i]);

                    builder.Append(SurtrClassReference.ListClose);
                    return;
                }

                case TypeSymbolKind.Closure:
                {
                    var closure = (ClosureTypeSymbol)type;
                    builder.Append(SurtrClassReference.SymbolClosure).Append(SurtrClassReference.ListOpen);
                    for (int i = 0; i < closure.ParameterTypes.Count; i++)
                        Append(builder, closure.ParameterTypes[i]);

                    builder.Append(SurtrClassReference.ListClose);
                    Append(builder, closure.ReturnType);
                    return;
                }

                case TypeSymbolKind.TypeParameter:
                {
                    var parameter = (TypeParameterSymbol)type;

                    // A method's parameter keeps its position under its own symbol: `H0` tells the
                    // importer which parameter it was, which is what lets a call site's inference
                    // and constraints survive the image. Signature keys still erase it, so no slot
                    // or dispatch changes.
                    if (parameter.IsMethodTypeParameter)
                    {
                        if ((uint)parameter.Ordinal > 9)
                        {
                            throw new InvalidOperationException(
                                $"'{parameter.ContainingSymbol?.Name}' declares more than ten type parameters, " +
                                "which the single-digit 'H' descriptor form cannot encode.");
                        }

                        builder.Append(SurtrClassReference.SymbolMethodGenericParameter).Append((char)('0' + parameter.Ordinal));
                        return;
                    }

                    if ((uint)parameter.Ordinal > 9)
                    {
                        throw new InvalidOperationException(
                            $"'{parameter.ContainingSymbol?.Name}' declares more than ten type parameters, " +
                            "which the single-digit 'G' descriptor form cannot encode.");
                    }

                    builder.Append(SurtrClassReference.SymbolGenericParameter).Append((char)('0' + parameter.Ordinal));
                    return;
                }

                case TypeSymbolKind.Error:
                    throw new InvalidOperationException(
                        $"The error type '{type.ToDisplayString()}' reached emission; the compilation should have stopped at its diagnostics.");

                case TypeSymbolKind.ValueClass:
                {
                    // A multi-field value class is a value type in its own right: it names its
                    // class like any other, and the flattened width travels through the linked
                    // metadata rather than through the descriptor.
                    var named = (NamedTypeSymbol)type;
                    if (ValueTypeLayout.IsMultiField(named))
                    {
                        AppendNamed(builder, named, SurtrClassReference.SymbolObject);
                        return;
                    }

                    // Erased to the field it wraps, and nullability rides along: `EntityId?` over an
                    // `int` field is exactly `int?`.
                    var underlying = UnwrapValueClass(named);
                    Append(builder, underlying.WithNullability(underlying.IsNullable || type.IsNullable));
                    return;
                }

                case TypeSymbolKind.Native:
                    AppendNamed(builder, (NamedTypeSymbol)type, SurtrClassReference.SymbolNative);
                    return;

                default:
                    AppendNamed(builder, (NamedTypeSymbol)type, SurtrClassReference.SymbolObject);
                    return;
            }
        }

        private void AppendNamed(StringBuilder builder, NamedTypeSymbol type, char symbol)
        {
            switch (type.SpecialType)
            {
                case SpecialType.Int: AppendPrimitive(builder, type, SurtrClassReference.SymbolInteger); return;
                case SpecialType.Float: AppendPrimitive(builder, type, SurtrClassReference.SymbolFloat); return;
                case SpecialType.Bool: AppendPrimitive(builder, type, SurtrClassReference.SymbolBoolean); return;
                case SpecialType.Char: AppendPrimitive(builder, type, SurtrClassReference.SymbolCharacter); return;

                // Reference built-ins: `?` adds nothing, since a reference is its payload and null
                // is already one of the values it can hold.
                case SpecialType.String: builder.Append(SurtrClassReference.SymbolString); return;
                case SpecialType.Range: builder.Append(SurtrClassReference.SymbolRange); return;
                case SpecialType.Void: builder.Append(SurtrClassReference.SymbolVoid); return;

                // `never` produces a value that never arrives: the call site must balance a result
                // (a `return fail()` reads one), but the body always throws before producing it.
                // Erased is the one reference representation a value of any type can stand in for.
                case SpecialType.Never: builder.Append(SurtrClassReference.SymbolErased); return;

                case SpecialType.Unknown: builder.Append(SurtrClassReference.SymbolErased); return;
            }

            builder.Append(symbol).Append(type.FullMetadataName).Append(SurtrClassReference.NameTerminator);

            // Arity is already in the name, so the argument list needs neither brackets nor a
            // count - the reader knows how many descriptors to expect before it reaches them.
            var arguments = type.TypeArguments;
            if (arguments.Count > 0)
            {
                for (int i = 0; i < arguments.Count; i++)
                    Append(builder, arguments[i]);

                return;
            }

            // An open declaration used as a type stands for itself: `Box` inside `Box<T>` is
            // `Box<T>`, so its own parameters are the arguments.
            var parameters = type.TypeParameters;
            for (int i = 0; i < parameters.Count; i++)
                Append(builder, parameters[i]);
        }

        private static void AppendPrimitive(StringBuilder builder, TypeSymbol type, char symbol)
        {
            if (type.IsNullable)
                builder.Append(SurtrClassReference.SymbolNullable);

            builder.Append(symbol);
        }

        private static TypeSymbol UnwrapValueClass(NamedTypeSymbol valueClass)
        {
            var current = valueClass;

            // A value class may wrap another one, so this walks down to whatever is left. The
            // binder rejects a cycle, but the guard costs nothing and turns a compiler bug into a
            // message instead of a hang.
            for (int depth = 0; depth < 64; depth++)
            {
                var underlying = current.UnderlyingType
                    ?? throw new InvalidOperationException(
                        $"Value class '{current.ToDisplayString()}' has no wrapped field bound, so it cannot be emitted.");

                if (underlying is NamedTypeSymbol named && named.TypeKind == TypeSymbolKind.ValueClass)
                {
                    current = named;
                    continue;
                }

                return underlying;
            }

            throw new InvalidOperationException(
                $"Value class '{valueClass.ToDisplayString()}' wraps itself through more than 64 levels.");
        }
    }
}
