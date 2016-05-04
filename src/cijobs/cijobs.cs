using System;
using System.IO;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using System.CommandLine;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.DotNet.Cli.Utils;
using Microsoft.DotNet.Tools.Common;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ManagedCodeGen
{
    class cijobs 
    {
        public enum Command {
            List,
            Copy
        }
        
        public enum ListOption {
            Invalid,
            Jobs,
            Builds,
            Number
        }
        
        // Define options to be parsed 
        public class Config
        {
            private ArgumentSyntax syntaxResult;
            private Command command = Command.List;
            private ListOption listOption = ListOption.Invalid;
            private string jobName;
            private string forkUrl;
            private int number;
            private string matchPattern = String.Empty;
            private string coreclrBranchName = "master";
            private string privateBranchName;
            private bool lastSuccessful = true;
            private bool install = false;
            private bool unzip = false;
            private string outputPath;
            private string rid;
            private string jitDasmRoot;
  
            public Config(string[] args) {

                syntaxResult = ArgumentSyntax.Parse(args, syntax =>
                {
                    // NOTE!!! - Commands and their options are ordered.  Moving an option out of line
                    // could move it to another command.  Take a careful look at how they're organized
                    // before changing.
                    syntax.DefineCommand("list", ref command, Command.List, "List jobs on the CI system.");
                    syntax.DefineOption("j|job", ref jobName, "Name of the job.");
                    syntax.DefineOption("b|branch", ref coreclrBranchName, "Name of the branch (dotnet/coreclr).");
                    syntax.DefineOption("m|match", ref matchPattern, "Regex pattern used to select job output.");
                    syntax.DefineOption("n|number", ref number, "Job number.");
                    syntax.DefineOption("l|last_successful", ref lastSuccessful, "List last successfull build");
                    
                    syntax.DefineCommand("copy", ref command, Command.Copy, "Start a job on the CI system.");
                    syntax.DefineOption("j|job", ref jobName, "Name of the job.");
                    syntax.DefineOption("n|number", ref number, "Job number.");
                    syntax.DefineOption("b|branch", ref coreclrBranchName, "Name of branch.");
                    syntax.DefineOption("o|output", ref outputPath, "Output path.");
                    syntax.DefineOption("u|unzip", ref unzip, "Unzip copied artifacts");
                    syntax.DefineOption("i|install", ref install, "Install tool in asmdiff.json");
                });
                
                // Extract system RID from dotnet cli
                List<string> commandArgs = new List<string> { "--info" };
                Microsoft.DotNet.Cli.Utils.Command infoCmd = Microsoft.DotNet.Cli.Utils.Command.Create(
                    "dotnet", commandArgs);
                infoCmd.CaptureStdOut();
                infoCmd.CaptureStdErr();
                
                CommandResult result = infoCmd.Execute();
                
                if (result.ExitCode != 0)
                {
                    Console.WriteLine("dotnet --info returned non-zero");
                }
                
                var lines = result.StdOut.Split(new [] {Environment.NewLine}, StringSplitOptions.None);
                
                foreach(var line in lines) {
                    Regex pattern = new Regex(@"RID:\s*([A-Za-z0-9\.-]*)$");
                    Match match = pattern.Match(line);
                    if (match.Success)
                    {
                        rid = match.Groups[1].Value;
                    }
                }
                
                jitDasmRoot = Environment.GetEnvironmentVariable("JIT_DASM_ROOT");
                
                // Run validation code on parsed input to ensure we have a sensible scenario.
                
                validate();
            }
            
            private void validate() 
            {
                switch (command)
                {
                    case Command.List:
                    {
                        validateList();
                    }
                    break;
                    case Command.Copy:
                    {
                        ;
                    }
                    break;
                }
            }
            
            private void validateCopy()
            {
                ;
            }
            
            private void validateList()
            {
                if (jobName != null)
                {
                    listOption = ListOption.Builds;
                    
                    if (matchPattern != String.Empty) {
                        syntaxResult.ReportError("Match pattern not valid with --job");
                    }
                }
                else
                {
                    listOption = ListOption.Jobs;
                }
            }
            
            public Command DoCommand { get { return command; } }
            public ListOption DoListOption { get { return listOption; } }
            public string JobName { get { return jobName; } }
            public int Number { get { return number; } }
            public string MatchPattern { get { return matchPattern; } }
            public string CoreclrBranchName { get { return coreclrBranchName; } }
            public bool LastSuccessful { get { return lastSuccessful; } }
            public bool DoInstall { get { return install; } }
            public bool DoUnzip { get { return unzip; } }
            public string OutputPath { get { return outputPath; } }
            public bool HasJitDasmRoot { get { return (jitDasmRoot != null); } }
            public string JitDasmRoot { get { return jitDasmRoot; } }
            public string RID { get { return rid; } }
            
        }
        
        struct Artifact {
            public string fileName;
            public string relativePath;
        }
        
        struct Revision {
            public string SHA1;
        }
        class Action {
            public Revision lastBuiltRevision;
        }
        class BuildInfo {
            public List<Action> actions;
            public List<Artifact> artifacts;
            public string result;
        }
        
        struct Job {
            public string name;
            public string url;
        }
        
        struct ProductJobs {
            public List<Job> jobs;
        }
        
        struct Build {
            public int number;
            public string url;
            public BuildInfo info;
        }
        
        struct JobBuilds {
            public List<Build> builds;
            public Build lastSuccessfulBuild;
        }

        static int Main(string[] args)
        {
            Config config = new Config(args);
            int error = 0;
            
            CIClient cic = new CIClient(config);
            
            Command currentCommand = config.DoCommand;
            switch (currentCommand) {
                case Command.List: {
                    ListCommand.List(cic, config).Wait();
                    break;
                }
                case Command.Copy: {
                    CopyCommand.Copy(cic, config).Wait();
                    break;
                }
                default: {
                    Console.WriteLine("super bad!  why no command!");
                    error = 1;
                    break;
                }
            }
            
            return error;
        }

        class CIClient 
        {
            private HttpClient client;
            
            public CIClient(Config config)
            {
                client = new HttpClient();
                client.BaseAddress = new Uri("http://dotnet-ci.cloudapp.net/");
                client.DefaultRequestHeaders.Accept.Clear();
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            }
            
            public async Task<IEnumerable<Job>> GetProductJobs(string productName, string branchName) 
            {
                string productString 
                    = String.Format("job/{0}/job/{1}/api/json?&tree=jobs[name,url]", 
                        productName, branchName);
                HttpResponseMessage response = await client.GetAsync(productString);
                    
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    var productJobs = JsonConvert.DeserializeObject<ProductJobs>(json);
                    return productJobs.jobs;
                }
                else
                {
                    return Enumerable.Empty<Job>();
                }
            }
            
            public async Task<IEnumerable<Build>> GetJobBuilds(string productName, string branchName, string jobName)
            {
                var jobString 
                    = String.Format(@"job/dotnet_coreclr/job/master/job/{0}", jobName);
                var messageString 
                    = String.Format("{0}/api/json?&tree=builds[number,url],lastSuccessfulBuild[number,url]",
                        jobString);
                HttpResponseMessage response = await client.GetAsync(messageString);
                
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    var jobBuilds = JsonConvert.DeserializeObject<JobBuilds>(json);
                    var builds = jobBuilds.builds;
                    
                    var count = builds.Count();
                    for(int i = 0; i < count; i++)
                    {
                        var build = builds[i];
                        // fill in build info
                        build.info = GetJobBuildInfo(jobName, build.number).Result;
                        builds[i] = build;
                    }
                    
                    return jobBuilds.builds;
                }
                else
                {
                    return Enumerable.Empty<Build>();
                }
            }
            
            public async Task<BuildInfo> GetJobBuildInfo(string jobName, int number) 
            {
                string buildString = String.Format("{0}/{1}/{2}", "job/dotnet_coreclr/job/master/job",
                   jobName , number);
                string buildMessage = String.Format("{0}/{1}", buildString,
                   "api/json?&tree=actions[lastBuiltRevision[SHA1]],artifacts[fileName,relativePath],result");
                HttpResponseMessage response = await client.GetAsync(buildMessage);
                
                if (response.IsSuccessStatusCode) 
                {
                    var buildInfoJson = await response.Content.ReadAsStringAsync();
                    var info = JsonConvert.DeserializeObject<BuildInfo>(buildInfoJson);
                    return info;
                }
                else
                {
                    return null;
                }
            }
        }

        class ListCommand {
            // List jobs and their details from the dotnet_coreclr project on .NETCI Jenkins instance.
            // List functionality:
            //    if --job is not specified, list jobs under branch. (default is "master" set in Config).
            //    if --job is specified, list job instances by id with details.
            //    if --job and --id is specified list particular job instance, status, and artifacts.
            // 
            public static async Task List(CIClient cic, Config config) {   
                switch (config.DoListOption)
                {
                    case ListOption.Jobs:
                    {
                        var jobs = cic.GetProductJobs("dotnet_coreclr", "master").Result;
                        
                        if (config.MatchPattern != null) 
                        {
                            var pattern = new Regex(config.MatchPattern);
                            PrettyJobs(jobs.Where(x => pattern.IsMatch(x.name)));
                        }
                        else
                        {
                            PrettyJobs(jobs);    
                        }
                    }
                    break;
                    case ListOption.Builds:
                    {
                        var builds = cic.GetJobBuilds("dotnet_coreclr", 
                            "master", config.JobName);
                        PrettyBuilds(builds.Result);
                    }
                    break;
                    case ListOption.Number:
                    {
                        var info = cic.GetJobBuildInfo(config.JobName, config.Number);
                        // Pretty build info
                        PrettyBuildInfo(info.Result);
                    }
                    break;
                    default:
                    {
                        Console.WriteLine("Unknown list option!");
                    }
                    break;
                }
            }

            static void PrettyJobs (IEnumerable<Job> jobs) {
                foreach(var job in jobs) {
                    Console.WriteLine("job {0}", job.name);
                }
            }
            
            static void PrettyBuilds (IEnumerable<Build> buildList) {
                foreach (var build in buildList) {
                    var result = build.info.result;
                    if (result != null) 
                    {
                        Console.Write("build {0} - {1} : ", build.number, result);
                        PrettyBuildInfo(build.info);
                    }
                }
            }
            
            static void PrettyBuildInfo(BuildInfo info, bool artifacts = false)
            {
                var actions = info.actions.Where(x => x.lastBuiltRevision.SHA1 != null);
                
                if (actions.Any())
                {
                    var action = actions.First();
                    Console.WriteLine("commit {0}", action.lastBuiltRevision.SHA1);
                }
                else
                {
                    Console.WriteLine("");
                }

                if (artifacts)
                {
                    Console.WriteLine("    artifacts:");
                    foreach (var artifact in info.artifacts) 
                    {
                        Console.WriteLine("       {0}", artifact.fileName);
                    }
                }
            }
        }
        
        class CopyCommand {
            public static async Task Copy(CIClient cic, Config config) {
                string tag = String.Format("{0}-{1}", config.JobName, config.Number);
                string outputPath = (config.OutputPath != null) 
                    ? config.OutputPath : config.JitDasmRoot;
                string toolPath = Path.Combine(outputPath, "tools", tag);
                // Object filled out with asmdiff.json if install selected.
                JObject jObj = null;
                JArray tools = null;
                bool install = false;
                string asmDiffPath = String.Empty;
                
                if (config.DoInstall)
                {
                    if (config.HasJitDasmRoot) 
                    {
                        asmDiffPath = Path.Combine(config.JitDasmRoot, "asmdiff.json");

                        if (File.Exists(asmDiffPath))
                        {
                            string configJson = File.ReadAllText(asmDiffPath);
                            jObj = JObject.Parse(configJson);
                            tools = (JArray)jObj["tools"];

                            if (tools.Where(x => (string)x["tag"] == tag).Any())
                            {
                                Console.WriteLine("{0} is already installed in the asmdiff.json. Remove before re-install.", tag);
                                return;
                            }
                        }
                    }
                    else
                    {
                        Console.WriteLine("Install specified but no asmdiff.json found - missing JIT_DASM_ROOT.");
                    }
                    
                    // Flag install to happen now that we've vetted the input.
                    install = true;
                }
                
                // Pull down the zip file.
                Directory.CreateDirectory(toolPath);
                DownloadZip(config, toolPath).Wait();
                
                if (install) {
                    JObject newTool = new JObject();
                    newTool.Add("tag", tag);  
                    // Derive underlying tool directory based on current RID.
                    string[] platStrings = config.RID.Split('.');
                    string platformPath = Path.Combine(toolPath, "Product");
                    foreach (var dir in Directory.EnumerateDirectories(platformPath))
                    {
                        if (Path.GetFileName(dir).ToUpper().Contains(platStrings[0].ToUpper())) {
                            newTool.Add("path", Path.GetFullPath(dir));
                            tools.Last.AddAfterSelf(newTool);
                            break;
                        }
                    }            
                    // Overwrite current asmdiff.json with new data.
                    using (var file = File.CreateText(asmDiffPath))
                    {
                        using (JsonTextWriter writer = new JsonTextWriter(file))
                        {
                            writer.Formatting = Formatting.Indented;
                            jObj.WriteTo(writer);
                        }
                    }
                }
            }
            
            public static async Task DownloadZip(Config config, string outputPath) 
            {                 
                // Add archive zip path info and copy tools to output location. 
                using (var client = new HttpClient())
                {
                    client.BaseAddress = new Uri("http://dotnet-ci.cloudapp.net/");
                    client.DefaultRequestHeaders.Accept.Clear();
                    client.DefaultRequestHeaders
                          .Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                    string messageString 
                        = String.Format("job/dotnet_coreclr/job/{0}/job/{1}/{2}/artifact/bin/Product/*zip*/Product.zip", 
                            config.CoreclrBranchName, config.JobName , config.Number);

                    Console.WriteLine("Downloading: {0}", messageString);

                    HttpResponseMessage response = await client.GetAsync(messageString);
                    
                    var zipPath = Path.Combine(outputPath, "Product.zip");
                    using (var outputStream = System.IO.File.Create(zipPath))
                    {
                        Stream inputStream = await response.Content.ReadAsStreamAsync();
                        inputStream.CopyTo(outputStream);
                    }
                    
                    if (config.DoUnzip)
                    {
                        // unzip archive in place.
                        List<string> commandArgs = new List<string>() { "-o", "-d", outputPath, zipPath };

                        Microsoft.DotNet.Cli.Utils.Command unzipCmd 
                            = Microsoft.DotNet.Cli.Utils.Command.Create("unzip", commandArgs);
                            
                        // By default forward to output to stdout/stderr.
                        unzipCmd.ForwardStdOut();
                        unzipCmd.ForwardStdErr();
                        CommandResult result = unzipCmd.Execute();

                        if (result.ExitCode != 0)
                        {
                            Console.WriteLine("unzip returned non-zero!");
                        }
                    }
                }
            }
        }
    }
}

