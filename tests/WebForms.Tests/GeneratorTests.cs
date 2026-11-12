using WebForms.Designer;

namespace WebForms.Tests;

public class GeneratorTests
{
    private static GenerationResult Generate(params string[] path) => DesignerGenerator.Generate(Fixture.Path_(path));

    [Fact]
    public void Default_page_fields_in_document_order_with_resolved_types()
    {
        var result = Generate("Default.aspx");
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
        var model = result.Model!;
        Assert.Equal("_Default", model.ClassName);
        Assert.Null(model.Namespace);
        Assert.Equal("SampleApp.SiteMaster", model.MasterTypeName);
        Assert.True(model.MasterTypeDeclared);

        const string Web = "Global.System.Web.UI.WebControls.";
        const string Html = "Global.System.Web.UI.HtmlControls.";
        (string, string)[] expected =
        [
            ("divOuter", Html + "HtmlGenericControl"),
            ("litInsideOuter", Web + "Literal"),
            ("txtName", Web + "TextBox"),
            ("btnSave", Web + "Button"),
            ("ddlChoice", Web + "DropDownList"),
            ("inpText", Html + "HtmlInputText"),
            ("inpCheck", Html + "HtmlInputCheckBox"),
            ("inpEmail", Html + "HtmlInputGenericControl"),
            ("inpNoType", Html + "HtmlInputText"),
            ("lnkHome", Html + "HtmlAnchor"),
            ("spanGeneric", Html + "HtmlGenericControl"),
            ("frame1", Html + "HtmlIframe"),
            ("tblData", Html + "HtmlTable"),
            ("rowFirst", Html + "HtmlTableRow"),
            ("cellFirst", Html + "HtmlTableCell"),
            ("lblInCell", Web + "Label"),
            ("rowInFoot", Html + "HtmlTableRow"),
            ("lblInFoot", Web + "Label"),
            ("rptItems", Web + "Repeater"),
            ("upMain", "Global.System.Web.UI.UpdatePanel"),
            ("lblInUpdatePanel", Web + "Label"),
            ("gvRows", Web + "GridView"),
            ("mvSteps", Web + "MultiView"),
            ("vwOne", Web + "View"),
            ("pnlInView", Web + "Panel"),
            ("widget1", "Global.SampleApp.Controls.Widget"),
            ("noCode1", "Global.System.Web.UI.UserControl"),
            ("footer1", "Global.SampleApp.Footer"),
            ("lblInHtmlComment", Web + "Label"),
        ];

        Assert.Equal(expected, model.Fields.Select(f => (f.Name, f.TypeName)));
    }

    [Fact]
    public void Table_colgroup_and_tfoot_are_ignored_without_warnings()
    {
        var result = Generate("Default.aspx");
        Assert.DoesNotContain(result.Diagnostics, d => d.Code is "WF3001" or "WF2001");
    }

    [Theory]
    [InlineData("Content1")] // asp:Content never gets a field
    [InlineData("lblInScript")] // inside <script runat="server">
    [InlineData("lblInTemplate")] // Repeater.ItemTemplate is a multi-instance template
    [InlineData("hlInGrid")] // TemplateField.ItemTemplate inside GridView.Columns
    [InlineData("lblInServerComment")]
    [InlineData("lblDeclaredInCodeBehind")] // already declared in Default.aspx.vb
    [InlineData("notAMember")]
    public void Excluded_controls(string id)
    {
        Assert.DoesNotContain(Generate("Default.aspx").Model!.Fields, f => f.Name == id);
    }

    [Fact]
    public void Ids_with_code_blocks_are_ignored()
    {
        Assert.DoesNotContain(Generate("Default.aspx").Model!.Fields, f => f.Name.Contains("lblDynamic"));
    }

    [Fact]
    public void Namespace_relative_to_root_namespace()
    {
        var model = Generate("Admin", "Clean.aspx").Model!;
        Assert.Equal("Admin", model.Namespace);
        Assert.Equal("Clean", model.ClassName);
        Assert.Null(model.MasterTypeName);
    }

    [Fact]
    public void Unknown_prefix_is_an_error_with_position()
    {
        var result = Generate("Admin", "Users.aspx");
        Assert.True(result.HasErrors);
        var d = Assert.Single(result.Diagnostics, x => x.Severity == Severity.Error);
        Assert.Equal("WF3001", d.Code);
        Assert.Equal(2, d.Line);
        Assert.Matches(@"Users\.aspx\(2,\d+\): error WF3001: Unknown tag prefix 'ajax'", d.ToString());
    }

    [Fact]
    public void CodeFile_pages_are_skipped()
    {
        Assert.NotNull(Generate("WebSite.aspx").SkipReason);
    }

    [Fact]
    public void Master_page_fields()
    {
        var model = Generate("Site.Master").Model!;
        Assert.Equal("SiteMaster", model.ClassName);
        Assert.Equal(["Head1", "form1", "Main"], model.Fields.Select(f => f.Name));
        Assert.Equal("Global.System.Web.UI.HtmlControls.HtmlHead", model.Fields[0].TypeName);
    }
}
