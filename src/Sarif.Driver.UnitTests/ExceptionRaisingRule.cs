﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis.Sarif.Sdk;

namespace Microsoft.CodeAnalysis.Sarif.Driver.Sdk
{
    internal class ExceptionRaisingRule : IRuleDescriptor, ISkimmer<TestAnalysisContext>
    {
        internal static ExceptionCondition s_exceptionCondition;

        private ExceptionCondition _exceptionCondition;

        public ExceptionRaisingRule()
        {
            _exceptionCondition = s_exceptionCondition;

            if (_exceptionCondition == ExceptionCondition.InvokingConstructor)
            {
                throw new InvalidOperationException(nameof(ExceptionCondition.InvokingConstructor));
            }
        }

        public string ExceptionRaisingRuleId = "TEST1001";

        public string Id
        {
            get
            {
                if (_exceptionCondition == ExceptionCondition.AccessingId)
                {
                    throw new InvalidOperationException(nameof(ExceptionCondition.AccessingId));
                }
                return ExceptionRaisingRuleId;
            }
        }

        public string Name
        {
            get
            {
                if (_exceptionCondition == ExceptionCondition.AccessingName)
                {
                    throw new InvalidOperationException(nameof(ExceptionCondition.AccessingName));
                }
                return nameof(ExceptionRaisingRule);
            }
        }

        public string FullDescription
        {
            get { return "Test Rule Description"; }
        }

        public string ShortDescription
        {
            get
            {
                throw new NotImplementedException();
            }
        }

        public Dictionary<string, string> Options
        {
            get
            {
                throw new NotImplementedException();
            }
        }

        public Dictionary<string, string> FormatSpecifiers
        {
            get
            {
                throw new NotImplementedException();
            }
        }

        public Dictionary<string, string> Properties
        {
            get
            {
                throw new NotImplementedException();
            }
        }

        public void Analyze(TestAnalysisContext context)
        {
            if (_exceptionCondition == ExceptionCondition.InvokingAnalyze)
            {
                throw new InvalidOperationException(nameof(ExceptionCondition.InvokingAnalyze));
            }
        }

        public AnalysisApplicability CanAnalyze(TestAnalysisContext context, out string reasonIfNotApplicable)
        {
            reasonIfNotApplicable = null;
            if (_exceptionCondition == ExceptionCondition.InvokingCanAnalyze)
            {
                throw new InvalidOperationException(nameof(ExceptionCondition.InvokingCanAnalyze));
            }
            return AnalysisApplicability.ApplicableToSpecifiedTarget;
        }

        public void Initialize(TestAnalysisContext context)
        {
            if (_exceptionCondition == ExceptionCondition.InvokingInitialize)
            {
                throw new InvalidOperationException(nameof(ExceptionCondition.InvokingInitialize));
            }
        }
    }
}
