using System.Text.Json;
using System.Text.Json.Serialization;

namespace MraReporting.Infrastructure;

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = Create();

    public static void Apply(JsonSerializerOptions target)
    {
        target.Converters.Add(new JsonStringEnumConverter());
        target.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    }

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Apply(options);
        return options;
    }
}
