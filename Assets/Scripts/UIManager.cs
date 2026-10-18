using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// Minimal Phase-4 UI glue (stable LocalMap):
/// - InfoPanel (time, energy, carried)
/// - LocalMapPanel (appears when player is inside a location, pooled rendering)
/// - Options button/menu (Begin/Join/Leave)
public class UIManager : MonoBehaviour
{
    [Header("Panels")]
    public RectTransform infoPanel;
    public RectTransform worldMapPanel;
    public RectTransform localMapPanel;     // starts inactive
    public RectTransform localContent;      // container inside LocalMapPanel
    public TMP_Text      localHeaderText;   // "Location #X"
    public TMP_Text      rivalInfoText;     // also used for time/throne status

    [Header("Info labels")]
    public TMP_Text timeText;
    public TMP_Text energyText;
    public TMP_Text carriedText;

    [Header("Warnings")]
    public int   warnSeconds = 30;
    public Color timeNormalColor = Color.white;
    public Color timeWarnColor   = new Color(1f, 0.4f, 0.2f, 1f);
    public bool  playWarnOnce = true;
    bool _warnShown = false;

    [Header("Options")]
    public Button optionsBtn;
    public GameObject optionsMenu;
    public Button beginNewBtn;
    public Button joinExistingBtn;
    public Button leaveGameBtn;

    [Header("Local map prefabs (UI Images)")]
    public Image playerPrefab;              // green
    public Image otherPlayerPrefab;         // blue
    public Image npcPrefab;                 // white
    public Image crystalPrefab;             // yellow

    [Header("Local map name tags (optional)")]
    public TMP_Text nameTagPrefab;          // text above dots (optional)
    public Vector2  nameTagOffset = new Vector2(10f, 12f);

    // ---- pooling for LocalMap (no flicker) ----
    readonly List<Image> pool = new();
    readonly List<TMP_Text> tagPool = new();
    int currentLi = -1;
    int presenceHash = 0;

    void Awake()
    {
        if (optionsBtn)
        {
            optionsBtn.onClick.RemoveAllListeners();
            optionsBtn.onClick.AddListener(ToggleOptions);
        }

        // Make Begin/Join behave exactly like Leave Game:
        if (beginNewBtn)
        {
            beginNewBtn.onClick.RemoveAllListeners();
            beginNewBtn.onClick.AddListener(() => { OnLeaveGame(); });
        }
        if (joinExistingBtn)
        {
            joinExistingBtn.onClick.RemoveAllListeners();
            joinExistingBtn.onClick.AddListener(() => { OnLeaveGame(); });
        }

        if (leaveGameBtn)
        {
            leaveGameBtn.onClick.RemoveAllListeners();
            leaveGameBtn.onClick.AddListener(OnLeaveGame);
        }

        if (optionsMenu) optionsMenu.SetActive(false);
        if (localMapPanel) localMapPanel.gameObject.SetActive(false);
    }

