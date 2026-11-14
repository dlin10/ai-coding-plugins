using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexSessionPulse.Infrastructure.Settings;

/// <summary>
/// Reads an enum from its exact name and maps any other value to an undefined member instead of throwing, so one bad
/// field falls back to its default on its own rather than discarding the whole settings file.
/// </summary>
/// <typeparam name="TEnum">The enum type.</typeparam>
internal sealed class LenientEnumConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    /// <inheritdoc />
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String
            && Enum.TryParse(reader.GetString(), ignoreCase: false, out TEnum value)
            && Enum.IsDefined(value)
            && !char.IsDigit(reader.GetString()![0]))
        {
            return value;
        }

        reader.Skip();

        // -1 is never a member here; the store's validation reports it and restores the default.
        return (TEnum)Enum.ToObject(typeof(TEnum), -1);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}
