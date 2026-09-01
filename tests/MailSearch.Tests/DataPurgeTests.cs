using MailSearch.Config;
using MailSearch.Storage;

namespace MailSearch.Tests;

public class DataPurgeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "minne-purge-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { Directory.Delete(_root, recursive: true); } catch { /* the tests delete it themselves */ }
    }

    private DataPaths Paths() => new(_root);

    [Fact]
    public void Measure_splits_database_models_and_the_rest()
    {
        var paths = Paths();
        File.WriteAllBytes(paths.DatabaseFile, new byte[1000]);
        File.WriteAllBytes(paths.DatabaseFile + "-wal", new byte[500]);
        Directory.CreateDirectory(Path.Combine(paths.ModelsDirectory, "nested"));
        File.WriteAllBytes(Path.Combine(paths.ModelsDirectory, "nested", "model.onnx"), new byte[300]);
        File.WriteAllBytes(paths.ConfigFile, new byte[20]);

        var usage = DataPurge.Measure(paths);

        Assert.Equal(1500, usage.DatabaseBytes);
        Assert.Equal(300, usage.ModelBytes);
        Assert.Equal(20, usage.OtherBytes);
        Assert.Equal(1820, usage.TotalBytes);
    }

    [Fact]
    public void Measure_of_an_untouched_directory_is_zero()
    {
        Assert.Equal(0, DataPurge.Measure(Paths()).TotalBytes);
    }

    [Fact]
    public void DeleteAll_removes_the_directory_including_subfolders()
    {
        var paths = Paths();
        File.WriteAllText(paths.ConfigFile, "{}");
        Directory.CreateDirectory(paths.ModelsDirectory);
        File.WriteAllText(Path.Combine(paths.ModelsDirectory, "tokenizer.json"), "{}");

        Assert.Empty(DataPurge.DeleteAll(paths));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void DeleteAll_on_a_missing_directory_is_a_no_op()
    {
        var paths = new DataPaths(Path.Combine(_root, "missing"));
        Directory.Delete(paths.Root);
        Assert.Empty(DataPurge.DeleteAll(paths));
    }

    [Fact]
    public void DeleteAll_reports_the_database_when_it_is_still_open()
    {
        if (!OperatingSystem.IsWindows()) return; // only Windows locks an open file against deletion

        var paths = Paths();
        using (var store = new SearchStore(paths.DatabaseFile))
        {
            var failures = DataPurge.DeleteAll(paths);
            Assert.Contains(failures, f => f.Contains("mail.db"));
            Assert.True(Directory.Exists(_root)); // the root survives so what is left stays findable
        }

        Assert.Empty(DataPurge.DeleteAll(paths));
    }
}
