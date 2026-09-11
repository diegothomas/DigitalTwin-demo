using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/*
 * MarmosetHeadMouseLook_LocalAbsolute
 *
 * Use with MarmosetJsonAnimatorV12_SkipHeadNeck.
 * V12 should exclude Head from pose animation/reset, then this script controls Head.
 *
 * Key idea:
 *   - Do NOT accumulate rotation on current head.localRotation.
 *   - Capture initial local rotation once.
 *   - Every frame overwrite:
 *       head.localRotation = headInitialLocal * headCorrection * weightedMouseOffset
 *   - Optional neck does the same.
 *
 * Default control mode is ScreenPosition:
 *   mouse left/right on screen -> yaw angle
 *   mouse up/down on screen    -> pitch angle
 *   screen center              -> zero offset
 * This avoids infinite spin and 180-degree drift caused by mouse delta accumulation.
 */
public class MarmosetHeadMouseLook_LocalAbsolute : MonoBehaviour
{
    public enum ControlMode
    {
        ScreenPosition,
        MouseDeltaAccumulated
    }

    public enum RotationOrder
    {
        YawThenPitch,
        PitchThenYaw
    }

    [Header("Bone Search")]
    public Transform boneSearchRoot;
    public SkinnedMeshRenderer skinnedMeshRenderer;

    [Header("Manual Bones")]
    public Transform headBone;
    public Transform neckBone;

    [Header("Control")]
    public bool enableControl = true;
    public ControlMode controlMode = ControlMode.ScreenPosition;

    [Tooltip("ScreenPosition mode: screen edge maps to max angle. MouseDelta mode: delta multiplier.")]
    public float mouseSensitivity = 1.0f;

    public float maxYawDegrees = 35f;
    public float maxPitchDegrees = 18f;
    public bool invertY = false;
    public float yawSign = 1f;

    [Header("Local Rotation Axes")]
    [Tooltip("Yaw axis in corrected local space. Usually Vector3.up. If direction is wrong, try Vector3.right or Vector3.forward.")]
    public Vector3 yawAxisLocal = Vector3.up;

    [Tooltip("Pitch axis in corrected local space. Usually Vector3.right. If direction is wrong, try Vector3.forward or Vector3.up.")]
    public Vector3 pitchAxisLocal = Vector3.right;

    public RotationOrder rotationOrder = RotationOrder.YawThenPitch;

    [Header("Correction Euler")]
    [Tooltip("Use this to fix 90/180 degree local bone axis mismatch for Head. Try (0,180,0), (180,0,0), or (0,0,180) if flipped.")]
    public Vector3 headCorrectionEuler = Vector3.zero;

    [Tooltip("Use this to fix 90/180 degree local bone axis mismatch for Neck.")]
    public Vector3 neckCorrectionEuler = Vector3.zero;

    [Header("Weights")]
    [Range(0f, 1f)] public float headWeight = 1.0f;
    [Range(0f, 1f)] public float neckWeight = 0.0f;

    [Header("Smoothing")]
    public float smoothSpeed = 18f;

    [Header("Initialization")]
    [Tooltip("Capture local rotations in first LateUpdate, after body animation script has run once. Recommended true.")]
    public bool captureInitialInLateUpdate = true;

    [Tooltip("Press R to recapture current Head/Neck local rotation as neutral.")]
    public bool allowKeyboardRecapture = true;

    [Header("Safety")]
    public bool printBoneInfo = true;
    public bool showDebugAngles = false;

    private Dictionary<string, Transform> boneMap = new Dictionary<string, Transform>();

    private bool capturedInitial = false;
    private Quaternion headInitialLocal = Quaternion.identity;
    private Quaternion neckInitialLocal = Quaternion.identity;

    private float targetYaw = 0f;
    private float targetPitch = 0f;
    private float currentYaw = 0f;
    private float currentPitch = 0f;

    void Start()
    {
        if (boneSearchRoot == null) boneSearchRoot = transform;
        if (skinnedMeshRenderer == null && boneSearchRoot != null)
            skinnedMeshRenderer = boneSearchRoot.GetComponentInChildren<SkinnedMeshRenderer>(true);

        BuildBoneMap();
        AutoFindBonesIfNeeded();

        if (!captureInitialInLateUpdate)
            CaptureInitialLocalRotations();

        if (printBoneInfo)
        {
            Debug.Log("MarmosetHeadMouseLook_LocalAbsolute loaded. head=" +
            (headBone != null ? headBone.name : "NULL") +
            ", neck=" + (neckBone != null ? neckBone.name : "NULL") +
            ", mode=" + controlMode);
        }
    }

