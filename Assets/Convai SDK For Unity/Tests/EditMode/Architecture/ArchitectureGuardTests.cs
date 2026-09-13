using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Architecture
{
    public sealed class ArchitectureGuardTests
    {
        private static readonly Dictionary<string, string> ArchitecturePrefixes = new(StringComparer.Ordinal)
        {
            ["Domain"] = "Convai.Domain",
            ["Application"] = "Convai.Application",
            ["Shared"] = "Convai.Shared",
            ["Infrastructure"] = "Convai.Infrastructure",
            ["Runtime"] = "Convai.Runtime",
            ["Modules"] = "Convai.Modules",
            ["Editor"] = "Convai.Editor"
        };

        private static readonly Regex MetaGuidPattern = new(
            @"^guid:\s*([0-9a-f]{32})\s*$",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);

        private static readonly Regex ReferenceGuidPattern = new(
            @"guid:\s*([0-9a-f]{32})",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);

        private static readonly string[] SerializedAssetExtensions =
        {
            ".unity", ".prefab", ".asset", ".mat", ".controller", ".anim", ".overrideController"
        };

        private static string PackageRoot => Path.GetFullPath(Path.Combine(
            UnityEngine.Application.dataPath,
            "..",
            "Packages",
            "com.convai.convai-sdk-for-unity"));

        private static string SdkRoot => Path.Combine(PackageRoot, "SDK");

        [Test]
        [Category("Architecture")]
        public void Namespaces_FollowOwnedArchitectureRoots()
        {
            var violations = new List<string>();
            var namespacePattern = new Regex(@"^\s*namespace\s+([A-Za-z_][A-Za-z0-9_.]*)", RegexOptions.Multiline);

            foreach (string filePath in Directory.EnumerateFiles(SdkRoot, "*.cs", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(filePath) == "AssemblyInfo.cs") continue;

                string relativePath = Path.GetRelativePath(SdkRoot, filePath).Replace('\\', '/');
                string[] segments = relativePath.Split('/');
                string[] expected = segments
                    .Take(segments.Length - 1)
                    .Where(ArchitecturePrefixes.ContainsKey)
                    .Select(segment => ArchitecturePrefixes[segment])
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (expected.Length == 0) continue;

                Match match = namespacePattern.Match(PackageFiles.ReadAllText(filePath));
                if (!match.Success) continue;

                string declared = match.Groups[1].Value;
                bool valid = expected.Any(prefix => declared == prefix || declared.StartsWith(prefix + "."));
                if (!valid && segments[0] == "Editor")
                    valid = declared == "Convai.Editor" || declared.Contains(".Editor.") || declared.EndsWith(".Editor");
                if (!valid) violations.Add($"{relativePath}: {declared}");
            }

            Assert.That(violations, Is.Empty);
        }

        [Test]
        [Category("Architecture")]
        public void DomainAssembly_DoesNotReferenceOuterLayers()
        {
            Assembly domain = FindOrLoadAssembly("Convai.Domain");
            Assert.That(domain, Is.Not.Null);

            string[] forbidden =
            {
                "Convai.Application", "Convai.Infrastructure", "Convai.Runtime",
                "Convai.Modules.Vision", "Convai.Modules.Narrative", "Convai.Editor"
            };
            HashSet<string> references = domain.GetReferencedAssemblies()
                .Select(assembly => assembly.Name)
                .ToHashSet(StringComparer.Ordinal);

            Assert.That(forbidden.Where(references.Contains), Is.Empty);
        }

        [Test]
        [Category("Architecture")]
        public void Infrastructure_DoesNotRedeclareCanonicalSessionErrorCodes()
        {
            string networkingRoot = Path.Combine(SdkRoot, "Infrastructure", "Networking");
            var codePattern = new Regex(
                @"const\s+string\s+\w+\s*=\s*""([a-z][a-z0-9_]*\.[a-z0-9_.]+)""",
                RegexOptions.Multiline);
            var violations = Directory
                .EnumerateFiles(networkingRoot, "*.cs", SearchOption.AllDirectories)
                .SelectMany(path => codePattern.Matches(PackageFiles.ReadAllText(path))
                    .Select(match => $"{ToRelativePath(path)}: {match.Groups[1].Value}"))
                .ToArray();

            Assert.That(violations, Is.Empty);
        }

        [Test]
        [Category("Architecture")]
        public void InternalRuntimeSeams_RemainInternal()
        {
            string[] typeNames =
            {
                "Convai.Runtime.Networking.Media.IAudioTrackManager",
                "Convai.Infrastructure.Networking.IRemotePlayerRegistry",
                "Convai.Runtime.Networking.Media.AudioTrackManager",
                "Convai.Runtime.Vision.Transport.VideoTrackManager",
                "Convai.Runtime.Adapters.Networking.PlayerSessionAdapter",
                "Convai.Runtime.Adapters.Vision.VideoTrackUnpublisherAdapter",
                "Convai.Runtime.Adapters.Platform.ConvaiPermissionService"
            };

            Type[] types = typeNames.Select(FindType).ToArray();
            Assert.That(types.All(type => type != null), Is.True);
            Assert.That(types.Where(type => type != null && type.IsPublic), Is.Empty);
        }

        [Test]
        [Category("Architecture")]
        public void Samples_DoNotCrossReferenceEachOther()
        {
            string basicRoot = Path.Combine(PackageRoot, "Samples", "BasicSample");
            string lipSyncRoot = Path.Combine(PackageRoot, "Samples", "LipSyncSample");
            Dictionary<string, string> pathsByGuid = BuildGuidPathMap();
            var violations = new List<string>();

            CollectCrossSampleReferences(basicRoot, "Samples/LipSyncSample/", pathsByGuid, violations);
            CollectCrossSampleReferences(lipSyncRoot, "Samples/BasicSample/", pathsByGuid, violations);

            Assert.That(violations, Is.Empty);
        }

        [Test]
        [Category("Architecture")]
        public void AssetsSharedBySampleScenes_LiveOutsideIndividualSamples()
        {
            string basicScene = Path.Combine(PackageRoot, "Samples", "BasicSample", "Scenes", "Basic Sample.unity");
            string lipSyncScene = Path.Combine(PackageRoot, "Samples", "LipSyncSample", "Scenes", "LipSync Sample.unity");
            Dictionary<string, string> pathsByGuid = BuildGuidPathMap();

            string[] misplaced = GetResolvedAssetReferences(basicScene, pathsByGuid)
                .Intersect(GetResolvedAssetReferences(lipSyncScene, pathsByGuid), StringComparer.Ordinal)
                .Where(path => path.StartsWith("Samples/") && !path.StartsWith("SamplesShared/"))
                .ToArray();

            Assert.That(misplaced, Is.Empty);
        }

        [Test]
        [Category("Architecture")]
        public void SampleAssets_DoNotReferenceEditorContent()
        {
            string[] roots =
            {
                Path.Combine(PackageRoot, "SamplesShared"),
                Path.Combine(PackageRoot, "Samples", "BasicSample"),
                Path.Combine(PackageRoot, "Samples", "LipSyncSample")
            };
            Dictionary<string, string> pathsByGuid = BuildGuidPathMap();
            var violations = new List<string>();

            foreach (string root in roots)
            foreach (string asset in EnumerateSerializedAssetFiles(root))
            foreach (string reference in GetResolvedAssetReferences(asset, pathsByGuid))
                if (reference.StartsWith("SDK/Editor/") &&
                    !reference.StartsWith("SDK/Editor/Art/UI/Branding"))
                    violations.Add($"{ToRelativePath(asset)} -> {reference}");

            Assert.That(violations, Is.Empty);
        }

        [Test]
        [Category("Architecture")]
        public void NonSamplePackageContent_DoesNotOwnRenderPipelineDependencies()
        {
            string[] roots =
            {
                Path.Combine(PackageRoot, "SDK"), Path.Combine(PackageRoot, "Prefabs"),
                Path.Combine(PackageRoot, "Resources"), Path.Combine(PackageRoot, "Tests")
            };
            string[] extensions = { ".cs", ".asmdef", ".unity", ".prefab", ".asset", ".mat", ".controller" };
            string[] forbidden = { "Unity.RenderPipelines.", "com.unity.render-pipelines." };
            var violations = new List<string>();

            foreach (string root in roots)
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(file) == nameof(ArchitectureGuardTests) + ".cs" ||
                    !extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                    continue;
                string content = PackageFiles.ReadAllText(file);
                if (forbidden.Any(token => content.Contains(token, StringComparison.Ordinal)))
                    violations.Add(ToRelativePath(file));
            }

            Assert.That(violations, Is.Empty);
        }

        private static void CollectCrossSampleReferences(
            string root,
            string forbiddenPrefix,
            IReadOnlyDictionary<string, string> pathsByGuid,
            ICollection<string> violations)
        {
            foreach (string asset in EnumerateSerializedAssetFiles(root))
            foreach (string reference in GetResolvedAssetReferences(asset, pathsByGuid))
                if (reference.StartsWith(forbiddenPrefix))
                    violations.Add($"{ToRelativePath(asset)} -> {reference}");
        }

        private static Dictionary<string, string> BuildGuidPathMap() => Directory
            .EnumerateFiles(PackageRoot, "*.meta", SearchOption.AllDirectories)
            .Select(path => new { Path = path, Match = MetaGuidPattern.Match(PackageFiles.ReadAllText(path)) })
            .Where(entry => entry.Match.Success)
            .ToDictionary(
                entry => entry.Match.Groups[1].Value,
                entry => Path.ChangeExtension(entry.Path, null),
                StringComparer.Ordinal);

        private static IEnumerable<string> EnumerateSerializedAssetFiles(string root) => Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => SerializedAssetExtensions.Contains(
                Path.GetExtension(path),
                StringComparer.OrdinalIgnoreCase));

        private static IEnumerable<string> GetResolvedAssetReferences(
            string asset,
            IReadOnlyDictionary<string, string> pathsByGuid) => ReferenceGuidPattern
            .Matches(PackageFiles.ReadAllText(asset))
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .Select(guid => pathsByGuid.TryGetValue(guid, out string path) ? ToRelativePath(path) : null)
            .Where(path => !string.IsNullOrEmpty(path));

        private static string ToRelativePath(string path) =>
            Path.GetRelativePath(PackageRoot, path).Replace('\\', '/');

        private static Assembly FindOrLoadAssembly(string name)
        {
            Assembly loaded = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly => assembly.GetName().Name == name);
            if (loaded != null) return loaded;

            try
            {
                return Assembly.Load(name);
            }
            catch
            {
                return null;
            }
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType(fullName, false);
                if (type != null) return type;
            }

            return null;
        }
    }
}