    void Update()
    {
        var gm = GameManager.Instance;
        if (gm == null) return;

        // Neutral UI when no game is running
        if (!gm.gameStarted)
        {
            if (timeText)   { timeText.text  = "Time 00:00"; timeText.color = timeNormalColor; }
            if (energyText) energyText.text  = "Energy 0";
            if (carriedText)carriedText.text = "Carried 0";
            _warnShown = false;

            if (!gm.modalOpen && rivalInfoText) rivalInfoText.text = "";
            if (localMapPanel && localMapPanel.gameObject.activeSelf)
                localMapPanel.gameObject.SetActive(false);
            return;
        }

        // ----- Time & warning tint -----
        int secs = 0;
        bool underWarn = false;
        if (timeText)
        {
            double msLeft = Math.Max(0, gm.timeEndUtc - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            secs = Mathf.CeilToInt((float)(msLeft / 1000.0));
            timeText.text = $"Time {secs / 60:00}:{secs % 60:00}";

            underWarn = (secs <= Mathf.Max(5, warnSeconds));
            timeText.color = underWarn ? timeWarnColor : timeNormalColor;

            if (underWarn && !_warnShown) _warnShown = playWarnOnce;
            else if (!underWarn) _warnShown = false;
        }

        // ----- Player info (energy / carried) -----
        PlayerState me = null;
        bool haveMe = gm.players.TryGetValue(gm.localPlayerId, out me) && me != null;

        if (haveMe)
        {
            if (energyText)  energyText.text  = $"Energy {me.energy}";
            if (carriedText) carriedText.text = $"Carried {(me.carrying?.Count ?? 0)}";
        }
        else
        {
            if (energyText)  energyText.text  = "Energy 0";
            if (carriedText) carriedText.text = "Carried 0";
        }

        // ----- Live throne detection message -----
        if (!gm.modalOpen && rivalInfoText)
        {
            bool atThrone = false;
            if (haveMe && !me.leftGame)
            {
                float r2 = gm.throneRadius * gm.throneRadius;
                atThrone = (me.pos - gm.throneRoom).sqrMagnitude <= r2;
            }

            string status = atThrone ? "🏛 At the throne" : "";
            if (underWarn)
            {
                status += (status.Length > 0 ? "\n" : "");
                status += atThrone
                    ? "⏰ Time’s almost up. Stay at the throne!"
                    : "⏰ Time’s almost up. Return to the throne!";
            }
            rivalInfoText.text = status;
        }

        // ----- Local map panel -----
        if (haveMe && me.insideLocationIndex.HasValue &&
            me.insideLocationIndex.Value >= 0 &&
            me.insideLocationIndex.Value < gm.locationRects.Count)
        {
            int li = me.insideLocationIndex.Value;

            if (!localMapPanel.gameObject.activeSelf)
                localMapPanel.gameObject.SetActive(true);

            if (localHeaderText) localHeaderText.text = $"Location #{li}";

            // Hash: who is in this location + crystal/npc counts
            int newHash = 17;
            foreach (var kv in gm.players)
            {
                var p = kv.Value;
                int inHere = (p.insideLocationIndex == li && !p.leftGame) ? 1 : 0;
                newHash = newHash * 31 + (p.playerId.GetHashCode() ^ inHere);
            }
            int crystalsHere = 0, npcsHere = 0;
            for (int i = 0; i < gm.crystals.Count; i++) if (gm.crystals[i].locationIndex == li) crystalsHere++;
            for (int i = 0; i < gm.npcs.Count; i++)     if (gm.npcs[i].locationIndex == li)     npcsHere++;
            newHash = newHash * 31 + crystalsHere;
            newHash = newHash * 31 + npcsHere;

            if (currentLi != li || newHash != presenceHash)
            {
                currentLi = li;
                presenceHash = newHash;
                RebuildLocalMapStatic(gm, li);   // NPCs + crystals + fresh player dots/tags
            }

            UpdateLocalPlayers(gm, li);
        }
        else
        {
            if (localMapPanel.gameObject.activeSelf)
                localMapPanel.gameObject.SetActive(false);
            currentLi = -1;
            presenceHash = 0;
        }
    }

    // ---------- Mapping: world -> LocalContent anchored ----------
    Vector2 ToLocalAnchored(Vector2 world, int li)
    {
        var gm = GameManager.Instance;
        var lr = gm.locationRects[li];

        float nx = Mathf.InverseLerp(lr.bl.x, lr.tr.x, world.x);
        float ny = Mathf.InverseLerp(lr.bl.y, lr.tr.y, world.y);

        var rect = localContent.rect;
        Vector2 baseOffset = new Vector2(-rect.width * localContent.pivot.x,
                                         -rect.height * localContent.pivot.y);

        return baseOffset + new Vector2(nx * rect.width, ny * rect.height);
    }

    // ---------- Pool helpers (images) ----------
    Image GetInactiveOrCreate(Image prefab)
    {
        for (int i = 0; i < pool.Count; i++)
        {
            if (!pool[i].gameObject.activeSelf)
            {
                var img = pool[i];
                img.sprite = prefab.sprite;
                img.rectTransform.sizeDelta = prefab.rectTransform.sizeDelta;
                img.color = prefab.color;
                img.gameObject.SetActive(true);
                return img;
            }
        }
        var inst = Instantiate(prefab, localContent);
        inst.raycastTarget = false;
        pool.Add(inst);
        return inst;
    }

    Image FindInPoolByName(string wanted)
    {
        for (int i = 0; i < pool.Count; i++)
        {
            if (pool[i].gameObject.activeSelf && pool[i].name == wanted)
                return pool[i];
        }
        return null;
    }

    Image FindOrCreateByName(string wanted, Image prefab)
    {
        var img = FindInPoolByName(wanted);
        if (img != null) return img;
        img = GetInactiveOrCreate(prefab);
        img.name = wanted;
        return img;
    }

    void DeactivateAllInPool()
    {
        for (int i = 0; i < pool.Count; i++)
            pool[i].gameObject.SetActive(false);
        for (int i = 0; i < tagPool.Count; i++)
            tagPool[i].gameObject.SetActive(false);
    }

    // ---------- Pool helpers (name tags) ----------
    TMP_Text GetInactiveOrCreateTag()
    {
        for (int i = 0; i < tagPool.Count; i++)
        {
            if (!tagPool[i].gameObject.activeSelf)
            {
                var t = tagPool[i];
                t.gameObject.SetActive(true);
                return t;
            }
        }
        var inst = Instantiate(nameTagPrefab, localContent);
        tagPool.Add(inst);
        return inst;
    }

    TMP_Text FindTagByName(string wanted)
    {
        for (int i = 0; i < tagPool.Count; i++)
        {
            if (tagPool[i].gameObject.activeSelf && tagPool[i].name == wanted)
                return tagPool[i];
        }
        return null;
    }

    TMP_Text FindOrCreateTagByName(string wanted)
    {
        var t = FindTagByName(wanted);
        if (t != null) return t;
        if (nameTagPrefab == null) return null;
        t = GetInactiveOrCreateTag();
        t.name = wanted;
        return t;
    }

    // ---------- Build static contents (on location/presence change only) ----------
    void RebuildLocalMapStatic(GameManager gm, int li)
    {
        if (localContent == null) return;

        DeactivateAllInPool();

        // NPCs
        if (npcPrefab)
        {
            foreach (var n in gm.npcs)
            {
                if (n.locationIndex != li) continue;
                var img = GetInactiveOrCreate(npcPrefab);
                img.name = "Local_NPC";
                img.rectTransform.anchoredPosition = ToLocalAnchored(n.pos, li);
            }
        }

        // Crystals
        if (crystalPrefab)
        {
            int idx = 0;
            foreach (var c in gm.crystals)
            {
                if (c.locationIndex != li) continue;
                var img = GetInactiveOrCreate(crystalPrefab);
                img.name = "Local_Crystal_" + idx++;
                img.rectTransform.anchoredPosition = ToLocalAnchored(c.pos, li);
            }
        }

        // Local player
        if (playerPrefab && gm.players.TryGetValue(gm.localPlayerId, out var me))
        {
            var img = GetInactiveOrCreate(playerPrefab);
            img.name = "Local_Player";
            img.rectTransform.anchoredPosition = ToLocalAnchored(me.pos, li);
            img.transform.SetAsLastSibling();

            // Name tag ("Me" or id)
            if (nameTagPrefab != null)
            {
                var tag = FindOrCreateTagByName("Tag_Player");
                if (tag != null)
                {
                    tag.text = gm.localPlayerId;
                    tag.rectTransform.anchoredPosition = img.rectTransform.anchoredPosition + nameTagOffset;
                    tag.gameObject.SetActive(true);
                }
            }
        }

        // Other players
        if (otherPlayerPrefab)
        {
            foreach (var kv in gm.players)
            {
                var p = kv.Value;
                if (p.playerId == gm.localPlayerId || p.leftGame) continue;
                if (p.insideLocationIndex != li) continue;

                var img = GetInactiveOrCreate(otherPlayerPrefab);
                img.name = "Local_Other_" + p.playerId;
                img.color = new Color(0.2f, 0.6f, 1f, 1f);
                if (otherPlayerPrefab.rectTransform.sizeDelta == Vector2.zero)
                    img.rectTransform.sizeDelta = new Vector2(16, 16);
                img.rectTransform.anchoredPosition = ToLocalAnchored(p.pos, li);
                img.transform.SetAsLastSibling();

                // Name tag for other
                if (nameTagPrefab != null)
                {
                    var tag = FindOrCreateTagByName("Tag_Other_" + p.playerId);
                    if (tag != null)
                    {
                        tag.text = p.playerId;
                        tag.rectTransform.anchoredPosition = img.rectTransform.anchoredPosition + nameTagOffset;
                        tag.gameObject.SetActive(true);
                    }
                }
            }
        }
    }

    void UpdateLocalCrystals(GameManager gm, int li)
    {
        int idx = 0;
        for (int i = 0; i < gm.crystals.Count; i++)
        {
            var c = gm.crystals[i];
            if (c.locationIndex != li) continue;

            string oname = "Local_Crystal_" + idx++;
            var img = FindInPoolByName(oname);
            if (img != null)
                img.rectTransform.anchoredPosition = ToLocalAnchored(c.pos, li);
        }
    }

    // ---------- Per-frame move for dynamic players only ----------
    void UpdateLocalPlayers(GameManager gm, int li)
    {
        // local player
        if (gm.players.TryGetValue(gm.localPlayerId, out var me))
        {
            var img = FindOrCreateByName("Local_Player", playerPrefab);
            img.rectTransform.anchoredPosition = ToLocalAnchored(me.pos, li);
            img.transform.SetAsLastSibling();

            if (nameTagPrefab != null)
            {
                var tag = FindOrCreateTagByName("Tag_Player");
                if (tag != null)
                {
                    tag.text = gm.localPlayerId;
                    tag.rectTransform.anchoredPosition = img.rectTransform.anchoredPosition + nameTagOffset;
                    tag.gameObject.SetActive(true);
                }
            }
        }

        // deactivate all "Local_Other_*" first (and their tags)
        for (int i = 0; i < pool.Count; i++)
        {
            if (pool[i].gameObject.activeSelf && pool[i].name.StartsWith("Local_Other_"))
                pool[i].gameObject.SetActive(false);
        }
        for (int i = 0; i < tagPool.Count; i++)
        {
            if (tagPool[i].gameObject.activeSelf && tagPool[i].name.StartsWith("Tag_Other_"))
                tagPool[i].gameObject.SetActive(false);
        }

        // re-enable/move others in the same location
        foreach (var kv in gm.players)
        {
            var p = kv.Value;
            if (p.leftGame || p.playerId == gm.localPlayerId) continue;
            if (!p.insideLocationIndex.HasValue || p.insideLocationIndex.Value != li) continue;

            string oname = "Local_Other_" + p.playerId;
            var img = FindOrCreateByName(oname, otherPlayerPrefab);
            img.gameObject.SetActive(true);
            img.color = new Color(0.2f, 0.6f, 1f, 1f);
            if (img.rectTransform.sizeDelta == Vector2.zero)
                img.rectTransform.sizeDelta = new Vector2(16, 16);
            img.rectTransform.anchoredPosition = ToLocalAnchored(p.pos, li);
            img.transform.SetAsLastSibling();

            if (nameTagPrefab != null)
            {
                var tag = FindOrCreateTagByName("Tag_Other_" + p.playerId);
                if (tag != null)
                {
                    tag.text = p.playerId;
                    tag.rectTransform.anchoredPosition = img.rectTransform.anchoredPosition + nameTagOffset;
                    tag.gameObject.SetActive(true);
                }
            }
        }

        UpdateLocalCrystals(gm, li);
    }

    // ---------- Options ----------
    void ToggleOptions()
    {
        if (!optionsMenu) return;
        optionsMenu.SetActive(!optionsMenu.activeSelf);
    }

    void OnBeginNew()
    {
        var start = FindObjectOfType<StartMenuUI>(true);
        if (start) start.gameObject.SetActive(true);
        ToggleOptions();
    }

    void OnJoinExisting()
    {
        var start = FindObjectOfType<StartMenuUI>(true);
        if (start) start.gameObject.SetActive(true);
        ToggleOptions();
    }

    void OnLeaveGame()
    {
        var gm  = GameManager.Instance;
        var net = NetworkManager.Instance;

        var enc = FindObjectOfType<EncounterUI>(true);
        if (enc) enc.Hide();

        net?.StopAll();

        if (gm != null)
        {
            gm.ResetToMenuState();
            gm.SetupSampleBoundary();
        }

        var overlays = FindObjectOfType<MapOverlays>(true);
        if (overlays) overlays.ClearAllOverlays();

        var maps = FindObjectOfType<MapboxStaticImagesBinder>(true);
        if (maps) maps.ResetMaps();

        var pc = FindObjectOfType<PlayerController>(true);
        if (pc) pc.enabled = false;

        MapViewController.Instance?.ShowLocalFull();

        var start = FindObjectOfType<StartMenuUI>(true);
        if (start) start.gameObject.SetActive(true);

        ToggleOptions();
    }

    // Used in Phase 5 (duel info) + time warning
    public void SetRivalInfo(string text) => rivalInfoText.text = text ?? "";
}
