namespace TanssGitConnector.Api;

/// <summary>
/// Zeitrechnung gegenüber TANSS.
/// </summary>
/// <remarks>
/// TANSS rechnet in <b>Unix-Sekunden</b>, nicht in Millisekunden.
///
/// <para><b>Millisekunden sind kein sauberer Fehler, sondern stille Datenverfälschung.</b>
/// Ein Millisekundenwert wird nicht abgewiesen, sondern als Sekundenwert gelesen — daraus wird
/// ein Datum im Jahr 51667, das erst weit hinten auffällt. Deshalb gibt es in diesem Projekt
/// genau diesen einen Umrechner und bewusst keinen für Millisekunden.</para>
///
/// <para>Das ist hier besonders leicht zu verfehlen: Git liefert seine Zeitstempel als
/// Unix-<b>Sekunden</b> (<c>%ct</c>, <c>%at</c>), und wer sie versehentlich mit 1000
/// multipliziert, bekommt keine Fehlermeldung, sondern eine Fernwartung im fünften
/// Jahrtausend.</para>
/// </remarks>
public static class TanssTime
{
    /// <summary>Wandelt einen Zeitpunkt in Unix-Sekunden.</summary>
    public static long ToUnixSeconds(DateTimeOffset moment) => moment.ToUnixTimeSeconds();

    /// <summary>Wandelt einen lokalen Zeitpunkt in Unix-Sekunden.</summary>
    public static long ToUnixSeconds(DateTime moment) =>
        new DateTimeOffset(moment.ToUniversalTime(), TimeSpan.Zero).ToUnixTimeSeconds();

    /// <summary>Liest Unix-Sekunden als Zeitpunkt. 0 bedeutet „nicht gesetzt“ und ergibt <c>null</c>.</summary>
    public static DateTimeOffset? FromUnixSeconds(long seconds) =>
        seconds == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(seconds);
}
