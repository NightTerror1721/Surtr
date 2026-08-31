import * as fs from 'fs';
import * as path from 'path';
import { window, workspace } from 'vscode';
import {
    Executable,
    LanguageClient,
    LanguageClientOptions,
    ServerOptions,
    TransportKind,
} from 'vscode-languageclient/node';

let client: LanguageClient | undefined;

export function activate(): void {
    const server = resolveServer();
    if (!server) {
        void window.showErrorMessage(
            'Surtr language server not found. Set the "surtr.languageServer.path" setting to the ' +
            'surtr-lsp executable, or put "surtr-lsp" on your PATH.');
        return;
    }

    const options: LanguageClientOptions = {
        documentSelector: [{ language: 'surtr' }],
        synchronize: {
            configurationSection: 'surtr',
            // Without this, a .surtr file created, edited or deleted outside any open editor
            // buffer (a git checkout, another tool, a rename) never reaches the server at all -
            // it only sees disk state again on the next didOpen/didChange/didSave. This is what
            // makes workspace/didChangeWatchedFiles notifications happen; the server's own handler
            // just triggers the same whole-workspace rebuild a save already does.
            fileEvents: workspace.createFileSystemWatcher('**/*.surtr'),
        },
        // Passed through verbatim as `initialize`'s initializationOptions - see
        // SurtrInitializationOptions on the server side for what it does with projectRoot.
        initializationOptions: {
            projectRoot: workspace.getConfiguration('surtr').get<string>('projectRoot') || undefined,
        },
        outputChannelName: 'Surtr Language Server',
    };

    client = new LanguageClient('surtr', 'Surtr Language Server', server, options);
    client.start().catch((error: unknown) => {
        void window.showErrorMessage(`Surtr language server failed to start: ${String(error)}`);
    });
}

export function deactivate(): Thenable<void> | undefined {
    return client ? client.stop() : undefined;
}

/**
 * Where the server lives. The `surtr.languageServer.path` setting wins when it names a file
 * (or a directory containing the executable); otherwise `surtr-lsp` is looked up on PATH.
 * A path ending in `.dll` is run through `dotnet`, since the apphost may not exist on every
 * platform. Returns null when nothing is found, so the user gets one clear message instead of
 * a bare spawn failure.
 */
function resolveServer(): ServerOptions | null {
    const configured = workspace.getConfiguration('surtr').get<string>('languageServer.path');
    if (configured) {
        const fromPath = resolveConfigured(configured);
        if (fromPath) return serverOptions(fromPath);
        return null;
    }

    const onPath = findOnPath('surtr-lsp');
    return onPath ? serverOptions(onPath) : null;
}

function resolveConfigured(configured: string): string | null {
    if (!fs.existsSync(configured)) return null;

    if (fs.statSync(configured).isDirectory()) {
        const candidates = process.platform === 'win32'
            ? ['surtr-lsp.exe', 'surtr-lsp']
            : ['surtr-lsp'];
        for (const name of candidates) {
            const inside = path.join(configured, name);
            if (fs.existsSync(inside)) return inside;
        }
        return null;
    }

    return configured;
}

function serverOptions(server: string): ServerOptions {
    const executable: Executable = {
        command: server,
        transport: TransportKind.stdio,
    };
    if (server.endsWith('.dll')) {
        executable.command = 'dotnet';
        executable.args = [server];
    }
    return { run: executable, debug: executable };
}

function findOnPath(command: string): string | null {
    const dirs = process.env.PATH?.split(path.delimiter) ?? [];
    const names = process.platform === 'win32'
        ? [command + '.exe', command + '.cmd', command]
        : [command];

    for (const dir of dirs) {
        if (!dir) continue;
        for (const name of names) {
            const candidate = path.join(dir, name);
            if (fs.existsSync(candidate)) return candidate;
        }
    }

    return null;
}
