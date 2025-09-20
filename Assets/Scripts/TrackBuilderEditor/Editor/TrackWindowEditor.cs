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

    int lengthSteps = 512; 
    int mapSteps = 2048;    


   
    Vector2 scroll;

    [MenuItem("Tools/RaceTrack Builder")]
    public static void Open() => GetWindow<TrackWindowEditor>("RaceTrack Builder").Show();

    void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);

        EditorGUILayout.LabelField("Source", EditorStyles.boldLabel);
        spline = (SplineContainer)EditorGUILayout.ObjectField("Spline", spline, typeof(SplineContainer), true);
        prefab = (GameObject)EditorGUILayout.ObjectField("Prefab", prefab, typeof(GameObject), false);
        parent = (Transform)EditorGUILayout.ObjectField("Parent (optional)", parent, typeof(Transform), true);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Placement", EditorStyles.boldLabel);
        spacing = Mathf.Max(0.01f, EditorGUILayout.FloatField("Spacing (m)", spacing));
        startOffset = Mathf.Max(0f, EditorGUILayout.FloatField("Start Offset (m)", startOffset));
        endOffset = Mathf.Max(0f, EditorGUILayout.FloatField("End Offset (m)", endOffset));
        alignToTangent = EditorGUILayout.Toggle("Align to Tangent", alignToTangent);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Quality", EditorStyles.boldLabel);
        lengthSteps = Mathf.Clamp(EditorGUILayout.IntField("Length Steps", lengthSteps), 64, 4096);
        mapSteps = Mathf.Clamp(EditorGUILayout.IntField("Map Steps", mapSteps), 128, 8192);

        EditorGUILayout.Space();
        using (new EditorGUILayout.HorizontalScope())
        {
            GUI.enabled = (spline != null && prefab != null);
            if (GUILayout.Button("Generate", GUILayout.Height(32)))
                Generate();

            GUI.enabled = (parent != null);
            if (GUILayout.Button("Clear Parent Children", GUILayout.Height(32)))
                ClearParentChildren();
            GUI.enabled = true;
        }

        EditorGUILayout.HelpBox(
            "Draw a Spline in your scene, choose a prefab, set spacing, then Generate.\n" +
            "Use Parent to keep generated objects grouped, if your reading this you might have also committed a crime :).",
            MessageType.Info);

        EditorGUILayout.EndScrollView();
    }

    void Generate()
    {
        if (spline == null || prefab == null)
        {
            // I would like build this out to be something that will tell the user this issue and not to console 
            // this is a note for all Debug Warnings.

            Debug.LogWarning("Assign a Spline and a Prefab.");
            return;
        }

        var s = spline.Spline;
        if (s == null || s.Count < 2) { Debug.LogWarning("Spline is empty."); return; }

        if (parent == null)
        {
            var holder = new GameObject("PrefabBuilt_");
            parent = holder.transform;
        }

        // Wrap in Undo and mark dirty
        EditorUtil.WithUndo(parent, "Generate Along Spline", () =>
        {
            float totalLen = ApproximateLength(s, lengthSteps);
            float usable = Mathf.Max(0f, totalLen - startOffset - endOffset);
            if (usable <= 0.01f) return;

            var world = spline.transform.localToWorldMatrix;

            for (float d = 0f; d <= usable; d += spacing)
            {
                float t = DistanceToT(s, startOffset + d, mapSteps);


            
                var posLocal = (Vector3)s.EvaluatePosition(t);
                var tanLocal = ((Vector3)s.EvaluateTangent(t)).normalized;
                var upLocal = ((Vector3)s.EvaluateUpVector(t)).normalized;
                if (upLocal.sqrMagnitude < 1e-4f) upLocal = Vector3.up;

                // to world
                Vector3 pos = world.MultiplyPoint3x4(posLocal);
                Vector3 tan = world.MultiplyVector(tanLocal).normalized;
                Vector3 up = world.MultiplyVector(upLocal).normalized;



                var rot = Quaternion.LookRotation(tan, up);


                var go = EditorUtil.InstantiatePrefab(prefab, parent);
                go.name = $"Piece_{parent.childCount:000}";

                if (alignToTangent)
                    go.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(tan, up));
                else
                    go.transform.position = pos;
            }
        });
    }

    void ClearParentChildren()
    {
        if (parent == null) return;
        EditorUtil.WithUndo(parent, "Clear Children", () =>
        {
            var toDelete = new List<GameObject>();
            foreach (Transform c in parent) toDelete.Add(c.gameObject);

            foreach (var go in toDelete)
            {
                if (!Application.isPlaying) DestroyImmediate(go);
                else Destroy(go);
            }
        });
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
}
static class EditorUtil
{
    public static void WithUndo(Object targetRoot, string label, System.Action act)
    {
        Undo.RegisterFullObjectHierarchyUndo(targetRoot, label);
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
}