using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using WebForms.Designer;
using WebForms.LanguageServer;
using WebForms.LanguageServer.CodeBlocks;
using WebForms.LanguageServer.Completion;
using WebForms.LanguageServer.Protocol;

namespace WebForms.Tests;

/// <summary>0.7.0: VB in markup code blocks (projection + Roslyn) and <c>&lt;%$ %&gt;</c> expression builders.</summary>
public sealed class CodeBlockTests : IDisposable
{
    private const string Page = """
        <%@ Page Language="vb" AutoEventWireup="false" CodeBehind="Blocks.aspx.vb" Inherits="SampleApp.Blocks" %>
        <%@ Import Namespace="System.Globalization" %>
        <html><body>
        <form id="form1" runat="server">
        <% If ShowTitle Then %><h1><%= Title.ToUpper() %></h1><% End If %>
        <p><%: Customer.Nmae %></p>
        <% =Greeting %>
        <asp:Repeater ID="rptLines" runat="server">
            <ItemTemplate><%# Eval("Sku") %> <%# Container.ItemIndex %></ItemTemplate>
        </asp:Repeater>
        <asp:Repeater ID="rptTyped" runat="server" ItemType="SampleApp.Line">
            <ItemTemplate><%# Item.Sku %></ItemTemplate>
        </asp:Repeater>
        <asp:Label ID="lblX" runat="server" Text='<%# Greeting %>' ToolTip="<%$ AppSettings:Tip %>" />
        <script runat="server">
            Protected Function Shout(s As String) As String
                Return s.ToUpper(CultureInfo.InvariantCulture)
            End Function
        </script>
        </form>
        </body></html>
        """;

    private const string CodeBehind = """
        Imports System.Collections.Generic

        Public Class Line
            Public Property Sku As String
            Public Property Qty As Integer
        End Class

        Partial Public Class Blocks
            Inherits System.Web.UI.Page

            Protected ShowTitle As Boolean
            Protected Greeting As String = "hi"
            Protected Customer As Line

            Protected Sub Page_Load(sender As Object, e As System.EventArgs) Handles Me.Load
                rptLines.DataSource = New List(Of Line)()
            End Sub
        End Class
        """;

    private readonly string _page;
    private readonly WebFormsWorkspace _workspace = new();
    private readonly CodeBlockService _service;

