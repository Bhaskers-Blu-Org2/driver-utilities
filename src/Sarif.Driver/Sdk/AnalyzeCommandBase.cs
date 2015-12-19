﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.CodeAnalysis.Sarif.Driver;
using Microsoft.CodeAnalysis.Sarif.Sdk;

namespace Microsoft.CodeAnalysis.Sarif.Driver.Sdk
{
    public abstract class AnalyzeCommandBase<TContext, TOptions> : PlugInDriverCommand<TOptions>
        where TContext : IAnalysisContext, new()
        where TOptions : IAnalyzeOptions
    {
        public Exception ExecutionException { get; set; }

        public RuntimeConditions RuntimeErrors { get; set; }

        public static bool RaiseUnhandledExceptionInDriverCode { get; set; }

        public override int Run(TOptions analyzeOptions)
        {
            // 0. Initialize an common logger that drives all outputs. This
            //    object drives logging for console, statistics, etc.
            using (AggregatingLogger logger = InitializeLogger(analyzeOptions))
            {
                try
                {
                    Analyze(analyzeOptions, logger);
                }
                catch (ExitApplicationException<ExitReason> ex)
                {
                    // These exceptions have already been logged
                    ExecutionException = ex;
                    return FAILURE;
                }
                catch (Exception ex)
                {
                    // These exceptions escaped our net and must be logged here
                    TContext context = new TContext();
                    context.Rule = ErrorDescriptors.UnhandledEngineException;
                    context.Logger = logger;
                    LogUnhandledEngineException(ex, context);
                    ExecutionException = ex;
                    return FAILURE;
                }
            }

            return ((RuntimeErrors & RuntimeConditions.Fatal) == RuntimeConditions.NoErrors) ? SUCCESS : FAILURE;
        }

        private void Analyze(TOptions analyzeOptions, AggregatingLogger logger)
        {
            // 1. Scrape the analyzer options for settings that alter
            //    behaviors of binary parsers (such as settings for
            //    symbols resolution).
            InitializeFromOptions(analyzeOptions);

            // 2. Produce a comprehensive set of analysis targets 
            HashSet<string> targets = CreateTargetsSet(analyzeOptions);

            // 3. Proactively validate that we can locate and 
            //    access all analysis targets. Helper will return
            //    a list that potentially filters out files which
            //    did not exist, could not be accessed, etc.
            targets = ValidateTargetsExist(logger, targets);

            // 4. Create our policy, which will be shared across
            //    all context objects that are created during analysis
            PropertyBag policy = CreatePolicyFromOptions(analyzeOptions);

            // 5. Create short-lived context object to pass to 
            //    skimmers during initialization. The logger and
            //    policy objects are common to all context instances
            //    and will be passed on again for analysis.
            TContext context = CreateContext(analyzeOptions, logger, policy);

            // 6. Initialize report file, if configured.
            InitializeOutputFile(analyzeOptions, context, targets);

            // 7. Instantiate skimmers.
            HashSet<ISkimmer<TContext>> skimmers = CreateSkimmers(logger);

            // 8. Initialize skimmers. Initialize occurs a single time only.
            skimmers = InitializeSkimmers(skimmers, context);

            // 9. Run all analysis
            AnalyzeTargets(analyzeOptions, skimmers, context, targets);

            // 10. For test purposes, raise an unhandled exception if indicated
            if (RaiseUnhandledExceptionInDriverCode)
            {
                throw new InvalidOperationException(this.GetType().Name);
            }
        }

        internal AggregatingLogger InitializeLogger(IAnalyzeOptions analyzeOptions) 
        {
            var logger = new AggregatingLogger();
            logger.Loggers.Add(new ConsoleLogger(analyzeOptions.Verbose));

            if (analyzeOptions.Statistics)
            {
                logger.Loggers.Add(new StatisticsLogger());
            }

            return logger;
        }


        protected virtual void InitializeFromOptions(TOptions analyzeOptions)
        {
        }

        private static HashSet<string> CreateTargetsSet(TOptions analyzeOptions)
        {
            HashSet<string> targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string specifier in analyzeOptions.TargetFileSpecifiers)
            {
                // Currently, we do not filter on any extensions.
                var fileSpecifier = new FileSpecifier(specifier, recurse: analyzeOptions.Recurse, filter: "*");
                foreach (string file in fileSpecifier.Files) { targets.Add(file); }
            }

            return targets;
        }

