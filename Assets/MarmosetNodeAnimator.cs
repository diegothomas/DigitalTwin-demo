using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

[DefaultExecutionOrder(-100)]
public sealed class MarmosetNodeAnimator : MonoBehaviour
{
    [Serializable]
    public sealed class AnimationNode
    {
        public string name = "Node";
        [Min(0)] public int frame;
    }

    [Serializable] private sealed class Metadata
    {
        public float fps;
    }

    [Serializable] private sealed class Vec3Data
    {
        public float x;
        public float y;
        public float z;
    }

    [Serializable] private sealed class JointData
    {
        public string name;
        public int parentIndex;
        public int primaryChildIndex;
    }

    [Serializable] private sealed class FrameData
    {
        public Vec3Data rootPosition;
        public Vec3Data[] jointPositions;
    }

    [Serializable] private sealed class AnimationData
    {
        public Metadata metadata;
        public JointData[] joints;
        public FrameData[] frames;
    }

    public enum VerticalAxis
    {
        X,
        Y,
        Z
    }

    [Header("Data and rig")]
    [SerializeField] private TextAsset animationJson;
    [SerializeField] private Transform motionRoot;
    [SerializeField] private Transform boneSearchRoot;
    [SerializeField] private SkinnedMeshRenderer skinnedMeshRenderer;

    [Header("Animation nodes")]
    [Tooltip("Each entry is a stop point in the source JSON. One key press plays from the current node to the next node.")]
    [SerializeField] private AnimationNode[] nodes =
    {
        new AnimationNode { name = "Start", frame = 0 },
        new AnimationNode { name = "End", frame = 499 }
    };
    [SerializeField] private Key advanceKey = Key.Space;
    [SerializeField] private Key resetKey = Key.R;
    [Min(0.01f)] [SerializeField] private float playbackSpeed = 1f;
    [SerializeField] private bool restartFromFirstNodeAtEnd;

    [Header("Body pose")]
    [Range(0f, 1f)] [SerializeField] private float poseWeight = 1f;
    [SerializeField] private bool targetDirectionsFollowMotionRoot = true;
    [Tooltip("These bones are neither reset nor animated, so another component can own them safely.")]
    [SerializeField] private string[] excludedBones = { "Neck", "Head" };
    [Tooltip("Apply the first node's pose to excluded bones once, then leave them entirely to the mouse controller.")]
    [SerializeField] private bool initializeExcludedBonesFromFirstNode;

    [Header("Rig retargeting")]
    [Tooltip("Automatically remap the shifted Hand/Foot numbering used by 3dmarmoset to the bone semantics stored in the older JSON.")]
    [SerializeField] private bool autoRemapShiftedHandFootBones = true;
    [Tooltip("Reject a JSON primary-child pair when the resolved rig bones are not directly parented. This prevents cross-finger/cross-toe aim directions.")]
    [SerializeField] private bool requireDirectPrimaryChild = true;
    [Tooltip("Use a second connected joint direction when available to correct bone roll/twist differences between the old and new bind poses.")]
    [SerializeField] private bool correctTwistFromSecondaryDirection = true;
    [Range(0f, 1f)] [SerializeField] private float secondaryDirectionTwistWeight = 1f;
    [Range(0.001f, 0.5f)] [SerializeField] private float minimumSecondaryPlaneSine = 0.08f;

    [Header("Root trajectory")]
    [SerializeField] private bool applyRootTranslation = true;
    [SerializeField] private string trajectoryJointName = "Body";
    [SerializeField] private float horizontalScale = 10f;
    [SerializeField] private float verticalScale = 13f;
    [SerializeField] private float verticalOffset;
    [SerializeField] private VerticalAxis verticalAxis = VerticalAxis.Y;
    [SerializeField] private Vector3 trajectoryRotationEuler;
    [SerializeField] private bool trajectoryFollowsInitialRootRotation;
    [SerializeField] private bool lockWorldX;
    [SerializeField] private bool lockWorldY;
    [SerializeField] private bool lockWorldZ;

    [Header("Diagnostics")]
    [SerializeField] private bool logInitialization = true;

