﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Crank.Models;
using Microsoft.Crank.Controller.Serializers;
using Fluid;
using Fluid.Values;
using McMaster.Extensions.CommandLineUtils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using NuGet.Versioning;
using YamlDotNet.Serialization;
using System.Reflection;
using System.Text;
using Manatee.Json.Schema;
using Manatee.Json;

namespace Microsoft.Crank.Controller
{
    public class Program
    {
        private static readonly HttpClient _httpClient;
        private static readonly HttpClientHandler _httpClientHandler;

        private static string _tableName = "Benchmarks";
        private static string _sqlConnectionString = "";

        private const string EventPipeOutputFile = "eventpipe.netperf";

        // Default to arguments which should be sufficient for collecting trace of default Plaintext run
        private const string _defaultTraceArguments = "BufferSizeMB=1024;CircularMB=1024;clrEvents=JITSymbols;kernelEvents=process+thread+ImageLoad+Profile";

        private static CommandOption
            _configOption,
            _scenarioOption,
            _jobOption,
            _profileOption,
            _outputOption,
            _compareOption,
            _variableOption,
            _sqlConnectionStringOption,
            _sqlTableOption,
            _sessionOption,
            _descriptionOption,
            _propertyOption,
            _excludeMetadataOption,
            _excludeMeasurementsOption,
            _autoflushOption,
            _repeatOption,
            _spanOption
            ;

        // The dynamic arguments that will alter the configurations
        private static List<KeyValuePair<string, string>> Arguments = new List<KeyValuePair<string, string>>();

