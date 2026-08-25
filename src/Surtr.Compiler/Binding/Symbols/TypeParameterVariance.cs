#nullable enable

namespace Surtr.Compiler.Binding.Symbols
{
    /// <summary>The declaration-site variance a generic type parameter carries (§6).</summary>
    /// <remarks>
    /// <para>
    /// The annotation is written once, on the declaration — <c>interface IIterable&lt;out T&gt;</c>
    /// — and the compiler checks the positions the parameter appears in before accepting it. A
    /// construction then converts along the annotated direction: covariant means
    /// <c>IIterable&lt;Circle&gt;</c> is an <c>IIterable&lt;IShape&gt;</c>, contravariant means
    /// an <c>IComparer&lt;IShape&gt;</c> serves where an <c>IComparer&lt;Circle&gt;</c> is asked
    /// for, and invariant is every unannotated parameter, which is what all of them were before
    /// variance existed.
    /// </para>
    /// <para>
    /// Variance is a compile-time answer about subtyping and nothing else. Erasure boxes every
    /// argument into a reference slot either way, so no representation, descriptor or runtime
    /// question changes with it.
    /// </para>
    /// </remarks>
    public enum TypeParameterVariance
    {
        /// <summary>No annotation: only one exact argument satisfies the parameter.</summary>
        Invariant,

        /// <summary>Written <c>out T</c>: the parameter only produces values.</summary>
        Covariant,

        /// <summary>Written <c>in T</c>: the parameter only consumes values.</summary>
        Contravariant,
    }
}
