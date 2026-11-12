// Runs inside VS Code (see runTest.ts). Each check goes through VS Code's own command API, so it exercises the
// grammar, the language contribution, the client and the server together.
import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';

const workspace = process.env.WEBFORMS_TEST_WORKSPACE!;
const uriOf = (name: string) => vscode.Uri.file(path.join(workspace, name));

/** Retries until the result passes (the server starts, then compiles the project in the background). */
async function eventually<T>(what: string, get: () => Thenable<T> | T, ok: (t: T) => boolean, timeoutMs = 60_000): Promise<T> {
  const end = Date.now() + timeoutMs;
  let last: T | undefined;
  for (;;) {
    try {
      last = await get();
      if (ok(last)) {
        return last;
      }
    } catch {
      // not ready yet
    }

    if (Date.now() > end) {
      throw new Error(`${what}: timed out; last result ${JSON.stringify(last)?.slice(0, 500)}`);
    }

    await new Promise(r => setTimeout(r, 250));
  }
}

function positionOf(doc: vscode.TextDocument, text: string, delta = 0): vscode.Position {
  const offset = doc.getText().indexOf(text);
  assert.ok(offset >= 0, `"${text}" not in ${doc.fileName}`);
  return doc.positionAt(offset + delta);
}

const labels = (list: vscode.CompletionList | undefined) =>
  (list?.items ?? []).map(i => (typeof i.label === 'string' ? i.label : i.label.label));

type Check = [string, () => Promise<void>];

