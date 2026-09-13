using System.Text.Json;
using System.Text.Json.Serialization;

namespace TanssGitConnector.Storage.Config;

/// <summary>
/// Die eine Fassung der JSON-Einstellungen für <c>config.json</c>.
/// </summary>
/// <remarks>
/// <para>Bewusst genau ein Satz Einstellungen für Lesen und Schreiben. Zwei getrennte Sätze
/// wären der klassische Weg zu einer Datei, die sich schreiben, aber nicht mehr lesen lässt.</para>
///
/// <para>Kommentare und nachgestellte Kommata sind beim Lesen erlaubt: Die Datei wird von Hand
/// gepflegt, und wer eine Zeile mit <c>//</c> stilllegt, soll dafür keinen Ladefehler bekommen.
/// Geschrieben werden sie nie — beim nächsten Speichern sind sie fort.</para>
///
/// <para><b>Aufzählungen stehen als Text in der Datei</b>, nicht als Zahl: <c>"duration_mode":
/// "since_last_commit"</c>. Eine Zahl an dieser Stelle wäre beim Lesen der Datei nicht zu
/// deuten, und eine verrutschte Zahl änderte stillschweigend die gebuchte Dauer. Ganzzahlen
/// werden deshalb ausdrücklich <b>nicht</b> angenommen.</para>
/// </remarks>
public static class ConfigJson
{
    /// <summary>Die Einstellungen. Nach der ersten Verwendung unveränderlich.</summary>
    public static JsonSerializerOptions Options { get; } = Create();

    /// <summary>Erzeugt einen unabhängigen Satz Einstellungen, etwa für Tests.</summary>
    public static JsonSerializerOptions Create() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false),
        },
    };
}
