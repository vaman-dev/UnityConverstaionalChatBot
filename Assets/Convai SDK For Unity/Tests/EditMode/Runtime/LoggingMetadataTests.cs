using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Convai.Domain.Logging;
using Convai.Runtime;
using Convai.Runtime.Logging;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Runtime
{
    [TestFixture]
    public class LoggingMetadataTests
    {
        [SetUp]
        public void SetUp()
        {
            _sink = new TestLogSink();
            _settings = ConvaiSettings.Instance;
            Assert.IsNotNull(_settings, "ConvaiSettings instance must exist for logging metadata tests.");

            _originalGlobalLevel = _settings.GlobalLogLevel;
            _originalCategoryOverrides = CloneOverrides(_settings.CategoryOverrides);
            _originalIncludeStackTraces = _settings.IncludeStackTraces;
            _originalColoredOutput = _settings.ColoredOutput;

            ConvaiLogger.ClearSinks();
            ConvaiLogger.Initialize();
            ConvaiLogger.RegisterSink(_sink);
            LoggingConfig.InvalidateCache();
        }

        [TearDown]
        public void TearDown()
        {
            if (_settings != null)
            {
                _settings.SetGlobalLogLevel(_originalGlobalLevel);
                _settings.SetCategoryOverrides(CloneOverrides(_originalCategoryOverrides));
                SetPrivateField(_settings, "_includeStackTraces", _originalIncludeStackTraces);
                SetPrivateField(_settings, "_coloredOutput", _originalColoredOutput);
                LoggingConfig.InvalidateCache();
            }

            ConvaiLogger.ClearSinks();
            _sink?.Dispose();
        }

        private TestLogSink _sink;
        private ConvaiSettings _settings;
        private LogLevel _originalGlobalLevel;
        private LogLevelOverride[] _originalCategoryOverrides;
        private bool _originalIncludeStackTraces;
        private bool _originalColoredOutput;

        [Test]
        public void ConvaiLogger_InfoWithLipSyncCategory_FormatsLipSyncCategoryName()
        {
            const string message = "Lip sync metadata test";
            const string taggedMessage = "[LoggingMetadataTests] Lip sync metadata test";
            EnableLogging(LogLevel.Info);

            ConvaiLogger.Info(message, LogCategory.LipSync);

            Assert.That(_sink.Entries.Count, Is.GreaterThanOrEqualTo(1));

            LogEntry entry = _sink.Entries.Find(candidate => candidate.Message == taggedMessage);
            Assert.That(entry.Category, Is.EqualTo(LogCategory.LipSync));

            var consoleSink = new UnityConsoleSink();
            MethodInfo formatMethod = typeof(UnityConsoleSink).GetMethod(
                "FormatLogEntry",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.IsNotNull(formatMethod, "Expected UnityConsoleSink.FormatLogEntry to exist.");

            string formatted = (string)formatMethod.Invoke(consoleSink, new object[] { entry });
            Assert.That(formatted, Does.Contain("[LipSync]"));
        }

        [Test]
        public void TaggedLogger_AllOverloads_PrefixOnceAndPreserveMetadata()
        {
            var inner = new RecordingLogger();
            ILogger logger = inner.WithTag("Probe");
            var context = new Dictionary<string, object> { ["request"] = "abc" };
            var exception = new InvalidOperationException("boom");

            logger.Log(LogLevel.Info, "message", LogCategory.Transport);
            logger.Log(LogLevel.Info, "message", context, LogCategory.Transport);
            logger.Debug("message", LogCategory.Transport);
            logger.Debug("message", context, LogCategory.Transport);
            logger.Info("message", LogCategory.Transport);
            logger.Info("message", context, LogCategory.Transport);
            logger.Warning("message", LogCategory.Transport);
            logger.Warning("message", context, LogCategory.Transport);
            logger.Error("message", LogCategory.Transport);
            logger.Error("message", context, LogCategory.Transport);
            logger.Error(exception, "message", LogCategory.Transport);
            logger.Error(exception, "message", context, LogCategory.Transport);

            Assert.That(inner.Invocations, Has.Count.EqualTo(12));
            Assert.That(inner.Invocations.Select(invocation => invocation.Message),
                Is.All.EqualTo("[Probe] message"));
            Assert.That(inner.Invocations.Select(invocation => invocation.Category),
                Is.All.EqualTo(LogCategory.Transport));
            CollectionAssert.AreEqual(new[]
            {
                "Log", "LogWithContext", "Debug", "DebugWithContext", "Info", "InfoWithContext",
                "Warning", "WarningWithContext", "Error", "ErrorWithContext", "ErrorException",
                "ErrorExceptionWithContext"
            }, inner.Invocations.Select(invocation => invocation.Operation).ToArray());
            Assert.That(inner.Invocations.Take(2).Select(invocation => invocation.Level),
                Is.All.EqualTo(LogLevel.Info));
            Invocation[] contextInvocations = inner.Invocations.Where(invocation => invocation.Context != null)
                .ToArray();
            Assert.That(contextInvocations, Has.Length.EqualTo(6));
            Assert.That(contextInvocations.All(invocation => ReferenceEquals(invocation.Context, context)), Is.True);
            Invocation[] exceptionInvocations = inner.Invocations
                .Where(invocation => invocation.Exception != null)
                .ToArray();
            Assert.That(exceptionInvocations, Has.Length.EqualTo(2));
            Assert.That(exceptionInvocations.All(invocation => ReferenceEquals(invocation.Exception, exception)),
                Is.True);

            inner.EnabledResult = false;
            Assert.That(logger.IsEnabled(LogLevel.Warning, LogCategory.Audio), Is.False);
            Assert.That(inner.LastEnabledLevel, Is.EqualTo(LogLevel.Warning));
            Assert.That(inner.LastEnabledCategory, Is.EqualTo(LogCategory.Audio));
        }

        [Test]
        public void WithTag_NullLoggerOrTag_IsSafe()
        {
            ILogger nullLogger = null;
            var logger = new RecordingLogger();

            Assert.That(nullLogger.WithTag("Probe"), Is.Null);
            Assert.That(logger.WithTag(null), Is.SameAs(logger));
            Assert.That(logger.WithTag(string.Empty), Is.SameAs(logger));
            Assert.That(logger.WithTag("  "), Is.SameAs(logger));
        }

        [Test]
        public void WithTag_RetaggedLogger_UsesOnlyChildTag()
        {
            var inner = new RecordingLogger();
            ILogger logger = inner.WithTag("Parent").WithTag("Child");

            logger.Info("message");

            Assert.That(inner.Invocations, Has.Count.EqualTo(1));
            Assert.That(inner.Invocations[0].Message, Is.EqualTo("[Child] message"));
        }

        [Test]
        public void TaggedLogger_WrappingConvaiLogger_DoesNotAddConvaiLoggerTag()
        {
            EnableLogging(LogLevel.Info);
            ILogger logger = ((ILogger)new ConvaiLogger()).WithTag("Consumer");

            logger.Info("message", LogCategory.SDK);

            Assert.That(_sink.Entries.Exists(entry => entry.Message == "[Consumer] message"), Is.True);
            Assert.That(_sink.Entries.Exists(entry => entry.Message.Contains("[ConvaiLogger] [Consumer]")), Is.False);
        }

        [Test]
        public void ConvaiLogger_ExplicitCallerPath_UsesPartialFilenameWithoutLeakingDirectories()
        {
            EnableLogging(LogLevel.Info);

            ConvaiLogger.Info("unix", LogCategory.SDK,
                "/Users/example/private/ConvaiRoomManager.Audio.cs");
            ConvaiLogger.Info("windows", LogCategory.SDK,
                @"C:\secret\workspace\ConvaiRoomManager.TurnTaking.cs");

            Assert.That(_sink.Entries.Exists(entry => entry.Message == "[ConvaiRoomManager] unix"), Is.True);
            Assert.That(_sink.Entries.Exists(entry => entry.Message == "[ConvaiRoomManager] windows"), Is.True);
            Assert.That(_sink.Entries.All(entry => !entry.Message.Contains("/Users/example/private") &&
                                                   !entry.Message.Contains(@"C:\secret\workspace")), Is.True);
        }

        [Test]
        public void ConvaiLogger_EmptyCallerPath_LeavesMessageUnchanged()
        {
            EnableLogging(LogLevel.Info);

            ConvaiLogger.Info("message", LogCategory.SDK, string.Empty);

            Assert.That(_sink.Entries.Exists(entry => entry.Message == "message"), Is.True);
        }

        [Test]
        public void ConvaiLogger_StaticDebugOverloads_RetainConditionalAttributes()
        {
            string[] expectedSymbols = { "UNITY_EDITOR", "DEVELOPMENT_BUILD", "CONVAI_DEBUG_LOGGING" };
            MethodInfo[] debugMethods = typeof(ConvaiLogger)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(method => method.Name == nameof(ConvaiLogger.Debug) ||
                                 method.Name == nameof(ConvaiLogger.DebugWithContext))
                .ToArray();

            Assert.That(debugMethods, Is.Not.Empty);
            foreach (MethodInfo method in debugMethods)
            {
                string[] actualSymbols = method
                    .GetCustomAttributes(typeof(ConditionalAttribute), false)
                    .Cast<ConditionalAttribute>()
                    .Select(attribute => attribute.ConditionString)
                    .ToArray();

                CollectionAssert.AreEquivalent(expectedSymbols, actualSymbols, method.ToString());
            }
        }

        [Test]
        public void ConvaiLogger_NonGenericStaticMessageOverloads_HaveTrailingCallerFilePath()
        {
            string[] methodNames =
            {
                nameof(ConvaiLogger.Info), nameof(ConvaiLogger.Debug), nameof(ConvaiLogger.Warning),
                nameof(ConvaiLogger.Error), nameof(ConvaiLogger.Exception), nameof(ConvaiLogger.InfoWithContext),
                nameof(ConvaiLogger.DebugWithContext), nameof(ConvaiLogger.WarningWithContext),
                nameof(ConvaiLogger.ErrorWithContext)
            };
            MethodInfo[] methods = typeof(ConvaiLogger)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(method => !method.IsGenericMethod && methodNames.Contains(method.Name))
                .ToArray();

            Assert.That(methods, Has.Length.EqualTo(14));
            foreach (MethodInfo method in methods)
            {
                ParameterInfo callerPath = method.GetParameters().Last();
                Assert.That(callerPath.ParameterType, Is.EqualTo(typeof(string)), method.ToString());
                Assert.That(callerPath.DefaultValue, Is.EqualTo(string.Empty), method.ToString());
                Assert.That(callerPath.IsDefined(typeof(CallerFilePathAttribute), false), Is.True,
                    method.ToString());
            }
        }

        [Test]
        public void ConvaiLogger_DisabledObjectLog_DoesNotFormatMessage()
        {
            EnableLogging(LogLevel.Error);

            Assert.DoesNotThrow(() => ConvaiLogger.Info(new ThrowOnToString(), LogCategory.SDK));
        }

        [Test]
        public void LoggingConfig_IsEnabled_RespectsLipSyncOverride()
        {
            _settings.SetGlobalLogLevel(LogLevel.Info);
            _settings.SetCategoryOverrides(new[] { new LogLevelOverride(LogCategory.LipSync, LogLevel.Error) });
            LoggingConfig.InvalidateCache();

            Assert.That(LoggingConfig.IsEnabled(LogLevel.Error, LogCategory.LipSync), Is.True);
            Assert.That(LoggingConfig.IsEnabled(LogLevel.Info, LogCategory.LipSync), Is.False);
            Assert.That(LoggingConfig.IsEnabled(LogLevel.Debug, LogCategory.LipSync), Is.False);
            Assert.That(LoggingConfig.IsEnabled(LogLevel.Info, LogCategory.SDK), Is.True);
        }

        /// <summary>
        ///     The same assertion as the LipSync case above, made for every category there is.
        ///     Written because the single-category version passed while the setting was being
        ///     ignored: the lookup table was sized from one named member rather than from the list,
        ///     so every category declared after it was silently out of range — reported as
        ///     configured by <see cref="ConvaiSettings.GetLogLevel" /> and ignored by the thing that
        ///     actually gates logging. Naming one category cannot catch that; enumerating them can,
        ///     including the ones added after this test was written.
        /// </summary>
        [Test]
        public void LoggingConfig_EveryCategory_RespectsItsOwnOverride()
        {
            foreach (LogCategory category in Enum.GetValues(typeof(LogCategory)).Cast<LogCategory>())
            {
                _settings.SetGlobalLogLevel(LogLevel.Info);
                _settings.SetCategoryOverrides(new[] { new LogLevelOverride(category, LogLevel.Error) });
                LoggingConfig.InvalidateCache();

                Assert.That(LoggingConfig.IsEnabled(LogLevel.Error, category), Is.True,
                    $"{category}: an Error override must still let errors through.");
                Assert.That(LoggingConfig.IsEnabled(LogLevel.Info, category), Is.False,
                    $"{category}: the override to Error is being ignored — this category is outside " +
                    "LoggingConfig's category table, so its setting does nothing.");
            }
        }

        /// <summary>
        ///     The other half of the same defect: a category outside the table never received the
        ///     global level either, so it sat at a hardcoded default no setting could move.
        /// </summary>
        [Test]
        public void LoggingConfig_EveryCategory_ReceivesTheGlobalLevel()
        {
            _settings.SetGlobalLogLevel(LogLevel.Error);
            _settings.SetCategoryOverrides(Array.Empty<LogLevelOverride>());
            LoggingConfig.InvalidateCache();

            foreach (LogCategory category in Enum.GetValues(typeof(LogCategory)).Cast<LogCategory>())
                Assert.That(LoggingConfig.IsEnabled(LogLevel.Info, category), Is.False,
                    $"{category}: the global level of Error is not reaching this category.");
        }

        [Test]
        public void UnityConsoleSink_FormatLogEntry_RespectsColoredOutputSetting()
        {
            SetPrivateField(_settings, "_coloredOutput", false);
            LoggingConfig.InvalidateCache();

            var entry = LogEntry.Info(LogCategory.SDK, "Colored output test");
            var consoleSink = new UnityConsoleSink();
            MethodInfo formatMethod = typeof(UnityConsoleSink).GetMethod(
                "FormatLogEntry",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.IsNotNull(formatMethod, "Expected UnityConsoleSink.FormatLogEntry to exist.");

            string formatted = (string)formatMethod.Invoke(consoleSink, new object[] { entry });

            Assert.That(formatted, Does.Not.Contain("<color="));
        }

        [Test]
        public void UnityConsoleSink_FormatLogEntry_RespectsIncludeStackTracesSetting()
        {
            Exception exception = CreateExceptionWithStackTrace();

            SetPrivateField(_settings, "_includeStackTraces", false);
            LoggingConfig.InvalidateCache();

            LogEntry entry = LogEntry.CreateWithException(LogLevel.Error, LogCategory.SDK, "Stack trace test",
                exception);
            var consoleSink = new UnityConsoleSink();
            MethodInfo formatMethod = typeof(UnityConsoleSink).GetMethod(
                "FormatLogEntry",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.IsNotNull(formatMethod, "Expected UnityConsoleSink.FormatLogEntry to exist.");

            string formatted = (string)formatMethod.Invoke(consoleSink, new object[] { entry });

            Assert.That(formatted, Does.Contain(exception.GetType().Name));
            Assert.That(formatted, Does.Contain(exception.Message));
            Assert.That(formatted, Does.Not.Contain(exception.StackTrace));
        }

        private static LogLevelOverride[] CloneOverrides(LogLevelOverride[] source)
        {
            if (source == null || source.Length == 0) return Array.Empty<LogLevelOverride>();

            var copy = new LogLevelOverride[source.Length];
            Array.Copy(source, copy, source.Length);
            return copy;
        }

        private static Exception CreateExceptionWithStackTrace()
        {
            try
            {
                ThrowForStackTrace();
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }

        private static void ThrowForStackTrace() => throw new InvalidOperationException("Stack trace test");

        private void EnableLogging(LogLevel level)
        {
            _settings.SetGlobalLogLevel(level);
            _settings.SetCategoryOverrides(Array.Empty<LogLevelOverride>());
            LoggingConfig.InvalidateCache();
        }

        private static void SetPrivateField(ConvaiSettings settings, string fieldName, object value)
        {
            FieldInfo field =
                typeof(ConvaiSettings).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, $"Expected ConvaiSettings.{fieldName} to exist.");
            field.SetValue(settings, value);
        }

        private sealed class ThrowOnToString
        {
            public override string ToString() => throw new InvalidOperationException("ToString must not run");
        }

        private sealed class RecordingLogger : ILogger
        {
            internal readonly List<Invocation> Invocations = new();
            internal bool EnabledResult = true;
            internal LogLevel LastEnabledLevel;
            internal LogCategory LastEnabledCategory;

            public void Log(LogLevel level, string message, LogCategory category = LogCategory.SDK) =>
                Record("Log", message, category, level: level);

            public void Log(LogLevel level, string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK) =>
                Record("LogWithContext", message, category, context, level: level);

            public void Debug(string message, LogCategory category = LogCategory.SDK) =>
                Record("Debug", message, category);

            public void Debug(string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK) => Record("DebugWithContext", message, category, context);

            public void Info(string message, LogCategory category = LogCategory.SDK) =>
                Record("Info", message, category);

            public void Info(string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK) => Record("InfoWithContext", message, category, context);

            public void Warning(string message, LogCategory category = LogCategory.SDK) =>
                Record("Warning", message, category);

            public void Warning(string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK) => Record("WarningWithContext", message, category, context);

            public void Error(string message, LogCategory category = LogCategory.SDK) =>
                Record("Error", message, category);

            public void Error(string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK) => Record("ErrorWithContext", message, category, context);

            public void Error(Exception exception, string message = null,
                LogCategory category = LogCategory.SDK) =>
                Record("ErrorException", message, category, exception: exception);

            public void Error(Exception exception, string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK) =>
                Record("ErrorExceptionWithContext", message, category, context, exception);

            public bool IsEnabled(LogLevel level, LogCategory category)
            {
                LastEnabledLevel = level;
                LastEnabledCategory = category;
                return EnabledResult;
            }

            private void Record(
                string operation,
                string message,
                LogCategory category,
                IReadOnlyDictionary<string, object> context = null,
                Exception exception = null,
                LogLevel? level = null) =>
                Invocations.Add(new Invocation(operation, message, category, context, exception, level));
        }

        private sealed class Invocation
        {
            internal Invocation(
                string operation,
                string message,
                LogCategory category,
                IReadOnlyDictionary<string, object> context,
                Exception exception,
                LogLevel? level)
            {
                Operation = operation;
                Message = message;
                Category = category;
                Context = context;
                Exception = exception;
                Level = level;
            }

            internal string Operation { get; }
            internal string Message { get; }
            internal LogCategory Category { get; }
            internal IReadOnlyDictionary<string, object> Context { get; }
            internal Exception Exception { get; }
            internal LogLevel? Level { get; }
        }
    }
}
