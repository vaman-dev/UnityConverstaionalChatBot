using System;
using UnityEngine;

namespace Kirit.Vision
{
    /// <summary>
    /// Owns the single physical webcam used by Kirit's vision pipeline.
    /// </summary>
    public sealed class WebcamCaptureService : MonoBehaviour
    {
        [Header("Camera Selection")]
        [SerializeField]
        private string preferredDeviceName = string.Empty;

        [Header("Requested Resolution")]
        [SerializeField]
        private int requestedWidth = 1280;

        [SerializeField]
        private int requestedHeight = 720;

        [SerializeField]
        private int requestedFPS = 30;

        private WebCamTexture webcamTexture;

        public WebCamTexture Texture => webcamTexture;

        public bool IsRunning => webcamTexture != null && webcamTexture.isPlaying;

        public event Action<WebCamTexture> CameraStarted;
        public event Action CameraStopped;

        private void Start()
        {
            PrintAvailableCameras();
            StartCamera();
        }

        public void StartCamera()
        {
            if (IsRunning)
            {
                return;
            }

            string deviceName = FindCameraDevice();
            if (string.IsNullOrEmpty(deviceName))
            {
                Debug.LogError("[Kirit Vision] No webcam device found.");
                return;
            }

            webcamTexture = new WebCamTexture(
                deviceName,
                requestedWidth,
                requestedHeight,
                requestedFPS);

            webcamTexture.Play();
            Debug.Log($"[Kirit Vision] Starting camera: {deviceName}");
            CameraStarted?.Invoke(webcamTexture);
        }

        public void StopCamera()
        {
            if (webcamTexture == null)
            {
                return;
            }

            if (webcamTexture.isPlaying)
            {
                webcamTexture.Stop();
            }

            CameraStopped?.Invoke();
            Destroy(webcamTexture);
            webcamTexture = null;
        }

        private string FindCameraDevice()
        {
            WebCamDevice[] devices = WebCamTexture.devices;
            if (devices == null || devices.Length == 0)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(preferredDeviceName))
            {
                foreach (WebCamDevice device in devices)
                {
                    if (device.name.Equals(preferredDeviceName, StringComparison.OrdinalIgnoreCase))
                    {
                        return device.name;
                    }
                }

                Debug.LogWarning(
                    $"[Kirit Vision] Preferred camera '{preferredDeviceName}' not found. " +
                    "Using the first available camera.");
            }

            return devices[0].name;
        }

        private void PrintAvailableCameras()
        {
            WebCamDevice[] devices = WebCamTexture.devices;
            Debug.Log($"[Kirit Vision] Cameras detected: {devices.Length}");

            for (int i = 0; i < devices.Length; i++)
            {
                Debug.Log($"[Kirit Vision] Camera {i}: {devices[i].name}");
            }
        }

        private void OnDestroy()
        {
            StopCamera();
        }
    }
}
