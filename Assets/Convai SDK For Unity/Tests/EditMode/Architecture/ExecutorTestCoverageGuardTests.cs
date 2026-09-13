using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Architecture
{
    /// <summary>
    ///     Appearing in <c>ActionsPublicSurfaceGuardTests</c>' naming allowlist proves a type is
    ///     deliberate public API — it proves nothing about whether it works. Executors had shipped
    ///     with zero behavioural test coverage while still passing every existing guard, because no
    ///     guard ever checked for coverage itself. This test closes that gap: every shipped action
    ///     executor (<see cref="ActionExecutorArchitectureGuardTests.ShippedExecutorTypes" /> — the
    ///     same <c>Convai.Runtime</c>/<c>Convai.Modules.*</c> discovery the architecture guard uses,
    ///     so "shipped executor" means one consistent thing across both guards) must be named by at
    ///     least one test source file.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Detection is structural, not a hand-maintained list</b> — that was the original
    ///         problem — but it reads the test <em>sources</em>, not compiled metadata. The earlier
    ///         version of this guard inspected local-variable and field tables via reflection, on the
    ///         reasoning that every executor test holds its subject as
    ///         <c>ExecutorType executor = go.AddComponent&lt;ExecutorType&gt;();</c>. That reasoning
    ///         does not survive contact with the C# compiler: whether such a local produces a local
    ///         slot, gets hoisted into an async state machine field, or is folded onto the evaluation
    ///         stack entirely is a codegen decision. In practice ten executors with real, dedicated
    ///         fixtures — dedicated per-behavior fixtures,
    ///         across several modules — were
    ///         reported as uncovered, because their subjects live inside <c>async Task</c> tests
    ///         where the compiler emitted neither a surviving local nor a state-machine field of that
    ///         type. A guard whose verdict depends on compiler codegen cannot be trusted in either
    ///         direction, so the mechanism is gone.
    ///     </para>
    ///     <para>
    ///         A source scan is deterministic and matches what the guard actually wants to know: does
    ///         any test file mention this executor by name. It is deliberately generous — a mention
    ///         in a comment counts — because the failure this guards against is an executor nobody
    ///         thought about at all, and the cost of a false <em>pass</em> (a test that names a type
    ///         without exercising it) is far lower than the cost of a false failure, which is what
    ///         teaches people to ignore the guard.
    ///     </para>
    /// </remarks>
    public sealed class ExecutorTestCoverageGuardTests
    {
        /// <summary>
        ///     Files that name executors without testing them, and so must not count as coverage:
        ///     the public-surface allowlist and this guard's own sources.
        /// </summary>
        private static readonly string[] NonCoverageFileNames =
        {
            "ActionsPublicSurfaceGuardTests.cs",
            "ExecutorTestCoverageGuardTests.cs",
            "ActionExecutorArchitectureGuardTests.cs",
            "ActionsComponentTooltipGuardTests.cs"
        };

        private static string PackageRoot => Path.GetFullPath(Path.Combine(
            UnityEngine.Application.dataPath, "..", "Packages", "com.convai.convai-sdk-for-unity"));

        [Test]
        [Category("Architecture")]
        public void ShippedActionExecutors_AreReferencedByAtLeastOneTest()
        {
            List<Type> executors = ActionExecutorArchitectureGuardTests.ShippedExecutorTypes().ToList();
            Assert.Greater(executors.Count, 0, "Expected at least one shipped action executor type to be discovered.");

            string testsRoot = Path.Combine(PackageRoot, "Tests");
            Assert.That(Directory.Exists(testsRoot), Is.True, $"Expected a test source tree at '{testsRoot}'.");

            HashSet<string> identifiers = ReadTestSourceIdentifiers(testsRoot);
            Assert.That(identifiers, Is.Not.Empty, "Expected to read at least one test source file.");

            List<string> uncovered = executors
                .Where(type => !identifiers.Contains(type.Name))
                .Select(type => type.FullName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

            Assert.IsEmpty(uncovered,
                "The following shipped action executors are not named by any test source file, so nothing " +
                "exercises them. Appearing in ActionsPublicSurfaceGuardTests' naming allowlist does not count " +
                "toward this — that guard only protects the public API surface, not behaviour. Add a " +
                "behavioural EditMode test covering success, missing-target, cancellation and graceful " +
                "degradation in its own fixture, then this guard picks it " +
                "up automatically — there is no allowlist to edit:\n" + string.Join(Environment.NewLine, uncovered));
        }

        /// <summary>
        ///     Every identifier appearing in any test <c>.cs</c> file, minus the files that name
        ///     executors for bookkeeping rather than for testing them. Tokenising once and looking
        ///     each executor up in a set keeps this O(sources + executors) — scanning the whole
        ///     corpus once per executor would mean forty-odd passes over several megabytes.
        /// </summary>
        private static HashSet<string> ReadTestSourceIdentifiers(string testsRoot)
        {
            var identifiers = new HashSet<string>(StringComparer.Ordinal);
            var identifierPattern = new Regex(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);

            foreach (string file in Directory.EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories))
            {
                if (NonCoverageFileNames.Contains(Path.GetFileName(file), StringComparer.Ordinal))
                    continue;

                string text;
                try
                {
                    text = File.ReadAllText(file);
                }
                catch (IOException)
                {
                    // A file that cannot be read contributes nothing; it must not fail the guard.
                    continue;
                }

                foreach (Match match in identifierPattern.Matches(text))
                    identifiers.Add(match.Value);
            }

            return identifiers;
        }
    }
}
