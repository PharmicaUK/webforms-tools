using WebForms.LanguageServer;
using WebForms.LanguageServer.Completion;
using WebForms.LanguageServer.Protocol;

namespace WebForms.Tests;

/// <summary>Completion against the Orders.aspx fixture (which has a code-behind and a designer). '|' marks the cursor.</summary>
public sealed class CompletionTests : IDisposable
{
    private static readonly ClientOptions Plain = new();
    private static readonly ClientOptions Snippets = new(Snippets: true, Markdown: true);

    private readonly string _dir = Fixture.CopyToTemp();
    private readonly WebFormsWorkspace _workspace = new();
    private readonly CompletionProvider _provider;
    private readonly string _page;
    private readonly string _original;
    private int _version;

    public CompletionTests()
    {
        _provider = new CompletionProvider(_workspace);
        _page = Path.Combine(_dir, "Orders.aspx");
        _original = File.ReadAllText(_page);
    }

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void Control_tags_replace_the_whole_prefixed_word()
    {
        var (list, text, offset) = Complete("    <asp:Panel ID=\"pnlActions\" runat=\"server\">", "    <asp:Panel ID=\"pnlActions\" runat=\"server\">\n        <asp:Bu|");
        var button = Item(list, "asp:Button");
        Assert.Equal("asp:Button", button.TextEdit!.NewText);
        Assert.Null(button.InsertTextFormat);
        Assert.Equal(text.LastIndexOf("<asp:Bu", offset, StringComparison.Ordinal) + 1, Offset(text, button.TextEdit.Range.Start));
        Assert.Equal(offset, Offset(text, button.TextEdit.Range.End));
        Assert.Null(button.FilterText); // the label
        Assert.False(list.IsIncomplete);

        Assert.Contains(list.Items, i => i.Label == "uc:Widget");
        Assert.Contains(list.Items, i => i.Label == "asp:UpdatePanel"); // System.Web.Extensions
        Assert.DoesNotContain(list.Items, i => i.Label == "asp:ListItem"); // not a control
        Assert.DoesNotContain(list.Items, i => i.Label == "asp:Style");
    }

    [Fact]
    public void Control_tags_get_ID_and_runat_as_a_snippet()
    {
        var (list, _, _) = Complete("        <asp:Button ID=\"btnReset\" runat=\"server\" Text=\"Reset\" />", "        <asp:Button ID=\"btnReset\" runat=\"server\" Text=\"Reset\" />\n        <asp:Lab|", Snippets);
        var label = Item(list, "asp:Label");
        Assert.Equal("asp:Label ID=\"$1\" runat=\"server\"$0", label.TextEdit!.NewText);
        Assert.Equal(InsertTextFormat.Snippet, label.InsertTextFormat);

        (list, _, _) = Complete("<asp:Button ID=\"btnReset\" runat=\"server\" Text=\"Reset\" />", "<asp:Butt|", Snippets);
        Assert.Equal("asp:Button ID=\"$1\" runat=\"server\"$0", Item(list, "asp:Button").TextEdit!.NewText);

        // Renaming the tag of an existing element only replaces the name.
        (list, _, _) = Complete("<asp:Button ID=\"btnReset\"", "<asp:Butt|on ID=\"btnReset\"", Snippets);
        Assert.Equal("asp:Button", Item(list, "asp:Button").TextEdit!.NewText);
    }

    [Fact]
    public void Edit_range_is_sent_once_when_the_client_takes_item_defaults()
    {
        var client = new ClientOptions(Snippets: true, EditRangeDefault: true);
        var (list, text, offset) = Complete("        <asp:Button ID=\"btnReset\" runat=\"server\" Text=\"Reset\" />", "        <asp:Lab|", client);
        Assert.Equal(offset - "asp:Lab".Length, Offset(text, list.ItemDefaults!.EditRange!.Start));
        Assert.Equal(offset, Offset(text, list.ItemDefaults.EditRange.End));
        var label = Item(list, "asp:Label");
        Assert.Null(label.TextEdit);
        Assert.Equal("asp:Label ID=\"$1\" runat=\"server\"$0", label.TextEditText);

        (list, _, _) = Complete("TextMode=\"SingleLine\"", "TextMode=\"|\"", client);
        Assert.Null(Item(list, "MultiLine").TextEditText); // same as the label
        Assert.NotNull(Item(list, "MultiLine").Data);
    }

