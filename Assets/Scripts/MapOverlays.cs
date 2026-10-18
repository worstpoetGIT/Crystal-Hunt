using UnityEngine;
using UnityEngine.UI;

/// Draws non-interactive overlays without changing any existing layout or transitions.
[DefaultExecutionOrder(10)]  // run after your layout code so we read final positions
public class MapOverlays : MonoBehaviour
{
    [Header("References (assign in Inspector)")]
    public RectTransform worldMapPanel;         // your existing WorldMapPanel
    public RectTransform localMapPanel;         // your existing LocalMapPanel
    public RectTransform localContent;          // the 'LocalContent' container under LocalMapPanel

    [Tooltip("Optional: empty child under WorldMapPanel for overlays. If null, we create one at runtime.")]
    public RectTransform worldOverlayLayer;

    [Header("World overlay style")]
    public Color allLocationsColor = new Color(1,1,1,0.3f);
    public Color currentLocationColor = new Color(0.15f, 0.75f, 1f, 0.9f);
    public float worldBorderThickness = 2f;

    [Header("Entry point marker (on world map)")]
    public bool showEntryDot = true;
    public Color entryDotColor = new Color(0.15f, 0.75f, 1f, 1f);
    public float entryDotSize = 10f;

    [Header("Local overlay style")]
    public bool showLocalPlayBorder = true;
    public Color localPlayBorderColor = new Color(1,1,1,0.35f);
    public float localPlayBorderThickness = 2f;


        [Header("Throne marker")]
    public bool showThrone = true;
    public Color throneDotColor = new Color(0.95f, 0.2f, 0.2f, 1f);
    public float throneDotSize = 14f;

    public bool showThroneRing = true;
    public Color throneRingColor = new Color(0.95f, 0.2f, 0.2f, 0.85f);
    public float throneRingThickness = 2.5f;

    // names for pooled/created throne children
    const string kWorldThroneDot  = "WThroneDot";
    const string kWorldThroneRing = "WThroneRing";


    // pooled children to avoid GC
    const string kWorldLocPrefix = "WLoc_";
    const string kWorldDotName   = "WEntryDot";
    const string kLocalBorder    = "LocalContent_Border";

    void Start()
    {
        // Ensure overlay root exists (does NOT change existing panel anchors/parents)
        if (worldOverlayLayer == null && worldMapPanel != null)
        {
            var go = new GameObject("WorldOverlay", typeof(RectTransform));
            worldOverlayLayer = go.GetComponent<RectTransform>();
            worldOverlayLayer.SetParent(worldMapPanel, false);
            worldOverlayLayer.anchorMin = Vector2.zero;
            worldOverlayLayer.anchorMax = Vector2.one;
            worldOverlayLayer.offsetMin = Vector2.zero;
            worldOverlayLayer.offsetMax = Vector2.zero;
            worldOverlayLayer.pivot = new Vector2(0.5f, 0.5f);
        }
    }

    void LateUpdate()
    {
        // Repaint every frame so overlays follow your existing animation/resizing.
        DrawWorldLocations();
        DrawLocalContentBorder();
    }

