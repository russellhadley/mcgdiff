﻿using System;
using System.Diagnostics;
using System.CommandLine;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.DotNet.Cli.Utils;
using Microsoft.DotNet.Tools.Common;
using Newtonsoft.Json;

namespace ManagedCodeGen
{
    public class analyze
    {   
        public class Config
        {
            private ArgumentSyntax syntaxResult;
            private string basePath = null;
            private string diffPath = null;
            private bool recursive = false;
            private bool full = false;
            private bool warn = false;
            private int count = 5;
            private string json;
            private string csv;
            
            public Config(string[] args) {

                syntaxResult = ArgumentSyntax.Parse(args, syntax =>
                {
                    syntax.DefineOption("b|base", ref basePath, "Base file or directory.");
                    syntax.DefineOption("d|diff", ref diffPath, "Diff file or directory.");
                    syntax.DefineOption("r|recursive", ref recursive, "Search directories recursively.");
                    syntax.DefineOption("c|count", ref count, 
                        "Count of files and methods (at most) to output in the summary."
                      + " (count) improvements and (count) regressions of each will be included."
                      + " (default 5)");
                    syntax.DefineOption("w|warn", ref warn, 
                        "Generate warning output for files/methods that only "
                      + "exists in one dataset or the other (only in base or only in diff).");
                    syntax.DefineOption("json", ref json, 
                        "Dump analysis data to specified file in JSON format.");
                    syntax.DefineOption("csv", ref csv, "Dump analysis data to specified file in CSV format.");
                });
                
                // Run validation code on parsed input to ensure we have a sensible scenario.
                validate();
            }
            
            void validate() {
                if (basePath == null) {
                    syntaxResult.ReportError("Base path (--base) is required.");
                }
                
                if (diffPath == null) {
                    syntaxResult.ReportError("Diff path (--diff) is required.");
                }
            }
            
            public string BasePath { get { return basePath; } }
            public string DiffPath { get { return diffPath; } }
            public bool Recursive { get { return recursive; } }
            public bool Full { get { return full; } }
            public bool Warn { get { return warn; } }
            public int Count { get { return count; } }
            public string CSVFileName { get { return csv; } }
            public string JsonFileName { get { return json; } }
            public bool DoGenerateJson { get { return json != null; } }
            public bool DoGenerateCSV { get { return csv != null; } }
        }
        
        public class FileInfo {
            public string path;
            public IEnumerable<MethodInfo> methodList;
        }
        
        // Custom comparer for the FileInfo class
        class FileInfoComparer : IEqualityComparer<FileInfo>
        {
            public bool Equals(FileInfo x, FileInfo y) {
                if (Object.ReferenceEquals(x, y)) return true;
                if (Object.ReferenceEquals(x, null) || Object.ReferenceEquals(y, null))
                    return false;
                return x.path == y.path;
            }

            // If Equals() returns true for a pair of objects 
            // then GetHashCode() must return the same value for these objects.

            public int GetHashCode(FileInfo fi) {
                if (Object.ReferenceEquals(fi, null)) return 0;
                return fi.path == null ? 0 : fi.path.GetHashCode();
            }
        }

        public class MethodInfo {
            public string name;
            public int totalBytes;
            public int prologBytes;
            public int functionCount;
            public IEnumerable<int> functionOffsets;
            public override string ToString() {
                return String.Format(@"name {0}, total bytes {1}, prolog bytes {2}, " 
                    + "function count {3}, offsets {4}",
                    name, totalBytes, prologBytes, functionCount, 
                    String.Join(", ", functionOffsets.ToArray()));
            }
        }
        
        // Custom comparer for the MethodInfo class
        class MethodInfoComparer : IEqualityComparer<MethodInfo>
        {
            public bool Equals(MethodInfo x, MethodInfo y) {
                if (Object.ReferenceEquals(x, y)) return true;
                if (Object.ReferenceEquals(x, null) || Object.ReferenceEquals(y, null))
                    return false;
                return x.name == y.name;
            }

            // If Equals() returns true for a pair of objects 
            // then GetHashCode() must return the same value for these objects.

            public int GetHashCode(MethodInfo mi) {
                if (Object.ReferenceEquals(mi, null)) return 0;
                return mi.name == null ? 0 : mi.name.GetHashCode();
            }
        }
        
        public class FileDelta {
            public string path;
            public int baseBytes;
            public int diffBytes;
            public int deltaBytes { get { return diffBytes - baseBytes; } }
            public double ratioBytes { get { return (double)deltaBytes / (double)baseBytes; } }
            public IEnumerable<MethodInfo> methodsOnlyInBase;
            public IEnumerable<MethodInfo> methodsOnlyInDiff;
            public IEnumerable<MethodDelta> methodDeltaList;
        }
        