    [Fact]
    public void Children_follow_the_parse_rules_of_the_parent()
    {
        // DropDownList: ParseChildren(true, "Items") -> ListItem only.
        var (list, _, _) = Complete("        <asp:ListItem Value=\"open\" Text=\"Open\" />", "        <|\n        <asp:ListItem Value=\"open\" Text=\"Open\" />");
        Assert.Equal(["asp:ListItem"], list.Items.Select(i => i.Label));

        // GridView: inner properties.
        (list, _, _) = Complete("        <HeaderStyle CssClass=\"head\" />", "        <|\n        <HeaderStyle CssClass=\"head\" />");
        Assert.Contains(list.Items, i => i.Label == "Columns");
        Assert.Contains(list.Items, i => i.Label == "RowStyle");
        Assert.Contains(list.Items, i => i.Label == "EmptyDataTemplate");
        Assert.DoesNotContain(list.Items, i => i.Label == "asp:Button");

        // GridView Columns: DataControlField items.
        (list, _, _) = Complete("            <asp:BoundField DataField=\"Id\" HeaderText=\"Order\" />", "            <asp:Hyp|\n            <asp:BoundField DataField=\"Id\" HeaderText=\"Order\" />");
        Assert.Contains(list.Items, i => i.Label == "asp:HyperLinkField");
        Assert.Contains(list.Items, i => i.Label == "asp:TemplateField");
        Assert.DoesNotContain(list.Items, i => i.Label == "asp:HyperLink");

        // Inside a template: controls again.
        (list, _, _) = Complete("                    <asp:LinkButton ID=\"lbOpen\"", "                    <asp:|<asp:LinkButton ID=\"lbOpen\"");
        Assert.Contains(list.Items, i => i.Label == "asp:HyperLink");
    }

    [Fact]
    public void Attributes_are_properties_events_and_sub_properties()
    {
        var (list, _, _) = Complete("<asp:Button ID=\"btnReset\" runat=\"server\" Text=\"Reset\" />", "<asp:Button ID=\"btnReset\" runat=\"server\" Text=\"Reset\" | />");
        Assert.Equal("CssClass=", Item(list, "CssClass").TextEdit!.NewText);
        Assert.Equal(CompletionItemKind.Event, Item(list, "OnClick").Kind);
        Assert.Contains(list.Items, i => i.Label == "OnCommand");
        Assert.Contains(list.Items, i => i.Label == "Font-Bold");
        Assert.Contains(list.Items, i => i.Label == "CausesValidation");
        Assert.DoesNotContain(list.Items, i => i.Label is "ID" or "runat" or "Text"); // already there
        Assert.DoesNotContain(list.Items, i => i.Label == "Controls"); // read-only
        Assert.DoesNotContain(list.Items, i => i.Label == "OnClientClick" && i.Kind == CompletionItemKind.Event); // a property, not an event
        Assert.Equal(CompletionItemKind.Property, Item(list, "OnClientClick").Kind);

        // Inner property objects and collection items get their own properties, no ID/runat.
        (list, _, _) = Complete("<HeaderStyle CssClass=\"head\" />", "<HeaderStyle CssClass=\"head\" Hor| />");
        Assert.Contains(list.Items, i => i.Label == "HorizontalAlign");
        Assert.DoesNotContain(list.Items, i => i.Label == "runat");
        (list, _, _) = Complete("<asp:BoundField DataField=\"Id\" HeaderText=\"Order\" />", "<asp:BoundField DataField=\"Id\" HeaderText=\"Order\" | />");
        Assert.Contains(list.Items, i => i.Label == "DataFormatString");
        Assert.Contains(list.Items, i => i.Label == "ItemStyle-CssClass");
        Assert.DoesNotContain(list.Items, i => i.Label == "runat");
    }

