//
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
//

namespace Microsoft.PowerShell.EditorServices.Test.Shared.References
{
    public class FindsReferencesOnBuiltInCommandWithAlias
    {
        public static readonly ScriptRegion SourceDetails =
            new ScriptRegion
            {
                File = TestUtilities.NormalizePath("References/SimpleFile.ps1"),
                StartLineNumber = 14,
                StartColumnNumber = 3
            };
    }
    public class FindsReferencesOnBuiltInAlias
    {
        public static readonly ScriptRegion SourceDetails =
            new ScriptRegion
            {
                File = TestUtilities.NormalizePath("References/SimpleFile.ps1"),
                StartLineNumber = 15,
                StartColumnNumber = 2
            };
    }
}

