using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System;
using System.Collections.Generic;

/// <summary>
/// World map overlay:
/// - Positions local PlayerDot
/// - Spawns & positions dots for Crystals and NPCs
/// - NEW: Shows other players as dots + optional name tags
/// </summary>
public class WorldMapUI : MonoBehaviour
{
    [Header("Refs")]
    public Image playerDot;                 // assign in Inspector (green)
    public RectTransform mapRectTransform;  // WorldMapPanel RectTransform

    [Header("Prefabs")]
    public Image crystalPrefab;             // small yellow dot
    public Image npcPrefab;                 // small purple dot

    [Header("Other Players (World Map)")]
    public Image otherPlayerPrefab;         // small blue dot (optional)
    public TMP_Text nameTagPrefab;          // Text label prefab (optional, TMP_Text)
    public Vector2 nameTagOffset = new Vector2(12f, 10f);

    [Header("Local player label (optional)")]
    public RectTransform playerLabel;       // RectTransform with TMP_Text attached
    public Vector2 playerLabelOffset = new Vector2(14f, 10f);

    [Header("Debug (optional)")]
    public TMP_Text infoText;

    // runtime pools
    private readonly List<Image> _crystalDots = new List<Image>();
    private readonly List<Image> _npcDots     = new List<Image>();

    // NEW: world-map other players (by id)
    private readonly Dictionary<string, Image>   _otherDots = new Dictionary<string, Image>(16);
    private readonly Dictionary<string, TMP_Text> _otherTags = new Dictionary<string, TMP_Text>(16);

    void LateUpdate()
    {
        var gm = GameManager.Instance;
        if (gm == null || mapRectTransform == null) return;

        // --- Local player dot ---
        Vector2 myPos = gm.players.TryGetValue(gm.localPlayerId, out var me) ? me.pos : Vector2.zero;
        bool myInside = gm.IsPointInRect(myPos, gm.boundaryPoints);

        if (playerDot != null)
            playerDot.rectTransform.anchoredPosition = myInside ? ToAnchored(myPos) : new Vector2(-9999, -9999);

        if (playerLabel != null)
        {
            // Allow either a RectTransform with a TMP_Text child, or the text on the same object
            var labText = playerLabel.GetComponent<TMP_Text>() ?? playerLabel.GetComponentInChildren<TMP_Text>(true);
            if (labText != null) labText.text = gm.localPlayerId;
            if (playerDot != null)
                playerLabel.anchoredPosition = playerDot.rectTransform.anchoredPosition + playerLabelOffset;
        }

        // --- Crystals ---
        SyncPool(_crystalDots, gm.crystals.Count, crystalPrefab);
        for (int i = 0; i < gm.crystals.Count; i++)
            _crystalDots[i].rectTransform.anchoredPosition = ToAnchored(gm.crystals[i].pos);

        // --- NPCs ---
        SyncPool(_npcDots, gm.npcs.Count, npcPrefab);
        for (int i = 0; i < gm.npcs.Count; i++)
            _npcDots[i].rectTransform.anchoredPosition = ToAnchored(gm.npcs[i].pos);

        // --- Other players (dots + nametags) ---
        var seen = new HashSet<string>();

        foreach (var kv in gm.players)
        {
            var ps = kv.Value;
            if (ps == null || ps.leftGame) continue;
            if (ps.playerId == gm.localPlayerId) continue; // skip me

            // Only show if inside the world rect
            if (!gm.IsPointInRect(ps.pos, gm.boundaryPoints)) continue;

            Vector2 ap = ToAnchored(ps.pos);
            seen.Add(ps.playerId);

            // Dot
            if (otherPlayerPrefab != null)
            {
                var dot = EnsureOtherDot(ps.playerId);
                dot.rectTransform.anchoredPosition = ap;
                dot.gameObject.SetActive(true);
            }

            // Name tag
            if (nameTagPrefab != null)
            {
                var tag = EnsureOtherTag(ps.playerId);
                tag.text = ps.playerId;
                tag.rectTransform.anchoredPosition = ap + nameTagOffset;
                tag.gameObject.SetActive(true);
            }
        }

        // Hide any stale others no longer present
        HideStaleOthers(seen);
    }

    // ---------- Helpers ----------

    private void SyncPool(List<Image> list, int need, Image prefab)
    {
        if (prefab == null) return;

        // create
        while (list.Count < need)
        {
            var dot = Instantiate(prefab, mapRectTransform);
            dot.gameObject.SetActive(true);
            list.Add(dot);
        }
        // destroy extra
        while (list.Count > need)
        {
            var last = list[list.Count - 1];
            list.RemoveAt(list.Count - 1);
            if (last != null) Destroy(last.gameObject);
        }
    }

    private Image EnsureOtherDot(string playerId)
    {
        if (_otherDots.TryGetValue(playerId, out var img) && img != null) return img;
        var inst = Instantiate(otherPlayerPrefab, mapRectTransform);
        inst.name = "World_OtherDot_" + playerId;
        inst.raycastTarget = false;
        _otherDots[playerId] = inst;
        return inst;
    }

    private TMP_Text EnsureOtherTag(string playerId)
    {
        if (_otherTags.TryGetValue(playerId, out var t) && t != null) return t;
        var inst = Instantiate(nameTagPrefab, mapRectTransform);
        inst.name = "World_OtherTag_" + playerId;
        _otherTags[playerId] = inst;
        return inst;
    }

    private void HideStaleOthers(HashSet<string> keep)
    {
        // Dots
        foreach (var kv in _otherDots)
        {
            if (!keep.Contains(kv.Key) && kv.Value != null)
                kv.Value.gameObject.SetActive(false);
        }
        // Tags
        foreach (var kv in _otherTags)
        {
            if (!keep.Contains(kv.Key) && kv.Value != null)
                kv.Value.gameObject.SetActive(false);
        }
    }

    private Vector2 ToAnchored(Vector2 world)
    {
        var n = GameManager.Instance.WorldToMap01(world);
        Vector2 size = mapRectTransform.rect.size;
        return new Vector2((n.x - 0.5f) * size.x, (n.y - 0.5f) * size.y);
    }
}
