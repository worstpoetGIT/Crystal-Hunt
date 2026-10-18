// Assets/Editor/ExportImportantInspectorSummary.cs
#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class ExportImportantInspectorSummary
{
    [MenuItem("Tools/Export/Export Important Inspector Summary")]
    public static void Export()
    {
        var sb = new StringBuilder(1 << 19); // ~0.5MB initial capacity
        sb.AppendLine($"=== Important Inspector Summary ===  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Unity {Application.unityVersion}  Project: {Application.productName}");
        sb.AppendLine();

        int sceneCount = SceneManager.sceneCount;
        for (int s = 0; s < sceneCount; s++)
        {
            var scene = SceneManager.GetSceneAt(s);
            if (!scene.isLoaded) continue;

            sb.AppendLine($"--- Scene: {scene.name} ({scene.path}) ---");
            var roots = scene.GetRootGameObjects();
            foreach (var root in roots)
                DumpGO(root, sb, scene, 0);
            sb.AppendLine();
        }

        const string folder = "Assets/InspectorDumps";
        if (!AssetDatabase.IsValidFolder(folder))
            AssetDatabase.CreateFolder("Assets", "InspectorDumps");

        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string file = $"{folder}/ImportantSceneSummary_{Sanitize(timestamp)}.txt";
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
        AssetDatabase.Refresh();
        EditorUtility.RevealInFinder(file);
        Debug.Log($"[ExportImportantInspectorSummary] Wrote: {file}");
    }

    private static void DumpGO(GameObject go, StringBuilder sb, Scene scene, int depth)
    {
        // Skip objects not belonging to this scene (e.g., DontDestroyOnLoad)
        if (go.scene != scene) return;

        string indent = new string(' ', depth * 2);
        string path = GetHierarchyPath(go.transform);

        // Header
        sb.AppendLine($"{indent}GameObject: {go.name}");
        sb.AppendLine($"{indent}  Path: {path}");
        sb.AppendLine($"{indent}  ActiveSelf: {go.activeSelf}   ActiveInHierarchy: {go.activeInHierarchy}");
        sb.AppendLine($"{indent}  Tag: {go.tag}   Layer: {LayerMask.LayerToName(go.layer)} ({go.layer})");

        // Prefab info (if instance)
        var source = PrefabUtility.GetCorrespondingObjectFromOriginalSource(go);
        if (source != null)
        {
            string assetPath = AssetDatabase.GetAssetPath(source);
            var status = PrefabUtility.GetPrefabInstanceStatus(go);
            sb.AppendLine($"{indent}  Prefab: {source.name}  [{status}]  AssetPath: {assetPath}");
        }

        // Transform (local)
        var t = go.transform;
        sb.AppendLine($"{indent}  Transform:");
        sb.AppendLine($"{indent}    LocalPosition: {t.localPosition}");
        sb.AppendLine($"{indent}    LocalRotation (Euler): {t.localRotation.eulerAngles}");
        sb.AppendLine($"{indent}    LocalScale: {t.localScale}");

        // Components (only type + enabled + object references)
        var comps = go.GetComponents<Component>();
        for (int i = 0; i < comps.Length; i++)
        {
            var c = comps[i];
            if (c == null)
            {
                sb.AppendLine($"{indent}  Component[{i}]: **Missing Script**");
                continue;
            }

            string typeName = c.GetType().Name;
            string enabledStr = (c is Behaviour b) ? $" enabled={b.enabled}" : "";
            sb.AppendLine($"{indent}  Component[{i}]: {typeName}{enabledStr}");

            try
            {
                var so = new SerializedObject(c);
                so.UpdateIfRequiredOrScript();

                var it = so.GetIterator();
                bool enterChildren = true;
                while (it.NextVisible(enterChildren))
                {
                    enterChildren = false;

                    // Only print object references (assets or scene objects)
                    if (it.propertyType != SerializedPropertyType.ObjectReference) continue;
                    if (it.propertyPath == "m_Script") continue; // skip script backref

                    var obj = it.objectReferenceValue;
                    string refStr = ObjectRefToString(obj);
                    if (!string.IsNullOrEmpty(refStr))
                        sb.AppendLine($"{indent}    {it.displayName}: {refStr}");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"{indent}    <error reading refs: {ex.Message}>");
            }
        }

        sb.AppendLine();

        // Recurse (include inactive)
        for (int i = 0; i < t.childCount; i++)
            DumpGO(t.GetChild(i).gameObject, sb, scene, depth + 1);
    }

    private static string ObjectRefToString(UnityEngine.Object obj)
    {
        if (obj == null) return "None";

        // Scene object?
        if (obj is Component comp)
            return $"{comp.GetType().Name} on {GetHierarchyPath(comp.transform)} (Scene)";
        if (obj is GameObject go)
            return $"{go.name} ({GetHierarchyPath(go.transform)}) (Scene)";

        // Asset or sub-asset
        string path = AssetDatabase.GetAssetPath(obj);
        if (!string.IsNullOrEmpty(path))
            return $"{obj.name} [{obj.GetType().Name}] @ {path}";

        // Fallback
        return $"{obj.name} [{obj.GetType().Name}]";
    }

    private static string GetHierarchyPath(Transform tr)
    {
        var sb = new StringBuilder();
        while (tr != null)
        {
            if (sb.Length == 0) sb.Insert(0, tr.name);
            else sb.Insert(0, tr.name + "/");
            tr = tr.parent;
        }
        return sb.ToString();
    }

    private static string Sanitize(string s)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        return s;
    }
}
#endif
