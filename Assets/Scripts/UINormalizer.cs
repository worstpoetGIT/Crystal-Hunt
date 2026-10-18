using System;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

/// Robust, conservative UI normalizer.
/// - Keeps your layout; only fixes mismatch that cause giant/misaligned UI.
/// - Works in Edit & Play (no prefab edits are required).
[ExecuteAlways]
public class UINormalizer : MonoBehaviour
{
    [Header("Targets (leave defaults if your names match)")]
    [Tooltip("Root Canvas that drives the whole UI")]
    public Canvas rootCanvas;                          // auto-find if null
    [Tooltip("Container under the root that used to have AspectRatioFitter")]
    public RectTransform safeFrame;                    // auto-find if null

    [Header("Scaler settings (shared by all Canvases)")]
    public Vector2 referenceResolution = new(1920, 1080);
    [Range(0,1)] public float matchWidthOrHeight = 0.5f;

    [Header("Behavior")]
    [Tooltip("Disable AspectRatioFitter on SafeFrame if present (prevents fighting the scaler).")]
    public bool disableAspectRatioFitterOnSafeFrame = true;
    [Tooltip("Force scale=1 on Canvas + SafeFrame (prevents multiplicative scaling).")]
    public bool forceScaleOne = true;
    [Tooltip("Ensure every Canvas has a CanvasScaler with the same settings.")]
    public bool unifyAllCanvasScalers = true;
    [Tooltip("Give common overlays predictable sorting orders (optional).")]
    public bool setSortingOrders = true;

    [Header("Overlay orders (only used if setSortingOrders=true)")]
    public int worldOrder        = 0;
    public int hudOrder          = 50;   // Toggle/NetHUD/Info
    public int optionsMenuOrder  = 150;  // Option menu panel
    public int encounterOrder    = 200;  // Duel/Riddle modal

    // Names to look for (change if your scene uses different names)
    static readonly string[] kHudNames       = { "NetDebugHUD", "InfoPanelCavas", "ToggleCanvas" };
    static readonly string[] kOptionsNames   = { "OptionMenuCavas" };
    static readonly string[] kEncounterNames = { "EncounterCanvas" };

    void OnEnable()  => NormalizeUI();
#if UNITY_EDITOR
    void Update()    { if (!Application.isPlaying) NormalizeUI(); } // keep tidy in edit mode
#endif

    void NormalizeUI()
    {
        try
        {
            if (!rootCanvas)
                rootCanvas = GetComponentsInChildren<Canvas>(true).FirstOrDefault(c => c.transform.parent == transform) ??
                             GetComponentInChildren<Canvas>(true);

            if (!rootCanvas) return;

            // 1) Root canvas scaler
            var rootScaler = rootCanvas.GetComponent<CanvasScaler>() ?? rootCanvas.gameObject.AddComponent<CanvasScaler>();
            if (rootScaler.uiScaleMode != CanvasScaler.ScaleMode.ScaleWithScreenSize ||
                rootScaler.referenceResolution != referenceResolution ||
                Math.Abs(rootScaler.matchWidthOrHeight - matchWidthOrHeight) > 0.0001f)
            {
                rootScaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                rootScaler.referenceResolution = referenceResolution;
                rootScaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
                rootScaler.matchWidthOrHeight = matchWidthOrHeight;
                Log($"Configured root CanvasScaler → {referenceResolution.x}×{referenceResolution.y}, match={matchWidthOrHeight}");
            }

            // 2) SafeFrame: scale & AspectRatioFitter
            if (!safeFrame)
            {
                var t = transform.Find("Canvas/SafeFrame") ?? transform.Find("SafeFrame");
                if (t) safeFrame = t as RectTransform;
            }
            if (safeFrame)
            {
                if (forceScaleOne && safeFrame.localScale != Vector3.one)
                {
                    safeFrame.localScale = Vector3.one;
                    Log("SafeFrame scale → (1,1,1)");
                }
                if (disableAspectRatioFitterOnSafeFrame)
                {
                    var arf = safeFrame.GetComponent<AspectRatioFitter>();
                    if (arf && arf.enabled)
                    {
                        arf.enabled = false;
                        Log("Disabled AspectRatioFitter on SafeFrame (CanvasScaler now drives layout).");
                    }
                }
            }

            // 3) Root scale=1 (crucial)
            if (forceScaleOne && rootCanvas.transform.localScale != Vector3.one)
            {
                rootCanvas.transform.localScale = Vector3.one;
                Log("Root Canvas scale → (1,1,1)");
            }

            // 4) Unify every Canvas scaler so nested canvases behave consistently
            if (unifyAllCanvasScalers)
            {
                var all = GetComponentsInChildren<Canvas>(true);
                foreach (var c in all)
                {
                    var sc = c.GetComponent<CanvasScaler>() ?? c.gameObject.AddComponent<CanvasScaler>();
                    bool changed = false;

                    if (sc.uiScaleMode != CanvasScaler.ScaleMode.ScaleWithScreenSize)
                    { sc.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; changed = true; }
                    if (sc.referenceResolution != referenceResolution)
                    { sc.referenceResolution = referenceResolution; changed = true; }
                    if (sc.screenMatchMode != CanvasScaler.ScreenMatchMode.MatchWidthOrHeight)
                    { sc.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight; changed = true; }
                    if (Math.Abs(sc.matchWidthOrHeight - matchWidthOrHeight) > 0.0001f)
                    { sc.matchWidthOrHeight = matchWidthOrHeight; changed = true; }

                    if (forceScaleOne && c.transform.localScale != Vector3.one)
                    { c.transform.localScale = Vector3.one; changed = true; }

                    if (changed && c != rootCanvas)
                        Log($"Unified scaler on '{c.name}'");
                }
            }

            // 5) Optional: predictable overlay orders (non-destructive)
            if (setSortingOrders)
            {
                SetOrderForGroup(kEncounterNames, encounterOrder);
                SetOrderForGroup(kOptionsNames,   optionsMenuOrder);
                SetOrderForGroup(kHudNames,       hudOrder);
                // everything else stays at worldOrder (0) if not overrideSorting
            }
        }
        catch (Exception ex)
        {
            Log("UINormalizer error: " + ex.Message);
        }
    }

    void SetOrderForGroup(string[] names, int order)
    {
        foreach (var n in names)
        {
            var t = transform.GetComponentsInChildren<Canvas>(true)
                             .FirstOrDefault(c => string.Equals(c.name, n, StringComparison.OrdinalIgnoreCase));
            if (!t) continue;

            t.overrideSorting = true;
            t.sortingOrder = order;

            // make sure child raycasters work as expected
            if (!t.GetComponent<GraphicRaycaster>())
                t.gameObject.AddComponent<GraphicRaycaster>();

            Log($"Canvas '{t.name}' → sortingOrder {order}");
        }
    }

    void Log(string msg)
    {
        Debug.Log("[UINormalizer] " + msg);
        var hud = FindObjectOfType<NetDebugHUD>(true);
        if (hud) NetDebugHUD.Log(msg);
    }
}
