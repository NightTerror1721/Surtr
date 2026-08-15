#nullable enable

using System;
using System.IO;
using Surtr.LanguageServer;

namespace Surtr.LanguageServer
{
    /// <summary>
    /// <c>surtr-lsp</c>: the entry point of the Surtr language server. It speaks LSP over standard
    /// input and output, which is what a language server must do to be launched by an editor —
    /// VSCode starts this process and talks to it on those pipes, so nothing here may write to the
    /// console.
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                using var input = Console.OpenStandardInput();
                using var output = Console.OpenStandardOutput();
                using var server = new LspServer(input, output);
                server.Run();
                return 0;
            }
            catch (Exception ex)
            {
                try
                {
                    File.AppendAllText(Path.Combine(Path.GetTempPath(), "surtr-lsp.log"), ex + Environment.NewLine);
                }
                catch (IOException)
                {
                    // No place to write and nothing else to report on; the server dies silently.
                }

                return 1;
            }
        }
    }
}
