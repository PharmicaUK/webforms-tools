# ASP.NET WebForms (VB) for VS Code

Language support for ASP.NET WebForms **VB.NET Web Application Projects** (`.vbproj` with code-behind and
`.designer.vb` files), from the `webforms` language server in
[webforms-tools](https://github.com/PharmicaUK/webforms-tools).

- **Highlighting** of `.aspx` / `.ascx` / `.master`: HTML, plus directives, server comments, server controls,
  `<%$ %>` expressions, and VB in `<% %>` / `<%= %>` / `<%: %>` / `<%# %>` / `<script runat="server">`.
- **Navigation**: go to the code-behind handler from `OnClick="btnSave_Click"`, to the designer field and every use
  from a control `ID`, to registered user controls and master pages; find references.
- **Completion**: server control tags, attributes, enum values, event handlers, `</` end tags, directives,
  `<%$ AppSettings: / ConnectionStrings: / Resources: %>`; VB members in code blocks, `Eval("` fields.
- **Diagnostics**: designer problems (unresolvable control types, duplicate IDs), missing or inaccessible event
  handlers, stale designer files, VB compile errors (`BCxxxxx`) in code blocks.
- **Generate designer**: the `WebForms: Generate Designer` command, a code action, or on save
  (`webforms.generateDesignerOnSave`). Writes `X.aspx.designer.vb` as Visual Studio would.
- **VB formatting** of `.vb` files (Format Document / Format Selection), Visual Studio's pretty listing.

## Requirements

- Windows x64, the .NET 10 runtime and the .NET Framework 4.x reference assemblies (see the
  [main README](https://github.com/PharmicaUK/webforms-tools#requirements)).
- The server, the `Pharmica.WebForms.Tools` dotnet tool:

  ```
  dotnet tool install -g Pharmica.WebForms.Tools
  ```

  `webforms --version` should print the version. The extension starts `webforms lsp` from `PATH`; set
`webforms.server.path` if it is somewhere else.

## Install

The extension is not on the Marketplace. Download `webforms-<version>.vsix` from the
[GitHub Releases](https://github.com/PharmicaUK/webforms-tools/releases), then
`code --install-extension webforms-<version>.vsix` (or *Extensions → … → Install from VSIX*).

## Settings

| Setting | Default | |
|---|---|---|
| `webforms.server.path` | `webforms` | The server executable. Changing it restarts the server. |
| `webforms.server.log` | (empty) | A file the server appends a request log to. |
| `webforms.generateDesignerOnSave` | `false` | Regenerate the designer file when markup is saved. |
| `webforms.completion.zeroArgHandlers` | `false` | Offer parameterless Subs as event handlers. |
| `webforms.codeBlocks.enabled` | `true` | VB features in code blocks (compiles the project in the server: ~450 MB per project, dropped after 10 idle minutes). |
| `webforms.diagnostics.staleDesigner` | `true` | Report `WF0100` when a saved page's designer file is out of date or missing. |
| `webforms.trace.server` | `off` | Log the LSP traffic to the `WebForms` output channel. |

## Notes

- **Restricted Mode** (a folder you haven't trusted) disables the extension: `.aspx` files then show as plain HTML
  and there is no server. Trust the folder to use it.
- `.aspx` files get this extension's language (`aspx`), so VS Code's HTML features (HTML completion, Emmet) don't
  apply to them. For Emmet, add `"emmet.includeLanguages": { "aspx": "html" }` to your settings.
- `.vb` files keep VS Code's built-in `vb` language; the server adds formatting, navigation from the code-behind,
  and handler diagnostics.

## Development

```
npm install
npm run check      # TypeScript
npm test           # downloads VS Code into .vscode-test/ and runs src/test/suite.ts against the webforms on PATH
npm run package    # -> ../../artifacts/vsix/webforms-<version>.vsix
```

`npm test` options (environment): `WEBFORMS_SERVER` (server path), `WEBFORMS_TEST_GRAMMAR_FILES` (`;`-separated
markup files that must have no `invalid.illegal` tokens, e.g. a project's pages, opened read-only),
`WEBFORMS_TEST_GRAMMAR_VERBOSE=1` (print the scopes around each one).

Install a built package with `code --install-extension artifacts/vsix/webforms-<version>.vsix`.
