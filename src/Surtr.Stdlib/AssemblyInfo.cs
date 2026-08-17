#nullable enable

using System.Runtime.CompilerServices;

// Surtr.Tests exercises SurtrStdlib.RegisterNativeBodies directly (it is internal - a host never
// calls it itself, LoadInto does) to compare what it publishes against
// Surtr.Stdlib/build/native-link-names.txt, the drift detector for a native fun added to the
// stdlib source without a matching C# body registered for it. Mirrors Surtr.Core's own
// AssemblyInfo.cs, which grants the same thing for the same reason.
[assembly: InternalsVisibleTo("Surtr.Tests")]
