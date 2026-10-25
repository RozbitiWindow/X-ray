namespace XRay.Core.Tests;

/// <summary>Creates an isolated temporary directory with synthetic content and deletes it afterwards.</summary>
public sealed class TestDirectory : IDisposable
{
    public TestDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xray-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string CreateFile(string relativePath, int length)
    {
        string full = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        using var stream = new FileStream(full, FileMode.Create, FileAccess.Write);
        stream.SetLength(length);
        return full;
    }

    public string CreateDirectory(string relativePath)
    {
        string full = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(full);
        return full;
    }

    public void Dispose()
    {
        try
        {
            // Remove links first so deletion never follows them.
            foreach (string dir in Directory.EnumerateDirectories(Path, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            {
                var info = new DirectoryInfo(dir);
                if (info.LinkTarget is not null)
                {
                    info.Delete();
                }
            }

            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
