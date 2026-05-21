using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

// SolarWindsPiBridgeService hosts the existing bridge runner as a Windows Service.
// The service reads one JSON config file so credentials and operational settings
// are not embedded directly in the Windows service binPath.
internal sealed class SolarWindsPiBridgeService : ServiceBase
{
    private const string DefaultServiceName = "SolarWindsPiBridge";

    private readonly string[] args;
    private CancellationTokenSource cancellation;
    private Task worker;
    private BridgeServiceConfig config;

    private SolarWindsPiBridgeService(string[] args)
    {
        this.args = args;
        ServiceName = DefaultServiceName;
        CanStop = true;
        CanShutdown = true;
    }

    private static int Main(string[] args)
    {
        if (HasArg(args, "--help") || HasArg(args, "-h") || HasArg(args, "/?"))
        {
            PrintUsage();
            return 0;
        }

        var service = new SolarWindsPiBridgeService(args);

        if (Environment.UserInteractive || HasArg(args, "--console"))
        {
            return service.RunConsole();
        }

        Run(service);
        return 0;
    }

    protected override void OnStart(string[] serviceArgs)
    {
        try
        {
            string[] effectiveArgs = serviceArgs != null && serviceArgs.Length > 0 ? serviceArgs : args;
            StartWorker(effectiveArgs);
        }
        catch (Exception ex)
        {
            WriteStartupFailure(ex);
            throw;
        }
    }

    protected override void OnStop()
    {
        StopWorker();
    }

    protected override void OnShutdown()
    {
        StopWorker();
    }

