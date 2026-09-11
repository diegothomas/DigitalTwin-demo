using System;
using System.IO;
using System.Collections.Generic;
using UnityEngine;

/*
 * MarmosetJsonAnimatorV12_SkipHeadNeck
 *
 * Purpose:
 *   - Keep the V4/V5 position-aim body animation.
 *   - Drive global movement by a simple trajectory rule:
 *       UnityWorldPosition[t] = InitialUnityPosition + scaled(R_extra * (SourcePoint[t] - SourcePoint[0]))
 *   - Unlike V10, horizontal and vertical trajectory scales can be adjusted separately
 *   - SourcePoint can be rootPosition, one joint such as Body/tail-root, or an average of joints.
 *   - The JSON is assumed to already be exported with the chosen axis-map.
 *     trajectoryExtraRotationEuler is only an additional correction if the trajectory still needs rotating in Unity.
 *
 * Recommended use:
 *   motionRoot      = outer MarmosetCharacterRoot, placed manually at desired start position
 *   boneSearchRoot  = FBX/visual model root containing Armature/SkinnedMeshRenderer
 *   animJson        = your best marmodel_unity_positions_v4_*.json
 *   trajectorySource = JointByName
 *   trajectoryJointName = Body
 *   applyGlobalTranslation = true
 *   useDeltaFromFirstFrame = true
 *   applyGlobalYaw = false
 */
public class MarmosetJsonAnimatorV12_SkipHeadNeck : MonoBehaviour
{
    [Header("Input")]
    public TextAsset animJson;
    public Transform motionRoot;
    public Transform boneSearchRoot;
    public SkinnedMeshRenderer skinnedMeshRenderer;

    [Header("Playback")]
    public bool playOnStart = true;
    public bool loop = true;
    public float playbackSpeed = 1.0f;
    public int startFrame = 0;
    public bool useFixedUpdate = false;

    [Header("Pose / Local Aim")]
    public bool resetToRestEachFrame = true;
    public bool usePrimaryChildOnly = true;
    public float aimWeight = 1.0f;
    public bool targetDirectionsFollowMotionRoot = true;
    public bool skipHandsAndFeet = false;
    public string[] extraSkipBones = new string[] { };

    [Header("User Controlled Head / Neck") ]
    [Tooltip("If true, V11/V12 will not animate these bones; they can be controlled by a separate mouse/head controller script.")]
    public bool excludeHeadNeckFromPoseAnimation = true;

    [Tooltip("Bone names excluded from pose aim animation when excludeHeadNeckFromPoseAnimation is true.")]
    public string[] headNeckSkipBones = new string[]
    {
        "Head", "head", "Head.001", "head.001",
        "Neck", "neck",
        "Neck.001", "neck.001",
        "Bendy_Bone.001"
    };

    [Header("Global Translation: p1->p2->p3 + initial Unity position")]
    public bool applyGlobalTranslation = true;
    public bool useDeltaFromFirstFrame = true;

    [Tooltip("Used only when useSeparateVerticalScale is false. Kept for compatibility with V10.")]
    public float globalTranslationScale = 1.0f;

    [Header("V11 Separate Horizontal / Vertical Scale")]
    public bool useSeparateVerticalScale = true;
    public float horizontalTranslationScale = 10.0f;
    public float verticalTranslationScale = 3.0f;
    public float verticalOffset = 0.0f;

    [Tooltip("Unity vertical axis after trajectoryExtraRotationEuler. Usually Y.")]
    public VerticalAxis verticalAxis = VerticalAxis.Y;

    public TrajectorySource trajectorySource = TrajectorySource.JointByName;
    public string trajectoryJointName = "Body";
    public string[] trajectoryAverageJointNames = new string[] { "Chest", "Pelvis.L", "Pelvis.R" };

    [Tooltip("Additional rotation applied to trajectory delta after JSON axis-map. Example: (0,180,0) flips horizontal direction around Unity Y.")]
    public Vector3 trajectoryExtraRotationEuler = Vector3.zero;

    [Tooltip("If true, final trajectory delta is additionally rotated by the initial motionRoot rotation. Usually true if motionRoot has an initial facing direction in Unity.")]
    public bool trajectoryFollowsInitialRootRotation = false;