        private HashSet<string> ValidateTargetsExist(IResultLogger logger, HashSet<string> targets)
        {
            return targets;
        }

        protected virtual TContext CreateContext(TOptions options, IResultLogger logger, PropertyBag policy, string filePath = null)
        {
            var context = new TContext();
            context.Logger = logger;
            context.Policy = policy;

            if (filePath != null)
            {
                context.TargetUri = new Uri(filePath);
            }

            return context;
        }

        private void InitializeOutputFile(TOptions analyzeOptions, TContext context, HashSet<string> targets)
        {
            string filePath = analyzeOptions.OutputFilePath;
            AggregatingLogger aggregatingLogger = (AggregatingLogger)context.Logger;

            if (!string.IsNullOrEmpty(filePath))
            {
                InvokeCatchingRelevantIOExceptions
                (
                    () => aggregatingLogger.Loggers.Add(
                            new SarifLogger(
                                analyzeOptions.OutputFilePath,
                                analyzeOptions.Verbose,
                                targets,
                                analyzeOptions.ComputeTargetsHash,
                                Prerelease)),
                    (ex) =>
                    {
                        LogExceptionCreatingLogFile(filePath, context, ex);
                        throw new ExitApplicationException<ExitReason>(SdkResources.UnexpectedApplicationExit, ex)
                        {
                            ExitReason = ExitReason.ExceptionCreatingLogFile
                        };
                    }
                );
            }
        }

        public void InvokeCatchingRelevantIOExceptions(Action action, Action<Exception> exceptionHandler)
        {
            try
            {
                action();
            }
            catch (UnauthorizedAccessException ex)
            {
                exceptionHandler(ex);
            }
            catch (IOException ex)
            {
                exceptionHandler(ex);
            }
        }

        private HashSet<ISkimmer<TContext>> CreateSkimmers(IResultLogger logger)
        {
            HashSet<ISkimmer<TContext>> result = new HashSet<ISkimmer<TContext>>();
            try
            {
                IEnumerable<ISkimmer<TContext>> skimmers;
                skimmers = DriverUtilities.GetExports<ISkimmer<TContext>>(DefaultPlugInAssemblies);

                foreach (ISkimmer<TContext> skimmer in skimmers)
                {
                    result.Add(skimmer);
                }
                return result;
            }
            catch (Exception ex)
            {
                RuntimeErrors |= RuntimeConditions.ExceptionInstantiatingSkimmers;
                throw new ExitApplicationException<ExitReason>(SdkResources.UnexpectedApplicationExit, ex)
                {
                    ExitReason = ExitReason.UnhandledExceptionInstantiatingSkimmers
                };
            }
        }

        protected virtual void AnalyzeTargets(
            TOptions options,
            IEnumerable<ISkimmer<TContext>> skimmers,
            TContext rootContext,
            IEnumerable<string> targets)
        {
            HashSet<string> disabledSkimmers = new HashSet<string>();

            foreach (string target in targets)
            {
                using (TContext context = AnalyzeTarget(options, skimmers, rootContext, target, disabledSkimmers))
                {
                }
            }
        }