        public class MethodDelta {
            public string name;
            public int baseBytes;
            public int diffBytes;
            public int deltaBytes { get { return diffBytes - baseBytes; } }
            public double ratioBytes { get { return (double)deltaBytes / (double)baseBytes; } }
            public IEnumerable<int> baseOffsets;
            public IEnumerable<int> diffOffsets;
        }
        
        public static IEnumerable<FileInfo> ExtractFileInfo(string path, bool recursive) {        
            // if path is a directory, enumerate files and extract
            // otherwise just extract.
            SearchOption searchOption = (recursive) ? 
                SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            FileAttributes attr = File.GetAttributes(path);
            string fullRootPath = Path.GetFullPath(path);

            if ((attr & FileAttributes.Directory) == FileAttributes.Directory) {
                return Directory.EnumerateFiles(fullRootPath, "*.dasm", searchOption)
                         .Select(p => new FileInfo {
                             path = p.Substring(fullRootPath.Length).TrimStart(Path.DirectorySeparatorChar),
                             methodList = ExtractMethodInfo(p)
                         });
            }
            else {
                // In the single file case just create a list with a single
                // to satisfy the interface.
                return new List<FileInfo> { new FileInfo { 
                        path = Path.GetFileName(path), 
                        methodList = ExtractMethodInfo(path)
                    }
                };
            }   
        }

        // Extract lines of the passed in file and create a method info object
        // for each method descript line containing total bytes, prolog bytes, 
        // and offset in the file.
        public static IEnumerable<MethodInfo> ExtractMethodInfo(string filePath) {
            Regex namePattern = new Regex(@"for method (.*)$");
            Regex dataPattern = new Regex(@"code ([0-9]{1,}), prolog size ([0-9]{1,})");
            return File.ReadLines(filePath)
                             .Select((x,i) => new {line = x, index = i})
                             .Where(l => l.line.StartsWith(@"; Total bytes of code") 
                                        || l.line.StartsWith(@"; Assembly listing for method"))
                             .Select((x) => {
                                 var nameMatch = namePattern.Match(x.line);
                                 var dataMatch = dataPattern.Match(x.line);
                                 return new { 
                                     name = nameMatch.Groups[1].Value,
                                     // Use matched data or default to 0
                                     totalBytes = dataMatch.Success ? 
                                        Int32.Parse(dataMatch.Groups[1].Value) : 0,
                                     prologBytes = dataMatch.Success ? 
                                        Int32.Parse(dataMatch.Groups[2].Value) : 0,
                                     // Use function index only from non-data lines (the name line)
                                     functionOffset = dataMatch.Success ?
                                        0 : x.index
                                 };
                             })
                             .GroupBy(x => x.name)
                             .Select(x => new MethodInfo {
                                 name = x.Key,
                                 totalBytes = x.Sum(z => z.totalBytes),
                                 prologBytes = x.Sum(z => z.prologBytes),
                                 functionCount = x.Select(z => z).Where(z => z.totalBytes == 0).Count(),
                                 // for all non-zero function offsets create list.
                                 functionOffsets = x.Select(z => z)
                                                    .Where(z => z.functionOffset != 0)
                                                    .Select(z => z.functionOffset).ToList()
                             });
        }

        // Compare base and diff file lists and produce a sorted list of method
        // deltas by file.  Delta is computed diffBytes - baseBytes so positive
        // numbers are regressions. (lower is better)       
        public static IEnumerable<FileDelta> Comparator(IEnumerable<FileInfo> baseInfo, 
            IEnumerable<FileInfo> diffInfo) {
            MethodInfoComparer methodInfoComparer = new MethodInfoComparer();    
            return baseInfo.Join(diffInfo, b => b.path, d => d.path, (b, d) => {
                var deltaList = b.methodList.Join(d.methodList, 
                        x => x.name, y => y.name, (x, y) => new MethodDelta {
                            name = x.name,
                            baseBytes = x.totalBytes,
                            diffBytes = y.totalBytes,
                            baseOffsets = x.functionOffsets,
                            diffOffsets = y.functionOffsets
                        })
                        .Where(r => r.deltaBytes != 0)
                        .OrderByDescending(r => r.deltaBytes);
                return new FileDelta {
                    path = b.path,
                    baseBytes = deltaList.Sym(x => x.baseBytes),
                    diffBytes = deltaList.Sym(x => x.diffBytes),
                    methodsOnlyInBase = b.methodList.Except(d.methodList, methodInfoComparer),
                    methodsOnlyInDiff = d.methodList.Except(b.methodList, methodInfoComparer), 
                    methodDeltaList = deltaList
                };
            });
        }
        
