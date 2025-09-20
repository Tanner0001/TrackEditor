#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public class TrackBuilderMan : EditorWindow
{
    string userText = "Type here...";
    int counter = 0;
    Vector2 scroll;

    [MenuItem("Tools/Track Builder Man")]
    public static void Open()
    {
        var w = EditorWindow.GetWindow<TrackBuilderMan>();
        w.titleContent = new GUIContent("Track Builder");
        w.Show();
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Hello Testing editor", EditorStyles.boldLabel);
        userText = EditorGUILayout.TextField("Test", userText);

        if (GUILayout.Button("Create Button"))
        {
            counter++;
            Debug.Log($"[TrackBuilderMan] Button clicked {counter} times. Input='{userText}'");
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Scrollable Box");
        scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.Height(120));
        GUILayout.Label("Any custom UI can go here.");
        EditorGUILayout.EndScrollView();
    }
}


#endif
