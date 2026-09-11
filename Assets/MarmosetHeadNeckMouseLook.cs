using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

[DefaultExecutionOrder(100)]
public sealed class MarmosetHeadNeckMouseLook : MonoBehaviour
{
    public enum MouseMode
    {
        ScreenPosition,
        AccumulatedDelta
    }

    [Header("Rig")]
    [SerializeField] private Transform boneSearchRoot;
    [SerializeField] private SkinnedMeshRenderer skinnedMeshRenderer;
    [SerializeField] private Transform orientationRoot;
    [SerializeField] private Transform bodyBone;
    [SerializeField] private Transform neckBone;
    [SerializeField] private Transform headBone;
    [SerializeField] private Transform leftEyeBone;
    [SerializeField] private Transform rightEyeBone;

    [Header("Mouse")]
    [SerializeField] private MouseMode mouseMode = MouseMode.AccumulatedDelta;
    [SerializeField] private float sensitivity = 0.15f;
    [SerializeField] private bool invertPitch;
    [SerializeField] private bool invertYaw;
    [Min(0f)] [SerializeField] private float maxYaw = 35f;
    [Min(0f)] [SerializeField] private float maxPitch = 18f;
    [Min(0f)] [SerializeField] private float smoothing = 18f;

    [Header("Face calibration")]
    [Tooltip("Use the eye bones to learn how the visible face is oriented relative to the irregular FBX Head axes.")]
    [SerializeField] private bool calibrateFaceFromEyes = true;
    [SerializeField] private Vector3 fallbackLocalFaceForward = Vector3.forward;
    [SerializeField] private Vector3 fallbackLocalFaceUp = Vector3.up;
    [Tooltip("Capture after body animation has initialized, before applying the Body-to-Neck look direction.")]
    [SerializeField] private bool captureCalibrationOnFirstLateUpdate = true;
    [SerializeField] private bool allowKeyboardRecapture = true;
    [SerializeField] private Key recaptureKey = Key.R;

    [Header("Diagnostics")]
    [SerializeField] private bool logInitialization = true;
    [SerializeField] private bool drawDebugRays;
    [Min(0.01f)] [SerializeField] private float debugRayLength = 0.2f;

    public float CurrentYaw { get; private set; }
    public float CurrentPitch { get; private set; }
    public bool HasNeutralPose => calibrated;

    private readonly Dictionary<string, Transform> bones =
        new Dictionary<string, Transform>(StringComparer.OrdinalIgnoreCase);

    private Quaternion boneRotationFromViewFrame = Quaternion.identity;
    private float targetYaw;
    private float targetPitch;
    private bool calibrated;

    private void Start()
    {
        boneSearchRoot ??= transform;
        orientationRoot ??= transform;
        if (skinnedMeshRenderer == null)
            skinnedMeshRenderer = boneSearchRoot.GetComponentInChildren<SkinnedMeshRenderer>(true);

        BuildBoneMap();
        FindBonesWhenUnassigned();

        if (bodyBone == null || neckBone == null || headBone == null)
        {
            Debug.LogError($"MarmosetHeadNeckMouseLook requires Body, Neck and Head. " +
                           $"body={BoneName(bodyBone)}, neck={BoneName(neckBone)}, head={BoneName(headBone)}.", this);
            enabled = false;
            return;
        }

        if (!captureCalibrationOnFirstLateUpdate)
            CaptureNeutralPose();

        if (logInitialization)
        {
            Debug.Log($"MarmosetHeadNeckMouseLook ready: zero direction=Body->Neck, " +
                      $"face calibration={(CanUseEyes() ? "eyes" : "fallback axes")}, mode={mouseMode}.", this);
        }
    }

    private void Update()
    {
        if (Mouse.current != null)
            ReadMouse(Mouse.current);

        if (allowKeyboardRecapture && Keyboard.current != null && recaptureKey != Key.None &&
            Keyboard.current[recaptureKey].wasPressedThisFrame)
        {
            ResetLook();
        }

        float blend = smoothing <= 0f ? 1f : 1f - Mathf.Exp(-smoothing * Time.deltaTime);
        CurrentYaw = Mathf.Lerp(CurrentYaw, targetYaw, blend);
        CurrentPitch = Mathf.Lerp(CurrentPitch, targetPitch, blend);
    }

