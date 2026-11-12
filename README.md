# webforms-tools

[![CI](https://github.com/PharmicaUK/webforms-tools/actions/workflows/ci.yml/badge.svg)](https://github.com/PharmicaUK/webforms-tools/actions/workflows/ci.yml)

Tooling for editing classic ASP.NET WebForms **VB.NET Web Application Projects** (a `.vbproj` with `.aspx` /
`.ascx` / `.master` markup, `.vb` code-behind and `.designer.vb` files) outside Visual Studio: in Neovim, VS Code,
Claude Code, Rider, or from the command line and CI.

One `webforms` command does three things:

- **`webforms designer`** regenerates `X.aspx.designer.vb` from the markup, the way Visual Studio does when you save
  a page, byte for byte. Control types come from the real assembly metadata (`System.Web`, your
  `<%@ Register %>` directives, web.config `<controls>`, user controls), and a type it can't resolve is an error,
  never a guess.
- **`webforms format`** formats VB the way Visual Studio *pretty lists* it: indentation, spacing, keyword case,
  `EndIf` → `End If`, missing `Then` / `()`.
- **`webforms lsp`** is a language server for the markup: go from `OnClick="btnSave_Click"` or a control `ID` to
  the code-behind, completion of tags / attributes / handlers / enum values, diagnostics, "generate designer", VB
  formatting, and full VB (completion, hover, definition, compile errors) inside `<% %>` / `<%# %>` blocks.

Nothing from your project is executed: control metadata is read with `MetadataLoadContext`, and code blocks are
compiled in memory with Roslyn.

## Requirements

- **Windows x64.** The tool reads .NET Framework assemblies, and the package is compiled ahead-of-time for
  win-x64 (startup time matters for format-on-save).
- **The .NET 10 runtime** (or SDK): <https://dotnet.microsoft.com/download/dotnet/10.0>.
- **The .NET Framework 4.x reference assemblies** for your project's `TargetFrameworkVersion`. Any one of:
  - Visual Studio, or the **.NET Framework Developer Pack** for your version
    (<https://dotnet.microsoft.com/download/dotnet-framework>). They land in
    `C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.x`.
  - Without Visual Studio or admin rights: the **`Microsoft.NETFramework.ReferenceAssemblies.net48`** NuGet package
    (`net472`, `net462`, … for other versions). If any SDK-style project on the machine has restored it, it is
    already in your NuGet cache and is found there. Otherwise unpack it into the cache yourself:

    ```powershell
    $v = '1.0.3'
    $dir = "$env:USERPROFILE\.nuget\packages\microsoft.netframework.referenceassemblies.net48\$v"
    Invoke-WebRequest "https://www.nuget.org/api/v2/package/Microsoft.NETFramework.ReferenceAssemblies.net48/$v" -OutFile "$env:TEMP\net48.zip"
    Expand-Archive "$env:TEMP\net48.zip" $dir
    ```

  - Or point `WEBFORMS_FRAMEWORK_REFS` at any folder that contains `System.Web.dll` and the other reference
    assemblies.

  When nothing is found, the error names the version it was looking for.

## Install

```powershell
dotnet tool install -g Pharmica.WebForms.Tools
webforms --version
# later
dotnet tool update -g Pharmica.WebForms.Tools
```

This puts `webforms` on `PATH` (`%USERPROFILE%\.dotnet\tools`).

Without access to nuget.org: download `Pharmica.WebForms.Tools.<version>.nupkg` from the
[GitHub Releases](https://github.com/PharmicaUK/webforms-tools/releases) into a folder, then

```powershell
dotnet tool install -g Pharmica.WebForms.Tools --add-source <that folder>
```

## Editors

### Neovim (0.11+)

Neovim has no ASP.NET filetype (an `.aspx` can even be detected as `mason`), so add one, then configure the server:

```lua
vim.filetype.add({ extension = { aspx = "aspx", ascx = "aspx", master = "aspx", Master = "aspx" } })

vim.lsp.config("webforms", {
  cmd = { "webforms", "lsp" },
  -- Markup, and VB too so that unsaved code-behind edits are seen ("vb" is Neovim's own name for VB,
  -- "vbnet" a common custom one).
  filetypes = { "aspx", "vb", "vbnet" },
  root_dir = function(bufnr, on_dir)
    on_dir(vim.fs.root(bufnr, function(name) return name:match("%.vbproj$") ~= nil end)
      or vim.fs.root(bufnr, { "web.config", ".git" }))
  end,
  init_options = {
    generateDesignerOnSave = true, -- regenerate X.aspx.designer.vb on every save, as Visual Studio does
  },
})
vim.lsp.enable("webforms")
```

`root_dir` is a function because `root_markers` takes file names, not patterns like `*.vbproj`. Then `gd` /
`CTRL-]`, `grr` (references), `gra` (code actions: *Generate designer*) and completion work in markup, and
`vim.lsp.buf.format()` formats VB. More (keymaps, conform.nvim, using the CLI without the server):
[editors/neovim](https://github.com/PharmicaUK/webforms-tools/blob/main/editors/neovim/README.md).

### VS Code

Download `webforms-<version>.vsix` from the
[GitHub Releases](https://github.com/PharmicaUK/webforms-tools/releases) and install it:

```powershell
code --install-extension webforms-<version>.vsix
```

It adds the `aspx` language for `.aspx` / `.ascx` / `.master` (highlighting, including VB in code blocks), starts
`webforms lsp` from `PATH`, and adds *WebForms: Generate Designer* to the command palette. **Restricted Mode**
(an untrusted workspace) disables the extension: the markup then shows as plain HTML and nothing else works until
you trust the folder. Settings and details:
[editors/vscode](https://github.com/PharmicaUK/webforms-tools/blob/main/editors/vscode/README.md).

### Claude Code

The plugin gives Claude the language server (an `LSP` tool for definition / references, and diagnostics after each
of its edits, including VB compile errors in `<% %>` blocks) and two hooks: after Claude edits markup or a
code-behind, the `.designer.vb` is regenerated (generation errors go back to Claude); a hand edit of a
`*.designer.vb` is blocked, so Claude never writes one. This repository is its marketplace:

```
claude plugin marketplace add PharmicaUK/webforms-tools
claude plugin install webforms@webforms-tools --scope project
```

For a team, commit this to the WebForms project's `.claude/settings.json` instead: everyone who trusts the folder
is offered the marketplace and the plugin (each still needs the `webforms` tool installed).

```json
{
  "extraKnownMarketplaces": {
    "webforms-tools": { "source": { "source": "github", "repo": "PharmicaUK/webforms-tools" } }
  },
  "enabledPlugins": { "webforms@webforms-tools": true }
}
```

Details: [editors/claude-code](https://github.com/PharmicaUK/webforms-tools/blob/main/editors/claude-code/README.md).

### Rider, CI, MSBuild, git hooks

See [Other integrations](#other-integrations) below.

## Working alongside Visual Studio

The generated designer files are what Visual Studio writes, so people on Visual Studio, Rider and these tools can
work on the same project without designer churn:

- `--style auto` (the default) keeps the existing file's byte-level style (BOM, trailing spaces, blank lines), which
  differs between Visual Studio and Rider. New files get Visual Studio's style. `--style vs` / `--style rider` force one.
- `--master directive` (the default) writes the typed `Master` property only for a `<%@ MasterType %>` directive,
  as Visual Studio does. Rider also writes it for a plain `MasterPageFile`, which throws `InvalidCastException`
  when the code-behind switches master page at runtime, so regeneration removes Rider's. `--master keep` leaves an
  existing one alone; `--master always` behaves like Rider.
- A file is only written when its bytes change. Visual Studio doesn't create a missing designer on save; the tool
  does, and warns (`WF0101`, with the `<Compile Include>` to add) if it isn't in the `.vbproj` yet.

| | Visual Studio | Rider | hand-written |
|---|---|---|---|
| UTF-8 BOM | yes | yes | often no |
| header `'     the code is regenerated. ` | trailing space kept | trimmed (`.editorconfig`) | either |
| blank lines between fields | empty (`"    "` when not regenerated through the VB editor) | empty | empty |
| blank line before `End Class` | no | some versions | no |
| typed `Master` property | only for `<%@ MasterType %>` | also for any `MasterPageFile` | rarely |
| field order | document order | document order | often appended at the end |

## Command line

```
webforms designer <file.aspx|ascx|master>... [options]
webforms designer --all <project-dir> [options]
webforms format [options] < input.vb > output.vb
webforms lsp [--log <file>]
webforms hook claude
webforms --version
```

**Exit codes** (stable, all commands): `0` unchanged / written / formatted · `1` would change (`--check`) · `2` error ·
`3` usage.

**Diagnostics** go to stderr in MSBuild canonical format, which VS Code (`$msCompile`), Rider, Vim/Neovim
(errorformat) and CI log parsers all understand:

```
C:\site\Admin\Users.aspx(12,6): error WF3001: Unknown tag prefix 'ajax' (no Register directive or web.config <controls> entry).
```

### `webforms designer`

| Option | |
|---|---|
| `--check` | Don't write; exit 1 if any designer would change. |
| `--stdout` | Print the generated designer instead of writing it (one file). |
| `--all <dir>` | Every `.aspx/.ascx/.master` with a `CodeBehind` under `<dir>` (skips `bin`, `obj`, `packages`, `node_modules`, `.claude`, …). |
| `--style auto\|vs\|rider` | Byte-level formatting (see above). |
| `--master directive\|keep\|always` | When to write the typed `Master` property (see above). |
| `--verbose`, `--time` | Report skipped/unchanged files, timing. |

On a file that uses `CodeFile` (web site model) or has no `CodeBehind`, the command does nothing and exits 0. When a
control's type cannot be resolved it **fails** (exit 2, nothing written). About 80–130 ms per file including process
start; `--all` over a 150-page project takes under a second.

How types are resolved:

| Markup | Field type |
|---|---|
| `asp:*` | Looked up in `System.Web` / `System.Web.Extensions` / … (the root web.config `asp` registrations) from the reference assemblies for the project's `TargetFrameworkVersion`. |
| `<%@ Register TagPrefix Namespace Assembly %>` and web.config `<pages><controls><add tagPrefix namespace assembly>` | Looked up in that assembly, found via the `.vbproj` `HintPath`s and `bin\`. |
| `<%@ Register TagPrefix TagName Src %>` and web.config `<add tagPrefix tagName src>` | `Global.` + the `Inherits` of the `.ascx`. A code-less `.ascx` (no `Inherits`) gives `Global.System.Web.UI.UserControl`, as Visual Studio does. |
| HTML element with `runat="server"` | `HtmlForm`, `HtmlAnchor`, `HtmlInputText`/`CheckBox`/… by `type` (HTML5 types → `HtmlInputGenericControl`), `HtmlTable`/`Row`/`Cell`, `HtmlIframe`, … otherwise `HtmlGenericControl`. |

Which controls get a field follows the ASP.NET parser (`ControlBuilder` rules, read from the assembly metadata): a
control with a static `ID` gets a field unless it is inside a **multi-instance template** (`Repeater` / `ListView` /
`GridView` item templates, `LoginView` templates, …). Single-instance templates (`[TemplateInstance(Single)]`, such as
`UpdatePanel.ContentTemplate`) *do* get fields. `asp:Content` never gets a field. Controls inside `<%-- --%>` are
ignored; controls inside `<!-- -->` are **not** (ASP.NET parses them). A field is not emitted when the code-behind
already declares a member with that name. The class name and namespace come from `Inherits` minus the project's
`RootNamespace`.

### `webforms format`

```
webforms format [--stdin-filename <path>] [--lines <a>-<b>] [--check] < input.vb > output.vb
```

Reads VB on stdin and writes it formatted to stdout, the way Visual Studio pretty lists VB on line commit and
*Format Document*:

| | Example |
|---|---|
| indentation and spacing (Roslyn's formatter) | `x=a+b` → `x = a + b` |
| keyword case, including `#Region` / `#If` | `dim x as integer` → `Dim x As Integer` |
| the implicit setter parameter | `_x = value` → `_x = Value` (inside `Set`) |
| missing `Then`, `Case`, `()` on a call to a method in the same file | `ElseIf x` → `ElseIf x Then`, `Select x` → `Select Case x`, `DoIt` → `DoIt()` |
| obsolete / spaced keywords | `EndIf` → `End If`, `Else If` → `ElseIf` |
| modifier order | `Public Partial Class` → `Partial Public Class` |
| literals | `&H00FF` → `&HFF`, `0.20` → `0.2` |
| unnecessary `_` line continuations and `:` separators | `"a" & _` → `"a" &` |

What it deliberately does **not** do:

- **Identifier case correction** (`response.write` → `Response.Write`). That needs the whole project and its
  references; on a single file it guesses wrong, so identifiers are left as typed.
- **Insert `End If` / `End Sub` / `Next`.** Visual Studio does that when you press Enter; a formatter can't know where
  the block was meant to end.
- **Format code with syntax errors.** Nothing is written to stdout, the errors go to stderr
  (`file(line,col): error BC30081: 'If' must end with a matching 'End If'.`) and the exit code is 2, so the editor
  keeps the buffer as it was.
- **Touch generated code.** `*.designer.vb` (see `--stdin-filename`), `*.g.vb` and files with an `<auto-generated>`
  header are passed through unchanged.

| Option | |
|---|---|
| `--stdin-filename <path>` | File name for diagnostics, and to recognise designer files. |
| `--lines <a>-<b>` | Only format lines *a*..*b* (1-based, inclusive; *b* past the end is clamped). |
| `--check` | Print nothing; exit 1 if formatting would change the input (warning `WF4100`). |
| `--verbose`, `--time` | Report skipped files, timing. |

The UTF-8 BOM and line endings of the input are kept; indentation is 4 spaces. Input that is not UTF-8 is an error
(`WF4001`). The output depends only on the input text and `--lines`. About 0.5 s per run (most of it loading Roslyn);
the language server formats from a warm process in ~25 ms.

Pretty listing uses Roslyn's VB code-cleanup providers, which are `internal` to Roslyn: the tool is compiled against
them with a pinned Roslyn version, so a Roslyn upgrade that changes them breaks the build rather than the formatter.

### `webforms lsp`

A [Language Server Protocol](https://microsoft.github.io/language-server-protocol/) server over stdin/stdout for
`.aspx` / `.ascx` / `.master` (and `.vb`, for formatting and unsaved code-behind edits). It uses the same parser and
control metadata as `webforms designer`, so it knows the real type of every control.

**Go to definition** from markup:

| Cursor on | Goes to |
|---|---|
| an event attribute, `OnClick="btnSave_Click"` | the `Sub btnSave_Click` in the code-behind (only real events from the control's metadata, not `OnClientClick`) |
| a control `ID="btnSave"` | the field in the designer file (or the code-behind), then the subs with `Handles btnSave.Click`, then every use in the code-behind; inside a repeating template, `FindControl("btnSave")` |
| `ControlToValidate`, `AssociatedControlID`, `ControlID`, `TargetControlID`, `DefaultButton`, … | the control with that ID |
| `ContentPlaceHolderID` on `asp:Content` | the `ContentPlaceHolder` in the master page |
| a user control tag (`<uc:Widget`), `Register Src`, `MasterPageFile`, `CodeBehind`, `VirtualPath` | that file |
| `Inherits` | the class in the code-behind |

**Find references** on a control ID or a handler also lists the markup attributes that name it.

**Completion**:

| Typing | Offers |
|---|---|
| `<asp:Bu`, `<uc:` | the controls of every `Register` / web.config / built-in registration, and the page's user controls |
| `<` inside a control | what that control's parser accepts: inner properties (`<Columns>`, `<HeaderStyle>`, `<ItemTemplate>`), collection items (`asp:ListItem`, `asp:BoundField`), or controls inside templates and panels |
| an attribute name | settable properties, events as `On<Event>`, sub-properties (`Font-Bold`, `HeaderStyle-CssClass`); a user control's public properties and events |
| `OnClick="` | the code-behind Subs that can handle that event, `btnSave_Click` (ID_Event) first. `Private` / `Friend` Subs are left out: ASP.NET compiles the markup into a class that inherits the code-behind from another assembly. Parameterless Subs only with `completion.zeroArgHandlers`. |
| an enum or Boolean property value | its values (`TextMode="` → `SingleLine`, `MultiLine`, `Password`, …) |
| `ControlToValidate="`, `ContentPlaceHolderID="`, … | the IDs in the page / the master page's placeholders |
| `<%@ `, directive attributes and values | directives, attributes, `MasterPageFile` / `Register Src` paths, `Inherits` |
| `</` | the end tag of the innermost open server element (HTML end tags are left to the HTML server) |
| `<%$ ` | `AppSettings:` keys, `ConnectionStrings:` names, `Resources:` classes and keys, from the web.config files that apply |

Markup completion stays out of code blocks, comments, client `<script>` / `<style>` and plain text, so an HTML
server attached to the same buffers has those to itself. Handler items have `kind = Method`: turn off auto-brackets
for markup buffers if your completion plugin adds `()` to methods.

**VB in code blocks** (`<% %>`, `<%= %>`, `<%: %>`, `<%# %>`, `<%#: %>`, `<script runat="server">`): completion,
hover, go to definition, find references and compile errors (`BCxxxxx`), from a Roslyn compilation of the whole
project. Each markup file is projected into a hidden VB class that inherits the code-behind class, as ASP.NET
compiles the page; `<%# %>` in a template sees `Container` with its real type and, with `ItemType`, `Item` /
`BindItem`. `Eval("` offers the fields of the data source's item type when the code-behind binds the control to a
typed collection. The compilation is built in the background when a page with code blocks is opened (about a second
for a 500-file project), costs ~450 MB per project, and is dropped after 10 minutes without an open file of that
project. `CodeFile` pages (web site model) are not covered.

**Diagnostics** for every open markup file, on open, ~300 ms after an edit (to the markup or its code-behind) and
on save:

| Code | Severity | |
|---|---|---|
| `WF1xxx`, `WF2001`, `WF3xxx` | as the CLI | what `webforms designer` reports: directives, parse errors, unresolvable control types |
| `WF5001` | warning | `OnClick="x"` names a Sub the code-behind class doesn't declare (not reported when the class inherits a custom base class) |
| `WF5002` | error | the handler is `Private` or `Friend` (a runtime compile error) |
| `WF0100` | information | the designer file differs from the one the markup produces (saved files only) |
| `BCxxxxx` | as the compiler | VB errors inside code blocks |

**Generate designer**: the command `webforms.generateDesigner` (argument: the URI of the markup file or of its `.vb` /
`.designer.vb`), also a code action (and the quick fix for `WF0100`). It writes the designer exactly as
`webforms designer` would, from the open buffer. With `generateDesignerOnSave` it runs on every save.

**Formatting** of `.vb` files (`textDocument/formatting` / `rangeFormatting`), as `webforms format`, returned as edits
of the changed lines only so marks and folds stay put.

**Settings** (`initializationOptions`, then `workspace/didChangeConfiguration`, bare or under a `webforms` key):

| Setting | Default | |
|---|---|---|
| `generateDesignerOnSave` | `false` | regenerate the designer file whenever a markup file is saved |
| `completion.zeroArgHandlers` | `false` | also offer parameterless Subs as event handlers |
| `codeBlocks.enabled` | `true` | VB in code blocks (compiles each project in the server) |
| `diagnostics.staleDesigner` | `true` | report `WF0100` (the Claude Code plugin turns it off: its hook regenerates the designer right after each edit) |

The server keeps each project's metadata loaded and checks the files it came from (`.vbproj`, `bin\*.dll`, HintPath
assemblies, `web.config`s, user controls, master pages) before each request: ~150 ms for the first request in a
project, then a few ms. Project assemblies are read into memory, so the server never locks `bin\` while you build.
It writes nothing to stderr and no files apart from the designers you ask for and the `--log <file>` (shared
between several servers; each line starts with the process id).

## Other integrations

### MSBuild (fail the build on a stale designer)

In each `.vbproj` (or a `Directory.Build.targets`):

```xml
<Target Name="CheckWebFormsDesigners" BeforeTargets="BeforeBuild" Condition="'$(CheckDesigners)' != 'false'">
  <Exec Command="webforms designer --all &quot;$(MSBuildProjectDirectory)&quot; --check" />
</Target>
```

`Exec` surfaces the `file(line,col): error ...` lines as build errors. Use `--all` without `--check` to regenerate
instead.

### CI (GitHub Actions, Windows runner)

```yaml
- run: dotnet tool install -g Pharmica.WebForms.Tools
- run: webforms designer --all MySite --check
```

### Rider

*Settings → Tools → External Tools*: Program `webforms`, Arguments `designer $FilePath$`. Or *File Watchers* with
file type ASP.NET, same program and arguments, *Trigger on save*.

### VS Code task (without the extension)

```json
{
  "label": "webforms: designer (current file)",
  "type": "process",
  "command": "webforms",
  "args": ["designer", "${file}"],
  "problemMatcher": "$msCompile"
}
```

### Claude Code hooks (without the plugin)

The [Claude Code plugin](#claude-code) installs these itself. Without it, add them to the WebForms project's
`.claude/settings.json`:

```json
{
  "hooks": {
    "PreToolUse": [
      { "matcher": "Edit|Write|MultiEdit", "hooks": [{ "type": "command", "command": "webforms hook claude" }] }
    ],
    "PostToolUse": [
      { "matcher": "Edit|Write|MultiEdit", "hooks": [{ "type": "command", "command": "webforms hook claude" }] }
    ]
  }
}
```

### git pre-commit hook

`.git/hooks/pre-commit`:

```sh
#!/bin/sh
files=$(git diff --cached --name-only --diff-filter=ACM | grep -iE '\.(aspx|ascx|master)$')
[ -z "$files" ] && exit 0
webforms designer $files --check || {
  echo "Designer files are out of date: run 'webforms designer <file>' and stage the result." >&2
  exit 1
}
```

## Building from source

```powershell
dotnet build
dotnet test tests/WebForms.Tests
dotnet pack src/WebForms.Cli -c Release      # -> artifacts/nupkg/Pharmica.WebForms.Tools.<version>.nupkg
dotnet tool install -g Pharmica.WebForms.Tools --add-source ./artifacts/nupkg
```

The repository layout, design notes and regression checks are in
[CLAUDE.md](https://github.com/PharmicaUK/webforms-tools/blob/main/CLAUDE.md).

## License

[MIT](https://github.com/PharmicaUK/webforms-tools/blob/main/LICENSE)
