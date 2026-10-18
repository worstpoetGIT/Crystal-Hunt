using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

public class TouchDpad : MonoBehaviour
{
    public static TouchDpad Current;

    [Header("Editor Testing")]
    [Tooltip("If true, the D-pad is visible & usable in the Unity Editor.")]
    public bool showInEditor = false;

    bool up, down, left, right;

    void Awake()
    {
        Current = this;

        // Show on real devices; in Editor only if you opt-in.
#if UNITY_EDITOR
        gameObject.SetActive(showInEditor || Application.isMobilePlatform);
#else
        gameObject.SetActive(true);
#endif
    }

    public void SetUp(bool v)    { up = v; }
    public void SetDown(bool v)  { down = v; }
    public void SetLeft(bool v)  { left = v; }
    public void SetRight(bool v) { right = v; }

    public Vector2 Read()
    {
        Vector2 v = new((left ? -1 : 0) + (right ? 1 : 0),
                        (down ? -1 : 0) + (up ? 1 : 0));
        if (v.sqrMagnitude > 1f) v.Normalize();
        return v;
    }

#if UNITY_EDITOR
    // Handy toggle under Tools menu (Editor only)
    [MenuItem("Tools/Touch D-pad/Toggle Show In Editor %#d")]
    static void ToggleShowInEditor()
    {
        var dpad = FindObjectOfType<TouchDpad>(true);
        if (!dpad) { Debug.LogWarning("No TouchDpad found in scene."); return; }
        dpad.showInEditor = !dpad.showInEditor;
        dpad.gameObject.SetActive(dpad.showInEditor || Application.isMobilePlatform);
        EditorUtility.SetDirty(dpad);
        Debug.Log("[TouchDpad] ShowInEditor = " + dpad.showInEditor);
    }
#endif
}
