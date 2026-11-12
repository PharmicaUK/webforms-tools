using System.Text;
using System.Text.Json;
using WebForms.LanguageServer;
using WebForms.LanguageServer.Protocol;

namespace WebForms.Tests;

/// <summary>0.6.0: diagnostics, "Generate designer" (code action, command, on save) and VB formatting.</summary>
public sealed class DiagnosticsTests : IDisposable
{
    private const string DiagPage = """
        <%@ Page Language="vb" AutoEventWireup="false" CodeBehind="Diag.aspx.vb" Inherits="SampleApp.Diag" %>
        <form id="form1" runat="server">
            <asp:Button ID="btnOk" runat="server" OnClick="btnOk_Click" />
            <asp:Button ID="btnMissing" runat="server" OnClick=" btnMissing_Click" />
            <asp:Button ID="btnPrivate" runat="server" OnClick="btnPrivate_Click" />
            <asp:Button ID="btnBound" runat="server" OnClick="<%# Nothing %>" OnClientClick="notAHandler" />
            <x:Thing ID="thing1" runat="server" />
        </form>
        """;

    private const string DiagCodeBehind = """
        Public Class Diag
            Inherits System.Web.UI.Page

            Protected Sub btnOk_Click(sender As Object, e As EventArgs)
            End Sub

            Private Sub btnPrivate_Click(sender As Object, e As EventArgs)
            End Sub
        End Class
        """;

    private readonly string _dir;
    private readonly WebFormsWorkspace _workspace = new();

    public DiagnosticsTests()
    {
        _dir = Fixture.CopyToTemp();
        File.WriteAllText(Path.Combine(_dir, "Diag.aspx"), DiagPage);
        File.WriteAllText(Path.Combine(_dir, "Diag.aspx.vb"), DiagCodeBehind);
    }

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void Designer_and_handler_diagnostics()
    {
        var page = Path.Combine(_dir, "Diag.aspx");
        var diagnostics = new MarkupDiagnostics(_workspace).Compute(page);
        var doc = new SourceDocument(page, DiagPage, null);

        Assert.All(diagnostics, d => Assert.Equal("webforms", d.Source));
        Assert.Equal(["WF5001", "WF5002", "WF3001"], diagnostics.Select(d => d.Code));

        var missing = diagnostics[0];
        Assert.Equal(DiagnosticSeverity.Warning, missing.Severity);
        Assert.Equal(doc.ToRange(DiagPage.IndexOf("btnMissing_Click", StringComparison.Ordinal), "btnMissing_Click".Length), missing.Range);
        Assert.Contains("'btnMissing_Click'", missing.Message, StringComparison.Ordinal);

        var inaccessible = diagnostics[1];
        Assert.Equal(DiagnosticSeverity.Error, inaccessible.Severity);
        Assert.Equal(doc.ToRange(DiagPage.IndexOf("btnPrivate_Click", StringComparison.Ordinal), "btnPrivate_Click".Length), inaccessible.Range);

        // The unknown tag prefix: the CLI's code and severity, on the tag name.
        var unknown = diagnostics[2];
        Assert.Equal(DiagnosticSeverity.Error, unknown.Severity);
        Assert.Equal(doc.ToRange(DiagPage.IndexOf("x:Thing", StringComparison.Ordinal), "x:Thing".Length), unknown.Range);

        // Markup with errors produces no designer, so there is no "out of date" diagnostic either.
        Assert.DoesNotContain(diagnostics, d => d.Code == MarkupDiagnostics.StaleDesignerCode);
    }

    [Fact]
    public void A_custom_base_class_may_declare_the_handler()
    {
        File.WriteAllText(Path.Combine(_dir, "Diag.aspx.vb"), DiagCodeBehind.Replace("System.Web.UI.Page", "BasePage", StringComparison.Ordinal));
        var codes = new MarkupDiagnostics(_workspace).Compute(Path.Combine(_dir, "Diag.aspx")).Select(d => d.Code);
        Assert.Equal(["WF5002", "WF3001"], codes);
    }