    void DrawWorldLocations()
    {
        if (worldMapPanel == null || worldOverlayLayer == null) return;
        var gm = GameManager.Instance;
        if (gm == null || gm.locationRects == null || gm.locationRects.Count == 0) return;

        gm.GetWorldMinMax(out var wmin, out var wmax);

        // Clear old children safely (only ours)
        for (int i = worldOverlayLayer.childCount - 1; i >= 0; i--)
            Destroy(worldOverlayLayer.GetChild(i).gameObject);

        // current location (if any)
        int currentLi = -1;
        if (gm.players.TryGetValue(gm.localPlayerId, out var me) && me.insideLocationIndex.HasValue)
            currentLi = me.insideLocationIndex.Value;

        // Draw each location as a rectangle that respects world bounds → normalized anchors
        for (int i = 0; i < gm.locationRects.Count; i++)
        {
            var lr = gm.locationRects[i];

            Vector2 aMin = new Vector2(
                Mathf.InverseLerp(wmin.x, wmax.x, lr.bl.x),
                Mathf.InverseLerp(wmin.y, wmax.y, lr.bl.y)
            );
            Vector2 aMax = new Vector2(
                Mathf.InverseLerp(wmin.x, wmax.x, lr.tr.x),
                Mathf.InverseLerp(wmin.y, wmax.y, lr.tr.y)
            );

            var go = new GameObject(kWorldLocPrefix + i, typeof(RectTransform), typeof(Image), typeof(Outline));
            go.transform.SetParent(worldOverlayLayer, false);

            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = aMin;
            rt.anchorMax = aMax;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            rt.pivot = new Vector2(0.5f, 0.5f);

            var img = go.GetComponent<Image>();
            img.color = new Color(0, 0, 0, 0);      // transparent fill
            img.raycastTarget = false;

            var ol = go.GetComponent<Outline>();
            bool isCurrent = (i == currentLi);
            ol.effectColor = isCurrent ? currentLocationColor : allLocationsColor;
            ol.effectDistance = Vector2.one * (isCurrent ? worldBorderThickness * 1.75f : worldBorderThickness);
            ol.useGraphicAlpha = false;
        }

        // Entry point dot (exact position where the player entered the current location)
        if (showEntryDot && gm.players.TryGetValue(gm.localPlayerId, out var me2) && me2.lastEnterPos.HasValue)
        {
            Vector2 pos = me2.lastEnterPos.Value;
            Vector2 n = new Vector2(
                Mathf.InverseLerp(wmin.x, wmax.x, pos.x),
                Mathf.InverseLerp(wmin.y, wmax.y, pos.y)
            );

            var dot = new GameObject(kWorldDotName, typeof(RectTransform), typeof(Image));
            dot.transform.SetParent(worldOverlayLayer, false);
            var rt = dot.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = n;
            rt.sizeDelta = new Vector2(entryDotSize, entryDotSize);
            var img = dot.GetComponent<Image>();
            img.color = entryDotColor;
            img.raycastTarget = false;
        }
        
                // --- Throne marker ---
        if (showThrone)
        {
            Vector2 t = gm.throneRoom;
            Vector2 tn = new Vector2(
                Mathf.InverseLerp(wmin.x, wmax.x, t.x),
                Mathf.InverseLerp(wmin.y, wmax.y, t.y)
            );

            // ring (optional)
            if (showThroneRing)
            {
                var ring = new GameObject(kWorldThroneRing, typeof(RectTransform), typeof(Image), typeof(Outline));
                ring.transform.SetParent(worldOverlayLayer, false);
                var rtR = ring.GetComponent<RectTransform>();
                rtR.anchorMin = rtR.anchorMax = tn;
                rtR.sizeDelta = new Vector2(throneDotSize * 2.2f, throneDotSize * 2.2f);
                var imgR = ring.GetComponent<Image>();
                imgR.color = new Color(0,0,0,0);
                imgR.raycastTarget = false;
                var olR = ring.GetComponent<Outline>();
                olR.effectColor = throneRingColor;
                olR.effectDistance = Vector2.one * throneRingThickness;
                olR.useGraphicAlpha = false;
            }

            // filled dot
            var dot = new GameObject(kWorldThroneDot, typeof(RectTransform), typeof(Image));
            dot.transform.SetParent(worldOverlayLayer, false);
            var rt = dot.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = tn;
            rt.sizeDelta = new Vector2(throneDotSize, throneDotSize);
            var img = dot.GetComponent<Image>();
            img.color = throneDotColor;
            img.raycastTarget = false;
        }

    }
public void ClearAllOverlays()
{
    if (worldOverlayLayer == null) return;
    for (int i = worldOverlayLayer.childCount - 1; i >= 0; i--)
        Destroy(worldOverlayLayer.GetChild(i).gameObject);
}

   void DrawLocalContentBorder()
{
    if (!showLocalPlayBorder || localContent == null) return;

    // Find or create a border child that stretches to LocalContent
    var border = localContent.Find(kLocalBorder) as RectTransform;
    if (border == null)
    {
        var go = new GameObject(kLocalBorder, typeof(RectTransform), typeof(Image), typeof(Outline));
        border = go.GetComponent<RectTransform>();
        border.SetParent(localContent, false);
        border.anchorMin = Vector2.zero;
        border.anchorMax = Vector2.one;
        border.offsetMin = border.offsetMax = Vector2.zero;
        border.pivot = new Vector2(0.5f, 0.5f);
        go.GetComponent<Image>().color = new Color(0, 0, 0, 0);
    }

    var ol = border.GetComponent<Outline>();
    ol.effectDistance = Vector2.one * localPlayBorderThickness;
    ol.useGraphicAlpha = false;

    // -------- Optional: highlight local border if this local contains the throne --------
    // Turns the border reddish when the local map the player is inside contains the throne.
    bool throneInThisLocal = false;
    var gm = GameManager.Instance;
    if (gm != null &&
        gm.players != null &&
        gm.players.TryGetValue(gm.localPlayerId, out var me3) &&
        me3 != null &&
        me3.insideLocationIndex.HasValue &&
        gm.locationRects != null)
    {
        int li = me3.insideLocationIndex.Value;
        if (li >= 0 && li < gm.locationRects.Count)
        {
            var lr = gm.locationRects[li]; // assumes lr has bl (bottom-left) and tr (top-right) with x/y
            var t  = gm.throneRoom;        // assumes throneRoom has x/y (e.g., Vector2/Vector2Int/struct)
            throneInThisLocal = (t.x >= lr.bl.x && t.x <= lr.tr.x && t.y >= lr.bl.y && t.y <= lr.tr.y);
        }
    }

    // Use highlight color if we're in the throne's local; otherwise the normal border color
    ol.effectColor = throneInThisLocal ? new Color(1f, 0.25f, 0.25f, 0.7f) : localPlayBorderColor;
    // ------------------------------------------------------------------------------------

    // Ensure it stays above other dots
    border.SetAsLastSibling();
}

}
