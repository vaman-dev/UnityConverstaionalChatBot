using Convai.Modules.Gaze.Providers;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     The fallback camera-eligibility predicate used when resolving the "first enabled
    ///     camera" candidate: only active, enabled, Game-view cameras rendering to the screen
    ///     may be picked, never render-texture or utility cameras.
    /// </summary>
    public sealed class PlayerAnchorCameraResolveTests
    {
        private GameObject _cameraGo;
        private RenderTexture _renderTexture;

        [TearDown]
        public void TearDown()
        {
            if (_cameraGo != null)
                Object.DestroyImmediate(_cameraGo);
            _cameraGo = null;

            if (_renderTexture != null)
                _renderTexture.Release();
            Object.DestroyImmediate(_renderTexture);
            _renderTexture = null;
        }

        [Test]
        public void EnabledGameCamera_IsEligible()
        {
            _cameraGo = new GameObject("GameCamera");
            Camera camera = _cameraGo.AddComponent<Camera>();

            Assert.IsTrue(PlayerAnchorTargetProvider.IsEligibleViewCamera(camera),
                "An enabled Game-view camera with no render texture must be eligible.");
        }

        [Test]
        public void RenderTextureCamera_IsNotEligible()
        {
            _cameraGo = new GameObject("RenderTextureCamera");
            Camera camera = _cameraGo.AddComponent<Camera>();
            _renderTexture = new RenderTexture(16, 16, 0);
            camera.targetTexture = _renderTexture;

            Assert.IsFalse(PlayerAnchorTargetProvider.IsEligibleViewCamera(camera),
                "A camera rendering to a texture must never become the gaze target.");
        }

        [Test]
        public void DisabledCamera_IsNotEligible()
        {
            _cameraGo = new GameObject("DisabledCamera");
            Camera camera = _cameraGo.AddComponent<Camera>();
            camera.enabled = false;

            Assert.IsFalse(PlayerAnchorTargetProvider.IsEligibleViewCamera(camera),
                "A disabled camera component must not be eligible.");
        }

        [Test]
        public void ReflectionCamera_IsNotEligible()
        {
            _cameraGo = new GameObject("ReflectionCamera");
            Camera camera = _cameraGo.AddComponent<Camera>();
            camera.cameraType = CameraType.Reflection;

            Assert.IsFalse(PlayerAnchorTargetProvider.IsEligibleViewCamera(camera),
                "A utility (reflection) camera must never be picked by the automatic fallback.");
        }

        [Test]
        public void NullCamera_IsNotEligible()
        {
            Assert.IsFalse(PlayerAnchorTargetProvider.IsEligibleViewCamera(null),
                "A null camera reference must not be eligible.");
        }
    }
}
