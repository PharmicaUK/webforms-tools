// VS Code client for `webforms lsp`: a thin wrapper. The server does everything (navigation, completion, diagnostics,
// designer generation, VB formatting); this file starts it, passes the settings and adds a few palette commands.
import * as vscode from 'vscode';
import {
  LanguageClient,
  LanguageClientOptions,
  RevealOutputChannelOn,
  ServerOptions,
  State,
  TransportKind,
} from 'vscode-languageclient/node';

/** The server's own command (see WebFormsLanguageServer.GenerateDesignerCommand); takes a document URI. */
const GENERATE_DESIGNER = 'webforms.generateDesigner';

let client: LanguageClient | undefined;
let output: vscode.OutputChannel | undefined;

export async function activate(context: vscode.ExtensionContext): Promise<void> {
  output = vscode.window.createOutputChannel('WebForms');
  context.subscriptions.push(
    output,
    vscode.commands.registerCommand('webforms.generateDesignerForActiveFile', generateDesignerForActiveFile),
    vscode.commands.registerCommand('webforms.restartServer', async () => {
      await stop();
      await start(context);
    }),
    vscode.commands.registerCommand('webforms.showOutput', () => output?.show(true)),
    vscode.workspace.onDidChangeConfiguration(async e => {
      // Settings the server reads arrive through workspace/didChangeConfiguration; these change how it is started.
      if (e.affectsConfiguration('webforms.server')) {
        await stop();
        await start(context);
      }
    }),
  );
  await start(context);
}

export async function deactivate(): Promise<void> {
  await stop();
}

/** The client, once started (the integration test reads it). */
export function languageClient(): LanguageClient | undefined {
  return client;
}

async function start(context: vscode.ExtensionContext): Promise<void> {
  const config = vscode.workspace.getConfiguration('webforms');
  const command = config.get<string>('server.path')?.trim() || 'webforms';
  const log = config.get<string>('server.log')?.trim();
  const args = ['lsp', ...(log ? ['--log', log] : [])];
  const serverOptions: ServerOptions = { command, args, transport: TransportKind.stdio };

  const clientOptions: LanguageClientOptions = {
    // .aspx / .ascx / .master (this extension's language) and VB code-behind (VS Code's built-in `vb`). The server
    // decides from the file extension, never from the languageId.
    documentSelector: [
      { scheme: 'file', language: 'aspx' },
      { scheme: 'file', language: 'vb' },
    ],
    initializationOptions: serverSettings(),
    synchronize: { configurationSection: 'webforms' },
    outputChannel: output,
    revealOutputChannelOn: RevealOutputChannelOn.Never,
  };

  client = new LanguageClient('webforms', 'WebForms', serverOptions, clientOptions);
  try {
    await client.start();
  } catch (err) {
    client = undefined;
    const message = err instanceof Error ? err.message : String(err);
    output?.appendLine(`Could not start "${command} ${args.join(' ')}": ${message}`);
    const install = 'Install instructions';
    const choice = await vscode.window.showErrorMessage(
      `WebForms: could not start the language server ("${command}"). Install it with ` +
        '`dotnet tool install -g Pharmica.WebForms.Tools`, or set webforms.server.path.',
      install,
      'Open Settings',
    );
    if (choice === install) {
      await vscode.env.openExternal(vscode.Uri.parse('https://github.com/PharmicaUK/webforms-tools#install'));
    } else if (choice === 'Open Settings') {
      await vscode.commands.executeCommand('workbench.action.openSettings', 'webforms.server.path');
    }
    return;
  }
  context.subscriptions.push({ dispose: () => void stop() });
}

async function stop(): Promise<void> {
  const c = client;
  client = undefined;
  if (c && c.state !== State.Stopped) {
    await c.stop();
  }
}

/** The settings the server reads (ServerSettings.cs), in its own shape. */
function serverSettings(): object {
  const config = vscode.workspace.getConfiguration('webforms');
  return {
    generateDesignerOnSave: config.get<boolean>('generateDesignerOnSave', false),
    completion: { zeroArgHandlers: config.get<boolean>('completion.zeroArgHandlers', false) },
    codeBlocks: { enabled: config.get<boolean>('codeBlocks.enabled', true) },
    diagnostics: { staleDesigner: config.get<boolean>('diagnostics.staleDesigner', true) },
  };
}

/** "WebForms: Generate Designer" for the active .aspx / .ascx / .master (or its code-behind). */
async function generateDesignerForActiveFile(): Promise<void> {
  const editor = vscode.window.activeTextEditor;
  if (!editor) {
    void vscode.window.showWarningMessage('WebForms: open an .aspx, .ascx or .master file (or its code-behind) first.');
    return;
  }

  if (!client || client.state !== State.Running) {
    void vscode.window.showWarningMessage('WebForms: the language server is not running.');
    return;
  }

  // The server generates from the open buffer (unsaved edits included), writes the file and shows its own message.
  await client.sendRequest('workspace/executeCommand', {
    command: GENERATE_DESIGNER,
    arguments: [editor.document.uri.toString()],
  });
}
