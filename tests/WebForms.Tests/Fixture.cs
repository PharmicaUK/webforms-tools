namespace WebForms.Tests;

internal static class Fixture
{
    public static string SampleApp => Path.Combine(AppContext.BaseDirectory, "Fixtures", "SampleApp");

    public static string Path_(params string[] parts) => Path.Combine([SampleApp, .. parts]);

    /// <summary>A private copy of the sample app, for tests that write files.</summary>
    public static string CopyToTemp()
    {
        var dest = Path.Combine(Path.GetTempPath(), "webforms-tests", Guid.NewGuid().ToString("N"));
        foreach (var file in Directory.EnumerateFiles(SampleApp, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(dest, Path.GetRelativePath(SampleApp, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        return dest;
    }
}