        protected virtual TContext AnalyzeTarget(
            TOptions options,
            IEnumerable<ISkimmer<TContext>> skimmers,
            TContext rootContext,
            string target,
            HashSet<string> disabledSkimmers)
        {
            var context = CreateContext(options, rootContext.Logger, rootContext.Policy, target);

            if (context.TargetLoadException != null)
            {
                LogExceptionLoadingTarget(context);
                return context;
            }
            else if (!context.IsValidAnalysisTarget)
            {
                LogExceptionInvalidTarget(context);
                return context;
            }

            // Analyzing {0}...
            context.Rule = NoteDescriptors.AnalyzingTarget;
            context.Logger.Log(ResultKind.Note, context,
                string.Format(SdkResources.Analyzing, Path.GetFileName(context.TargetUri.LocalPath)));

            foreach (ISkimmer<TContext> skimmer in skimmers)
            {
                if (disabledSkimmers.Contains(skimmer.Id)) { continue; }

                string reasonForNotAnalyzing = null;
                context.Rule = skimmer;

                AnalysisApplicability applicability = AnalysisApplicability.Unknown;

                try
                {
                    applicability = skimmer.CanAnalyze(context, out reasonForNotAnalyzing);
                }
                catch (Exception ex)
                {
                    LogUnhandledRuleExceptionAssessingTargetApplicability(disabledSkimmers, context, skimmer, ex);
                    continue;
                }

                switch (applicability)
                {
                    case AnalysisApplicability.NotApplicableToSpecifiedTarget:
                    {
                        // Image '{0}' was not evaluated for check '{1}' as the analysis
                        // is not relevant based on observed binary metadata: {2}.
                        context.Logger.Log(ResultKind.NotApplicable,
                            context,
                            MessageUtilities.BuildTargetNotAnalyzedMessage(
                                context.TargetUri.LocalPath,
                                context.Rule.Name,
                                reasonForNotAnalyzing));

                        break;
                    }

                    case AnalysisApplicability.NotApplicableToAnyTargetWithoutPolicy:
                    {
                        // Check '{0}' was disabled for this run as the analysis was not 
                        // configured with required policy ({1}). To resolve this, 
                        // configure and provide a policy file on the BinSkim command-line 
                        // using the --policy argument (recommended), or pass 
                        // '--policy default' to invoke built-in settings. Invoke the 
                        // BinSkim.exe 'export' command to produce an initial policy file 
                        // that can be edited if required and passed back into the tool.
                        context.Logger.Log(ResultKind.ConfigurationError, context,
                            MessageUtilities.BuildRuleDisabledDueToMissingPolicyMessage(
                                context.Rule.Name,
                                reasonForNotAnalyzing));
                        disabledSkimmers.Add(skimmer.Id);
                        break;
                    }

                    case AnalysisApplicability.ApplicableToSpecifiedTarget:
                    {
                        try
                        {
                            skimmer.Analyze(context);
                        }
                        catch (Exception ex)
                        {
                            LogUnhandledRuleExceptionAnalyzingTarget(disabledSkimmers, context, skimmer, ex);
                        }
                        break;
                    }
                }
            }

            return context;
        }

        private void LogUnhandledEngineException(Exception ex, IAnalysisContext context)
        {
            // An unhandled exception was raised during analysis:
            // {1}
            context.Logger.Log(ResultKind.InternalError,
                context,
                string.Format(SdkResources.UnhandledEngineException,
                    ex.ToString()));

            RuntimeErrors |= RuntimeConditions.ExceptionInEngine;
        }

        private void LogExceptionLoadingRoslynAnalyzer(string analyzerFilePath, TContext context, Exception ex)
        {
            context.Rule = ErrorDescriptors.InvalidConfiguration;

            // An exception was raised attempting to load Roslyn analyzer '{0}'. Exception information:
            // {1}
            context.Logger.Log(ResultKind.ConfigurationError,
                context,
                string.Format(SdkResources.ExceptionLoadingAnalysisPlugIn,
                    analyzerFilePath,
                    context.TargetLoadException.ToString()));

            RuntimeErrors |= RuntimeConditions.ExceptionLoadingAnalysisPlugIn;
        }

        private void LogExceptionInvalidTarget(TContext context)
        {
            context.Rule = NoteDescriptors.InvalidTarget;

            // Image '{0}' was not analyzed as the it does not
            // appear to be a valid portable executable.
            context.Logger.Log(ResultKind.NotApplicable,
                context,
                string.Format(
                    SdkResources.TargetNotAnalyzed_InvalidTarget,
                    context.TargetUri.LocalPath));

            context.Dispose();
            RuntimeErrors |= RuntimeConditions.OneOrMoreTargetsNotValidToAnalyze;
        }

        private void LogExceptionLoadingTarget(TContext context)
        {
            context.Rule = ErrorDescriptors.InvalidConfiguration;

            // An exception was raised attempting to load analysis target '{0}'. Exception information:
            // {1}
            context.Logger.Log(ResultKind.ConfigurationError,
                context,
                string.Format(SdkResources.ExceptionLoadingAnalysisTarget,
                    context.TargetUri.LocalPath,
                    context.TargetLoadException.ToString()));

            context.Dispose();
            RuntimeErrors |= RuntimeConditions.ExceptionLoadingTargetFile;
        }

        private void LogExceptionCreatingLogFile(string fileName, TContext context, Exception ex)
        {
            // An exception was raised attempting to create output file '{0}'. Exception information:
            // {1}
            context.Rule = ErrorDescriptors.InvalidConfiguration;
            context.Logger.Log(ResultKind.ConfigurationError,
                context,
                string.Format(SdkResources.ExceptionCreatingLogFile,
                    fileName,
                    ex.ToString()));

            RuntimeErrors |= RuntimeConditions.ExceptionCreatingLogfile;
        }

