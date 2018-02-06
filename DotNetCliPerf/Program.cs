﻿using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess;
using BenchmarkDotNet.Validators;
using CommandLine;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace DotNetCliPerf
{
    class Options
    {
        [Option('t', "types", HelpText = "Comma-separated list of types to benchmark. Default is all types.", Separator = ',')]
        public IEnumerable<string> Types { get; set; }

        [Option('m', "methods", HelpText = "Comma-separated list of methods to benchmark. Default is all methods.", Separator = ',')]
        public IEnumerable<string> Methods { get; set; }

        [Option('p', "parameters", HelpText = "Comma-separated list of parameters to benchmark. Default is all parameters. Example: 'SdkVersion=1234|5678,SourceChanged=Leaf'",
            Separator = ',')]
        public IEnumerable<string> Parameters { get; set; }

        [Option('c', "targetCount", Default = 1)]
        public int TargetCount { get; set; }

        [Option('w', "warmupCount", Default = 0)]
        public int WarmupCount { get; set; }

        [Option('d', "debug")]
        public bool Debug { get; set; }
    }

    class Program
    {
        private static Stopwatch _stopwatch;

        static int Main(string[] args)
        {
            _stopwatch = Stopwatch.StartNew();

            Console.WriteLine($"[{_stopwatch.Elapsed}] Enter Main()");

            return Parser.Default.ParseArguments<Options>(args).MapResult(
                options => Run(options),
                _ => 1
            );
        }

        private static int Run(Options options)
        {
            Console.WriteLine($"[{_stopwatch.Elapsed}] Enter Run()");

            var job = new Job();
            job.Run.RunStrategy = RunStrategy.Monitoring;
            job.Run.LaunchCount = 1;
            job.Run.WarmupCount = options.WarmupCount;
            job.Run.TargetCount = options.TargetCount;

            // Increase timeout from default 5 minutes to 10 minutes.  Required for OrchardCore.
            job = job.With(new InProcessToolchain(timeout: TimeSpan.FromMinutes(10), codegenMode: BenchmarkActionCodegen.ReflectionEmit, logOutput: true));

            var config = ManualConfig.Create(DefaultConfig.Instance).With(job);

            if (options.Debug)
            {
                ((List<IValidator>)config.GetValidators()).Remove(JitOptimizationsValidator.FailOnError);
                config = config.With(JitOptimizationsValidator.DontFailOnError);
            }

            var allBenchmarks = new List<Benchmark>();
            foreach (var type in typeof(Program).Assembly.GetTypes().Where(t => !t.IsAbstract).Where(t => t.IsPublic))
            {
                allBenchmarks.AddRange(BenchmarkConverter.TypeToBenchmarks(type, config).Benchmarks);
            }

            var selectedBenchmarks = (IEnumerable<Benchmark>)allBenchmarks;
            var parameters = ParametersToDictionary(options.Parameters);

            // If not specified, default "Restore" to "true" for Core and "false" for Framework,
            // to match typical customer usage.
            if (!parameters.ContainsKey("Restore"))
            {
                selectedBenchmarks = selectedBenchmarks.Where(b =>
                {
                    if (b.Target.Type.Name.IndexOf("Core", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return (bool)b.Parameters["Restore"];
                    }
                    else if (b.Target.Type.Name.IndexOf("Framework", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return !(bool)b.Parameters["Restore"];
                    }
                    else
                    {
                        return true;
                    }
                });
            }

            // If not specified, default "Parallel" to "true" for both Core and Framework, to match typical customer usage.
            if (!parameters.ContainsKey("Parallel"))
            {
                selectedBenchmarks = selectedBenchmarks.Where(b =>
                {
                    if (b.Target.Type.Name.IndexOf("Core", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        b.Target.Type.Name.IndexOf("Framework", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return (bool)b.Parameters["Parallel"];
                    }
                    else
                    {
                        return true;
                    }
                });
            }

            // If not specified, default "SdkVersion" to "2.0.2" and "2.2.0" for Core
            if (!parameters.ContainsKey("SdkVersion"))
            {
                selectedBenchmarks = selectedBenchmarks.Where(b =>
                {
                    if (b.Target.Type.Name.IndexOf("Core", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return ((string)b.Parameters["SdkVersion"]) == "2.0.2" || ((string)b.Parameters["SdkVersion"]).StartsWith("2.2.0");
                    }
                    else
                    {
                        return true;
                    }
                });
            }

            // If not specified, default "MSBuildFlavor" to "Core" for Core, to match typical customer usage.
            if (!parameters.ContainsKey("MSBuildFlavor"))
            {
                selectedBenchmarks = selectedBenchmarks.Where(b =>
                {
                    if (b.Target.Type.Name.IndexOf("Core", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return ((MSBuildFlavor)b.Parameters["MSBuildFlavor"]) == MSBuildFlavor.Core;
                    }
                    else
                    {
                        return true;
                    }
                });
            }

            // Skip benchmarks with MSBuildFlavor=Core and NodeReuse=True, since Core MSBuild currently does
            // not support NodeReuse.
            selectedBenchmarks = selectedBenchmarks.Where(b =>
                !(((MSBuildFlavor?)b.Parameters["MSBuildFlavor"]) == MSBuildFlavor.Core &&
                  (bool)b.Parameters["NodeReuse"]));

            // If MSBuildFlavor=Core, MSBuildVersion is irrelevant, so limit it to "NotApplicable"
            selectedBenchmarks = selectedBenchmarks.Where(b =>
                !(((MSBuildFlavor?)b.Parameters["MSBuildFlavor"]) == MSBuildFlavor.Core &&
                  !b.Parameters["MSBuildVersion"].ToString().Equals("NotApplicable", StringComparison.OrdinalIgnoreCase)));

            // If MSBuildFlavor=Framework or type is Framework, MSBuildVersion is required, so skip "NotApplicable"
            selectedBenchmarks = selectedBenchmarks.Where(b =>
                !((((MSBuildFlavor?)b.Parameters["MSBuildFlavor"]) == MSBuildFlavor.Framework ||
                   b.Target.Type.Name.IndexOf("Framework", StringComparison.OrdinalIgnoreCase) >= 0) &&
                  b.Parameters["MSBuildVersion"].ToString().Equals("NotApplicable", StringComparison.OrdinalIgnoreCase)));

            // If not specified, default "NodeReuse" to "true" for Framework, to match typical customer usage.
            if (!parameters.ContainsKey("NodeReuse"))
            {
                selectedBenchmarks = selectedBenchmarks.Where(b =>
                {
                    if (((MSBuildFlavor?)b.Parameters["MSBuildFlavor"]) == MSBuildFlavor.Framework ||
                        b.Target.Type.Name.IndexOf("Framework", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return (bool)b.Parameters["NodeReuse"];
                    }
                    else
                    {
                        return true;
                    }
                });
            }

            // Large apps and "SourceChanged" methods can choose from SourceChanged.Leaf and SourceChanged.Root
            // All other apps and methods must use SourceChanged.NotApplicable
            selectedBenchmarks = selectedBenchmarks.Where(b =>
            {
                var sourceChanged = ((SourceChanged)b.Parameters["SourceChanged"]);

                if (b.Target.Type.Name.IndexOf("Large", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    b.Target.Method.Name.IndexOf("SourceChanged", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return (sourceChanged == SourceChanged.Leaf) || (sourceChanged == SourceChanged.Root);
                }
                else
                {
                    return sourceChanged == SourceChanged.NotApplicable;
                }
            });

            // If not specified, remove SourceChanged=Root, since SourceChanged=Leaf is tested more often
            if (!parameters.ContainsKey("SourceChanged"))
            {
                selectedBenchmarks = selectedBenchmarks.Where(b => ((SourceChanged)b.Parameters["SourceChanged"]) != SourceChanged.Root);
            }

            selectedBenchmarks = selectedBenchmarks.
                Where(b => !options.Types.Any() ||
                                       b.Target.Type.Name.ContainsAny(options.Types, StringComparison.OrdinalIgnoreCase)).
                            Where(b => !options.Methods.Any() ||
                                       b.Target.Method.Name.ContainsAny(options.Methods, StringComparison.OrdinalIgnoreCase)).
                            Where(b => b.Parameters.Match(parameters));

            Console.WriteLine($"[{_stopwatch.Elapsed}] Before BenchmarkRunner.Run()");

            BenchmarkRunner.Run(selectedBenchmarks.ToArray(), config);

            return 0;
        }

        private static IDictionary<string, IEnumerable<string>> ParametersToDictionary(IEnumerable<string> parameters)
        {
            var dict = new Dictionary<string, IEnumerable<string>>(parameters.Count(), StringComparer.OrdinalIgnoreCase);

            foreach (var p in parameters)
            {
                var parts = p.Split('=');
                var patterns = parts[1].Split('|');
                dict.Add(parts[0], patterns);
            }

            return dict;
        }
    }
}
