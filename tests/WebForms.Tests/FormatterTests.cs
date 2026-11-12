using System.Text;
using WebForms.Cli;
using WebForms.Formatting;

namespace WebForms.Tests;

public class FormatterTests
{
    private static string Crlf(string s) => s.ReplaceLineEndings("\r\n");

    private static string Format(string input, LineRange? lines = null, string? fileName = null)
    {
        var result = VbFormatter.Format(Crlf(input), lines, fileName);
        Assert.Empty(result.Diagnostics);
        Assert.NotNull(result.Text);
        return result.Text!;
    }

    [Fact]
    public void Pretty_lists_keywords_and_tokens_like_visual_studio()
    {
        var output = Format("""
            public class Foo
              private  shared readonly _count as integer=0
                overridable public function GetIt() as string
                    if _count>0 then
                  return "a"
                    elseif _count<0
                        return "b"
                    endif
                    dim s = "a" & _
                        "b"
                    select _count
                        case 1: return s
                    end select
                    return &H00FF.ToString()
                end function
            end class
            """);

        Assert.Equal(Crlf("""
            Public Class Foo
                Private Shared ReadOnly _count As Integer = 0
                Public Overridable Function GetIt() As String
                    If _count > 0 Then
                        Return "a"
                    ElseIf _count < 0 Then
                        Return "b"
                    End If
                    Dim s = "a" &
                        "b"
                    Select Case _count
                        Case 1 : Return s
                    End Select
                    Return &HFF.ToString()
                End Function
            End Class
            """), output);
    }

