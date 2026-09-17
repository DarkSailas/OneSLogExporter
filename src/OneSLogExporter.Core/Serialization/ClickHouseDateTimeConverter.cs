using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OneSLogExporter.Core.Serialization;

/// <summary>
/// Конвертер DateTime для совместимого формата как с ClickHouse (JSONEachRow), так и с Elasticsearch.
/// ClickHouse ожидает дату в виде 'yyyy-MM-dd HH:mm:ss.fff' без суффикса 'Z'.
/// </summary>
public sealed class ClickHouseDateTimeConverter : JsonConverter<DateTime>
{
    private const string Format = "yyyy-MM-dd HH:mm:ss.fff";

    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && DateTime.TryParse(reader.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
        {
            return dt;
        }

        return reader.GetDateTime();
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString(Format, CultureInfo.InvariantCulture));
    }
}

/// <summary>
/// Конвертер DateTime? для совместимого формата с ClickHouse (JSONEachRow) и Elasticsearch.
/// </summary>
public sealed class ClickHouseNullableDateTimeConverter : JsonConverter<DateTime?>
{
    private const string Format = "yyyy-MM-dd HH:mm:ss.fff";

    public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        if (reader.TokenType == JsonTokenType.String)
        {
            var str = reader.GetString();
            if (string.IsNullOrEmpty(str)) return null;
            if (DateTime.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
                return dt;
        }

        return reader.GetDateTime();
    }

    public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
        {
            writer.WriteStringValue(value.Value.ToString(Format, CultureInfo.InvariantCulture));
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}