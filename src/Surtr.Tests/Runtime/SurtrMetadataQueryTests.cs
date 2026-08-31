#nullable enable

using Surtr.Compiler.CodeGen;
using Surtr.Compiler.Compilation;
using Surtr.Runtime;
using Surtr.Runtime.Classes;
using System.Linq;

namespace Surtr.Tests.Runtime
{
    /// <summary>
    /// Covers <see cref="SurtrMetadataQuery"/>: exact-signature lookup, human-readable signatures
    /// and full names, "everything a module/type declares" enumeration, and the small
    /// builder-assist helper, against a module compiled through the real front end.
    /// </summary>
    public sealed class SurtrMetadataQueryTests
    {
        private const string Root = "D:/proj/src";

        private const string Source = @"
class Box {
    fun get(x: int): int { return x; }
    fun get(x: int, y: int): int { return x + y; }

    class Inner {
        fun ping(): int { return 1; }
    }
}

fun standalone(a: int, b: string): bool { return true; }
";

        private static SurtrRuntime LoadModule(out SurtrModule module)
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(Root + "/game/A.surtr", Source);
            project.AddSourceFile(Root + "/game/B.surtr", "fun other(): void { }\n");

            var compilation = SurtrCompilation.Create(project);
            var binder = compilation.Bind();
            binder.BindBodies();

            Assert.True(!compilation.HasErrors, "Unexpected diagnostics: " + string.Join("; ", compilation.Diagnostics.Select(d => d.ToString())));

            var emitter = new ModuleEmitter(compilation, binder);
            Assert.True(emitter.TryEmit(), "Emission failed: " + string.Join("; ", compilation.Diagnostics.Select(d => d.ToString())));

            var runtime = new SurtrRuntime();
            foreach (var built in emitter.Modules)
                runtime.LoadModule(built);

            Assert.True(runtime.TryGetModule("game.A", out module!));
            return runtime;
        }

        #region FindMethod

        [Fact]
        public void FindMethod_OnAClass_LocatesTheExactOverload()
        {
            using var runtime = LoadModule(out var module);
            Assert.True(module.TryGetClass("Box", out var box));

            var oneArg = SurtrMetadataQuery.FindMethod(box, "get", SurtrClassReference.Integer);
            var twoArg = SurtrMetadataQuery.FindMethod(box, "get", SurtrClassReference.Integer, SurtrClassReference.Integer);
            var noMatch = SurtrMetadataQuery.FindMethod(box, "get", SurtrClassReference.Integer, SurtrClassReference.Integer, SurtrClassReference.Integer);

            Assert.NotNull(oneArg);
            Assert.NotNull(twoArg);
            Assert.NotSame(oneArg, twoArg);
            Assert.Equal(1, oneArg!.ParameterCount);
            Assert.Equal(2, twoArg!.ParameterCount);
            Assert.Null(noMatch);
        }

        [Fact]
        public void FindMethod_OnAModule_LocatesAModuleLevelFunction()
        {
            using var runtime = LoadModule(out var module);
            var found = SurtrMetadataQuery.FindMethod(module, "standalone", SurtrClassReference.Integer, SurtrClassReference.String);
            Assert.NotNull(found);
            Assert.Equal("standalone", found!.Name);
        }

        #endregion

        #region Human-readable output

        [Fact]
        public void DescribeSignature_RendersNameParametersAndReturnType()
        {
            using var runtime = LoadModule(out var module);
            Assert.True(module.TryGetClass("Box", out var box));
            var twoArg = SurtrMetadataQuery.FindMethod(box, "get", SurtrClassReference.Integer, SurtrClassReference.Integer)!;

            Assert.Equal("get(x: int, y: int): int", SurtrMetadataQuery.DescribeSignature(twoArg));
        }

        [Fact]
        public void FullName_QualifiesAModuleLevelFunctionByModulePath()
        {
            using var runtime = LoadModule(out var module);
            var standalone = SurtrMetadataQuery.FindMethod(module, "standalone", SurtrClassReference.Integer, SurtrClassReference.String)!;

            string fullName = SurtrMetadataQuery.FullName(module, standalone);
            Assert.StartsWith("game.A:standalone(", fullName);
            Assert.Contains("int", fullName);
            Assert.Contains("string", fullName);
        }

        #endregion

        #region Enumeration

        [Fact]
        public void AllTypes_DescendsIntoNestedClasses()
        {
            using var runtime = LoadModule(out var module);
            var names = SurtrMetadataQuery.AllTypes(module, recursive: true).Select(t => t.Name).ToArray();

            Assert.Contains("Box", names);
            Assert.Contains("Inner", names);
        }

        [Fact]
        public void AllMembers_OnAClass_IncludesOverloadsAndNestedMembersWhenRecursive()
        {
            using var runtime = LoadModule(out var module);
            Assert.True(module.TryGetClass("Box", out var box));

            var members = SurtrMetadataQuery.AllMembers(box, recursive: true).ToArray();

            // Both "get" overloads plus "ping" from the nested Inner class.
            Assert.Equal(2, members.Count(m => m.Name == "get"));
            Assert.Contains(members, m => m.Name == "ping");
        }

        [Fact]
        public void AllMembers_NonRecursive_ExcludesNestedClassMembers()
        {
            using var runtime = LoadModule(out var module);
            Assert.True(module.TryGetClass("Box", out var box));

            var members = SurtrMetadataQuery.AllMembers(box, recursive: false).ToArray();
            Assert.DoesNotContain(members, m => m.Name == "ping");
        }

        [Fact]
        public void IsSynthetic_RecognizesTheLeadingDollarConvention()
        {
            Assert.True(SurtrMetadataQuery.IsSynthetic("$backing$health"));
            Assert.False(SurtrMetadataQuery.IsSynthetic("health"));
        }

        #endregion

        #region Runtime-level lookups

        [Fact]
        public void TryResolveType_ResolvesAKnownDescriptor_AndFailsForAnUnknownOne()
        {
            using var runtime = LoadModule(out var module);

            Assert.True(runtime.TryResolveType(SurtrClassReference.Object("game.A:Box"), out var resolved));
            Assert.NotNull(resolved);

            Assert.False(runtime.TryResolveType(SurtrClassReference.Object("game.A:NoSuchClass"), out var missing));
            Assert.Null(missing);
        }

        [Fact]
        public void NativeClasses_EnumeratesEveryHostDeclaredNativeClass()
        {
            using var runtime = new SurtrRuntime();
            var declared = runtime.DefineNativeClass("host:Widget");
            runtime.FinishNativeClass(declared);

            Assert.Contains(declared, runtime.NativeClasses);
        }

        [Fact]
        public void GetSubmodules_FindsEveryModuleStrictlyUnderThePrefix()
        {
            using var runtime = LoadModule(out _);

            var submodules = runtime.GetSubmodules("game").Select(m => m.Path).ToArray();
            Assert.Contains("game.A", submodules);
            Assert.Contains("game.B", submodules);
        }

        #endregion

        #region Building metadata by hand

        [Fact]
        public void Parameter_BuildsFromADescriptorStringInOneCall()
        {
            using var runtime = new SurtrRuntime();
            var parameter = SurtrMetadataQuery.Parameter(runtime, "x", "I");

            Assert.Equal("x", parameter.Name);
            Assert.Equal("I", parameter.ParameterType.Reference.Descriptor);
        }

        #endregion
    }
}