    [Tooltip("If true, final trajectory delta is additionally rotated by the current motionRoot rotation. Use carefully if another script changes yaw during playback.")]
    public bool trajectoryFollowsCurrentRootRotation = false;

    public bool lockWorldY = false;
    public bool lockWorldX = false;
    public bool lockWorldZ = false;

    [Header("Optional Global Yaw")]
    public bool applyGlobalYaw = false;
    public YawSource yawSource = YawSource.TrajectoryVelocity;
    public Vector3 yawUpAxis = Vector3.up;
    public float yawSmoothing = 12.0f;
    public float minVelocityForYaw = 1e-4f;

    [Header("Rigidbody")]
    public Rigidbody motionRigidbody;
    public bool useRigidbodyMove = false;

    [Header("Debug")]
    public bool logOnStart = true;
    public bool writeTrajectoryDebugCsv = false;
    public int debugEveryNFrames = 1;

    public enum TrajectorySource
    {
        RootPosition,
        JointByName,
        AverageJoints
    }

    public enum VerticalAxis
    {
        X,
        Y,
        Z
    }

    public enum YawSource
    {
        None,
        TrajectoryVelocity
    }

    [Serializable] public class Metadata { public float fps; public int frameCount; public int jointCount; public string axisMap; }
    [Serializable] public class Vec3Data { public float x; public float y; public float z; }
    [Serializable] public class JointData { public int index; public string name; public int parentIndex; public string parentName; public int primaryChildIndex; public string primaryChildName; }
    [Serializable] public class FrameData { public int frame; public Vec3Data rootPosition; public Vec3Data[] jointPositions; }
    [Serializable] public class AnimData { public Metadata metadata; public JointData[] joints; public FrameData[] frames; }

    private AnimData anim;
    private Dictionary<string, Transform> boneMap = new Dictionary<string, Transform>();
    private Dictionary<string, Quaternion> restWorldRot = new Dictionary<string, Quaternion>();
    private Dictionary<string, Quaternion> restLocalRot = new Dictionary<string, Quaternion>();
    private Dictionary<string, Vector3> restWorldPos = new Dictionary<string, Vector3>();
    private Dictionary<string, int> jointNameToIndex = new Dictionary<string, int>();
    private HashSet<string> skipBoneSet = new HashSet<string>();

    private float fps = 30.0f;
    private float timer = 0.0f;
    private int currentFrame = 0;
    private Vector3 initialMotionRootPosition;
    private Quaternion initialMotionRootRotation;
    private Vector3 sourceStart;
    private bool initialized = false;

    void Start()
    {
        Initialize();
    }

    void Update()
    {
        if (!useFixedUpdate) Tick(Time.deltaTime);
    }

    void FixedUpdate()
    {
        if (useFixedUpdate) Tick(Time.fixedDeltaTime);
    }

    public void Initialize()
    {
        if (initialized) return;
        if (motionRoot == null) motionRoot = transform;
        if (boneSearchRoot == null) boneSearchRoot = transform;
        if (skinnedMeshRenderer == null) skinnedMeshRenderer = boneSearchRoot.GetComponentInChildren<SkinnedMeshRenderer>(true);
        if (motionRigidbody == null && motionRoot != null) motionRigidbody = motionRoot.GetComponent<Rigidbody>();

        if (animJson == null)
        {
            Debug.LogError("Marmoset V11: animJson is null.");
            enabled = false;
            return;
        }

        anim = JsonUtility.FromJson<AnimData>(animJson.text);
        if (anim == null || anim.frames == null || anim.frames.Length == 0 || anim.joints == null)
        {
            Debug.LogError("Marmoset V11: failed to parse JSON or empty animation.");
            enabled = false;
            return;
        }

        fps = (anim.metadata != null && anim.metadata.fps > 0.0f) ? anim.metadata.fps : 30.0f;
        BuildJointIndex();
        BuildSkipSet();
        BuildBoneMap();
        SaveRestState();

        startFrame = Mathf.Clamp(startFrame, 0, anim.frames.Length - 1);
        currentFrame = startFrame;
        timer = startFrame / fps;
        initialMotionRootPosition = motionRoot.position;
        initialMotionRootRotation = motionRoot.rotation;
        sourceStart = GetTrajectorySource(startFrame);

        if (writeTrajectoryDebugCsv) WriteTrajectoryCsv();

        if (logOnStart)
        {
            Debug.Log("Marmoset V12 SkipHeadNeck loaded. frames=" + anim.frames.Length +
                " fps=" + fps +
                " joints=" + anim.joints.Length +
                " foundBones=" + CountFoundBones() + "/" + anim.joints.Length +
                " axisMap=" + (anim.metadata != null ? anim.metadata.axisMap : "unknown") +
                " trajectorySource=" + trajectorySource +
                " sourceStart=" + sourceStart.ToString("F4") +
                " initialUnity=" + initialMotionRootPosition.ToString("F4") +
                " separateScale=" + useSeparateVerticalScale +
                " hScale=" + horizontalTranslationScale +
                " vScale=" + verticalTranslationScale +
                " vAxis=" + verticalAxis +
                " vOffset=" + verticalOffset);
        }

        initialized = true;
        if (!playOnStart) ApplyFrame(startFrame);
    }

