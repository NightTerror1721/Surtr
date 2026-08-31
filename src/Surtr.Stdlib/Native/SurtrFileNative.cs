#nullable enable

using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System.IO;

namespace Surtr.Stdlib.Native
{
    /// <summary>
    /// The C# bodies behind <c>surtr.io.File</c>'s native declarations - whole-file reads/writes and
    /// directory queries, backed directly by <see cref="System.IO"/>. In-process, synchronous, no
    /// sandboxing of its own: a host that does not want a script touching the filesystem simply does
    /// not load this module (<c>StdlibModules.Io</c> already gates every other <c>surtr.io</c> type
    /// the same way).
    /// </summary>
    /// <remarks>
    /// None of these catch <see cref="System.IO"/>'s own exceptions by hand: any exception a native
    /// body throws is automatically turned into the Surtr object a <c>catch</c> clause is matched
    /// against (<c>SurtrVirtualMachine.AsSurtrObject</c>) - mapped to a built-in class where one
    /// exists, or a native proxy otherwise. A script sees a real, catchable failure either way.
    /// </remarks>
    internal static unsafe class SurtrFileNative
    {
        internal static int FileExists(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateBool(File.Exists(arguments.GetString(0).ToString())));

        internal static int DirectoryExists(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateBool(Directory.Exists(arguments.GetString(0).ToString())));

        internal static int FileDelete(SurtrCallArguments arguments)
        {
            File.Delete(arguments.GetString(0).ToString());
            return 0;
        }

        internal static int CreateDirectory(SurtrCallArguments arguments)
        {
            Directory.CreateDirectory(arguments.GetString(0).ToString());
            return 0;
        }

        internal static int FileReadAllText(SurtrCallArguments arguments)
        {
            string text = File.ReadAllText(arguments.GetString(0).ToString());
            return arguments.Return(SurtrValue.CreateReference(arguments.Runtime.NewString(text).GetSurtrReference()));
        }

        internal static int FileWriteAllText(SurtrCallArguments arguments)
        {
            File.WriteAllText(arguments.GetString(0).ToString(), arguments.GetString(1).ToString());
            return 0;
        }

        internal static int FileAppendAllText(SurtrCallArguments arguments)
        {
            File.AppendAllText(arguments.GetString(0).ToString(), arguments.GetString(1).ToString());
            return 0;
        }

        internal static int FileReadAllBytes(SurtrCallArguments arguments)
        {
            byte[] data = File.ReadAllBytes(arguments.GetString(0).ToString());
            var result = arguments.Runtime.NewBytes(data);
            return arguments.Return(SurtrValue.CreateReference(result.GetSurtrReference()));
        }

        internal static int FileWriteAllBytes(SurtrCallArguments arguments)
        {
            var bytesObject = arguments.Get<SurtrBytes>(1);
            File.WriteAllBytes(arguments.GetString(0).ToString(), bytesObject.ToArray());
            return 0;
        }

        internal static int ListFiles(SurtrCallArguments arguments)
        {
            string[] entries = Directory.GetFiles(arguments.GetString(0).ToString());
            var result = arguments.Runtime.NewArray(SurtrClassReference.String, entries.Length);
            for (int i = 0; i < entries.Length; i++)
                result.Add(SurtrValue.CreateReference(arguments.Runtime.NewString(entries[i]).GetSurtrReference()));
            return arguments.Return(SurtrValue.CreateReference(result.GetSurtrReference()));
        }

        internal static int ListDirectories(SurtrCallArguments arguments)
        {
            string[] entries = Directory.GetDirectories(arguments.GetString(0).ToString());
            var result = arguments.Runtime.NewArray(SurtrClassReference.String, entries.Length);
            for (int i = 0; i < entries.Length; i++)
                result.Add(SurtrValue.CreateReference(arguments.Runtime.NewString(entries[i]).GetSurtrReference()));
            return arguments.Return(SurtrValue.CreateReference(result.GetSurtrReference()));
        }
    }
}