    private void LateUpdate()
    {
        if (!calibrated)
            CaptureNeutralPose();
        if (!calibrated || !TryBuildDefaultViewFrame(out Quaternion viewFrame))
            return;

        Vector3 baseForward = viewFrame * Vector3.forward;
        Vector3 baseUp = viewFrame * Vector3.up;
        Vector3 baseRight = viewFrame * Vector3.right;

        Quaternion yawRotation = Quaternion.AngleAxis(CurrentYaw, baseUp);
        Vector3 yawedForward = yawRotation * baseForward;
        Vector3 yawedUp = yawRotation * baseUp;
        Vector3 yawedRight = yawRotation * baseRight;

        Quaternion pitchRotation = Quaternion.AngleAxis(CurrentPitch, yawedRight);
        Vector3 targetForward = pitchRotation * yawedForward;
        Vector3 targetUp = pitchRotation * yawedUp;
        Quaternion targetViewFrame = Quaternion.LookRotation(targetForward, targetUp);
        headBone.rotation = targetViewFrame * boneRotationFromViewFrame;

        if (drawDebugRays)
        {
            Vector3 neckRay = (neckBone.position - bodyBone.position).normalized;
            Debug.DrawRay(bodyBone.position, neckRay * debugRayLength, Color.blue);
            Debug.DrawRay(headBone.position, targetForward * debugRayLength, Color.green);
            Debug.DrawRay(headBone.position, targetUp * debugRayLength, Color.yellow);
        }
    }

    public void SetLook(float yawDegrees, float pitchDegrees, bool immediate = false)
    {
        targetYaw = Mathf.Clamp(yawDegrees, -maxYaw, maxYaw);
        targetPitch = Mathf.Clamp(pitchDegrees, -maxPitch, maxPitch);
        if (immediate)
        {
            CurrentYaw = targetYaw;
            CurrentPitch = targetPitch;
        }
    }

    public void ResetLook()
    {
        targetYaw = 0f;
        targetPitch = 0f;
        CurrentYaw = 0f;
        CurrentPitch = 0f;
    }

    public void CaptureNeutralPose()
    {
        if (headBone == null || !TryBuildDefaultViewFrame(out Quaternion viewFrame) ||
            !TryBuildCurrentFaceFrame(viewFrame, out Quaternion faceFrame))
        {
            calibrated = false;
            return;
        }

        // Convert a desired visible view frame to the imported Head bone rotation. The face
        // frame signs are selected against the perpendicular forward frame, avoiding 180° flips.
        boneRotationFromViewFrame = Quaternion.Inverse(faceFrame) * headBone.rotation;
        calibrated = true;
    }

    private bool TryBuildDefaultViewFrame(out Quaternion frame)
    {
        frame = Quaternion.identity;
        Vector3 neckAxis = neckBone.position - bodyBone.position;
        if (neckAxis.sqrMagnitude < 1e-10f)
            return false;
        neckAxis.Normalize();

        Vector3 referenceUp = orientationRoot != null ? orientationRoot.up : Vector3.up;
        Vector3 right = Vector3.Cross(referenceUp, neckAxis).normalized;
        if (right.sqrMagnitude < 1e-8f)
        {
            Vector3 referenceRight = orientationRoot != null ? orientationRoot.right : Vector3.right;
            right = Vector3.ProjectOnPlane(referenceRight, neckAxis).normalized;
            if (right.sqrMagnitude < 1e-8f)
                return false;
        }

        // This forward lies in the body's vertical/longitudinal plane and is exactly
        // perpendicular to Body->Neck. Pick the sign that follows the horizontal part
        // of Body->Neck, which distinguishes the animal's front from its back.
        Vector3 forward = Vector3.Cross(right, neckAxis).normalized;
        Vector3 planarBodyForward = Vector3.ProjectOnPlane(neckAxis, referenceUp).normalized;
        if (planarBodyForward.sqrMagnitude > 1e-8f && Vector3.Dot(forward, planarBodyForward) < 0f)
            forward = -forward;
        else if (planarBodyForward.sqrMagnitude <= 1e-8f && orientationRoot != null &&
                 Vector3.Dot(forward, orientationRoot.forward) < 0f)
            forward = -forward;

        frame = Quaternion.LookRotation(forward, neckAxis);
        return true;
    }

