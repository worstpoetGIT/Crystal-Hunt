using UnityEngine;
using System;
using System.Text;
using System.Collections.Generic;

// ---------- Shared DTOs ----------
[Serializable] public struct SnapshotData { public Vector2[] boundary; }
[Serializable] public struct LocRect   { public Vector2 bl, tr; }
[Serializable] public struct Crystal   { public string id; public Vector2 pos; public int locationIndex; }
[Serializable] public struct Npc       { public string id; public Vector2 pos; public int locationIndex; }

[Serializable] public struct PlayerJoin
{
    public string playerId;
    public Vector2 initialTarget;
    public double arrivalDeadlineUtc; // unix ms
}

[Serializable] public struct ResultEntry
{
    public string playerId;
    public int crystals;
    public int energy;
    public bool atThrone;
}

[Serializable] public class FinalResult
{
    public bool gameOver;
    public string winnerId;
    public string reason;
    public ResultEntry[] table;
}

// --- Minimal player payload for snapshots ---
[Serializable] public struct PlayerLite
{
    public string playerId;
    public Vector2 pos;
    public int insideLocationIndexOrMinus1;
    public bool leftGame;
    public int energy;
    public int carryingCount;
}

[Serializable]
public class FullSnapshot
{
    public string gameName;
    public Vector2[] boundary;
    public Vector2 throne;
    public LocRect[] locations;
    public Crystal[] crystals;
    public Npc[] npcs;
    public PlayerJoin[] playerAssignments;
    public PlayerLite[] players;
    public double timeEndUtc;        // unix ms
    public FinalResult result;       // optional (null during game)
}

[Serializable]
public class PlayerState
{
    public string playerId;
    public Vector2 pos;
    public int energy = 10;
    public List<string> carrying = new();
    public int? insideLocationIndex = null;
    public bool leftGame = false;

    // entry detection helpers
    public int? lastLocationIndex = null;
    public Vector2? lastEnterPos = null;
    public bool gotEntryCrystalBonus = false;

    // late-arrival tracking
    public bool reachedInitial = false;
    public bool arrivalPenaltyApplied = false;
    
    //  NEW: single-duel-per-entry support + simple, fair ordering
    public bool  dueledSinceEntry = false;
    public float lastEnterTime    = 0f;
}

[DefaultExecutionOrder(-100)]
public class GameManager : MonoBehaviour
{
    public static GameManager Instance { get; private set; }

    [Header("Energy Tick (debug)")]
    public float secondsPerFiveMinuteTick = 5f;
    private float _tickAccum = 0f;

    public bool gameStarted = false;
    public bool IsRunning => gameStarted && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < timeEndUtc;

    [Header("Throne check")]
    public float throneRadius = 0.35f;
    public FinalResult lastResult = null;

    public bool modalOpen = false;
    public bool IsInputAllowed => gameStarted && !modalOpen;
    public bool IsTicking     => gameStarted && !modalOpen;

    [Header("Initial assignment")]
    public float initialArrivalRadius = 0.5f;

    [Header("Anti-cheat (optional)")]
    public float maxUnitsPerSecond = 6f;

    [Header("Crystal attraction (locals)")]
    public float attractStartRadius   = 0.60f;
    public float crystalPickupRadius  = 0.12f;
    public float crystalMoveSpeed     = 1.50f;

    [Header("Networking")]
    [Tooltip("How often (seconds) the host may broadcast movement snapshots.")]
    public float moveBroadcastInterval = 0.15f;  // ~6–8 per second
    private float _nextMoveBroadcastTime = 0f;

    // -------------------- Phase 1 (kept) --------------------
    public Vector2[] boundaryPoints = new Vector2[4];
    public List<Vector2[]> locations = new List<Vector2[]>();
    public Vector2 throneRoom;

    public string localPlayerId = "LocalPlayer";
    public Dictionary<string, PlayerState> players = new();

    public string lastWinnerPlayerId = null;

    // -------------------- Phase 2 state --------------------
    public string gameName = "CrystalGame";
    public List<LocRect> locationRects = new();
    public List<Crystal> crystals = new();
    public List<Npc> npcs = new();
    public List<PlayerJoin> playerAssignments = new();
    public double timeEndUtc; // unix ms

    private System.Random rng = new System.Random();

