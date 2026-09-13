using System.Diagnostics;
using System.Text;

namespace TanssGitConnector.Git;

/// <summary>Ein Aufruf von <c>git</c> und was dabei herauskam.</summary>
/// <param name="ExitCode">Der Rückgabewert.</param>
/// <param name="StandardOutput">Die Ausgabe, ohne abschließenden Zeilenumbruch.</param>
/// <param name="StandardError">Die Fehlerausgabe, ohne abschließenden Zeilenumbruch.</param>
public sealed record GitResult(int ExitCode, string StandardOutput, string StandardError)
{
    /// <summary>Ist der Aufruf gelungen?</summary>
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// Führt <c>git</c> aus.
/// </summary>
/// <remarks>
/// Eine Schnittstelle, damit die Auswertung der Ausgaben ohne ein echtes Repository prüfbar
/// bleibt. Die Zerlegung der <c>git log</c>-Ausgabe ist die fehleranfälligste Stelle dieses
/// Werkzeugs; sie gegen eine Attrappe zu prüfen ist der einzige Weg, dabei auch die Fälle zu
/// treffen, die man im Alltag selten erzeugt — leerer Betreff, mehrzeiliger Rumpf, Wurzelcommit.
/// </remarks>
public interface IGitRunner
{
    /// <summary>
    /// Ruft <c>git</c> im angegebenen Verzeichnis auf.
    /// </summary>
    /// <param name="workingDirectory">Das Arbeitsverzeichnis; üblicherweise die Wurzel des Repositorys.</param>
    /// <param name="arguments">Die Argumente, bereits zerlegt. Sie werden nicht durch eine Shell gereicht.</param>
    /// <param name="ct">Abbruchmarke.</param>
    /// <returns>Rückgabewert und beide Ausgaben.</returns>
    /// <exception cref="GitNotFoundException"><c>git</c> liess sich nicht starten.</exception>
    Task<GitResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments,
                             CancellationToken ct = default);
}

/// <summary>
/// Ruft das echte <c>git</c> auf.
/// </summary>
/// <remarks>
/// <para><b>Ohne Shell.</b> Die Argumente gehen einzeln in <see cref="ProcessStartInfo.ArgumentList"/>.
/// Ein Zweigname darf Leerzeichen, Anführungszeichen und Semikola enthalten; über eine
/// Befehlszeile gereicht wäre das eine Einladung, die niemand ausschlagen müsste.</para>
/// <para><b>Beide Ausgaben werden nebenläufig gelesen.</b> Wer erst auf das Ende wartet und dann
/// liest, verklemmt sich an einer vollen Ausgabepuffer-Pipe — bei <c>git log</c> eines grossen
/// Rumpfs ist das keine graue Theorie.</para>
/// <para><b>Die Sprache wird festgelegt.</b> <c>LC_ALL=C</c> und <c>LANG=C</c> sorgen dafür,
/// dass Fehlermeldungen von Git in derselben Sprache kommen, egal wie der Rechner eingestellt
/// ist — sonst prüft eine Fallunterscheidung auf einen englischen Text, den ein deutscher
/// Rechner nie liefert.</para>
/// </remarks>
public sealed class GitRunner : IGitRunner
{
    /// <summary>Zeitgrenze eines einzelnen Aufrufs.</summary>
    /// <remarks>
    /// Grosszügig für einen Aufruf, der Millisekunden dauert, und trotzdem nötig: Liegt das
    /// Repository auf einem Netzlaufwerk, das gerade nicht antwortet, hinge sonst der
    /// <c>post-commit</c>-Haken und mit ihm der Techniker.
    /// </remarks>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    private readonly string _executable;
    private readonly TimeSpan _timeout;

    /// <summary>Baut den Aufrufer.</summary>
    /// <param name="executable">Der Name oder Pfad von <c>git</c>; ohne Angabe aus dem Suchpfad.</param>
    /// <param name="timeout">Zeitgrenze je Aufruf; ohne Angabe <see cref="DefaultTimeout"/>.</param>
    public GitRunner(string executable = "git", TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        _executable = executable;
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <inheritdoc />
    public async Task<GitResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments,
                                          CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(arguments);

        ProcessStartInfo start = new()
        {
            FileName = _executable,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        // Kein Pager, keine Nachfrage, feste Sprache. Ein Haken hat kein Terminal, an dem
        // jemand "q" druecken oder ein Kennwort eintippen koennte - ohne diese drei Zeilen
        // bliebe er im Zweifel stehen, bis die Zeitgrenze greift.
        start.Environment["GIT_PAGER"] = "cat";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["LC_ALL"] = "C";
        start.Environment["LANG"] = "C";

        using Process process = new() { StartInfo = start };

        try
        {
            if (!process.Start())
            {
                throw new GitNotFoundException(
                    $"„{_executable}“ liess sich nicht starten. Ohne Git kann dieses Werkzeug "
                    + "keinen Commit lesen.");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
                                       or InvalidOperationException
                                       or PlatformNotSupportedException)
        {
            throw new GitNotFoundException(
                $"„{_executable}“ wurde nicht gefunden. Git muss installiert und im Suchpfad "
                + "erreichbar sein. Läuft der Aufruf aus einer Oberfläche heraus, bringt die "
                + "häufig einen eigenen, knappen Suchpfad mit — dann hilft ein vollständiger "
                + "Pfad zu git.", ex);
        }

        // Erst die Leseaufgaben anlegen, dann warten. Umgekehrt fuellt sich die Pipe, git
        // blockiert beim Schreiben, und beide warten aufeinander.
        Task<string> output = process.StandardOutput.ReadToEndAsync(ct);
        Task<string> error = process.StandardError.ReadToEndAsync(ct);

        // Git darf nicht auf eine Eingabe warten, die nie kommt.
        process.StandardInput.Close();

        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(_timeout);

        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);
            throw new GitException(
                $"git {string.Join(' ', arguments)} hat innerhalb von "
                + $"{_timeout.TotalSeconds:0} Sekunden nicht geantwortet. Liegt das Repository "
                + "auf einem Netzlaufwerk, ist dieses zu prüfen.");
        }

        return new GitResult(process.ExitCode,
                             (await output.ConfigureAwait(false)).TrimEnd('\r', '\n'),
                             (await error.ConfigureAwait(false)).TrimEnd('\r', '\n'));
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Schon beendet. Das ist der gewuenschte Zustand und kein Fehler.
        }
        catch (NotSupportedException)
        {
        }
    }
}
