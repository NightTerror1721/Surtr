#nullable enable

using Surtr.Bytecode.Image;
using Surtr.Runtime;
using Surtr.Stdlib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Surtr.Run
{
    /// <summary>
    /// Loads every <c>.surtrc</c> image a path names into one runtime.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A path is a single image or a directory of them - "a compiled file or an entire compiled
    /// project" is exactly the choice <see cref="SurtrModuleImage"/> already makes room for:
    /// <c>SurtrBuild</c> writes one image per module into an output directory, and nothing about
    /// loading one requires the others to be loaded in any particular file order.
    /// </para>
    /// <para>
    /// <c>SurtrRuntime.LoadModule</c> resolves a module's type handles against whatever is already
    /// loaded (<c>docs/Module-Format.md</c> §4), so a module naming another one has to follow it. An
    /// image carries no dependency list of its own to sort by - that lives in the type handle table,
    /// which only exists once <see cref="SurtrModuleImage.Instantiate"/> has run - so this makes
    /// progress the same way the runtime already resolves a native import: try, and try again with
    /// whatever succeeded now in scope. A pass that loads nothing means what remains cannot be
    /// resolved from what was given, whether that is a missing reference, a genuine cycle, or a
    /// module that is simply broken, and <see cref="Load"/> reports it rather than looping forever.
    /// </para>
    /// </remarks>
    internal static class ModuleSet
    {
        /// <summary>What went wrong assembling or loading a set of images.</summary>
        internal sealed class LoadFailure : Exception
        {
            public LoadFailure(string message) : base(message)
            {
            }
        }

        /// <summary>Finds every <c>.surtrc</c> file a path names.</summary>
        /// <remarks>
        /// A single file is taken as itself; a directory is searched recursively, since nothing
        /// about the format requires a build's output to sit in one flat folder - a host merging
        /// more than one build's output only has to point here at their common parent.
        /// </remarks>
        /// <exception cref="LoadFailure">Nothing exists at the path, or a directory holds no images.</exception>
        internal static List<string> Discover(string path)
        {
            if (File.Exists(path))
                return new List<string> { path };

            if (!Directory.Exists(path))
                throw new LoadFailure($"there is nothing at '{path}'.");

            string[] files = Directory.GetFiles(path, "*" + SurtrModuleImage.FileExtension, SearchOption.AllDirectories);

            if (files.Length == 0)
                throw new LoadFailure($"'{path}' holds no '{SurtrModuleImage.FileExtension}' files.");

            Array.Sort(files, StringComparer.Ordinal);
            return new List<string>(files);
        }

        /// <summary>
        /// Loads every image at <paramref name="files"/> into <paramref name="runtime"/>, retrying
        /// what does not yet resolve until nothing more can be made to.
        /// </summary>
        /// <returns>
        /// The path of every module now loaded, in the order loading it succeeded - the runtime
        /// itself exposes lookup by path but no enumeration, so this is the one record of what a
        /// caller can now ask <see cref="SurtrRuntime.TryGetModule"/> for.
        /// </returns>
        /// <exception cref="LoadFailure">
        /// An image's bytes could not be read, or what remained after a pass that loaded nothing
        /// could not be resolved from what was given.
        /// </exception>
        internal static List<string> Load(SurtrRuntime runtime, IReadOnlyList<string> files)
        {
            var images = Read(files);
            SurtrStdlib.LoadAll(runtime);
            runtime.LoadModules(images);

            var loaded = new List<string>(images.Count);
            foreach (var image in images)
                if (runtime.TryGetModule(image.Path, out _))
                    loaded.Add(image.Path);

            return loaded;
        }

        /// <summary>Reads every image a path names into memory, reporting a read failure once.</summary>
        /// <exception cref="LoadFailure">An image's bytes could not be read.</exception>
        internal static List<SurtrModuleImage> Read(IReadOnlyList<string> files)
        {
            var images = new List<SurtrModuleImage>(files.Count);

            foreach (string file in files)
            {
                try
                {
                    images.Add(SurtrModuleImage.FromBytes(File.ReadAllBytes(file)));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SurtrImageFormatException)
                {
                    throw new LoadFailure($"'{file}' could not be read: {exception.Message}");
                }
            }

            return images;
        }
    }
}