        // Summarize differences across all the files.
        // Output:
        //     Total bytes differences
        //     Top 5 files by difference size
        //     Top 5 diffs by size across all files
        //
        //
        public static int Summarize(IEnumerable<FileDelta> fileDeltaList, int requestedCount) {
            var totalBytes = fileDeltaList.Sum(x => x.deltaBytes);
            Console.WriteLine("\nSummary:\n(Note: Lower is better)\n");
            Console.WriteLine("Total bytes of diff: {0}", totalBytes.ToString());
            if (totalBytes != 0) {
                Console.WriteLine("    diff is {0}", totalBytes < 0 ? "an improvement." : "a regression."); 
            }
            else {
                // Early out if there are not bytes of difference.
                return totalBytes;
            }
 
            var sortedFileDelta = fileDeltaList
                                      .Where(x => x.deltaBytes != 0)
                                      .OrderByDescending(d => d.deltaBytes).ToList();
            int sortedFileCount = sortedFileDelta.Count();
            int fileCount = (sortedFileCount < requestedCount) 
                ? sortedFileCount : requestedCount; 
            if (sortedFileDelta[0].deltaBytes > 0) {
                Console.WriteLine("\nTop file regressions by size (bytes):");                          
                foreach (var fileDelta in sortedFileDelta.GetRange(0, fileCount)
                                                         .Where(x => x.deltaBytes > 0)) {
                    Console.WriteLine("    {1} : {0}", fileDelta.path, fileDelta.deltaBytes);
                }
            }

            if (sortedFileDelta.Last().deltaBytes < 0) {
                // index of the element count from the end.
                int fileDeltaIndex = (sortedFileDelta.Count() - fileCount);
                Console.WriteLine("\nTop file improvements by size (bytes):");

                foreach (var fileDelta in sortedFileDelta.GetRange(fileDeltaIndex, fileCount)
                                                         .Where(x => x.deltaBytes < 0)
                                                         .OrderBy(x => x.deltaBytes)) {
                    Console.WriteLine("    {1} : {0}", fileDelta.path, fileDelta.deltaBytes);
                }
            }
            
            Console.WriteLine("\n{0} total files with size differences.", sortedFileCount);
          
            var sortedMethodDelta;
            
            if (ratio) {
                sortedMethodData = fileDeltaList
                                       .SelectMany(fd => fd.methodDeltaList, (fd, md) => new {
                                            path = fd.path,
                                            name = md.name,
                                            delta = md.deltaBytes
                                       }).OrderByDescending(x => x.delta).ToList();
            }
            else {
                sortedMethodData = fileDeltaList
                                       .SelectMany(fd => fd.methodDeltaList, (fd, md) => new {
                                            path = fd.path,
                                            name = md.name,
                                            delta = md.ratioBytes,
                                       }).OrderByDescending(x => x.delta).ToList();
            }

            int sortedMethodCount = sortedMethodDelta.Count();
            int methodCount = (sortedMethodCount < requestedCount) 
                ? sortedMethodCount : requestedCount;
            if (sortedMethodDelta[0].delta > 0) {
                Console.WriteLine("\nTop method regessions by size (bytes):");
                
                foreach (var method in sortedMethodDelta.GetRange(0, methodCount)
                                                        .Where(x => x.delta > 0)) {
                    Console.WriteLine("    {2} : {0} - {1}", method.path, method.name, method.delta);
                }
            }

            if (sortedMethodDelta.Last().deltaBytes < 0) {
                 // index of the element count from the end.
                int methodDeltaIndex = (sortedMethodCount - methodCount);
                Console.WriteLine("\nTop method improvements by size (bytes):");

                foreach (var method in sortedMethodDelta.GetRange(methodDeltaIndex, methodCount)
                                                        .Where(x => x.delta < 0)
                                                        .OrderBy(x => x.delta)) {
                    Console.WriteLine("    {2} : {0} - {1}", method.path, method.name, method.delta);
                }
            }
            
            Console.WriteLine("\n{0} total methods with size differences.", sortedMethodCount);
            
            return Math.Abs(totalBytes);
        }
 
        public static void WarnFiles(IEnumerable<FileInfo> diffList, IEnumerable<FileInfo> baseList) {
            FileInfoComparer fileInfoComparer = new FileInfoComparer();
            var onlyInBaseList = baseList.Except(diffList, fileInfoComparer);
            var onlyInDiffList = diffList.Except(baseList, fileInfoComparer);
            
            //  Go through the files and flag anything not in both lists.

            var onlyInBaseCount = onlyInBaseList.Count();
            if (onlyInBaseCount > 0) {
                Console.WriteLine("Warning: {0} files in base but not in diff.", onlyInBaseCount);
                Console.WriteLine("\nOnly in base files:");
                foreach(var file in onlyInBaseList) {
                    Console.WriteLine(file.path);
                }
            }
            
            var onlyInDiffCount = onlyInDiffList.Count();
            if (onlyInDiffCount > 0) {
                Console.WriteLine("Warning: {0} files in diff but not in base.", onlyInDiffCount);
                Console.WriteLine("\nOnly in diff files:");
                foreach(var file in onlyInDiffList) {
                    Console.WriteLine(file.path);
                }
            } 
        }
        