        private static Dictionary<string, string> _deprecatedArguments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "--projectfile", "--project-file" },
            { "--outputfile", "--output-file" },
            { "--clientName", "--client-name" }
        };

        private static Dictionary<string, string> _synonymArguments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "--aspnet", "--aspnetcoreversion" },
            { "--runtime", "--runtimeversion" },
            { "--clientThreads", "--client-threads" },
        };

        static Program()
        {
            // Configuring the http client to trust the self-signed certificate
            _httpClientHandler = new HttpClientHandler();
            _httpClientHandler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
            _httpClientHandler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

            _httpClient = new HttpClient(_httpClientHandler);

            TemplateContext.GlobalMemberAccessStrategy.Register<JObject, object>((obj, name) => obj[name]);
            FluidValue.SetTypeMapping<JObject>(o => new ObjectValue(o));
            FluidValue.SetTypeMapping<JValue>(o => FluidValue.Create(((JValue)o).Value));
            FluidValue.SetTypeMapping<DateTime>(o => new ObjectValue(o));
        }

        public static int Main(string[] args)
        {
            // Replace deprecated arguments with new ones
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];

                if (_deprecatedArguments.TryGetValue(arg, out var mappedArg))
                {
                    Log.Write($"WARNING: '{arg}' has been deprecated, in the future please use '{mappedArg}'.");
                    args[i] = mappedArg;
                }
                else if (_synonymArguments.TryGetValue(arg, out var synonymArg))
                {
                    // We don't need to display a warning
                    args[i] = synonymArg;
                }
            }

            var app = new CommandLineApplication()
            {
                Name = "Crank",
                FullName = "ASP.NET Benchmarks Controller",
                Description = "Crank orchestrates benchmark jobs on Crank agents.",
                ResponseFileHandling = ResponseFileHandling.ParseArgsAsSpaceSeparated,
                OptionsComparison = StringComparison.OrdinalIgnoreCase,
            };

            app.HelpOption("-?|-h|--help");

            _configOption = app.Option("-c|--config", "Configuration file or url", CommandOptionType.MultipleValue);
            _scenarioOption = app.Option("-s|--scenario", "Scenario to execute", CommandOptionType.SingleValue);
            _jobOption = app.Option("-j|--job", "Name of job to define", CommandOptionType.MultipleValue);
            _profileOption = app.Option("--profile", "Profile name", CommandOptionType.MultipleValue);
            _outputOption = app.Option("-o|--output", "Output filename", CommandOptionType.SingleValue);
            _compareOption = app.Option("--compare", "An optional filename to compare the results to. Can be used multiple times.", CommandOptionType.MultipleValue);
            _variableOption = app.Option("--variable", "Variable", CommandOptionType.MultipleValue);
            _sqlConnectionStringOption = app.Option("--sql",
                "Connection string of the SQL Server Database to store results in", CommandOptionType.SingleValue);
            _sqlTableOption = app.Option("--table",
                "Table name of the SQL Database to store results in", CommandOptionType.SingleValue);
            _sessionOption = app.Option("--session", "A logical identifier to group related jobs.", CommandOptionType.SingleValue);
            _descriptionOption = app.Option("--description", "A string describing the job.", CommandOptionType.SingleValue);
            _propertyOption = app.Option("-p|--property", "Some custom key/value that will be added to the results, .e.g. --property arch=arm --property os=linux", CommandOptionType.MultipleValue);
            _excludeMeasurementsOption = app.Option("--no-measurements", "Remove all measurements from the stored results. For instance, all samples of a measure won't be stored, only the final value.", CommandOptionType.SingleOrNoValue);
            _excludeMetadataOption = app.Option("--no-metadata", "Remove all metadata from the stored results. The metadata is only necessary for being to generate friendly outputs.", CommandOptionType.SingleOrNoValue);
            _autoflushOption = app.Option("--auto-flush", "Runs a single long-running job and flushes measurements automatically.", CommandOptionType.NoValue);
            _repeatOption = app.Option("--repeat", "The job to repeat using the '--span' argument.", CommandOptionType.SingleValue);
            _spanOption = app.Option("--span", "The duration while the job is repeated.", CommandOptionType.SingleValue);

            var verboseOption = app.Option("-v|--verbose",
                "Verbose output", CommandOptionType.NoValue);
            var quietOption = app.Option("--quiet",
                "Quiet output, only the results are displayed", CommandOptionType.NoValue);
            var iterationsOption = app.Option("-i|--iterations",
                "The number of iterations.", CommandOptionType.SingleValue);

            app.Command("compare", compareCmd =>
            {
                compareCmd.Description = "Compares result files";
                var files = compareCmd.Argument("Files", "Files to compare", multipleValues: true).IsRequired();

                compareCmd.OnExecute(() =>
                {
                    return ResultComparer.Compare(files.Values);
                });
            });

            // Extract dynamic arguments
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];

                if (arg.StartsWith("--") && !app.Options.Any(option => arg.StartsWith("--" + option.LongName)))
                {
                    // Remove this argument from the command line
                    args[i] = "";

                    // Dynamic arguments always come in pairs 
                    if (i + 1 < args.Length)
                    {
                        Arguments.Add(KeyValuePair.Create(arg.Substring(2), args[i + 1]));
                        args[i + 1] = "";

                        i++;
                    }
                }
            }

            app.OnExecuteAsync(async (t) =>
            {
                Log.IsQuiet = quietOption.HasValue();
                Log.IsVerbose = verboseOption.HasValue();

                var session = _sessionOption.Value();
                var iterations = 1;
                var exclude = 0;
                var span = TimeSpan.Zero;

                if (string.IsNullOrEmpty(session))
                {
                    session = Guid.NewGuid().ToString("n");
                }

                var description = _descriptionOption.Value() ?? "";

                if (iterationsOption.HasValue() && _spanOption.HasValue())
                {
                    Console.WriteLine($"The options --iterations and --span can't be used together.");
                    return -1;
                }

                if (_spanOption.HasValue() && !TimeSpan.TryParse(_spanOption.Value(), out span))
                {
                    Console.WriteLine($"Invalid value for --span. Format is 'HH:mm:ss'");
                    return -1;
                }

                if (_sqlTableOption.HasValue())
                {
                    _tableName = _sqlTableOption.Value();

                    if (!String.IsNullOrEmpty(Environment.GetEnvironmentVariable(_tableName)))
                    {
                        _tableName = Environment.GetEnvironmentVariable(_tableName);
                    }
                }

                if (_sqlConnectionStringOption.HasValue())
                {
                    _sqlConnectionString = _sqlConnectionStringOption.Value();

                    if (!String.IsNullOrEmpty(Environment.GetEnvironmentVariable(_sqlConnectionString)))
                    {
                        _sqlConnectionString = Environment.GetEnvironmentVariable(_sqlConnectionString);
                    }
                }

                if (!_configOption.HasValue())
                {
                    if (!_jobOption.HasValue())
                    {
                        app.ShowHelp();
                        return 1;
                    }
                }
                else
                {
                    if (!_scenarioOption.HasValue())
                    {
                        Console.Error.WriteLine("No jobs were found. Are you missing the --scenario argument?");
                        return 1;
                    }
                }

                if (_scenarioOption.HasValue() && _jobOption.HasValue())
                {
                    Console.Error.WriteLine("The arguments --scenario and --job can't be used together. They both define which jobs to run.");
                    return 1;
                }

                var results = new ExecutionResult();

                var scenarioName = _scenarioOption.Value();
                var jobNames = _jobOption.Values;

                var variables = new JObject();

                foreach (var variable in _variableOption.Values)
                {
                    var segments = variable.Split('=', 2);

                    if (segments.Length != 2)
                    {
                        Console.WriteLine($"Invalid variable argument: '{variable}', format is \"[NAME]=[VALUE]\"");

                        app.ShowHelp();
                        return -1;
                    }

                    // Try to parse as integer, or the value would be a string
                    if (long.TryParse(segments[1], out var intVariable))
                    {
                        variables[segments[0]] = intVariable;
                    }
                    else
                    {
                        variables[segments[0]] = segments[1];
                    }
                }

                foreach (var property in _propertyOption.Values)
                {
                    var segments = property.Split('=', 2);

                    if (segments.Length != 2)
                    {
                        Console.WriteLine($"Invalid property argument: '{property}', format is \"[NAME]=[VALUE]\"");

                        app.ShowHelp();
                        return -1;
                    }
                }

                var configuration = await BuildConfigurationAsync(_configOption.Values, scenarioName, _jobOption.Values, Arguments, variables, _profileOption.Values);

                // Storing the list of services to run as part of the selected scenario
                var dependencies = String.IsNullOrEmpty(scenarioName)
                    ? _jobOption.Values.ToArray()
                    : configuration.Scenarios[scenarioName].Select(x => x.Key).ToArray()
                    ;

                var serializer = new Serializer();

                string groupId = Guid.NewGuid().ToString("n");

                // Verifying jobs
                foreach (var jobName in dependencies)
                {
                    var service = configuration.Jobs[jobName];

                    service.RunId = groupId;

                    if (String.IsNullOrEmpty(service.Source.Project) &&
                        String.IsNullOrEmpty(service.Source.DockerFile) &&
                        String.IsNullOrEmpty(service.Source.DockerLoad) &&
                        String.IsNullOrEmpty(service.Executable))
                    {
                        Console.WriteLine($"The service '{jobName}' is missing some properties to start the job.");
                        Console.WriteLine($"Check that any of these properties is set: project, executable, dockerFile, dockerLoad");
                        return -1;
                    }

                    if (!service.Endpoints.Any())
                    {
                        Console.WriteLine($"The service '{jobName}' is missing an endpoint to deploy on.");
                        return -1;
                    }

                    foreach (var endpoint in service.Endpoints)
                    {
                        try
                        {
                            using (var cts = new CancellationTokenSource(10000))
                            {
                                var response = await _httpClient.GetAsync(endpoint, cts.Token);
                                response.EnsureSuccessStatusCode();
                            }
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine($"The specified endpoint url '{endpoint}' for '{jobName}' is invalid or not responsive: \"{e.Message}\"");
                            return -1;
                        }
                    }
                }

                // Initialize database
                if (!String.IsNullOrWhiteSpace(_sqlConnectionString))
                {
                    await JobSerializer.InitializeDatabaseAsync(_sqlConnectionString, _tableName);
                }

                await CheckUpdateAsync();

                Log.Write($"Running session '{session}' with description '{_descriptionOption.Value()}'");

                if (_autoflushOption.HasValue())
                {
                    results = await RunAutoFlush(
                        configuration,
                        dependencies,
                        session,
                        span
                        );
                }
                else
                {
                    results = await Run(
                        configuration,
                        dependencies,
                        session,
                        iterations,
                        exclude,
                        span
                        );
                }


                // Display diff

                if (_compareOption.HasValue())
                {
                    var jobName = "Current";

                    if (_scenarioOption.HasValue())
                    {
                        if (_outputOption.HasValue())
                        {
                            jobName = Path.GetFileNameWithoutExtension(_outputOption.Value());
                        }
                    }

                    ResultComparer.Compare(_compareOption.Values, results.JobResults, jobName);
                }

                return results.ReturnCode;
            });

            try
            {
                return app.Execute(args.Where(x => !String.IsNullOrEmpty(x)).ToArray());
            }
            catch (ControllerException e)
            {
                Console.WriteLine(e.Message);
                return -1;
            }
            catch (CommandParsingException e)
            {
                Console.WriteLine(e.Message);
                return -1;
            }
        }

        private static async Task<ExecutionResult> Run(
            Configuration configuration,
            string[] dependencies,
            string session,
            int iterations,
            int exclude,
            TimeSpan span
            )
        {
            var executionResults = new ExecutionResult();
            var iterationStart = DateTime.UtcNow;
            var jobsByDependency = new Dictionary<string, List<JobConnection>>();

            do
            {

                // Repeat until the span duration is over

                for (var i = 1; i <= iterations; i++)
                {

                    if (iterations > 1)
                    {
                        jobsByDependency.Clear();
                        Log.Write($"Job {i} of {iterations}");
                    }

                    foreach (var jobName in dependencies)
                    {
                        var service = configuration.Jobs[jobName];
                        service.DriverVersion = 2;

                        List<JobConnection> jobs;

                        // Create a new list of JobConnection instances if the service is
                        // not already running from a previous loop

                        if (jobsByDependency.ContainsKey(jobName) && SpanShouldKeepJobRunning(jobName))
                        {
                            jobs = jobsByDependency[jobName];

                            // Clear measurements, only if the service is not a console app as it
                            // would already be stopped

                            if (!service.WaitForExit)
                            {
                                await Task.WhenAll(jobs.Select(job => job.ClearMeasurements()));
                            }
                        }
                        else
                        {
                            jobs = service.Endpoints.Select(endpoint => new JobConnection(service, new Uri(endpoint))).ToList();

                            jobsByDependency[jobName] = jobs;

                            // Check os and architecture requirements
                            if (!await EnsureServerRequirementsAsync(jobs, service))
                            {
                                Log.Write($"Scenario skipped as the agent doesn't match the operating and architecture constraints for '{jobName}' ({String.Join("/", new[] { service.Options.RequiredArchitecture, service.Options.RequiredOperatingSystem })})");
                                return new ExecutionResult();
                            }

                            // Start this service on all configured agent endpoints
                            await Task.WhenAll(
                                jobs.Select(job =>
                                {
                                    // Start job on agent
                                    return job.StartAsync(jobName);
                                })
                            );

                            if (service.WaitForExit)
                            {
                                // Wait for all clients to stop
                                while (true)
                                {
                                    var stop = true;

                                    foreach (var job in jobs)
                                    {
                                        var state = await job.GetStateAsync();

                                        stop = stop && (
                                            state == JobState.Stopped ||
                                            state == JobState.Failed ||
                                            state == JobState.Deleted
                                            );
                                    }

                                    if (stop)
                                    {
                                        break;
                                    }

                                    await Task.Delay(1000);
                                }

                                // Stop a blocking job
                                await Task.WhenAll(jobs.Select(job => job.StopAsync()));

                                await Task.WhenAll(jobs.Select(job => job.TryUpdateJobAsync()));

                                await Task.WhenAll(jobs.Select(job => job.DownloadAssetsAsync(jobName)));

                                await Task.WhenAll(jobs.Select(job => job.DeleteAsync()));
                            }
                        }

                        var aJobFailed = false;

                        // Skipped other services if a job has failed
                        foreach (var job in jobs)
                        {
                            var state = await job.GetStateAsync();

                            if (state == JobState.Failed)
                            {
                                aJobFailed = true;
                                break;
                            }
                        }

                        if (aJobFailed)
                        {
                            Log.Write($"Job has failed, interrupting benchmarks ...");
                            break;
                        }
                    }

                    // Download traces, before the jobs are stopped
                    foreach (var jobName in dependencies)
                    {
                        // Unless the jobs can't be stopped
                        if (SpanShouldKeepJobRunning(jobName))
                        {
                            continue;
                        }

                        var service = configuration.Jobs[jobName];

                        // Skip failed jobs
                        if (!jobsByDependency.ContainsKey(jobName))
                        {
                            continue;
                        }

                        var jobConnections = jobsByDependency[jobName];

                        foreach (var jobConnection in jobConnections)
                        {
                            var info = await jobConnection.GetInfoAsync();
                            var os = Enum.Parse<Models.OperatingSystem>(info["os"]?.ToString() ?? "linux", ignoreCase: true);

                            var traceExtension = ".nettrace";

                            // Download trace
                            if (jobConnection.Job.DotNetTrace || jobConnection.Job.Collect)
                            {
                                if (jobConnection.Job.Collect)
                                {
                                    traceExtension = os == Models.OperatingSystem.Windows
                                        ? ".etl.zip"
                                        : ".trace.zip"
                                        ;
                                }

                                try
                                {
                                    var traceDestination = jobConnection.Job.Options.TraceOutput;

                                    if (String.IsNullOrWhiteSpace(traceDestination))
                                    {
                                        traceDestination = jobName;
                                    }


                                    if (!traceDestination.EndsWith(traceExtension, StringComparison.OrdinalIgnoreCase))
                                    {
                                        traceDestination = traceDestination + "." + DateTime.Now.ToString("MM-dd-HH-mm-ss") + traceExtension;
                                    }

                                    Log.Write($"Collecting trace file '{traceDestination}' ...");

                                    await jobConnection.DownloadDotnetTrace(traceDestination);
                                }
                                catch (Exception e)
                                {
                                    Log.Write($"Error while fetching trace for '{jobName}'");
                                    Log.Verbose(e.Message);
                                }
                            }
                        }
                    }

                    // Stop all non-blocking jobs in reverse dependency order (clients first)
                    foreach (var jobName in dependencies.Reverse())
                    {
                        var service = configuration.Jobs[jobName];

                        // Skip failed jobs
                        if (!jobsByDependency.ContainsKey(jobName))
                        {
                            continue;
                        }

                        var jobs = jobsByDependency[jobName];

                        if (!service.WaitForExit)
                        {
                            // Unless the jobs can't be stopped
                            if (!SpanShouldKeepJobRunning(jobName))
                            {
                                await Task.WhenAll(jobs.Select(job => job.StopAsync()));
                            }

                            await Task.WhenAll(jobs.Select(job => job.TryUpdateJobAsync()));

                            // Unless the jobs can't be stopped
                            if (!SpanShouldKeepJobRunning(jobName))
                            {
                                await Task.WhenAll(jobs.Select(job => job.DownloadAssetsAsync(jobName)));

                                await Task.WhenAll(jobs.Select(job => job.DeleteAsync()));
                            }
                        }
                    }

                    // Display results
                    foreach (var jobName in dependencies)
                    {
                        var service = configuration.Jobs[jobName];

                        // Skip failed jobs
                        if (!jobsByDependency.ContainsKey(jobName))
                        {
                            continue;
                        }

                        var jobConnections = jobsByDependency[jobName];

                        if (!service.Options.DiscardResults)
                        {
                            Log.Quiet("");
                            Log.Quiet($"{jobName}");
                            Log.Quiet($"-------");
                        }

                        // Convert any json result to an object
                        NormalizeResults(jobConnections);

                        foreach (var jobConnection in jobConnections)
                        {
                            if (!service.Options.DiscardResults)
                            {
                                WriteMeasures(jobConnection);
                            }
                        }
                    }

                    var jobResults = await CreateJobResultsAsync(configuration, dependencies, jobsByDependency);

                    foreach (var property in _propertyOption.Values)
                    {
                        var segments = property.Split('=', 2);

                        jobResults.Properties[segments[0]] = segments[1];
                    }

                    executionResults.JobResults = jobResults;
                }

                // Save results

                if (_outputOption.HasValue())
                {
                    var filename = _outputOption.Value();
                    
                    var directory = Path.GetDirectoryName(filename);
                    if (!String.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }
                    
                    var index = 1;

                    // If running in a span, create a unique filename for each run
                    if (span > TimeSpan.Zero)
                    {
                        do
                        {
                            var filenameWithoutExtension = Path.GetFileNameWithoutExtension(_outputOption.Value());
                            filename = filenameWithoutExtension + "-" + index++ + Path.GetExtension(_outputOption.Value());
                        } while (File.Exists(filename));
                    }

                    await File.WriteAllTextAsync(filename, JsonConvert.SerializeObject(executionResults, Formatting.Indented, new JsonSerializerSettings { ContractResolver = new CamelCasePropertyNamesContractResolver() }));

                    Log.Write("", notime: true);
                    Log.Write($"Results saved in '{new FileInfo(filename).FullName}'", notime: true);
                }

                // Store data

                if (!String.IsNullOrEmpty(_sqlConnectionString))
                {
                    await JobSerializer.WriteJobResultsToSqlAsync(executionResults.JobResults, _sqlConnectionString, _tableName, session, _scenarioOption.Value(), _descriptionOption.Value());
                }

                if (span > TimeSpan.Zero)
                {
                    Console.WriteLine("Remaining job duration: {0}", GetRemainingTime());
                }
            }
            while (!IsSpanOver());


            return executionResults;

            TimeSpan GetRemainingTime()
            {
                return span - GetEllapsedTime();
            }

            TimeSpan GetEllapsedTime()
            {
                return DateTime.UtcNow - iterationStart;
            }

            bool IsSpanOver()
            {
                return span == TimeSpan.Zero || GetEllapsedTime() > span;
            }

            bool SpanShouldKeepJobRunning(string jobName)
            {
                if (IsSpanOver())
                {
                    return false;
                }

                var repeatAfterJob = _repeatOption.Value();
                var jobKeptRunning = dependencies.TakeWhile(x => !String.Equals(repeatAfterJob, x, StringComparison.OrdinalIgnoreCase));

                return jobKeptRunning.Any(x => String.Equals(jobName, x, StringComparison.OrdinalIgnoreCase));
            }
        }

        private static async Task<ExecutionResult> RunAutoFlush(
            Configuration configuration,
            string[] dependencies,
            string session,
            TimeSpan span
            )
        {
            var executionResults = new ExecutionResult();

            if (dependencies.Length != 1)
            {
                Log.Write($"With --auto-flush a single job is required.");
                return executionResults;
            }

            var jobName = dependencies.First();
            var service = configuration.Jobs[jobName];

            if (service.Endpoints.Count() != 1)
            {
                Log.Write($"With --auto-flush a single endpoint is required.");
                return executionResults;
            }

            if (!service.WaitForExit && span == TimeSpan.Zero)
            {
                Log.Write($"With --auto-flush a --span duration or a blocking job is required (missing 'waitForExit' option).");
                return executionResults;
            }

            service.DriverVersion = 2;

            var job = new JobConnection(service, new Uri(service.Endpoints.First()));

            // Check os and architecture requirements
            if (!await EnsureServerRequirementsAsync(new[] { job }, service))
            {
                Log.Write($"Scenario skipped as the agent doesn't match the operating and architecture constraints for '{jobName}' ({String.Join("/", new[] { service.Options.RequiredArchitecture, service.Options.RequiredOperatingSystem })})");
                return new ExecutionResult();
            }

            // Start this service on the configured agent endpoint
            await job.StartAsync(jobName);

            var start = DateTime.UtcNow;

            // Wait for the job to stop
            while (true)
            {
                await Task.Delay(5000);

                await job.TryUpdateJobAsync();

                var stop =
                    job.Job.State == JobState.Stopped ||
                    job.Job.State == JobState.Deleted ||
                    job.Job.State == JobState.Failed
                    ;

                if (start + span > DateTime.UtcNow)
                {
                    stop = true;
                }

                if (job.Job.Measurements.Any(x => x.IsDelimiter))
                {
                    // Remove all values after the delimiter locally
                    Measurement measurement;
                    var measurements = new List<Measurement>();

                    do
                    {
                        job.Job.Measurements.TryDequeue(out measurement);
                        measurements.Add(measurement);
                    } while (!measurement.IsDelimiter);

                    job.Job.Measurements = new ConcurrentQueue<Measurement>(measurements);

                    // Removes all values before the delimiter on the server
                    await job.FlushMeasurements();

                    // Convert any json result to an object
                    NormalizeResults(new[] { job });

                    if (!service.Options.DiscardResults)
                    {
                        WriteMeasures(job);
                    }

                    var jobResults = await CreateJobResultsAsync(configuration, dependencies, new Dictionary<string, List<JobConnection>> { [jobName] = new List<JobConnection> { job } });

                    foreach (var property in _propertyOption.Values)
                    {
                        var segments = property.Split('=', 2);

                        jobResults.Properties[segments[0]] = segments[1];
                    }

                    // Save results

                    if (_outputOption.HasValue())
                    {
                        var filename = _outputOption.Value();
                        var index = 1;

                        do
                        {
                            var filenameWithoutExtension = Path.GetFileNameWithoutExtension(_outputOption.Value());
                            filename = filenameWithoutExtension + "-" + index++ + Path.GetExtension(_outputOption.Value());
                        } while (File.Exists(filename));

                        await File.WriteAllTextAsync(filename, JsonConvert.SerializeObject(jobResults, Formatting.Indented, new JsonSerializerSettings { ContractResolver = new CamelCasePropertyNamesContractResolver() }));

                        Log.Write("", notime: true);
                        Log.Write($"Results saved in '{new FileInfo(filename).FullName}'", notime: true);
                    }

                    // Store data

                    if (!String.IsNullOrEmpty(_sqlConnectionString))
                    {
                        await JobSerializer.WriteJobResultsToSqlAsync(jobResults, _sqlConnectionString, _tableName, session, _scenarioOption.Value(), _descriptionOption.Value());
                    }
                }

                if (stop)
                {
                    break;
                }
            }

            await job.StopAsync();

            await job.TryUpdateJobAsync();

            await job.DownloadAssetsAsync(jobName);

            await job.DeleteAsync();

            return executionResults;
        }

        public static JObject MergeVariables(params object[] variableObjects)
        {
            var mergeOptions = new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace, MergeNullValueHandling = MergeNullValueHandling.Merge };

            var result = new JObject();

            foreach (var variableObject in variableObjects)
            {
                if (variableObject == null)
                {
                    continue;
                }

                result.Merge(JObject.FromObject(variableObject), mergeOptions);
            }

            return result;
        }

        /// <summary>
        /// Applies all command line argument to alter the configuration files and build a final Configuration instance.
        /// 1- Merges the configuration files in the same order as requested
        /// 2- For each scenario's job, clone it in the Configuration's jobs list
        /// 3- Patch the new job with the scenario's properties
        /// 4- Add custom job entries 
        /// </summary>
        public static async Task<Configuration> BuildConfigurationAsync(
            IEnumerable<string> configurationFileOrUrls,
            string scenarioName,
            IEnumerable<string> customJobs,
            IEnumerable<KeyValuePair<string, string>> arguments,
            JObject commandLineVariables,
            IEnumerable<string> profiles
            )
        {
            JObject configuration = null;

            // Merge all configuration sources
            foreach (var configurationFileOrUrl in configurationFileOrUrls)
            {
                var localconfiguration = await LoadConfigurationAsync(configurationFileOrUrl);

                if (configuration != null)
                {
                    var mergeOptions = new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace, MergeNullValueHandling = MergeNullValueHandling.Merge };

                    configuration.Merge(localconfiguration);
                }
                else
                {
                    configuration = localconfiguration;
                }
            }

            // Roundtrip the JObject such that it contains all the extra properties of the Configuration class that are not in the configuration file
            var configurationInstance = configuration.ToObject<Configuration>();

            // After that point we only modify the concrete instance of Configuration
            if (!configurationInstance.Scenarios.ContainsKey(scenarioName))
            {
                var availableScenarios = String.Join("', '", configurationInstance.Scenarios.Keys);
                throw new ControllerException($"The scenario `{scenarioName}` was not found. Possible values: '{availableScenarios}'");
            }

            if (!String.IsNullOrEmpty(scenarioName))
            {
                var scenario = configurationInstance.Scenarios[scenarioName];

                // Clone each service from the selected scenario inside the Jobs property of the Configuration
                foreach (var service in scenario)
                {
                    var jobName = service.Value.Job;
                    var serviceName = service.Key;

                    if (!configurationInstance.Jobs.ContainsKey(jobName))
                    {
                        throw new ControllerException($"The job named `{jobName}` was not found for `{serviceName}`");
                    }

                    var jobObject = JObject.FromObject(configurationInstance.Jobs[jobName]);
                    var dependencyObject = (JObject)configuration["scenarios"][scenarioName][serviceName];

                    PatchObject(jobObject, dependencyObject);

                    configurationInstance.Jobs[serviceName] = jobObject.ToObject<Job>();
                }
            }

            foreach (var jobName in customJobs)
            {
                configurationInstance.Jobs[jobName] = new Job();
            }

            foreach (var job in configurationInstance.Jobs)
            {
                // Force all jobs as self-contained by default. This can be overrided by command line config.
                // This can't be done in ServerJob for backward compatibility
                job.Value.SelfContained = true;

                job.Value.Service = job.Key;
            }

            // After that point we only modify the JObject representation of Configuration
            configuration = JObject.FromObject(configurationInstance);

            // Apply profiles
            foreach (var profileName in profiles)
            {
                if (!configurationInstance.Profiles.ContainsKey(profileName))
                {
                    var availableProfiles = String.Join("', '", configurationInstance.Profiles.Keys);
                    throw new ControllerException($"Could not find a profile named '{profileName}'. Possible values: '{availableProfiles}'");
                }

                var profile = (JObject)configuration["Profiles"][profileName];
                
                // Copy the profile variables to the jobs in this profile
                // such that it will override what is in the source job.
                // Otherwise the variables in the profile would not override
                // the ones in the source profile as they would be patching
                // the global variables.

                var profileVariables = profile.GetValue("Variables", StringComparison.OrdinalIgnoreCase);
                if (profileVariables is JObject variables)
                {
                    var profileJobs = profile.GetValue("Jobs", StringComparison.OrdinalIgnoreCase) as JObject ?? new JObject();

                    foreach (var profileJobProperty in profileJobs.Properties())
                    {
                        var profileJob = (JObject)profileJobProperty.Value;

                        var profileJobVariables = profileJob.GetValue("Variables", StringComparison.OrdinalIgnoreCase) as JObject;

                        if (profileJobVariables == null)
                        {
                            profileJob.Add("Variables", profileJobVariables = new JObject());
                        }

                        PatchObject(profileJobVariables, variables);
                    }
                }

                PatchObject(configuration, profile);
            }

            // Apply custom arguments
            foreach (var argument in arguments)
            {
                JToken node = configuration["Jobs"];

                var segments = argument.Key.Split('.');

                foreach (var segment in segments)
                {
                    node = ((JObject)node).GetValue(segment, StringComparison.OrdinalIgnoreCase);

                    if (node == null)
                    {
                        throw new ControllerException($"Could not find part of the configuration path: '{argument}'");
                    }
                }

                if (node is JArray jArray)
                {
                    jArray.Add(argument.Value);
                }
                else if (node is JValue jValue)
                {
                    // The value is automatically converted to the destination type
                    jValue.Value = argument.Value;
                }
                else if (node is JObject jObject)
                {
                    // String to Object mapping -> try to parse as KEY=VALUE
                    var argumentSegments = argument.Value.ToString().Split('=', 2);

                    if (argumentSegments.Length != 2)
                    {
                        throw new ControllerException($"Argument value '{argument.Value}' could not assigned to `{segments.Last()}`.");
                    }

                    jObject[argumentSegments[0]] = argumentSegments[1];
                }
            }

            // Evaluate templates

            foreach (JProperty property in configuration["Jobs"] ?? new JObject())
            {
                var job = property.Value;
                var rootVariables = configuration["Variables"] as JObject ?? new JObject();
                var jobVariables = job["Variables"] as JObject ?? new JObject();

                var variables = MergeVariables(rootVariables, jobVariables, commandLineVariables);

                ApplyTemplates(job, new TemplateContext { Model = variables });
            }

            var result = configuration.ToObject<Configuration>();

            return result;
        }

        private static void ApplyTemplates(JToken node, TemplateContext templateContext)
        {
            foreach (var token in node.Children())
            {
                if (token is JValue jValue)
                {
                    if (jValue.Type == JTokenType.String)
                    {
                        var template = jValue.ToString();

                        if (template.Contains("{"))
                        {
                            if (FluidTemplate.TryParse(template, out var tree))
                            {
                                jValue.Value = tree.Render(templateContext);
                            }
                        }
                    }
                }
                else
                {
                    ApplyTemplates(token, templateContext);
                }
            }
        }

        public static async Task<JObject> LoadConfigurationAsync(string configurationFilenameOrUrl)
        {
            JObject localconfiguration;

            if (!string.IsNullOrWhiteSpace(configurationFilenameOrUrl))
            {
                string configurationContent;

                // Load the job definition from a url or locally
                try
                {
                    if (configurationFilenameOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        configurationContent = await _httpClient.GetStringAsync(configurationFilenameOrUrl);
                    }
                    else
                    {
                        configurationContent = File.ReadAllText(configurationFilenameOrUrl);
                    }
                }
                catch
                {
                    throw new ControllerException($"Configuration '{configurationFilenameOrUrl}' could not be loaded.");
                }

                localconfiguration = null;

                string configurationExtension = null;

                if (configurationFilenameOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    // Remove any query string to detect the correct extension
                    var questionMarkIndex = configurationFilenameOrUrl.IndexOf("?");
                    if (questionMarkIndex != -1)
                    {
                        var filename = configurationFilenameOrUrl.Substring(0, questionMarkIndex);
                        configurationExtension = Path.GetExtension(filename);
                    }
                    else
                    {
                        configurationExtension = Path.GetExtension(configurationFilenameOrUrl);
                    }
                }
                else
                {
                    configurationExtension = Path.GetExtension(configurationFilenameOrUrl);
                }

                switch (configurationExtension)
                {
                    case ".json":
                        localconfiguration = JObject.Parse(configurationContent);
                        break;

                    case ".yml":
                    case ".yaml":

                        var deserializer = new DeserializerBuilder()
                            .WithNodeTypeResolver(new JsonTypeResolver())
                            .Build();

                        var yamlObject = deserializer.Deserialize(new StringReader(configurationContent));

                        var serializer = new SerializerBuilder()
                            .JsonCompatible()
                            .Build();

                        var json = serializer.Serialize(yamlObject);
                        // Format json in case the schema validation fails and we need to render error line numbers
                        localconfiguration = JObject.Parse(json);

                        var schemaJson = File.ReadAllText(Path.Combine(Path.GetDirectoryName(typeof(Program).Assembly.Location), "benchmarks.schema.json"));
                        var schema = new Manatee.Json.Serialization.JsonSerializer().Deserialize<JsonSchema>(JsonValue.Parse(schemaJson));

                        var jsonToValidate = JsonValue.Parse(json);
                        var validationResults = schema.Validate(jsonToValidate, new JsonSchemaOptions { OutputFormat = SchemaValidationOutputFormat.Detailed });

                        if (!validationResults.IsValid)
                        {
                            // Create a json debug file with the schema
                            localconfiguration.AddFirst(new JProperty("$schema", "https://raw.githubusercontent.com/dotnet/crank/master/src/Microsoft.Crank.Controller/benchmarks.schema.json"));

                            var debugFilename = Path.Combine(Path.GetTempPath(), "crank-debug.json");
                            File.WriteAllText(debugFilename, localconfiguration.ToString(Formatting.Indented));

                            var errorBuilder = new StringBuilder();

                            errorBuilder.AppendLine($"Invalid configuration file '{configurationFilenameOrUrl}' at '{validationResults.InstanceLocation}'");
                            errorBuilder.AppendLine($"{validationResults.ErrorMessage}");
                            errorBuilder.AppendLine($"Debug file created at '{debugFilename}'");

                            throw new ControllerException(errorBuilder.ToString());
                        }

                        break;
                    default:
                        throw new ControllerException($"Unsupported configuration format: {configurationExtension}");
                }

                // Resolves local paths
                if (!configurationFilenameOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) && localconfiguration.ContainsKey("jobs"))
                {
                    foreach (JProperty job in localconfiguration["jobs"])
                    {
                        var jobObject = (JObject)job.Value;
                        if (jobObject.ContainsKey("source"))
                        {
                            var source = (JObject)jobObject["source"];
                            if (source.ContainsKey("localFolder"))
                            {
                                var localFolder = source["localFolder"].ToString();

                                if (!localFolder.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                                {
                                    var configurationFilename = new FileInfo(configurationFilenameOrUrl).FullName;
                                    var resolvedFilename = new FileInfo(Path.Combine(Path.GetDirectoryName(configurationFilename), localFolder)).FullName;

                                    source["localFolder"] = resolvedFilename;
                                }
                            }
                        }
                    }
                }

                // Process imports
                if (localconfiguration.ContainsKey("imports"))
                {
                    var mergeOptions = new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace, MergeNullValueHandling = MergeNullValueHandling.Merge };

                    foreach (JValue import in (JArray)localconfiguration.GetValue("imports"))
                    {
                        var importFilenameOrUrl = import.ToString();

                        var importedConfiguration = await LoadConfigurationAsync(importFilenameOrUrl);

                        if (importedConfiguration != null)
                        {
                            localconfiguration.Merge(importedConfiguration, mergeOptions);
                        }
                    }
                }

                localconfiguration.Remove("imports");

                return localconfiguration;
            }
            else
            {
                throw new ControllerException($"Invalid file path or url: '{configurationFilenameOrUrl}'");
            }
        }

        /// <summary>
        /// Merges a JObject into another one.
        /// </summary>
        public static void PatchObject(JObject source, JObject patch)
        {
            foreach (var patchProperty in patch)
            {
                var sourceProperty = source.Properties().Where(x => x.Name.Equals(patchProperty.Key, StringComparison.OrdinalIgnoreCase)).FirstOrDefault();

                // The property to patch exists
                if (sourceProperty != null)
                {
                    // if it's an object, patch it recursively
                    if (sourceProperty.Value.Type == JTokenType.Object)
                    {
                        if (patchProperty.Value.Type == JTokenType.Object)
                        {
                            // JObject to JObject mapping
                            PatchObject((JObject)sourceProperty.Value, (JObject)patchProperty.Value);
                        }
                    }
                    else if (sourceProperty.Value.Type == JTokenType.Array)
                    {
                        if (patchProperty.Value.Type == JTokenType.Array)
                        {
                            foreach (var value in (JArray)patchProperty.Value)
                            {
                                ((JArray)sourceProperty.Value).Add(value.DeepClone());
                            }
                        }
                    }
                    else
                    {
                        sourceProperty.Value = patchProperty.Value;
                    }
                }
                else
                {
                    source.Add(patchProperty.Key, patchProperty.Value.DeepClone());
                }
            }
        }

        private async static Task<bool> EnsureServerRequirementsAsync(IEnumerable<JobConnection> jobs, Job service)
        {
            if (String.IsNullOrEmpty(service.Options.RequiredOperatingSystem)
                && String.IsNullOrEmpty(service.Options.RequiredArchitecture))
            {
                return true;
            }

            foreach (var job in jobs)
            {
                var info = await job.GetInfoAsync();

                var os = info["os"]?.ToString();
                var arch = info["arch"]?.ToString();

                if (!String.IsNullOrEmpty(service.Options.RequiredOperatingSystem) && !String.Equals(os, service.Options.RequiredOperatingSystem, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (!String.IsNullOrEmpty(service.Options.RequiredArchitecture) && !String.Equals(arch, service.Options.RequiredArchitecture, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }
        private static Func<IEnumerable<double>, double> Percentile(int percentile)
        {
            return list =>
            {
                var orderedList = list.OrderBy(x => x).ToArray();

                var nth = (int)Math.Ceiling((double)orderedList.Length * percentile / 100);

                if (orderedList.Length > nth)
                {
                    return orderedList[nth];
                }
                else
                {
                    return 0;
                }
            };
        }

        private static void NormalizeResults(IEnumerable<JobConnection> jobs)
        {
            if (jobs == null || !jobs.Any())
            {
                return;
            }

            // For each job, compute the operation on each measurement
            foreach (var job in jobs)
            {
                // Group by name for easy lookup
                var measurements = job.Job.Measurements.GroupBy(x => x.Name).ToDictionary(x => x.Key, x => x.ToList());

                foreach (var metadata in job.Job.Metadata)
                {
                    if (!measurements.ContainsKey(metadata.Name))
                    {
                        continue;
                    }

                    if (metadata.Format == "json")
                    {
                        foreach (var measurement in measurements[metadata.Name])
                        {
                            measurement.Value = JsonConvert.DeserializeObject(measurement.Value.ToString());
                        }

                        metadata.Format = "object";
                    }
                }
            }
        }

        private static async Task<JobResults> CreateJobResultsAsync(Configuration configuration, string[] dependencies, Dictionary<string, List<JobConnection>> jobsByDependency)
        {
            var jobResults = new JobResults();

            foreach (var jobName in dependencies)
            {
                if (configuration.Jobs[jobName].Options.DiscardResults)
                {
                    continue;
                }

                // Skip failed jobs
                if (!jobsByDependency.ContainsKey(jobName))
                {
                    continue;
                }

                var jobResult = jobResults.Jobs[jobName] = new JobResult();
                var jobConnections = jobsByDependency[jobName];

                jobResult.Results = SummarizeResults(jobConnections);

                // Insert metadata
                if (!_excludeMetadataOption.HasValue())
                {
                    jobResult.Metadata = jobConnections[0].Job.Metadata.ToArray();

                }

                // Insert measurements
                if (!_excludeMeasurementsOption.HasValue())
                {
                    foreach (var jobConnection in jobConnections)
                    {
                        jobResult.Measurements.Add(jobConnection.Job.Measurements.ToArray());
                    }
                }

                jobResult.Environment = await jobConnections.First().GetInfoAsync();
            }

            return jobResults;
        }

        private static Dictionary<string, object> SummarizeResults(IEnumerable<JobConnection> jobs)
        {
            if (jobs == null || !jobs.Any())
            {
                return new Dictionary<string, object>();
            }

            // For each job, compute the operation on each measurement
            var groups = jobs.Select(job =>
            {
                // Group by name for easy lookup
                var measurements = job.Job.Measurements.GroupBy(x => x.Name).ToDictionary(x => x.Key, x => x.ToList());

                var summaries = new Dictionary<string, object>();

                foreach (var metadata in job.Job.Metadata)
                {
                    if (!measurements.ContainsKey(metadata.Name))
                    {
                        continue;
                    }

                    object result = 0;

                    switch (metadata.Aggregate)
                    {
                        case Operation.All:
                            result = measurements[metadata.Name].Select(x => x.Value).ToArray();
                            break;

                        case Operation.First:
                            result = measurements[metadata.Name].First().Value;
                            break;

                        case Operation.Last:
                            result = measurements[metadata.Name].Last().Value;
                            break;

                        case Operation.Avg:
                            result = measurements[metadata.Name].Average(x => Convert.ToDouble(x.Value));
                            break;

                        case Operation.Count:
                            result = measurements[metadata.Name].Count();
                            break;

                        case Operation.Max:
                            result = measurements[metadata.Name].Max(x => Convert.ToDouble(x.Value));
                            break;

                        case Operation.Median:
                            result = Percentile(50)(measurements[metadata.Name].Select(x => Convert.ToDouble(x.Value)));
                            break;

                        case Operation.Min:
                            result = measurements[metadata.Name].Min(x => Convert.ToDouble(x.Value));
                            break;

                        case Operation.Sum:
                            result = measurements[metadata.Name].Sum(x => Convert.ToDouble(x.Value));
                            break;

                        case Operation.Delta:
                            result = measurements[metadata.Name].Max(x => Convert.ToDouble(x.Value)) - measurements[metadata.Name].Min(x => Convert.ToDouble(x.Value));
                            break;

                        default:
                            result = measurements[metadata.Name].First().Value;
                            break;
                    }

                    if (!String.IsNullOrEmpty(metadata.Format) && metadata.Format != "object")
                    {
                        summaries[metadata.Name] = Convert.ToDouble(result);
                    }
                    else
                    {
                        summaries[metadata.Name] = result;
                    }
                }

                return summaries;
            }).ToArray();

            // Single job, no reduce operation is necessary
            if (groups.Length == 1)
            {
                return groups[0];
            }

            var reduced = new Dictionary<string, object>();

            var maxWidth = jobs.First().Job.Metadata.Max(x => x.ShortDescription.Length) + 2;

            foreach (var metadata in jobs.First().Job.Metadata)
            {
                var reducedValues = groups.SelectMany(x => x)
                    .Where(x => x.Key == metadata.Name);

                object reducedValue = null;

                switch (metadata.Reduce)
                {
                    case Operation.All:
                        reducedValue = reducedValues.ToArray();
                        break;

                    case Operation.First:
                        reducedValue = reducedValues.First().Value;
                        break;

                    case Operation.Last:
                        reducedValue = reducedValues.Last().Value;
                        break;

                    case Operation.Avg:
                        reducedValue = reducedValues.Average(x => Convert.ToDouble(x.Value));
                        break;

                    case Operation.Count:
                        reducedValue = reducedValues.Count();
                        break;

                    case Operation.Max:
                        reducedValue = reducedValues.Max(x => Convert.ToDouble(x.Value));
                        break;

                    case Operation.Median:
                        reducedValue = Percentile(50)(reducedValues.Select(x => Convert.ToDouble(x.Value)));
                        break;

                    case Operation.Min:
                        reducedValue = reducedValues.Min(x => Convert.ToDouble(x.Value));
                        break;

                    case Operation.Sum:
                        reducedValue = reducedValues.Sum(x => Convert.ToDouble(x.Value));
                        break;

                    case Operation.Delta:
                        reducedValue = reducedValues.Max(x => Convert.ToDouble(x.Value)) - reducedValues.Min(x => Convert.ToDouble(x.Value));
                        break;

                    default:
                        reducedValue = reducedValues.First().Value;
                        break;
                }

                reduced[metadata.Name] = reducedValue;

                Log.Quiet("");
                Log.Quiet($"# Summary");

                if (metadata.Format != "object")
                {
                    if (!String.IsNullOrEmpty(metadata.Format))
                    {
                        Console.WriteLine($"{(metadata.ShortDescription + ":").PadRight(maxWidth)} {Convert.ToDouble(reducedValue).ToString(metadata.Format)}");
                    }
                    else
                    {
                        Console.WriteLine($"{(metadata.ShortDescription + ":").PadRight(maxWidth)} {reducedValue.ToString()}");
                    }
                }

            }

            return reduced;

        }

        private static void WriteMeasures(JobConnection job)
        {
            // Handle old server versions that don't expose measurements
            if (!job.Job.Measurements.Any() || !job.Job.Metadata.Any())
            {
                return;
            }

            // Group by name for easy lookup
            var measurements = job.Job.Measurements.GroupBy(x => x.Name).ToDictionary(x => x.Key, x => x.ToList());
            var maxWidth = job.Job.Metadata.Max(x => x.ShortDescription.Length) + 2;

            var previousSource = "";

            foreach (var metadata in job.Job.Metadata)
            {
                if (!measurements.ContainsKey(metadata.Name))
                {
                    continue;
                }

                if (previousSource != metadata.Source)
                {
                    Log.Quiet("");
                    Log.Quiet($"## {metadata.Source}:");

                    previousSource = metadata.Source;
                }

                object result = 0;

                switch (metadata.Aggregate)
                {
                    case Operation.All:
                        result = measurements[metadata.Name].Select(x => x.Value).ToArray();
                        break;

                    case Operation.First:
                        result = measurements[metadata.Name].First().Value;
                        break;

                    case Operation.Last:
                        result = measurements[metadata.Name].Last().Value;
                        break;

                    case Operation.Avg:
                        result = measurements[metadata.Name].Average(x => Convert.ToDouble(x.Value));
                        break;

                    case Operation.Count:
                        result = measurements[metadata.Name].Count();
                        break;

                    case Operation.Max:
                        result = measurements[metadata.Name].Max(x => Convert.ToDouble(x.Value));
                        break;

                    case Operation.Median:
                        result = Percentile(50)(measurements[metadata.Name].Select(x => Convert.ToDouble(x.Value)));
                        break;

                    case Operation.Min:
                        result = measurements[metadata.Name].Min(x => Convert.ToDouble(x.Value));
                        break;

                    case Operation.Sum:
                        result = measurements[metadata.Name].Sum(x => Convert.ToDouble(x.Value));
                        break;

                    case Operation.Delta:
                        result = measurements[metadata.Name].Max(x => Convert.ToDouble(x.Value)) - measurements[metadata.Name].Min(x => Convert.ToDouble(x.Value));
                        break;

                    default:
                        result = measurements[metadata.Name].First().Value;
                        break;
                }

                // We don't render the result if it's a raw object
                if (metadata.Format != "object")
                {
                    if (!String.IsNullOrEmpty(metadata.Format))
                    {
                        Console.WriteLine($"{(metadata.ShortDescription + ":").PadRight(maxWidth)} {Convert.ToDouble(result).ToString(metadata.Format)}");
                    }
                    else
                    {
                        Console.WriteLine($"{(metadata.ShortDescription + ":").PadRight(maxWidth)} {result.ToString()}");
                    }
                }
            }
        }

        private static async Task CheckUpdateAsync()
        {
            var packageVersionUrl = "https://api.nuget.org/v3-flatcontainer/microsoft.crank.controller/index.json";

            try
            {
                var content = await _httpClient.GetStringAsync(packageVersionUrl);
                var document = JObject.Parse(content);
                var versions = (JArray)document["versions"];
                var last = versions.FirstOrDefault().ToString();

                var attribute = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>();

                if (new NuGetVersion(last) > new NuGetVersion(attribute.InformationalVersion))
                {
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    Console.WriteLine($"A new version is available on NuGet.org ({last}). Run 'dotnet tool update Microsoft.Crank.Controller -g' to update");
                    Console.ResetColor();
                }
            }
            catch
            {
            }
        }
    }
}