    private void Tick(float dt)
    {
        if (!initialized) Initialize();
        if (!playOnStart) return;
        timer += dt * playbackSpeed;
        int frame = Mathf.FloorToInt(timer * fps);
        if (loop)
        {
            int len = Mathf.Max(1, anim.frames.Length);
            frame = ((frame % len) + len) % len;
        }
        else
        {
            frame = Mathf.Clamp(frame, 0, anim.frames.Length - 1);
        }
        currentFrame = frame;
        ApplyFrame(frame);
    }

    private Vector3 V(Vec3Data v)
    {
        if (v == null) return Vector3.zero;
        return new Vector3(v.x, v.y, v.z);
    }

    private void BuildJointIndex()
    {
        jointNameToIndex.Clear();
        for (int i = 0; i < anim.joints.Length; i++)
        {
            if (anim.joints[i] != null && !string.IsNullOrEmpty(anim.joints[i].name) && !jointNameToIndex.ContainsKey(anim.joints[i].name))
                jointNameToIndex.Add(anim.joints[i].name, i);
        }
    }

    private void BuildSkipSet()
    {
        skipBoneSet.Clear();

        if (extraSkipBones != null)
        {
            foreach (string s in extraSkipBones)
                if (!string.IsNullOrEmpty(s)) skipBoneSet.Add(s);
        }

        if (excludeHeadNeckFromPoseAnimation && headNeckSkipBones != null)
        {
            foreach (string s in headNeckSkipBones)
                if (!string.IsNullOrEmpty(s)) skipBoneSet.Add(s);
        }
    }

    private void BuildBoneMap()
    {
        boneMap.Clear();
        if (skinnedMeshRenderer != null && skinnedMeshRenderer.bones != null)
        {
            foreach (Transform b in skinnedMeshRenderer.bones)
            {
                if (b != null && !boneMap.ContainsKey(b.name)) boneMap.Add(b.name, b);
            }
        }
        Transform[] all = boneSearchRoot.GetComponentsInChildren<Transform>(true);
        foreach (Transform t in all)
        {
            if (t != null && !boneMap.ContainsKey(t.name)) boneMap.Add(t.name, t);
        }
    }

    private void SaveRestState()
    {
        restWorldRot.Clear(); restLocalRot.Clear(); restWorldPos.Clear();
        foreach (var kv in boneMap)
        {
            Transform t = kv.Value;
            restWorldRot[kv.Key] = t.rotation;
            restLocalRot[kv.Key] = t.localRotation;
            restWorldPos[kv.Key] = t.position;
        }
    }

    private int CountFoundBones()
    {
        int c = 0;
        foreach (var j in anim.joints)
        {
            if (j != null && boneMap.ContainsKey(j.name)) c++;
        }
        return c;
    }

    private Vector3 GetJointPosition(int frame, int jointIndex)
    {
        frame = Mathf.Clamp(frame, 0, anim.frames.Length - 1);
        if (jointIndex < 0 || jointIndex >= anim.frames[frame].jointPositions.Length) return Vector3.zero;
        return V(anim.frames[frame].jointPositions[jointIndex]);
    }

    private Vector3 GetJointPositionByName(int frame, string name)
    {
        if (!jointNameToIndex.ContainsKey(name)) return Vector3.zero;
        return GetJointPosition(frame, jointNameToIndex[name]);
    }

