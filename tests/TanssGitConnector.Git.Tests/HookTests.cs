using System.Text;
using Xunit;

namespace TanssGitConnector.Git.Tests;

/// <summary>
/// Der Haken selbst: sein Text, seine Erkennungsmarke und der Umgang mit fremden Haken.
/// </summary>
/// <remarks>
/// Diese Datei wird von einer Shell ausgeführt, und zwar auf drei Betriebssystemen. Die drei
/// Fallen — Byte-Order-Mark, Wagenrücklauf, Leerzeichen im Pfad — fallen nicht beim Übersetzen
/// auf, sondern bei jedem Commit des Kunden.
/// </remarks>
public class HookTests
{
    [Fact]
    public void Der_Haken_traegt_die_Erkennungsmarke()
    {
        string script = HookScript.Build("/usr/local/bin/tanss-git");

        Assert.Contains(HookScript.Marker, script, StringComparison.Ordinal);
        Assert.True(HookScript.IsOurs(script));
    }

    [Fact]
    public void Ein_fremder_Haken_wird_nicht_als_unserer_erkannt()
    {
        Assert.False(HookScript.IsOurs("#!/bin/sh\nmake test\n"));
        Assert.False(HookScript.IsOurs(null));
    }

    [Fact]
    public void Der_Haken_beginnt_mit_der_Kennzeichnung_des_Interpreters()
    {
        Assert.StartsWith("#!/bin/sh\n", HookScript.Build("/usr/local/bin/tanss-git"),
                          StringComparison.Ordinal);
    }

    /// <summary>
    /// Keine Wagenrückläufe — auch nicht, wenn unter Windows gebaut wurde.
    /// </summary>
    /// <remarks>
    /// Ein Skript mit CRLF scheitert unter Unix an <c>bad interpreter: /bin/sh^M</c>. Der Fehler
    /// sieht nach einem kaputten Programm aus und ist keiner.
    /// </remarks>
    [Fact]
    public void Der_Haken_enthaelt_keine_Wagenrufe()
    {
        Assert.DoesNotContain('\r', HookScript.Build("/usr/local/bin/tanss-git"));
    }

    [Fact]
    public void Der_Haken_endet_mit_Null()
    {
        Assert.Contains("exit 0", HookScript.Build("/usr/local/bin/tanss-git"),
                        StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("C:\\Program Files\\tanss-git.exe", "'C:/Program Files/tanss-git.exe'")]
    [InlineData("/usr/local/bin/tanss-git", "'/usr/local/bin/tanss-git'")]
    [InlineData("/home/o'brien/bin/tanss-git", "'/home/o'\\''brien/bin/tanss-git'")]
    public void Der_Programmpfad_wird_fuer_die_Shell_aufbereitet(string path, string expected) =>
        Assert.Equal(expected, HookScript.ForShell(path));

    /// <summary>
    /// Ein Dollarzeichen im Pfad bleibt ein Dollarzeichen.
    /// </summary>
    /// <remarks>
    /// In doppelten Anführungszeichen würde die Shell daraus eine Ersetzung machen — und der
    /// Haken riefe ein Programm auf, das es nicht gibt.
    /// </remarks>
    [Fact]
    public void Ein_Dollarzeichen_im_Pfad_wird_nicht_ersetzt()
    {
        Assert.Equal("'/opt/$tools/tanss-git'", HookScript.ForShell("/opt/$tools/tanss-git"));
    }

    [Fact]
    public void Ohne_Datei_meldet_die_Pruefung_nichts_vorhanden()
    {
        using Sandbox sandbox = new();

        HookStatus status = HookInstaller.Inspect(sandbox.Path);

        Assert.Equal(HookState.Missing, status.State);
        Assert.EndsWith(HookScript.FileName, status.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void Eingerichtet_und_wieder_entfernt()
    {
        using Sandbox sandbox = new();

        HookStatus installed = HookInstaller.Install(sandbox.Path, "/usr/local/bin/tanss-git");
        Assert.Equal(HookState.Ours, installed.State);
        Assert.Equal(HookState.Ours, HookInstaller.Inspect(sandbox.Path).State);

        HookStatus removed = HookInstaller.Remove(sandbox.Path);
        Assert.Equal(HookState.Missing, removed.State);
        Assert.False(File.Exists(installed.Path));
    }

    [Fact]
    public void Der_geschriebene_Haken_traegt_keinen_Vorspann()
    {
        using Sandbox sandbox = new();

        HookStatus installed = HookInstaller.Install(sandbox.Path, "/usr/local/bin/tanss-git");
        byte[] bytes = File.ReadAllBytes(installed.Path);

        // Ein Byte-Order-Mark vor #! macht aus der Kennzeichnung des Interpreters Zeichensalat.
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.Equal((byte)'#', bytes[0]);
    }

    /// <summary>
    /// Ein fremder Haken wird nicht überschrieben — und nicht gelöscht.
    /// </summary>
    /// <remarks>
    /// Er kann ein Prüflauf, eine Signatur oder eine Benachrichtigung sein. Ihn lautlos zu
    /// entfernen wäre schlimmer als eine nicht gebuchte Arbeitszeit.
    /// </remarks>
    [Fact]
    public void Ein_fremder_Haken_bleibt_unangetastet()
    {
        using Sandbox sandbox = new();
        string path = Path.Combine(sandbox.Path, HookScript.FileName);
        File.WriteAllText(path, "#!/bin/sh\nmake test\n", Encoding.UTF8);

        Assert.Equal(HookState.Foreign, HookInstaller.Inspect(sandbox.Path).State);
        Assert.Throws<HookException>(() => HookInstaller.Install(sandbox.Path, "/bin/tanss-git"));
        Assert.Equal("#!/bin/sh\nmake test\n", File.ReadAllText(path));

        HookStatus afterRemove = HookInstaller.Remove(sandbox.Path);
        Assert.Equal(HookState.Foreign, afterRemove.State);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Mit_force_wird_ersetzt_und_der_alte_Stand_aufbewahrt()
    {
        using Sandbox sandbox = new();
        string path = Path.Combine(sandbox.Path, HookScript.FileName);
        File.WriteAllText(path, "#!/bin/sh\nmake test\n", Encoding.UTF8);

        HookStatus installed = HookInstaller.Install(sandbox.Path, "/bin/tanss-git", force: true);

        Assert.Equal(HookState.Ours, installed.State);
        Assert.True(HookScript.IsOurs(File.ReadAllText(path)));

        string[] preserved = Directory.GetFiles(sandbox.Path, HookScript.FileName + ".vorher-*");
        Assert.Single(preserved);
        Assert.Contains("make test", File.ReadAllText(preserved[0]), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/a/b", "/a/b/", true)]
    [InlineData("/a/b", "/a/c", false)]
    public void Pfadvergleich_uebersieht_den_letzten_Schraegstrich(string left, string right,
                                                                   bool expected) =>
        Assert.Equal(expected, HookInstaller.SamePath(left, right));

    /// <summary>Ein Verzeichnis, das sich am Ende des Tests selbst wieder aufräumt.</summary>
    private sealed class Sandbox : IDisposable
    {
        public Sandbox()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "tanss-git-test-" + Guid.NewGuid().ToString("N"));
            _ = Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Ein liegengebliebenes Testverzeichnis ist kein Grund, einen gruenen Lauf
                // rot zu faerben.
            }
        }
    }
}
