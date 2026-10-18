using UnityEngine;
using UnityEngine.UI;
using System.Collections;

[DefaultExecutionOrder(-50)]
public class MapViewController : MonoBehaviour
{
    public static MapViewController Instance { get; private set; }

    [Header("Assign your existing panels")]
    public RectTransform localMapPanel;     // LocalMapPanel (full-screen behind)
    public RectTransform worldMapPanel;     // WorldMapPanel (animates mini <-> full)
    public RectTransform infoPanel;         // Info panel pinned top-left

    [Header("Minimap layout (over local map)")]
    public Vector2 minimapSize = new Vector2(260, 260);
    public Vector2 minimapMarginBR = new Vector2(24, 24); // right/bottom margins

    [Header("Ranges (relative, not GPS)")]
    public float localRangeKm = 1f;
    public float worldRangeKm = 3f;

    [Header("Transitions")]
    [Tooltip("Seconds for view transition (shrink/expand).")]
    public float transitionSeconds = 0.35f;
    public AnimationCurve ease = AnimationCurve.EaseInOut(0, 0, 1, 1);

    [Tooltip("Start with Local map full-screen and World minimap in corner.")]
    public bool startInLocalFull = true;

    [Header("Optional UI (toggle button)")]
    public Button toggleViewBtn;

    // state
    bool _worldIsFull = false;
    bool _animating = false;

    // cached anchors for mini vs full (relative to the root canvas)
    Vector2 _miniAnchorMin, _miniAnchorMax;
    readonly Vector2 _fullAnchorMin = Vector2.zero;
    readonly Vector2 _fullAnchorMax = Vector2.one;

    [Header("LocalContent hide (when World is full)")]
    public RectTransform localContent;           // assign your LocalContent
    public float localContentSlideExtra = 40f;   // extra pixels beyond bottom edge

    // --- Debounce fields ---
    float _lastRequestAt = -999f;
    const float _minGap = 0.05f; // 50ms

    void Awake()
    {
        if (Instance == null) Instance = this;
        else { Destroy(gameObject); return; }

        if (toggleViewBtn) toggleViewBtn.onClick.AddListener(ToggleFullView);

        if (localMapPanel) localMapPanel.gameObject.SetActive(true);
        if (worldMapPanel) worldMapPanel.gameObject.SetActive(true);

        PinInfoTopLeft();
        CacheMiniAnchors();

        // Place initial state
        _worldIsFull = !startInLocalFull;
        ApplyImmediate(_worldIsFull);
    }

    // ------------- Public API -------------

    public void ToggleFullView()
    {
        if (_animating) return;
        StartCoroutine(AnimateWorld(!_worldIsFull));
    }

    public float GetSpeedMultiplier(bool isInLocal) =>
        isInLocal ? 1f : Mathf.Max(0.01f, worldRangeKm / Mathf.Max(0.01f, localRangeKm));

    /// <summary>Show Local full screen (World as minimap), with debounce.</summary>
    public void ShowLocalFull()
    {
        if (_animating) return;
        if (Time.unscaledTime - _lastRequestAt < _minGap) return;
        _lastRequestAt = Time.unscaledTime;
        StartCoroutine(AnimateWorld(false)); // world -> mini
    }

    /// <summary>Show World full screen, with debounce.</summary>
    public void ShowWorldFull()
    {
        if (_animating) return;
        if (Time.unscaledTime - _lastRequestAt < _minGap) return;
        _lastRequestAt = Time.unscaledTime;
        StartCoroutine(AnimateWorld(true));  // world -> full
    }

    // ------------- Internals -------------

    void PinInfoTopLeft()
    {
        if (!infoPanel) return;
        infoPanel.anchorMin = new Vector2(0, 1);
        infoPanel.anchorMax = new Vector2(0, 1);
        infoPanel.pivot     = new Vector2(0, 1);
        infoPanel.anchoredPosition = new Vector2(16, -16);
    }

    RectTransform RootCanvasRT()
    {
        return localMapPanel ? localMapPanel.GetComponentInParent<Canvas>().GetComponent<RectTransform>() : null;
    }

    void CacheMiniAnchors()
    {
        var canvas = RootCanvasRT();
        if (!canvas || !worldMapPanel) return;

        var full = canvas.rect;
        // mini rect in pixels (bottom-right)
        Rect mini = new Rect(
            full.width - minimapMarginBR.x - minimapSize.x,
            minimapMarginBR.y,
            minimapSize.x,
            minimapSize.y
        );

        // convert to anchors (0..1)
        _miniAnchorMin = new Vector2(mini.xMin / full.width, mini.yMin / full.height);
        _miniAnchorMax = new Vector2(mini.xMax / full.width, mini.yMax / full.height);
    }

