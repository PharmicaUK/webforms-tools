using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using WebForms.Designer;
using WebForms.LanguageServer;
using WebForms.LanguageServer.Protocol;

namespace WebForms.Tests;

public sealed class LanguageServerTests : IDisposable
{
    private const string Page = """
        <%@ Page Language="vb" AutoEventWireup="false" MasterPageFile="~/Site.Master" CodeBehind="Lsp.aspx.vb" Inherits="SampleApp.Lsp" %>
        <%@ Register Src="~/Controls/Widget.ascx" TagPrefix="uc" TagName="Widget" %>
        <asp:Content ID="c1" ContentPlaceHolderID="Main" runat="server">
            <asp:Label ID="lblName" runat="server" AssociatedControlID="txtName" />
            <asp:TextBox ID="txtName" runat="server" OnTextChanged="txtName_Changed" />
            <asp:RequiredFieldValidator ID="rfvName" runat="server" ControlToValidate="txtName" />
            <asp:Button ID="btnSave" runat="server" OnClick="btnSave_Click" OnClientClick="return true;" />
            <button id="btnHtml" runat="server" onserverclick="btnHtml_Click">x</button>
            <asp:Repeater ID="rptRows" runat="server"><ItemTemplate><asp:Label ID="lblRow" runat="server" /><asp:Label ID="lblUnused" runat="server" /></ItemTemplate></asp:Repeater>
            <uc:Widget ID="widget1" runat="server" />
            <x:Footer ID="footer2" runat="server" />
        </asp:Content>
        """;

    private const string CodeBehind = """
        Public Class Lsp
            Inherits System.Web.UI.Page

            Protected Sub btnSave_Click(sender As Object, e As EventArgs)
                txtName.Text = ""
                Me.txtName.Focus()
                Dim other As New Lsp()
                other.txtName.Text = "x"
            End Sub

            Protected Sub txtName_Changed(sender As Object, e As EventArgs) Handles txtName.TextChanged
            End Sub

            Private Sub rptRows_ItemDataBound(sender As Object, e As RepeaterItemEventArgs) Handles rptRows.ItemDataBound
                Dim lbl = CType(e.Item.FindControl("lblRow"), Label)
            End Sub
        End Class
        """;

    private readonly string _dir;
    private readonly string _page;
    private readonly string _codeBehind;
    private readonly WebFormsWorkspace _workspace = new();
    private readonly Navigator _navigator;

    public LanguageServerTests()
    {
        _dir = Fixture.CopyToTemp();
        _page = Path.Combine(_dir, "Lsp.aspx");
        _codeBehind = _page + ".vb";
        File.WriteAllText(_page, Page);
        File.WriteAllText(_codeBehind, CodeBehind);

        // A real designer file, as `webforms designer` writes it (x:Footer has an unknown prefix: an error for that
        // control only, the model is still complete for the others).
        using var project = ProjectContext.Create(_dir, Path.Combine(_dir, "SampleApp.vbproj"));
        var result = DesignerGenerator.Generate(_page, Page, project);
        File.WriteAllBytes(result.DesignerPath!, DesignerOutput.Build(result.Model!, null, new OutputOptions()));

        _navigator = new Navigator(_workspace);
    }

    public void Dispose() => _workspace.Dispose();

    [Theory]
    [InlineData("file:///C:/web/App/Default.aspx")] // Neovim
    [InlineData("file:///c%3A/web/App/Default.aspx")] // VS Code escapes the drive colon
    public void Document_uris_become_paths(string uri)
    {
        Assert.Equal(@"c:\web\app\default.aspx", DocumentUri.ToPath(uri)!.ToLowerInvariant());
    }

    [Fact]
    public void Uris_go_back_as_the_client_sent_them()
    {
        // The path may not round-trip (GetFullPath expands 8.3 names such as RUNNER~1), so the client's own URI is
        // what diagnostics and locations for that file carry.
        const string uri = "file:///c%3A/Users/RUNNER~1/AppData/Local/Temp/Some%20Page.aspx";
        var path = DocumentUri.ToPath(uri)!;
        Assert.Equal(uri, DocumentUri.FromPath(path));
        Assert.Equal("file:///C:/never/sent.aspx", DocumentUri.FromPath(@"C:\never\sent.aspx"));
    }