    [Fact]
    public void Attribute_snippets_put_the_cursor_between_the_quotes()
    {
        var (list, _, _) = Complete("<asp:Button ID=\"btnReset\" runat=\"server\" Text=\"Reset\" />", "<asp:Button ID=\"btnReset\" runat=\"server\" Text=\"Reset\" Css| />", Snippets);
        var css = Item(list, "CssClass");
        Assert.Equal("CssClass=\"$1\"", css.TextEdit!.NewText);
        Assert.Equal(InsertTextFormat.Snippet, css.InsertTextFormat);

        // Renaming an attribute that has a value: only the name.
        (list, _, _) = Complete("Text=\"Reset\" />", "Te|xt=\"Reset\" />", Snippets);
        Assert.Equal("Text", Item(list, "Text").TextEdit!.NewText);
    }

    [Fact]
    public void Unfinished_tags_still_complete()
    {
        // No closing bracket: the tokenizer sees literal text, the cursor lexer still reads the tag.
        var (list, _, _) = Complete("    <uc:Widget ID=\"widgetSummary\" runat=\"server\" />", "    <asp:TextBox ID=\"txtNew\" Te|\n    <uc:Widget ID=\"widgetSummary\" runat=\"server\" />");
        Assert.Contains(list.Items, i => i.Label == "TextMode");
        Assert.Contains(list.Items, i => i.Label == "OnTextChanged");
    }

    [Fact]
    public void User_control_attributes_come_from_its_code_behind()
    {
        var (list, _, _) = Complete("<uc:Widget ID=\"widgetSummary\" runat=\"server\" />", "<uc:Widget ID=\"widgetSummary\" runat=\"server\" | />");
        Assert.Contains(list.Items, i => i.Label == "Title");
        Assert.Contains(list.Items, i => i.Label == "OnChanged");
        Assert.DoesNotContain(list.Items, i => i.Label == "Hidden");
        Assert.Contains(list.Items, i => i.Label == "Visible"); // UserControl base
    }

    [Fact]
    public void Enum_and_boolean_values()
    {
        var (list, text, offset) = Complete("TextMode=\"SingleLine\"", "TextMode=\"Mu|\"");
        var multi = Item(list, "MultiLine");
        Assert.Equal("MultiLine", multi.TextEdit!.NewText);
        Assert.Equal(offset - 2, Offset(text, multi.TextEdit.Range.Start));
        Assert.Contains(list.Items, i => i.Label == "Password");
        Assert.Equal(CompletionItemKind.EnumMember, multi.Kind);

        (list, _, _) = Complete("AutoPostBack=\"true\"", "AutoPostBack=\"|\"");
        Assert.Equal(["True", "False"], list.Items.Select(i => i.Label));

        // Right after '=' (no quote yet): the value is inserted with quotes.
        (list, _, _) = Complete("TextMode=\"SingleLine\"", "TextMode=|");
        Assert.Equal("\"MultiLine\"", Item(list, "MultiLine").TextEdit!.NewText);

        // Sub-property values.
        (list, _, _) = Complete("<asp:Button ID=\"btnReset\" runat=\"server\" Text=\"Reset\" />", "<asp:Button ID=\"btnReset\" runat=\"server\" Text=\"Reset\" Font-Bold=\"|\" />");
        Assert.Equal(["True", "False"], list.Items.Select(i => i.Label));
    }

