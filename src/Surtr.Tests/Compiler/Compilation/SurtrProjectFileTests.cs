#nullable enable

using Surtr.Compiler.Compilation;
using Surtr.Compiler.Diagnostics;
using System;
using System.IO;
using System.Linq;

namespace Surtr.Tests.Compiler.Compilation
{
    /// <summary>
    /// Covers <see cref="SurtrProjectFile"/>: the file-backed <c>Read</c> and its in-memory sibling
    /// <c>Parse</c> (for a host with no real file - project settings in memory, in an asset
    /// database, wherever else), plus the <c>warningsAsErrors</c>/<c>suppress</c> directives.
    /// </summary>
    public sealed class SurtrProjectFileTests : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "surtr-project-file-tests",
            Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }

        private const string Contents = @"
root = src
module = game
output = out
warningsAsErrors = true
suppress ProjectFileInvalid, 2001
define Debug = true
reference ../lib/x.surtrc
";

        [Fact]
        public void Parse_ReproducesWhatReadProducesFromAFile()
        {
            Directory.CreateDirectory(_root);
            string path = Path.Combine(_root, "game.surtrproj");
            File.WriteAllText(path, Contents);

            var fromFile = SurtrProjectFile.Read(path, new SurtrDiagnosticBag());
            var fromMemory = SurtrProjectFile.Parse(Contents, "D:/virtual/dir", new SurtrDiagnosticBag());

            Assert.Equal(fromFile.Root, fromMemory.Root);
            Assert.Equal(fromFile.RootModulePath, fromMemory.RootModulePath);
            Assert.Equal(fromFile.Output, fromMemory.Output);
            Assert.Equal(fromFile.WarningsAsErrors, fromMemory.WarningsAsErrors);
            Assert.Equal(fromFile.SuppressedCodes.OrderBy(c => c), fromMemory.SuppressedCodes.OrderBy(c => c));
            Assert.Equal(fromFile.References, fromMemory.References);
            Assert.True(fromMemory.Constants.ContainsKey("Debug"));

            // Read derives Directory from the real file's location; Parse takes it as given.
            Assert.Equal("D:/virtual/dir", fromMemory.Directory);
        }

        [Fact]
        public void WarningsAsErrors_DefaultsToFalse()
        {
            var project = SurtrProjectFile.Parse("root = src", "d", new SurtrDiagnosticBag());
            Assert.False(project.WarningsAsErrors);
        }

        [Fact]
        public void Suppress_ParsesCodesByNameAndByNumber()
        {
            var diagnostics = new SurtrDiagnosticBag();
            var project = SurtrProjectFile.Parse("suppress ProjectFileInvalid, 2001", "d", diagnostics);

            Assert.False(diagnostics.HasErrors);
            Assert.Contains(SurtrDiagnosticCode.ProjectFileInvalid, project.SuppressedCodes);
            Assert.Contains((SurtrDiagnosticCode)2001, project.SuppressedCodes);
        }

        [Fact]
        public void Suppress_ReportsAnUnknownCodeAsAnError()
        {
            var diagnostics = new SurtrDiagnosticBag();
            SurtrProjectFile.Parse("suppress NotARealCode", "d", diagnostics);

            Assert.True(diagnostics.HasErrors);
        }
    }
}
