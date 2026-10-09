using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace Cosmos.VisualStudio.Core.Testing
{
    public enum TestOutcome
    {
        None,
        Running,
        Passed,
        Failed,
        Skipped
    }

    public sealed class JUnitCase
    {
        public string Name { get; set; } = "";
        public string ClassName { get; set; } = "";
        public double TimeSeconds { get; set; }
        public TestOutcome Status { get; set; } = TestOutcome.Passed;
        public string Message { get; set; }
    }

    public sealed class JUnitSuite
    {
        public string Name { get; set; } = "";
        public int Tests { get; set; }
        public int Failures { get; set; }
        public int Skipped { get; set; }
        public double TimeSeconds { get; set; }
        public List<JUnitCase> Cases { get; } = new List<JUnitCase>();
        public string Architecture { get; set; }
        public bool TimedOut { get; set; }
        public string SystemErr { get; set; }
        public string SystemOut { get; set; }
    }

    /// <summary>
    /// Parses the JUnit-style XML written by Cosmos.TestRunner.Engine
    /// (OutputHandlerXml.cs). Only one testsuite per file in practice.
    /// </summary>
    public static class JUnitParser
    {
        public static JUnitSuite ParseFile(string path)
        {
            try
            {
                return Parse(File.ReadAllText(path));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return null;
            }
        }

        public static JUnitSuite Parse(string xml)
        {
            XDocument doc;
            try
            {
                doc = XDocument.Parse(xml);
            }
            catch (XmlException)
            {
                return null;
            }

            XElement suiteElement = doc.Descendants("testsuite").FirstOrDefault();
            if (suiteElement == null)
            {
                return null;
            }

            var suite = new JUnitSuite
            {
                Name = (string)suiteElement.Attribute("name") ?? "",
                Tests = ParseInt(suiteElement.Attribute("tests")),
                Failures = ParseInt(suiteElement.Attribute("failures")),
                Skipped = ParseInt(suiteElement.Attribute("skipped")),
                TimeSeconds = ParseDouble(suiteElement.Attribute("time"))
            };

            foreach (XElement property in suiteElement.Elements("properties").Elements("property"))
            {
                string name = (string)property.Attribute("name");
                string value = (string)property.Attribute("value");
                if (name == "architecture")
                {
                    suite.Architecture = value;
                }
                else if (name == "timedOut" && value == "true")
                {
                    suite.TimedOut = true;
                }
            }

            foreach (XElement testCase in suiteElement.Elements("testcase"))
            {
                var c = new JUnitCase
                {
                    Name = (string)testCase.Attribute("name") ?? "",
                    ClassName = (string)testCase.Attribute("classname") ?? "",
                    TimeSeconds = ParseDouble(testCase.Attribute("time"))
                };
                XElement failure = testCase.Element("failure") ?? testCase.Element("error");
                XElement skipped = testCase.Element("skipped");
                if (failure != null)
                {
                    c.Status = TestOutcome.Failed;
                    c.Message = NonEmpty(failure.Value.Trim()) ?? (string)failure.Attribute("message") ?? "Test failed";
                }
                else if (skipped != null)
                {
                    c.Status = TestOutcome.Skipped;
                    c.Message = NonEmpty(skipped.Value.Trim()) ?? (string)skipped.Attribute("message");
                }
                suite.Cases.Add(c);
            }

            suite.SystemErr = suiteElement.Element("system-err")?.Value;
            suite.SystemOut = suiteElement.Element("system-out")?.Value;
            return suite;
        }

        private static string NonEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;

        private static int ParseInt(XAttribute a) =>
            int.TryParse((string)a, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;

        private static double ParseDouble(XAttribute a) =>
            double.TryParse((string)a, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
    }
}
