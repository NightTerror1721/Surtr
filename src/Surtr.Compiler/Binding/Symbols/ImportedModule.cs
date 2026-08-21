#nullable enable

using System;
using System.Collections.Generic;

namespace Surtr.Compiler.Binding.Symbols
{
    /// <summary>
    /// One module a file brings into scope, together with which of its members it brought.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A wildcard import (<c>import X.*;</c>) or a whole-module import (<c>import module X;</c>,
    /// §2.1) brings a module with no filter: every module-level member is reachable unqualified,
    /// the way §2.5 makes a module a container of members. A named or selective import that
    /// reached a module-level member (<c>import X.fun;</c>, <c>import X.{fun, var}</c>) brings the
    /// module with <see cref="Only"/> naming exactly those members, so nothing else from it leaks
    /// into bare-name resolution.
    /// </para>
    /// <para>
    /// The same shape describes a re-export (<c>export import ...</c>): a module re-exported whole
    /// has no filter, a module re-exported through a named/selective member import has <see cref="Only"/>.
    /// </para>
    /// </remarks>
    public readonly struct ImportedModule
    {
        /// <summary>The module brought into scope.</summary>
        public ModuleSymbol Module { get; }

        /// <summary>
        /// The module-level member names brought from it, or <see langword="null"/> for every one.
        /// </summary>
        public IReadOnlyList<string>? Only { get; }

        /// <summary>Creates an import of a whole module (no member filter).</summary>
        public ImportedModule(ModuleSymbol module)
        {
            Module = module ?? throw new ArgumentNullException(nameof(module));
            Only = null;
        }

        /// <summary>Creates an import of selected members of a module.</summary>
        public ImportedModule(ModuleSymbol module, IReadOnlyList<string> only)
        {
            Module = module ?? throw new ArgumentNullException(nameof(module));
            Only = only ?? throw new ArgumentNullException(nameof(only));
        }
    }
}