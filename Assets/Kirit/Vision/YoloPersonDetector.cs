using System;
using Unity.InferenceEngine;
using UnityEngine;

namespace Kirit.Vision
{
    public sealed class YoloPersonDetector : MonoBehaviour
    {
        [Header("References")]
        [SerializeField]
        private WebcamCaptureService captureService;

        [SerializeField]
        private ModelAsset modelAsset;

        [SerializeField]
        private TextAsset classesAsset;

        [Header("Detection")]
        [SerializeField, Range(0f, 1f)]
        private float confidenceThreshold = 0.5f;

        [SerializeField, Range(0f, 1f)]
        private float iouThreshold = 0.5f;

        [SerializeField, Min(1f)]
        private float inferenceFPS = 8f;

        [Header("Debug")]
        [SerializeField]
        private bool logDetections = true;

        private bool previousLoggedPresence;
        private int previousLoggedCount = -1;

        private const int InputWidth = 640;
        private const int InputHeight = 640;

        // COCO class index:
        // 0 = person
        private const int PersonClassId = 0;

        private Worker worker;

        private RenderTexture inferenceTexture;

        private Tensor<float> centersToCorners;

        private float nextInferenceTime;

        public bool IsPersonPresent { get; private set; }

        public int PersonCount { get; private set; }

        public float HighestConfidence { get; private set; }

        public event Action<bool> PersonPresenceChanged;

        private void Start()
        {
            if (captureService == null)
            {
                Debug.LogError(
                    "[Kirit YOLO] WebcamCaptureService reference is missing.",
                    this
                );

                enabled = false;
                return;
            }

            if (modelAsset == null)
            {
                Debug.LogError(
                    "[Kirit YOLO] YOLO model asset is missing.",
                    this
                );

                enabled = false;
                return;
            }

            if (classesAsset == null)
            {
                Debug.LogError(
                    "[Kirit YOLO] classes.txt is missing.",
                    this
                );

                enabled = false;
                return;
            }

            CreateModel();

            inferenceTexture = new RenderTexture(
                InputWidth,
                InputHeight,
                0,
                RenderTextureFormat.ARGB32
            );

            inferenceTexture.Create();

            Debug.Log(
                "[Kirit YOLO] YOLO person detector initialized."
            );
        }

        private void CreateModel()
        {
            Model sourceModel = ModelLoader.Load(modelAsset);

            centersToCorners = new Tensor<float>(
                new TensorShape(4, 4),
                new float[]
                {
                    1f,     0f,      1f,     0f,
                    0f,     1f,      0f,     1f,
                   -0.5f,   0f,      0.5f,   0f,
                    0f,    -0.5f,    0f,     0.5f
                }
            );

            FunctionalGraph graph = new FunctionalGraph();

            FunctionalTensor[] inputs =
                graph.AddInputs(sourceModel);

            FunctionalTensor modelOutput =
                Functional.Forward(
                    sourceModel,
                    inputs
                )[0];

            // YOLO11 COCO:
            // (1, 84, 8400)
            //
            // 4 box values
            // +
            // 80 class scores

            FunctionalTensor boxCoords =
                modelOutput[0, 0..4, ..]
                    .Transpose(0, 1);

            FunctionalTensor allScores =
                modelOutput[0, 4.., ..];

            FunctionalTensor scores =
                Functional.ReduceMax(
                    allScores,
                    0
                );

            FunctionalTensor classIds =
                Functional.ArgMax(
                    allScores,
                    0
                );

            FunctionalTensor boxCorners =
                Functional.MatMul(
                    boxCoords,
                    Functional.Constant(
                        centersToCorners
                    )
                );

            FunctionalTensor indices =
                Functional.NMS(
                    boxCorners,
                    scores,
                    iouThreshold,
                    confidenceThreshold
                );

            FunctionalTensor selectedBoxes =
                Functional.IndexSelect(
                    boxCoords,
                    0,
                    indices
                );

            FunctionalTensor selectedClasses =
                Functional.IndexSelect(
                    classIds,
                    0,
                    indices
                );

            Model runtimeModel =
                graph.Compile(
                    selectedBoxes,
                    selectedClasses
                );

            worker = new Worker(
                runtimeModel,
                BackendType.GPUCompute
            );
        }

        private void Update()
        {
            if (Time.unscaledTime < nextInferenceTime)
                return;

            nextInferenceTime =
                Time.unscaledTime +
                (1f / inferenceFPS);

            RunDetection();
        }

        private void RunDetection()
        {
            WebCamTexture cameraTexture =
                captureService.Texture;

            if (cameraTexture == null)
                return;

            if (!cameraTexture.isPlaying)
                return;

            if (!cameraTexture.didUpdateThisFrame)
                return;

            // WebCamTexture often reports a tiny placeholder
            // size while the camera is still initializing.
            if (cameraTexture.width < 100 ||
                cameraTexture.height < 100)
            {
                return;
            }

            /*
             * First milestone:
             *
             * simply resize the webcam frame into
             * YOLO's 640x640 input.
             *
             * Later we'll replace this with proper
             * aspect-preserving letterboxing.
             */
            Graphics.Blit(
                cameraTexture,
                inferenceTexture
            );

            using Tensor<float> inputTensor =
                new Tensor<float>(
                    new TensorShape(
                        1,
                        3,
                        InputHeight,
                        InputWidth
                    )
                );

            TextureConverter.ToTensor(
                inferenceTexture,
                inputTensor
            );

            worker.Schedule(inputTensor);

            using Tensor<float> boxes =
                (worker.PeekOutput("output_0")
                    as Tensor<float>)
                    ?.ReadbackAndClone();

            using Tensor<int> classIds =
                (worker.PeekOutput("output_1")
                    as Tensor<int>)
                    ?.ReadbackAndClone();

            if (boxes == null ||
                classIds == null)
            {
                return;
            }

            int personCount = 0;

            /*
             * For phase 4A we only need to prove
             * that YOLO sees people.
             *
             * The graph output currently contains
             * selected boxes + class IDs.
             *
             * Confidence extraction will be added
             * cleanly in the next revision.
             */
            int detections =
                classIds.shape[0];

            for (int i = 0; i < detections; i++)
            {
                if (classIds[i] == PersonClassId)
                {
                    personCount++;
                }
            }

            bool personPresent =
                personCount > 0;

            if (personPresent != IsPersonPresent)
            {
                IsPersonPresent =
                    personPresent;

                PersonPresenceChanged?.Invoke(
                    IsPersonPresent
                );
            }

            PersonCount =
                personCount;

            if (logDetections &&
                (IsPersonPresent != previousLoggedPresence ||
                 PersonCount != previousLoggedCount))
            {
                Debug.Log(
                    $"[Kirit YOLO] " +
                    $"PersonPresent={IsPersonPresent}, " +
                    $"Count={PersonCount}"
                );

                previousLoggedPresence = IsPersonPresent;
                previousLoggedCount = PersonCount;
            }
        }

        private void OnDestroy()
        {
            worker?.Dispose();

            centersToCorners?.Dispose();

            if (inferenceTexture != null)
            {
                inferenceTexture.Release();

                Destroy(
                    inferenceTexture
                );
            }
        }
    }
}
