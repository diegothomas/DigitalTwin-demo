// Put this file in Assets/Editor/MarmosetKp3dLocalAnimImporterV6.cs
// Select the FBX instance root in Hierarchy (the object with Animator, Armature's parent), then run:
// Tools > Marmoset > Import KP3D Body-Local Anim V6 Robust

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

public class MarmosetKp3dLocalAnimImporterV6 : EditorWindow
{
    [Serializable] public class V3 { public float x; public float y; public float z; public Vector3 ToVector3() { return new Vector3(x, y, z); } }
    [Serializable] public class DirectionCurve { public string bone; public string start; public string end; public V3[] dirs; }
    [Serializable] public class AnimData
    {
        public string format;
        public string source_kp3d;
        public int total_source_frames;
        public int start_frame;
        public int end_frame;
        public int stride;
        public float fps;
        public float original_fps;
        public string axis_map;
        public float forward_sign;
        public bool global_body_rotation_removed;
        public int frame_count;
        public int[] frame_indices;
        public DirectionCurve[] curves;
        public string[] notes;
    }

    private class BoneCurve
    {
        public string boneName;
        public Transform transform;
        public Transform child;
        public string path;
        public Vector3 restDirWorld;
        public Quaternion restWorldRot;
        public Quaternion restLocalRot;
        public Vector3[] targetDirsBody;
        public int depth;
    }

    private class Finder
    {
        public Transform root;
        public List<Transform> all;
        public Dictionary<string, List<Transform>> byExact = new Dictionary<string, List<Transform>>();
        public Dictionary<string, List<Transform>> byCanonical = new Dictionary<string, List<Transform>>();

        public Finder(Transform root)
        {
            this.root = root;
            all = root.GetComponentsInChildren<Transform>(true).ToList();
            foreach (Transform t in all)
            {
                Add(byExact, t.name, t);
                Add(byCanonical, Canonical(t.name), t);
                Add(byCanonical, Canonical(StripNamespace(t.name)), t);
            }
        }

        private static void Add(Dictionary<string, List<Transform>> dict, string key, Transform t)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (!dict.TryGetValue(key, out List<Transform> list)) { list = new List<Transform>(); dict[key] = list; }
            if (!list.Contains(t)) list.Add(t);
        }

        public Transform FindBone(string requested)
        {
            foreach (string alias in AliasesFor(requested))
            {
                Transform hit = BestFromList(Get(byExact, alias));
                if (hit != null) return hit;
                hit = BestFromList(Get(byCanonical, Canonical(alias)));
                if (hit != null) return hit;
            }

            // Last fallback: contains match, useful for namespace/prefix imported bones.
            string want = Canonical(requested);
            List<Transform> contains = all.Where(t => Canonical(t.name).Contains(want) || want.Contains(Canonical(t.name))).ToList();
            return BestFromList(contains);
        }

        public List<Transform> Get(Dictionary<string, List<Transform>> dict, string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            return dict.TryGetValue(key, out List<Transform> list) ? list : null;
        }

