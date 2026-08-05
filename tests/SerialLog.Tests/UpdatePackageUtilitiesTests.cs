using System.IO.Compression;
using System.Text;
using SerialLog.Update;

namespace SerialLog.Tests;

public sealed class UpdatePackageUtilitiesTests
{
    [Fact]
    public async Task ComputeSha256AndParseSha256_RoundTrip()
    {
        using var temp = new TemporaryDirectory();
        var file = Path.Combine(temp.Path, "package.zip");
        await File.WriteAllTextAsync(file, "serial-log-update", Encoding.UTF8);

        var hash = await UpdatePackageUtilities.ComputeSha256Async(file);
        var parsed = UpdatePackageUtilities.ParseSha256($"{hash.ToLowerInvariant()}  package.zip");

        Assert.Equal(hash, parsed);
    }

    [Fact]
    public void ExtractVerifiedZip_ExtractsPortablePackage()
    {
        using var temp = new TemporaryDirectory();
        var zip = Path.Combine(temp.Path, "package.zip");
        CreateZip(zip,
            (UpdatePaths.ApplicationFileName, "app"),
            (UpdatePaths.UpdaterFileName, "updater"),
            ("sub/config.json", "{}"));
        var staging = Path.Combine(temp.Path, "stage");

        UpdatePackageUtilities.ExtractVerifiedZip(zip, staging);

        Assert.Equal("app", File.ReadAllText(Path.Combine(staging, UpdatePaths.ApplicationFileName)));
        Assert.True(File.Exists(Path.Combine(staging, "sub", "config.json")));
    }

    [Fact]
    public void ExtractVerifiedZip_RejectsPathTraversalAndCleansStagingDirectory()
    {
        using var temp = new TemporaryDirectory();
        var zip = Path.Combine(temp.Path, "unsafe.zip");
        CreateZip(zip,
            (UpdatePaths.ApplicationFileName, "app"),
            (UpdatePaths.UpdaterFileName, "updater"),
            ("../outside.txt", "unsafe"));
        var staging = Path.Combine(temp.Path, "stage");

        Assert.Throws<InvalidDataException>(() =>
            UpdatePackageUtilities.ExtractVerifiedZip(zip, staging));

        Assert.False(Directory.Exists(staging));
        Assert.False(File.Exists(Path.Combine(temp.Path, "outside.txt")));
    }

    private static void CreateZip(string path, params (string Name, string Content)[] entries)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var item in entries)
        {
            var entry = archive.CreateEntry(item.Name);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(item.Content);
        }
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"serial-log-update-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch
        {
        }
    }
}
