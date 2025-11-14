using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiaryApp.Server.Storage;

namespace DiaryApp.Server.Serialization;

internal sealed class DescriptionEmbeddingConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            return reader.GetString();
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var values = new List<float>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray)
                {
                    break;
                }

                if (reader.TokenType != JsonTokenType.Number)
                {
                    throw new JsonException("Unexpected token while reading embedding array.");
                }

                values.Add(reader.GetSingle());
            }

            var floats = values.ToArray();
            var bytes = EmbeddingSerializer.SerializeBinary(floats);
            return Convert.ToBase64String(bytes);
        }

        throw new JsonException($"Unexpected token '{reader.TokenType}' when reading description embedding.");
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStringValue(value);
        }
    }
}