    void Update()
    {
        if (!enableControl || Mouse.current == null)
            return;

        if (allowKeyboardRecapture && Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame)
        {
            CaptureInitialLocalRotations();
            ResetLookAngles();
            Debug.Log("MarmosetHeadMouseLook: recaptured neutral local rotations and reset look angles.");
        }

        if (controlMode == ControlMode.ScreenPosition)
        {
            Vector2 pos = Mouse.current.position.ReadValue();
            float w = Mathf.Max(1f, Screen.width);
            float h = Mathf.Max(1f, Screen.height);

            float nx = Mathf.Clamp((pos.x / w - 0.5f) * 2f, -1f, 1f);
            float ny = Mathf.Clamp((pos.y / h - 0.5f) * 2f, -1f, 1f);

            targetYaw = nx * maxYawDegrees * mouseSensitivity * yawSign;
            targetPitch = ny * maxPitchDegrees * mouseSensitivity * (invertY ? -1f : 1f);
        }
        else
        {
            Vector2 delta = Mouse.current.delta.ReadValue();
            targetYaw += delta.x * mouseSensitivity * yawSign;
            targetPitch += delta.y * mouseSensitivity * (invertY ? 1f : -1f);
            targetYaw = Mathf.Clamp(targetYaw, -maxYawDegrees, maxYawDegrees);
            targetPitch = Mathf.Clamp(targetPitch, -maxPitchDegrees, maxPitchDegrees);
        }

        float k = 1f - Mathf.Exp(-smoothSpeed * Time.deltaTime);
        currentYaw = Mathf.Lerp(currentYaw, targetYaw, k);
        currentPitch = Mathf.Lerp(currentPitch, targetPitch, k);
    }

    void LateUpdate()
    {
        if (!enableControl)
            return;

        if (!capturedInitial)
            CaptureInitialLocalRotations();

        Quaternion mouseOffset = BuildMouseOffset(currentYaw, currentPitch);

        if (neckBone != null && neckWeight > 0f)
        {
            Quaternion neckCorrection = Quaternion.Euler(neckCorrectionEuler);
            Quaternion neckOffset = Quaternion.Slerp(Quaternion.identity, mouseOffset, Mathf.Clamp01(neckWeight));
            neckBone.localRotation = neckInitialLocal * neckCorrection * neckOffset;
        }

        if (headBone != null && headWeight > 0f)
        {
            Quaternion headCorrection = Quaternion.Euler(headCorrectionEuler);
            Quaternion headOffset = Quaternion.Slerp(Quaternion.identity, mouseOffset, Mathf.Clamp01(headWeight));
            headBone.localRotation = headInitialLocal * headCorrection * headOffset;
        }

        if (showDebugAngles)
            Debug.Log("MarmosetHeadMouseLook yaw=" + currentYaw.ToString("F2") + " pitch=" + currentPitch.ToString("F2"));
    }

    private Quaternion BuildMouseOffset(float yaw, float pitch)
    {
        Vector3 yawAxis = yawAxisLocal.sqrMagnitude > 1e-8f ? yawAxisLocal.normalized : Vector3.up;
        Vector3 pitchAxis = pitchAxisLocal.sqrMagnitude > 1e-8f ? pitchAxisLocal.normalized : Vector3.right;

        Quaternion qYaw = Quaternion.AngleAxis(yaw, yawAxis);
        Quaternion qPitch = Quaternion.AngleAxis(pitch, pitchAxis);

        if (rotationOrder == RotationOrder.YawThenPitch)
            return qYaw * qPitch;
        else
            return qPitch * qYaw;
    }

    public void CaptureInitialLocalRotations()
    {
        if (headBone != null) headInitialLocal = headBone.localRotation;
        if (neckBone != null) neckInitialLocal = neckBone.localRotation;
        capturedInitial = true;
    }

    public void ResetLookAngles()
    {
        targetYaw = 0f;
        targetPitch = 0f;
        currentYaw = 0f;
        currentPitch = 0f;
    }

    private void BuildBoneMap()
    {
        boneMap.Clear();

        if (skinnedMeshRenderer != null && skinnedMeshRenderer.bones != null)
        {
            foreach (Transform b in skinnedMeshRenderer.bones)
            {
                if (b != null && !boneMap.ContainsKey(b.name))
                    boneMap.Add(b.name, b);
            }
        }

        if (boneSearchRoot != null)
        {
            Transform[] all = boneSearchRoot.GetComponentsInChildren<Transform>(true);
            foreach (Transform t in all)
            {
                if (t != null && !boneMap.ContainsKey(t.name))
                    boneMap.Add(t.name, t);
            }
        }
    }

    private void AutoFindBonesIfNeeded()
    {
        if (headBone == null)
            headBone = FindFirst(new string[] { "Head", "head", "Head.001", "head.001" });

        if (neckBone == null)
            neckBone = FindFirst(new string[] { "Neck", "neck", "Neck.001", "neck.001" });
    }

    private Transform FindFirst(string[] names)
    {
        foreach (string n in names)
        {
            if (boneMap.ContainsKey(n))
                return boneMap[n];
        }
        return null;
    }
}
