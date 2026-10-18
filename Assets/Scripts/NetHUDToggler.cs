using UnityEngine;
using TMPro; // only for the TMP_Text type

public class NetHUDToggler : MonoBehaviour
{
    [Header("Hook up either (A) or (B)")]
    [Tooltip("(A) Reference to your NetDebugHUD component (it has a Target field to the text).")]
    public MonoBehaviour netDebugHUD;   // put your NetDebugHUD component here (keep type loose)

    [Tooltip("(B) Direct reference to the TMP text object, if you prefer.")]
    public TMP_Text hudText;

    [Header("Options")]
    public bool startHidden = false;           // start with HUD hidden
    public KeyCode hotkey = KeyCode.H;         // optional keyboard toggle (Editor/testing)

    GameObject _textGO;

    void Awake()
    {
        // Resolve the text GameObject from either path
        if (hudText != null)
        {
            _textGO = hudText.gameObject;
        }
        else if (netDebugHUD != null)
        {
            // Try to read a field or property named "Target" on NetDebugHUD that points to a TMP_Text
            var t = netDebugHUD.GetType().GetField("Target")?.GetValue(netDebugHUD) as TMP_Text;
            if (t == null)
                t = netDebugHUD.GetType().GetProperty("Target")?.GetValue(netDebugHUD) as TMP_Text;

            if (t != null) _textGO = t.gameObject;
        }

        if (_textGO == null)
        {
            Debug.LogWarning("[NetHUDToggler] No HUD text found. Drag NetHUDText into 'hudText' or NetDebugHUD into 'netDebugHUD'.");
            return;
        }

        SetVisible(!startHidden);
    }

    void Update()
    {
        if (hotkey != KeyCode.None && Input.GetKeyDown(hotkey))
            Toggle();
    }

    public void Toggle()
    {
        if (_textGO == null) return;
        SetVisible(!_textGO.activeSelf);
    }

    public void Show()  => SetVisible(true);
    public void Hide()  => SetVisible(false);

    void SetVisible(bool on)
    {
        if (_textGO != null) _textGO.SetActive(on);

        // Optional: also pause/resume the NetDebugHUD script so it does no work when hidden
        if (netDebugHUD != null) netDebugHUD.enabled = on;
    }
}
