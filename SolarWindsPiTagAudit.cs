using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using OSIsoft.AF.Asset;
using OSIsoft.AF.PI;
using OSIsoft.AF.Time;

// SolarWindsPiTagAudit is the production-oriented bridge process.
// It polls SolarWinds through SWIS REST, derives deterministic PI tag names,
// creates missing PI points, and writes current ResponseTime/PercentLoss snapshots.
internal static class SolarWindsPiTagAudit
{
    // Keep the SWQL in one place so the PI tag population is driven by the same
    // SolarWinds node criteria every run.
    private const string Swql = @"
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
ORDER BY n.Caption";

#if !WINDOWS_SERVICE
    private static int Main(string[] args)
    {
        try
        {
            Options options = Options.Parse(args);
            if (options.ShowHelp)
            {
                Options.PrintUsage();
                return 0;
            }

            if (!options.IsValid())
            {
                Options.PrintUsage();
                return 2;
            }

            string password = options.OrionPassword ?? ReadPassword("Orion password: ");
            Run(options, password);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("SolarWinds to PI tag audit failed.");
            Console.Error.WriteLine(ex.GetType().FullName + ": " + ex.Message);
            return 1;
        }
    }
#endif

    internal static void Run(Options options, string password)
    {
        do
        {
            try
            {
                RunOnce(options, password);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Run failed at " + DateTime.Now + ".");
                Console.Error.WriteLine(ex.GetType().FullName + ": " + ex.Message);

                if (!options.Forever)
                {
                    throw;
                }
            }

            if (!options.Forever)
            {
                return;
            }

            Console.WriteLine("Sleeping for " + options.Delay.TotalMinutes + " minute(s)...");
            System.Threading.Thread.Sleep(options.Delay);
        }
        while (true);
    }

    internal static void RunOnce(Options options, string password)
    {
        Console.WriteLine("Run started: " + DateTime.Now);

        // One run is intentionally linear: query SolarWinds, derive expected PI
        // points, reconcile missing points, then write snapshots for all points.
        IReadOnlyList<SolarWindsNode> nodes = QuerySolarWindsAsync(options, password).GetAwaiter().GetResult();

        Console.WriteLine("SolarWinds rows: " + nodes.Count);
        if (nodes.Count == 0)
        {
            throw new InvalidOperationException("SolarWinds query returned no rows.");
        }

        IReadOnlyList<ExpectedTag> expectedTags = nodes.SelectMany(CreateExpectedTags).ToList();
        Console.WriteLine("Expected PI tags: " + expectedTags.Count);

        IReadOnlyList<ExpectedTag> missingTags = FindMissingPiTags(options.PiServer, expectedTags);
        if (options.DryRunCreate)
        {
            PrintDryRunCreate(missingTags);
            PrintDryRunWrites(expectedTags);
            return;
        }

        if (missingTags.Count > 0)
        {
            CreateMissingPiTags(options.PiServer, missingTags);
        }

        WriteSnapshots(options.PiServer, expectedTags);
        Console.WriteLine("All expected PI tags exist.");
        Console.WriteLine("Snapshot writes completed.");
        Console.WriteLine("Run completed: " + DateTime.Now);
    }

    private static async Task<IReadOnlyList<SolarWindsNode>> QuerySolarWindsAsync(Options options, string password)
    {
        // SWIS REST avoids any dependency on older SolarWinds client assemblies.
        using (HttpClient client = CreateClient(options.OrionUsername, password, options.OrionSkipCertValidation, options.OrionNoProxy))
        {
            var serializer = new JavaScriptSerializer();
            string requestJson = serializer.Serialize(new Dictionary<string, string> { { "query", Swql } });
            var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
            var queryUri = new Uri(string.Format("https://{0}:{1}/SolarWinds/InformationService/v3/Json/Query", options.OrionServer, options.OrionPort));

            using (HttpResponseMessage response = await client.PostAsync(queryUri, content).ConfigureAwait(false))
            {
                string responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException(
                        string.Format("SWIS query failed: {0} {1}{2}{3}", (int)response.StatusCode, response.ReasonPhrase, Environment.NewLine, responseBody));
                }

                return ParseSolarWindsRows(responseBody);
            }
        }
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

