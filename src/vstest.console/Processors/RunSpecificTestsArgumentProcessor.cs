// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.TestPlatform.CommandLine.Processors
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Diagnostics.Contracts;
    using System.Globalization;
    using System.IO;
    using System.Linq;

    using Microsoft.VisualStudio.TestPlatform.Client.RequestHelper;
    using Microsoft.VisualStudio.TestPlatform.CommandLine.Internal;
    using Microsoft.VisualStudio.TestPlatform.CommandLine.TestPlatformHelpers;
    using Microsoft.VisualStudio.TestPlatform.Common;
    using Microsoft.VisualStudio.TestPlatform.Common.Interfaces;
    using Microsoft.VisualStudio.TestPlatform.ObjectModel;
    using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client;
    using Microsoft.VisualStudio.TestPlatform.Utilities;
    using CommandLineResources = Microsoft.VisualStudio.TestPlatform.CommandLine.Resources.Resources;

    internal class RunSpecificTestsArgumentProcessor : IArgumentProcessor
    {
        public const string CommandName = "/Tests";

        private Lazy<IArgumentProcessorCapabilities> metadata;

        private Lazy<IArgumentExecutor> executor;

        public Lazy<IArgumentProcessorCapabilities> Metadata
        {
            get
            {
                if (this.metadata == null)
                {
                    this.metadata = new Lazy<IArgumentProcessorCapabilities>(() => new RunSpecificTestsArgumentProcessorCapabilities());
                }

                return this.metadata;
            }
        }

        public Lazy<IArgumentExecutor> Executor
        {
            get
            {
                if (this.executor == null)
                {
                    this.executor = new Lazy<IArgumentExecutor>(() =>
                    new RunSpecificTestsArgumentExecutor(
                        CommandLineOptions.Instance,
                        RunSettingsManager.Instance,
                        TestRequestManager.Instance,
                        ConsoleOutput.Instance));
                }

                return this.executor;
            }

            set
            {
                this.executor = value;
            }
        }
    }

    internal class RunSpecificTestsArgumentProcessorCapabilities : BaseArgumentProcessorCapabilities
    {
        public override string CommandName => RunSpecificTestsArgumentProcessor.CommandName;

        public override bool IsAction => true;

        public override bool AllowMultiple => false;

        public override string HelpContentResourceName => CommandLineResources.RunSpecificTestsHelp;

        public override HelpContentPriority HelpPriority => HelpContentPriority.RunSpecificTestsArgumentProcessorHelpPriority;

        public override ArgumentProcessorPriority Priority => ArgumentProcessorPriority.Normal;
    }

    internal class RunSpecificTestsArgumentExecutor : IArgumentExecutor
    {
        public const char SplitDelimiter = ',';
        public const char EscapeDelimiter = '\\';

        #region Fields

        /// <summary>
        /// Used for getting sources.
        /// </summary>
        private CommandLineOptions commandLineOptions;

        /// <summary>
        /// The instance of testPlatforms
        /// </summary>
        private ITestRequestManager testRequestManager;

        /// <summary>
        /// Used for sending output.
        /// </summary>
        internal IOutput output;

        /// <summary>
        /// RunSettingsManager to get currently active run settings.
        /// </summary>
        private IRunSettingsProvider runSettingsManager;

        /// <summary>
        /// Given Collection of strings for filtering test cases
        /// </summary>
        private Collection<string> selectedTestNames;

        /// <summary>
        /// Used for tracking the total no. of tests discovered from the given sources.
        /// </summary>
        private long discoveredTestCount = 0;

        /// <summary>
        /// Collection of test cases that match atleast one of the given search strings
        /// </summary>
        private Collection<TestCase> selectedTestCases = new Collection<TestCase>();

        /// <summary>
        /// Effective run settings applicable to test run after inferring the multi-targeting settings.
        /// </summary>
        private string effectiveRunSettings = null;

        /// <summary>
        /// List of filters that have not yet been discovered
        /// </summary>
        HashSet<string> undiscoveredFilters = new HashSet<string>();

        /// <summary>
        /// Registers for discovery events during discovery
        /// </summary>
        private ITestDiscoveryEventsRegistrar discoveryEventsRegistrar;

        #endregion

        #region Constructor

        /// <summary>
        /// Default constructor.
        /// </summary>
        public RunSpecificTestsArgumentExecutor(
            CommandLineOptions options,
            IRunSettingsProvider runSettingsProvider,
            ITestRequestManager testRequestManager,
            IOutput output)
        {
            Contract.Requires(options != null);
            Contract.Requires(testRequestManager != null);

            this.commandLineOptions = options;
            this.testRequestManager = testRequestManager;

            this.runSettingsManager = runSettingsProvider;
            this.output = output;
            this.discoveryEventsRegistrar = new DiscoveryEventsRegistrar(this.discoveryRequest_OnDiscoveredTests);
        }

        #endregion

        #region IArgumentProcessor

        /// <summary>
        /// Splits given the search strings and adds to selectTestNamesCollection.
        /// </summary>
        /// <param name="argument"></param>
        public void Initialize(string argument)
        {
            if (!string.IsNullOrWhiteSpace(argument))
            {
                // cmd/ps has the max string input length limitation
                // make the argument can accept a txt file that records the specific case names. 
                if (argument.EndsWith(".txt"))
                {
                    var filePath = argument;
                    argument = File.ReadAllText(filePath);
                    this.output.WriteLine($"Read specified cases from {filePath} successfully", OutputLevel.Information);
                }
                this.selectedTestNames = new Collection<string>(
                    argument.Tokenize(SplitDelimiter, EscapeDelimiter)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Select(s => s.Trim()).ToList());
            }

            if (this.selectedTestNames == null || this.selectedTestNames.Count <= 0)
            {
                throw new CommandLineException(CommandLineResources.SpecificTestsRequired);
            }

            // by default all filters are not discovered on launch
            this.undiscoveredFilters = new HashSet<string>(this.selectedTestNames);
        }

        /// <summary>
        /// Execute specific tests that match any of the given strings.
        /// </summary>
        /// <returns></returns>
        public ArgumentProcessorResult Execute()
        {
            Contract.Assert(this.output != null);
            Contract.Assert(this.commandLineOptions != null);
            Contract.Assert(this.testRequestManager != null);
            Contract.Assert(!string.IsNullOrWhiteSpace(this.runSettingsManager.ActiveRunSettings.SettingsXml));

            if (!this.commandLineOptions.Sources.Any())
            {
                throw new CommandLineException(string.Format(CultureInfo.CurrentUICulture, CommandLineResources.MissingTestSourceFile));
            }

            if (!string.IsNullOrWhiteSpace(this.commandLineOptions.TestCaseFilterValue))
            {
                throw new CommandLineException(string.Format(CultureInfo.CurrentUICulture, CommandLineResources.InvalidTestCaseFilterValueForSpecificTests));
            }

            this.effectiveRunSettings = this.runSettingsManager.ActiveRunSettings.SettingsXml;

            // Discover tests from sources and filter on every discovery reported.
            this.DiscoverTestsAndSelectSpecified(this.commandLineOptions.Sources);

            // Now that tests are discovered and filtered, we run only those selected tests.
            this.ExecuteSelectedTests();

            return ArgumentProcessorResult.Success;
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Discovers tests from the given sources and selects only specified tests.
        /// </summary>
        /// <param name="sources"> Test source assemblies paths. </param>
        private void DiscoverTestsAndSelectSpecified(IEnumerable<string> sources)
        {
            this.output.WriteLine(CommandLineResources.StartingDiscovery, OutputLevel.Information);
            if (!string.IsNullOrEmpty(EqtTrace.LogFile))
            {
                this.output.Information(false, CommandLineResources.VstestDiagLogOutputPath, EqtTrace.LogFile);
            }

            this.testRequestManager.DiscoverTests(
                new DiscoveryRequestPayload() { Sources = sources, RunSettings = this.effectiveRunSettings }, this.discoveryEventsRegistrar, Constants.DefaultProtocolConfig);
        }

        /// <summary>
        ///  Executes the selected tests
        /// </summary>
        private void ExecuteSelectedTests()
        {
            if (this.selectedTestCases.Count > 0)
            {
                if (this.undiscoveredFilters.Count() != 0)
                {
                    string missingFilters = string.Join(", ", this.undiscoveredFilters);
                    string warningMessage = string.Format(CultureInfo.CurrentCulture, CommandLineResources.SomeTestsUnavailableAfterFiltering, this.discoveredTestCount, missingFilters);
                    this.output.Warning(false, warningMessage);
                }

                // for command line keep alive is always false.
                bool keepAlive = false;

                EqtTrace.Verbose("RunSpecificTestsArgumentProcessor:Execute: Test run is queued.");
                var runRequestPayload = new TestRunRequestPayload() { TestCases = this.selectedTestCases.ToList(), RunSettings = this.effectiveRunSettings, KeepAlive = keepAlive, TestPlatformOptions = new TestPlatformOptions() { TestCaseFilter = this.commandLineOptions.TestCaseFilterValue }};
                this.testRequestManager.RunTests(runRequestPayload, null, null, Constants.DefaultProtocolConfig);
            }
            else
            {
                string warningMessage;
                if (this.discoveredTestCount > 0)
                {
                    // No tests that matched any of the given strings.
                    warningMessage = string.Format(CultureInfo.CurrentCulture, CommandLineResources.NoTestsAvailableAfterFiltering, this.discoveredTestCount, string.Join(", ", this.selectedTestNames));
                }
                else
                {
                    // No tests were discovered from the given sources.
                    warningMessage = string.Format(CultureInfo.CurrentUICulture, CommandLineResources.NoTestsAvailableInSources, string.Join(", ", this.commandLineOptions.Sources));

                    if (string.IsNullOrEmpty(this.commandLineOptions.TestAdapterPath))
                    {
                        warningMessage = string.Format(CultureInfo.CurrentCulture, CommandLineResources.StringFormatToJoinTwoStrings, warningMessage, CommandLineResources.SuggestTestAdapterPathIfNoTestsIsFound);
                    }
                }

                this.output.Warning(false, warningMessage);
            }
        }

        /// <summary>
        /// Filter discovered tests and find matching tests from given search strings.
        /// Any name of the test that can match multiple strings will be added only once.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        private void discoveryRequest_OnDiscoveredTests(object sender, DiscoveredTestsEventArgs args)
        {
            this.discoveredTestCount += args.DiscoveredTestCases.Count();
            foreach (var testCase in args.DiscoveredTestCases)
            {
                foreach (var nameCriteria in this.selectedTestNames)
                {
                    if (testCase.FullyQualifiedName.IndexOf(nameCriteria, StringComparison.OrdinalIgnoreCase) != -1)
                    {
                        this.selectedTestCases.Add(testCase);

                        // If a testcase matched then a filter matched - so remove the filter from not found list
                        this.undiscoveredFilters.Remove(nameCriteria);
                        break;
                    }
                }
            }
        }

        #endregion

        private class DiscoveryEventsRegistrar : ITestDiscoveryEventsRegistrar
        {
            private EventHandler<DiscoveredTestsEventArgs> discoveredTestsHandler;

            public DiscoveryEventsRegistrar(EventHandler<DiscoveredTestsEventArgs> discoveredTestsHandler)
            {
                this.discoveredTestsHandler = discoveredTestsHandler;
            }

            public void LogWarning(string message)
            {
                ConsoleLogger.RaiseTestRunWarning(message);
            }

            public void RegisterDiscoveryEvents(IDiscoveryRequest discoveryRequest)
            {
                discoveryRequest.OnDiscoveredTests += this.discoveredTestsHandler;
            }

            public void UnregisterDiscoveryEvents(IDiscoveryRequest discoveryRequest)
            {
                discoveryRequest.OnDiscoveredTests -= this.discoveredTestsHandler;
            }
        }
    }
}