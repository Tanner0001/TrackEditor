using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Splines;
using System.Collections.Generic;

public class TrackWindowEditor : EditorWindow
{
 
    SplineContainer spline;
    GameObject prefab;
    Transform parent;

    float spacing = 5f;
    float startOffset = 0f;
    float endOffset = 0f;
    bool alignToTangent = true;
    bool snapSpacingToPrefab = true;
    bool liveUpdate = false;
    bool stretchToEnd = false;

    int lengthSteps = 512;
    int mapSteps = 2048;



    Vector2 scroll;

    BuildSettings lastSettings;
    int lastSplineHash;
    bool hasGenerated;

    [MenuItem("Tools/RaceTrack Builder")]
    public static void Open() => GetWindow<TrackWindowEditor>("RaceTrack Builder").Show();

    void OnEnable()
    {
        EditorApplication.update += OnEditorUpdate;
    }

    void OnDisable()
    {
        EditorApplication.update -= OnEditorUpdate;
    }

    void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);

        EditorGUILayout.LabelField("Source", EditorStyles.boldLabel);
        spline = (SplineContainer)EditorGUILayout.ObjectField("Spline", spline, typeof(SplineContainer), true);
        prefab = (GameObject)EditorGUILayout.ObjectField("Prefab", prefab, typeof(GameObject), false);
        parent = (Transform)EditorGUILayout.ObjectField("Parent (optional)", parent, typeof(Transform), true);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Placement", EditorStyles.boldLabel);
        var prefabBounds = CalculatePrefabBounds(prefab);
        snapSpacingToPrefab = EditorGUILayout.ToggleLeft("Snap Spacing To Prefab Length", snapSpacingToPrefab);

        bool usingAutomaticSpacing = snapSpacingToPrefab && prefabBounds.IsValid;
        float spacingFieldValue = usingAutomaticSpacing ? Mathf.Max(0.01f, prefabBounds.Length) : spacing;

        using (new EditorGUI.DisabledScope(usingAutomaticSpacing))
        {
            spacingFieldValue = EditorGUILayout.FloatField("Spacing (m)", spacingFieldValue);
        }

        if (usingAutomaticSpacing)
        {
            EditorGUILayout.LabelField($"Prefab length: {prefabBounds.Length:F3} m", EditorStyles.miniLabel);
        }
        else
        {
            spacing = Mathf.Max(0.01f, spacingFieldValue);
        }

        if (snapSpacingToPrefab && !prefabBounds.IsValid && prefab != null)
        {
            EditorGUILayout.HelpBox("Prefab bounds not found; manual spacing will be used.", MessageType.Info);
        }
        startOffset = Mathf.Max(0f, EditorGUILayout.FloatField("Start Offset (m)", startOffset));
        endOffset = Mathf.Max(0f, EditorGUILayout.FloatField("End Offset (m)", endOffset));
        alignToTangent = EditorGUILayout.Toggle("Align to Tangent", alignToTangent);
        stretchToEnd = EditorGUILayout.ToggleLeft("Stretch Last Segment To End", stretchToEnd);
        liveUpdate = EditorGUILayout.ToggleLeft("Live Update", liveUpdate);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Quality", EditorStyles.boldLabel);
        lengthSteps = Mathf.Clamp(EditorGUILayout.IntField("Length Steps", lengthSteps), 64, 4096);
        mapSteps = Mathf.Clamp(EditorGUILayout.IntField("Map Steps", mapSteps), 128, 8192);

        EditorGUILayout.Space();
        using (new EditorGUILayout.HorizontalScope())
        {
            GUI.enabled = (spline != null && prefab != null);
            if (GUILayout.Button("Generate", GUILayout.Height(32)))
            {
                if (Generate(recordUndo: true))
                {
                    UpdateLastSettings();
                    hasGenerated = true;
                }
            }

            GUI.enabled = (parent != null);
            if (GUILayout.Button("Clear Parent Children", GUILayout.Height(32)))
            {
                ClearParentChildren();
                hasGenerated = false;
                lastSettings = default;
                lastSplineHash = 0;
            }
            GUI.enabled = true;
        }

        EditorGUILayout.HelpBox(
            "Draw a Spline in your scene, choose a prefab, set spacing, then Generate.\n" +
            "Use Parent to keep generated objects grouped, if your reading this you might have also committed a crime :).",
            MessageType.Info);

        EditorGUILayout.EndScrollView();

        if (liveUpdate)
            TryLiveUpdate();
    }

    void OnEditorUpdate()
    {
        if (liveUpdate)
            TryLiveUpdate();
    }

    bool Generate(bool recordUndo)
    {
        if (spline == null || prefab == null)
        {
            Debug.LogWarning("Assign a Spline and a Prefab.");
            return false;
        }

        var s = spline.Spline;
        if (s == null || s.Count < 2)
        {
            Debug.LogWarning("Spline is empty.");
            return false;
        }

        if (parent == null)
        {
            var holder = new GameObject("PrefabBuilt_");
            Undo.RegisterCreatedObjectUndo(holder, "Create Track Parent");
            parent = holder.transform;
        }

        var placements = BuildPlacements(s);
        System.Action apply = () => ApplyPlacements(placements, recordUndo);

        if (recordUndo)
            EditorUtil.WithUndo(parent, "Generate Along Spline", apply);
        else
            EditorUtil.WithSceneDirty(apply);

        return true;
    }

    void ClearParentChildren()
    {
        if (parent == null) return;
        EditorUtil.WithUndo(parent, "Clear Children", () =>
        {
            var toDelete = new List<GameObject>();
            foreach (Transform c in parent) toDelete.Add(c.gameObject);

            foreach (var go in toDelete)
                EditorUtil.DestroyObject(go);
        });
    }

    void TryLiveUpdate()
    {
        var settings = CaptureSettings();
        if (!settings.IsValid || spline == null)
        {
            hasGenerated = false;
            return;
        }

        int splineHash = ComputeSplineHash(spline.Spline);
        if (!hasGenerated || !settings.Equals(lastSettings) || splineHash != lastSplineHash)
        {
            if (Generate(recordUndo: false))
            {
                lastSettings = settings;
                lastSplineHash = splineHash;
                hasGenerated = true;
            }
        }
    }

    float ApproximateLength(Spline s, int steps)
    {
        Vector3 prev = s.EvaluatePosition(0f);
        float len = 0f;
        for (int i = 1; i <= steps; i++)
        {
            float t = i / (float)steps;
            Vector3 p = s.EvaluatePosition(t);
            len += Vector3.Distance(prev, p);
            prev = p;
        }
        return len;
    }

    float DistanceToT(Spline s, float distance, int steps)
    {
        distance = Mathf.Max(0f, distance);
        Vector3 prev = s.EvaluatePosition(0f);
        float accum = 0f;

        for (int i = 1; i <= steps; i++)
        {
            float t = i / (float)steps;
            Vector3 p = s.EvaluatePosition(t);
            float seg = Vector3.Distance(prev, p);

            if (accum + seg >= distance)
            {
                float remain = distance - accum;
                float segT = seg > 1e-5f ? (remain / seg) : 0f;
                float tPrev = (i - 1) / (float)steps;
                return Mathf.Lerp(tPrev, t, segT);
            }

            accum += seg;
            prev = p;
        }
        return 1f;
    }

    PrefabBoundsInfo CalculatePrefabBounds(GameObject targetPrefab)
    {
        PrefabBoundsInfo result = default;
        if (targetPrefab == null)
            return result;

        var root = targetPrefab.transform;
        Matrix4x4 rootWorldToLocal = root.worldToLocalMatrix;
        float minZ = float.PositiveInfinity;
        float maxZ = float.NegativeInfinity;
        bool hasBounds = false;

        var meshFilters = targetPrefab.GetComponentsInChildren<MeshFilter>(true);
        foreach (var filter in meshFilters)
        {
            var mesh = filter.sharedMesh;
            if (mesh == null)
                continue;

            Matrix4x4 meshToRoot = rootWorldToLocal * filter.transform.localToWorldMatrix;
            ExpandBounds(ref minZ, ref maxZ, meshToRoot, mesh.bounds);
            hasBounds = true;
        }

        var skinnedMeshes = targetPrefab.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        foreach (var skinned in skinnedMeshes)
        {
            var mesh = skinned.sharedMesh;
            if (mesh == null)
                continue;

            Matrix4x4 meshToRoot = rootWorldToLocal * skinned.transform.localToWorldMatrix;
            ExpandBounds(ref minZ, ref maxZ, meshToRoot, mesh.bounds);
            hasBounds = true;
        }

        var colliders = targetPrefab.GetComponentsInChildren<Collider>(true);
        foreach (var collider in colliders)
        {
            Bounds localBounds;
            if (collider is BoxCollider box)
            {
                localBounds = new Bounds(box.center, box.size);
            }
            else if (collider is SphereCollider sphere)
            {
                float diameter = sphere.radius * 2f;
                localBounds = new Bounds(sphere.center, new Vector3(diameter, diameter, diameter));
            }
            else if (collider is CapsuleCollider capsule)
            {
                Vector3 size = Vector3.one * (capsule.radius * 2f);
                switch (capsule.direction)
                {
                    case 0: size.x = capsule.height; break;
                    case 1: size.y = capsule.height; break;
                    default: size.z = capsule.height; break;
                }
                localBounds = new Bounds(capsule.center, size);
            }
            else if (collider is MeshCollider meshCollider && meshCollider.sharedMesh != null)
            {
                localBounds = meshCollider.sharedMesh.bounds;
            }
            else
            {
                continue;
            }

            Matrix4x4 colliderToRoot = rootWorldToLocal * collider.transform.localToWorldMatrix;
            ExpandBounds(ref minZ, ref maxZ, colliderToRoot, localBounds);
            hasBounds = true;
        }

        if (!hasBounds || float.IsNaN(minZ) || float.IsNaN(maxZ) || float.IsInfinity(minZ) || float.IsInfinity(maxZ))
            return result;

        if (minZ > maxZ)
        {
            float temp = minZ;
            minZ = maxZ;
            maxZ = temp;
        }

        result.IsValid = true;
        result.MinZ = minZ;
        result.MaxZ = maxZ;
        return result;
    }

    static void ExpandBounds(ref float minZ, ref float maxZ, Matrix4x4 localToRoot, Bounds localBounds)
    {
        Vector3 center = localBounds.center;
        Vector3 extents = localBounds.extents;

        for (int i = 0; i < 8; i++)
        {
            Vector3 corner = center;
            corner.x += ((i & 1) == 0) ? -extents.x : extents.x;
            corner.y += ((i & 2) == 0) ? -extents.y : extents.y;
            corner.z += ((i & 4) == 0) ? -extents.z : extents.z;

            Vector3 rootSpace = localToRoot.MultiplyPoint3x4(corner);
            float z = rootSpace.z;
            if (z < minZ) minZ = z;
            if (z > maxZ) maxZ = z;
        }
    }

    List<PlacementData> BuildPlacements(Spline s)
    {
        var placements = new List<PlacementData>();

        float totalLen = ApproximateLength(s, lengthSteps);
        if (totalLen <= 1e-4f)
            return placements;

        float usableStart = Mathf.Clamp(startOffset, 0f, totalLen);
        float usableEnd = Mathf.Clamp(totalLen - endOffset, 0f, totalLen);
        if (usableEnd < usableStart)
            usableEnd = usableStart;

        var bounds = CalculatePrefabBounds(prefab);
        float step = Mathf.Max(0.01f, snapSpacingToPrefab && bounds.IsValid ? bounds.Length : spacing);
        float pivotBackOffset = bounds.IsValid ? -bounds.MinZ : 0f;
        float segmentLength = bounds.IsValid ? Mathf.Max(0.01f, bounds.Length) : step;

        float maxContact = Mathf.Max(usableStart, usableEnd - segmentLength);
        const int MaxPlacements = 100000;
        var contacts = new List<float>();

        float contact = usableStart;
        int guard = 0;
        while (contact <= maxContact + 1e-3f && guard++ < MaxPlacements)
        {
            contacts.Add(contact);
            contact += step;
        }

        if (contacts.Count == 0)
        {
            contacts.Add(usableStart);
        }
        else if (stretchToEnd)
        {
            float lastContact = contacts[contacts.Count - 1];
            if (maxContact - lastContact > 1e-3f && contacts.Count < MaxPlacements)
            {
                contacts.Add(maxContact);
            }
            else
            {
                contacts[contacts.Count - 1] = maxContact;
            }
        }

        foreach (var contactDistance in contacts)
        {
            float pivotDistance = Mathf.Clamp(contactDistance + pivotBackOffset, 0f, totalLen);
            placements.Add(CreatePlacement(s, pivotDistance));
        }

        return placements;
    }

    PlacementData CreatePlacement(Spline s, float distance)
    {
        float t = DistanceToT(s, distance, mapSteps);

        var posLocal = (Vector3)s.EvaluatePosition(t);
        var tanLocal = ((Vector3)s.EvaluateTangent(t)).normalized;
        var upLocal = ((Vector3)s.EvaluateUpVector(t)).normalized;
        if (upLocal.sqrMagnitude < 1e-4f) upLocal = Vector3.up;

        var world = spline.transform.localToWorldMatrix;
        Vector3 pos = world.MultiplyPoint3x4(posLocal);
        Vector3 tan = world.MultiplyVector(tanLocal).normalized;
        Vector3 up = world.MultiplyVector(upLocal).normalized;

        return new PlacementData
        {
            position = pos,
            rotation = Quaternion.LookRotation(tan, up)
        };
    }

    void ApplyPlacements(List<PlacementData> placements, bool recordUndo)
    {
        var existing = new List<Transform>();
        for (int i = 0; i < parent.childCount; i++)
            existing.Add(parent.GetChild(i));

        int count = placements.Count;
        for (int i = 0; i < count; i++)
        {
            var placement = placements[i];
            Transform child;

            if (i < existing.Count)
            {
                child = existing[i];
            }
            else
            {
                var go = EditorUtil.InstantiatePrefab(prefab, parent);
                if (recordUndo)
                    Undo.RegisterCreatedObjectUndo(go, "Create Track Piece");
                child = go.transform;
            }

            child.gameObject.name = $"Piece_{i:000}";

            if (alignToTangent)
                child.SetPositionAndRotation(placement.position, placement.rotation);
            else
                child.position = placement.position;
        }

        for (int i = count; i < existing.Count; i++)
            EditorUtil.DestroyObject(existing[i].gameObject);
    }

    void UpdateLastSettings()
    {
        lastSettings = CaptureSettings();
        lastSplineHash = spline != null ? ComputeSplineHash(spline.Spline) : 0;
    }

    BuildSettings CaptureSettings()
    {
        return new BuildSettings
        {
            splineId = spline != null ? spline.GetInstanceID() : 0,
            prefabId = prefab != null ? prefab.GetInstanceID() : 0,
            parentId = parent != null ? parent.GetInstanceID() : 0,
            spacing = this.spacing,
            startOffset = this.startOffset,
            endOffset = this.endOffset,
            alignToTangent = this.alignToTangent,
            snapSpacingToPrefab = this.snapSpacingToPrefab,
            stretchToEnd = this.stretchToEnd,
            lengthSteps = this.lengthSteps,
            mapSteps = this.mapSteps
        };
    }

    int ComputeSplineHash(Spline s)
    {
        if (s == null)
            return 0;

        unchecked
        {
            int hash = s.Count;
            int samples = Mathf.Clamp(s.Count * 8, 16, 256);
            for (int i = 0; i <= samples; i++)
            {
                float t = samples > 0 ? i / (float)samples : 0f;
                hash = hash * 31 + HashVector3(s.EvaluatePosition(t));
                hash = hash * 31 + HashVector3(s.EvaluateTangent(t));
            }
            return hash;
        }
    }

    static int HashVector3(Vector3 value)
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 23 + Mathf.RoundToInt(value.x * 1000f);
            hash = hash * 23 + Mathf.RoundToInt(value.y * 1000f);
            hash = hash * 23 + Mathf.RoundToInt(value.z * 1000f);
            return hash;
        }
    }

    struct PlacementData
    {
        public Vector3 position;
        public Quaternion rotation;
    }

    struct PrefabBoundsInfo
    {
        public bool IsValid;
        public float MinZ;
        public float MaxZ;

        public float Length => MaxZ - MinZ;
    }

    struct BuildSettings
    {
        public int splineId;
        public int prefabId;
        public int parentId;
        public float spacing;
        public float startOffset;
        public float endOffset;
        public bool alignToTangent;
        public bool snapSpacingToPrefab;
        public bool stretchToEnd;
        public int lengthSteps;
        public int mapSteps;

        public bool IsValid => splineId != 0 && prefabId != 0;

        public bool Equals(BuildSettings other)
        {
            return splineId == other.splineId &&
                   prefabId == other.prefabId &&
                   parentId == other.parentId &&
                   Mathf.Approximately(spacing, other.spacing) &&
                   Mathf.Approximately(startOffset, other.startOffset) &&
                   Mathf.Approximately(endOffset, other.endOffset) &&
                   alignToTangent == other.alignToTangent &&
                   snapSpacingToPrefab == other.snapSpacingToPrefab &&
                   stretchToEnd == other.stretchToEnd &&
                   lengthSteps == other.lengthSteps &&
                   mapSteps == other.mapSteps;
        }

        public override bool Equals(object obj) => obj is BuildSettings other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = splineId;
                hash = hash * 31 + prefabId;
                hash = hash * 31 + parentId;
                hash = hash * 31 + Mathf.RoundToInt(spacing * 1000f);
                hash = hash * 31 + Mathf.RoundToInt(startOffset * 1000f);
                hash = hash * 31 + Mathf.RoundToInt(endOffset * 1000f);
                hash = hash * 31 + (alignToTangent ? 1 : 0);
                hash = hash * 31 + (snapSpacingToPrefab ? 1 : 0);
                hash = hash * 31 + (stretchToEnd ? 1 : 0);
                hash = hash * 31 + lengthSteps;
                hash = hash * 31 + mapSteps;
                return hash;
            }
        }
    }
}
static class EditorUtil
{
    public static void WithUndo(Object targetRoot, string label, System.Action act)
    {
        RegisterUndoHierarchy(targetRoot, label);
        try { act?.Invoke(); }
        finally
        {
            var scene = EditorSceneManager.GetActiveScene();
            if (scene.IsValid()) EditorSceneManager.MarkSceneDirty(scene);
        }
    }

