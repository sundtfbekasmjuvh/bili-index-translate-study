using System.Text.Json;
using System.Text.Json.Serialization;
using BiliIndexTranslate.Core.Translation;

namespace BiliIndexTranslate.Core.Settings;

/// <summary>How a screen snip or clipboard image is read.</summary>
public enum VisionMode
{
    /// <summary>OpenAI-compatible endpoints use the cloud vision model; other formats read the image locally.</summary>
    Auto,

    /// <summary>Always recognize the text on this PC first, then translate the text. The image is never uploaded.</summary>
    Local,

    /// <summary>Always send the image to the vision model (OpenAI-compatible endpoints only).</summary>
    Cloud,
}

public enum VisionRoute { Cloud, Local, Unsupported }

public static class VisionRouting
{
    public static VisionRoute Decide(VisionMode mode, ApiFormat format) => mode switch
    {
        VisionMode.Local => VisionRoute.Local,
        VisionMode.Cloud => format == ApiFormat.OpenAi ? VisionRoute.Cloud : VisionRoute.Unsupported,
        _ => format == ApiFormat.OpenAi ? VisionRoute.Cloud : VisionRoute.Local,
    };
}

/// <summary>
/// Reads <see cref="VisionMode"/> by name. An unknown name or an unexpected JSON type becomes the default
/// instead of failing the whole settings file.
/// </summary>
public sealed class VisionModeJsonConverter : JsonConverter<VisionMode>
{
    public override VisionMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return Enum.TryParse<VisionMode>(reader.GetString(), ignoreCase: true, out var v) && Enum.IsDefined(v) && !IsNumeric(reader.GetString())
                    ? v
                    : VisionMode.Auto;
            case JsonTokenType.StartObject:
            case JsonTokenType.StartArray:
                reader.Skip();
                return VisionMode.Auto;
            default:
                return VisionMode.Auto;
        }
    }

    private static bool IsNumeric(string? s) => !string.IsNullOrEmpty(s) && char.IsDigit(s[0]);

    public override void Write(Utf8JsonWriter writer, VisionMode value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}