    [Fact]
    public void Identifiers_keep_their_case()
    {
        // Identifier case correction needs the whole project; the formatter leaves identifiers alone.
        var output = Format("""
            Class C
                Sub M()
                    response.write(MYSTRING)
                    Dim loginStatus = 1
                    Select Case loginStatus
                        Case LoginStatus.Success
                    End Select
                End Sub
                Private Const MyString As String = "q"
            End Class
            """);

        Assert.Contains("response.write(MYSTRING)", output, StringComparison.Ordinal);
        Assert.Contains("Case LoginStatus.Success", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Adds_parentheses_to_a_call_without_a_blank_line_before_a_block()
    {
        var output = Format("""
            Class C
                Sub M()
                    DoIt
                    If True Then
                    End If
                End Sub
                Sub DoIt()
                End Sub
            End Class
            """);

        Assert.Contains(Crlf("        DoIt()\n        If True Then\n"), output, StringComparison.Ordinal);
    }

    [Fact]
    public void Setter_value_gets_the_parameter_spelling()
    {
        var output = Format("""
            Class C
                Private _x As String
                Property X As String
                    Get
                        Return _x
                    End Get
                    Set
                        _x = value
                        Dim o = New With {.value = value}
                    End Set
                End Property
                Sub M(value As String)
                    _x = value
                End Sub
            End Class
            """);

        Assert.Contains("_x = Value\r\n", output, StringComparison.Ordinal);
        Assert.Contains("New With {.value = Value}", output, StringComparison.Ordinal);
        Assert.Contains("Sub M(value As String)\r\n        _x = value\r\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Is_idempotent_and_keeps_line_endings()
    {
        const string input = "class C\n  sub M()\n  end sub\nend class\n";
        var once = VbFormatter.Format(input).Text!;
        Assert.Equal("Class C\n    Sub M()\n    End Sub\nEnd Class\n", once);
        Assert.Equal(once, VbFormatter.Format(once).Text);
    }

    [Fact]
    public void Line_range_only_touches_those_lines()
    {
        var output = Format("""
            class C
                sub A()
                  dim x as integer=1
                end sub
                sub B()
                  dim y as integer=1
                end sub
            end class
            """, new LineRange(5, 7));

        Assert.Equal(Crlf("""
            class C
                sub A()
                  dim x as integer=1
                end sub
                Sub B()
                    Dim y As Integer = 1
                End Sub
            end class
            """), output);
    }

    [Fact]
    public void Line_range_reindents_its_first_line()
    {
        // The pair "previous line's last token / first token of the range" decides the first line's indentation.
        var output = Format("""
            Class C
                Sub A()
            dim x as integer=1
              dim y as integer=1
                End Sub
            End Class
            """, new LineRange(3, 3));

        Assert.Equal(Crlf("""
            Class C
                Sub A()
                    Dim x As Integer = 1
              dim y as integer=1
                End Sub
            End Class
            """), output);
    }

    [Fact]
    public void Refuses_code_with_syntax_errors()
    {
        var result = VbFormatter.Format(Crlf("""
            Class C
                Sub M()
                    If True Then
                        M()
                End Sub
            End Class
            """));

        Assert.Null(result.Text);
        var d = Assert.Single(result.Diagnostics);
        Assert.Equal("error", d.Severity);
        Assert.Equal("BC30081", d.Code);
        Assert.Equal((3, 9), (d.Line, d.Column));
    }

    [Theory]
    [InlineData("Default.aspx.designer.vb", "Class C\r\nend class\r\n")]
    [InlineData("Reference.vb", "'------\r\n' <auto-generated>\r\n'------\r\nclass C\r\nend class\r\n")]
    public void Leaves_generated_code_alone(string fileName, string input)
    {
        var result = VbFormatter.Format(input, fileName: fileName);
        Assert.NotNull(result.SkipReason);
        Assert.Equal(input, result.Text);
    }
}

[Collection("Console")]
public class FormatCommandTests
{
    private static (int Exit, byte[] Stdout, string Stderr) Run(byte[] stdin, params string[] args)
    {
        var err = new StringWriter();
        var oldErr = Console.Error;
        Console.SetError(err);
        try
        {
            using var input = new MemoryStream(stdin);
            using var output = new MemoryStream();
            int exit = FormatCommand.Run(args, input, output);
            return (exit, output.ToArray(), err.ToString());
        }
        finally
        {
            Console.SetError(oldErr);
        }
    }

    private static byte[] Utf8(string s, bool bom = false) => [.. (bom ? new byte[] { 0xEF, 0xBB, 0xBF } : []), .. Encoding.UTF8.GetBytes(s)];

    [Fact]
    public void Formats_stdin_to_stdout_keeping_the_bom()
    {
        var (exit, stdout, _) = Run(Utf8("class C\r\nend class\r\n", bom: true));
        Assert.Equal(ExitCodes.Ok, exit);
        Assert.Equal(Utf8("Class C\r\nEnd Class\r\n", bom: true), stdout);

        (exit, stdout, _) = Run(Utf8("class C\r\nend class\r\n"));
        Assert.Equal(Utf8("Class C\r\nEnd Class\r\n"), stdout);
    }

    [Fact]
    public void Check_reports_without_printing()
    {
        var (exit, stdout, stderr) = Run(Utf8("class C\r\nend class\r\n"), "--check", "--stdin-filename", @"C:\site\C.vb");
        Assert.Equal(ExitCodes.WouldChange, exit);
        Assert.Empty(stdout);
        Assert.Contains(@"C:\site\C.vb(1,1): warning WF4100: ", stderr, StringComparison.Ordinal);

        (exit, _, _) = Run(Utf8("Class C\r\nEnd Class\r\n"), "--check");
        Assert.Equal(ExitCodes.Ok, exit);
    }

    [Fact]
    public void Syntax_errors_exit_2_with_nothing_on_stdout()
    {
        var (exit, stdout, stderr) = Run(Utf8("Class C\r\n    Sub M()\r\n        If True Then\r\n    End Sub\r\nEnd Class\r\n"), "--stdin-filename", "C.vb");
        Assert.Equal(ExitCodes.Error, exit);
        Assert.Empty(stdout);
        Assert.Matches(@"^C\.vb\(3,9\): error BC30081: ", stderr);
    }

    [Fact]
    public void Invalid_utf8_is_an_error()
    {
        var (exit, stdout, stderr) = Run([0x43, 0x6C, 0xE9, 0x0D, 0x0A]);
        Assert.Equal(ExitCodes.Error, exit);
        Assert.Empty(stdout);
        Assert.Contains("error WF4001", stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--lines", "5")]
    [InlineData("--lines", "0-3")]
    [InlineData("--lines", "4-2")]
    [InlineData("--lines", "9-9")]
    [InlineData("C.vb")]
    public void Usage_errors_exit_3(params string[] args)
    {
        var (exit, stdout, _) = Run(Utf8("Class C\r\nEnd Class\r\n"), args);
        Assert.Equal(ExitCodes.Usage, exit);
        Assert.Empty(stdout);
    }

    [Fact]
    public void Lines_end_past_the_input_is_clamped()
    {
        var (exit, stdout, _) = Run(Utf8("class C\r\nend class\r\n"), "--lines", "2-99");
        Assert.Equal(ExitCodes.Ok, exit);
        Assert.Equal(Utf8("class C\r\nEnd Class\r\n"), stdout);
    }
}