    public static void WithSceneDirty(System.Action act)
    {
        try { act?.Invoke(); }
        finally
        {
            var scene = EditorSceneManager.GetActiveScene();
            if (scene.IsValid()) EditorSceneManager.MarkSceneDirty(scene);
        }
    }

    public static GameObject InstantiatePrefab(GameObject prefab, Transform parent = null)
    {
        var go = PrefabUtility.InstantiatePrefab(prefab, parent) as GameObject;
        if (go == null) go = Object.Instantiate(prefab, parent);
        return go;
    }

    public static void DestroyObject(GameObject go)
    {
        if (go == null) return;

        if (!Application.isPlaying)
            Object.DestroyImmediate(go);
        else
            Object.Destroy(go);
    }

    static void RegisterUndoHierarchy(Object targetRoot, string label)
    {
#if UNITY_2021_2_OR_NEWER
        Undo.RegisterFullObjectHierarchyUndo(targetRoot, label);
#else
        if (targetRoot == null)
            return;

        if (targetRoot is GameObject go)
        {
            Undo.RegisterCompleteObjectUndo(go, label);
            if (go.transform != null)
                Undo.RegisterCompleteObjectUndo(go.transform, label);
            foreach (Transform child in go.transform)
                RegisterUndoHierarchy(child.gameObject, label);
        }
        else if (targetRoot is Transform tf)
        {
            Undo.RegisterCompleteObjectUndo(tf, label);
            Undo.RegisterCompleteObjectUndo(tf.gameObject, label);
            foreach (Transform child in tf)
                RegisterUndoHierarchy(child.gameObject, label);
        }
        else
        {
            Undo.RegisterCompleteObjectUndo(targetRoot, label);
        }
#endif
    }
}