    [Fact]
    public void Handlers_are_subs_with_a_matching_signature()
    {
        var (list, _, _) = Complete("OnClick=\"btnSearch_Click\"", "OnClick=\"|\"");
        var names = list.Items.OrderBy(i => i.SortText, StringComparer.Ordinal).Select(i => i.Label).ToList();
        Assert.Equal("btnSearch_Click", names[0]); // ID_Event first
        Assert.Contains("txtCustomer_TextChanged", names);
        Assert.Contains("ProtectedFriendSub", names);
        Assert.Contains("NoModifierSub", names); // Public
        // The page class compiled from the markup inherits the code-behind from another assembly.
        Assert.DoesNotContain("btnReset_Click", names); // Private
        Assert.DoesNotContain("FriendSub", names);
        Assert.DoesNotContain("NoArguments", names); // parameterless: only with completion.zeroArgHandlers
        Assert.DoesNotContain("gvOrders_RowCommand", names); // GridViewCommandEventArgs is not an EventArgs a Click passes
        Assert.DoesNotContain("WrongArguments", names);
        Assert.DoesNotContain("Validate", names);
        Assert.DoesNotContain("NotASub", names);
        Assert.Equal(CompletionItemKind.Method, Item(list, "btnSearch_Click").Kind);
        Assert.StartsWith("Protected Sub btnSearch_Click(", Item(list, "btnSearch_Click").Detail, StringComparison.Ordinal);

        (list, _, _) = Complete("OnRowCommand=\"gvOrders_RowCommand\"", "OnRowCommand=\"|\"");
        names = [.. list.Items.OrderBy(i => i.SortText, StringComparer.Ordinal).Select(i => i.Label)];
        Assert.Equal("gvOrders_RowCommand", names[0]);
        Assert.Contains("WrongArguments", names);
        Assert.Contains("btnSearch_Click", names); // (Object, EventArgs) handles any EventArgs-derived event
        // ID_Event, then exact args type, then base args type; within each, Subs without Handles first.
        Assert.True(names.IndexOf("WrongArguments") < names.IndexOf("btnSearch_Click"));
        Assert.Equal("Page_Load", names[^1]); // Handles Me.Load: last of the EventArgs group

        // Parameterless Subs on request, after every two-parameter Sub.
        _provider.Settings = new ServerSettings(ZeroArgHandlers: true);
        (list, _, _) = Complete("OnClick=\"btnSearch_Click\"", "OnClick=\"|\"");
        names = [.. list.Items.OrderBy(i => i.SortText, StringComparer.Ordinal).Select(i => i.Label)];
        Assert.Equal("NoArguments", names[^1]);
        Assert.True(names.IndexOf("Page_Load") < names.IndexOf("NoArguments"));
    }

    [Fact]
    public void Settings_come_bare_or_under_webforms_and_keep_absent_keys()
    {
        static System.Text.Json.JsonElement Json(string s) => System.Text.Json.JsonDocument.Parse(s).RootElement;
        var on = ServerSettings.Read(Json("""{ "completion": { "zeroArgHandlers": true } }"""), new ServerSettings());
        Assert.True(on.ZeroArgHandlers);
        Assert.True(ServerSettings.Read(Json("""{ "other": 1 }"""), on).ZeroArgHandlers);
        Assert.False(ServerSettings.Read(Json("""{ "webforms": { "completion": { "zeroArgHandlers": false } } }"""), on).ZeroArgHandlers);
        Assert.Same(on, ServerSettings.Read(null, on));
    }

    [Fact]
    public void End_tag_of_the_innermost_open_server_element()
    {
        // An unclosed Panel: "</" and "</asp:Pa" offer its end tag, replacing from the '/'.
        var (list, text, offset) = Complete("    <uc:Widget ID=\"widgetSummary\" runat=\"server\" />", "    <asp:Panel ID=\"pnlNew\" runat=\"server\">\n        <asp:Label ID=\"lblNew\" runat=\"server\" />\n    </|");
        var item = Assert.Single(list.Items);
        Assert.Equal("/asp:Panel>", item.Label);
        Assert.Equal("/asp:Panel>", item.TextEdit!.NewText);
        Assert.Equal(offset - 1, Offset(text, item.TextEdit.Range.Start));

        (list, text, offset) = Complete("    <uc:Widget ID=\"widgetSummary\" runat=\"server\" />", "    <asp:Panel ID=\"pnlNew\" runat=\"server\">\n    </asp:Pa|");
        item = Assert.Single(list.Items);
        Assert.Equal(offset - "/asp:Pa".Length, Offset(text, item.TextEdit!.Range.Start));

        // Inner properties too.
        (list, _, _) = Complete("                </ItemTemplate>", "                </|");
        Assert.Equal("/ItemTemplate>", Assert.Single(list.Items).Label);

        // Directly inside an element that is closed further down: still its end tag (only the tags before the cursor count).
        (list, _, _) = Complete("        <asp:Button ID=\"btnReset\" runat=\"server\" Text=\"Reset\" />", "        </|");
        Assert.Equal("/asp:Panel>", Assert.Single(list.Items).Label);

        (list, _, _) = Complete("Text=\"Customer\" />", "Text=\"Customer\" />\n</|");
        Assert.Equal("/asp:Content>", Assert.Single(list.Items).Label);

        // A new Panel inside the existing pnlActions Panel (whose </asp:Panel> is further down).
        (list, _, _) = Complete("        <asp:Button ID=\"btnReset\" runat=\"server\" Text=\"Reset\" />", "        <asp:Panel ID=\"pnlInner\" runat=\"server\">\n            </|");
        Assert.Equal("/asp:Panel>", Assert.Single(list.Items).Label);

        // An inner ItemTemplate (a Repeater's) inside the TemplateField's ItemTemplate.
        (list, _, _) = Complete(
            "                    <asp:LinkButton ID=\"lbOpen\" runat=\"server\" CommandName=\"Open\" Text=\"Open\" />",
            "                    <asp:Repeater ID=\"rptInner\" runat=\"server\">\n                        <ItemTemplate>\n                            <asp:Label ID=\"lblInner\" runat=\"server\" />\n                        </|");
        Assert.Equal("/ItemTemplate>", Assert.Single(list.Items).Label);

        // Innermost open element is HTML (the HTML server's), or a finished end tag: nothing.
        Assert.Null(CompleteOrNull("        <asp:Button ID=\"btnReset\" runat=\"server\" Text=\"Reset\" />", "        <div class=\"x\">\n            <br>\n            </|"));
        Assert.Null(CompleteOrNull("    </asp:Panel>", "    </asp:Panel|>"));
    }