    [Fact]
    public void Event_attribute_goes_to_the_handler()
    {
        var locations = _navigator.Definition(_page, At(Page, "btnSave_Click\""));
        var loc = Assert.Single(locations);
        Assert.EndsWith("Lsp.aspx.vb", loc.Uri, StringComparison.Ordinal);
        Assert.Equal(Line(CodeBehind, "Protected Sub btnSave_Click"), loc.Range.Start.Line);
        Assert.Equal(CodeBehind.Split('\n')[loc.Range.Start.Line].IndexOf("btnSave_Click", StringComparison.Ordinal), loc.Range.Start.Character);

        // OnClientClick is a property (Button has no ClientClick event), not a handler.
        Assert.Empty(_navigator.Definition(_page, At(Page, "return true")));
    }

    [Fact]
    public void Html_control_server_events_come_from_metadata()
    {
        var analysis = _workspace.Analyze(_page)!;
        var target = Navigator.TargetAt(analysis, Page.IndexOf("btnHtml_Click", StringComparison.Ordinal));
        Assert.Equal(new HandlerTarget("btnHtml_Click"), target);
    }

    [Fact]
    public void Control_id_goes_to_designer_field_then_handlers_then_uses()
    {
        var locations = _navigator.Definition(_page, At(Page, "txtName\" runat"));
        Assert.Equal(4, locations.Count);
        Assert.EndsWith("Lsp.aspx.designer.vb", locations[0].Uri, StringComparison.Ordinal);
        Assert.Equal(Line(CodeBehind, "Handles txtName.TextChanged"), locations[1].Range.Start.Line);
        Assert.Equal(Line(CodeBehind, "txtName.Text = \"\""), locations[2].Range.Start.Line);
        Assert.Equal(Line(CodeBehind, "Me.txtName.Focus()"), locations[3].Range.Start.Line);
        // other.txtName is a member of something else.
        Assert.DoesNotContain(locations, l => l.Range.Start.Line == Line(CodeBehind, "other.txtName"));
    }

    [Fact]
    public void Control_in_a_template_goes_to_FindControl()
    {
        var loc = Assert.Single(_navigator.Definition(_page, At(Page, "lblRow")));
        Assert.EndsWith("Lsp.aspx.vb", loc.Uri, StringComparison.Ordinal);
        Assert.Equal(Line(CodeBehind, "FindControl(\"lblRow\")"), loc.Range.Start.Line);
    }

    [Fact]
    public void Control_with_no_field_or_use_goes_to_its_own_ID()
    {
        var loc = Assert.Single(_navigator.Definition(_page, At(Page, "lblUnused")));
        Assert.EndsWith("Lsp.aspx", loc.Uri, StringComparison.Ordinal);
        Assert.Equal(new LspPosition(Line(Page, "lblUnused"), Column(Page, "lblUnused")), loc.Range.Start);
    }

    [Fact]
    public void Log_file_can_be_shared_by_two_servers()
    {
        string log = Path.Combine(_dir, "shared.log");
        using (var first = WebForms.Cli.SharedLogWriter.TryOpen(log)!)
        using (var second = WebForms.Cli.SharedLogWriter.TryOpen(log)!)
        {
            first.WriteLine("one");
            first.Flush();
            second.WriteLine("two");
            second.Flush();
            first.WriteLine("three");
            first.Flush();
        }

        string prefix = $"[{Environment.ProcessId}] ";
        Assert.Equal([prefix + "one", prefix + "two", prefix + "three"], File.ReadAllLines(log));
    }

    [Fact]
    public void Control_reference_attribute_goes_to_the_control_in_markup()
    {
        var loc = Assert.Single(_navigator.Definition(_page, At(Page, "txtName\" />")));
        Assert.EndsWith("Lsp.aspx", loc.Uri, StringComparison.Ordinal);
        Assert.Equal(new LspPosition(Line(Page, "<asp:TextBox"), Column(Page, "txtName\" runat")), loc.Range.Start);
    }

