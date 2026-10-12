using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Insomnia.Core.Store;

/// <summary>JSON settings shared by every file Insomnia writes.</summary>
public static partial class Json
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.Converters.Add(new StrictDateConverter());
        return options;
    }

    /// <summary>
    /// Parses a date in the one form Insomnia writes: <c>2027-01-15T08:00:00Z</c>,
    /// or the same with an offset such as <c>+02:00</c> in place of <c>Z</c>,
    /// in whole seconds, naming a date and time that exist, years 1970 to 9999.
    /// </summary>
    public static DateTimeOffset? ParseDate(string? text)
    {
        if (text is null)
        {
            return null;
        }
        var match = DatePattern().Match(text);
        if (!match.Success)
        {
            return null;
        }
        var year = int.Parse(match.Groups["y"].Value, CultureInfo.InvariantCulture);
        if (year < 1970)
        {
            return null;
        }
        var format = match.Groups["z"].Value == "Z" ? "yyyy-MM-dd'T'HH:mm:ss'Z'" : "yyyy-MM-dd'T'HH:mm:sszzz";
        var styles = match.Groups["z"].Value == "Z" ? DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal : DateTimeStyles.None;
        if (!DateTimeOffset.TryParseExact(text, format, CultureInfo.InvariantCulture, styles, out var value))
        {
            return null;
        }
        return value.ToUniversalTime();
    }

    public static string FormatDate(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^(?<y>\d{4})-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?<z>Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex DatePattern();

    private sealed class StrictDateConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException("date must be a string");
            }
            return ParseDate(reader.GetString()) ?? throw new JsonException($"invalid date '{reader.GetString()}'");
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(FormatDate(value));
    }
}
