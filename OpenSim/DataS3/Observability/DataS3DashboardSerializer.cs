using System.Text.Json;

namespace OpenSim.DataS3.Observability
{
    public static class DataS3DashboardSerializer
    {
        private static readonly JsonSerializerOptions s_jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

        public static string ToJson(DataS3DashboardSnapshot snapshot)
        {
            return JsonSerializer.Serialize(snapshot, s_jsonOptions);
        }
    }
}