    public bool IsPlaying { get; private set; }
    public int CurrentNodeIndex { get; private set; }
    public int CurrentFrame { get; private set; }
    public int FrameCount => animationData != null && animationData.frames != null
        ? animationData.frames.Length
        : 0;
    public float NormalizedProgress => FrameCount > 1
        ? Mathf.Clamp01(frameCursor / (FrameCount - 1f))
        : 0f;
    public int NodeCount => nodes?.Length ?? 0;
    public string CurrentNodeName => GetNodeName(CurrentNodeIndex);
    public string NextNodeName => GetNodeName(Mathf.Min(CurrentNodeIndex + 1, NodeCount - 1));

    private readonly Dictionary<string, int> jointIndices =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Transform> bones =
        new Dictionary<string, Transform>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Quaternion> restLocalRotations =
        new Dictionary<string, Quaternion>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Quaternion> restRotationsInRootSpace =
        new Dictionary<string, Quaternion>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Vector3> restDirectionsInRootSpace =
        new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Vector3> restSecondaryDirectionsInRootSpace =
        new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> secondaryOriginJointIndices =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> secondaryJointIndices =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> excludedBoneNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> sourceToRigBoneNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> rigMappingIssues = new List<string>();

    private AnimationData animationData;
    private float sourceFps = 30f;
    private float frameCursor;
    private int targetNodeIndex = -1;
    private int trajectoryJointIndex = -1;
    private Vector3 trajectoryOrigin;
    private Vector3 initialRootPosition;
    private Quaternion initialRootRotation;
    private int mappedJointCount;
    private int missingJointCount;
    private int invalidPrimaryChildCount;
    private int twistCalibratedJointCount;
    private bool initialized;

    private void Start()
    {
        Initialize();
    }

    private void Update()
    {
        if (!initialized || animationData == null)
            return;

        if (Keyboard.current != null && resetKey != Key.None &&
            Keyboard.current[resetKey].wasPressedThisFrame)
        {
            ResetToStart();
        }
        else if (Keyboard.current != null && advanceKey != Key.None &&
                 Keyboard.current[advanceKey].wasPressedThisFrame)
        {
            PlayToNextNode();
        }

        if (IsPlaying)
            AdvancePlayback(Time.deltaTime);
    }

    public bool Initialize()
    {
        if (initialized)
            return true;

        motionRoot ??= transform;
        boneSearchRoot ??= transform;
        if (skinnedMeshRenderer == null)
            skinnedMeshRenderer = boneSearchRoot.GetComponentInChildren<SkinnedMeshRenderer>(true);

        if (animationJson == null)
            return FailInitialization("Animation JSON is not assigned.");

        animationData = JsonUtility.FromJson<AnimationData>(animationJson.text);
        if (animationData == null || animationData.joints == null ||
            animationData.frames == null || animationData.frames.Length == 0)
        {
            return FailInitialization("Animation JSON is empty or has an unsupported layout.");
        }

        sourceFps = animationData.metadata != null && animationData.metadata.fps > 0f
            ? animationData.metadata.fps
            : 30f;
        initialRootPosition = motionRoot.position;
        initialRootRotation = motionRoot.rotation;

        BuildJointIndex();
        BuildBoneMap();
        BuildRigBoneRemap();
        ValidateRigMapping();
        BuildExcludedBoneSet();
        CaptureRestPose();
        BuildRestDirections();

        if (nodes == null || nodes.Length == 0)
            nodes = new[] { new AnimationNode { name = "Start", frame = 0 } };

        ClampNodeFrames();
        trajectoryOrigin = GetTrajectoryPosition(nodes[0].frame);
        CurrentNodeIndex = 0;
        frameCursor = nodes[0].frame;
        CurrentFrame = nodes[0].frame;
        initialized = true;
        ApplyFrame(CurrentFrame);
        if (initializeExcludedBonesFromFirstNode)
            ApplyPose(CurrentFrame, true);

        if (logInitialization)
        {
            Debug.Log($"MarmosetNodeAnimator ready: {FrameCount} frames at {sourceFps:F1} fps, " +
                      $"{nodes.Length} nodes, start frame {CurrentFrame}, excluded bones {excludedBoneNames.Count}, " +
                      $"rig joints {mappedJointCount}/{animationData.joints.Length}, " +
                      $"missing joints {missingJointCount}, automatic remaps {sourceToRigBoneNames.Count}, " +
                      $"invalid primary-child pairs {invalidPrimaryChildCount}, " +
                      $"twist-calibrated joints {twistCalibratedJointCount}.", this);
        }

        if (rigMappingIssues.Count > 0)
        {
            int shownIssueCount = Mathf.Min(8, rigMappingIssues.Count);
            string issueSummary = string.Join("\n", rigMappingIssues.GetRange(0, shownIssueCount));
            if (shownIssueCount < rigMappingIssues.Count)
                issueSummary += $"\n... and {rigMappingIssues.Count - shownIssueCount} more.";
            Debug.LogWarning($"MarmosetNodeAnimator rig mapping skipped unsafe joints:\n{issueSummary}", this);
        }

        return true;
    }

