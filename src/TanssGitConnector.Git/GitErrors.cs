namespace TanssGitConnector.Git;

/// <summary>Basis aller Fehler, die aus dem Umgang mit Git stammen.</summary>
public class GitException : Exception
{
    /// <summary>Der Rückgabewert von <c>git</c>, sofern es überhaupt gelaufen ist.</summary>
    public int? ExitCode { get; }

    public GitException(string message, int? exitCode = null, Exception? inner = null)
        : base(message, inner) => ExitCode = exitCode;
}

/// <summary>
/// <c>git</c> ist nicht aufrufbar.
/// </summary>
/// <remarks>
/// Getrennt von <see cref="GitException"/>, weil der Aufrufer anders reagieren muss: Hier hilft
/// kein zweiter Versuch, sondern nur eine Installation oder ein berichtigter Pfad. Der Fall
/// tritt in der Praxis fast nur auf, wenn der Haken aus einer Oberfläche heraus läuft, die einen
/// eigenen, knappen Suchpfad mitbringt.
/// </remarks>
public sealed class GitNotFoundException : GitException
{
    public GitNotFoundException(string message, Exception? inner = null)
        : base(message, inner: inner) { }
}

/// <summary>Das Verzeichnis ist kein Git-Repository — oder keines mit einem Commit darin.</summary>
public sealed class NotARepositoryException : GitException
{
    public NotARepositoryException(string message, int? exitCode = null)
        : base(message, exitCode) { }
}