        string basicAuth = Convert.ToBase64String(Encoding.ASCII.GetBytes(username + ":" + password));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basicAuth);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        return client;
    }

    private static IReadOnlyList<SolarWindsNode> ParseSolarWindsRows(string responseBody)
    {
        var serializer = new JavaScriptSerializer();
        var root = serializer.DeserializeObject(responseBody) as Dictionary<string, object>;
        if (root == null || !root.ContainsKey("results"))
        {
            throw new InvalidOperationException("SWIS returned an unexpected response: " + responseBody);
        }

        var results = root["results"] as IEnumerable;
        if (results == null)
        {
            throw new InvalidOperationException("SWIS response did not contain a results array.");
        }

        var nodes = new List<SolarWindsNode>();
        foreach (object item in results)
        {
            var row = item as Dictionary<string, object>;
            if (row == null)
            {
                continue;
            }

            nodes.Add(new SolarWindsNode(
                ToText(row, "NodeID"),
                ToText(row, "Site"),
                ToText(row, "Device_Type"),
                ToText(row, "Caption"),
                ToNullableDouble(row, "ResponseTime"),
                ToNullableDouble(row, "PercentLoss")));
        }

        return nodes;
    }

    private static string ToText(IDictionary<string, object> row, string column)
    {
        object value;
        if (!row.TryGetValue(column, out value) || value == null)
        {
            return string.Empty;
        }

        return Convert.ToString(value).Trim();
    }

    private static double? ToNullableDouble(IDictionary<string, object> row, string column)
    {
        object value;
        if (!row.TryGetValue(column, out value) || value == null)
        {
            return null;
        }

        double result;
        if (double.TryParse(Convert.ToString(value), out result))
        {
            return result;
        }

        return null;
    }

    private static IReadOnlyList<ExpectedTag> CreateExpectedTags(SolarWindsNode node)
    {
        if (string.IsNullOrWhiteSpace(node.NodeId))
        {
            throw new InvalidOperationException("SolarWinds row is missing NodeID for caption '" + node.Caption + "'.");
        }

        if (string.IsNullOrWhiteSpace(node.Site))
        {
            throw new InvalidOperationException("SolarWinds node " + node.NodeId + " is missing CustomProperties.Site.");
        }

        if (string.IsNullOrWhiteSpace(node.DeviceType))
        {
            throw new InvalidOperationException("SolarWinds node " + node.NodeId + " is missing CustomProperties.Device_Type.");
        }

        // Tag naming is the integration contract with PI. Changing this format
        // creates a new set of expected PI points.
        string tagPrefix = "SW " + node.Site + " " + node.DeviceType + " " + node.NodeId;
        return new[]
        {
            new ExpectedTag(tagPrefix + " ResponseTime", node, "ResponseTime", "ms", node.ResponseTime),
            new ExpectedTag(tagPrefix + " PercentLoss", node, "PercentLoss", "%", node.PercentLoss)
        };
    }

    private static IReadOnlyList<ExpectedTag> FindMissingPiTags(string piServerName, IReadOnlyList<ExpectedTag> expectedTags)
    {
        PIServer piServer = ResolvePIServer(piServerName);

        Console.WriteLine("PI Data Archive: " + piServer.Name);
        Console.WriteLine("Connecting to PI...");
        piServer.Connect();
        Console.WriteLine("Connected as: " + piServer.CurrentUserName);

        var missingTags = new List<ExpectedTag>();

        foreach (ExpectedTag expectedTag in expectedTags)
        {
            if (!PiTagExists(piServer, expectedTag.TagName))
            {
                missingTags.Add(expectedTag);
            }
        }

        piServer.Disconnect();

        return missingTags;
    }

    private static void PrintDryRunCreate(IReadOnlyList<ExpectedTag> missingTags)
    {
        Console.WriteLine("Dry run create mode: no PI tags were created.");

        if (missingTags.Count == 0)
        {
            Console.WriteLine("No missing PI tags found.");
            return;
        }

        Console.WriteLine("Would create " + missingTags.Count + " PI tag(s):");
        foreach (ExpectedTag missingTag in missingTags)
        {
            Console.WriteLine(missingTag.TagName + " [" + missingTag.PointSource + ", " + missingTag.EngineeringUnits + "]");
        }
    }

    private static void PrintDryRunWrites(IReadOnlyList<ExpectedTag> expectedTags)
    {
        Console.WriteLine("Dry run snapshot write mode: no PI snapshot values were written.");

        foreach (ExpectedTag expectedTag in expectedTags)
        {
            if (!expectedTag.Value.HasValue)
            {
                Console.WriteLine("Would skip snapshot write, missing SolarWinds value: " + expectedTag.TagName);
                continue;
            }

            Console.WriteLine("Would write snapshot: " + expectedTag.TagName + " = " + expectedTag.Value.Value);
        }
    }

    private static void CreateMissingPiTags(string piServerName, IReadOnlyList<ExpectedTag> missingTags)
    {
        if (missingTags.Count == 0)
        {
            return;
        }

        PIServer piServer = ResolvePIServer(piServerName);

        Console.WriteLine("Creating " + missingTags.Count + " missing PI tag(s) on " + piServer.Name + "...");
        piServer.Connect();

        try
        {
            foreach (ExpectedTag missingTag in missingTags)
            {
                CreatePiTag(piServer, missingTag);
                Console.WriteLine("Created: " + missingTag.TagName);
            }
        }
        finally
        {
            piServer.Disconnect();
        }
    }

    private static void CreatePiTag(PIServer piServer, ExpectedTag expectedTag)
    {
        // The point attributes are intentionally minimal: enough to make the
        // points numeric, identifiable as SolarWinds sourced, and readable in PI tools.
        var attributes = new Dictionary<string, object>
        {
            { PICommonPointAttributes.Descriptor, "SolarWinds " + expectedTag.MetricName + " for " + expectedTag.Node.Caption },
            { PICommonPointAttributes.PointSource, expectedTag.PointSource },
            { PICommonPointAttributes.PointType, PIPointType.Float32 },
            { PICommonPointAttributes.EngineeringUnits, expectedTag.EngineeringUnits }
        };

        piServer.CreatePIPoint(expectedTag.TagName, attributes);
    }

    private static void WriteSnapshots(string piServerName, IReadOnlyList<ExpectedTag> expectedTags)
    {
        PIServer piServer = ResolvePIServer(piServerName);

        Console.WriteLine("Writing snapshot values to " + piServer.Name + "...");
        piServer.Connect();

        try
        {
            foreach (ExpectedTag expectedTag in expectedTags)
            {
                WriteSnapshot(piServer, expectedTag);
            }
        }
        finally
        {
            piServer.Disconnect();
        }
    }

    private static void WriteSnapshot(PIServer piServer, ExpectedTag expectedTag)
    {
        if (!expectedTag.Value.HasValue)
        {
            Console.WriteLine("Skipped snapshot write, missing SolarWinds value: " + expectedTag.TagName);
            return;
        }

        PIPoint point = PIPoint.FindPIPoint(piServer, expectedTag.TagName);
        var value = new AFValue(expectedTag.Value.Value, AFTime.Now);
        // Replace is appropriate for current-value polling: reruns at the same
        // timestamp should leave one authoritative snapshot event.
        point.UpdateValue(value, OSIsoft.AF.Data.AFUpdateOption.Replace);
        Console.WriteLine("Wrote snapshot: " + expectedTag.TagName + " = " + expectedTag.Value.Value);
    }

    private static PIServer ResolvePIServer(string serverName)
    {
        var piServers = new PIServers();

        if (!string.IsNullOrWhiteSpace(serverName))
        {
            return piServers[serverName];
        }

        if (piServers.DefaultPIServer == null)
        {
            throw new InvalidOperationException("No default PI Data Archive is configured. Pass --pi-server <name>.");
        }

        return piServers.DefaultPIServer;
    }

    private static bool PiTagExists(PIServer piServer, string tagName)
    {
        try
        {
            PIPoint.FindPIPoint(piServer, tagName);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ReadPassword(string prompt)
    {
        Console.Write(prompt);
        var password = new StringBuilder();

        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(true);

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

    private sealed class SolarWindsNode
    {
        public SolarWindsNode(string nodeId, string site, string deviceType, string caption, double? responseTime, double? percentLoss)
        {
            NodeId = nodeId;
            Site = site;
            DeviceType = deviceType;
            Caption = caption;
            ResponseTime = responseTime;
            PercentLoss = percentLoss;
        }

        public string NodeId { get; private set; }
        public string Site { get; private set; }
        public string DeviceType { get; private set; }
        public string Caption { get; private set; }
        public double? ResponseTime { get; private set; }
        public double? PercentLoss { get; private set; }
    }

    private sealed class ExpectedTag
    {
        public ExpectedTag(string tagName, SolarWindsNode node, string metricName, string engineeringUnits, double? value)
        {
            TagName = tagName;
            Node = node;
            MetricName = metricName;
            EngineeringUnits = engineeringUnits;
            Value = value;
        }

        public string TagName { get; private set; }
        public SolarWindsNode Node { get; private set; }
        public string MetricName { get; private set; }
        public string EngineeringUnits { get; private set; }
        public double? Value { get; private set; }
        public string PointSource { get { return "SW"; } }
    }

    private sealed class MissingPiTagsException : Exception
    {
        public MissingPiTagsException(IReadOnlyList<ExpectedTag> missingTags)
            : base(BuildMessage(missingTags))
        {
        }

        private static string BuildMessage(IReadOnlyList<ExpectedTag> missingTags)
        {
            var builder = new StringBuilder();
            builder.AppendLine("Missing " + missingTags.Count + " expected PI tag(s):");

            foreach (ExpectedTag missingTag in missingTags)
            {
                builder.AppendLine("  " + missingTag.TagName + " (SolarWinds NodeID " + missingTag.Node.NodeId + ", Caption '" + missingTag.Node.Caption + "')");
            }

            return builder.ToString();
        }
    }

    internal sealed class Options
    {
        // Options stays dependency-free so the command-line contract is visible
        // in this file and easy to run from PowerShell, VS Code, or Task Scheduler.
        public string OrionServer { get; private set; }
        public string OrionUsername { get; private set; }
        public string OrionPassword { get; private set; }
        public int OrionPort { get; private set; }
        public bool OrionSkipCertValidation { get; private set; }
        public bool OrionNoProxy { get; private set; }
        public string PiServer { get; private set; }
        public bool DryRunCreate { get; private set; }
        public bool Forever { get; private set; }
        public TimeSpan Delay { get; private set; }
        public bool ShowHelp { get; private set; }

        private Options()
        {
            OrionPort = 17774;
            Delay = TimeSpan.FromMinutes(5);
        }

        public static Options Parse(string[] args)
        {
            var options = new Options();

            for (int index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--orion-server":
                        options.OrionServer = RequiredValue(args, ref index, "--orion-server");
                        break;
                    case "--orion-username":
                        options.OrionUsername = RequiredValue(args, ref index, "--orion-username");
                        break;
                    case "--orion-password":
                        options.OrionPassword = RequiredValue(args, ref index, "--orion-password");
                        break;
                    case "--orion-port":
                        int port;
                        if (!int.TryParse(RequiredValue(args, ref index, "--orion-port"), out port))
                        {
                            throw new ArgumentException("--orion-port requires a numeric value.");
                        }

                        options.OrionPort = port;
                        break;
                    case "--orion-skip-cert-validation":
                        options.OrionSkipCertValidation = true;
                        break;
                    case "--orion-no-proxy":
                        options.OrionNoProxy = true;
                        break;
                    case "--pi-server":
                        options.PiServer = RequiredValue(args, ref index, "--pi-server");
                        break;
                    case "--dry-run-create":
                        options.DryRunCreate = true;
                        break;
                    case "--forever":
                        options.Forever = true;
                        break;
                    case "--delay-minutes":
                        double delayMinutes;
                        if (!double.TryParse(RequiredValue(args, ref index, "--delay-minutes"), out delayMinutes) || delayMinutes <= 0)
                        {
                            throw new ArgumentException("--delay-minutes requires a positive numeric value.");
                        }

                        options.Delay = TimeSpan.FromMinutes(delayMinutes);
                        break;
                    case "--help":
                    case "-h":
                    case "/?":
                        options.ShowHelp = true;
                        break;
                    default:
                        throw new ArgumentException("Unexpected argument: " + args[index]);
                }
            }

            return options;
        }

        public bool IsValid()
        {
            return ShowHelp || (!string.IsNullOrWhiteSpace(OrionServer) && !string.IsNullOrWhiteSpace(OrionUsername));
        }

        public static void PrintUsage()
        {
            Console.Error.WriteLine("Usage:");
            Console.Error.WriteLine("  dotnet run --project .\\SolarWindsPiTagAudit.csproj -- --orion-server <server> --orion-username <user> [--orion-password <password>] [--pi-server <pi-server>] [--orion-no-proxy] [--orion-skip-cert-validation] [--dry-run-create] [--forever] [--delay-minutes 5]");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Tag format:");
            Console.Error.WriteLine("  SW <Site> <Device_Type> <NodeID> ResponseTime");
            Console.Error.WriteLine("  SW <Site> <Device_Type> <NodeID> PercentLoss");
        }

        private static string RequiredValue(string[] args, ref int index, string optionName)
        {
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException(optionName + " requires a value.");
            }

            index++;
            return args[index];
        }
    }
}