    // Fallback duel cooldown (used only when net-side arbiter is OFF)
    private readonly Dictionary<string, float> _duelCooldownUntil = new Dictionary<string, float>(32);

    private void Awake()
    {
        if (Instance == null) { Instance = this; DontDestroyOnLoad(gameObject); }
        else { Destroy(gameObject); return; }

        // Ensure per-device unique id if left at default
        if (string.IsNullOrWhiteSpace(localPlayerId) || localPlayerId == "LocalPlayer")
        {
            var raw = SystemInfo.deviceUniqueIdentifier;
            if (string.IsNullOrEmpty(raw)) raw = Guid.NewGuid().ToString("N");
            localPlayerId = "P-" + raw.Substring(0, 6);
            Debug.Log($"[GM] localPlayerId set to {localPlayerId}");
        }

        ResetToMenuState();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void CreateGameManagerIfMissing()
    {
        if (Instance == null)
        {
            var gm = new GameObject("GameManager").AddComponent<GameManager>();
            gm.SetupSampleBoundary();
        }
    }

    // -------------------- Geometry helpers --------------------
    public bool IsPointInRect(Vector2 p, Vector2[] rect)
    {
        if (rect == null || rect.Length < 4) return false;
        float minX = rect[0].x, maxX = rect[0].x;
        float minY = rect[0].y, maxY = rect[0].y;
        for (int i = 1; i < rect.Length; i++)
        {
            var v = rect[i];
            if (v.x < minX) minX = v.x; if (v.x > maxX) maxX = v.x;
            if (v.y < minY) minY = v.y; if (v.y > maxY) maxY = v.y;
        }
        return p.x >= minX && p.x <= maxX && p.y >= minY && p.y <= maxY;
    }

    public void SetupSampleBoundary()
    {
        // 10x10 box centered on 0,0
        boundaryPoints = new Vector2[]
        {
            new Vector2(-5f, -5f),
            new Vector2(-5f,  5f),
            new Vector2( 5f,  5f),
            new Vector2( 5f, -5f)
        };

        throneRoom = Vector2.zero;

        locations.Clear();
        locations.Add(new Vector2[]
        {
            new Vector2(-1f, -1f),
            new Vector2(-1f,  1f),
            new Vector2( 1f,  1f),
            new Vector2( 1f, -1f)
        });
    }

    public void GetWorldMinMax(out Vector2 min, out Vector2 max)
    {
        min = max = boundaryPoints[0];
        for (int i = 1; i < boundaryPoints.Length; i++)
        {
            var v = boundaryPoints[i];
            if (v.x < min.x) min.x = v.x; if (v.x > max.x) max.x = v.x;
            if (v.y < min.y) min.y = v.y; if (v.y > max.y) max.y = v.y;
        }
    }

    public Vector2 WorldToMap01(Vector2 world)
    {
        GetWorldMinMax(out var min, out var max);
        return new Vector2(
            Mathf.InverseLerp(min.x, max.x, world.x),
            Mathf.InverseLerp(min.y, max.y, world.y)
        );
    }

    // Legacy Phase-1 snapshot (still fine to keep)
    public void ApplySnapshot(SnapshotData snapshot)
    {
        boundaryPoints = snapshot.boundary ?? boundaryPoints;
        Debug.Log("Snapshot applied with " + boundaryPoints.Length + " boundary points.");
    }

    // -------------------- State lifecycle --------------------
    public void ResetToMenuState()
    {
        gameStarted = false;
        timeEndUtc = 0;
        players.Clear();
        crystals.Clear();
        npcs.Clear();
        playerAssignments.Clear();
        lastResult = null;
        _duelCooldownUntil.Clear();
    }

    public void BeginNewGame(string name, int port, int minutes)
    {
        ResetToMenuState();
        gameName = string.IsNullOrWhiteSpace(name) ? "CrystalGame" : name.Trim();

        // Throne at world center
        GetWorldMinMax(out var min, out var max);
        throneRoom = new Vector2((min.x + max.x) * 0.5f, (min.y + max.y) * 0.5f);

        // Build world contents
        GenerateLocations(5, 1.2f, 2.2f);
        ScatterCrystals();
        PlaceNpcs();

        // Local host player fresh at the throne
        var host = EnsurePlayer(localPlayerId);
        host.leftGame = false;
        host.carrying.Clear();
        host.energy = 10;
        host.pos    = throneRoom;

        int? liAtThrone = GetLocationIndexAt(throneRoom);
        host.insideLocationIndex = liAtThrone;
        host.lastLocationIndex   = liAtThrone;
        host.lastEnterPos        = liAtThrone.HasValue ? (Vector2?)throneRoom : null;
        host.gotEntryCrystalBonus = false;

        // Timer
        timeEndUtc = DateTimeOffset.UtcNow.AddMinutes(Mathf.Max(1, minutes)).ToUnixTimeMilliseconds();

        // Start running
        gameStarted = true;
        modalOpen   = false;

        var enc = EncounterUI.Instance ?? FindObjectOfType<EncounterUI>(true);
        if (enc) enc.Hide();

        Debug.Log($"[GM] New game '{gameName}' on port {port} | locs={locationRects.Count} | crystals={crystals.Count} | npcs={npcs.Count}");

        MapViewController.Instance?.ShowWorldFull();

        // Push initial state
        NetworkManager.Instance?.BroadcastSnapshot();
    }

    private void GenerateLocations(int count, float minSize, float maxSize)
    {
        locationRects.Clear();
        locations.Clear();
        GetWorldMinMax(out var min, out var max);

        for (int i = 0; i < count; i++)
        {
            float w = Mathf.Lerp(minSize, maxSize, (float)rng.NextDouble());
            float h = Mathf.Lerp(minSize, maxSize, (float)rng.NextDouble());
            float x = Mathf.Lerp(min.x, max.x - w, (float)rng.NextDouble());
            float y = Mathf.Lerp(min.y, max.y - h, (float)rng.NextDouble());

            var bl = new Vector2(x, y);
            var tr = new Vector2(x + w, y + h);

            locationRects.Add(new LocRect { bl = bl, tr = tr });
            locations.Add(new Vector2[] { bl, new Vector2(bl.x, tr.y), tr, new Vector2(tr.x, bl.y) });
        }
    }

    private void ScatterCrystals()
    {
        crystals.Clear();
        for (int i = 0; i < locationRects.Count; i++)
        {
            int n = rng.Next(0, 11); // 0..10
            var lr = locationRects[i];

            for (int k = 0; k < n; k++)
            {
                float x = Mathf.Lerp(lr.bl.x, lr.tr.x, (float)rng.NextDouble());
                float y = Mathf.Lerp(lr.bl.y, lr.tr.y, (float)rng.NextDouble());
                crystals.Add(new Crystal { id = $"C{i}_{k}", pos = new Vector2(x, y), locationIndex = i });
            }
        }
    }

    private void PlaceNpcs()
    {
        npcs.Clear();
        for (int i = 0; i < locationRects.Count; i++)
        {
            if (rng.NextDouble() < 0.5) // ≤1 per location
            {
                var lr = locationRects[i];
                float x = Mathf.Lerp(lr.bl.x, lr.tr.x, (float)rng.NextDouble());
                float y = Mathf.Lerp(lr.bl.y, lr.tr.y, (float)rng.NextDouble());
                npcs.Add(new Npc { id = $"S{i}", pos = new Vector2(x, y), locationIndex = i });
            }
        }
    }

    public PlayerJoin AssignInitialTarget(string playerId, int secondsToArrive = 30)
    {
        if (locationRects.Count == 0) GenerateLocations(5, 1.2f, 2.2f);

        int li = UnityEngine.Random.Range(0, locationRects.Count);
        var lr = locationRects[li];
        var pt = new Vector2(
            UnityEngine.Random.Range(lr.bl.x, lr.tr.x),
            UnityEngine.Random.Range(lr.bl.y, lr.tr.y)
        );
        double deadline = DateTimeOffset.UtcNow.AddSeconds(Mathf.Max(5, secondsToArrive)).ToUnixTimeMilliseconds();

        var pj = new PlayerJoin { playerId = playerId, initialTarget = pt, arrivalDeadlineUtc = deadline };
        int idx = playerAssignments.FindIndex(p => p.playerId == playerId);
        if (idx >= 0) playerAssignments[idx] = pj; else playerAssignments.Add(pj);
        return pj;
    }

    public PlayerState EnsurePlayer(string playerId)
    {
        if (!players.TryGetValue(playerId, out var ps))
        {
            ps = new PlayerState { playerId = playerId, pos = throneRoom, energy = 10 };
            players[playerId] = ps;
        }
        return ps;
    }

    public int? GetLocationIndexAt(Vector2 p)
    {
        for (int i = 0; i < locationRects.Count; i++)
        {
            var lr = locationRects[i];
            if (p.x >= lr.bl.x && p.x <= lr.tr.x && p.y >= lr.bl.y && p.y <= lr.tr.y) return i;
        }
        return null;
    }

    // -------------------- Networking snapshots --------------------
    public FullSnapshot BuildSnapshot()
    {
        var plist = new List<PlayerLite>(players.Count);
        foreach (var kv in players)
        {
            var ps = kv.Value;
            plist.Add(new PlayerLite
            {
                playerId = ps.playerId,
                pos = ps.pos,
                insideLocationIndexOrMinus1 = ps.insideLocationIndex.HasValue ? ps.insideLocationIndex.Value : -1,
                leftGame = ps.leftGame,
                energy = ps.energy,
                carryingCount = ps.carrying?.Count ?? 0
            });
        }

        return new FullSnapshot
        {
            gameName  = gameName,
            boundary  = boundaryPoints,
            throne    = throneRoom,
            locations = locationRects.ToArray(),
            crystals  = crystals.ToArray(),
            npcs      = npcs.ToArray(),
            playerAssignments = playerAssignments.ToArray(),
            players   = plist.ToArray(),
            timeEndUtc= timeEndUtc,
            result    = lastResult
        };
    }

    public void ApplySnapshot(FullSnapshot s)
    {
        gameName       = s.gameName;
        boundaryPoints = s.boundary ?? boundaryPoints;
        throneRoom     = s.throne;

        locationRects = s.locations != null ? new List<LocRect>(s.locations) : new List<LocRect>();
        crystals      = s.crystals  != null ? new List<Crystal>(s.crystals)   : new List<Crystal>();
        npcs          = s.npcs      != null ? new List<Npc>(s.npcs)           : new List<Npc>();
        playerAssignments = s.playerAssignments != null ? new List<PlayerJoin>(s.playerAssignments) : new List<PlayerJoin>();
        timeEndUtc    = s.timeEndUtc;

        // keep Phase-1 'locations' in sync for any legacy UI
        // keep Phase-1 'locations' in sync for any legacy UI
locations.Clear();
foreach (var lr in locationRects)
{
    locations.Add(new Vector2[]
    {
        lr.bl,
        new Vector2(lr.bl.x, lr.tr.y),
        lr.tr,
        new Vector2(lr.tr.x, lr.bl.y) // <- fixed
    });
}

        // players → PlayerState
        if (s.players != null)
        {
            foreach (var pl in s.players)
            {
                var ps = EnsurePlayer(pl.playerId);
                ps.pos = pl.pos;
                ps.leftGame = pl.leftGame;
                ps.energy = pl.energy;
                ps.insideLocationIndex = (pl.insideLocationIndexOrMinus1 >= 0) ? pl.insideLocationIndexOrMinus1 : (int?)null;

                ps.lastLocationIndex = ps.insideLocationIndex;
                ps.lastEnterPos = null;
            }
        }

        Debug.Log($"[GM] Snapshot applied: players={players.Count}, locs={locationRects.Count}, crystals={crystals.Count}, npcs={npcs.Count}");

        gameStarted = true;
        MapViewController.Instance?.ShowWorldFull();

        lastResult = s.result;
        if (lastResult != null && lastResult.gameOver)
        {
            gameStarted = false;
            ShowFinalModal(lastResult);
        }
    }

    // -------------------- Host movement & interactions --------------------
    public void HostSetPlayerPosition(string playerId, Vector2 newPos)
    {
        var ps = EnsurePlayer(playerId);
        if (ps.leftGame) return;

        // Boundary guard
        GetWorldMinMax(out var min, out var max);
        bool outside = (newPos.x < min.x || newPos.x > max.x || newPos.y < min.y || newPos.y > max.y);
        if (outside)
        {
            LeaveAndDropAll(playerId);
            NetworkManager.Instance?.BroadcastSnapshot();
            return;
        }

        // Speed clamp
        if (maxUnitsPerSecond > 0f)
        {
            var last = ps.pos;
            float maxStep = maxUnitsPerSecond * Time.deltaTime * 1.5f;
            var delta = newPos - last;
            if (delta.magnitude > maxStep)
                newPos = last + delta.normalized * maxStep;
        }

        // Move
        ps.pos = newPos;

        // Mark initial-target arrival if reached
        if (TryMarkArrival(playerId, ps.pos, ref ps))
        {
            NetworkManager.Instance?.BroadcastSnapshot();
        }

        // Enter / exit detection
        int? nowIndex = GetLocationIndexAt(ps.pos);
if (nowIndex != ps.lastLocationIndex)
{
    ps.lastLocationIndex   = nowIndex;
    ps.insideLocationIndex = nowIndex;
    ps.lastEnterPos = nowIndex.HasValue ? (Vector2?)ps.pos : null;
     
     if (nowIndex.HasValue)
            {
                ps.dueledSinceEntry = false;
                ps.lastEnterTime = Time.time;
            }
    

    // 🔒 Only switch views if THIS is the local player
            if (playerId == localPlayerId)
            {
                if (nowIndex.HasValue) MapViewController.Instance?.ShowLocalFull();
                else MapViewController.Instance?.ShowWorldFull();
            }

    //  NEW: tell EncounterManager a player entered a location (host only)
    if (nowIndex.HasValue && NetworkManager.Instance != null && NetworkManager.Instance.isHost)
        EncounterManager.Instance?.OnPlayerEnteredLocation(playerId, nowIndex.Value);

   
  

    // Duel triggering (fallback only when net arbiter is OFF)
            if (nowIndex.HasValue &&
                (NetworkManager.Instance == null || !NetworkManager.Instance.isHost || !NetworkManager.Instance.useNetSideDuelArbiter))
            {
                CheckAndEmitDuels();
            }

    ps.gotEntryCrystalBonus = false;

    NetworkManager.Instance?.BroadcastSnapshot();
    _nextMoveBroadcastTime = Time.unscaledTime + moveBroadcastInterval;
    return;
}


        // Collect WORLD crystals (locationIndex == -1)
        if (!ps.insideLocationIndex.HasValue)
        {
            float pickupR2 = crystalPickupRadius * crystalPickupRadius;
            bool changed = false;

            for (int i = crystals.Count - 1; i >= 0; i--)
            {
                var c = crystals[i];
                if (c.locationIndex != -1) continue;
                if ((ps.pos - c.pos).sqrMagnitude <= pickupR2)
                {
                    ps.carrying.Add(c.id);
                    crystals.RemoveAt(i);
                    EncounterManager.Instance?.TryAutoSettleDebts(playerId);
                    changed = true;
                }
            }

            if (changed)
            {
                NetworkManager.Instance?.BroadcastSnapshot();
                _nextMoveBroadcastTime = Time.unscaledTime + moveBroadcastInterval;
                return;
            }
        }

        // Throttled movement broadcast so clients see motion
        if (Time.unscaledTime >= _nextMoveBroadcastTime)
        {
            _nextMoveBroadcastTime = Time.unscaledTime + moveBroadcastInterval;
            NetworkManager.Instance?.BroadcastSnapshot();
        }
    }

    // Mark arrival to initial target; remove assignment and flag ps.reachedInitial
    bool TryMarkArrival(string playerId, Vector2 pos, ref PlayerState ps)
    {
        if (ps.reachedInitial) return false;
        int idx = playerAssignments.FindIndex(p => p.playerId == playerId);
        if (idx < 0) return false;

        var pj = playerAssignments[idx];
        if ((pos - pj.initialTarget).sqrMagnitude <= (initialArrivalRadius * initialArrivalRadius))
        {
            ps.reachedInitial = true;
            playerAssignments.RemoveAt(idx);
            return true;
        }
        return false;
    }

    public void LeaveAndDropAll(string playerId)
    {
        var ps = EnsurePlayer(playerId);
        if (ps.leftGame) return;

        // Close any modal
        var encUI = EncounterUI.Instance ?? FindObjectOfType<EncounterUI>(true);
        encUI?.Hide();   // also sets gm.modalOpen = false

        // Re-scatter carried crystals
        const bool RESCATTER_ONLY_MAP_BORN = false;

        GetWorldMinMax(out var min, out var max);
        foreach (var id in ps.carrying)
        {
            if (RESCATTER_ONLY_MAP_BORN && !id.StartsWith("C")) continue;

            var pos = new Vector2(
                UnityEngine.Random.Range(min.x, max.x),
                UnityEngine.Random.Range(min.y, max.y)
            );
            int? li = GetLocationIndexAt(pos);
            crystals.Add(new Crystal { id = id, pos = pos, locationIndex = li ?? -1 });
        }

        ps.carrying.Clear();
        ps.leftGame = true;

        Debug.Log($"[GM] {playerId} left the game; crystals re-scattered.");

        // Only flip views / show “you left” copy on the device that owns this playerId
        if (playerId == localPlayerId)
        {
            MapViewController.Instance?.ShowWorldFull();
            FindObjectOfType<UIManager>(true)?.SetRivalInfo("You left the map — you lost. Your crystals were re-scattered.");

            var start = FindObjectOfType<StartMenuUI>(true);
            System.Action openStart = () => { if (start) start.OpenStartPanel(); };

            if (encUI != null && encUI.GetType().GetMethod("ShowChoices") != null)
            {
                encUI.ShowChoices(
                    "You left the map",
                    "You lost. Your crystals were re-scattered.",
                    new EncounterUI.Choice("Return to Start", openStart),
                    new EncounterUI.Choice("Host Game",      openStart),
                    new EncounterUI.Choice("Join Existing",  openStart)
                );
            }
            else
            {
                openStart();
            }
        }

        NetworkManager.Instance?.BroadcastSnapshot();
    }

    // -------------------- Crystal attraction (locals) --------------------
    void AttractCrystalsTick()
    {
        float pickupR2 = crystalPickupRadius * crystalPickupRadius;
        bool anyChange = false;

        foreach (var kv in players)
        {
            var ps = kv.Value;
            if (ps.leftGame) continue;
            if (!ps.insideLocationIndex.HasValue) continue;
            int li = ps.insideLocationIndex.Value;

            // Only when location is empty (no NPC, no other players)
            if (npcs.Exists(n => n.locationIndex == li)) continue;
            bool othersHere = false;
            foreach (var kv2 in players)
            {
                var op = kv2.Value;
                if (op.playerId == ps.playerId || op.leftGame) continue;
                if (op.insideLocationIndex == li) { othersHere = true; break; }
            }
            if (othersHere) continue;

            // Pull nearby crystals toward the player
            for (int i = crystals.Count - 1; i >= 0; i--)
            {
                var c = crystals[i];
                if (c.locationIndex != li) continue;

                var toPlayer = ps.pos - c.pos;
                float dist = toPlayer.magnitude;

                if (dist <= attractStartRadius)
                {
                    if (dist > 0.0001f)
                    {
                        c.pos += (toPlayer / dist) * crystalMoveSpeed * Time.deltaTime;
                        crystals[i] = c;
                        anyChange = true;
                    }

                    if ((ps.pos - c.pos).sqrMagnitude <= pickupR2)
                    {
                        ps.carrying.Add(c.id);
                        crystals.RemoveAt(i);

                        if (!ps.gotEntryCrystalBonus)
                        {
                            ps.energy += 2;
                            ps.gotEntryCrystalBonus = true;
                        }

                        EncounterManager.Instance?.TryAutoSettleDebts(ps.playerId);
                        anyChange = true;
                    }
                }
            }
        }

        if (anyChange) NetworkManager.Instance?.BroadcastSnapshot();
    }

    // -------------------- Optional fallback duel arbiter (host-only) --------------------
    static string PairKey(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return "";
        return string.CompareOrdinal(a, b) <= 0 ? $"{a}|{b}" : $"{b}|{a}";
    }

    public void CheckAndEmitDuels(float cooldownSeconds = 2f)
    {
        var net = NetworkManager.Instance;
        if (net == null || !net.isHost) return;
        if (net.useNetSideDuelArbiter) return; // net arbiter is preferred
        if (modalOpen) return;

        // Group players by location
        var groups = new Dictionary<int, List<string>>();
        foreach (var kv in players)
        {
            var ps = kv.Value;
            if (ps == null || ps.leftGame) continue;
            if (!ps.insideLocationIndex.HasValue) continue;
            int li = ps.insideLocationIndex.Value;

            if (!groups.TryGetValue(li, out var list))
            {
                list = new List<string>(4);
                groups[li] = list;
            }
            list.Add(ps.playerId);
        }

        // If 2+ players co-located → pick a pair and open duel locally (host only)
        foreach (var kv in groups)
        {
            var list = kv.Value;
            if (list.Count < 2) continue;

            list.Sort(StringComparer.Ordinal);
            string a = list[0];
            string b = list[1];
            string key = PairKey(a, b);

            float now = Time.time;
            if (_duelCooldownUntil.TryGetValue(key, out var until) && now < until) continue;
            _duelCooldownUntil[key] = now + Mathf.Max(0.5f, cooldownSeconds);

            EncounterUI.Instance?.ShowDuel(a, b);
            break; // one per frame
        }
    }

    // --- Bridge (kept for compatibility with prior code paths) ---
    public void HostTryQueueDuelIfNeeded(int li)
    {
        var net = NetworkManager.Instance;
        if (net == null || !net.isHost) return;
        if (!net.useNetSideDuelArbiter) CheckAndEmitDuels();
    }

    public void HostResolveDuel(string aId, string bId, int winnerIndex, int li)
    {
        NetworkManager.Instance?.BroadcastSnapshot();
    }

    // -------------------- Win condition --------------------
    bool IsAtThrone(Vector2 p) => (p - throneRoom).sqrMagnitude <= (throneRadius * throneRadius);
    int CountCrystals(PlayerState ps) => ps?.carrying?.Count ?? 0;

    void EvaluateAndFinishGame()
    {
        if (lastResult != null) return;

        var table = new List<ResultEntry>();
        foreach (var kv in players)
        {
            var ps = kv.Value;
            if (ps == null) continue;
            table.Add(new ResultEntry {
                playerId  = ps.playerId,
                crystals  = CountCrystals(ps),
                energy    = ps.energy,
                atThrone  = !ps.leftGame && IsAtThrone(ps.pos)
            });
        }

        var eligible = table.FindAll(r => r.atThrone);

        string winner = null;
        string reason;

        if (eligible.Count == 0)
        {
            winner = null;
            reason = "Time up. No one returned to the throne room.";
        }
        else
        {
            eligible.Sort((a,b) => b.crystals.CompareTo(a.crystals));
            int topC = eligible[0].crystals;
            var tieC = eligible.FindAll(r => r.crystals == topC);

            if (tieC.Count == 1)
            {
                winner = tieC[0].playerId;
                reason = "Winner by crystal count.";
            }
            else
            {
                tieC.Sort((a,b) => b.energy.CompareTo(a.energy));
                int topE = tieC[0].energy;
                var tieE = tieC.FindAll(r => r.energy == topE);

                if (tieE.Count == 1)
                {
                    winner = tieE[0].playerId;
                    reason = "Winner by energy tie-break.";
                }
                else
                {
                    string Duel(string A, string B)
                    {
                        int rollA = UnityEngine.Random.Range(0, 3);
                        int rollB = UnityEngine.Random.Range(0, 3);
                        if (rollA == rollB) rollB = (rollB + 1) % 3;
                        bool beats = (rollA == 0 && rollB == 2) || (rollA == 1 && rollB == 0) || (rollA == 2 && rollB == 1);
                        return beats ? A : B;
                    }

                    var queue = new Queue<string>();
                    foreach (var r in tieE) queue.Enqueue(r.playerId);
                    while (queue.Count > 1)
                    {
                        var a = queue.Dequeue();
                        var b = queue.Dequeue();
                        var w = Duel(a,b);
                        queue.Enqueue(w);
                    }
                    winner = queue.Dequeue();
                    reason = "Winner by sudden-death duel among tied players.";
                }
            }
        }

        lastResult = new FinalResult {
            gameOver = true,
            winnerId = winner,
            reason   = reason,
            table    = table.ToArray()
        };

        lastWinnerPlayerId = winner;

        gameStarted = false;
        modalOpen = false;

        NetworkManager.Instance?.BroadcastSnapshot();

        var enc = EncounterUI.Instance ?? FindObjectOfType<EncounterUI>(true);
        if (enc != null)
        {
            string msg = (winner != null) ? $"🏆 {winner} wins!\n{reason}" : reason;
            enc.ShowChoices(
                "Game Over",
                msg,
                new EncounterUI.Choice("Return to Start", () => UIQuitFlow.ReturnToStartPanel()),
                new EncounterUI.Choice("Host Game",      () => UIQuitFlow.ReturnToStartPanel()),
                new EncounterUI.Choice("Join Existing",  () => UIQuitFlow.ReturnToStartPanel())
            );
        }
        else
        {
            ShowFinalModal(lastResult);
        }
    }

    void ShowFinalModal(FinalResult r)
    {
        var ui = FindObjectOfType<EncounterUI>(true);
        if (ui == null) return;

        string title = string.IsNullOrEmpty(r.winnerId) ? "Game Over" : $"Winner: {r.winnerId}";
        var sb = new StringBuilder();
        sb.AppendLine(r.reason).AppendLine();
        if (r.table != null)
        {
            foreach (var row in r.table)
                sb.AppendLine($"{(row.atThrone ? "🏛️" : " ")} {row.playerId}  |  C:{row.crystals}  E:{row.energy}");
        }

        if (ui.encounterPanel != null) ui.encounterPanel.SetActive(true);
        modalOpen = true;

        if (ui.titleText != null) ui.titleText.text = title;
        if (ui.bodyText  != null) ui.bodyText.text  = sb.ToString();

        if (ui.buttonsRow != null)
        {
            for (int i = ui.buttonsRow.childCount - 1; i >= 0; i--)
                Destroy(ui.buttonsRow.GetChild(i).gameObject);
        }

        if (ui.continueBtn != null)
        {
            ui.continueBtn.gameObject.SetActive(true);
            ui.continueBtn.onClick.RemoveAllListeners();
            ui.continueBtn.onClick.AddListener(() =>
            {
                ui.Hide(); // also sets gm.modalOpen = false via EncounterUI
            });
        }
    }

    // -------------------- Main tick --------------------
    private void Update()
    {
        if (!gameStarted) return;

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Time-up first
        if (now >= timeEndUtc)
        {
            if (lastResult == null)
            {
                Debug.Log("[GM] Time up → EvaluateAndFinishGame()");
                EvaluateAndFinishGame();
            }
            return;
        }

        if (!IsTicking) return;

#if USE_FIREBASE
        FirebaseLobby.Instance?.RefreshExpiry(900);
#endif

        // Energy tick
        _tickAccum += Time.deltaTime;
        if (_tickAccum >= secondsPerFiveMinuteTick)
        {
            _tickAccum = 0f;
            foreach (var kv in players)
            {
                var ps = kv.Value;
                if (ps.leftGame) continue;
                ps.energy -= 1;
                if (ps.insideLocationIndex.HasValue) ps.energy -= 2;
                if (ps.energy < 0) ps.energy = 0;
            }
        }

        // Crystal attraction
        AttractCrystalsTick();

        // Late-arrival penalty
        if (playerAssignments.Count > 0)
        {
            bool changed = false;

            for (int i = playerAssignments.Count - 1; i >= 0; i--)
            {
                var pj = playerAssignments[i];

                if (!players.TryGetValue(pj.playerId, out var ps) || ps.leftGame)
                {
                    playerAssignments.RemoveAt(i);
                    changed = true;
                    continue;
                }

                if (ps.reachedInitial)
                {
                    playerAssignments.RemoveAt(i);
                    changed = true;
                    continue;
                }

                if (now >= pj.arrivalDeadlineUtc && !ps.arrivalPenaltyApplied)
                {
                    ps.energy = Mathf.Max(0, ps.energy - 1); // –1 once
                    ps.arrivalPenaltyApplied = true;
                    playerAssignments.RemoveAt(i);
                    changed = true;
                }
            }

            if (changed) NetworkManager.Instance?.BroadcastSnapshot();
        }

        // Fallback duel arbiter (only if net-side arbiter is OFF)
        if (NetworkManager.Instance != null &&
            NetworkManager.Instance.isHost &&
            !NetworkManager.Instance.useNetSideDuelArbiter)
        {
            CheckAndEmitDuels();
        }
    }
}