        private void LogUnhandledExceptionInitializingRule(TContext context, ISkimmer<TContext> skimmer, Exception ex)
        {
            string ruleName = context.Rule.Name;
            // An unhandled exception was encountered initializing check '{0}', which 
            // has been disabled for the remainder of the analysis. Exception information:
            // {1}
            context.Rule = ErrorDescriptors.UnhandledRuleException;
            context.Logger.Log(ResultKind.InternalError,
                context,
                string.Format(SdkResources.ExceptionInitializingRule,
                    ruleName,
                    ex.ToString()));

            RuntimeErrors |= RuntimeConditions.ExceptionInSkimmerInitialize;
        }

        private void LogUnhandledRuleExceptionAssessingTargetApplicability(HashSet<string> disabledSkimmers, TContext context, ISkimmer<TContext> skimmer, Exception ex)
        {
            string ruleName = context.Rule.Name;
            // An unhandled exception was raised attempting to determine whether '{0}' 
            // is a valid analysis target for check '{1}' (which has been disabled 
            // for the remainder of the analysis). The exception may have resulted 
            // from a problem related to parsing image metadata and not specific to 
            // the rule, however. Exception information:
            // {2}
            context.Rule = ErrorDescriptors.UnhandledRuleException;
            context.Logger.Log(ResultKind.InternalError,
                context,
                string.Format(SdkResources.ExceptionCheckingApplicability,
                    context.TargetUri.LocalPath,
                    ruleName,
                    ex.ToString()));

            if (disabledSkimmers != null) { disabledSkimmers.Add(skimmer.Id); }

            RuntimeErrors |= RuntimeConditions.ExceptionRaisedInSkimmerCanAnalyze;
        }

        private void LogUnhandledRuleExceptionAnalyzingTarget(HashSet<string> disabledSkimmers, TContext context, ISkimmer<TContext> skimmer, Exception ex)
        {
            string ruleName = context.Rule.Name;
            // An unhandled exception was encountered analyzing '{0}' for check '{1}', 
            // which has been disabled for the remainder of the analysis.The 
            // exception may have resulted from a problem related to parsing 
            // image metadata and not specific to the rule, however.
            // Exception information:
            // {2}
            context.Rule = ErrorDescriptors.UnhandledRuleException;
            context.Logger.Log(ResultKind.InternalError,
                context,
                string.Format(SdkResources.ExceptionAnalyzingTarget,
                    context.TargetUri.LocalPath,
                    ruleName,
                    ex.ToString()));

            if (disabledSkimmers != null) { disabledSkimmers.Add(skimmer.Id); }

            RuntimeErrors |= RuntimeConditions.ExceptionInSkimmerAnalyze;
        }

        protected virtual HashSet<ISkimmer<TContext>> InitializeSkimmers(HashSet<ISkimmer<TContext>> skimmers, TContext context)
        {
            HashSet<ISkimmer<TContext>> disabledSkimmers = new HashSet<ISkimmer<TContext>>();

            // ONE-TIME initialization of skimmers. Do not call 
            // Initialize more than once per skimmer instantiation
            foreach (ISkimmer<TContext> skimmer in skimmers)
            {
                try
                {
                    context.Rule = skimmer;
                    skimmer.Initialize(context);
                }
                catch (Exception ex)
                {
                    RuntimeErrors |= RuntimeConditions.ExceptionInSkimmerInitialize;
                    LogUnhandledExceptionInitializingRule(context, skimmer, ex);
                    disabledSkimmers.Add(skimmer);
                }
            }

            foreach (ISkimmer<TContext> disabledSkimmer in disabledSkimmers)
            {
                skimmers.Remove(disabledSkimmer);
            }

            return skimmers;
        }


        private static PropertyBag CreatePolicyFromOptions(TOptions analyzeOptions)
        {
            PropertyBag policy = null;
            string policyFilePath = analyzeOptions.PolicyFilePath;

            if (!string.IsNullOrEmpty(policyFilePath))
            {
                policy = new PropertyBag();
                if (!policyFilePath.Equals("default", StringComparison.OrdinalIgnoreCase))
                {
                    policy.LoadFrom(policyFilePath);
                }
            }

            return policy;
        }       
    }
}