    private Vector3 GetTrajectorySource(int frame)
    {
        frame = Mathf.Clamp(frame, 0, anim.frames.Length - 1);
        if (trajectorySource == TrajectorySource.RootPosition)
        {
            return V(anim.frames[frame].rootPosition);
        }
        if (trajectorySource == TrajectorySource.JointByName)
        {
            if (!jointNameToIndex.ContainsKey(trajectoryJointName))
            {
                Debug.LogWarning("Marmoset V11: trajectory joint not found: " + trajectoryJointName + ". fallback to rootPosition.");
                return V(anim.frames[frame].rootPosition);
            }
            return GetJointPosition(frame, jointNameToIndex[trajectoryJointName]);
        }

        Vector3 sum = Vector3.zero;
        int n = 0;
        if (trajectoryAverageJointNames != null)
        {
            foreach (string name in trajectoryAverageJointNames)
            {
                if (jointNameToIndex.ContainsKey(name))
                {
                    sum += GetJointPosition(frame, jointNameToIndex[name]);
                    n++;
                }
            }
        }
        if (n > 0) return sum / n;
        return V(anim.frames[frame].rootPosition);
    }

    private Vector3 ApplySeparateScale(Vector3 d)
    {
        if (!useSeparateVerticalScale)
        {
            return d * globalTranslationScale;
        }

        Vector3 outD = d;
        if (verticalAxis == VerticalAxis.Y)
        {
            outD.x = d.x * horizontalTranslationScale;
            outD.y = d.y * verticalTranslationScale + verticalOffset;
            outD.z = d.z * horizontalTranslationScale;
        }
        else if (verticalAxis == VerticalAxis.Z)
        {
            outD.x = d.x * horizontalTranslationScale;
            outD.y = d.y * horizontalTranslationScale;
            outD.z = d.z * verticalTranslationScale + verticalOffset;
        }
        else // VerticalAxis.X
        {
            outD.x = d.x * verticalTranslationScale + verticalOffset;
            outD.y = d.y * horizontalTranslationScale;
            outD.z = d.z * horizontalTranslationScale;
        }
        return outD;
    }

    private Vector3 ComputeTrajectoryDelta(int frame)
    {
        Vector3 src = GetTrajectorySource(frame);
        Vector3 delta = useDeltaFromFirstFrame ? (src - sourceStart) : src;

        // Important order:
        // 1. compute source delta in JSON coordinate system
        // 2. apply extra coordinate rotation if trajectory direction still needs correction
        // 3. scale horizontal and vertical components separately in Unity coordinates
        Quaternion extra = Quaternion.Euler(trajectoryExtraRotationEuler);
        delta = extra * delta;
        delta = ApplySeparateScale(delta);

        if (trajectoryFollowsInitialRootRotation) delta = initialMotionRootRotation * delta;
        if (trajectoryFollowsCurrentRootRotation) delta = motionRoot.rotation * delta;

        return delta;
    }

    private void ApplyGlobalTranslation(int frame)
    {
        if (!applyGlobalTranslation || motionRoot == null) return;
        Vector3 delta = ComputeTrajectoryDelta(frame);
        Vector3 target = initialMotionRootPosition + delta;
        if (lockWorldX) target.x = initialMotionRootPosition.x;
        if (lockWorldY) target.y = initialMotionRootPosition.y;
        if (lockWorldZ) target.z = initialMotionRootPosition.z;

        if (useRigidbodyMove && motionRigidbody != null)
        {
            motionRigidbody.MovePosition(target);
        }
        else
        {
            motionRoot.position = target;
        }
    }

    private void ApplyGlobalYaw(int frame)
    {
        if (!applyGlobalYaw || yawSource == YawSource.None || motionRoot == null) return;
        if (yawSource == YawSource.TrajectoryVelocity)
        {
            int prev = Mathf.Max(0, frame - 1);
            Vector3 d0 = ComputeTrajectoryDelta(prev);
            Vector3 d1 = ComputeTrajectoryDelta(frame);
            Vector3 vel = d1 - d0;
            Vector3 planar = Vector3.ProjectOnPlane(vel, yawUpAxis.normalized);
            if (planar.sqrMagnitude > minVelocityForYaw)
            {
                Quaternion targetRot = Quaternion.LookRotation(planar.normalized, yawUpAxis.normalized);
                Quaternion newRot = Quaternion.Slerp(motionRoot.rotation, targetRot, 1.0f - Mathf.Exp(-yawSmoothing * Time.deltaTime));
                if (useRigidbodyMove && motionRigidbody != null) motionRigidbody.MoveRotation(newRot);
                else motionRoot.rotation = newRot;
            }
        }
    }

