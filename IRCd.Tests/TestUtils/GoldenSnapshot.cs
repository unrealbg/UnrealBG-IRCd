namespace IRCd.Tests.TestUtils
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Text;

    using Xunit;

    public static class GoldenSnapshot
    {
        private const string UpdateEnvVar = "IRCD_UPDATE_GOLDENS";

        public static void AssertLinesMatch(string snapshotFilePath, string[] actualLines)
        {
            var normalizedActual = NormalizeLines(actualLines);

            Directory.CreateDirectory(Path.GetDirectoryName(snapshotFilePath)!);

            var update = string.Equals(Environment.GetEnvironmentVariable(UpdateEnvVar), "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Environment.GetEnvironmentVariable(UpdateEnvVar), "true", StringComparison.OrdinalIgnoreCase);

            if (!File.Exists(snapshotFilePath))
            {
                if (update)
                {
                    File.WriteAllText(snapshotFilePath, string.Join("\n", normalizedActual) + "\n", Encoding.UTF8);
                    return;
                }

                Assert.Fail($"Golden file missing: {snapshotFilePath}.\n" +
                            $"Run with {UpdateEnvVar}=1 to generate snapshots.");
            }

            var expectedText = File.ReadAllText(snapshotFilePath, Encoding.UTF8);
            var expectedLines = NormalizeTextToLines(expectedText);

            if (expectedLines.SequenceEqual(normalizedActual, StringComparer.Ordinal))
            {
                return;
            }

            if (update)
            {
                File.WriteAllText(snapshotFilePath, string.Join("\n", normalizedActual) + "\n", Encoding.UTF8);
                return;
            }

            var message = BuildMismatchMessage(snapshotFilePath, expectedLines, normalizedActual);
            Assert.Fail(message);
        }

        private static string[] NormalizeLines(string[] lines)
            => lines
                .Select(l => l.Replace("\r\n", "\n").Replace("\r", "\n"))
                .SelectMany(l => l.Split('\n'))
                .Select(l => l.TrimEnd())
                .ToArray();

        private static string[] NormalizeTextToLines(string text)
            => text.Replace("\r\n", "\n").Replace("\r", "\n")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.TrimEnd())
                .ToArray();

        private static string BuildMismatchMessage(string path, string[] expected, string[] actual)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Golden mismatch: {path}");
            sb.AppendLine("To accept changes, re-run tests with IRCD_UPDATE_GOLDENS=1");

            var max = Math.Max(expected.Length, actual.Length);
            for (var i = 0; i < max; i++)
            {
                var e = i < expected.Length ? expected[i] : "<missing>";
                var a = i < actual.Length ? actual[i] : "<missing>";
                if (!string.Equals(e, a, StringComparison.Ordinal))
                {
                    sb.AppendLine($"First diff at line {i + 1}:");
                    sb.AppendLine($"  expected: {e}");
                    sb.AppendLine($"  actual:   {a}");
                    break;
                }
            }

            return sb.ToString();
        }
    }
}
