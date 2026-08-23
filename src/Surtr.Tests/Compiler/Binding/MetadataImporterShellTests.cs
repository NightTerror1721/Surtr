#nullable enable

using Surtr.Bytecode.Image;
using Surtr.Compiler.Binding;
using Surtr.Compiler.CodeGen;
using Surtr.Compiler.Compilation;
using System;
using System.Collections.Generic;
using Xunit;

namespace Surtr.Tests.Compiler.Binding
{
    /// <summary>
    /// The containing-module shell trap: importing one module's metadata can require creating a
    /// <see cref="ModuleSymbol"/> for another module's path - a base class, an interface, any
    /// descriptor naming it - and that shell starts empty. If <c>ImportModule</c> then treated the
    /// cached shell as an imported surface, the implicitly-imported built-in library (§13) came
    /// back with no types and every unqualified library name stopped resolving. These tests pin
    /// completion-in-place: reference an image whose metadata names built-ins, then use those
    /// names unqualified in a second module compiled against it.
    /// </summary>
    public sealed class MetadataImporterShellTests
    {
        private const string DeclaringSource =
            "public class Derived : Exception { public constructor() : super(\"derived\") { } }\n"
            + "public fun make(): Exception { return Derived(); }\n";

        private const string DriverSource =
            "fun run(): int {\n"
            + "    try { throw InvalidOperationException(\"boom\"); }\n"
            + "    catch (e: Exception) { return e.message == \"boom\" ? 7 : 0; }\n"
            + "}\n";

        [Fact]
        public void ReferencingAnImageThatNamesBuiltinsKeepsTheLibraryInScope()
        {
            byte[] declaringImage = CompileToImage("declaring", DeclaringSource);

            var project = new SurtrProject(sourceRoot: ".");
            project.AddReference(SurtrModuleImage.FromBytes(declaringImage));
            project.AddSourceFile("driver.surtr", "driver", DriverSource);

            AssertCompiles(project);
        }

        [Fact]
        public void TwoReferencedImagesStillKeepTheLibraryInScope()
        {
            byte[] first = CompileToImage("first", DeclaringSource);
            byte[] second = CompileToImage(
                "second",
                "public interface IMarker { fun mark(): void; }\n"
                    + "public class Marked : IMarker { public fun mark(): void { } }\n");

            var project = new SurtrProject(sourceRoot: ".");
            project.AddReference(SurtrModuleImage.FromBytes(first));
            project.AddReference(SurtrModuleImage.FromBytes(second));
            project.AddSourceFile("driver.surtr", "driver", DriverSource);

            AssertCompiles(project);
        }

        private static void AssertCompiles(SurtrProject project)
        {
            using var compilation = SurtrCompilation.Create(project);
            var binder = compilation.Bind();
            binder.BindBodies();

            Assert.True(
                !compilation.Diagnostics.HasErrors,
                "Binding reported: " + string.Join("; ", compilation.Diagnostics));

            var emitter = new ModuleEmitter(compilation, binder);
            Assert.True(emitter.TryEmit());
        }

        private static byte[] CompileToImage(string moduleName, string source)
        {
            var project = new SurtrProject(sourceRoot: ".");
            project.AddSourceFile(moduleName + ".surtr", moduleName, source);

            using var compilation = SurtrCompilation.Create(project);
            var binder = compilation.Bind();
            binder.BindBodies();

            Assert.True(!compilation.Diagnostics.HasErrors);

            var emitter = new ModuleEmitter(compilation, binder);
            return emitter.EmitImages()[0].ToBytes();
        }
    }
}
