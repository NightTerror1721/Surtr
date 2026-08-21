#nullable enable

using System.Collections.Generic;

namespace Surtr.Compiler.Syntax.Ast
{
    /// <summary>Base of every type as written in source.</summary>
    /// <remarks>
    /// These are purely syntactic. Nothing here is resolved: <c>int</c> and <c>MyClass</c> are both
    /// a <see cref="NamedTypeSyntax"/> at this stage, because §1.1 of
    /// <c>docs/Language-Syntax.md</c> makes type names ordinary identifiers resolved in the type
    /// namespace rather than keywords. Turning one of these into a <c>SurtrClassReference</c>
    /// descriptor is the binder's job.
    /// </remarks>
    public abstract class TypeSyntax : SyntaxNode
    {
        /// <summary>Initializes the node with the position it starts at.</summary>
        /// <param name="span">The source the type covers.</param>
        protected TypeSyntax(SourceSpan span) : base(span)
        {
        }
    }

    /// <summary>A named type, possibly qualified and possibly generic: <c>int</c>, <c>Entity.Handle</c>, <c>IntMap&lt;V&gt;</c>.</summary>
    /// <remarks>
    /// The name is kept as a path because §2.6 makes <c>.</c> the only qualifier at every level —
    /// module, nested type, enum case — so <c>Ogame.core.Entity.Handle</c> arrives as four segments
    /// and only the binder can say where the module ends and the type begins.
    /// </remarks>
    public sealed class NamedTypeSyntax : TypeSyntax
    {
        /// <summary>The dotted name, split into its segments.</summary>
        public IReadOnlyList<string> Path { get; }

        /// <summary>The type arguments, empty when the type is not generic.</summary>
        public IReadOnlyList<TypeSyntax> TypeArguments { get; }

        /// <summary>Initializes a named type.</summary>
        /// <param name="span">The source the type covers.</param>
        /// <param name="path">The dotted name's segments.</param>
        /// <param name="typeArguments">The type arguments, or an empty list.</param>
        public NamedTypeSyntax(SourceSpan span, IReadOnlyList<string> path, IReadOnlyList<TypeSyntax> typeArguments)
            : base(span)
        {
            Path = path;
            TypeArguments = typeArguments;
        }
    }

    /// <summary>
    /// A type argument slot that does not name a type, written as an empty position between commas:
    /// <c>Box&lt;&gt;</c> (one), <c>Box&lt;,&gt;</c> (two), <c>Box&lt;,,&gt;</c> (three). It only
    /// ever appears in a <see cref="GenericNameExpressionSyntax"/> naming an open generic for a
    /// static member access — the arity it occupies is what selects the declaration, and the member
    /// is shared by every construction of it.
    /// </summary>
    public sealed class WildcardTypeSyntax : TypeSyntax
    {
        /// <summary>Initializes a wildcard type argument.</summary>
        /// <param name="span">The source the slot covers.</param>
        public WildcardTypeSyntax(SourceSpan span) : base(span)
        {
        }
    }

    /// <summary>An array type, written with the <c>[]</c> suffix: <c>int[]</c>, <c>string[][]</c>.</summary>
    public sealed class ArrayTypeSyntax : TypeSyntax
    {
        /// <summary>The element type.</summary>
        public TypeSyntax ElementType { get; }

        /// <summary>Initializes an array type.</summary>
        /// <param name="span">The source the type covers.</param>
        /// <param name="elementType">The element type.</param>
        public ArrayTypeSyntax(SourceSpan span, TypeSyntax elementType) : base(span)
        {
            ElementType = elementType;
        }
    }

    /// <summary>A dictionary type, written as a brace-delimited key/value pair: <c>{int: string}</c>.</summary>
    public sealed class DictTypeSyntax : TypeSyntax
    {
        /// <summary>The key type.</summary>
        public TypeSyntax KeyType { get; }

        /// <summary>The value type.</summary>
        public TypeSyntax ValueType { get; }

        /// <summary>Initializes a dictionary type.</summary>
        /// <param name="span">The source the type covers.</param>
        /// <param name="keyType">The key type.</param>
        /// <param name="valueType">The value type.</param>
        public DictTypeSyntax(SourceSpan span, TypeSyntax keyType, TypeSyntax valueType) : base(span)
        {
            KeyType = keyType;
            ValueType = valueType;
        }
    }

    /// <summary>A tuple type, reusing the shape of a tuple literal: <c>(int, string)</c>.</summary>
    public sealed class TupleTypeSyntax : TypeSyntax
    {
        /// <summary>The element types, in order.</summary>
        public IReadOnlyList<TypeSyntax> ElementTypes { get; }

        /// <summary>Initializes a tuple type.</summary>
        /// <param name="span">The source the type covers.</param>
        /// <param name="elementTypes">The element types, in order.</param>
        public TupleTypeSyntax(SourceSpan span, IReadOnlyList<TypeSyntax> elementTypes) : base(span)
        {
            ElementTypes = elementTypes;
        }
    }

    /// <summary>A closure type: <c>(int, int) -&gt; float</c>.</summary>
    /// <remarks>
    /// Written with <c>-&gt;</c> rather than the <c>=&gt;</c> of a lambda value, which is what
    /// keeps "the type of a function" visually distinct from "a function" (§5.3).
    /// </remarks>
    public sealed class ClosureTypeSyntax : TypeSyntax
    {
        /// <summary>The parameter types, in order.</summary>
        public IReadOnlyList<TypeSyntax> ParameterTypes { get; }

        /// <summary>The return type; <c>void</c> is legal here and nowhere else.</summary>
        public TypeSyntax ReturnType { get; }

        /// <summary>Initializes a closure type.</summary>
        /// <param name="span">The source the type covers.</param>
        /// <param name="parameterTypes">The parameter types, in order.</param>
        /// <param name="returnType">The return type.</param>
        public ClosureTypeSyntax(SourceSpan span, IReadOnlyList<TypeSyntax> parameterTypes, TypeSyntax returnType)
            : base(span)
        {
            ParameterTypes = parameterTypes;
            ReturnType = returnType;
        }
    }

    /// <summary>A nullable type, written with the <c>?</c> suffix: <c>string?</c>, <c>int?</c>.</summary>
    /// <remarks>
    /// §5.1 applies <c>?</c> uniformly to references and primitives alike, so this wraps any type
    /// rather than only a reference one.
    /// </remarks>
    public sealed class NullableTypeSyntax : TypeSyntax
    {
        /// <summary>The type made nullable.</summary>
        public TypeSyntax ElementType { get; }

        /// <summary>Initializes a nullable type.</summary>
        /// <param name="span">The source the type covers.</param>
        /// <param name="elementType">The type being made nullable.</param>
        public NullableTypeSyntax(SourceSpan span, TypeSyntax elementType) : base(span)
        {
            ElementType = elementType;
        }
    }
}
