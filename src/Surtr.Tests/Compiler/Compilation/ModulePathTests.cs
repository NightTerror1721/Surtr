#nullable enable

using Surtr.Compiler.Compilation;

namespace Surtr.Tests.Compiler.Compilation
{
    /// <summary>
    /// Covers §2.1: a module has no header line, so where a file lives — its directories under
    /// the source root plus its own file name — is the only thing that says what module it is in.
    /// Each <c>.surtr</c> file is its own module.
    /// </summary>
    public sealed class ModulePathTests
    {
        private const string Root = "D:/proj/src";

        private static ModulePathStatus Derive(string filePath, out string modulePath, string rootModulePath = "")
            => ModulePath.TryDerive(Root, filePath, rootModulePath, out modulePath, out _);

        [Fact]
        public void DirectoriesAndFileNameBecomeSegments()
        {
            Assert.Equal(ModulePathStatus.Ok, Derive("D:/proj/src/game/core/Entity.surtr", out string path));
            Assert.Equal("game.core.Entity", path);
        }

        [Fact]
        public void FilesInOneDirectoryAreDistinctModules()
        {
            Derive("D:/proj/src/game/core/Entity.surtr", out string first);
            Derive("D:/proj/src/game/core/World.surtr", out string second);

            Assert.Equal("game.core.Entity", first);
            Assert.Equal("game.core.World", second);
            Assert.NotEqual(first, second);
        }

        [Fact]
        public void TheRootModulePathIsPrefixedOntoEverything()
        {
            Assert.Equal(ModulePathStatus.Ok, Derive("D:/proj/src/core/Entity.surtr", out string path, "game"));
            Assert.Equal("game.core.Entity", path);
        }

        [Fact]
        public void AFileAtTheRootTakesTheRootModulePathAndItsOwnName()
        {
            Assert.Equal(ModulePathStatus.Ok, Derive("D:/proj/src/Entity.surtr", out string path, "game"));
            Assert.Equal("game.Entity", path);
        }

        [Fact]
        public void AFileAtTheRootWithNoRootModulePathIsItsOwnModule()
        {
            // A module is a file, so the file's own name is always a usable module — nothing is
            // left unnamed by a missing root module path.
            Assert.Equal(ModulePathStatus.Ok, Derive("D:/proj/src/Entity.surtr", out string path));
            Assert.Equal("Entity", path);
        }

        [Fact]
        public void AFileOutsideTheSourceRootIsRejected()
        {
            Assert.Equal(ModulePathStatus.OutsideSourceRoot, Derive("D:/proj/other/Entity.surtr", out _));
        }

        [Fact]
        public void ASiblingDirectoryIsNotAPrefixMatch()
        {
            // "D:/proj/srcExtra" starts with "D:/proj/src" as text but is a different directory.
            Assert.Equal(ModulePathStatus.OutsideSourceRoot, Derive("D:/proj/srcExtra/Entity.surtr", out _));
        }

        [Fact]
        public void ADirectoryThatIsNotAnIdentifierIsRejected()
        {
            var status = ModulePath.TryDerive(
                Root, "D:/proj/src/my-module/Entity.surtr", string.Empty, out _, out string offending);

            Assert.Equal(ModulePathStatus.InvalidSegment, status);
            Assert.Equal("my-module", offending);
        }

        [Fact]
        public void AFileNameThatIsNotAnIdentifierIsRejected()
        {
            var status = ModulePath.TryDerive(
                Root, "D:/proj/src/my-module.surtr", string.Empty, out _, out string offending);

            Assert.Equal(ModulePathStatus.InvalidSegment, status);
            Assert.Equal("my-module", offending);
        }

        [Fact]
        public void BackslashesAndForwardSlashesAreTheSamePath()
        {
            Assert.Equal(ModulePathStatus.Ok, Derive(@"D:\proj\src\game\core\Entity.surtr", out string path));
            Assert.Equal("game.core.Entity", path);
        }

        [Theory]
        [InlineData("game", true)]
        [InlineData("_private", true)]
        [InlineData("core2", true)]
        [InlineData("2core", false)]
        [InlineData("my-module", false)]
        [InlineData("my module", false)]
        [InlineData("", false)]
        public void ASegmentMustBeWritableAsAnIdentifier(string segment, bool expected)
        {
            Assert.Equal(expected, ModulePath.IsValidSegment(segment));
        }

        [Theory]
        [InlineData("game.core", true)]
        [InlineData("game", true)]
        [InlineData("game..core", false)]
        [InlineData("", false)]
        public void AWholePathIsValidatedSegmentBySegment(string path, bool expected)
        {
            Assert.Equal(expected, ModulePath.IsValid(path));
        }

        [Fact]
        public void CombineToleratesAnEmptySide()
        {
            Assert.Equal("game.core", ModulePath.Combine("game", "core"));
            Assert.Equal("core", ModulePath.Combine("", "core"));
            Assert.Equal("game", ModulePath.Combine("game", ""));
        }
    }
}
