using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rtfc.Core;

/// <summary>Indented, because people edit <c>config.json</c> by hand.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(RtfcConfig))]
internal sealed partial class ConfigJson : JsonSerializerContext;

public static class ConfigFile
{
    public static RtfcConfig Load(RtfcHome home)
    {
        if (!File.Exists(home.ConfigPath))
        {
            return RtfcConfig.Default;
        }

        return JsonSerializer.Deserialize(File.ReadAllBytes(home.ConfigPath), ConfigJson.Default.RtfcConfig) ?? RtfcConfig.Default;
    }

    public static void Save(RtfcHome home, RtfcConfig config) =>
        File.WriteAllBytes(home.ConfigPath, JsonSerializer.SerializeToUtf8Bytes(config, ConfigJson.Default.RtfcConfig));
}