    public bool PlayToNextNode()
    {
        if (!initialized && !Initialize())
            return false;
        if (IsPlaying || nodes.Length < 2)
            return false;

        if (CurrentNodeIndex >= nodes.Length - 1)
        {
            if (!restartFromFirstNodeAtEnd)
                return false;

            GoToNode(0);
            return true;
        }

        targetNodeIndex = CurrentNodeIndex + 1;
        IsPlaying = true;
        return true;
    }

    public bool GoToNode(int nodeIndex)
    {
        if (!initialized && !Initialize())
            return false;
        if (nodeIndex < 0 || nodeIndex >= nodes.Length)
            return false;

        IsPlaying = false;
        targetNodeIndex = -1;
        CurrentNodeIndex = nodeIndex;
        frameCursor = nodes[nodeIndex].frame;
        CurrentFrame = nodes[nodeIndex].frame;
        ApplyFrame(CurrentFrame);
        return true;
    }

    public bool ResetToStart()
    {
        if (!initialized && !Initialize())
            return false;

        if (motionRoot != null)
            motionRoot.SetPositionAndRotation(initialRootPosition, initialRootRotation);

        return GoToNode(0);
    }

    public int GetNodeFrame(int nodeIndex)
    {
        if (!initialized && !Initialize())
            return 0;
        if (nodeIndex < 0 || nodeIndex >= NodeCount || nodes[nodeIndex] == null)
            return 0;
        return nodes[nodeIndex].frame;
    }

    public string GetNodeName(int nodeIndex)
    {
        if (nodes == null || nodeIndex < 0 || nodeIndex >= nodes.Length || nodes[nodeIndex] == null)
            return string.Empty;
        return string.IsNullOrWhiteSpace(nodes[nodeIndex].name)
            ? $"Node {nodeIndex}"
            : nodes[nodeIndex].name;
    }

    public Vector3 GetNodeWorldPosition(int nodeIndex)
    {
        if (!initialized && !Initialize())
            return transform.position;
        if (nodeIndex < 0 || nodeIndex >= NodeCount || nodes[nodeIndex] == null)
            return initialRootPosition;
        return CalculateTrajectoryTarget(nodes[nodeIndex].frame);
    }

    private void AdvancePlayback(float deltaTime)
    {
        int targetFrame = nodes[targetNodeIndex].frame;
        float direction = Mathf.Sign(targetFrame - frameCursor);

        if (Mathf.Approximately(direction, 0f))
        {
            ReachTargetNode(targetFrame);
            return;
        }

        frameCursor += direction * sourceFps * playbackSpeed * Mathf.Max(0f, deltaTime);
        bool reached = direction > 0f ? frameCursor >= targetFrame : frameCursor <= targetFrame;
        if (reached)
            frameCursor = targetFrame;

        int newFrame = direction > 0f
            ? Mathf.FloorToInt(frameCursor)
            : Mathf.CeilToInt(frameCursor);
        newFrame = Mathf.Clamp(newFrame, 0, FrameCount - 1);
        if (newFrame != CurrentFrame || reached)
        {
            CurrentFrame = newFrame;
            ApplyFrame(CurrentFrame);
        }

        if (reached)
            ReachTargetNode(targetFrame);
    }