    void ApplyImmediate(bool worldFull)
    {
        if (!localMapPanel || !worldMapPanel) return;

        // Local always stretched full-screen; we just fade it when world is full
        StretchToFull(localMapPanel);
        var localCg = RequireCG(localMapPanel);
        localCg.alpha = worldFull ? 0.15f : 1f;      // dim when world is full
        localCg.blocksRaycasts = !worldFull;
        localCg.interactable   = !worldFull;

        // Slide LocalContent fully below screen if world is full
        SetLocalContentY(worldFull ? 1f : 0f);

        // World either full anchors or mini anchors
        if (worldFull)
        {
            worldMapPanel.anchorMin = _fullAnchorMin;
            worldMapPanel.anchorMax = _fullAnchorMax;
            worldMapPanel.SetAsLastSibling();    // render above local
        }
        else
        {
            worldMapPanel.anchorMin = _miniAnchorMin;
            worldMapPanel.anchorMax = _miniAnchorMax;
            localMapPanel.SetAsLastSibling();    // local above world-mini
        }
        worldMapPanel.pivot = new Vector2(0.5f, 0.5f);
        worldMapPanel.offsetMin = Vector2.zero;
        worldMapPanel.offsetMax = Vector2.zero;
    }

    IEnumerator AnimateWorld(bool toFull)
    {
        if (!worldMapPanel || !localMapPanel)
            yield break;

        // --- FIX #1: never expand World to full while the local player is inside a location ---
        var gm = GameManager.Instance;
        if (toFull &&
            gm != null &&
            gm.players != null &&
            gm.players.TryGetValue(gm.localPlayerId, out var me) &&
            me != null &&
            me.insideLocationIndex.HasValue)
        {
            toFull = false; // local view has priority
        }

        _animating = true;
        _worldIsFull = toFull;

        // make sure our mini anchors are up-to-date if resolution/aspect changed
        CacheMiniAnchors();

        // start/end
        Vector2 aMin = worldMapPanel.anchorMin;
        Vector2 aMax = worldMapPanel.anchorMax;
        Vector2 bMin = toFull ? _fullAnchorMin : _miniAnchorMin;
        Vector2 bMax = toFull ? _fullAnchorMax : _miniAnchorMax;

        // fade local in/out while world animates
        var localCg = RequireCG(localMapPanel);
        float startAlpha = localCg.alpha;
        float endAlpha   = toFull ? 0.15f : 1f;

        // ensure draw order feels right
        if (toFull) worldMapPanel.SetAsLastSibling();
        else        localMapPanel.SetAsLastSibling();

        float t = 0f;
        float dur = Mathf.Max(0.01f, transitionSeconds);
        while (t < 1f)
        {
            t += Time.deltaTime / dur;
            float k = ease.Evaluate(Mathf.Clamp01(t));

            worldMapPanel.anchorMin = Vector2.Lerp(aMin, bMin, k);
            worldMapPanel.anchorMax = Vector2.Lerp(aMax, bMax, k);
            worldMapPanel.offsetMin = Vector2.zero;
            worldMapPanel.offsetMax = Vector2.zero;

            localCg.alpha = Mathf.Lerp(startAlpha, endAlpha, k);

            // --- FIX #2: slide LocalContent in the correct direction ---
            // k goes 0→1; for toFull=false we want slide to move back down from 1→0.
            float slide = toFull ? k : (1f - k);
            SetLocalContentY(slide);

            yield return null;
        }

        // snap final
        ApplyImmediate(toFull);
        _animating = false;
    }

    // --- LocalContent slide helper ---
    void SetLocalContentY(float t01)
    {
        if (!localContent || !localMapPanel) return;

        // t01 = 0 → Local full (content at Y = 0)
        // t01 = 1 → World full (content moved completely below the screen)
        var canvas = localMapPanel.GetComponentInParent<Canvas>().GetComponent<RectTransform>();
        float off = canvas.rect.height + localContentSlideExtra;
        float y   = Mathf.Lerp(0f, -off, t01);

        var pos = localContent.anchoredPosition;
        pos.y   = y;
        localContent.anchoredPosition = pos;

        // fade & disable input as it leaves
        var cg = localContent.GetComponent<CanvasGroup>();
        if (!cg) cg = localContent.gameObject.AddComponent<CanvasGroup>();
        cg.alpha          = 1f - t01;
        cg.interactable   = (t01 < 0.02f);
        cg.blocksRaycasts = (t01 < 0.02f);
    }

    // ---------- helpers ----------
    static void StretchToFull(RectTransform rt)
    {
        if (!rt) return;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot     = new Vector2(0.5f, 0.5f);
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
        rt.anchoredPosition3D = Vector3.zero;
        rt.localScale = Vector3.one;
    }

    static CanvasGroup RequireCG(RectTransform rt)
    {
        var cg = rt.GetComponent<CanvasGroup>();
        if (!cg) cg = rt.gameObject.AddComponent<CanvasGroup>();
        return cg;
    }
}
