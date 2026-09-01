#nullable enable

using System.Runtime.CompilerServices;

// ReplSession/ReplOutcome (see ReplSession.cs) are deliberately internal - surtr's own subcommands
// are the only supported entry point into this project's behaviour - but the test project needs to
// drive a REPL session headlessly, feeding it lines and asserting on outcomes without a real
// console. Same rationale as Surtr.Core's own AssemblyInfo.cs.
[assembly: InternalsVisibleTo("Surtr.Tests")]