    private bool ShouldSkipBone(string name)
    {
        if (skipBoneSet.Contains(name)) return true;
        if (!skipHandsAndFeet) return false;
        string n = name.ToLowerInvariant();
        return n.Contains("hand") || n.Contains("finger") || n.Contains("foot") || n.Contains("toe");
    }

    private void ResetBonesToRest()
    {
        foreach (var kv in boneMap)
        {
            // Important for user-controlled head/neck:
            // Do not reset skipped bones to rest each frame, otherwise the mouse controller
            // and this animator will fight over Head/Neck rotations.
            if (ShouldSkipBone(kv.Key)) continue;

            if (restLocalRot.ContainsKey(kv.Key)) kv.Value.localRotation = restLocalRot[kv.Key];
        }
    }

    private void ApplyPoseAim(int frame)
    {
        if (resetToRestEachFrame) ResetBonesToRest();
        frame = Mathf.Clamp(frame, 0, anim.frames.Length - 1);

        for (int i = 0; i < anim.joints.Length; i++)
        {
            JointData j = anim.joints[i];
            if (j == null || string.IsNullOrEmpty(j.name)) continue;
            if (!boneMap.ContainsKey(j.name)) continue;
            if (ShouldSkipBone(j.name)) continue;

            int childIndex = usePrimaryChildOnly ? j.primaryChildIndex : j.primaryChildIndex;
            if (childIndex < 0 || childIndex >= anim.joints.Length) continue;
            JointData child = anim.joints[childIndex];
            if (child == null) continue;

            Vector3 srcParent = GetJointPosition(frame, i);
            Vector3 srcChild = GetJointPosition(frame, childIndex);
            Vector3 targetDir = srcChild - srcParent;
            if (targetDir.sqrMagnitude < 1e-10f) continue;

            if (targetDirectionsFollowMotionRoot && motionRoot != null)
                targetDir = motionRoot.TransformDirection(targetDir.normalized);
            else
                targetDir = targetDir.normalized;

            Transform bone = boneMap[j.name];
            Transform childBone = boneMap.ContainsKey(child.name) ? boneMap[child.name] : null;
            Vector3 currentDir;
            if (childBone != null)
                currentDir = childBone.position - bone.position;
            else
                currentDir = bone.TransformDirection(Vector3.forward);

            if (currentDir.sqrMagnitude < 1e-10f) continue;
            currentDir.Normalize();

            Quaternion delta = Quaternion.FromToRotation(currentDir, targetDir);
            Quaternion targetWorldRot = delta * bone.rotation;
            bone.rotation = Quaternion.Slerp(bone.rotation, targetWorldRot, Mathf.Clamp01(aimWeight));
        }
    }

    public void ApplyFrame(int frame)
    {
        ApplyGlobalTranslation(frame);
        ApplyGlobalYaw(frame);
        ApplyPoseAim(frame);
    }

    private string OutputDir()
    {
        string dir = Path.Combine(Application.persistentDataPath, "MarmosetV11Debug");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private void WriteTrajectoryCsv()
    {
        string path = Path.Combine(OutputDir(), "trajectory_debug_v11.csv");
        using (StreamWriter sw = new StreamWriter(path))
        {
            sw.WriteLine("frame,src_x,src_y,src_z,delta_x,delta_y,delta_z,target_x,target_y,target_z");
            for (int f = 0; f < anim.frames.Length; f += Mathf.Max(1, debugEveryNFrames))
            {
                Vector3 src = GetTrajectorySource(f);
                Vector3 delta = ComputeTrajectoryDelta(f);
                Vector3 target = initialMotionRootPosition + delta;
                sw.WriteLine(string.Join(",", f, src.x, src.y, src.z, delta.x, delta.y, delta.z, target.x, target.y, target.z));
            }
        }
        Debug.Log("Marmoset V11 wrote trajectory csv: " + path);
    }
}