    [Fact]
    public void Stale_designer_is_reported_only_for_saved_markup_and_generate_fixes_it()
    {
        var page = Path.Combine(_dir, "Orders.aspx");
        var designer = page + ".designer.vb";
        var diagnostics = new MarkupDiagnostics(_workspace);
        Assert.Empty(diagnostics.Compute(page)); // the checked-in designer is up to date

        var text = File.ReadAllText(page).Replace("</asp:Content>", "    <asp:Label ID=\"lblNew\" runat=\"server\" />\r\n</asp:Content>", StringComparison.Ordinal);
        File.WriteAllText(page, text);
        var stale = Assert.Single(diagnostics.Compute(page));
        Assert.Equal((MarkupDiagnostics.StaleDesignerCode, DiagnosticSeverity.Information), (stale.Code, stale.Severity));
        diagnostics.StaleDesigner = false; // diagnostics.staleDesigner: the Claude Code plugin's hook regenerates instead
        Assert.Empty(diagnostics.Compute(page));
        diagnostics.StaleDesigner = true;
        Assert.Equal(0, stale.Range.Start.Line);
        Assert.Equal("Page", text.Substring(stale.Range.Start.Character, stale.Range.End.Character - stale.Range.Start.Character));

        // An open buffer that matches the file (line endings aside) is saved; an edited one is ahead of its designer.
        _workspace.Open(page, text.Replace("\r\n", "\n", StringComparison.Ordinal), 1);
        Assert.Single(diagnostics.Compute(page));
        _workspace.Change(page, text + " ", 2);
        Assert.Empty(diagnostics.Compute(page));
        _workspace.Change(page, text, 3);

        var before = File.GetLastWriteTimeUtc(designer);
        var written = DesignerFiles.Generate(_workspace, page);
        Assert.Equal(DesignerOutcome.Written, written.Outcome);
        Assert.Contains("lblNew", File.ReadAllText(designer), StringComparison.Ordinal);
        Assert.Empty(diagnostics.Compute(page));

        Assert.Equal(DesignerOutcome.Unchanged, DesignerFiles.Generate(_workspace, page).Outcome);
        Assert.Equal(DesignerOutcome.Errors, DesignerFiles.Generate(_workspace, Path.Combine(_dir, "Diag.aspx")).Outcome);
        Assert.Equal(DesignerOutcome.Skipped, DesignerFiles.Generate(_workspace, Path.Combine(_dir, "WebSite.aspx")).Outcome);
        Assert.True(File.GetLastWriteTimeUtc(designer) >= before);
    }

    [Theory]
    [InlineData("a\nb\nc\n", "a\nb\nc\n", 0)]
    [InlineData("a\nb\nc\n", "a\nB\nc\n", 1)]
    [InlineData("a\nb\nc\nd\ne\n", "A\nb\nc\nd\nE\n", 2)]
    [InlineData("a\nb\nc\n", "a\nc\n", 1)]
    [InlineData("a\nc\n", "a\nb\nc\n", 1)]
    [InlineData("a\r\nb\r\nc", "a\r\nb\r\nC", 1)]
    [InlineData("x\ny", "x\ny\nz\n", 1)]
    [InlineData("", "a\n", 1)]
    [InlineData("a\nb\n", "", 1)]
    [InlineData("1\n2\n3\n4\n5\n6\n", "0\n1\n3\n4\nx\n6\n7\n", 4)]
    public void Line_diff_edits_turn_the_old_text_into_the_new(string oldText, string newText, int edits)
    {
        var doc = new SourceDocument("x.vb", oldText, 1);
        var result = LineDiff.Edits(doc, newText);
        Assert.Equal(edits, result.Length);
        Assert.Equal(newText, Apply(doc, result));
    }

    [Fact]
    public void Line_diff_of_a_large_reformat()
    {
        var random = new Random(42);
        var lines = Enumerable.Range(0, 3000).Select(i => $"line {i}\r\n").ToList();
        var changed = lines.Select(l => random.Next(10) == 0 ? "    " + l : l).Where(_ => random.Next(50) != 0).ToList();
        var doc = new SourceDocument("x.vb", string.Concat(lines), 1);
        var edits = LineDiff.Edits(doc, string.Concat(changed));
        Assert.Equal(string.Concat(changed), Apply(doc, edits));
        Assert.True(edits.Length > 100, $"{edits.Length} edits");
    }

