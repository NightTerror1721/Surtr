#nullable enable

using System;
using System.Collections.Generic;

namespace Surtr.Compiler.Compilation
{
    /// <summary>
    /// Which modules depend on which, and the order they can be loaded in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Order matters because static initializers run <em>eagerly</em> at module load, classes
    /// before the module, in declaration order — so a module has to be loaded after everything it
    /// reaches. A cycle has no such order, which is why it is rejected outright rather than broken
    /// arbitrarily.
    /// </para>
    /// <para>
    /// The graph is filled in over time rather than computed once. Imports (§2.1) declare most
    /// edges and are known as soon as a file is parsed, but a name may also be written fully
    /// qualified without importing it, and that edge only appears once the binder resolves the
    /// name. So this accumulates, and the check can be run again after binding adds to it.
    /// </para>
    /// </remarks>
    public sealed class ModuleDependencyGraph
    {
        private readonly Dictionary<string, HashSet<string>> _edges =
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        /// <summary>Every module the graph knows about, whether or not it has edges.</summary>
        public IReadOnlyCollection<string> Modules => _edges.Keys;

        /// <summary>Records a module, so it appears in an ordering even with no dependencies.</summary>
        public void AddModule(string modulePath)
        {
            if (!_edges.ContainsKey(modulePath))
                _edges.Add(modulePath, new HashSet<string>(StringComparer.Ordinal));
        }

        /// <summary>
        /// Records that <paramref name="dependent"/> reaches <paramref name="dependency"/>, and so
        /// must be loaded after it.
        /// </summary>
        /// <remarks>
        /// A module depending on itself is not an edge: a module is one file (§2.1), so a
        /// reference within it is ordinary and says nothing about load order.
        /// </remarks>
        public void AddDependency(string dependent, string dependency)
        {
            AddModule(dependent);
            AddModule(dependency);

            if (!string.Equals(dependent, dependency, StringComparison.Ordinal))
                _edges[dependent].Add(dependency);
        }

        /// <summary>What <paramref name="modulePath"/> depends on directly.</summary>
        public IReadOnlyCollection<string> DependenciesOf(string modulePath)
            => _edges.TryGetValue(modulePath, out var dependencies)
                ? dependencies
                : (IReadOnlyCollection<string>)Array.Empty<string>();

        /// <summary>
        /// Every module that depends directly on <paramref name="modulePath"/> - the reverse of
        /// <see cref="DependenciesOf"/>.
        /// </summary>
        /// <remarks>
        /// Nothing here keeps a reverse index, so this walks every edge on each call - fine for the
        /// tooling this graph already serves (compile-time, once per build), and for what it exists
        /// for now: incremental invalidation asks "who breaks if this module's signature changes",
        /// which is exactly this direction, and only needs asking when a module's content or its
        /// own dependents just changed.
        /// </remarks>
        public IReadOnlyCollection<string> Dependents(string modulePath)
        {
            var result = new List<string>();

            foreach (var pair in _edges)
            {
                if (pair.Value.Contains(modulePath))
                    result.Add(pair.Key);
            }

            return result;
        }

        /// <summary>
        /// Orders the modules so every one comes after everything it depends on.
        /// </summary>
        /// <param name="order">The load order, or empty when a cycle was found.</param>
        /// <param name="cycle">
        /// The modules forming a cycle, in the order they reach one another, with the first
        /// repeated at the end so the loop reads as a loop. Empty when there is none.
        /// </param>
        /// <returns><see langword="false"/> if the graph has a cycle.</returns>
        public bool TryGetLoadOrder(out IReadOnlyList<string> order, out IReadOnlyList<string> cycle)
        {
            var result = new List<string>(_edges.Count);
            var state = new Dictionary<string, VisitState>(_edges.Count, StringComparer.Ordinal);
            var path = new List<string>();

            // Sorted so an ordering is reproducible across runs: two modules with no dependency
            // between them have no natural order, and a build that reorders them for no reason
            // produces images that differ for no reason.
            var roots = new List<string>(_edges.Keys);
            roots.Sort(StringComparer.Ordinal);

            foreach (string module in roots)
            {
                if (!Visit(module, state, path, result, out cycle))
                {
                    order = Array.Empty<string>();
                    return false;
                }
            }

            order = result;
            cycle = Array.Empty<string>();
            return true;
        }

        private bool Visit(
            string module,
            Dictionary<string, VisitState> state,
            List<string> path,
            List<string> result,
            out IReadOnlyList<string> cycle)
        {
            if (state.TryGetValue(module, out var visited))
            {
                if (visited == VisitState.Done)
                {
                    cycle = Array.Empty<string>();
                    return true;
                }

                // Still on the stack: we have walked back into something we are inside of.
                int start = path.LastIndexOf(module);
                var loop = new List<string>();
                for (int i = start < 0 ? 0 : start; i < path.Count; i++)
                    loop.Add(path[i]);

                loop.Add(module);
                cycle = loop;
                return false;
            }

            state[module] = VisitState.Visiting;
            path.Add(module);

            var dependencies = new List<string>(_edges[module]);
            dependencies.Sort(StringComparer.Ordinal);

            foreach (string dependency in dependencies)
            {
                if (!Visit(dependency, state, path, result, out cycle))
                    return false;
            }

            path.RemoveAt(path.Count - 1);
            state[module] = VisitState.Done;
            result.Add(module);

            cycle = Array.Empty<string>();
            return true;
        }

        private enum VisitState
        {
            Visiting,
            Done,
        }
    }
}
