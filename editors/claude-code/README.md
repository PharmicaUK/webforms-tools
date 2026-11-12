# webforms plugin for Claude Code

Code intelligence for ASP.NET WebForms **VB.NET Web Application Projects** in Claude Code.

- **Language server** (`.lsp.json`): `webforms lsp` for `.aspx` / `.ascx` / `.master` / `.vb`. Claude gets an `LSP`
  tool for definition and references, so it goes from `OnClick="btnSave_Click"` or a control `ID` to the code-behind
  (and back) without grepping. After each edit it sees the server's diagnostics: unresolvable control types,
  duplicate IDs, missing or `Private` event handlers, stale designer files, and VB compile errors (`BCxxxxx`) in
  `<% %>` blocks, caught before a build.
- **Designer hooks** (`hooks/hooks.json`, `webforms hook claude`): after Claude edits a markup file or its
  code-behind, the `.designer.vb` file is regenerated (errors in the markup go back to Claude, exit code 2); a hand
  edit of a `*.designer.vb` file is blocked, since it is generated.

## Install

1. The server, on the `PATH` of the shell you start `claude` from (Windows x64, the .NET 10 runtime and the .NET
   Framework 4.x reference assemblies: see the
   [main README](https://github.com/PharmicaUK/webforms-tools#requirements)):

   ```
   dotnet tool install -g Pharmica.WebForms.Tools
   webforms --version
   ```

2. The plugin:

   ```
   claude plugin marketplace add PharmicaUK/webforms-tools
   claude plugin install webforms@webforms-tools --scope project
   ```

   or inside a session: `/plugin marketplace add PharmicaUK/webforms-tools`, then
   `/plugin install webforms@webforms-tools`.

   `--scope project` enables it for that repository only (written to its `.claude/settings.json`, so the team gets
   it too). To have teammates offered the marketplace as well, add `extraKnownMarketplaces` to that file (see the
   [main README](https://github.com/PharmicaUK/webforms-tools#claude-code)). The hooks run on every Edit / Write while the plugin is enabled (~80 ms each for a non-markup file), so
   a project scope keeps them out of unrelated repositories.

3. Check: ask Claude to put a typo in a `<%= %>` block of a page. `Found 1 new diagnostic issue` under the edit means
   the server is running; `/plugin` → **Errors** shows `Executable not found in $PATH: "webforms"` if step 1 is
   missing.

## Updating

`dotnet tool update -g Pharmica.WebForms.Tools` for the server; `claude plugin update
webforms@webforms-tools` for the plugin (only when its `version` changes).

If the repository's `.claude/settings.json` already runs `webforms hook claude` as a hook (the setup the main README
describes), remove those entries: the plugin's hooks replace them.
