﻿using BenchmarkDotNet.Attributes;
using Common;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace DotNetCliPerf
{
    public abstract class CoreApp : DotNetApp
    {
        private const string _globalJson = @"{ ""sdk"": { ""version"": ""0.0.0"" } }";

        [Params("2.0.2", "2.1.4", "2.1.300-preview2-008012", "2.1.300-preview2-008044")]
        public string SdkVersion { get; set; }

        [Params(true, false)]
        public bool Restore { get; set; }

        // [Params(false, true)]
        public bool TieredJit { get; set; }

        [Params(MSBuildFlavor.Framework, MSBuildFlavor.Core)]
        public MSBuildFlavor MSBuildFlavor { get; set; }

        [Params("2.0", "2.1")]
        public string TargetFramework { get; set; }

        [Params(false, true)]
        public bool RazorCompileOnBuild { get; set; }

        public override void GlobalSetup()
        {
            if (TieredJit)
            {
                Environment.Add("COMPLUS_EXPERIMENTAL_TieredCompilation", "1");
            }

            base.GlobalSetup();
        }

        protected override void CopyApp()
        {
            base.CopyApp();

            File.WriteAllText(Path.Combine(RootTempDir, "global.json"), _globalJson.Replace("0.0.0", SdkVersion));

            if (MSBuildFlavor == MSBuildFlavor.Core)
            {
                // Verify version
                var output = DotNet("--info");
                if (!output.Contains($"Version:            {SdkVersion}"))
                {
                    throw new InvalidOperationException($"Incorrect SDK version");
                }
            }
        }

        protected override void Build(bool first = false)
        {
            if (MSBuildFlavor == MSBuildFlavor.Framework)
            {
                var argument = "/t:build";
                if (RazorCompileOnBuild)
                {
                    argument += " /p:RazorCompileOnBuild=true /p:UseRazorBuildServer=true";
                }

                MSBuild(argument, restore: first || Restore);
            }
            else
            {
                var argument = "build";
                if (RazorCompileOnBuild)
                {
                    argument += " /p:RazorCompileOnBuild=true /p:UseRazorBuildServer=true";
                }
                DotNet(argument, restore: first || Restore);
            }
        }

        protected string DotNet(
            string dotnetArguments,
            string appArguments = null,
            string workingSubDirectory = "",
            bool restore = true,
            bool build = true,
            bool throwOnError = true)
        {
            var p = StartDotNet(dotnetArguments, appArguments, workingSubDirectory, restore, build);
            return Util.WaitForExit(p.Process, p.OutputBuilder, p.ErrorBuilder, throwOnError: throwOnError);
        }

        protected (Process Process, StringBuilder OutputBuilder, StringBuilder ErrorBuilder) StartDotNet(
            string dotnetArguments,
            string appArguments = null,
            string workingSubDirectory = "",
            bool restore = true,
            bool build = true)
        {
            if (build && NodeReuse)
            {
                throw new InvalidOperationException("Core MSBuild currently does not support NodeReuse");
            }

            var arguments = dotnetArguments +
                (Parallel ? "" : " /m:1") +
                (restore ? "" : " --no-restore") +
                (build ? "" : " --no-build") +
                (appArguments == null ? "" : " -- " + appArguments);

            return Util.StartProcess(
                "dotnet",
                arguments,
                Path.Combine(RootTempDir, workingSubDirectory),
                environment: Environment
            );
        }

    }
}
