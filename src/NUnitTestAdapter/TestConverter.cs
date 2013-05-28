using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using NUnit.Core;
using TestResult = Microsoft.VisualStudio.TestPlatform.ObjectModel.TestResult;
using System.Reflection;
using System.IO;
using System.Text.RegularExpressions;

namespace NUnit.VisualStudio.TestAdapter
{
    public class TestConverter : IDisposable
    {
        private Dictionary<string, TestCase> vsTestCaseMap;
        private string sourceAssembly;
        private Dictionary<string, NUnit.Core.TestNode> nunitTestCases;
        private NavigationData navigationData;

        #region Constructors

        private readonly bool isBuildOnTfs;
        public TestConverter(string sourceAssembly, Dictionary<string, NUnit.Core.TestNode> nunitTestCases, bool isbuildOnTfs)
        {
            this.sourceAssembly = sourceAssembly;
            this.vsTestCaseMap = new Dictionary<string, TestCase>();
            this.nunitTestCases = nunitTestCases;
            this.navigationData = new NavigationData(sourceAssembly);
            this.isBuildOnTfs = isbuildOnTfs;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Converts an NUnit test into a TestCase for Visual Studio,
        /// using the best method available according to the exact
        /// type passed and caching results for efficiency.
        /// </summary>
        public TestCase ConvertTestCase(NUnit.Core.ITest test)
        {
            if (test.IsSuite)
                throw new ArgumentException("The argument must be a test case", "test");

            // Return cached value if we have one
            if (vsTestCaseMap.ContainsKey(test.TestName.UniqueName))
                return vsTestCaseMap[test.TestName.UniqueName];

            // See if this is a TestNode - if not, try to
            // find one in our cache of NUnit TestNodes
            var testNode = test as TestNode;
            if (testNode == null && nunitTestCases.ContainsKey(test.TestName.UniqueName))
                testNode = nunitTestCases[test.TestName.UniqueName];

            // No test node: just build a TestCase without any
            // navigation data using the TestName
            if (testNode == null)
                return MakeTestCaseFromTestName(test.TestName);
            
            // Use the TestNode and cache the result
            var testCase = MakeTestCaseFromNUnitTest(testNode);
            vsTestCaseMap.Add(test.TestName.UniqueName, testCase);
            return testCase;             
        }

        /// <summary>
        /// Makes a TestCase from a TestNode, adding
        /// navigation data if it can be found.
        /// </summary>
        public TestCase MakeTestCaseFromNUnitTest(NUnit.Core.ITest nunitTest)
        {
            var testCase = MakeTestCaseFromTestName(nunitTest.TestName);

            var navData = navigationData.For(nunitTest.ClassName, nunitTest.MethodName);
            if (navData != null)
            {
                testCase.CodeFilePath = navData.FileName;
                testCase.LineNumber = navData.MinLineNumber;
            }

            testCase.AddTraitsFromNUnitTest(nunitTest);

            return testCase;
        }

        /// <summary>
        /// Makes a TestCase without source info from TestName.
        /// </summary>
        public TestCase MakeTestCaseFromTestName(TestName testName)
        {
            var testCase = new TestCase(
                                 /*    this.isBuildOnTfs ? testName.UniqueName :*/ testName.FullName,
                                     new Uri(NUnitTestExecutor.ExecutorUri),
                                     this.sourceAssembly)
                {
                    DisplayName = testName.Name,
                    CodeFilePath = null,
                    LineNumber = 0
                };

            return testCase;
        }

        public TestResult ConvertTestResult(NUnit.Core.TestResult result)
        {
            TestCase ourCase = ConvertTestCase(result.Test);

            TestResult ourResult = new TestResult(ourCase)
                {
                    DisplayName = ourCase.DisplayName,
                    Outcome = ResultStateToTestOutcome(result.ResultState),
                    Duration = TimeSpan.FromSeconds(result.Time)
                };
            // TODO: Remove this when NUnit provides a better duration
            if (ourResult.Duration == TimeSpan.Zero && (ourResult.Outcome == TestOutcome.Passed || ourResult.Outcome == TestOutcome.Failed))
                ourResult.Duration = TimeSpan.FromTicks(1);
            ourResult.ComputerName = Environment.MachineName;

            // TODO: Stuff we don't yet set
            //   StartTime   - not in NUnit result
            //   EndTime     - not in NUnit result
            //   Messages    - could we add messages other than the error message? Where would they appear?
            //   Attachments - don't exist in NUnit

            if (result.Message != null)
                ourResult.ErrorMessage = GetErrorMessage(result);

            if (!string.IsNullOrEmpty(result.StackTrace))
            {
                string stackTrace = StackTraceFilter.Filter(result.StackTrace);
                ourResult.ErrorStackTrace = stackTrace;
                //if (!string.IsNullOrEmpty(stackTrace))
                //{
                //    var stackFrame = new Internal.Stacktrace(stackTrace).GetTopStackFrame();
                //    if (stackFrame != null)
                //    {
                //       /ourResult.ErrorFilePath = stackFrame.FileName;
                //        ourResult.SetPropertyValue(TestResultProperties.ErrorLineNumber, stackFrame.LineNumber);
                //    }
                //}
            }

            return ourResult;
        }

        public void Dispose()
        {
            if (this.navigationData != null)
                this.navigationData.Dispose();
        }

        #endregion

        #region Helper Methods

        // Public for testing
        public static TestOutcome ResultStateToTestOutcome(ResultState resultState)
        {
            switch (resultState)
            {
                case ResultState.Cancelled:
                    return TestOutcome.None;
                case ResultState.Error:
                    return TestOutcome.Failed;
                case ResultState.Failure:
                    return TestOutcome.Failed;
                case ResultState.Ignored:
                    return TestOutcome.Skipped;
                case ResultState.Inconclusive:
                    return TestOutcome.None;
                case ResultState.NotRunnable:
                    return TestOutcome.Failed;
                case ResultState.Skipped:
                    return TestOutcome.Skipped;
                case ResultState.Success:
                    return TestOutcome.Passed;
            }

            return TestOutcome.None;
        }

        private string GetErrorMessage(NUnit.Core.TestResult result)
        {
            string message = result.Message;
            string NL = Environment.NewLine;

            // If we're running in the IDE, remove any caret line from the message
            // since it will be displayed using a variable font and won't make sense.
            if (message != null && RunningUnderIDE && (result.ResultState == ResultState.Failure || result.ResultState == ResultState.Inconclusive))
            {
                string pattern = NL + "  -*\\^" + NL;
                message = Regex.Replace(message, pattern, NL, RegexOptions.Multiline);
            }

            return message;
        }

        #endregion

        #region Private Properties

        private string exeName;
        private bool RunningUnderIDE
        {
            get
            {
                if (exeName == null)
                {
                    Assembly entryAssembly = Assembly.GetEntryAssembly();
                    if (entryAssembly != null)
                        exeName = Path.GetFileName(AssemblyHelper.GetAssemblyPath(entryAssembly));
                }
                
                return exeName == "vstest.executionengine.exe";
            }
        }

        #endregion
    }
}