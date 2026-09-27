using System.Text.Json.Serialization;

namespace Rtfc.Storage;

/// <summary>Source-generated JSON for the columns that hold JSON (AOT, spec §16).</summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(string[]))]
internal sealed partial class StorageJson : JsonSerializerContext;
