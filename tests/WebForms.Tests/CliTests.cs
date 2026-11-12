using WebForms.Cli;

namespace WebForms.Tests;

[CollectionDefinition("Console", DisableParallelization = true)]
public class ConsoleCollection;

[Collection("Console")]
public class CliTests
{
    private static (int Exit, string Stderr) Run(params string[] args)
    {
        var err = new StringWriter();
        var oldErr = Console.Error;
        Console.SetError(err);
        try
        {
            return (Program.Main(args), err.ToString());
        }
        finally
        {
            Console.SetError(oldErr);
        }
    }

    [Fact]
    public void Check_write_check_cycle()
    {
        var dir = Fixture.CopyToTemp();
        var page = Path.Combine(dir, "Default.aspx");
        var designer = page + ".designer.vb";

        var (exit, _) = Run("designer", page, "--check");
        Assert.Equal(ExitCodes.WouldChange, exit);
        Assert.False(File.Exists(designer));

        (exit, var stderr) = Run("designer", page);
        Assert.Equal(ExitCodes.Ok, exit);
        Assert.True(File.Exists(designer));
        Assert.Contains("WF0101", stderr); // new file not in the .vbproj

        var written = File.GetLastWriteTimeUtc(designer);
        (exit, _) = Run("designer", page, "--check");
        Assert.Equal(ExitCodes.Ok, exit);

        // Unchanged output is not rewritten (keeps file watchers quiet).
        (exit, _) = Run("designer", page);
        Assert.Equal(ExitCodes.Ok, exit);
        Assert.Equal(written, File.GetLastWriteTimeUtc(designer));
    }

    [Fact]
    public void Error_exit_code_and_message_format()
    {
        var dir = Fixture.CopyToTemp();
        var (exit, stderr) = Run("designer", Path.Combine(dir, "Admin", "Users.aspx"));
        Assert.Equal(ExitCodes.Error, exit);
        Assert.Matches(@"Users\.aspx\(2,\d+\): error WF3001: ", stderr);
        Assert.False(File.Exists(Path.Combine(dir, "Admin", "Users.aspx.designer.vb")));
    }

    [Fact]
    public void All_reports_errors_and_changes()
    {
        var dir = Fixture.CopyToTemp();
        var (exit, stderr) = Run("designer", "--all", dir, "--check");
        Assert.Equal(ExitCodes.Error, exit); // Users.aspx has an unknown prefix
        Assert.Contains("1 error(s)", stderr);
    }

    [Fact]
    public void Usage_errors()
    {
        Assert.Equal(ExitCodes.Usage, Run("designer").Exit);
        Assert.Equal(ExitCodes.Usage, Run("designer", "x.aspx", "--bogus").Exit);
        Assert.Equal(ExitCodes.Usage, Run("frobnicate").Exit);
    }

    [Fact]
    public void Missing_file_is_an_error()
    {
        var (exit, stderr) = Run("designer", "does-not-exist.aspx");
        Assert.Equal(ExitCodes.Error, exit);
        Assert.Contains("does-not-exist.aspx(1,1): error WF0001", stderr);
    }
}
