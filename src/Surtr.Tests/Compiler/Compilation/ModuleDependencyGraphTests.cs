#nullable enable

using Surtr.Compiler.Compilation;
using System.Collections.Generic;
using System.Linq;

namespace Surtr.Tests.Compiler.Compilation
{
    /// <summary>
    /// Covers the ordering static initializers depend on: they run eagerly at load, so a module has
    /// to come after everything it reaches, and a cycle has no such order.
    /// </summary>
    public sealed class ModuleDependencyGraphTests
    {
        private static int IndexOf(IReadOnlyList<string> order, string module) => order.ToList().IndexOf(module);

        [Fact]
        public void ADependencyComesBeforeWhatDependsOnIt()
        {
            var graph = new ModuleDependencyGraph();
            graph.AddDependency("app", "game.core");
            graph.AddDependency("game.core", "game.math");

            Assert.True(graph.TryGetLoadOrder(out var order, out _));

            Assert.True(IndexOf(order, "game.math") < IndexOf(order, "game.core"));
            Assert.True(IndexOf(order, "game.core") < IndexOf(order, "app"));
        }

        [Fact]
        public void ADiamondOrdersBothSidesBeforeTheJoin()
        {
            var graph = new ModuleDependencyGraph();
            graph.AddDependency("app", "left");
            graph.AddDependency("app", "right");
            graph.AddDependency("left", "base");
            graph.AddDependency("right", "base");

            Assert.True(graph.TryGetLoadOrder(out var order, out _));

            Assert.Equal(4, order.Count);
            Assert.Equal(0, IndexOf(order, "base"));
            Assert.Equal(3, IndexOf(order, "app"));
        }

        [Fact]
        public void AModuleWithNoDependenciesStillAppears()
        {
            var graph = new ModuleDependencyGraph();
            graph.AddModule("lonely");

            Assert.True(graph.TryGetLoadOrder(out var order, out _));
            Assert.Equal(new[] { "lonely" }, order);
        }

        [Fact]
        public void AModuleDependingOnItselfIsNotACycle()
        {
            // Every file in a directory contributes to one module, so a reference within it says
            // nothing about load order.
            var graph = new ModuleDependencyGraph();
            graph.AddDependency("app", "app");

            Assert.True(graph.TryGetLoadOrder(out var order, out _));
            Assert.Equal(new[] { "app" }, order);
            Assert.Empty(graph.DependenciesOf("app"));
        }

        [Fact]
        public void ACycleIsRejectedAndReported()
        {
            var graph = new ModuleDependencyGraph();
            graph.AddDependency("a", "b");
            graph.AddDependency("b", "c");
            graph.AddDependency("c", "a");

            Assert.False(graph.TryGetLoadOrder(out var order, out var cycle));

            Assert.Empty(order);

            // The first module is repeated at the end so the loop reads as a loop.
            Assert.Equal(cycle[0], cycle[cycle.Count - 1]);
            Assert.Contains("a", cycle);
            Assert.Contains("b", cycle);
            Assert.Contains("c", cycle);
        }

        [Fact]
        public void ATwoModuleCycleIsCaught()
        {
            var graph = new ModuleDependencyGraph();
            graph.AddDependency("a", "b");
            graph.AddDependency("b", "a");

            Assert.False(graph.TryGetLoadOrder(out _, out var cycle));
            Assert.Equal(3, cycle.Count);
        }

        [Fact]
        public void TheOrderIsReproducible()
        {
            // Two modules with no dependency between them have no natural order, and a build that
            // reorders them for no reason produces images that differ for no reason.
            var first = new ModuleDependencyGraph();
            first.AddModule("zeta");
            first.AddModule("alpha");
            first.AddDependency("zeta", "base");
            first.AddDependency("alpha", "base");

            var second = new ModuleDependencyGraph();
            second.AddDependency("alpha", "base");
            second.AddModule("zeta");
            second.AddDependency("zeta", "base");
            second.AddModule("alpha");

            Assert.True(first.TryGetLoadOrder(out var left, out _));
            Assert.True(second.TryGetLoadOrder(out var right, out _));
            Assert.Equal(left, right);
        }

        [Fact]
        public void DependenciesAreDeduplicated()
        {
            var graph = new ModuleDependencyGraph();
            graph.AddDependency("app", "core");
            graph.AddDependency("app", "core");

            Assert.Single(graph.DependenciesOf("app"));
        }
    }
}