        public static void WarnMethods(IEnumerable<FileDelta> compareList) {
            foreach(var delta in compareList) {
                var onlyInBaseCount = delta.methodsOnlyInBase.Count();
                var onlyInDiffCount = delta.methodsOnlyInDiff.Count();
                
                if ((onlyInBaseCount > 0) || (onlyInDiffCount > 0)) {
                    Console.WriteLine("Mismatched methods in {0}", delta.path);
                    if(onlyInBaseCount > 0) {
                        Console.WriteLine("Base:");
                        foreach (var method in delta.methodsOnlyInBase) {
                            Console.WriteLine("    {0}", method.name);
                        }
                    }
                    if (onlyInDiffCount > 0) {
                        Console.WriteLine("Diff:");
                        foreach (var method in delta.methodsOnlyInDiff) {
                            Console.WriteLine("    {0}", method.name);
                        }   
                    }
                }
            }
        }
        
        public static void GenerateJson(IEnumerable<FileDelta> compareList, string path) {
            using (var outputStream = System.IO.File.Create(path)) {
                using (var outputStreamWriter = new StreamWriter(outputStream)) {
                    foreach(var file in compareList) {
                        if (file.deltaBytes == 0) {
                            // Early out if there are no diff bytes.
                            continue;
                        }
                                                      
                        try {
                            // Serialize file delta to output file.
                            outputStreamWriter.Write(JsonConvert.SerializeObject(file, Formatting.Indented));
                        }
                        catch (Exception e) {
                            Console.WriteLine("Exception serializing JSON: {0}", e.ToString());
                            break;
                        }
                    }
                }
            }  
        }
        
        public static void GenerateCSV(IEnumerable<FileDelta> compareList, string path) {
            using (var outputStream = System.IO.File.Create(path)) {
                using (var outputStreamWriter = new StreamWriter(outputStream)) {
                    outputStreamWriter.WriteLine("File, Method, Diff bytes, Base bytes, Delta, Delta/Base");
                    foreach(var file in compareList) {
                        if (file.deltaBytes == 0) {
                            // Early out if there are no diff bytes.
                            continue;
                        }
                        
                        foreach (var method in file.methodDeltaList) {
                            outputStreamWriter.WriteLine("{0}, {1}, {2}, {3}, {4}, {5}", file.path, 
                            method.name, method.diffBytes, method.baseBytes, method.deltaBytes, method.ratioBytes);
                        }
                    }
                }
            }  
        }
        
        public static bool DiffInText(string diffPath, string basePath) {
            // run get diff command to see if we have textual diffs.
            // (use git diff since it's already a dependency and cross platform)
            List<string> commandArgs = new List<string>();
            commandArgs.Add("diff");
            commandArgs.Add("--exit-code");
            commandArgs.Add("--name-only");
            commandArgs.Add(diffPath);
            commandArgs.Add(basePath);
            Command diffCmd = Command.Create(@"git", commandArgs);
            diffCmd.CaptureStdOut();
            diffCmd.CaptureStdErr();
            
            CommandResult result = diffCmd.Execute();
            
            if (result.ExitCode != 0) {
                // TODO - there's some issue with capturing stdout.  Just leave this commented out for now.
                // Console.WriteLine("here {0}", result.StdOut);
                // var lines = result.StdOut.Split(new [] {Environment.NewLine}, StringSplitOptions.None).ToList();
                Console.WriteLine("Found files with textual diffs.");
                return true;
            }
            return false;
        }
 
        public static int Main(string[] args)
        {
            // Parse incoming arguments
            Config config = new Config(args);
            
            // Early out if no textual diffs found.
            if (!DiffInText(config.DiffPath, config.BasePath)) {
                Console.WriteLine("No diffs found.");
                return 0;
            }
            
            // Extract method info from base and diff directory or file.
            var baseList = ExtractFileInfo(config.BasePath, config.Recursive);
            var diffList = ExtractFileInfo(config.DiffPath, config.Recursive);

            // Compare the method info for each file and generate a list of
            // non-zero deltas.  The lists that include files in one but not
            // the other are used as the comparator function only compares where it 
            // has both sides.

            var compareList = Comparator(baseList, diffList);
            
            // Generate warning lists if requested.
            if (config.Warn) {
                WarnFiles(diffList, baseList);
                WarnMethods(compareList);
            }
            
            if (config.DoGenerateCSV) {
                GenerateCSV(compareList, config.CSVFileName);
            }
            
            if (config.DoGenerateJson) {
                GenerateJson(compareList, config.JsonFileName);
            }
            
            return Summarize(compareList, config.Count);
        }
    }
}