    [Fact]
    public void References_include_markup_designer_and_code_behind()
    {
        var all = _navigator.References(_page, At(Page, "txtName\" runat"), includeDeclaration: true);
        Assert.Contains(all, l => l.Uri.EndsWith("Lsp.aspx", StringComparison.Ordinal) && l.Range.Start.Line == Line(Page, "AssociatedControlID"));
        Assert.Contains(all, l => l.Uri.EndsWith("Lsp.aspx", StringComparison.Ordinal) && l.Range.Start.Line == Line(Page, "ControlToValidate"));
        Assert.Contains(all, l => l.Uri.EndsWith("Lsp.aspx", StringComparison.Ordinal) && l.Range.Start.Line == Line(Page, "<asp:TextBox"));
        Assert.Contains(all, l => l.Uri.EndsWith(".designer.vb", StringComparison.Ordinal));
        Assert.Equal(7, all.Count); // markup ID, designer field, Handles, 2 uses, 2 markup references

        var noDecl = _navigator.References(_page, At(Page, "txtName\" runat"), includeDeclaration: false);
        Assert.Equal(5, noDecl.Count); // 3 code-behind + 2 markup references
    }

    [Fact]
    public void Files_classes_and_placeholders()
    {
        Assert.EndsWith("Controls/Widget.ascx", Assert.Single(_navigator.Definition(_page, At(Page, "uc:Widget ID"))).Uri, StringComparison.Ordinal);
        Assert.EndsWith("Controls/Widget.ascx", Assert.Single(_navigator.Definition(_page, At(Page, "Controls/Widget.ascx\""))).Uri, StringComparison.Ordinal);
        Assert.EndsWith("Site.Master", Assert.Single(_navigator.Definition(_page, At(Page, "Site.Master\""))).Uri, StringComparison.Ordinal);

        var cls = Assert.Single(_navigator.Definition(_page, At(Page, "SampleApp.Lsp")));
        Assert.Equal(new LspPosition(0, "Public Class ".Length), cls.Range.Start);

        var placeholder = Assert.Single(_navigator.Definition(_page, At(Page, "Main\"")));
        Assert.EndsWith("Site.Master", placeholder.Uri, StringComparison.Ordinal);
        Assert.Equal(2, placeholder.Range.Start.Line);
    }

    [Fact]
    public void Open_buffers_override_files_on_disk()
    {
        var edited = CodeBehind.Replace("    Protected Sub btnSave_Click", "\n\n    Protected Sub btnSave_Click", StringComparison.Ordinal);
        _workspace.Open(_codeBehind, edited, 1);
        var loc = Assert.Single(_navigator.Definition(_page, At(Page, "btnSave_Click\"")));
        Assert.Equal(Line(edited, "Protected Sub btnSave_Click"), loc.Range.Start.Line);

        _workspace.Close(_codeBehind);
        loc = Assert.Single(_navigator.Definition(_page, At(Page, "btnSave_Click\"")));
        Assert.Equal(Line(CodeBehind, "Protected Sub btnSave_Click"), loc.Range.Start.Line);
    }