    public CodeBlockTests()
    {
        var dir = Fixture.CopyToTemp();
        _page = Path.Combine(dir, "Blocks.aspx");
        File.WriteAllText(_page, Page);
        File.WriteAllText(_page + ".vb", CodeBehind);
        var vbproj = Path.Combine(dir, "SampleApp.vbproj");
        File.WriteAllText(vbproj, File.ReadAllText(vbproj).Replace("  </ItemGroup>",
            "    <Compile Include=\"Blocks.aspx.vb\" />\r\n    <Compile Include=\"Blocks.aspx.designer.vb\" />\r\n  </ItemGroup>", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(dir, "web.config"), File.ReadAllText(Path.Combine(dir, "web.config"))
            .Replace("</configuration>", "  <appSettings><add key=\"Tip\" value=\"x\" /><add key=\"Other\" value=\"y\" /></appSettings>\r\n</configuration>", StringComparison.Ordinal));

        using (var project = ProjectContext.Create(dir, vbproj))
        {
            var result = DesignerGenerator.Generate(_page, Page, project);
            File.WriteAllBytes(result.DesignerPath!, DesignerOutput.Build(result.Model!, null, new OutputOptions()));
        }

        _service = new CodeBlockService(_workspace);
        _service.WaitUntilReady(_page);
    }

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void Projection_copies_each_block_and_maps_both_ways()
    {
        var projection = _service.ProjectionOf(_workspace.Analyze(_page)!)!;
        Assert.Equal(10, projection.BlockCount); // 2 statements, 3 expressions, 4 data bindings, 1 script
        int markup = Page.IndexOf("Title.ToUpper", StringComparison.Ordinal);
        int projected = projection.ToProjection(markup)!.Value;
        Assert.Equal("Title.ToUpper()", projection.Text.Substring(projected, "Title.ToUpper()".Length));
        Assert.Equal(markup, projection.ToMarkup(projected));
        Assert.Null(projection.ToProjection(Page.IndexOf("<h1>", StringComparison.Ordinal)));

        Assert.Contains("__o = (Greeting )", projection.Text, StringComparison.Ordinal); // <% =Greeting %> is an expression
        Assert.Contains("Private Sub __Template1(Container As Global.System.Web.UI.WebControls.RepeaterItem)", projection.Text, StringComparison.Ordinal);
        Assert.Contains("Dim Item As Global.SampleApp.Line", projection.Text, StringComparison.Ordinal);
        Assert.Contains("Inherits Global.SampleApp.Blocks", projection.Text, StringComparison.Ordinal);
        Assert.Contains("Imports System.Globalization", projection.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("AppSettings", projection.Text, StringComparison.Ordinal); // <%$ %> is not VB
    }

    [Fact]
    public void BC_errors_only_inside_blocks()
    {
        var error = Assert.Single(_service.Diagnostics(_page));
        Assert.Equal("BC30456", error.Code);
        Assert.Equal("webforms", error.Source);
        var doc = new SourceDocument(_page, Page, null);
        Assert.Equal(Page.IndexOf("Customer.Nmae", StringComparison.Ordinal), doc.ToOffset(error.Range.Start));
        Assert.Contains("Nmae", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Title.", "ToUpper")]
    [InlineData("Container.", "ItemIndex")]
    [InlineData("Item.", "Sku")]
    [InlineData("Return s.", "ToUpper")] // inside <script runat="server">
    public void Member_completion_in_blocks(string before, string expected)
    {
        var list = Complete(Page.IndexOf(before, StringComparison.Ordinal) + before.Length, ".");
        Assert.NotNull(list);
        var item = Assert.Single(list!.Items, i => i.Label == expected);
        Assert.NotNull(item.TextEdit);
        Assert.False(list.IsIncomplete);
    }

    /// <summary>
    /// A block being typed has no <c>%&gt;</c> yet (Neovim doesn't close <c>&lt;%</c>): the tokenizer runs it on to the
    /// next block's <c>%&gt;</c>, and <c>Container.</c> + newline + <c>&lt;div</c> was a VB XML axis access, offering
    /// nothing (Neovim test of 0.7.0, a page with repeaters).
    /// </summary>
    [Theory]
    [InlineData("<ItemTemplate>", "<%# Container.", "\r\n<div class=\"x\">", "ItemIndex")]
    [InlineData("<ItemTemplate>", "<%# Container.", "</ItemTemplate>", "ItemIndex")]
    [InlineData("<form id=\"form1\" runat=\"server\">", "<%= Me.", "\r\n  <h2>x</h2>", "Greeting")]
    [InlineData("</form>", "<%= Me.", "\r\n</body></html>", "Greeting")] // unterminated: runs to the end of the file
    [InlineData("<form id=\"form1\" runat=\"server\">", "<% If Me.", " <br /> <% End If %>", "Greeting")] // markup on the same line
    [InlineData("<form id=\"form1\" runat=\"server\">", "<% If Customer.", "\r\n  <p>x</p>\r\n<% Else %>\r\n<% End If %>", "Sku")] // swallowed <% Else
    [InlineData("<ItemTemplate>", "<%#: Container.", "<span>, </span>", "ItemIndex")]
    [InlineData("<ItemTemplate>", "<%# Eval(\"", "\r\n<div class=\"x\">", "Qty")] // the string runs on (VB 14 strings span lines)
    [InlineData("<form id=\"form1\" runat=\"server\">", "\"@id\": \"<%=\"https://\" & Me.", "\",", "Greeting")] // JSON in client script, not an attribute (structured data in a page)
    public void Member_completion_in_an_unclosed_block(string after, string typed, string next, string expected)
    {
        int at = Page.IndexOf(after, StringComparison.Ordinal) + after.Length;
        string rest = Page[at..];
        if (after == "</form>")
        {
            rest = ""; // drop everything after: the block is the last thing in the file
        }

        string text = Page[..at] + typed + next + rest;
        _workspace.Open(_page, text, 9);
        var doc = new SourceDocument(_page, text, null);
        var list = _service.Complete(_page, doc.ToPosition(at + typed.Length), new CompletionContext(2, "."), new ClientOptions(), out bool inBlock);
        Assert.True(inBlock);
        Assert.Contains(list!.Items, i => i.Label == expected);
    }

    /// <summary>
    /// The run-on block swallows the template's end tag, or the rest of the tag around an attribute value: the
    /// projection is built from a copy with the block closed, so the template (Container, Item) is still known.
    /// </summary>
    [Theory]
    [InlineData("<%# Item.Sku %></ItemTemplate>", "<%# Item.", "</ItemTemplate>", "Sku")]
    [InlineData("<%# Container.ItemIndex %></ItemTemplate>", "<%# Container.", "</ItemTemplate>", "ItemIndex")]
    [InlineData("Text='<%# Greeting %>'", "Text='<%# Me.", "'", "Greeting")]
    [InlineData("<%# Eval(\"Sku\") %>", "<%# Eval(\"", "\") ", "Qty")]
    [InlineData("<%# Eval(\"Sku\") %>", "<%# Eval(\"", " ", "Qty")] // no closing quote yet (no auto-pairs)
    public void Completion_in_an_unclosed_block_that_swallows_markup(string replace, string typed, string rest, string expected)
    {
        string text = Page.Replace(replace, typed + rest + replace[replace.IndexOf("%>", StringComparison.Ordinal)..][2..], StringComparison.Ordinal);
        _workspace.Open(_page, text, 9);
        var doc = new SourceDocument(_page, text, null);
        int at = text.IndexOf(typed, StringComparison.Ordinal) + typed.Length;
        var list = _service.Complete(_page, doc.ToPosition(at), null, new ClientOptions(), out bool inBlock);
        Assert.True(inBlock);
        Assert.Contains(list!.Items, i => i.Label == expected);
        Assert.NotNull(_service.ProjectionOf(_workspace.Analyze(_page)!)!.ToProjection(text.IndexOf("Return s.", StringComparison.Ordinal))); // later blocks still map
    }

    [Fact]
    public void Finished_blocks_keep_their_code()
    {
        string text = Page.Replace("<% =Greeting %>", "<% Dim x = 1\r\n<p>%>", StringComparison.Ordinal);
        _workspace.Open(_page, text, 9);
        var projection = _service.ProjectionOf(_workspace.Analyze(_page)!)!;
        Assert.Contains(" Dim x = 1\r\n<p>\r\n", projection.Text, StringComparison.Ordinal); // closed, nothing nested: an XML literal stays VB
    }

    [Fact]
    public void Identifier_completion_is_filtered_and_capped()
    {
        // At "Greeting" (start of an identifier): everything in scope, filtered by the typed prefix "Gr".
        int offset = Page.IndexOf("=Greeting", StringComparison.Ordinal) + "=Gr".Length;
        var list = Complete(offset, null)!;
        Assert.Contains(list.Items, i => i.Label == "Greeting");
        Assert.True(list.Items.Length <= CodeBlockService.MaxItems);
        Assert.All(list.Items, i => Assert.StartsWith("Gr", i.FilterText ?? i.Label, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Eval_fields_come_from_the_typed_data_source()
    {
        var list = Complete(Page.IndexOf("Eval(\"", StringComparison.Ordinal) + "Eval(\"".Length, "\"")!;
        Assert.Equal(["Sku", "Qty"], list.Items.Select(i => i.Label));
        Assert.Equal("Line.Sku As String", list.Items[0].Detail);
    }

    [Fact]
    public void Hover_and_definition_in_blocks()
    {
        var doc = new SourceDocument(_page, Page, null);
        var at = doc.ToPosition(Page.IndexOf("=Greeting", StringComparison.Ordinal) + 2);
        var (_, text) = _service.Hover(_page, at)!.Value;
        Assert.Contains("Greeting As String", text, StringComparison.Ordinal);

        var location = Assert.Single(_service.Definition(_page, at)!);
        Assert.EndsWith("Blocks.aspx.vb", location.Uri, StringComparison.Ordinal);
        Assert.Equal(CodeBehind.Split('\n').ToList().FindIndex(l => l.Contains("Protected Greeting", StringComparison.Ordinal)), location.Range.Start.Line);

        Assert.Null(_service.Definition(_page, doc.ToPosition(Page.IndexOf("<h1>", StringComparison.Ordinal)))); // not in a block
    }

    [Fact]
    public void Open_buffers_are_compiled()
    {
        _workspace.Open(_page + ".vb", CodeBehind.Replace("Protected Customer As Line", "Protected Customer As Object", StringComparison.Ordinal), 1);
        Assert.Empty(_service.Diagnostics(_page)); // late-bound now (Option Strict Off)
    }

    [Theory]
    [InlineData("<%$ ", new[] { "AppSettings", "ConnectionStrings", "Resources", "RouteUrl", "RouteValue" })]
    [InlineData("<%$ AppSettings:", new[] { "Tip", "Other" })]
    [InlineData("<%$ ConnectionStrings:Main.", new[] { "ConnectionString", "ProviderName" })]
    public void Expression_builder_completion(string typed, string[] expected)
    {
        string text = Page.Replace("<%$ AppSettings:Tip %>", typed + " %>", StringComparison.Ordinal);
        _workspace.Open(_page, text, 7);
        var analysis = _workspace.Analyze(_page)!;
        var list = ExpressionBuilderCompletion.Complete(analysis, text.IndexOf(typed, StringComparison.Ordinal) + typed.Length, new ClientOptions())!;
        Assert.Equal(expected, list.Items.Select(i => i.Label));
        Assert.Null(ExpressionBuilderCompletion.Complete(analysis, text.IndexOf("<h1>", StringComparison.Ordinal), new ClientOptions()));
    }

    private CompletionList? Complete(int offset, string? trigger)
    {
        var doc = new SourceDocument(_page, Page, null);
        var context = trigger is null ? null : new CompletionContext(2, trigger);
        var list = _service.Complete(_page, doc.ToPosition(offset), context, new ClientOptions(), out bool inBlock);
        Assert.True(inBlock);
        return list;
    }

    [Fact]
    public async Task Code_blocks_over_the_protocol_compile_in_the_background()
    {
        using var toServer = new AnonymousPipeServerStream(PipeDirection.Out);
        using var serverIn = new AnonymousPipeClientStream(PipeDirection.In, toServer.ClientSafePipeHandle);
        using var fromServer = new AnonymousPipeServerStream(PipeDirection.In);
        using var serverOut = new AnonymousPipeClientStream(PipeDirection.Out, fromServer.ClientSafePipeHandle);
        using var server = new WebFormsLanguageServer(serverIn, serverOut, diagnosticsDelay: TimeSpan.Zero);
        var run = Task.Run(() => server.RunAsync());
        var uri = JsonSerializer.Serialize(DocumentUri.FromPath(_page));

        void Send(string json)
        {
            var body = Encoding.UTF8.GetBytes(json);
            toServer.Write(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
            toServer.Write(body);
            toServer.Flush();
        }

        // Frames until one matches (the server also sends notifications in between).
        JsonElement Next(Func<JsonElement, bool> match)
        {
            while (true)
            {
                var header = new StringBuilder();
                while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    int b = fromServer.ReadByte();
                    Assert.NotEqual(-1, b);
                    header.Append((char)b);
                }

                int length = int.Parse(header.ToString().Trim()["Content-Length: ".Length..], CultureInfo.InvariantCulture);
                var body = new byte[length];
                fromServer.ReadExactly(body);
                var frame = JsonDocument.Parse(body).RootElement.Clone();
                if (match(frame))
                {
                    return frame;
                }
            }
        }

        static bool IsResponse(JsonElement f, int id) => f.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.Number && i.GetInt32() == id;

        Send("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"processId":null,"capabilities":{}}}""");
        var init = Next(f => IsResponse(f, 1));
        Assert.True(init.GetProperty("result").GetProperty("capabilities").GetProperty("hoverProvider").GetBoolean());
        Send("""{"jsonrpc":"2.0","method":"textDocument/didOpen","params":{"textDocument":{"uri":""" + uri + ""","languageId":"aspx","version":1,"text":""" + JsonSerializer.Serialize(Page) + "}}}");

        // Markup diagnostics are published on open; the BC error follows once the project has compiled in the background.
        var published = Next(f => f.TryGetProperty("method", out var m) && m.GetString() == "textDocument/publishDiagnostics" &&
            f.GetProperty("params").GetProperty("diagnostics").EnumerateArray().Any(d => d.GetProperty("code").GetString() == "BC30456"));
        Assert.Single(published.GetProperty("params").GetProperty("diagnostics").EnumerateArray());

        var pos = new SourceDocument(_page, Page, null).ToPosition(Page.IndexOf("Title.", StringComparison.Ordinal) + "Title.".Length);
        string position = """{"line":""" + pos.Line + ""","character":""" + pos.Character + "}";
        Send("""{"jsonrpc":"2.0","id":2,"method":"textDocument/completion","params":{"textDocument":{"uri":""" + uri + """},"position":""" + position + ""","context":{"triggerKind":2,"triggerCharacter":"."}}}""");
        var toUpper = Next(f => IsResponse(f, 2)).GetProperty("result").GetProperty("items").EnumerateArray().First(i => i.GetProperty("label").GetString() == "ToUpper");
        Assert.True(toUpper.GetProperty("data").GetInt32() < 0);

        Send("""{"jsonrpc":"2.0","id":3,"method":"completionItem/resolve","params":""" + toUpper.GetRawText() + "}");
        var resolved = Next(f => IsResponse(f, 3)).GetProperty("result");
        Assert.Contains("ToUpper", resolved.GetProperty("documentation").GetProperty("value").GetString(), StringComparison.Ordinal);

        string onToUpper = """{"line":""" + pos.Line + ""","character":""" + (pos.Character + 1) + "}";
        Send("""{"jsonrpc":"2.0","id":4,"method":"textDocument/hover","params":{"textDocument":{"uri":""" + uri + """},"position":""" + onToUpper + "}}");
        var hover = Next(f => IsResponse(f, 4)).GetProperty("result");
        Assert.Contains("ToUpper", hover.GetProperty("contents").GetProperty("value").GetString(), StringComparison.Ordinal);

        Send("""{"jsonrpc":"2.0","id":5,"method":"shutdown"}""");
        Send("""{"jsonrpc":"2.0","method":"exit"}""");
        Assert.Equal(0, await run);
    }
}