    [Fact]
    public void Innermost_open_tag_skips_void_elements_and_script_bodies()
    {
        static string? Open(string text)
        {
            int at = text.IndexOf('|', StringComparison.Ordinal);
            text = text.Remove(at, 1);
            // Stand-in for the parsed tree: prefixed tags are the server elements.
            return CompletionProvider.InnermostOpenTag(WebForms.Markup.MarkupTokenizer.Tokenize(text), at, t => t.Name.Contains(':', StringComparison.Ordinal))?.Name;
        }

        Assert.Equal("asp:Panel", Open("<asp:Panel runat=\"server\"><img src=x><input type=text><br/>|"));
        Assert.Equal("asp:Panel", Open("<asp:Panel runat=\"server\"><script>var s = '<div>';</script>|"));
        Assert.Equal("asp:Panel", Open("<asp:Panel runat=\"server\"><DIV></div>|</asp:Panel>"));
        Assert.Equal("asp:Panel", Open("<asp:Panel runat=\"server\"><asp:Panel runat=\"server\"></asp:panel>|"));
        Assert.Null(Open("<asp:Panel runat=\"server\"></asp:Panel>|"));

        // A stray HTML end tag doesn't close across a server element: HTML can be unbalanced across <% If %> branches.
        Assert.Equal("asp:Panel", Open("<div><asp:Panel runat=\"server\"></div>|"));
        Assert.Equal("div", Open("<div><asp:Panel runat=\"server\"></div></asp:Panel>|"));
        Assert.Equal("asp:Panel", Open("<x:Div><asp:Panel runat=\"server\"></x:Div>|")); // not even a server element's end tag

        // Server tags inside a client script still count, e.g. a ContentPlaceHolder in a master page's script; HTML ones don't.
        Assert.Equal("asp:ContentPlaceHolder", Open("<script>\n<asp:ContentPlaceHolder ID=\"cph\" runat=\"server\">|"));
        Assert.Equal("script", Open("<script>\n<asp:ContentPlaceHolder ID=\"cph\" runat=\"server\"></asp:ContentPlaceHolder>|"));
        Assert.Null(Open("<script>\n<asp:ContentPlaceHolder ID=\"cph\" runat=\"server\"></asp:ContentPlaceHolder></script>|"));
    }

    [Fact]
    public void Control_ids_and_placeholders()
    {
        var (list, _, _) = Complete("ControlToValidate=\"txtCustomer\"", "ControlToValidate=\"|\"");
        Assert.Contains(list.Items, i => i.Label == "txtCustomer" && i.Detail == "TextBox");
        Assert.DoesNotContain(list.Items, i => i.Label == "rfvCustomer"); // itself

        (list, _, _) = Complete("ContentPlaceHolderID=\"Main\"", "ContentPlaceHolderID=\"|\"");
        Assert.Equal(["Main"], list.Items.Select(i => i.Label));
    }

