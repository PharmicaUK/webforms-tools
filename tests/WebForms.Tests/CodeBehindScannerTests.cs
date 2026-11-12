using WebForms.Designer;

namespace WebForms.Tests;

public class CodeBehindScannerTests
{
    private static CodeBehindInfo Scan(string source, string cls = "Page1") =>
        CodeBehindScanner.ScanText(source.Replace("\n", "\r\n"), cls);

    [Fact]
    public void Fields_properties_events_but_not_locals()
    {
        var info = Scan("""
            Imports System
            Public Class Page1
                Inherits System.Web.UI.Page
                Protected WithEvents lblA As Label, lblB As Label
                Private _count As Integer = 1
                Public Property Title As String
                Public ReadOnly Property Total As Integer
                    Get
                        Dim notMember = 1
                        Return 1
                    End Get
                End Property
                Public Event Saved As EventHandler
                Protected Sub Page_Load(sender As Object, e As EventArgs) Handles Me.Load
                    Dim localVar As String = "x"
                    Dim f = Function(x As Integer) As Integer
                                Return x
                            End Function
                    Dim g = Sub()
                                Dim inLambda = 1
                            End Sub
                End Sub
                Dim afterMethods As String = "it's"
            End Class
            """);
        Assert.True(info.ClassFound);
        Assert.Null(info.Namespace);
        Assert.True(info.Members.SetEquals(["_count", "afterMethods", "lblA", "lblB", "Saved", "Title", "Total"]), string.Join(", ", info.Members));
    }

    [Fact]
    public void Multi_line_and_interpolated_strings_do_not_confuse_blocks()
    {
        var info = Scan("""
            Namespace Admin
                Partial Class Page1
                    Shared s As String = "line one
                        line two ' not a comment
                        End Class"
                    Sub X(id As Integer)
                        Dim q = $"a {If(id = 1, " in ('x')", "=y")} b {{literal}}"
                    End Sub
                    Private kept As Integer
                End Class
            End Namespace
            """);
        Assert.Equal("Admin", info.Namespace);
        Assert.Equal(new[] { "kept", "s" }, info.Members.Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Generic_class_with_same_name_is_a_different_type()
    {
        var info = Scan("""
            Public Class Page1
                Public A As Integer
            End Class
            Public Class Page1(Of T)
                Public B As T
            End Class
            """);
        Assert.Equal(["A"], info.Members);
    }
}