    private void ReachTargetNode(int targetFrame)
    {
        CurrentNodeIndex = targetNodeIndex;
        targetNodeIndex = -1;
        CurrentFrame = targetFrame;
        frameCursor = targetFrame;
        IsPlaying = false;
        ApplyFrame(CurrentFrame);
    }

    private void ApplyFrame(int frame)
    {
        frame = Mathf.Clamp(frame, 0, FrameCount - 1);
        ApplyTrajectory(frame);
        ResetAnimatedBonesToRest();
        ApplyPose(frame, false);
    }

    private void ApplyTrajectory(int frame)
    {
        if (!applyRootTranslation || motionRoot == null)
            return;

        motionRoot.position = CalculateTrajectoryTarget(frame);
    }

    private Vector3 CalculateTrajectoryTarget(int frame)
    {
        if (!applyRootTranslation || motionRoot == null)
            return initialRootPosition;

        Vector3 delta = GetTrajectoryPosition(frame) - trajectoryOrigin;
        delta = Quaternion.Euler(trajectoryRotationEuler) * delta;

        Vector3 scaled = delta * horizontalScale;
        switch (verticalAxis)
        {
            case VerticalAxis.X:
                scaled.x = delta.x * verticalScale + verticalOffset;
                break;
            case VerticalAxis.Y:
                scaled.y = delta.y * verticalScale + verticalOffset;
                break;
            case VerticalAxis.Z:
                scaled.z = delta.z * verticalScale + verticalOffset;
                break;
        }

        if (trajectoryFollowsInitialRootRotation)
            scaled = initialRootRotation * scaled;

        Vector3 target = initialRootPosition + scaled;
        if (lockWorldX) target.x = initialRootPosition.x;
        if (lockWorldY) target.y = initialRootPosition.y;
        if (lockWorldZ) target.z = initialRootPosition.z;
        return target;
    }

    private void ResetAnimatedBonesToRest()
    {
        foreach (KeyValuePair<string, Transform> pair in bones)
        {
            if (excludedBoneNames.Contains(pair.Key))
                continue;
            if (restLocalRotations.TryGetValue(pair.Key, out Quaternion restRotation))
                pair.Value.localRotation = restRotation;
        }
    }