    [Fact]
    public void Html_tags_get_runat_and_server_events_only()
    {
        var (list, _, _) = Complete("    <asp:Panel ID=\"pnlActions\" runat=\"server\">", "    <div |\n    <asp:Panel ID=\"pnlActions\" runat=\"server\">");
        Assert.Equal(["runat"], list.Items.Select(i => i.Label));

        (list, _, _) = Complete("    <asp:Panel ID=\"pnlActions\" runat=\"server\">", "    <button id=\"b\" runat=\"server\" |\n    <asp:Panel ID=\"pnlActions\" runat=\"server\">");
        Assert.Contains(list.Items, i => i.Label == "OnServerClick");
        Assert.DoesNotContain(list.Items, i => i.Label is "runat" or "Disabled");

        (list, _, _) = Complete("runat=\"server\" AssociatedControlID", "runat=\"|\" AssociatedControlID");
        Assert.Equal(["server"], list.Items.Select(i => i.Label));
    }

    [Fact]
    public void Nothing_inside_code_script_style_comments_or_text()
    {
        Assert.Null(CompleteOrNull("<asp:Content ID=\"Content1\"", "<% Dim x = | %>\n<asp:Content ID=\"Content1\""));
        Assert.Null(CompleteOrNull("<asp:Content ID=\"Content1\"", "<%-- <asp:| --%>\n<asp:Content ID=\"Content1\""));
        Assert.Null(CompleteOrNull("<asp:Content ID=\"Content1\"", "<script>if (a <b|) {}</script>\n<asp:Content ID=\"Content1\""));
        Assert.Null(CompleteOrNull("<asp:Content ID=\"Content1\"", "<style>a { color: red; |}</style>\n<asp:Content ID=\"Content1\""));
        Assert.Null(CompleteOrNull("Text=\"Customer\" />", "Text=\"Customer\" /> plain text |"));
        Assert.Null(CompleteOrNull("Text=\"Customer\"", "Text='<%# Eval(|) %>'"));

        // HTML comments: closed, unclosed, spanning lines, a tag already inside one; but not after one.
        Assert.Null(CompleteOrNull("<asp:Content ID=\"Content1\"", "<!-- <asp:Bu| -->\n<asp:Content ID=\"Content1\""));
        Assert.Null(CompleteOrNull("<asp:Content ID=\"Content1\"", "<!-- old:\n  <asp:Bu|\n<asp:Content ID=\"Content1\""));
        Assert.Null(CompleteOrNull("    <asp:Button ID=\"btnReset\" runat=\"server\" Text=\"Reset\" />", "    <!-- <asp:Button ID=\"btnReset\" runat=\"server\" | /> -->"));
        Assert.Null(CompleteOrNull("    <asp:Button ID=\"btnReset\" runat=\"server\" Text=\"Reset\" />", "    <!-- <asp:Button ID=\"btnReset\" runat=\"server\" Text=\"|\" /> -->"));
        Assert.NotNull(CompleteOrNull("    <asp:Panel ID=\"pnlActions\" runat=\"server\">", "    <asp:Panel ID=\"pnlActions\" runat=\"server\">\n        <!-- a\n b --> <asp:Bu|"));
        Assert.NotNull(CompleteOrNull("    <asp:Panel ID=\"pnlActions\" runat=\"server\">", "    <asp:Panel ID=\"pnlActions\" runat=\"server\">\n        <%-- <!-- --%> <asp:Bu|"));
    }