    [Fact]
    public async Task Diagnostics_code_action_and_generate_designer_over_the_protocol()
    {
        var page = Path.Combine(_dir, "Orders.aspx");
        var designer = page + ".designer.vb";
        var codeBehindUri = JsonSerializer.Serialize(DocumentUri.FromPath(page + ".vb"));
        var text = File.ReadAllText(page).Replace("</asp:Content>", "    <asp:Label ID=\"lblNew\" runat=\"server\" />\r\n</asp:Content>", StringComparison.Ordinal);
        File.WriteAllText(page, text);
        var uri = JsonSerializer.Serialize(DocumentUri.FromPath(page));
        var edited = text.Replace("lblNew", "lblNewer", StringComparison.Ordinal);

        var input = new MemoryStream();
        Frame(input, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"processId":null,"capabilities":{"textDocument":{"codeAction":{"codeActionLiteralSupport":{"codeActionKind":{"valueSet":["quickfix","source"]}}},"formatting":{"dynamicRegistration":true},"rangeFormatting":{"dynamicRegistration":true}},"workspace":{"didChangeWatchedFiles":{"dynamicRegistration":true}}}}}""");
        Frame(input, """{"jsonrpc":"2.0","method":"initialized","params":{}}""");
        Frame(input, """{"jsonrpc":"2.0","id":"webforms-1","result":null}"""); // the client's answer to registerCapability
        Frame(input, """{"jsonrpc":"2.0","method":"textDocument/didOpen","params":{"textDocument":{"uri":""" + uri + ""","languageId":"aspx","version":1,"text":""" + JsonSerializer.Serialize(text) + "}}}");
        Frame(input, """{"jsonrpc":"2.0","id":2,"method":"textDocument/codeAction","params":{"textDocument":{"uri":""" + uri + """},"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":0}},"context":{"diagnostics":[{"range":{"start":{"line":0,"character":4},"end":{"line":0,"character":8}},"severity":3,"code":"WF0100","source":"webforms","message":"Designer file is out of date."}]}}}""");
        Frame(input, """{"jsonrpc":"2.0","id":3,"method":"textDocument/codeAction","params":{"textDocument":{"uri":""" + codeBehindUri + """},"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":0}},"context":{"diagnostics":[],"only":["refactor"]}}}""");
        Frame(input, """{"jsonrpc":"2.0","id":4,"method":"workspace/executeCommand","params":{"command":"webforms.generateDesigner","arguments":[""" + uri + "]}}");
        // Edit, then save with generateDesignerOnSave on.
        Frame(input, """{"jsonrpc":"2.0","method":"workspace/didChangeConfiguration","params":{"settings":{"webforms":{"generateDesignerOnSave":true}}}}""");
        Frame(input, """{"jsonrpc":"2.0","method":"textDocument/didChange","params":{"textDocument":{"uri":""" + uri + ""","version":2},"contentChanges":[{"text":""" + JsonSerializer.Serialize(edited) + "}]}}");
        Frame(input, """{"jsonrpc":"2.0","method":"textDocument/didSave","params":{"textDocument":{"uri":""" + uri + "}}}");
        Frame(input, """{"jsonrpc":"2.0","method":"textDocument/didClose","params":{"textDocument":{"uri":""" + uri + "}}}");
        Frame(input, """{"jsonrpc":"2.0","id":9,"method":"shutdown"}""");
        Frame(input, """{"jsonrpc":"2.0","method":"exit"}""");
        input.Position = 0;

        var output = new MemoryStream();
        using (var server = new WebFormsLanguageServer(input, output, diagnosticsDelay: TimeSpan.Zero))
        {
            Assert.Equal(0, await server.RunAsync());
        }

        var frames = ReadFrames(output.ToArray()).ToList();
        var responses = frames.Where(f => f.TryGetProperty("id", out _) && !f.TryGetProperty("method", out _)).ToDictionary(f => f.GetProperty("id").ToString());

        // Capabilities: save notifications, formatting registered dynamically for .vb only, the command.
        var caps = responses["1"].GetProperty("result").GetProperty("capabilities");
        Assert.False(caps.GetProperty("textDocumentSync").GetProperty("save").GetProperty("includeText").GetBoolean());
        Assert.False(caps.TryGetProperty("documentFormattingProvider", out _));
        Assert.Equal("webforms.generateDesigner", caps.GetProperty("executeCommandProvider").GetProperty("commands")[0].GetString());
        var register = Assert.Single(frames, f => f.TryGetProperty("method", out var m) && m.GetString() == "client/registerCapability");
        var registrations = register.GetProperty("params").GetProperty("registrations").EnumerateArray().ToList();
        Assert.Equal(["textDocument/formatting", "textDocument/rangeFormatting", "workspace/didChangeWatchedFiles"], registrations.Select(r => r.GetProperty("method").GetString()));
        Assert.Equal("**/*.vb", registrations[0].GetProperty("registerOptions").GetProperty("documentSelector")[0].GetProperty("pattern").GetString());

        // Diagnostics: stale on open; cleared by the command; the edit makes the buffer unsaved (nothing new to say);
        // the save regenerates the designer; closing clears.
        var published = frames.Where(f => f.TryGetProperty("method", out var m) && m.GetString() == "textDocument/publishDiagnostics")
            .Select(f => f.GetProperty("params").GetProperty("diagnostics").EnumerateArray().Select(d => d.GetProperty("code").GetString()).ToList())
            .ToList();
        Assert.Equal([["WF0100"], [], []], published);

        var action = Assert.Single(responses["2"].GetProperty("result").EnumerateArray().ToList());
        Assert.Equal("Generate designer", action.GetProperty("title").GetString());
        Assert.Equal("quickfix", action.GetProperty("kind").GetString());
        Assert.Equal("webforms.generateDesigner", action.GetProperty("command").GetProperty("command").GetString());
        Assert.Equal(DocumentUri.FromPath(page), action.GetProperty("command").GetProperty("arguments")[0].GetString());
        Assert.Empty(responses["3"].GetProperty("result").EnumerateArray()); // only refactorings asked for

        Assert.Equal("Orders.aspx.designer.vb written.", responses["4"].GetProperty("result").GetString());
        var shown = frames.Where(f => f.TryGetProperty("method", out var m) && m.GetString() == "window/showMessage").ToList();
        Assert.Single(shown); // the command; saving is silent
        Assert.Contains("lblNewer", File.ReadAllText(designer), StringComparison.Ordinal);
        Assert.DoesNotContain("lblNew ", File.ReadAllText(designer), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Formatting_vb_only_as_minimal_edits_and_errors_for_syntax_errors()
    {
        var vb = Path.Combine(_dir, "Format.vb");
        const string Source = "Public Class Format\r\nSub Foo()\r\ndim x as integer = 1\r\nEnd Sub\r\nEnd Class\r\n";
        const string Broken = "Public Class Broken\r\nSub Foo()\r\nIf True Then\r\nEnd Sub\r\nEnd Class\r\n";
        File.WriteAllText(vb, Source);
        var vbUri = JsonSerializer.Serialize(DocumentUri.FromPath(vb));
        var aspxUri = JsonSerializer.Serialize(DocumentUri.FromPath(Path.Combine(_dir, "Orders.aspx")));

        var input = new MemoryStream();
        Frame(input, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"processId":null,"capabilities":{}}}""");
        Frame(input, """{"jsonrpc":"2.0","method":"initialized","params":{}}""");
        Frame(input, """{"jsonrpc":"2.0","id":2,"method":"textDocument/formatting","params":{"textDocument":{"uri":""" + vbUri + """},"options":{"tabSize":4,"insertSpaces":true}}}""");
        Frame(input, """{"jsonrpc":"2.0","id":3,"method":"textDocument/rangeFormatting","params":{"textDocument":{"uri":""" + vbUri + """},"range":{"start":{"line":2,"character":0},"end":{"line":3,"character":0}},"options":{"tabSize":4,"insertSpaces":true}}}""");
        Frame(input, """{"jsonrpc":"2.0","id":4,"method":"textDocument/formatting","params":{"textDocument":{"uri":""" + aspxUri + """},"options":{"tabSize":4,"insertSpaces":true}}}""");
        Frame(input, """{"jsonrpc":"2.0","method":"textDocument/didOpen","params":{"textDocument":{"uri":""" + vbUri + ""","languageId":"vbnet","version":1,"text":""" + JsonSerializer.Serialize(Broken) + "}}}");
        Frame(input, """{"jsonrpc":"2.0","id":5,"method":"textDocument/formatting","params":{"textDocument":{"uri":""" + vbUri + """},"options":{"tabSize":4,"insertSpaces":true}}}""");
        Frame(input, """{"jsonrpc":"2.0","id":6,"method":"shutdown"}""");
        Frame(input, """{"jsonrpc":"2.0","method":"exit"}""");
        input.Position = 0;

        var output = new MemoryStream();
        using (var server = new WebFormsLanguageServer(input, output, diagnosticsDelay: TimeSpan.Zero))
        {
            Assert.Equal(0, await server.RunAsync());
        }

        var frames = ReadFrames(output.ToArray()).ToList();
        var responses = frames.Where(f => f.TryGetProperty("id", out _)).ToDictionary(f => f.GetProperty("id").ToString());
        var caps = responses["1"].GetProperty("result").GetProperty("capabilities");
        Assert.True(caps.GetProperty("documentFormattingProvider").GetBoolean()); // static: no dynamic registration
        Assert.True(caps.GetProperty("codeActionProvider").GetBoolean()); // no code action literals: bare commands
        Assert.DoesNotContain(frames, f => f.TryGetProperty("method", out _));

        var doc = new SourceDocument(vb, Source, null);
        var whole = Edits(responses["2"]);
        Assert.Equal("Public Class Format\r\n    Sub Foo()\r\n        Dim x As Integer = 1\r\n    End Sub\r\nEnd Class\r\n", Apply(doc, whole));
        var edit = Assert.Single(whole); // lines 2-4 (adjacent: one edit); the first and last lines are left alone
        Assert.Equal(new LspRange(new LspPosition(1, 0), new LspPosition(4, 0)), edit.Range);

        var ranged = Assert.Single(Edits(responses["3"]));
        Assert.Equal("        Dim x As Integer = 1\r\n", ranged.NewText);

        Assert.Equal(JsonValueKind.Null, responses["4"].GetProperty("result").ValueKind);

        var error = responses["5"].GetProperty("error");
        Assert.Equal(ErrorCodes.RequestFailed, error.GetProperty("code").GetInt32());
        Assert.StartsWith("BC30081 at line 3: ", error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_read_generate_designer_on_save()
    {
        var json = JsonDocument.Parse("""{"webforms":{"generateDesignerOnSave":true}}""").RootElement;
        var settings = ServerSettings.Read(json, new ServerSettings(ZeroArgHandlers: true));
        Assert.Equal(new ServerSettings(ZeroArgHandlers: true, GenerateDesignerOnSave: true), settings);

        var off = JsonDocument.Parse("""{"diagnostics":{"staleDesigner":false}}""").RootElement;
        Assert.False(ServerSettings.Read(off, settings).StaleDesignerDiagnostic);
    }

    private static TextEdit[] Edits(JsonElement response) =>
        response.GetProperty("result").Deserialize(LspJsonContext.Default.TextEditArray)!;

    private static string Apply(SourceDocument doc, IEnumerable<TextEdit> edits)
    {
        var text = doc.Text;
        foreach (var e in edits.OrderByDescending(e => doc.ToOffset(e.Range.Start)))
        {
            int start = doc.ToOffset(e.Range.Start), end = doc.ToOffset(e.Range.End);
            text = string.Concat(text.AsSpan(0, start), e.NewText, text.AsSpan(end));
        }

        return text;
    }

    private static void Frame(Stream s, string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        s.Write(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
        s.Write(body);
    }

    private static IEnumerable<JsonElement> ReadFrames(byte[] data)
    {
        int pos = 0;
        while (pos < data.Length)
        {
            int headerEnd = data.AsSpan(pos).IndexOf("\r\n\r\n"u8);
            var header = Encoding.ASCII.GetString(data, pos, headerEnd);
            int length = int.Parse(header["Content-Length: ".Length..], System.Globalization.CultureInfo.InvariantCulture);
            pos += headerEnd + 4;
            yield return JsonDocument.Parse(data.AsMemory(pos, length)).RootElement.Clone();
            pos += length;
        }
    }
}
