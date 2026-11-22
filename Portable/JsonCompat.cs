using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

// The shared stores and metadata readers use JavaScriptSerializer on .NET Framework.
// Preserve their dictionary/array representation and history timestamp format on modern .NET.
namespace System.Web.Script.Serialization
{
    internal sealed class JavaScriptSerializer
    {
        public int MaxJsonLength { get; set; } = 2097152;
        public int RecursionLimit { get; set; } = 100;

        private JsonSerializerOptions Options()
        {
            var options = new JsonSerializerOptions { MaxDepth = RecursionLimit, IncludeFields = true,
                PropertyNameCaseInsensitive = true };
            options.Converters.Add(new ValueConverter());
            options.Converters.Add(new DateConverter());
            return options;
        }

        public T Deserialize<T>(string json)
        {
            if (json == null || json.Length > MaxJsonLength) throw new ArgumentException("Invalid JSON length.");
            try { return JsonSerializer.Deserialize<T>(json, Options()); }
            catch (JsonException ex) { throw new ArgumentException("Invalid JSON data.", ex); }
        }

        public object DeserializeObject(string json) { return Deserialize<object>(json); }
        public string Serialize(object value)
        {
            string json = JsonSerializer.Serialize(value, Options());
            if (json.Length > MaxJsonLength) throw new InvalidOperationException("JSON data is too long.");
            return json;
        }

        private sealed class ValueConverter : JsonConverter<object>
        {
            public override object Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
            {
                using var document = JsonDocument.ParseValue(ref reader);
                return Unpack(document.RootElement);
            }

            private static object Unpack(JsonElement value)
            {
                switch (value.ValueKind)
                {
                    case JsonValueKind.Object:
                        var map = new Dictionary<string, object>(StringComparer.Ordinal);
                        foreach (var field in value.EnumerateObject()) map[field.Name] = Unpack(field.Value);
                        return map;
                    case JsonValueKind.Array: return value.EnumerateArray().Select(Unpack).ToArray();
                    case JsonValueKind.String: return value.GetString();
                    case JsonValueKind.True: return true;
                    case JsonValueKind.False: return false;
                    case JsonValueKind.Null: return null;
                    case JsonValueKind.Number:
                        if (value.TryGetInt32(out int integer)) return integer;
                        if (value.TryGetInt64(out long wide)) return wide;
                        if (value.TryGetDecimal(out decimal number)) return number;
                        return value.GetDouble();
                    default: throw new JsonException("Unsupported JSON value.");
                }
            }

            public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
            {
                if (value.GetType() != typeof(object)) JsonSerializer.Serialize(writer, value, value.GetType(), options);
                else { writer.WriteStartObject(); writer.WriteEndObject(); }
            }
        }

        private sealed class DateConverter : JsonConverter<DateTime>
        {
            public override DateTime Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
            {
                if (reader.TokenType != JsonTokenType.String) throw new JsonException("Invalid date.");
                string value = reader.GetString();
                if (value.StartsWith("/Date(", StringComparison.Ordinal) && value.EndsWith(")/", StringComparison.Ordinal) &&
                    long.TryParse(value.AsSpan(6, value.Length - 8), NumberStyles.AllowLeadingSign,
                        CultureInfo.InvariantCulture, out long milliseconds))
                {
                    try { return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime; }
                    catch (ArgumentOutOfRangeException ex) { throw new JsonException("Invalid date.", ex); }
                }
                if (reader.TryGetDateTime(out DateTime parsed)) return parsed;
                throw new JsonException("Invalid date.");
            }
            public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
            {
                var instant = new DateTimeOffset(value.ToUniversalTime());
                // The Framework reader recognizes its escaped date token before parsing plain strings.
                writer.WriteRawValue("\"\\/Date(" + instant.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + ")\\/\"");
            }
        }
    }
}
