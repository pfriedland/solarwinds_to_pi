using System;
using System.Collections.Generic;
using OSIsoft.AF.Asset;
using OSIsoft.AF.PI;

// PIArchiveConnectivity is a focused AF SDK smoke test. It confirms the local
// AF SDK install, PI Data Archive authentication, and optional snapshot reads.
internal static class PIArchiveConnectivity
{
    private static int Main(string[] args)
    {
        string serverName = null;
        string pointName = null;

        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--server":
                    if (index + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("--server requires a PI Data Archive server name.");
                        return 2;
                    }

                    serverName = args[++index];
                    break;
                case "--point":
                    if (index + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("--point requires a PI point name.");
                        return 2;
                    }

                    pointName = args[++index];
                    break;
                case "--help":
                case "-h":
                case "/?":
                    PrintUsage();
                    return 0;
                default:
                    Console.Error.WriteLine("Unexpected argument: " + args[index]);
                    PrintUsage();
                    return 2;
            }
        }

        try
        {
            PIServer piServer = ResolvePIServer(serverName);

            Console.WriteLine("PI Data Archive: " + piServer.Name);
            Console.WriteLine("Connecting...");
            piServer.Connect();

            Console.WriteLine("Connected: " + piServer.ConnectionInfo.IsConnected);
            Console.WriteLine("Server version: " + piServer.ServerVersion);
            Console.WriteLine("Current user: " + piServer.CurrentUserName);

            if (!string.IsNullOrWhiteSpace(pointName))
            {
                ReadSnapshot(piServer, pointName);
            }

            piServer.Disconnect();
            Console.WriteLine("Disconnected.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("PI connectivity test failed.");
            Console.Error.WriteLine(ex.GetType().FullName + ": " + ex.Message);

            if (ex.InnerException != null)
            {
                Console.Error.WriteLine("Inner exception: " + ex.InnerException.Message);
            }

            return 1;
        }
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
            throw new InvalidOperationException("No default PI Data Archive is configured. Pass --server <name>.");
        }

        return piServers.DefaultPIServer;
    }

    private static void ReadSnapshot(PIServer piServer, string pointName)
    {
        Console.WriteLine("Reading snapshot for PI point: " + pointName);

        PIPoint point = PIPoint.FindPIPoint(piServer, pointName);
        AFValue snapshot = point.CurrentValue();

        Console.WriteLine("Point: " + point.Name);
        Console.WriteLine("Timestamp: " + snapshot.Timestamp);
        Console.WriteLine("Value: " + snapshot.Value);
        Console.WriteLine("Units: " + GetEngineeringUnits(point));
    }

    private static string GetEngineeringUnits(PIPoint point)
    {
        try
        {
            // AF SDK point attributes must be loaded before GetAttribute can read them.
            point.LoadAttributes(new[] { PICommonPointAttributes.EngineeringUnits });
            object units = point.GetAttribute(PICommonPointAttributes.EngineeringUnits);
            return units == null ? string.Empty : units.ToString();
        }
        catch (KeyNotFoundException)
        {
            return string.Empty;
        }
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  dotnet run --project .\\PIArchiveConnectivity.csproj -- [--server <pi-server>] [--point <pi-point>]");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Examples:");
        Console.Error.WriteLine("  dotnet run --project .\\PIArchiveConnectivity.csproj -- --server PISRV01");
        Console.Error.WriteLine("  dotnet run --project .\\PIArchiveConnectivity.csproj -- --server PISRV01 --point sinusoid");
    }
}