    private bool TryBuildCurrentFaceFrame(Quaternion referenceFrame, out Quaternion frame)
    {
        frame = Quaternion.identity;
        Vector3 referenceForward = referenceFrame * Vector3.forward;
        Vector3 referenceUp = referenceFrame * Vector3.up;
        if (calibrateFaceFromEyes && CanUseEyes())
        {
            Vector3 forward = (leftEyeBone.position + rightEyeBone.position) * 0.5f - headBone.position;
            Vector3 right = rightEyeBone.position - leftEyeBone.position;
            right.Normalize();
            forward = Vector3.ProjectOnPlane(forward, right).normalized;
            if (forward.sqrMagnitude > 1e-8f && right.sqrMagnitude > 1e-8f)
            {
                if (Vector3.Dot(forward, referenceForward) < 0f)
                    forward = -forward;

                Vector3 candidateUp = Vector3.Cross(forward, right).normalized;
                if (Vector3.Dot(candidateUp, referenceUp) < 0f)
                    right = -right;

                Vector3 up = Vector3.Cross(forward, right).normalized;
                frame = Quaternion.LookRotation(forward, up);
                return true;
            }
        }

        Vector3 localForward = fallbackLocalFaceForward.sqrMagnitude > 1e-8f
            ? fallbackLocalFaceForward.normalized
            : Vector3.forward;
        Vector3 localUp = fallbackLocalFaceUp.sqrMagnitude > 1e-8f
            ? fallbackLocalFaceUp.normalized
            : Vector3.up;
        Vector3 worldForward = headBone.TransformDirection(localForward);
        Vector3 worldUp = headBone.TransformDirection(localUp);
        worldUp = Vector3.ProjectOnPlane(worldUp, worldForward).normalized;
        if (worldUp.sqrMagnitude < 1e-8f)
            return false;

        if (Vector3.Dot(worldForward, referenceForward) < 0f)
            worldForward = -worldForward;
        if (Vector3.Dot(worldUp, referenceUp) < 0f)
            worldUp = -worldUp;
        frame = Quaternion.LookRotation(worldForward, worldUp);
        return true;
    }

    private void ReadMouse(Mouse mouse)
    {
        float yaw;
        float pitch;
        if (mouseMode == MouseMode.ScreenPosition)
        {
            Vector2 position = mouse.position.ReadValue();
            float normalizedX = Mathf.Clamp(position.x / Mathf.Max(1f, Screen.width) * 2f - 1f, -1f, 1f);
            float normalizedY = Mathf.Clamp(position.y / Mathf.Max(1f, Screen.height) * 2f - 1f, -1f, 1f);
            yaw = normalizedX * maxYaw * sensitivity * (invertYaw ? -1f : 1f);
            pitch = normalizedY * maxPitch * sensitivity * (invertPitch ? -1f : 1f);
        }
        else
        {
            Vector2 delta = mouse.delta.ReadValue();
            yaw = targetYaw + delta.x * sensitivity * (invertYaw ? -1f : 1f);
            pitch = targetPitch + delta.y * sensitivity * (invertPitch ? 1f : -1f);
        }

        targetYaw = Mathf.Clamp(yaw, -maxYaw, maxYaw);
        targetPitch = Mathf.Clamp(pitch, -maxPitch, maxPitch);
    }

    private void BuildBoneMap()
    {
        bones.Clear();
        if (skinnedMeshRenderer != null && skinnedMeshRenderer.bones != null)
        {
            foreach (Transform bone in skinnedMeshRenderer.bones)
                AddBone(bone);
        }

        foreach (Transform bone in boneSearchRoot.GetComponentsInChildren<Transform>(true))
            AddBone(bone);
    }

    private void AddBone(Transform bone)
    {
        if (bone != null && !bones.ContainsKey(bone.name))
            bones.Add(bone.name, bone);
    }

    private void FindBonesWhenUnassigned()
    {
        bodyBone ??= FindFirst("Body");
        neckBone ??= FindFirst("Neck", "Neck.001");
        headBone ??= FindFirst("Head", "Head.001");
        leftEyeBone ??= FindFirst("Eye.L", "Eye_L", "LeftEye");
        rightEyeBone ??= FindFirst("Eye.R", "Eye_R", "RightEye");
    }

    private Transform FindFirst(params string[] names)
    {
        foreach (string boneName in names)
        {
            if (bones.TryGetValue(boneName, out Transform bone))
                return bone;
        }
        return null;
    }

    private bool CanUseEyes()
    {
        return leftEyeBone != null && rightEyeBone != null;
    }

    private static string BoneName(Transform bone)
    {
        return bone == null ? "not found" : bone.name;
    }
}
