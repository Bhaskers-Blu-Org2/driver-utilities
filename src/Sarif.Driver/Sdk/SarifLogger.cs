﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

using Microsoft.CodeAnalysis.Sarif;
using Microsoft.CodeAnalysis.Sarif.Sdk;
using Microsoft.CodeAnalysis.Sarif.Writers;

using Newtonsoft.Json;

namespace Microsoft.CodeAnalysis.Sarif.Driver.Sdk
{
    public class SarifLogger : IDisposable, IResultLogger
    {
        private FileStream _fileStream;
        private TextWriter _textWriter;
        private JsonTextWriter _jsonTextWriter;
        private ResultLogJsonWriter _issueLogJsonWriter;

        public static ToolInfo CreateDefaultToolInfo(string prereleaseInfo = null)
        {
            Assembly assembly = typeof(SarifLogger).Assembly;
            string name = Path.GetFileNameWithoutExtension(assembly.Location);
            Version version = assembly.GetName().Version;

            ToolInfo toolInfo = new ToolInfo();
            toolInfo.Name = name;
            toolInfo.Version = version.Major.ToString() + "." + version.Minor.ToString() + "." + version.Build.ToString();
            toolInfo.FullName = name + " " + toolInfo.Version + (prereleaseInfo ?? "");

            return toolInfo;
        }

        public SarifLogger(
            string outputFilePath,
            bool verbose,
            IEnumerable<string> analysisTargets,
            bool computeTargetsHash,
            string prereleaseInfo)
        {
            Verbose = verbose;

            _fileStream = new FileStream(outputFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
            _textWriter = new StreamWriter(_fileStream);
            _jsonTextWriter = new JsonTextWriter(_textWriter);

            // for debugging it is nice to have the following line added.
            _jsonTextWriter.Formatting = Newtonsoft.Json.Formatting.Indented;

            _issueLogJsonWriter = new ResultLogJsonWriter(_jsonTextWriter);

            var toolInfo = CreateDefaultToolInfo(prereleaseInfo);

            RunInfo runInfo = new RunInfo();
            runInfo.AnalysisTargets = new List<FileReference>();

            foreach (string target in analysisTargets)
            {
                var fileReference = new FileReference()
                {
                    Uri = target.CreateUriForJsonSerialization(),
                };

                if (computeTargetsHash)
                {
                    string sha256Hash = HashUtilities.ComputeSha256Hash(target) ?? "[could not compute file hash]";
                    fileReference.Hashes = new List<Hash>(new Hash[]
                    {
                            new Hash()
                            {
                                Value = sha256Hash,
                                Algorithm = AlgorithmKind.Sha256,
                            }
                    });
                }
                runInfo.AnalysisTargets.Add(fileReference);
            }
            runInfo.InvocationInfo = Environment.CommandLine;

            _issueLogJsonWriter.WriteToolAndRunInfo(toolInfo, runInfo);
        }

        public bool Verbose { get; set; }

        public void Dispose()
        {
            // Disposing the json writer closes the stream but the textwriter 
            // still needs to be disposed or closed to write the results
            if (_issueLogJsonWriter != null) { _issueLogJsonWriter.Dispose(); }
            if (_textWriter != null) { _textWriter.Dispose(); }
        }

        public void Log(ResultKind messageKind, IAnalysisContext context, string message)
        {
            switch (messageKind)
            {
                case ResultKind.Pass:
                    {
                        if (Verbose)
                        {
                            WriteJsonIssue(context.TargetUri.LocalPath, context.Rule.Id, message, ResultKind.Pass);
                        }
                        break;
                    }

                case ResultKind.Error:
                case ResultKind.Warning:
                {
                        WriteJsonIssue(context.TargetUri.LocalPath, context.Rule.Id, message, ResultKind.Error);
                        break;
                    }

                case ResultKind.NotApplicable:
                    {
                        if (Verbose)
                        {
                            WriteJsonIssue(context.TargetUri.LocalPath, context.Rule.Id, message, ResultKind.NotApplicable);
                        }
                        break;
                    }

                case ResultKind.Note:
                    {
                        if (Verbose)
                        {
                            WriteJsonIssue(context.TargetUri.LocalPath, context.Rule.Id, message, ResultKind.Note);
                        }
                        break;
                    }

                case ResultKind.ConfigurationError:
                {
                    WriteJsonIssue(context.TargetUri.LocalPath, context.Rule.Id, message, ResultKind.ConfigurationError);
                    break;
                }

                case ResultKind.InternalError:
                    {
                        WriteJsonIssue(context.TargetUri.LocalPath, context.Rule.Id, message, ResultKind.InternalError);
                        break;
                    }

                default:
                    {
                        throw new InvalidOperationException();
                    }
            }
        }
        private void WriteJsonIssue(string binary, string ruleId, string message, ResultKind issueKind)
        {
            Result result = new Result();

            result.RuleId = ruleId;
            result.FullMessage = message;
            result.Kind = issueKind;
            result.Locations = new[]{
                new Sarif.Sdk.Location {  
                    AnalysisTarget = new[]
                    {
                        new PhysicalLocationComponent
                        {
                            // Why? When NewtonSoft serializes this Uri, it will use the
                            // original string used to construct the Uri. For a file path, 
                            // this will be the local file path. We want to persist this 
                            // information using the file:// protocol rendering, however.
                            Uri = binary.CreateUriForJsonSerialization(),
                            MimeType = MimeType.Binary
                        }
                    }
                }
            };

            _issueLogJsonWriter.WriteResult(result);
        }

        public void Log(ResultKind messageKind, IAnalysisContext context, FormattedMessage message)
        {
            throw new NotImplementedException();
        }
    }
}



