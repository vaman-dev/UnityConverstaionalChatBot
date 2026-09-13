using UnityEngine;
using UnityEngine.UI;

namespace Kirit.Vision
{
    /// <summary>
    /// Displays the texture owned by a WebcamCaptureService in a RawImage.
    /// </summary>
    public sealed class CameraPreview : MonoBehaviour
    {
        [SerializeField]
        private WebcamCaptureService captureService;

        [SerializeField]
        private RawImage previewImage;

        private void OnEnable()
        {
            if (captureService != null)
            {
                captureService.CameraStarted += HandleCameraStarted;
                captureService.CameraStopped += HandleCameraStopped;
            }
        }

        private void OnDisable()
        {
            if (captureService != null)
            {
                captureService.CameraStarted -= HandleCameraStarted;
                captureService.CameraStopped -= HandleCameraStopped;
            }
        }

        private void Start()
        {
            if (captureService != null && captureService.Texture != null)
            {
                HandleCameraStarted(captureService.Texture);
            }
        }

        private void HandleCameraStarted(WebCamTexture texture)
        {
            if (previewImage != null)
            {
                previewImage.texture = texture;
            }
        }

        private void HandleCameraStopped()
        {
            if (previewImage != null)
            {
                previewImage.texture = null;
            }
        }
    }
}