    private void ApplyPose(int frame, bool excludedBonesOnly)
    {
        FrameData frameData = animationData.frames[frame];
        if (frameData == null || frameData.jointPositions == null)
            return;

        for (int i = 0; i < animationData.joints.Length; i++)
        {
            JointData joint = animationData.joints[i];
            bool isExcluded = joint != null && excludedBoneNames.Contains(joint.name);
            if (joint == null || string.IsNullOrWhiteSpace(joint.name) || isExcluded != excludedBonesOnly ||
                !TryGetRigBone(joint.name, out Transform bone))
            {
                continue;
            }

            int childIndex = joint.primaryChildIndex;
            if (!TryGetJointDirection(frameData, i, childIndex, out Vector3 sourceDirection) ||
                !restDirectionsInRootSpace.TryGetValue(joint.name, out Vector3 restDirection) ||
                !restRotationsInRootSpace.TryGetValue(bone.name, out Quaternion restRotation))
            {
                continue;
            }

            Vector3 targetWorldDirection = targetDirectionsFollowMotionRoot
                ? motionRoot.TransformDirection(sourceDirection)
                : sourceDirection;
            Vector3 restWorldDirection = motionRoot.TransformDirection(restDirection);
            Quaternion restWorldRotation = motionRoot.rotation * restRotation;
            Quaternion swing = Quaternion.FromToRotation(restWorldDirection, targetWorldDirection);
            Quaternion targetWorldRotation = swing * restWorldRotation;

            if (correctTwistFromSecondaryDirection && secondaryDirectionTwistWeight > 0f &&
                secondaryOriginJointIndices.TryGetValue(joint.name, out int secondaryOriginIndex) &&
                secondaryJointIndices.TryGetValue(joint.name, out int secondaryIndex) &&
                restSecondaryDirectionsInRootSpace.TryGetValue(joint.name, out Vector3 restSecondaryDirection) &&
                TryGetJointDirection(frameData, secondaryOriginIndex, secondaryIndex,
                    out Vector3 sourceSecondaryDirection))
            {
                Vector3 targetWorldSecondaryDirection = targetDirectionsFollowMotionRoot
                    ? motionRoot.TransformDirection(sourceSecondaryDirection)
                    : sourceSecondaryDirection;
                Vector3 restWorldSecondaryDirection = motionRoot.TransformDirection(restSecondaryDirection);

                if (TryBuildDirectionBasis(restWorldDirection, restWorldSecondaryDirection,
                        out Quaternion restBasis, out float restPlaneSine) &&
                    TryBuildDirectionBasis(targetWorldDirection, targetWorldSecondaryDirection,
                        out Quaternion targetBasis, out float targetPlaneSine))
                {
                    Quaternion fullDirectionDelta = targetBasis * Quaternion.Inverse(restBasis);
                    Quaternion fullDirectionRotation = fullDirectionDelta * restWorldRotation;
                    float planeReliability = Mathf.InverseLerp(
                        minimumSecondaryPlaneSine,
                        Mathf.Min(1f, minimumSecondaryPlaneSine * 2f),
                        Mathf.Min(restPlaneSine, targetPlaneSine));
                    float twistWeight = Mathf.Clamp01(secondaryDirectionTwistWeight * planeReliability);
                    targetWorldRotation = Quaternion.Slerp(targetWorldRotation, fullDirectionRotation, twistWeight);
                }
            }

            bone.rotation = Quaternion.Slerp(bone.rotation, targetWorldRotation, poseWeight);
        }
    }

    private bool TryBuildDirectionBasis(
        Vector3 primaryDirection,
        Vector3 secondaryDirection,
        out Quaternion basis,
        out float planeSine)
    {
        basis = Quaternion.identity;
        planeSine = 0f;
        if (primaryDirection.sqrMagnitude < 1e-10f || secondaryDirection.sqrMagnitude < 1e-10f)
            return false;

        primaryDirection.Normalize();
        secondaryDirection.Normalize();
        Vector3 planeNormal = Vector3.Cross(primaryDirection, secondaryDirection);
        planeSine = planeNormal.magnitude;
        if (planeSine < minimumSecondaryPlaneSine)
            return false;

        planeNormal /= planeSine;
        Vector3 planeUp = Vector3.Cross(planeNormal, primaryDirection).normalized;
        basis = Quaternion.LookRotation(primaryDirection, planeUp);
        return true;
    }

    private bool TryGetJointDirection(FrameData frame, int parentIndex, int childIndex, out Vector3 direction)
    {
        direction = Vector3.zero;
        if (childIndex < 0 || parentIndex < 0 || parentIndex >= frame.jointPositions.Length ||
            childIndex >= frame.jointPositions.Length)
        {
            return false;
        }

        direction = ToVector(frame.jointPositions[childIndex]) - ToVector(frame.jointPositions[parentIndex]);
        if (direction.sqrMagnitude < 1e-10f)
            return false;
        direction.Normalize();
        return true;
    }

