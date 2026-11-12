using WebForms.Markup;

namespace WebForms.Tests;

public class TokenizerTests
{
    [Fact]
    public void Directive_name_and_attributes()
    {
        var tokens = MarkupTokenizer.Tokenize("<%@ Page Language=\"vb\" Inherits='A.B' %>");
        var d = Assert.Single(tokens);
        Assert.Equal(TokenKind.Directive, d.Kind);
        Assert.Equal("Page", d.Name);
        Assert.Equal("vb", d.GetAttributeValue("language"));
        Assert.Equal("A.B", d.GetAttributeValue("Inherits"));
    }

    [Fact]
    public void Databinding_expression_with_inner_quotes_stays_inside_attribute()
    {
        var tokens = MarkupTokenizer.Tokenize("<asp:Label ID=\"x\" Text=\"<%# Eval(\"Name\") %>\" runat=\"server\" />");
        var tag = Assert.Single(tokens);
        Assert.Equal(TokenKind.StartTag, tag.Kind);
        Assert.True(tag.SelfClosing);
        Assert.Equal("<%# Eval(\"Name\") %>", tag.GetAttributeValue("Text"));
        Assert.True(MarkupTokenizer.IsRunatServer(tag));
    }

    [Fact]
    public void Server_comment_hides_tags()
    {
        var tokens = MarkupTokenizer.Tokenize("a<%-- <asp:Label runat=\"server\" /> --%>b");
        Assert.DoesNotContain(tokens, t => t.Kind == TokenKind.StartTag);
        Assert.Contains(tokens, t => t.Kind == TokenKind.ServerComment);
    }

    [Fact]
    public void Html_comment_does_not_hide_tags()
    {
        var tokens = MarkupTokenizer.Tokenize("<!-- <asp:Label runat=\"server\" /> -->");
        Assert.Contains(tokens, t => t.Kind == TokenKind.StartTag && t.Name == "asp:Label");
    }

    [Fact]
    public void Lone_less_than_is_text()
    {
        var tokens = MarkupTokenizer.Tokenize("if (a < b && c > d) <b>x</b>");
        Assert.Equal(["b"], tokens.Where(t => t.Kind == TokenKind.StartTag).Select(t => t.Name));
    }

    [Fact]
    public void Server_script_body_is_opaque()
    {
        var tokens = MarkupTokenizer.Tokenize("<script runat=\"server\">Dim s = \"<asp:Label runat='server' />\"</script>");
        Assert.Equal([TokenKind.StartTag, TokenKind.ServerScript, TokenKind.EndTag], tokens.Select(t => t.Kind));
    }

    [Fact]
    public void Positions_are_one_based_line_and_column()
    {
        var tokens = MarkupTokenizer.Tokenize("x\r\n  <asp:Label runat=\"server\" />");
        var tag = tokens.Single(t => t.Kind == TokenKind.StartTag);
        Assert.Equal(2, tag.Start.Line);
        Assert.Equal(3, tag.Start.Column);
    }
}