    private int RunConsole()
    {
        try
        {
            StartWorker(args);
            Console.WriteLine("Service running in console mode. Press Ctrl+C to stop.");

            using (var stopSignal = new ManualResetEvent(false))
            {
                Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs eventArgs)
                {
                    eventArgs.Cancel = true;
                    stopSignal.Set();
                };

                stopSignal.WaitOne();
            }

            StopWorker();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.GetType().FullName + ": " + ex.Message);
            return 1;
        }
    }

    private void StartWorker(string[] effectiveArgs)
    {
        config = BridgeServiceConfig.Load(GetConfigPath(effectiveArgs));
        ConfigureFileLogging(config.LogPath);

        SolarWindsPiTagAudit.Options options = config.ToBridgeOptions();
        if (string.IsNullOrWhiteSpace(options.OrionPassword))
        {
            throw new InvalidOperationException("Service config must provide orionPassword. Services cannot prompt for credentials.");
        }

        cancellation = new CancellationTokenSource();
        worker = Task.Run(delegate { WorkerLoop(options, options.OrionPassword, config.RunForever, config.Interval, cancellation.Token); });
    }

    private void StopWorker()
    {
        if (cancellation == null)
        {
            return;
        }

        cancellation.Cancel();

        try
        {
            if (worker != null)
            {
                worker.Wait(TimeSpan.FromSeconds(30));
            }
        }
        catch (AggregateException)
        {
        }
        finally
        {
            cancellation.Dispose();
            cancellation = null;
            worker = null;
        }
    }

    private static void WorkerLoop(SolarWindsPiTagAudit.Options options, string password, bool runForever, TimeSpan interval, CancellationToken cancellationToken)
    {
        Console.WriteLine("SolarWinds PI Bridge service started at " + DateTime.Now + ".");
        Console.WriteLine("Forever mode: " + runForever + ".");
        Console.WriteLine("Interval minutes: " + interval.TotalMinutes + ".");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                SolarWindsPiTagAudit.RunOnce(options, password);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Bridge service run failed at " + DateTime.Now + ".");
                Console.Error.WriteLine(ex.GetType().FullName + ": " + ex.Message);
            }

            if (!runForever)
            {
                Console.WriteLine("Forever mode is false; service worker completed one run.");
                break;
            }

            Console.WriteLine("Next run after " + interval.TotalMinutes + " minute(s).");
            if (cancellationToken.WaitHandle.WaitOne(interval))
            {
                break;
            }
        }

        Console.WriteLine("SolarWinds PI Bridge service stopped at " + DateTime.Now + ".");
    }

    private static string GetConfigPath(string[] args)
    {
        for (int index = 0; index < args.Length; index++)
        {
            if (string.Equals(args[index], "--config", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Length)
                {
                    throw new ArgumentException("--config requires a JSON config path.");
                }

                return args[index + 1];
            }
        }

        throw new ArgumentException("Missing required --config <path> argument.");
    }

    private static bool HasArg(string[] args, string value)
    {
        foreach (string arg in args)
        {
            if (string.Equals(arg, value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void ConfigureFileLogging(string logPath)
    {
        if (string.IsNullOrWhiteSpace(logPath))
        {
            return;
        }

        string directory = Path.GetDirectoryName(logPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var writer = new FileAppendTextWriter(logPath);
        Console.SetOut(writer);
        Console.SetError(writer);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  SolarWindsPiBridgeService.exe --console --config C:\\apps\\sw_to_pi\\SolarWindsPiBridgeService.json");
        Console.WriteLine();
        Console.WriteLine("Windows service binPath example:");
        Console.WriteLine("  C:\\apps\\sw_to_pi\\SolarWindsPiBridgeService.exe --config C:\\apps\\sw_to_pi\\SolarWindsPiBridgeService.json");
    }

    private static void WriteStartupFailure(Exception ex)
    {
        string message = "SolarWinds PI Bridge service startup failed." + Environment.NewLine
            + ex.GetType().FullName + ": " + ex.Message;

        try
        {
            EventLog.WriteEntry(DefaultServiceName, message, EventLogEntryType.Error);
        }
        catch
        {
            try
            {
                string fallbackPath = Path.Combine(Path.GetTempPath(), DefaultServiceName + "-startup-error.log");
                File.AppendAllText(fallbackPath, DateTime.Now.ToString("s") + " " + message + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
            }
        }
    }

    private sealed class BridgeServiceConfig
    {
        public string OrionServer { get; set; }
        public string OrionUsername { get; set; }
        public string OrionPassword { get; set; }
        public int? OrionPort { get; set; }
        public bool OrionNoProxy { get; set; }
        public bool OrionSkipCertValidation { get; set; }
        public string PiServer { get; set; }
        public bool? Forever { get; set; }
        public double? IntervalMinutes { get; set; }
        public double? DelayMinutes { get; set; }
        public bool DryRunCreate { get; set; }
        public string LogPath { get; set; }

        public bool RunForever
        {
            get { return !Forever.HasValue || Forever.Value; }
        }

        public TimeSpan Interval
        {
            get { return TimeSpan.FromMinutes(EffectiveIntervalMinutes); }
        }

        private double EffectiveIntervalMinutes
        {
            get { return IntervalMinutes ?? DelayMinutes ?? 5; }
        }

        public static BridgeServiceConfig Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Config path is required.");
            }

            if (!File.Exists(path))
            {
                throw new FileNotFoundException("Service config file was not found.", path);
            }

            var serializer = new JavaScriptSerializer();
            var config = serializer.Deserialize<BridgeServiceConfig>(File.ReadAllText(path));
            if (config == null)
            {
                throw new InvalidOperationException("Service config file is empty or invalid JSON.");
            }

            config.Validate();
            return config;
        }

        public SolarWindsPiTagAudit.Options ToBridgeOptions()
        {
            var args = new List<string>
            {
                "--orion-server",
                OrionServer,
                "--orion-username",
                OrionUsername,
                "--orion-password",
                OrionPassword
            };

            if (OrionPort.HasValue)
            {
                args.Add("--orion-port");
                args.Add(OrionPort.Value.ToString());
            }

            if (OrionNoProxy)
            {
                args.Add("--orion-no-proxy");
            }

            if (OrionSkipCertValidation)
            {
                args.Add("--orion-skip-cert-validation");
            }

            if (!string.IsNullOrWhiteSpace(PiServer))
            {
                args.Add("--pi-server");
                args.Add(PiServer);
            }

            if (DryRunCreate)
            {
                args.Add("--dry-run-create");
            }

            if (IntervalMinutes.HasValue || DelayMinutes.HasValue)
            {
                args.Add("--delay-minutes");
                args.Add(EffectiveIntervalMinutes.ToString());
            }

            return SolarWindsPiTagAudit.Options.Parse(args.ToArray());
        }

        private void Validate()
        {
            if (string.IsNullOrWhiteSpace(OrionServer))
            {
                throw new InvalidOperationException("Config field orionServer is required.");
            }

            if (string.IsNullOrWhiteSpace(OrionUsername))
            {
                throw new InvalidOperationException("Config field orionUsername is required.");
            }

            if (string.IsNullOrWhiteSpace(OrionPassword))
            {
                throw new InvalidOperationException("Config field orionPassword is required.");
            }

            if (IntervalMinutes.HasValue && IntervalMinutes.Value <= 0)
            {
                throw new InvalidOperationException("Config field intervalMinutes must be greater than zero.");
            }

            if (DelayMinutes.HasValue && DelayMinutes.Value <= 0)
            {
                throw new InvalidOperationException("Config field delayMinutes must be greater than zero.");
            }
        }
    }

    private sealed class FileAppendTextWriter : TextWriter
    {
        private readonly string path;
        private readonly object gate = new object();

        public FileAppendTextWriter(string path)
        {
            this.path = path;
        }

        public override Encoding Encoding
        {
            get { return Encoding.UTF8; }
        }

        public override void WriteLine(string value)
        {
            lock (gate)
            {
                File.AppendAllText(path, DateTime.Now.ToString("s") + " " + value + Environment.NewLine, Encoding.UTF8);
            }
        }

        public override void Write(char value)
        {
            lock (gate)
            {
                File.AppendAllText(path, value.ToString(), Encoding.UTF8);
            }
        }
    }
}
