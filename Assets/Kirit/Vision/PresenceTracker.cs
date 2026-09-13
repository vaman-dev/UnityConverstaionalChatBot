using System;
using UnityEngine;

namespace Kirit.Vision
{
    public sealed class PresenceTracker : MonoBehaviour
    {
        [Header("References")]
        [SerializeField]
        private YoloPersonDetector personDetector;

        [Header("Timing")]
        [SerializeField, Min(0f)]
        private float arrivalConfirmationSeconds = 1f;

        [SerializeField, Min(0f)]
        private float leaveConfirmationSeconds = 5f;

        [Header("Debug")]
        [SerializeField]
        private bool logStateChanges = true;

        private float visibleTimer;
        private float absentTimer;

        public bool VisitorPresent { get; private set; }

        public event Action VisitorArrived;
        public event Action VisitorLeft;

        private void Awake()
        {
            if (personDetector == null)
            {
                Debug.LogError(
                    "[Kirit Presence] YoloPersonDetector is not assigned.",
                    this
                );

                enabled = false;
            }
        }

        private void Update()
        {
            if (personDetector == null)
                return;

            if (personDetector.IsPersonPresent)
            {
                HandlePersonVisible();
            }
            else
            {
                HandlePersonAbsent();
            }
        }

        private void HandlePersonVisible()
        {
            absentTimer = 0f;

            if (VisitorPresent)
                return;

            visibleTimer += Time.deltaTime;

            if (visibleTimer < arrivalConfirmationSeconds)
                return;

            visibleTimer = 0f;
            VisitorPresent = true;

            if (logStateChanges)
            {
                Debug.Log(
                    "[Kirit Presence] VISITOR ARRIVED"
                );
            }

            VisitorArrived?.Invoke();
        }

        private void HandlePersonAbsent()
        {
            visibleTimer = 0f;

            if (!VisitorPresent)
                return;

            absentTimer += Time.deltaTime;

            if (absentTimer < leaveConfirmationSeconds)
                return;

            absentTimer = 0f;
            VisitorPresent = false;

            if (logStateChanges)
            {
                Debug.Log(
                    "[Kirit Presence] VISITOR LEFT"
                );
            }

            VisitorLeft?.Invoke();
        }
    }
}