    [Fact]
    public void Directives()
    {
        var (list, _, _) = Complete("<%@ Register Src", "<%@ |Register Src");
        Assert.Contains(list.Items, i => i.Label == "MasterType");

        (list, _, _) = Complete("Language=\"vb\"", "Language=\"|\"");
        Assert.Equal(["vb", "C#"], list.Items.Select(i => i.Label));

        (list, _, _) = Complete("MasterPageFile=\"~/Site.Master\"", "MasterPageFile=\"~/|\"");
        Assert.Equal(["~/Site.Master"], list.Items.Select(i => i.Label));

        (list, _, _) = Complete("Src=\"~/Controls/Widget.ascx\"", "Src=\"|\"");
        Assert.Contains(list.Items, i => i.Label == "~/Controls/Widget.ascx");
        Assert.Contains(list.Items, i => i.Label == "~/Controls/Footer.ascx");

        (list, _, _) = Complete("Inherits=\"SampleApp.Orders\"", "Inherits=\"|\"");
        Assert.Equal(["SampleApp.Orders"], list.Items.Select(i => i.Label));

        (list, _, _) = Complete(" CodeBehind=\"Orders.aspx.vb\"", " |CodeBehind=\"Orders.aspx.vb\"");
        Assert.Contains(list.Items, i => i.Label == "EnableViewState");
        Assert.DoesNotContain(list.Items, i => i.Label is "Language" or "Title"); // present
    }

    [Fact]
    public void Resolve_adds_detail_and_documentation()
    {
        var (list, _, _) = Complete("<asp:Button ID=\"btnReset\" runat=\"server\" Text=\"Reset\" />", "<asp:Button ID=\"btnReset\" runat=\"server\" | />", Snippets);
        Assert.All(list.Items, i => Assert.Null(i.Documentation)); // the first response stays light

        var text = _provider.Resolve(Item(list, "Text"), Snippets);
        Assert.Equal("Property Text As String (default \"\")", text.Detail);
        Assert.Equal("markdown", text.Documentation!.Kind);
        Assert.Contains("text caption", text.Documentation.Value, StringComparison.OrdinalIgnoreCase);

        var click = _provider.Resolve(Item(list, "OnClick"), Plain);
        Assert.Equal("Event Click As EventHandler", click.Detail);
        Assert.Equal("plaintext", click.Documentation!.Kind);

        var css = _provider.Resolve(Item(list, "CssClass"), Snippets); // declared on WebControl
        Assert.NotNull(css.Documentation);
    }

    [Fact]
    public void Pages_without_a_code_behind_still_complete_tags()
    {
        var page = Path.Combine(_dir, "WebSite.aspx");
        var text = File.ReadAllText(page) + "\n<asp:Che";
        _workspace.Open(page, text, 1);
        var doc = new SourceDocument(page, text, 1);
        var list = _provider.Complete(page, doc.ToPosition(text.Length), Plain);
        Assert.NotNull(list);
        Assert.Contains(list.Items, i => i.Label == "asp:CheckBox");
    }

    /// <summary>Replaces <paramref name="original"/> in Orders.aspx with <paramref name="edited"/> (containing '|'), opens it, completes at '|'.</summary>
    private (CompletionList List, string Text, int Offset) Complete(string original, string edited, ClientOptions? client = null)
    {
        var result = CompleteOrNull(original, edited, client, out var text, out var offset);
        Assert.NotNull(result);
        return (result, text, offset);
    }

    private CompletionList? CompleteOrNull(string original, string edited) => CompleteOrNull(original, edited, null, out _, out _);

    private CompletionList? CompleteOrNull(string original, string edited, ClientOptions? client, out string text, out int offset)
    {
        int at = _original.IndexOf(original, StringComparison.Ordinal);
        Assert.True(at >= 0, original);
        var withCursor = string.Concat(_original.AsSpan(0, at), edited, _original.AsSpan(at + original.Length));
        offset = withCursor.IndexOf('|', StringComparison.Ordinal);
        text = withCursor.Remove(offset, 1);
        _workspace.Open(_page, text, ++_version);
        return _provider.Complete(_page, new SourceDocument(_page, text, null).ToPosition(offset), client ?? Plain);
    }

    private static CompletionItem Item(CompletionList list, string label) =>
        list.Items.FirstOrDefault(i => i.Label == label) ?? throw new Xunit.Sdk.XunitException($"No '{label}' in: {string.Join(", ", list.Items.Select(i => i.Label).Take(60))}");

    private int Offset(string text, LspPosition position) => new SourceDocument(_page, text, null).ToOffset(position);
}
