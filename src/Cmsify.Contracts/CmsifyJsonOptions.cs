using System.Text.Json;
using System.Text.Json.Serialization;

namespace SyntaxCircus.Cmsify.Contracts;

public static class CmsifyJsonOptions
{
    public static JsonSerializerOptions Create() => new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static void Configure(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
        options.Converters.Add(new JsonStringEnumConverter());
    }
}
