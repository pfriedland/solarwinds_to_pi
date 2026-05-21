using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

// QueryOrionNodes is a diagnostic utility for validating the SWIS REST query
// independently from any PI AF SDK dependencies.
internal static class QueryOrionNodes
{
    // This query mirrors the SolarWinds node selection used by the PI bridge.
    private const string Swql = """
SELECT
    n.NodeID,
    n.CustomProperties.Device_Type AS Device_Type,
    n.CustomProperties.Site_Type AS Site_Type,
    n.CustomProperties.Site AS Site,
    n.Caption,
    n.IPAddress,
    n.Status,
    n.Vendor,
    n.MachineType,
    n.ResponseTime,
    n.PercentLoss,
    CASE
        WHEN n.Status = 1 THEN 'Up'
        WHEN n.Status = 2 THEN 'Down'
        WHEN n.Status = 3 THEN 'Warning'
        WHEN n.Status = 9 THEN 'Unmanaged'
        WHEN n.Status = 12 THEN 'Unreachable'
        WHEN n.Status = 14 THEN 'Critical'
        ELSE 'Unknown'
    END AS StatusLabel
FROM Orion.Nodes n
WHERE n.CustomProperties.Site_Type LIKE 'Plant'
  AND (
      n.CustomProperties.Device_Type LIKE 'PI Gateways'
      OR n.CustomProperties.Device_Type LIKE 'IEC_104 Server'
      OR n.CustomProperties.Device_Type LIKE 'DNP3 Server'
      OR n.CustomProperties.Device_Type LIKE 'Firewall'
  )
ORDER BY n.Caption
""";

    private static readonly string[] Columns =
    [
        "NodeID",
        "Device_Type",
        "Site_Type",
        "Site",
        "Caption",
        "IPAddress",
        "Status",
        "StatusLabel",
        "Vendor",
        "MachineType",
        "ResponseTime",
        "PercentLoss"
    ];

    private static async Task<int> Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: dotnet run -- <orion-server> <username> [password] [--port 17774] [--skip-cert-validation] [--no-proxy]");
            return 2;
        }

        string server = args[0];
        string username = args[1];
        string? password = null;
        int port = 17774;
        bool skipCertificateValidation = false;
        bool noProxy = false;

        for (int index = 2; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--port":
                    if (index + 1 >= args.Length || !int.TryParse(args[++index], out port))
                    {
                        Console.Error.WriteLine("--port requires a numeric value.");
                        return 2;
                    }
                    break;
                case "--skip-cert-validation":
                    skipCertificateValidation = true;
                    break;
                case "--no-proxy":
                    noProxy = true;
                    break;
                default:
                    if (password is not null)
                    {
                        Console.Error.WriteLine($"Unexpected argument: {args[index]}");
                        return 2;
                    }

                    password = args[index];
                    break;
            }
        }

        password ??= ReadPassword("Orion password: ");

        // The no-proxy and certificate switches are useful for internal Orion
        // deployments where Windows proxy or private PKI settings can block tests.
        using HttpClient client = CreateClient(username, password, skipCertificateValidation, noProxy);
        Uri queryUri = new($"https://{server}:{port}/SolarWinds/InformationService/v3/Json/Query");

        using HttpResponseMessage response = await client.PostAsJsonAsync(queryUri, new QueryRequest(Swql));
        string responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"SWIS query failed: {(int)response.StatusCode} {response.ReasonPhrase}");
            Console.Error.WriteLine(responseBody);
            return 1;
        }

        QueryResponse? queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            responseBody,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (queryResponse?.Results is null)
        {
            Console.Error.WriteLine("SWIS returned an unexpected response:");
            Console.Error.WriteLine(responseBody);
            return 1;
        }

        PrintTsv(queryResponse.Results);
        return 0;
    }

    private static HttpClient CreateClient(string username, string password, bool skipCertificateValidation, bool noProxy)
    {
        var handler = new HttpClientHandler
        {
            PreAuthenticate = true,
            UseProxy = !noProxy
        };

        if (skipCertificateValidation)
        {
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(60)
        };

        string basicAuth = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{password}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basicAuth);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        return client;
    }

    private static void PrintTsv(IReadOnlyCollection<Dictionary<string, JsonElement>> rows)
    {
        Console.WriteLine(string.Join('\t', Columns));

        foreach (Dictionary<string, JsonElement> row in rows)
        {
            Console.WriteLine(string.Join('\t', Columns.Select(column => ToText(row, column))));
        }
    }

    private static string ToText(Dictionary<string, JsonElement> row, string column)
    {
        if (!row.TryGetValue(column, out JsonElement value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return string.Empty;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString();
    }

    private static string ReadPassword(string prompt)
    {
        Console.Write(prompt);
        var password = new StringBuilder();

        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return password.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (password.Length > 0)
                {
                    password.Length--;
                }

                continue;
            }

            password.Append(key.KeyChar);
        }
    }

    private sealed record QueryRequest([property: JsonPropertyName("query")] string Query);

    private sealed record QueryResponse(
        [property: JsonPropertyName("results")] List<Dictionary<string, JsonElement>> Results,
        [property: JsonPropertyName("totalRows")] int? TotalRows);
}
