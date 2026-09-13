#if UNITY_EDITOR
using System.IO;
using System.Reflection;
using Convai.Editor.Compatibility;
using Convai.Shared.Compatibility;
using Convai.Tests.EditMode.Fixtures;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Runtime
{
    /// <summary>
    ///     Pins down exactly how much the editor half of the identity seam adds, and how little.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         An earlier review reported the runtime seam as generally narrowed — that resolving
    ///         through <c>Resources</c> rather than <c>EditorUtility</c> loses assets Unity has
    ///         unloaded. <see cref="UnloadedAsset_ResolvesThroughNeitherSeam" /> measures that claim
    ///         and finds it false on the supported floor: for a current id the two behave the same.
    ///     </para>
    ///     <para>
    ///         What is real is the legacy path, which
    ///         <see cref="LegacyInstanceId_ResolvesOnlyThroughTheEditorSeam" /> covers. Keep both: the
    ///         first is what stops the general version of the finding being raised again, and it
    ///         should start failing the day Unity makes the general claim true.
    ///     </para>
    /// </remarks>
    [TestFixture]
    public class ConvaiEditorObjectIdTests
    {
        private static readonly string _tempFolder =
            $"{TestAssetSandbox.Root}/{nameof(ConvaiEditorObjectIdTests)}";

        private string _assetPath;
        private ScriptableObject _probe;

        [SetUp]
        public void SetUp()
        {
            TestAssetSandbox.Ensure(nameof(ConvaiEditorObjectIdTests));
            AssetDatabase.Refresh();

            _assetPath = _tempFolder + "/Probe.asset";
            _probe = ScriptableObject.CreateInstance<ScriptableObject>();
            AssetDatabase.CreateAsset(_probe, _assetPath);
            AssetDatabase.SaveAssets();
        }

        [TearDown]
        public void TearDown()
        {
            TestAssetSandbox.Clear(nameof(ConvaiEditorObjectIdTests));
            AssetDatabase.Refresh();
        }

        [Test]
        public void LoadedAsset_ResolvesThroughBothSeams()
        {
            long id = ConvaiObjectId.Of(_probe);
            Assert.AreNotEqual(0L, id, "The probe asset must produce a non-zero id.");

            Assert.IsTrue(
                ConvaiObjectId.TryResolve(id, out Object viaRuntime),
                "A loaded asset must resolve through the runtime seam.");
            Assert.IsTrue(
                ConvaiEditorObjectId.TryResolve(id, out Object viaEditor),
                "A loaded asset must resolve through the editor seam.");
            Assert.AreSame(viaRuntime, viaEditor, "Both seams must return the same object.");
        }

        /// <summary>
        ///     Records the measured behaviour that makes the editor seam narrow: an unloaded asset is
        ///     lost to both halves, so the editor half buys nothing for a current id.
        /// </summary>
        /// <remarks>
        ///     If this ever fails, <see cref="EditorUtility" /> has gained the ability to load an
        ///     unloaded asset from an id and the editor seam has become generally useful — which is
        ///     worth knowing, and worth rewriting its documentation for.
        /// </remarks>
        [Test]
        public void UnloadedAsset_ResolvesThroughNeitherSeam()
        {
            long id = ConvaiObjectId.Of(_probe);
            Resources.UnloadAsset(_probe);
            EditorUtility.UnloadUnusedAssetsImmediate();

            Assert.IsFalse(
                ConvaiObjectId.TryResolve(id, out Object _),
                "The runtime seam reaches loaded objects only.");
            Assert.IsFalse(
                ConvaiEditorObjectId.TryResolve(id, out Object _),
                "Measured on 6000.4: EditorUtility.EntityIdToObject does not load an unloaded asset "
                + "either, so the editor seam adds nothing for a current id. If this now passes, the "
                + "seam is more useful than its documentation claims — update the documentation.");
        }

        /// <summary>
        ///     The one case the editor seam exists for: a historical <c>int</c> instance ID, which an
        ///     assistant can still deliver by replaying an id it saved in an earlier session.
        /// </summary>
        [Test]
        public void LegacyInstanceId_ResolvesOnlyThroughTheEditorSeam()
        {
            long legacyId = LegacyInstanceId(_probe);
            Assert.AreNotEqual(0L, legacyId, "The probe asset must produce a non-zero legacy id.");

            Assert.IsTrue(
                ConvaiEditorObjectId.TryResolve(legacyId, out Object viaEditor),
                "A historical int instance ID must still resolve — ConvaiObjectId documents that "
                + "retry as a promise, and ConvaiMcpEntityRef.Resolve takes its id from outside.");
            Assert.AreSame(_probe, viaEditor, "The legacy id must resolve to the same asset.");
        }

        /// <summary>
        ///     Produces a genuine historical instance ID.
        /// </summary>
        /// <remarks>
        ///     <b>This is a deliberately exotic fixture, not a normal call path.</b>
        ///     Unity's instance-ID accessor is obsolete-as-error on 6000.5, so no supported call
        ///     syntax still produces one of these — it is fetched reflectively, the same way
        ///     <c>ConvaiMcpIdRoundTripTests</c> does. A reader should not take its presence here as a
        ///     sign that package code may produce ids this way.
        /// </remarks>
        private static long LegacyInstanceId(Object value) =>
            (int)typeof(Object)
                .GetMethod("GetInstanceID", BindingFlags.Public | BindingFlags.Instance)
                .Invoke(value, null);
    }
}
#endif
