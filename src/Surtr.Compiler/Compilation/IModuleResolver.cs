#nullable enable

using System;
using System.Collections.Generic;

namespace Surtr.Compiler.Compilation
{
    /// <summary>
    /// Resolves a dotted module path (§2.1) to a parsed source module, or reports that the
    /// compilation already has it as built metadata. This is the seam that hides where a module's
    /// source comes from: a compilation may hold the module in memory (parsed from the project's
    /// own files), may load it lazily through an <see cref="ISourceProvider"/>, or may know it
    /// only as an already-compiled image through the metadata importer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The compiler used to answer "does this module exist" in two places with two dictionaries —
    /// the source modules keyed in <see cref="SurtrCompilation"/>, and the compiled modules keyed
    /// in the metadata importer — and the binder repeated that pair again. This interface is what
    /// those checks share: one place that knows whether a module is reachable, and how to get the
    /// parsed form of one that is source.
    /// </para>
    /// <para>
    /// <see cref="TryGetSourceModule"/> is what makes lazy loading possible: a module that is not
    /// yet in the compilation but is known to an <see cref="ISourceProvider"/> is parsed on demand
    /// and joined to the compilation's module set. Resolution is then just "give me the module for
    /// this path", regardless of whether it was discovered up front or on first use.
    /// </para>
    /// </remarks>
    public interface IModuleResolver
    {
        /// <summary>
        /// Whether a module of this path is reachable: as a source module (loaded or loadable
        /// through a provider) or as already-built metadata.
        /// </summary>
        bool KnowsModule(string modulePath);

        /// <summary>
        /// The parsed source module for a path, loading it through a provider on first use when it
        /// is not already in the compilation.
        /// </summary>
        /// <param name="modulePath">The dotted module path (§2.1).</param>
        /// <returns>The parsed module, or <see langword="null"/> when no provider supplies it.</returns>
        SurtrSourceModule? TryGetSourceModule(string modulePath);

        /// <summary>
        /// Every source module whose path sits strictly under <paramref name="prefix"/> — what
        /// lets a directory wildcard (§2.1, Fase 9) reach a submodule. Source modules only, the
        /// same set the dependency graph reasons about; an already-compiled image has no directory
        /// index for this to walk.
        /// </summary>
        IEnumerable<string> ModulesUnderPrefix(string prefix);
    }
}