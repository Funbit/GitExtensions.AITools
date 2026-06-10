using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitExtensions.AITools
{
    internal static class YouTrackIssueProvider
    {
        public class YouTrackIssueOutput
        {
            [JsonPropertyName("id")]
            public string Id { get; set; } = "";

            [JsonPropertyName("title")]
            public string Title { get; set; } = "";

            [JsonPropertyName("text")]
            public string Text { get; set; } = "";

            [JsonPropertyName("Tags")]
            public string Tags { get; set; } = "";
        }

        public class YouTrackIssueApiModel
        {
            [JsonPropertyName("idReadable")]
            public string? IdReadable { get; set; }

            [JsonPropertyName("summary")]
            public string? Summary { get; set; }

            [JsonPropertyName("description")]
            public string? Description { get; set; }

            [JsonPropertyName("tags")]
            public List<YouTrackTagApiModel>? Tags { get; set; }
        }

        public class YouTrackTagApiModel
        {
            [JsonPropertyName("name")]
            public string? Name { get; set; }
        }

        public static async Task<string> GetMyAssignedIssuesAsJsonAsync(string baseUrl, string token)
        {
            using var httpClient = new HttpClient
            {
                BaseAddress = new Uri(baseUrl)
            };

            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token.Replace("Bearer ", "", StringComparison.OrdinalIgnoreCase));
            httpClient.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));

            var requestUrl =
                "/api/issues" +
                "?query=for:%20me%20State%3a%20%7bIn%20Progress%7d%2c%20Review" + // filter only personal "In Progress" OR "Review" tasks
                "&fields=idReadable,summary,description,tags(name)" +
                "&$top=120";

            using var response = await httpClient.GetAsync(requestUrl);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();

            var apiIssues = JsonSerializer.Deserialize<List<YouTrackIssueApiModel>>(
                json,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }) ?? new List<YouTrackIssueApiModel>();

            var output = apiIssues.Select(item => new YouTrackIssueOutput
            {
                Id = item.IdReadable ?? "",
                Title = item.Summary ?? "",
                Text = Truncate(item.Description ?? "", 256),
                Tags = item.Tags == null || item.Tags.Count == 0
                    ? ""
                    : string.Join(", ", item.Tags
                        .Select(t => t.Name)
                        .Where(name => !string.IsNullOrWhiteSpace(name)))
            }).ToList();

            return JsonSerializer.Serialize(output, new JsonSerializerOptions
            {
                WriteIndented = true
            });
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            return value.Length <= maxLength
                ? value
                : value.Substring(0, maxLength);
        }
    }
}
