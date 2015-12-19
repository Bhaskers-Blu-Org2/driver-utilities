﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;

using Microsoft.CodeAnalysis.Sarif.Sdk;

using Xunit;

namespace Microsoft.CodeAnalysis.Sarif.Driver.Sdk
{
    internal static class SarifHelpers
    {
        public static void ValidateRunLog(RunLog runLog, Action<Result> resultAction)
        {
            ValidateToolInfo(runLog.ToolInfo);

            foreach (Result result in runLog.Results) { resultAction(result); }
        }

        public static void ValidateToolInfo(ToolInfo toolInfo)
        {
            Assert.Equal("Sarif.Driver", toolInfo.Name);
            // TODO version, etc
        }
    }
}
