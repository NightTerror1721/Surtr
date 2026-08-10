#nullable enable

using System.Runtime.CompilerServices;

// Surtr.Tests builds SurtrChunk instances by hand (there is no compiler yet) and exercises
// the VM through Surtr.Core's internal surface - SurtrVirtualMachine, SurtrChunk and friends
// are deliberately internal to every other consumer, but the test project is the one place
// that has to reach past SurtrRuntime's public surface to verify them directly.
[assembly: InternalsVisibleTo("Surtr.Tests")]