        public Transform BestFromList(List<Transform> list)
        {
            if (list == null || list.Count == 0) return null;
            if (list.Count == 1) return list[0];

            // Avoid mesh object named Body; prefer armature chains and deeper bones.
            string[] preferred = new string[] {
                "/Armature/Base/", "Armature/Base/", "/Base/", "/Armature/", "/Root/", "/Skeleton/"
            };
            foreach (string token in preferred)
            {
                Transform hit = list.FirstOrDefault(t => FullPath(t).Contains(token));
                if (hit != null) return hit;
            }
            return list.OrderByDescending(t => Depth(t)).First();
        }
    }

    [MenuItem("Tools/Marmoset/Dump Selected Hierarchy V6")]
    public static void DumpSelectedHierarchy()
    {
        GameObject root = Selection.activeGameObject;
        if (root == null)
        {
            EditorUtility.DisplayDialog("Marmoset Import", "Select the FBX instance root in Hierarchy first.", "OK");
            return;
        }
        Finder finder = new Finder(root.transform);
        string report = BuildFullHierarchyReport(root.transform, finder.all);
        string path = "Assets/marmoset_selected_hierarchy_debug.txt";
        File.WriteAllText(path, report);
        AssetDatabase.Refresh();
        Debug.Log("Wrote hierarchy debug report: " + path + "\n" + BuildKeyPathReport(root.transform, finder));
        EditorUtility.DisplayDialog("Marmoset Import", "Wrote Assets/marmoset_selected_hierarchy_debug.txt\nOpen it and check whether Shoulder.L/R and Pelvis.L/R are listed.", "OK");
    }

    [MenuItem("Tools/Marmoset/Import KP3D Body-Local Anim V6 Robust")]
    public static void ImportAnim()
    {
        GameObject rootObj = Selection.activeGameObject;
        if (rootObj == null)
        {
            EditorUtility.DisplayDialog("Marmoset Import", "Select the FBX instance root in Hierarchy, not the .fbx asset in Project. It should be the object with Animator, i.e. Armature's parent.", "OK");
            return;
        }

        string jsonPath = EditorUtility.OpenFilePanel("Select all_local_body_dirs_v3/v4.json", Application.dataPath, "json");
        if (string.IsNullOrEmpty(jsonPath)) return;

        AnimData data = JsonUtility.FromJson<AnimData>(File.ReadAllText(jsonPath));
        if (data == null || data.curves == null || data.curves.Length == 0)
        {
            EditorUtility.DisplayDialog("Marmoset Import", "Could not parse JSON or JSON has no curves.", "OK");
            return;
        }

        Transform root = rootObj.transform;
        Finder finder = new Finder(root);
        Debug.Log("Selected root: " + rootObj.name + " | transform count under selected root: " + finder.all.Count);
        Debug.Log("Detected key FBX paths:\n" + BuildKeyPathReport(root, finder));

        string hierarchyReportPath = "Assets/marmoset_selected_hierarchy_debug.txt";
        File.WriteAllText(hierarchyReportPath, BuildFullHierarchyReport(root, finder.all));
        AssetDatabase.Refresh();

        if (!TryComputeRestBodyBasis(finder, root, data.forward_sign == 0 ? 1.0f : data.forward_sign,
                                     out Vector3 restX, out Vector3 restY, out Vector3 restZ,
                                     out string bodyBasisError))
        {
            string msg = bodyBasisError + "\n\nI wrote a debug hierarchy file here:\n" + hierarchyReportPath +
                         "\n\nMost common fix: select the FBX model in Project > Rig > Animation Type = Generic > uncheck Optimize Game Objects > Apply, then drag the model into Scene again and select that Hierarchy object.";
            Debug.LogWarning(msg + "\nDetected key paths:\n" + BuildKeyPathReport(root, finder));
            EditorUtility.DisplayDialog("Marmoset Import", msg, "OK");
            return;
        }

        List<BoneCurve> boneCurves = new List<BoneCurve>();
        List<string> missing = new List<string>();

        foreach (DirectionCurve curve in data.curves)
        {
            if (curve == null || string.IsNullOrEmpty(curve.bone) || curve.dirs == null || curve.dirs.Length == 0) continue;
            Transform bone = finder.FindBone(curve.bone);
            if (bone == null)
            {
                missing.Add(curve.bone + " [bone not found]");
                continue;
            }

            Transform child = FindBestChildForBone(bone, finder, curve.bone);
            Vector3 restDir;
            if (child != null) restDir = child.position - bone.position;
            else if (bone.parent != null) restDir = bone.position - bone.parent.position;
            else
            {
                missing.Add(curve.bone + " [no child and no parent to infer segment direction]");
                continue;
            }
            if (restDir.sqrMagnitude < 1e-10f)
            {
                missing.Add(curve.bone + " [zero rest segment length: " + RelativePath(root, bone) + "]");
                continue;
            }

            boneCurves.Add(new BoneCurve
            {
                boneName = curve.bone,
                transform = bone,
                child = child,
                path = RelativePath(root, bone),
                restDirWorld = restDir.normalized,
                restWorldRot = bone.rotation,
                restLocalRot = bone.localRotation,
                targetDirsBody = curve.dirs.Select(v => v.ToVector3()).ToArray(),
                depth = DepthFrom(root, bone)
            });
        }

        if (missing.Count > 0) Debug.LogWarning("Missing/skipped curves:\n" + string.Join("\n", missing));
        if (boneCurves.Count == 0)
        {
            EditorUtility.DisplayDialog("Marmoset Import", "No curves could be bound. Check Assets/marmoset_selected_hierarchy_debug.txt.", "OK");
            return;
        }

        boneCurves = boneCurves.OrderBy(b => b.depth).ToList();
        int nFrames = boneCurves.Min(b => b.targetDirsBody.Length);
        if (data.frame_count > 0) nFrames = Math.Min(nFrames, data.frame_count);
        float fps = data.fps > 0 ? data.fps : 30.0f;

        AnimationClip clip = new AnimationClip();
        clip.name = Path.GetFileNameWithoutExtension(jsonPath) + "_local_v6";
        clip.frameRate = fps;
        clip.legacy = false;

        Dictionary<Transform, Quaternion[]> targetWorldByBone = new Dictionary<Transform, Quaternion[]>();
        Dictionary<Transform, Quaternion[]> targetLocalByBone = new Dictionary<Transform, Quaternion[]>();

        foreach (BoneCurve bc in boneCurves)
        {
            Quaternion[] worldArr = new Quaternion[nFrames];
            Quaternion[] localArr = new Quaternion[nFrames];
            for (int f = 0; f < nFrames; f++)
            {
                Vector3 dBody = bc.targetDirsBody[f];
                if (dBody.sqrMagnitude < 1e-10f) dBody = Vector3.up;
                dBody.Normalize();

                Vector3 targetWorldDir = (restX * dBody.x + restY * dBody.y + restZ * dBody.z).normalized;
                if (targetWorldDir.sqrMagnitude < 1e-10f) targetWorldDir = bc.restDirWorld;

                Quaternion deltaWorld = Quaternion.FromToRotation(bc.restDirWorld, targetWorldDir);
                Quaternion targetWorld = NormalizeQuaternion(deltaWorld * bc.restWorldRot);
                worldArr[f] = targetWorld;

                Quaternion parentWorld = GetAnimatedOrRestParentWorld(bc.transform.parent, targetWorldByBone, f);
                Quaternion targetLocal = NormalizeQuaternion(Quaternion.Inverse(parentWorld) * targetWorld);
                localArr[f] = targetLocal;
            }
            MakeContinuous(localArr);
            targetWorldByBone[bc.transform] = worldArr;
            targetLocalByBone[bc.transform] = localArr;
        }

        foreach (BoneCurve bc in boneCurves)
        {
            Quaternion[] arr = targetLocalByBone[bc.transform];
            AnimationCurve x = new AnimationCurve();
            AnimationCurve y = new AnimationCurve();
            AnimationCurve z = new AnimationCurve();
            AnimationCurve w = new AnimationCurve();
            for (int f = 0; f < nFrames; f++)
            {
                float t = f / fps;
                Quaternion q = arr[f];
                x.AddKey(t, q.x); y.AddKey(t, q.y); z.AddKey(t, q.z); w.AddKey(t, q.w);
            }
            SetLinearTangents(x); SetLinearTangents(y); SetLinearTangents(z); SetLinearTangents(w);
            SetRotCurve(clip, bc.path, "x", x);
            SetRotCurve(clip, bc.path, "y", y);
            SetRotCurve(clip, bc.path, "z", z);
            SetRotCurve(clip, bc.path, "w", w);
        }

        clip.EnsureQuaternionContinuity();
        string defaultName = Path.GetFileNameWithoutExtension(jsonPath) + "_local_v6.anim";
        string assetPath = EditorUtility.SaveFilePanelInProject("Save generated localRotation .anim", defaultName, "anim", "Choose where to save the generated AnimationClip asset.");
        if (string.IsNullOrEmpty(assetPath)) return;
        AssetDatabase.CreateAsset(clip, assetPath);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        string summary = "Created local marmoset animation clip: " + assetPath +
                         "\nCurves bound: " + boneCurves.Count +
                         "\nSkipped/missing: " + missing.Count +
                         "\nFrames: " + nFrames +
                         "\nFPS: " + fps +
                         "\nBound paths:\n" + string.Join("\n", boneCurves.Select(b => "  " + b.boneName + " => " + b.path + " restRef=" + (b.child != null ? b.child.name : "parent")));
        Debug.Log(summary);
        EditorUtility.DisplayDialog("Marmoset Import", "Created animation. Curves bound: " + boneCurves.Count + ", missing: " + missing.Count + "\nCheck Console for bound paths.", "OK");
    }

    private static readonly Dictionary<string, string[]> ChildCandidates = new Dictionary<string, string[]>
    {
        {"Chest", new [] {"Neck", "Shoulder.L", "Shoulder.R"}},
        {"Neck", new [] {"Head"}},
        {"Head", new [] {"Head_IK_end", "Head_end"}},
        {"Shoulder.L", new [] {"UpperArm.L"}},
        {"UpperArm.L", new [] {"Forearm.L"}},
        {"Forearm.L", new [] {"Hand.001.L", "Hand.L", "Forearm_IK_L_end"}},
        {"Shoulder.R", new [] {"UpperArm.R"}},
        {"UpperArm.R", new [] {"Forearm.R"}},
        {"Forearm.R", new [] {"Hand.001.R", "Hand.R", "Forearm_IK_R_end"}},
        {"Pelvis.L", new [] {"UpperLeg.L"}},
        {"UpperLeg.L", new [] {"LowerLeg.L"}},
        {"LowerLeg.L", new [] {"Foot.001.L", "Foot.L", "LowerLeg_end.L"}},
        {"Pelvis.R", new [] {"UpperLeg.R"}},
        {"UpperLeg.R", new [] {"LowerLeg.R"}},
        {"LowerLeg.R", new [] {"Foot.001.R", "Foot.R", "LowerLeg_end.R"}},
    };

    private static Transform FindBestChildForBone(Transform bone, Finder finder, string boneName)
    {
        if (ChildCandidates.TryGetValue(boneName, out string[] candidates))
        {
            foreach (string name in candidates)
            {
                Transform hit = FindDescendantByBoneName(bone, finder, name);
                if (hit != null) return hit;
            }
        }
        if (bone.childCount > 0) return bone.GetChild(0);
        return null;
    }

    private static Transform FindDescendantByBoneName(Transform root, Finder finder, string wanted)
    {
        string cw = Canonical(wanted);
        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
        {
            if (t == root) continue;
            string ct = Canonical(t.name);
            if (ct == cw) return t;
        }
        return null;
    }

    private static bool TryComputeRestBodyBasis(Finder finder, Transform root, float forwardSign, out Vector3 x, out Vector3 y, out Vector3 z, out string error)
    {
        x = Vector3.right; y = Vector3.up; z = Vector3.forward; error = "";

        Transform lsho = finder.FindBone("Shoulder.L");
        Transform rsho = finder.FindBone("Shoulder.R");
        Transform lhip = finder.FindBone("Pelvis.L");
        Transform rhip = finder.FindBone("Pelvis.R");

        List<string> missing = new List<string>();
        if (lsho == null) missing.Add("Shoulder.L");
        if (rsho == null) missing.Add("Shoulder.R");
        if (lhip == null) missing.Add("Pelvis.L");
        if (rhip == null) missing.Add("Pelvis.R");
        if (missing.Count > 0)
        {
            error = "Could not find required visible FBX bones under the selected object: " + string.Join(", ", missing) +
                    "\nSelected root: " + root.name +
                    "\nVisible Transform count: " + finder.all.Count +
                    "\n\nThis usually means one of these:\n" +
                    "1) You selected the wrong object. Select the FBX instance in Hierarchy, not the Project .fbx asset and not Armature/Base itself.\n" +
                    "2) The FBX Rig has Optimize Game Objects ON, so Unity hides bones from the hierarchy.\n" +
                    "3) The bone names changed after import.";
            return false;
        }

        // Important for this FBX:
        // Shoulder.L and Shoulder.R share the same pivot at Chest/Neck, and Pelvis.L/R share the same pivot at Base.
        // So using the bone transform positions directly makes the left-right axis zero.  Use the first real child
        // segment endpoint instead: UpperArm.L/R for shoulders and UpperLeg.L/R for hips.
        Vector3 lShoPt = ResolveBodyBasisPoint(finder, lsho, "UpperArm.L", "Forearm.L");
        Vector3 rShoPt = ResolveBodyBasisPoint(finder, rsho, "UpperArm.R", "Forearm.R");
        Vector3 lHipPt = ResolveBodyBasisPoint(finder, lhip, "UpperLeg.L", "LowerLeg.L");
        Vector3 rHipPt = ResolveBodyBasisPoint(finder, rhip, "UpperLeg.R", "LowerLeg.R");

        Vector3 shoulderCenter = 0.5f * (lShoPt + rShoPt);
        Vector3 hipCenter = 0.5f * (lHipPt + rHipPt);

        x = 0.5f * (lShoPt + lHipPt) - 0.5f * (rShoPt + rHipPt);       // body left-right axis, positive to .L side
        y = shoulderCenter - hipCenter;                                // body up/cranial axis

        if (x.sqrMagnitude < 1e-10f || y.sqrMagnitude < 1e-10f)
        {
            error = "Found Shoulder/Pelvis bones, but could not build a non-degenerate body basis even from child endpoints. " +
                    "Check that UpperArm.L/R and UpperLeg.L/R are visible and that the selected object is the rest-pose FBX instance.\n\n" +
                    "Computed basis points:\n" +
                    "  L shoulder point = " + lShoPt.ToString("F4") + "\n" +
                    "  R shoulder point = " + rShoPt.ToString("F4") + "\n" +
                    "  L hip point      = " + lHipPt.ToString("F4") + "\n" +
                    "  R hip point      = " + rHipPt.ToString("F4");
            return false;
        }

        x.Normalize();
        y.Normalize();

        // Make x perpendicular to y; otherwise sideways and up can leak into each other.
        Vector3 xProjected = Vector3.ProjectOnPlane(x, y);
        if (xProjected.sqrMagnitude > 1e-10f) x = xProjected.normalized;

        z = Vector3.Cross(x, y).normalized * Mathf.Sign(forwardSign == 0 ? 1.0f : forwardSign);
        if (z.sqrMagnitude < 1e-10f) z = root.forward;
        y = Vector3.Cross(z, x).normalized;

        Debug.Log("FBX rest body basis V6 endpoint mode: X(left)=" + x.ToString("F4") + " Y(up)=" + y.ToString("F4") + " Z(forward-ish)=" + z.ToString("F4") +
                  "\nBasis endpoint paths:" +
                  "\n  LShoulder endpoint=" + RelativePath(root, FindDescendantByBoneName(lsho, finder, "UpperArm.L")) +
                  "\n  RShoulder endpoint=" + RelativePath(root, FindDescendantByBoneName(rsho, finder, "UpperArm.R")) +
                  "\n  LPelvis endpoint=" + RelativePath(root, FindDescendantByBoneName(lhip, finder, "UpperLeg.L")) +
                  "\n  RPelvis endpoint=" + RelativePath(root, FindDescendantByBoneName(rhip, finder, "UpperLeg.R")));
        return true;
    }

    private static Vector3 ResolveBodyBasisPoint(Finder finder, Transform pivot, string primaryChild, string fallbackChild)
    {
        Transform p = FindDescendantByBoneName(pivot, finder, primaryChild);
        if (p != null && (p.position - pivot.position).sqrMagnitude > 1e-10f) return p.position;

        Transform f = FindDescendantByBoneName(pivot, finder, fallbackChild);
        if (f != null && (f.position - pivot.position).sqrMagnitude > 1e-10f) return f.position;

        // Fall back to any child/descendant that is not at the same pivot.
        foreach (Transform t in pivot.GetComponentsInChildren<Transform>(true))
        {
            if (t == pivot) continue;
            if ((t.position - pivot.position).sqrMagnitude > 1e-10f) return t.position;
        }

        // Last fallback keeps the old behavior, but it will likely be caught as degenerate.
        return pivot.position;
    }

    private static string[] AliasesFor(string name)
    {
        Dictionary<string, string[]> aliases = new Dictionary<string, string[]>
        {
            {"Shoulder.L", new [] {"Shoulder.L", "Shoulder_L", "LeftShoulder", "LShoulder", "ShoulderLeft", "shoulder.L"}},
            {"Shoulder.R", new [] {"Shoulder.R", "Shoulder_R", "RightShoulder", "RShoulder", "ShoulderRight", "shoulder.R"}},
            {"UpperArm.L", new [] {"UpperArm.L", "UpperArm_L", "LeftUpperArm", "UpperArmLeft"}},
            {"UpperArm.R", new [] {"UpperArm.R", "UpperArm_R", "RightUpperArm", "UpperArmRight"}},
            {"Forearm.L", new [] {"Forearm.L", "Forearm_L", "LeftForearm", "ForeArm.L", "LowerArm.L"}},
            {"Forearm.R", new [] {"Forearm.R", "Forearm_R", "RightForearm", "ForeArm.R", "LowerArm.R"}},
            {"Pelvis.L", new [] {"Pelvis.L", "Pelvis_L", "LeftPelvis", "LPelvis", "Hip.L", "Hip_L", "LeftHip"}},
            {"Pelvis.R", new [] {"Pelvis.R", "Pelvis_R", "RightPelvis", "RPelvis", "Hip.R", "Hip_R", "RightHip"}},
            {"UpperLeg.L", new [] {"UpperLeg.L", "UpperLeg_L", "LeftUpperLeg", "Thigh.L", "Thigh_L", "LeftThigh"}},
            {"UpperLeg.R", new [] {"UpperLeg.R", "UpperLeg_R", "RightUpperLeg", "Thigh.R", "Thigh_R", "RightThigh"}},
            {"LowerLeg.L", new [] {"LowerLeg.L", "LowerLeg_L", "LeftLowerLeg", "Shin.L", "Shin_L", "Calf.L"}},
            {"LowerLeg.R", new [] {"LowerLeg.R", "LowerLeg_R", "RightLowerLeg", "Shin.R", "Shin_R", "Calf.R"}},
        };
        if (aliases.TryGetValue(name, out string[] arr)) return arr;
        return new [] { name };
    }

    private static string Canonical(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = StripNamespace(s).ToLowerInvariant();
        char[] keep = s.Where(c => char.IsLetterOrDigit(c)).ToArray();
        return new string(keep);
    }

    private static string StripNamespace(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        int colon = s.LastIndexOf(':');
        if (colon >= 0 && colon < s.Length - 1) s = s.Substring(colon + 1);
        int slash = s.LastIndexOf('/');
        if (slash >= 0 && slash < s.Length - 1) s = s.Substring(slash + 1);
        return s;
    }

    private static Quaternion GetAnimatedOrRestParentWorld(Transform parent, Dictionary<Transform, Quaternion[]> previousWorlds, int frame)
    {
        Transform p = parent;
        while (p != null)
        {
            if (previousWorlds.TryGetValue(p, out Quaternion[] arr)) return arr[Mathf.Clamp(frame, 0, arr.Length - 1)];
            p = p.parent;
        }
        return parent != null ? parent.rotation : Quaternion.identity;
    }

    private static Quaternion NormalizeQuaternion(Quaternion q)
    {
        float m = Mathf.Sqrt(q.x*q.x + q.y*q.y + q.z*q.z + q.w*q.w);
        if (m < 1e-10f) return Quaternion.identity;
        return new Quaternion(q.x/m, q.y/m, q.z/m, q.w/m);
    }

    private static void MakeContinuous(Quaternion[] q)
    {
        for (int i = 1; i < q.Length; i++)
        {
            if (Quaternion.Dot(q[i-1], q[i]) < 0f) q[i] = new Quaternion(-q[i].x, -q[i].y, -q[i].z, -q[i].w);
        }
    }

    private static void SetRotCurve(AnimationClip clip, string path, string component, AnimationCurve curve)
    {
        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, typeof(Transform), "m_LocalRotation." + component), curve);
    }

    private static void SetLinearTangents(AnimationCurve c)
    {
        for (int i = 0; i < c.length; i++)
        {
            AnimationUtility.SetKeyLeftTangentMode(c, i, AnimationUtility.TangentMode.Linear);
            AnimationUtility.SetKeyRightTangentMode(c, i, AnimationUtility.TangentMode.Linear);
        }
    }

    private static int Depth(Transform t) { int d = 0; while (t.parent != null) { d++; t = t.parent; } return d; }
    private static int DepthFrom(Transform root, Transform t) { int d = 0; while (t != null && t != root) { d++; t = t.parent; } return d; }

    private static string RelativePath(Transform root, Transform t)
    {
        if (t == root) return "";
        List<string> names = new List<string>();
        Transform cur = t;
        while (cur != null && cur != root) { names.Add(cur.name); cur = cur.parent; }
        names.Reverse();
        return string.Join("/", names.ToArray());
    }

    private static string FullPath(Transform t)
    {
        List<string> names = new List<string>();
        while (t != null) { names.Add(t.name); t = t.parent; }
        names.Reverse();
        return string.Join("/", names.ToArray());
    }

    private static string BuildKeyPathReport(Transform root, Finder finder)
    {
        string[] keys = new string[] {"Armature", "Base", "Body", "Chest", "Neck", "Head", "Shoulder.L", "UpperArm.L", "Forearm.L", "Shoulder.R", "UpperArm.R", "Forearm.R", "Pelvis.L", "UpperLeg.L", "LowerLeg.L", "Foot.001.L", "Pelvis.R", "UpperLeg.R", "LowerLeg.R", "Foot.001.R"};
        List<string> lines = new List<string>();
        foreach (string k in keys)
        {
            Transform t = finder.FindBone(k);
            if (t != null) lines.Add(k + " => " + RelativePath(root, t) + "  actualName=" + t.name);
            else lines.Add(k + " => NOT FOUND");
        }
        return string.Join("\n", lines.ToArray());
    }

    private static string BuildFullHierarchyReport(Transform root, List<Transform> all)
    {
        List<string> lines = new List<string>();
        lines.Add("Selected root: " + root.name);
        lines.Add("Visible Transform count: " + all.Count);
        lines.Add("Hierarchy:");
        foreach (Transform t in all.OrderBy(t => RelativePath(root, t)))
        {
            lines.Add("  " + RelativePath(root, t) + "    name=" + t.name + "    localRot=" + t.localRotation.eulerAngles.ToString("F3") + "    pos=" + t.position.ToString("F4"));
        }
        return string.Join("\n", lines.ToArray());
    }
}
#endif