export async function run(): Promise<void> {
  const doc = await vscode.workspace.openTextDocument(uriOf('Probe.aspx'));
  await vscode.window.showTextDocument(doc);

  const checks: Check[] = [
    ['language: .aspx is aspx, .vb is vb', async () => {
      assert.strictEqual(doc.languageId, 'aspx');
      const vb = await vscode.workspace.openTextDocument(uriOf('Probe.aspx.vb'));
      assert.strictEqual(vb.languageId, 'vb');
    }],

    ['grammar: VB in <%= %>, server comment, directive, expression builder', async () => {
      // _workbench.captureSyntaxTokens is what VS Code's own colorization tests use.
      const tokens = await vscode.commands.executeCommand<{ c: string; t: string }[]>('_workbench.captureSyntaxTokens', doc.uri);
      const scopes = (text: string) => tokens.filter(t => t.c.includes(text)).map(t => t.t);
      const scopeOf = (text: string) => scopes(text)[0] ?? `(no token "${text}")`;
      assert.match(scopeOf('Greeting.'), /meta\.embedded\.block\.aspx source\.asp\.vb\.net/);
      assert.match(scopeOf('a server comment'), /comment\.block\.server\.aspx/);
      assert.match(scopeOf('Page'), /keyword\.control\.directive\.aspx/);
      assert.match(scopeOf('AppSettings'), /support\.function\.expression-builder\.aspx/);
      assert.match(scopeOf('asp:Button'), /entity\.name\.tag\.aspx/);
      assert.doesNotMatch(scopeOf('asp:Button'), /invalid/);
      assert.match(scopeOf('ItemTemplate'), /entity\.name\.tag\.aspx/); // an inner property
      // <script runat="server"> is VB, a client <script> stays JavaScript.
      assert.ok(scopes('Shout').some(s => /source\.asp\.vb\.net/.test(s)), scopes('Shout').join('\n'));
      assert.ok(scopes('clientSide').some(s => /source\.js/.test(s)), scopes('clientSide').join('\n'));
      // <%# %> inside a server control's attribute value is still VB.
      assert.ok(scopes('Greeting').some(s => /string\.quoted\.single\.aspx meta\.embedded\.block\.aspx source\.asp\.vb\.net/.test(s)),
        scopes('Greeting').join('\n'));
    }],

    ['definition: OnClick handler -> code-behind', async () => {
      const locations = await eventually('definition',
        () => vscode.commands.executeCommand<(vscode.Location | vscode.LocationLink)[]>('vscode.executeDefinitionProvider', doc.uri, positionOf(doc, 'btnGo_Click', 2)),
        l => (l?.length ?? 0) > 0);
      const target = 'targetUri' in locations[0] ? locations[0].targetUri : locations[0].uri;
      assert.ok(target.fsPath.toLowerCase().endsWith('probe.aspx.vb'), target.fsPath);
    }],

    ['completion: attributes of asp:Button', async () => {
      const list = await eventually('attribute completion',
        () => vscode.commands.executeCommand<vscode.CompletionList>('vscode.executeCompletionItemProvider', doc.uri, positionOf(doc, 'Text="Go"')),
        l => labels(l).includes('OnClientClick'));
      assert.ok(labels(list).includes('CausesValidation'));
    }],

    ['completion: VB members in a code block', async () => {
      const list = await eventually('code-block completion',
        () => vscode.commands.executeCommand<vscode.CompletionList>('vscode.executeCompletionItemProvider', doc.uri, positionOf(doc, 'Greeting.ToUpper', 'Greeting.'.length), '.'),
        l => labels(l).includes('ToUpper'));
      assert.ok(labels(list).includes('Substring'));
    }],

    ['diagnostics: a BC error in a code block', async () => {
      const editor = await vscode.window.showTextDocument(doc);
      const at = positionOf(doc, 'Greeting.ToUpper');
      assert.ok(await editor.edit(e => e.replace(new vscode.Range(at, at.translate(0, 'Greeting'.length)), 'Greting')), 'edit not applied');
      assert.ok(doc.getText().includes('Greting.ToUpper'));
      let diagnostics: vscode.Diagnostic[];
      try {
        diagnostics = await eventually('BC30451',
          () => vscode.languages.getDiagnostics(doc.uri),
          d => d.some(x => String(typeof x.code === 'object' ? x.code.value : x.code) === 'BC30451'));
      } catch (err) {
        // Which URIs did get diagnostics (a path spelled differently by the server would show up here)?
        for (const [uri, list] of vscode.languages.getDiagnostics()) {
          console.log(`        diagnostics for ${uri.toString()}: ${list.map(d => `${String(typeof d.code === 'object' ? d.code.value : d.code)} ${d.message}`).join('; ')}`);
        }
        console.log(`        document: ${doc.uri.toString()}`);
        throw err;
      }
      assert.ok(diagnostics.length > 0);
      await vscode.commands.executeCommand('undo');
      await eventually('BC30451 cleared', () => vscode.languages.getDiagnostics(doc.uri), d => !d.some(x => String(x.code).startsWith('BC')));
    }],

    ['formatting: a .vb file', async () => {
      const vb = await vscode.workspace.openTextDocument(uriOf('Unformatted.vb'));
      await vscode.window.showTextDocument(vb);
      const edits = await eventually('format edits',
        () => vscode.commands.executeCommand<vscode.TextEdit[]>('vscode.executeFormatDocumentProvider', vb.uri, { tabSize: 4, insertSpaces: true }),
        e => (e?.length ?? 0) > 0);
      const formatted = new vscode.WorkspaceEdit();
      formatted.set(vb.uri, edits);
      await vscode.workspace.applyEdit(formatted);
      assert.match(vb.getText(), /Public Class Unformatted\r?\n {4}Sub M\(\)\r?\n {8}Dim x As Integer = 1/);
    }],

    ['command: WebForms: Generate Designer', async () => {
      await vscode.window.showTextDocument(doc);
      const designer = path.join(workspace, 'Probe.aspx.designer.vb');
      assert.ok(!fs.existsSync(designer));
      await vscode.commands.executeCommand('webforms.generateDesignerForActiveFile');
      await eventually('designer written', () => fs.existsSync(designer), x => x, 10_000);
      assert.match(fs.readFileSync(designer, 'utf8'), /Protected WithEvents btnGo As Global\.System\.Web\.UI\.WebControls\.Button/);
    }],
  ];

  // Optional: WEBFORMS_TEST_GRAMMAR_FILES="a.aspx;b.ascx" (e.g. corpus pages, opened read-only) must have no token
  // scoped invalid.illegal (HTML marks tags it doesn't know, such as server tags, that way). invalid.deprecated
  // (HTML's verdict on align= and the like) is what any .html file gets, so it is not counted.
  for (const file of (process.env.WEBFORMS_TEST_GRAMMAR_FILES ?? '').split(';').filter(f => f.length > 0)) {
    checks.push([`grammar: no invalid tokens in ${path.basename(file)}`, async () => {
      const uri = vscode.Uri.file(file);
      await vscode.workspace.openTextDocument(uri);
      const tokens = await vscode.commands.executeCommand<{ c: string; t: string }[]>('_workbench.captureSyntaxTokens', uri);
      const invalid = tokens.filter(t => /\binvalid\.illegal\b/.test(t.t));
      const blocks = tokens.filter(t => t.t.includes('meta.embedded.block.aspx')).length;
      console.log(`        ${tokens.length} tokens, ${blocks} in code blocks, ${invalid.length} invalid`);
      for (const t of invalid.slice(0, 10)) {
        const at = tokens.indexOf(t);
        const context = tokens.slice(Math.max(0, at - 4), at + 2).map(x => x.c).join('');
        console.log(`        invalid ${JSON.stringify(t.c)} in ${JSON.stringify(context.slice(0, 120))}: ${t.t}`);
        if (process.env.WEBFORMS_TEST_GRAMMAR_VERBOSE) {
          for (const x of tokens.slice(Math.max(0, at - 12), at + 2)) {
            console.log(`          ${JSON.stringify(x.c)} ${x.t}`);
          }
        }
      }
      assert.deepStrictEqual(invalid.slice(0, 10).map(t => `${JSON.stringify(t.c)} ${t.t}`), []);
    }]);
  }

  const failures: string[] = [];
  for (const [name, check] of checks) {
    const started = Date.now();
    try {
      await check();
      console.log(`  ok    ${name} (${Date.now() - started} ms)`);
    } catch (err) {
      failures.push(name);
      console.log(`  FAIL  ${name}: ${err instanceof Error ? err.message : String(err)}`);
    }
  }

  if (failures.length > 0) {
    throw new Error(`${failures.length} of ${checks.length} checks failed`);
  }
}