    private void BuildJointIndex()
    {
        jointIndices.Clear();
        for (int i = 0; i < animationData.joints.Length; i++)
        {
            string jointName = animationData.joints[i]?.name;
            if (!string.IsNullOrWhiteSpace(jointName) && !jointIndices.ContainsKey(jointName))
                jointIndices.Add(jointName, i);
        }

        if (!jointIndices.TryGetValue(trajectoryJointName, out trajectoryJointIndex))
        {
            trajectoryJointIndex = -1;
            Debug.LogWarning($"MarmosetNodeAnimator could not find trajectory joint '{trajectoryJointName}'; rootPosition will be used.", this);
        }
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

    private void BuildRigBoneRemap()
    {
        sourceToRigBoneNames.Clear();
        rigMappingIssues.Clear();
        if (!autoRemapShiftedHandFootBones)
            return;

        AddShiftedExtremityRemap("Hand", "L");
        AddShiftedExtremityRemap("Hand", "R");
        AddShiftedExtremityRemap("Foot", "L");
        AddShiftedExtremityRemap("Foot", "R");
    }

    private void AddShiftedExtremityRemap(string prefix, string side)
    {
        string unnumberedBone = $"{prefix}.{side}";
        string oldLastBone = $"{prefix}.019.{side}";

        // The old rig has .001-.019 and no unnumbered Hand/Foot bone. The new
        // rig inserts an unnumbered branch and ends at .018, shifting the bone
        // names without preserving their old semantic meaning.
        if (!bones.ContainsKey(unnumberedBone) || bones.ContainsKey(oldLastBone))
            return;

        string[] sourceNames =
        {
            $"{prefix}.005.{side}",
            $"{prefix}.006.{side}", $"{prefix}.007.{side}",
            $"{prefix}.008.{side}", $"{prefix}.009.{side}",
            $"{prefix}.010.{side}",
            $"{prefix}.015.{side}", $"{prefix}.016.{side}",
            $"{prefix}.017.{side}", $"{prefix}.018.{side}",
            $"{prefix}.019.{side}"
        };
        string[] rigNames =
        {
            unnumberedBone,
            $"{prefix}.007.{side}", $"{prefix}.008.{side}",
            $"{prefix}.009.{side}", $"{prefix}.010.{side}",
            $"{prefix}.005.{side}",
            $"{prefix}.006.{side}", $"{prefix}.015.{side}",
            $"{prefix}.016.{side}", $"{prefix}.017.{side}",
            $"{prefix}.018.{side}"
        };

        for (int i = 0; i < rigNames.Length; i++)
        {
            if (!bones.ContainsKey(rigNames[i]))
            {
                rigMappingIssues.Add($"Cannot enable {prefix}.{side} remap: rig bone '{rigNames[i]}' is missing.");
                return;
            }
        }

        for (int i = 0; i < sourceNames.Length; i++)
            sourceToRigBoneNames[sourceNames[i]] = rigNames[i];
    }

    private void ValidateRigMapping()
    {
        mappedJointCount = 0;
        missingJointCount = 0;

        foreach (JointData joint in animationData.joints)
        {
            if (joint == null || string.IsNullOrWhiteSpace(joint.name))
                continue;

            if (TryGetRigBone(joint.name, out _))
            {
                mappedJointCount++;
                continue;
            }

            missingJointCount++;
            rigMappingIssues.Add($"Missing rig bone for JSON joint '{joint.name}' (resolved as '{ResolveRigBoneName(joint.name)}').");
        }
    }

    private string ResolveRigBoneName(string sourceBoneName)
    {
        return sourceToRigBoneNames.TryGetValue(sourceBoneName, out string rigBoneName)
            ? rigBoneName
            : sourceBoneName;
    }

    private bool TryGetRigBone(string sourceBoneName, out Transform bone)
    {
        return bones.TryGetValue(ResolveRigBoneName(sourceBoneName), out bone);
    }

    private void BuildExcludedBoneSet()
    {
        excludedBoneNames.Clear();
        if (excludedBones == null)
            return;
        foreach (string boneName in excludedBones)
        {
            if (!string.IsNullOrWhiteSpace(boneName))
                excludedBoneNames.Add(boneName.Trim());
        }
    }

    private void CaptureRestPose()
    {
        restLocalRotations.Clear();
        restRotationsInRootSpace.Clear();
        Quaternion rootInverse = Quaternion.Inverse(motionRoot.rotation);
        foreach (KeyValuePair<string, Transform> pair in bones)
        {
            restLocalRotations[pair.Key] = pair.Value.localRotation;
            restRotationsInRootSpace[pair.Key] = rootInverse * pair.Value.rotation;
        }
    }

    private void BuildRestDirections()
    {
        restDirectionsInRootSpace.Clear();
        restSecondaryDirectionsInRootSpace.Clear();
        secondaryOriginJointIndices.Clear();
        secondaryJointIndices.Clear();
        invalidPrimaryChildCount = 0;
        twistCalibratedJointCount = 0;
        Quaternion rootInverse = Quaternion.Inverse(motionRoot.rotation);
        for (int i = 0; i < animationData.joints.Length; i++)
        {
            JointData joint = animationData.joints[i];
            if (joint == null || string.IsNullOrWhiteSpace(joint.name) ||
                !TryGetRigBone(joint.name, out Transform parentBone))
            {
                continue;
            }

            int childIndex = joint.primaryChildIndex;
            if (childIndex < 0 || childIndex >= animationData.joints.Length)
                continue;
            string childName = animationData.joints[childIndex]?.name;
            if (string.IsNullOrWhiteSpace(childName) || !TryGetRigBone(childName, out Transform childBone))
                continue;

            if (requireDirectPrimaryChild && childBone.parent != parentBone)
            {
                invalidPrimaryChildCount++;
                rigMappingIssues.Add(
                    $"JSON aim pair '{joint.name} -> {childName}' resolved to " +
                    $"'{parentBone.name} -> {childBone.name}', but the rig parent is " +
                    $"'{(childBone.parent != null ? childBone.parent.name : "<root>")}'.");
                continue;
            }

            Vector3 direction = childBone.position - parentBone.position;
            if (direction.sqrMagnitude <= 1e-10f)
                continue;

            Vector3 primaryDirection = direction.normalized;
            restDirectionsInRootSpace[joint.name] = (rootInverse * primaryDirection).normalized;

            if (!correctTwistFromSecondaryDirection)
                continue;

            int secondaryOriginIndex;
            int secondaryIndex;
            Vector3 secondaryDirection;
            if (!TryGetAnatomicalSecondaryDirection(joint.name, primaryDirection,
                    out secondaryOriginIndex, out secondaryIndex, out secondaryDirection))
            {
                secondaryOriginIndex = i;
                secondaryIndex = FindBestSecondaryJointIndex(i, childIndex, parentBone, primaryDirection,
                    out secondaryDirection);
            }

            if (secondaryIndex < 0)
                continue;

            secondaryOriginJointIndices[joint.name] = secondaryOriginIndex;
            secondaryJointIndices[joint.name] = secondaryIndex;
            restSecondaryDirectionsInRootSpace[joint.name] = (rootInverse * secondaryDirection).normalized;
            twistCalibratedJointCount++;
        }
    }

    private bool TryGetAnatomicalSecondaryDirection(
        string jointName,
        Vector3 primaryDirection,
        out int originIndex,
        out int endIndex,
        out Vector3 direction)
    {
        originIndex = -1;
        endIndex = -1;
        direction = Vector3.zero;

        string originName;
        string endName;
        if (jointName.Equals("Body", StringComparison.OrdinalIgnoreCase) ||
            jointName.Equals("Chest", StringComparison.OrdinalIgnoreCase))
        {
            // Body -> Chest alone cannot determine torso roll because both
            // pelvis roots are coincident with Body in the source skeleton.
            // The shoulder line supplies a stable anatomical left/right axis.
            originName = "Shoulder.L";
            endName = "Shoulder.R";
        }
        else if (jointName.Equals("Pelvis.L", StringComparison.OrdinalIgnoreCase) ||
                 jointName.Equals("Pelvis.R", StringComparison.OrdinalIgnoreCase))
        {
            // Both pelvis bones start at Body, so use the upper-leg span to
            // resolve the otherwise unconstrained hip roll.
            originName = "UpperLeg.L";
            endName = "UpperLeg.R";
        }
        else
        {
            return false;
        }

        if (!jointIndices.TryGetValue(originName, out originIndex) ||
            !jointIndices.TryGetValue(endName, out endIndex) ||
            !TryGetRigBone(originName, out Transform originBone) ||
            !TryGetRigBone(endName, out Transform endBone))
        {
            originIndex = -1;
            endIndex = -1;
            return false;
        }

        direction = endBone.position - originBone.position;
        if (direction.sqrMagnitude < 1e-10f)
            return false;

        direction.Normalize();
        if (Vector3.Cross(primaryDirection, direction).magnitude < minimumSecondaryPlaneSine)
        {
            direction = Vector3.zero;
            return false;
        }

        return true;
    }

    private int FindBestSecondaryJointIndex(
        int jointIndex,
        int primaryChildIndex,
        Transform rigBone,
        Vector3 primaryDirection,
        out Vector3 bestDirection)
    {
        int bestIndex = -1;
        float bestPlaneSine = minimumSecondaryPlaneSine;
        bestDirection = Vector3.zero;

        // Branching joints (chest, forearm/palm, lower leg/foot) provide the
        // most stable roll reference, so prefer the most non-collinear child.
        for (int candidateIndex = 0; candidateIndex < animationData.joints.Length; candidateIndex++)
        {
            if (candidateIndex == primaryChildIndex ||
                animationData.joints[candidateIndex] == null ||
                animationData.joints[candidateIndex].parentIndex != jointIndex ||
                !TryGetRigBone(animationData.joints[candidateIndex].name, out Transform candidateBone) ||
                candidateBone.parent != rigBone)
            {
                continue;
            }

            ConsiderSecondaryDirection(candidateIndex, candidateBone.position - rigBone.position,
                primaryDirection, ref bestIndex, ref bestPlaneSine, ref bestDirection);
        }

        // A bend plane formed by parent -> joint -> child resolves roll for
        // single-child chains such as upper arms and thighs. Near-straight
        // chains automatically fall back to the original single-direction aim.
        JointData joint = animationData.joints[jointIndex];
        int parentIndex = joint.parentIndex;
        if (parentIndex >= 0 && parentIndex < animationData.joints.Length && parentIndex != jointIndex &&
            animationData.joints[parentIndex] != null &&
            TryGetRigBone(animationData.joints[parentIndex].name, out Transform rigParent) &&
            rigBone.parent == rigParent)
        {
            ConsiderSecondaryDirection(parentIndex, rigParent.position - rigBone.position,
                primaryDirection, ref bestIndex, ref bestPlaneSine, ref bestDirection);
        }

        return bestIndex;
    }

    private static void ConsiderSecondaryDirection(
        int candidateIndex,
        Vector3 candidateDirection,
        Vector3 primaryDirection,
        ref int bestIndex,
        ref float bestPlaneSine,
        ref Vector3 bestDirection)
    {
        if (candidateDirection.sqrMagnitude < 1e-10f)
            return;

        Vector3 normalizedDirection = candidateDirection.normalized;
        float planeSine = Vector3.Cross(primaryDirection, normalizedDirection).magnitude;
        if (planeSine <= bestPlaneSine)
            return;

        bestIndex = candidateIndex;
        bestPlaneSine = planeSine;
        bestDirection = normalizedDirection;
    }

    private void ClampNodeFrames()
    {
        int lastFrame = FrameCount - 1;
        foreach (AnimationNode node in nodes)
        {
            if (node != null)
                node.frame = Mathf.Clamp(node.frame, 0, lastFrame);
        }
    }

    private Vector3 GetTrajectoryPosition(int frame)
    {
        FrameData frameData = animationData.frames[Mathf.Clamp(frame, 0, FrameCount - 1)];
        if (frameData == null)
            return Vector3.zero;
        if (trajectoryJointIndex >= 0 && frameData.jointPositions != null &&
            trajectoryJointIndex < frameData.jointPositions.Length)
        {
            return ToVector(frameData.jointPositions[trajectoryJointIndex]);
        }
        return ToVector(frameData.rootPosition);
    }

    private static Vector3 ToVector(Vec3Data value)
    {
        return value == null ? Vector3.zero : new Vector3(value.x, value.y, value.z);
    }

    private bool FailInitialization(string message)
    {
        Debug.LogError($"MarmosetNodeAnimator: {message}", this);
        enabled = false;
        return false;
    }
}