    [Fact]
    public void Web_config_changes_are_picked_up()
    {
        int footerTag = Page.IndexOf("x:Footer", StringComparison.Ordinal) + 1;
        Assert.Null(Navigator.TargetAt(_workspace.Analyze(_page)!, footerTag)); // unknown prefix

        var config = Path.Combine(_dir, "web.config");
        File.WriteAllText(config, File.ReadAllText(config).Replace(
            "</controls>", "  <add tagPrefix=\"x\" tagName=\"Footer\" src=\"~/Controls/Footer.ascx\" />\n      </controls>", StringComparison.Ordinal));
        File.SetLastWriteTimeUtc(config, DateTime.UtcNow.AddSeconds(5));

        var target = Assert.IsType<FileTarget>(Navigator.TargetAt(_workspace.Analyze(_page)!, footerTag));
        Assert.EndsWith(Path.Combine("Controls", "Footer.ascx"), target.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_assemblies_are_not_locked_and_are_reloaded_when_rebuilt()
    {
        var bin = Directory.CreateDirectory(Path.Combine(_dir, "bin")).FullName;
        var dll = Path.Combine(bin, "Acme.Controls.dll");
        CompileControlAssembly(dll, "Spin");

        var config = Path.Combine(_dir, "web.config");
        File.WriteAllText(config, File.ReadAllText(config).Replace(
            "</controls>", "  <add tagPrefix=\"acme\" namespace=\"Acme.Controls\" assembly=\"Acme.Controls\" />\n      </controls>", StringComparison.Ordinal));
        const string markup = """
            <%@ Page Language="vb" CodeBehind="Acme.aspx.vb" Inherits="SampleApp.Acme" %>
            <form id="form1" runat="server"><acme:Gauge ID="g1" runat="server" OnSpin="g1_Spin" OnTurn="g1_Turn" /></form>
            """;
        var page = Path.Combine(_dir, "Acme.aspx");
        File.WriteAllText(page, markup);
        int spin = markup.IndexOf("g1_Spin", StringComparison.Ordinal);
        int turn = markup.IndexOf("g1_Turn", StringComparison.Ordinal);

        Assert.Equal(new HandlerTarget("g1_Spin"), Navigator.TargetAt(_workspace.Analyze(page)!, spin));
        Assert.Null(Navigator.TargetAt(_workspace.Analyze(page)!, turn));

        // A build replaces the assembly while the server holds its metadata: the file must not be locked.
        CompileControlAssembly(dll, "Turn");
        File.SetLastWriteTimeUtc(dll, DateTime.UtcNow.AddSeconds(5));

        Assert.Null(Navigator.TargetAt(_workspace.Analyze(page)!, spin));
        Assert.Equal(new HandlerTarget("g1_Turn"), Navigator.TargetAt(_workspace.Analyze(page)!, turn));
    }

    [Fact]
    public async Task Protocol_session_end_to_end()
    {
        var defaultPage = Fixture.Path_("Default.aspx");
        var text = File.ReadAllText(defaultPage);
        var uri = DocumentUri.FromPath(defaultPage);
        var pos = new SourceDocument(defaultPage, text, null).ToPosition(text.IndexOf("btnSave_Click", StringComparison.Ordinal));

        var input = new MemoryStream();
        Frame(input, """{"jsonrpc":"2.0","id":0,"method":"textDocument/definition","params":{}}""");
        Frame(input, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"processId":null,"capabilities":{}}}""");
        Frame(input, """{"jsonrpc":"2.0","method":"initialized","params":{}}""");
        Frame(input, """{"jsonrpc":"2.0","method":"textDocument/didOpen","params":{"textDocument":{"uri":""" +
                     JsonSerializer.Serialize(uri) + ""","languageId":"aspx","version":1,"text":""" + JsonSerializer.Serialize(text) + "}}}");
        Frame(input, """{"jsonrpc":"2.0","id":2,"method":"textDocument/definition","params":{"textDocument":{"uri":""" +
                     JsonSerializer.Serialize(uri) + """},"position":{"line":""" + pos.Line + ""","character":""" + pos.Character + "}}}");
        Frame(input, """{"jsonrpc":"2.0","id":"three","method":"textDocument/signatureHelp","params":{}}""");
        Frame(input, """{"jsonrpc":"2.0","id":4,"method":"shutdown"}""");
        Frame(input, """{"jsonrpc":"2.0","method":"exit"}""");
        input.Position = 0;

        var output = new MemoryStream();
        using var server = new WebFormsLanguageServer(input, output);
        Assert.Equal(0, await server.RunAsync());

        var responses = Responses(output);
        Assert.Equal(ErrorCodes.ServerNotInitialized, responses["0"].GetProperty("error").GetProperty("code").GetInt32());
        Assert.True(responses["1"].GetProperty("result").GetProperty("capabilities").GetProperty("definitionProvider").GetBoolean());
        var def = Assert.Single(responses["2"].GetProperty("result").EnumerateArray().ToList());
        Assert.EndsWith("Default.aspx.vb", def.GetProperty("uri").GetString(), StringComparison.Ordinal);
        Assert.Equal(5, def.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(ErrorCodes.MethodNotFound, responses["three"].GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(JsonValueKind.Null, responses["4"].GetProperty("result").ValueKind);
    }

    [Fact]
    public void Fixture_with_designer_orders_field_then_handles_then_uses()
    {
        // Orders.aspx has a checked-in designer (as Visual Studio writes it): no generated copy involved.
        var page = Fixture.Path_("Orders.aspx");
        var markup = File.ReadAllText(page);
        var codeBehind = File.ReadAllText(page + ".vb");
        var designer = File.ReadAllText(page + ".designer.vb");
        var doc = new SourceDocument(page, markup, null);

        var reset = _navigator.Definition(page, doc.ToPosition(markup.IndexOf("\"btnReset\"", StringComparison.Ordinal) + 2));
        Assert.Equal(2, reset.Count);
        Assert.EndsWith("Orders.aspx.designer.vb", reset[0].Uri, StringComparison.Ordinal);
        Assert.Equal(Line(designer, "WithEvents btnReset"), reset[0].Range.Start.Line);
        Assert.Equal(Line(codeBehind, "Sub btnReset_Click"), reset[1].Range.Start.Line);

        var customer = _navigator.Definition(page, doc.ToPosition(markup.IndexOf("\"txtCustomer\" runat", StringComparison.Ordinal) + 2));
        Assert.Equal(
            [Line(designer, "WithEvents txtCustomer"), Line(codeBehind, "= txtCustomer.Text"), Line(codeBehind, "txtCustomer.Text = \"\"")],
            customer.Select(l => l.Range.Start.Line));

        var handler = Assert.Single(_navigator.Definition(page, doc.ToPosition(markup.IndexOf("gvOrders_RowCommand", StringComparison.Ordinal) + 1)));
        Assert.Equal(Line(codeBehind, "Sub gvOrders_RowCommand"), handler.Range.Start.Line);
    }

    [Fact]
    public async Task Completion_over_the_protocol()
    {
        var page = Fixture.Path_("Orders.aspx");
        var text = File.ReadAllText(page).Replace("<asp:Button ID=\"btnReset\" runat=\"server\" Text=\"Reset\" />", "<asp:Butt", StringComparison.Ordinal);
        var uri = JsonSerializer.Serialize(DocumentUri.FromPath(page));
        var pos = new SourceDocument(page, text, null).ToPosition(text.IndexOf("<asp:Butt\r\n", StringComparison.Ordinal) + "<asp:Butt".Length);

        var input = new MemoryStream();
        Frame(input, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"processId":null,"capabilities":{"textDocument":{"completion":{"completionItem":{"snippetSupport":true,"documentationFormat":["markdown","plaintext"]}}}}}}""");
        Frame(input, """{"jsonrpc":"2.0","method":"textDocument/didOpen","params":{"textDocument":{"uri":""" + uri + ""","languageId":"aspx","version":1,"text":""" + JsonSerializer.Serialize(text) + "}}}");
        Frame(input, """{"jsonrpc":"2.0","id":2,"method":"textDocument/completion","params":{"textDocument":{"uri":""" + uri + """},"position":{"line":""" + pos.Line + ""","character":""" + pos.Character + """},"context":{"triggerKind":1}}}""");
        Frame(input, """{"jsonrpc":"2.0","id":3,"method":"textDocument/completion","params":{"textDocument":{"uri":""" + uri + """},"position":{"line":0,"character":0}}}""");
        Frame(input, """{"jsonrpc":"2.0","id":4,"method":"shutdown"}""");
        Frame(input, """{"jsonrpc":"2.0","method":"exit"}""");
        input.Position = 0;

        var output = new MemoryStream();

        using (var server = new WebFormsLanguageServer(input, output))
        {
            Assert.Equal(0, await server.RunAsync());
        }

        var responses = Responses(output);
        var completion = responses["1"].GetProperty("result").GetProperty("capabilities").GetProperty("completionProvider");
        Assert.True(completion.GetProperty("resolveProvider").GetBoolean());
        Assert.Contains("<", completion.GetProperty("triggerCharacters").EnumerateArray().Select(c => c.GetString()));

        var list = responses["2"].GetProperty("result");
        Assert.False(list.GetProperty("isIncomplete").GetBoolean());
        var button = list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("label").GetString() == "asp:Button");
        Assert.Equal(2, button.GetProperty("insertTextFormat").GetInt32()); // the client said snippetSupport
        Assert.Equal("asp:Button ID=\"$1\" runat=\"server\"$0", button.GetProperty("textEdit").GetProperty("newText").GetString());
        Assert.Equal(pos.Character - "asp:Butt".Length, button.GetProperty("textEdit").GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(JsonValueKind.Null, responses["3"].GetProperty("result").ValueKind); // before the directive: nothing

        // Resolve, in a second session (a client without markdown) that completes in the same file first, so the
        // item (sent back exactly as the client received it) names a file the server knows.
        input = new MemoryStream();
        Frame(input, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"processId":null,"capabilities":{}}}""");
        Frame(input, """{"jsonrpc":"2.0","method":"textDocument/didOpen","params":{"textDocument":{"uri":""" + uri + ""","languageId":"aspx","version":1,"text":""" + JsonSerializer.Serialize(text) + "}}}");
        Frame(input, """{"jsonrpc":"2.0","id":5,"method":"textDocument/completion","params":{"textDocument":{"uri":""" + uri + """},"position":{"line":""" + pos.Line + ""","character":""" + pos.Character + "}}}");
        Frame(input, """{"jsonrpc":"2.0","id":2,"method":"completionItem/resolve","params":""" + button.GetRawText() + "}");
        Frame(input, """{"jsonrpc":"2.0","id":6,"method":"completionItem/resolve","params":{"label":"x","data":99}}""");
        Frame(input, """{"jsonrpc":"2.0","id":3,"method":"shutdown"}""");
        Frame(input, """{"jsonrpc":"2.0","method":"exit"}""");
        input.Position = 0;
        output = new MemoryStream();
        using (var server = new WebFormsLanguageServer(input, output))
        {
            Assert.Equal(0, await server.RunAsync());
        }

        var second = Responses(output);
        Assert.Equal("x", second["6"].GetProperty("result").GetProperty("label").GetString()); // unknown file id: unchanged
        var resolved = second["2"].GetProperty("result");
        Assert.Equal("Class System.Web.UI.WebControls.Button", resolved.GetProperty("detail").GetString());
        Assert.Equal("plaintext", resolved.GetProperty("documentation").GetProperty("kind").GetString()); // no markdown in these capabilities
        Assert.Equal("asp:Button ID=\"$1\" runat=\"server\"$0", resolved.GetProperty("textEdit").GetProperty("newText").GetString());
    }

    [Fact]
    public async Task Settings_from_initialization_options_then_did_change_configuration()
    {
        var page = Fixture.Path_("Orders.aspx");
        var text = File.ReadAllText(page);
        var uri = JsonSerializer.Serialize(DocumentUri.FromPath(page));
        var pos = new SourceDocument(page, text, null).ToPosition(text.IndexOf("OnClick=\"", StringComparison.Ordinal) + "OnClick=\"".Length);
        string complete(int id) => """{"jsonrpc":"2.0","id":""" + id + ""","method":"textDocument/completion","params":{"textDocument":{"uri":""" + uri + """},"position":{"line":""" + pos.Line + ""","character":""" + pos.Character + "}}}";

        var input = new MemoryStream();
        Frame(input, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"processId":null,"capabilities":{},"initializationOptions":{"completion":{"zeroArgHandlers":true}}}}""");
        Frame(input, """{"jsonrpc":"2.0","method":"textDocument/didOpen","params":{"textDocument":{"uri":""" + uri + ""","languageId":"aspx","version":1,"text":""" + JsonSerializer.Serialize(text) + "}}}");
        Frame(input, complete(2));
        Frame(input, """{"jsonrpc":"2.0","method":"workspace/didChangeConfiguration","params":{"settings":{"webforms":{"completion":{"zeroArgHandlers":false}}}}}""");
        Frame(input, complete(3));
        Frame(input, """{"jsonrpc":"2.0","id":4,"method":"shutdown"}""");
        Frame(input, """{"jsonrpc":"2.0","method":"exit"}""");
        input.Position = 0;
        var output = new MemoryStream();
        using (var server = new WebFormsLanguageServer(input, output))
        {
            Assert.Equal(0, await server.RunAsync());
        }

        var responses = Responses(output);
        static List<string?> Labels(JsonElement r) => [.. r.GetProperty("result").GetProperty("items").EnumerateArray().Select(i => i.GetProperty("label").GetString())];
        Assert.Contains("NoArguments", Labels(responses["2"]));
        Assert.DoesNotContain("NoArguments", Labels(responses["3"]));
    }

    [Fact]
    public async Task Exit_without_shutdown_returns_1()
    {
        var input = new MemoryStream();
        Frame(input, """{"jsonrpc":"2.0","method":"exit"}""");
        input.Position = 0;
        using var server = new WebFormsLanguageServer(input, new MemoryStream());
        Assert.Equal(1, await server.RunAsync());
    }

    [Fact]
    public void Ranged_changes_are_applied_in_order()
    {
        var doc = new SourceDocument("x.aspx", "line one\r\nline two\n", 1);
        var text = WebFormsLanguageServer.ApplyChanges(doc,
        [
            new TextDocumentContentChangeEvent(new LspRange(new LspPosition(1, 5), new LspPosition(1, 8)), "2"),
            new TextDocumentContentChangeEvent(new LspRange(new LspPosition(0, 0), new LspPosition(0, 4)), "LINE"),
        ]);
        Assert.Equal("LINE one\r\nline 2\n", text);
    }

    private static void CompileControlAssembly(string path, string eventName)
    {
        var refs = FrameworkReferences.Locate("v4.8");
        var source = $"""
            Namespace Acme.Controls
                Public Class Gauge
                    Inherits System.Web.UI.WebControls.WebControl
                    Public Event {eventName} As System.EventHandler
                End Class
            End Namespace
            """;
        var compilation = VisualBasicCompilation.Create(
            "Acme.Controls",
            [VisualBasicSyntaxTree.ParseText(source)],
            new[] { "mscorlib.dll", "System.dll", "System.Web.dll" }.Select(r => MetadataReference.CreateFromFile(Path.Combine(refs, r))),
            new VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary, embedVbCoreRuntime: true));
        using var stream = File.Create(path); // throws if the server still has the file mapped
        var emit = compilation.Emit(stream);
        Assert.True(emit.Success, string.Join("\n", emit.Diagnostics));
    }

    private LspPosition At(string text, string marker)
    {
        int offset = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(offset >= 0, marker);
        return new SourceDocument(_page, text, null).ToPosition(offset + 1);
    }

    private static int Line(string text, string marker)
    {
        int offset = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(offset >= 0, marker);
        return text[..offset].Count(c => c == '\n');
    }

    private static int Column(string text, string marker)
    {
        int offset = text.IndexOf(marker, StringComparison.Ordinal);
        return offset - (text.LastIndexOf('\n', offset) + 1);
    }

    private static void Frame(Stream s, string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        s.Write(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
        s.Write(body);
    }

    /// <summary>Responses by id (notifications such as publishDiagnostics have none).</summary>
    private static Dictionary<string, JsonElement> Responses(MemoryStream output) =>
        ReadFrames(output.ToArray()).Where(r => r.TryGetProperty("id", out _)).ToDictionary(r => r.GetProperty("id").ToString());

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
