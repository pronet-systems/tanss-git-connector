namespace TanssGitConnector.Storage.Tests;

/// <summary>Ein Verzeichnis, das sich am Ende des Tests selbst wieder aufräumt.</summary>
/// <remarks>
/// Jeder Test bekommt sein eigenes. Ablagetests, die sich ein Verzeichnis teilen, finden
/// einander früher oder später — und dann ist nicht mehr zu sagen, welcher den anderen
/// umgeworfen hat.
/// </remarks>
internal sealed class Sandbox : IDisposable
{
    public Sandbox()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "tanss-git-test-" + Guid.NewGuid().ToString("N"));

        _ = Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Ein liegengebliebenes Testverzeichnis ist kein Grund, einen gruenen Lauf rot zu
            // faerben.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Eine Uhr, die stehen bleibt, bis ein Test sie weiterstellt.</summary>
/// <remarks>
/// Rückstau, Fälligkeit und Aufbewahrungsfristen hängen an der Zeit. Sie gegen die echte Uhr zu
/// prüfen hiesse, entweder zu warten oder zu raten.
/// </remarks>
internal sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now;

    public TestClock(DateTimeOffset? start = null) =>
        _now = start ?? new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan span) => _now += span;
}
