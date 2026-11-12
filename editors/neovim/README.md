# webforms in Neovim

Requires Neovim 0.11+ (`vim.lsp.config`) and the `webforms` tool on `PATH` (see the
[main README](../../README.md#install)).

## Language server

```lua
-- Stock Neovim has no ASP.NET filetype (an .aspx can even be detected as "mason", HTML::Mason).
vim.filetype.add({ extension = { aspx = "aspx", ascx = "aspx", master = "aspx", Master = "aspx" } })

vim.lsp.config("webforms", {
  cmd = { "webforms", "lsp" },
  -- Optional: { "webforms", "lsp", "--log", vim.fn.stdpath("log") .. "/webforms.log" }
  -- VB buffers too ("vb" is Neovim's name, "vbnet" a common custom one), so that unsaved code-behind edits are
  -- seen. Markup requests are answered for markup files; formatting for .vb files.
  filetypes = { "aspx", "vb", "vbnet" },
  -- root_markers takes names, not patterns, hence a function for *.vbproj.
  root_dir = function(bufnr, on_dir)
    on_dir(vim.fs.root(bufnr, function(name) return name:match("%.vbproj$") ~= nil end)
      or vim.fs.root(bufnr, { "web.config", ".git" }))
  end,
  init_options = {
    generateDesignerOnSave = true,
    -- completion = { zeroArgHandlers = true },  -- also offer parameterless Subs as event handlers
    -- codeBlocks = { enabled = false },         -- no VB compilation in the server (saves ~450 MB per project)
  },
})
vim.lsp.enable("webforms")
```

With Neovim's default LSP keymaps:

| | |
|---|---|
| `CTRL-]` (tagfunc), or `vim.lsp.buf.definition()` (map `gd` to it if you like) | go to the handler / designer field / user control / master page under the cursor. With several results (an ID with uses in the code-behind) Neovim shows a list, definition first. |
| `grr` | references |
| `gra` | code actions: *Generate designer* (markup and VB buffers) |
| `K` | hover in code blocks |
| `vim.lsp.buf.format({ name = "webforms" })` | format a VB buffer through the server |

Diagnostics show up as usual (`source = "webforms"`). Neovim reports anything a server writes to stderr as an error,
so the server writes nothing there; use `--log` to see what it does.

Completion plugins: handler items have `kind = Method`, so plugins that add brackets to accepted methods
(blink.cmp's auto-brackets, for one) insert `btnSave_Click()`; turn that off for `aspx` buffers.

## VB formatting with conform.nvim

The server formats VB too (above). To use the CLI through [conform.nvim](https://github.com/stevearc/conform.nvim)
instead (whole buffer, and ranges for `conform.format({ range = ... })` / visual-mode formatting):

```lua
require("conform").setup({
  formatters = {
    webforms_vb = {
      command = "webforms",
      args = { "format", "--stdin-filename", "$FILENAME" },
      range_args = function(_, ctx)
        return { "format", "--stdin-filename", ctx.filename,
                 "--lines", ctx.range.start[1] .. "-" .. ctx.range["end"][1] }
      end,
      stdin = true,
    },
  },
  formatters_by_ft = { vb = { "webforms_vb" } },
  format_on_save = { timeout_ms = 2000 },
})
```

On syntax errors the formatter exits 2 and conform leaves the buffer alone; the `BC` errors are in `:ConformInfo`.

## Designer on save without the server

```lua
vim.api.nvim_create_autocmd("BufWritePost", {
  pattern = { "*.aspx", "*.ascx", "*.master", "*.Master" },
  callback = function(args)
    local file = vim.api.nvim_buf_get_name(args.buf)
    vim.system({ "webforms", "designer", file }, { text = true }, function(res)
      vim.schedule(function()
        if res.code >= 2 then
          vim.fn.setqflist({}, "r", {
            title = "webforms designer",
            lines = vim.split(res.stderr, "\n", { trimempty = true }),
            efm = "%f(%l\\,%c): %trror %m,%f(%l\\,%c): %tarning %m",
          })
          vim.notify("webforms designer failed (see quickfix)", vim.log.levels.ERROR)
        end
      end)
    end)
  end,
})
```
