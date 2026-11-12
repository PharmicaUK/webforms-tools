using System.Text.Json;
using WebForms.Cli;

namespace WebForms.Tests;

[Collection("Console")]
public class ClaudeHookTests
{
    private static (int Exit, string Stderr) Hook(string eventName, string filePath)
    {
        var json = JsonSerializer.Serialize(new { hook_event_name = eventName, tool_name = "Edit", tool_input = new { file_path = filePath } });
        var err = new StringWriter();
        var old = Console.Error;
        Console.SetError(err);
        try
        {
            return (ClaudeHook.Run(new StringReader(json), []), err.ToString());
        }
        finally
        {
            Console.SetError(old);
        }
    }

    [Fact]
    public void Post_tool_use_on_markup_writes_designer()
    {
        var dir = Fixture.CopyToTemp();
        var page = Path.Combine(dir, "Default.aspx");
        Assert.Equal(ExitCodes.Ok, Hook("PostToolUse", page).Exit);
        Assert.True(File.Exists(page + ".designer.vb"));
    }

    [Fact]
    public void Post_tool_use_on_code_behind_regenerates_its_page()
    {
        var dir = Fixture.CopyToTemp();
        var page = Path.Combine(dir, "Default.aspx");
        Assert.Equal(ExitCodes.Ok, Hook("PostToolUse", page + ".vb").Exit);
        Assert.True(File.Exists(page + ".designer.vb"));
    }

    [Fact]
    public void Errors_are_fed_back_with_exit_2()
    {
        var dir = Fixture.CopyToTemp();
        var (exit, stderr) = Hook("PostToolUse", Path.Combine(dir, "Admin", "Users.aspx"));
        Assert.Equal(ExitCodes.Error, exit);
        Assert.Contains("WF3001", stderr);
    }

    [Fact]
    public void Pre_tool_use_blocks_designer_edits()
    {
        var (exit, stderr) = Hook("PreToolUse", @"C:\x\Default.aspx.designer.vb");
        Assert.Equal(ExitCodes.Error, exit);
        Assert.Contains("generated", stderr);
    }

    [Fact]
    public void Unrelated_files_are_ignored()
    {
        Assert.Equal(ExitCodes.Ok, Hook("PostToolUse", @"C:\x\readme.md").Exit);
        Assert.Equal(ExitCodes.Ok, Hook("PreToolUse", @"C:\x\Default.aspx").Exit);
    }
}
