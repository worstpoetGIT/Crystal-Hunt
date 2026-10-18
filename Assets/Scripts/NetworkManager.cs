using UnityEngine;
using NativeWebSocket;
using System;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Linq;
using System.Collections.Generic;

public class NetworkManager : MonoBehaviour
{
    public static NetworkManager Instance { get; private set; }

    [Header("Mode & Port")]
    public bool isHost = false;
    [Range(3000, 5000)] public int port = 4000;

    private WebSocket websocket;

    // UDP transport
    private UdpClient udp;
    private readonly HashSet<IPEndPoint> knownClients = new();
    private float snapshotTimer;

    // client: host endpoint
    private IPEndPoint hostEP;

    // ---- Diagnostics (for HUD) ----
    string[] _localIPs = Array.Empty<string>();

    // ---- Client HELLO retry until first snapshot received (robust on mobile)
    string _clientPlayerId = null;
    bool _clientWaitingSnapshot = false;
    float _helloTimer = 0f;
    const float HelloRetrySeconds = 2f;

    // ---- Host snapshot rate limiting (avoid spamming mobile UDP)
    private float _snapshotCooldown = 0f;
    [Tooltip("Minimum seconds between host snapshot broadcasts triggered by POS updates")]
    public float snapshotMinInterval = 0.20f;

    [Header("Duel")]
    [Tooltip("If true, host will detect co-located players and broadcast a duel.")]
    public bool useNetSideDuelArbiter = true;
    [Tooltip("Seconds to wait before the same pair can be matched again. (Kept, but pair-lock also enforces single round per co-location)")]
    public float duelCooldownSeconds = 2f;

    // cooldown cache for time-based throttling (still OK to keep)
    private readonly Dictionary<string, float> _duelCooldownUntil = new(32);

    // --- Endpoint ↔ AssignedId mapping to prevent ID collisions ---
    // Key: "ip:port" → assigned unique id on host
    private readonly Dictionary<string, string> epToAssignedId = new(32);
    // Reverse: assignedId → last seen endpoint
    private readonly Dictionary<string, IPEndPoint> assignedIdToEP = new(32);

    // --- Turn-based duel state (host only) ---
    // pairKey = "a|b" (sorted)
    private readonly Dictionary<string, int> _pendingDuelChoiceA = new(8); // host's choice waiting for client
    // Single-round-per-entry: lock each pair while both are co-located; cleared when they separate
    private readonly HashSet<string> _pairLocks = new(); // key: $"LI{li}:{PairKey(a,b)}"

    void Awake()
    {
        if (Instance == null) { Instance = this; DontDestroyOnLoad(gameObject); }
        else { Destroy(gameObject); }
    }

    void OnValidate() => port = Mathf.Clamp(port, 3000, 5000);

    // ---------------- Host / Client bootstrap ----------------

    public bool StartHost(int p)
    {
        port = Mathf.Clamp(p, 3000, 5000);
        try
        {
            StopAll();
            udp = new UdpClient(port);
            udp.Client.Blocking = false;
            try { udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true); } catch { }

            isHost = true;
            knownClients.Clear();
            snapshotTimer = 0f;
            _snapshotCooldown = 0f;
            _duelCooldownUntil.Clear();
            epToAssignedId.Clear();
            assignedIdToEP.Clear();
            _pendingDuelChoiceA.Clear();
            _pairLocks.Clear();

            _localIPs = IPv4Addrs();
            UpdateHostHUD();

            Debug.Log($"[Host/UDP] Listening on {string.Join(", ", _localIPs)}:{port}");
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Host/UDP] {ex.Message}");
            NetDebugHUD.ShowEssential("Mode: HOST", $"Port: {port}", $"ERR: {ex.Message}");
            return false;
        }
    }

    public bool StartClient(string ip, int p)
    {
        port = Mathf.Clamp(p, 3000, 5000);
        try
        {
            StopAll();
            udp = new UdpClient();
            udp.Client.Blocking = false;
            try { udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true); } catch { }

            isHost = false;
            hostEP = new IPEndPoint(IPAddress.Parse(ip), port);

            _localIPs = IPv4Addrs();
            UpdateClientHUD(ip, port, "Init OK");

            Debug.Log($"[Client/UDP] Ready; host={ip}:{port} local={string.Join(", ", _localIPs)}");
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Client/UDP] {ex.Message}");
            NetDebugHUD.ShowEssential("Mode: CLIENT", $"Join → {ip}:{port}", $"ERR: {ex.Message}");
            return false;
        }
    }

    public void StopAll()
    {
        try { udp?.Close(); } catch { }
        udp = null;
        knownClients.Clear();

        _clientPlayerId = null;
        _clientWaitingSnapshot = false;
        _helloTimer = 0f;
        _snapshotCooldown = 0f;
        _duelCooldownUntil.Clear();

        epToAssignedId.Clear();
        assignedIdToEP.Clear();

        _pendingDuelChoiceA.Clear();
        _pairLocks.Clear();
    }

    // ---------------- Messages ----------------

    /// <summary>
    /// Client → Host: send position (~5Hz from PlayerController).
    /// </summary>
    public void ClientReportPosition(string playerId, Vector2 pos, int? insideLi)
    {
        if (udp == null || isHost || hostEP == null) return;
        string li = insideLi.HasValue ? insideLi.Value.ToString() : "-1";
        string msg = $"POS:{playerId}:{pos.x:F3}:{pos.y:F3}:{li}";
        var bytes = Encoding.UTF8.GetBytes(msg);
        try { udp.Send(bytes, bytes.Length, hostEP); } catch { }
    }

    public void SendHello(string playerId)
    {
        if (udp == null || hostEP == null) { Debug.Log("[Client/UDP] HELLO aborted: udp/hostEP null"); return; }

        _clientPlayerId = playerId;
        _clientWaitingSnapshot = true;
        _helloTimer = 0f;

        var bytes = Encoding.UTF8.GetBytes("HELLO:" + _clientPlayerId);
        udp.Send(bytes, bytes.Length, hostEP);
        UpdateClientHUD(hostEP.Address.ToString(), hostEP.Port, $"HELLO → '{playerId}'");
    }

    void SendSnapshotTo(IPEndPoint ep)
    {
        if (udp == null) return;
        var gm = GameManager.Instance;
        var snap = gm.BuildSnapshot();
        string json = JsonUtility.ToJson(snap);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        try { udp.Send(bytes, bytes.Length, ep); } catch { }
        // keep HUD quiet for snapshots
    }

    public void BroadcastSnapshot()
    {
        if (udp == null || knownClients.Count == 0) return;
        foreach (var ep in knownClients) SendSnapshotTo(ep);
    }

    // ---------------- Duel messaging (turn-based) ----------------

    // Host opens duel UI locally and notifies the opponent client only
    public void BroadcastDuel(string aId, string bId)
    {
        if (udp == null || !isHost) return;

        // show on host (A) locally
        EncounterUI.Instance?.ShowDuel(aId, bId);

        string msg = $"DUEL:{aId}:{bId}";
        var bytes = Encoding.UTF8.GetBytes(msg);

        // Try direct to B only; fallback broadcast (clients not involved should ignore).
        if (!SendToAssigned(bId, bytes))
        {
            foreach (var ep in knownClients) { try { udp.Send(bytes, bytes.Length, ep); } catch { } }
        }

        NetDebugHUD.ShowEssential("Mode: HOST", $"Port: {port}", $"Duel: {aId} vs {bId}");
    }

    // Host picked choice; tell B to enable their turn and remember host choice
    public void HostSendDuelHostChoice(string aId, string bId, int aChoice)
    {
        if (udp == null || !isHost) return;

        string pkey = PairKey(aId, bId);
        _pendingDuelChoiceA[pkey] = Mathf.Clamp(aChoice, 0, 2);

        string msg = $"DUEL_HOSTCHOICE:{aId}:{bId}:{aChoice}";
        var bytes = Encoding.UTF8.GetBytes(msg);

        if (!SendToAssigned(bId, bytes))
        {
            foreach (var ep in knownClients) { try { udp.Send(bytes, bytes.Length, ep); } catch { } }
        }
    }

    // Client picked choice; send to host for resolution
    public void ClientSendDuelClientChoice(string aId, string bId, int bChoice)
    {
        if (udp == null || isHost || hostEP == null) return;
        string msg = $"DUEL_CLIENTCHOICE:{aId}:{bId}:{Mathf.Clamp(bChoice, 0, 2)}";
        var bytes = Encoding.UTF8.GetBytes(msg);
        try { udp.Send(bytes, bytes.Length, hostEP); } catch { }
    }

    // Host resolved: inform B (and any others if broadcast fallback used)
    void BroadcastDuelResult(string aId, string bId, int aChoice, int bChoice, string winner, bool paidNow)
    {
        if (udp == null) return;

        string msg = $"DUEL_RESULT:{aId}:{bId}:{aChoice}:{bChoice}:{winner}:{(paidNow ? 1 : 0)}";
        var bytes = Encoding.UTF8.GetBytes(msg);

        // Prefer direct to the participant client
        if (!SendToAssigned(bId, bytes))
        {
            foreach (var ep in knownClients) { try { udp.Send(bytes, bytes.Length, ep); } catch { } }
        }
    }

    // Send bytes to a specific assigned player; returns true if direct endpoint known/sent
    bool SendToAssigned(string playerId, byte[] bytes)
    {
        if (assignedIdToEP.TryGetValue(playerId, out var ep))
        {
            try { udp.Send(bytes, bytes.Length, ep); } catch { }
            return true;
        }
        return false;
    }

    public void HostSendRiddleTo(string targetPlayerId, int locationIndex, int riddleIdx)
    {
        if (udp == null || !isHost) return;

        var gm = GameManager.Instance;
        // If the host themselves is the target, open locally.
        if (gm != null && targetPlayerId == gm.localPlayerId)
        {
            EncounterUI.Instance?.ShowRiddle(targetPlayerId, riddleIdx, "Sage");
            return;
        }

        string msg = $"RIDDLE:{targetPlayerId}:{locationIndex}:{riddleIdx}";
        var bytes  = Encoding.UTF8.GetBytes(msg);

        if (!SendToAssigned(targetPlayerId, bytes))
        {
            foreach (var k in knownClients) { try { udp.Send(bytes, bytes.Length, k); } catch { } }
        }
    }

    // ---------------- Update loop ----------------

    void Update()
    {
    #if !UNITY_WEBGL || UNITY_EDITOR
        websocket?.DispatchMessageQueue();
    #endif
        if (udp == null) return;

        if (isHost)
        {
            bool stateChanged = false;

            while (udp.Available > 0)
            {
                IPEndPoint any = new IPEndPoint(IPAddress.Any, 0);
                byte[] bytes;
                try { bytes = udp.Receive(ref any); }
                catch { break; }

                string msg = Encoding.UTF8.GetString(bytes);
                string epKey = any.ToString(); // "ip:port"

                // HELLO:<playerId>
                if (msg.StartsWith("HELLO:"))
                {
                    string wantedId = msg.Substring(6);
                    knownClients.Add(any);

                    var gm = GameManager.Instance;

                    // Choose an assignedId that is unique in the host dictionary
                    string assignedId = wantedId;

                    bool collidesWithHost = (assignedId == gm.localPlayerId);
                    bool collidesWithExisting =
                        gm.players.ContainsKey(assignedId) &&
                        (!assignedIdToEP.TryGetValue(assignedId, out var bound) || bound.ToString() != epKey);

                    if (collidesWithHost || collidesWithExisting)
                    {
                        var suffix = any.Address.ToString().Split('.').LastOrDefault() ?? UnityEngine.Random.Range(100, 999).ToString();
                        assignedId = $"{wantedId}-{suffix}";
                        Debug.LogWarning($"[HOST] HELLO id collision: '{wantedId}' → assigned '{assignedId}' for {epKey}");
                    }

                    epToAssignedId[epKey] = assignedId;
                    assignedIdToEP[assignedId] = any;

                    bool isNew = !gm.players.ContainsKey(assignedId);
                    if (isNew)
                    {
                        var pj = gm.AssignInitialTarget(assignedId);
                        gm.EnsurePlayer(assignedId);
                        gm.HostSetPlayerPosition(assignedId, pj.initialTarget); // authoritative initial drop
                        stateChanged = true;
                    }

                    // reply snapshot (first join / rejoin)
                    SendSnapshotTo(any);

                    // essentials on HUD
                    UpdateHostHUD($"{any.Address}");
                    continue;
                }

                // POS:<playerId>:<x>:<y>:<li|-1>
                if (msg.StartsWith("POS:"))
                {
                    var parts = msg.Split(':');
                    if (parts.Length >= 5)
                    {
                        string pidText = parts[1];
                        if (float.TryParse(parts[2], out var x) &&
                            float.TryParse(parts[3], out var y) &&
                            int.TryParse(parts[4], out var _ /*liVal ignored; host computes entry/exit*/))
                        {
                            var gm = GameManager.Instance;
                            string assignedId;

                            // If we already mapped this endpoint, use the assigned id.
                            if (!epToAssignedId.TryGetValue(epKey, out assignedId))
                            {
                                // First contact from this endpoint (maybe POS before HELLO).
                                // Create a safe assignedId that won't collide with host or another client.
                                assignedId = pidText;

                                bool collidesWithHost = (assignedId == gm.localPlayerId);
                                bool collidesWithExisting =
                                    gm.players.ContainsKey(assignedId) &&
                                    (!assignedIdToEP.TryGetValue(assignedId, out var bound) || bound.ToString() != epKey);

                                if (collidesWithHost || collidesWithExisting)
                                {
                                    var suffix = any.Address.ToString().Split('.').LastOrDefault() ?? UnityEngine.Random.Range(100, 999).ToString();
                                    assignedId = $"{pidText}-{suffix}";
                                    Debug.LogWarning($"[HOST] POS pre-HELLO id collision: '{pidText}' → assigned '{assignedId}' for {epKey}");
                                }

                                epToAssignedId[epKey] = assignedId;
                                assignedIdToEP[assignedId] = any;

                                if (!gm.players.ContainsKey(assignedId))
                                {
                                    var pj = gm.AssignInitialTarget(assignedId);
                                    gm.EnsurePlayer(assignedId);
                                    gm.HostSetPlayerPosition(assignedId, pj.initialTarget);
                                }
                            }
                            else if (assignedId != pidText)
                            {
                                // Informative log: translating claimed id to assigned id
                                NetDebugHUD.Log($"[HOST] Translating POS '{pidText}' → '{assignedId}' (ep {epKey})");
                            }

                            knownClients.Add(any);

                            // IMPORTANT: use the host’s state machine (enter/exit, duels, throttled snaps)
                            gm.HostSetPlayerPosition(assignedId, new Vector2(x, y));
                            stateChanged = true;
                        }
                    }
                    continue;
                }

                // DUEL_CLIENTCHOICE:aId:bId:bChoice   (client → host)
                if (msg.StartsWith("DUEL_CLIENTCHOICE:"))
                {
                    var parts = msg.Split(':');
                    // 0=DUEL_CLIENTCHOICE 1=aId 2=bId 3=bChoice
                    if (parts.Length >= 4 && int.TryParse(parts[3], out var bChoice))
                    {
                        string aId = parts[1];
                        string bId = parts[2];
                        string pkey = PairKey(aId, bId);

                        if (_pendingDuelChoiceA.TryGetValue(pkey, out var aChoice))
                        {
                            // Resolve on host
                            var em = EncounterManager.Instance;
                            var (winner, loser, paidNow, text) = em.ResolveDuelWithChoices(aId, bId, aChoice, Mathf.Clamp(bChoice, 0, 2));

                            // Show on host immediately
                            EncounterUI.Instance?.ShowDuelResult(aChoice, bChoice, winner, paidNow);

                            // Inform client
                            BroadcastDuelResult(aId, bId, aChoice, bChoice, winner, paidNow);

                            _pendingDuelChoiceA.Remove(pkey);
                            // Movement/energy/crystal changes were done in EncounterManager; push state
                            BroadcastSnapshot();
                        }
                    }
                    continue;
                }

                // ignore other lines
            }

            // Rate-limited broadcast when something changed from POS/HELLO
            if (stateChanged)
            {
                _snapshotCooldown -= Time.deltaTime;
                if (_snapshotCooldown <= 0f)
                {
                    BroadcastSnapshot();
                    _snapshotCooldown = snapshotMinInterval;
                }
            }

            // Periodic recovery snapshot (30s)
            snapshotTimer += Time.deltaTime;
            if (snapshotTimer >= 30f)
            {
                snapshotTimer = 0f;
                BroadcastSnapshot();
            }

            // Net-side duel arbiter (host authoritative)
            if (useNetSideDuelArbiter)
                TryEmitDuelsHost();
        }
        else
        {
            // Client: apply snapshots and handle duel/riddle messages
            bool gotSnapshotThisFrame = false;

            while (udp.Available > 0)
            {
                IPEndPoint any = new IPEndPoint(IPAddress.Any, 0);
                byte[] bytes;
                try { bytes = udp.Receive(ref any); }
                catch { break; }

                string text = Encoding.UTF8.GetString(bytes);
                if (string.IsNullOrEmpty(text)) continue;

                if (text[0] == '{')
                {
                    // snapshot
                    try
                    {
                        var snap = JsonUtility.FromJson<FullSnapshot>(text);
                        GameManager.Instance.ApplySnapshot(snap);
                        gotSnapshotThisFrame = true;
                    }
                    catch { /* ignore parse noise */ }
                    continue;
                }

                // DUEL:aId:bId  (host → client target)
                if (text.StartsWith("DUEL:"))
                {
                    var parts = text.Split(':');
                    if (parts.Length >= 3)
                    {
                        string aId = parts[1];
                        string bId = parts[2];

                        var gm = GameManager.Instance;
                        if (gm != null && gm.localPlayerId == bId)
                        {
                            EncounterUI.Instance?.ShowDuel(aId, bId);
                            NetDebugHUD.ShowEssential("Mode: CLIENT", $"Duel: {aId} vs {bId}");
                        }
                    }
                    continue;
                }

                // DUEL_HOSTCHOICE:aId:bId:aChoice (host → client B)
                if (text.StartsWith("DUEL_HOSTCHOICE:"))
                {
                    var parts = text.Split(':');
                    if (parts.Length >= 4 && int.TryParse(parts[3], out var aChoice))
                    {
                        string aId = parts[1];
                        string bId = parts[2];
                        var gm = GameManager.Instance;
                        if (gm != null && gm.localPlayerId == bId)
                        {
                            EncounterUI.Instance?.EnableClientTurn(aId, bId, Mathf.Clamp(aChoice, 0, 2));
                        }
                    }
                    continue;
                }

                // DUEL_RESULT:aId:bId:aChoice:bChoice:winner:paidNowInt (host → client B)
                if (text.StartsWith("DUEL_RESULT:"))
                {
                    var parts = text.Split(':');
                    if (parts.Length >= 7 &&
                        int.TryParse(parts[3], out var aC) &&
                        int.TryParse(parts[4], out var bC))
                    {
                        string aId = parts[1];
                        string bId = parts[2];
                        string winner = parts[5];
                        bool paidNow = (parts[6] == "1");

                        var gm = GameManager.Instance;
                        if (gm != null && (gm.localPlayerId == bId))
                        {
                            EncounterUI.Instance?.ShowDuelResult(Mathf.Clamp(aC,0,2), Mathf.Clamp(bC,0,2), winner, paidNow);
                        }
                    }
                    continue;
                }

                if (text.StartsWith("RIDDLE:"))
                {
                    // RIDDLE:<playerId>:<li>:<riddleIdx>
                    var parts = text.Split(':');
                    if (parts.Length >= 4)
                    {
                        string pid = parts[1];
                        int rid = int.TryParse(parts[3], out var tmpR) ? tmpR : -1;

                        var gm = GameManager.Instance;
                        if (gm != null && rid >= 0 && gm.localPlayerId == pid)
                        {
                            EncounterUI.Instance?.ShowRiddle(pid, rid, "Sage");
                            NetDebugHUD.Log($"[CLIENT] Riddle for {pid} (idx {rid})");
                        }
                    }
                    continue;
                }
            }

            if (gotSnapshotThisFrame && _clientWaitingSnapshot)
            {
                _clientWaitingSnapshot = false;
                UpdateClientHUD(hostEP?.Address?.ToString() ?? "?", port, "Snapshot ");
            }

            // Retry HELLO until first snapshot arrives (quiet)
            if (_clientWaitingSnapshot && !string.IsNullOrEmpty(_clientPlayerId) && hostEP != null)
            {
                _helloTimer += Time.deltaTime;
                if (_helloTimer >= HelloRetrySeconds)
                {
                    _helloTimer = 0f;
                    try
                    {
                        var bytes = Encoding.UTF8.GetBytes("HELLO:" + _clientPlayerId);
                        udp.Send(bytes, bytes.Length, hostEP);
                    }
                    catch { }
                }
            }
        }
    }

    async void OnApplicationQuit()
    {
    #if USE_FIREBASE
        FirebaseLobby.Instance?.HostUnpublishLobby();
    #endif
        if (websocket != null) await websocket.Close();
        StopAll();
    }

    // --------- Duel helper (host-only) ---------

    static string PairKey(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return "";
        return string.CompareOrdinal(a, b) <= 0 ? $"{a}|{b}" : $"{b}|{a}";
    }

    void TryEmitDuelsHost()
{
    if (!isHost) return;
    var gm = GameManager.Instance;
    if (gm == null || gm.modalOpen) return;

    // Group eligible players (not left, inside a location, not yet dueled this entry)
    var groups = new Dictionary<int, List<string>>();
    foreach (var kv in gm.players)
    {
        var ps = kv.Value;
        if (ps == null || ps.leftGame) continue;
        if (!ps.insideLocationIndex.HasValue) continue;
        if (ps.dueledSinceEntry) continue;                    //  only players who haven't dueled this entry

        int li = ps.insideLocationIndex.Value;
        if (!groups.TryGetValue(li, out var list))
        {
            list = new List<string>(4);
            groups[li] = list;
        }
        list.Add(ps.playerId);
    }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
    if (groups.Count > 0)
    {
        var sb = new System.Text.StringBuilder("[DUEL] eligible groups: ");
        foreach (var kv2 in groups)
            sb.Append($"LI{kv2.Key}=[{string.Join(",", kv2.Value)}] ");
        Debug.Log(sb.ToString());
    }
#endif

    // For each location: if 2+ eligible players → pick earliest two entrants and duel (with cooldown)
    foreach (var kv in groups)
    {
        var li = kv.Key;
        var list = kv.Value;
        if (list.Count < 2) continue;

        // Sort by lastEnterTime (earliest first). If tie, sort by playerId for determinism.
        list.Sort((a,b) =>
        {
            float ta = gm.players[a].lastEnterTime;
            float tb = gm.players[b].lastEnterTime;
            int tcmp = ta.CompareTo(tb);
            return (tcmp != 0) ? tcmp : string.CompareOrdinal(a, b);
        });

        string aId = list[0];
        string bId = list[1];

        string key = PairKey(aId, bId);
        float now = Time.time;
        if (_duelCooldownUntil.TryGetValue(key, out var until) && now < until) continue;

        _duelCooldownUntil[key] = now + Mathf.Max(0.5f, duelCooldownSeconds);

        // Fire duel (will open on host and broadcast to clients)
        BroadcastDuel(aId, bId);
        break; // one duel per frame; modal will prevent re-entry until resolved
    }
}


    // --------- Minimal helpers for HUD ---------
    static string[] IPv4Addrs()
    {
        var list = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(ua.Address))
                        list.Add(ua.Address.ToString());
                }
            }
        }
        catch { }
        return list.Distinct().ToArray();
    }

    static bool LikelySameSubnet(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        var ap = a.Split('.'); var bp = b.Split('.');
        if (ap.Length < 3 || bp.Length < 3) return false;
        return ap[0] == bp[0] && ap[1] == bp[1] && ap[2] == bp[2];
    }

    void UpdateHostHUD(string lastHelloIp = null)
    {
        var my = string.Join(", ", _localIPs);
        var lines = new List<string>
        {
            "Mode: HOST",
            $"Port: {port}",
            $"My IPs: {my}",
            $"Clients: {knownClients.Count}"
        };
        if (!string.IsNullOrEmpty(lastHelloIp)) lines.Add($"Last HELLO: {lastHelloIp}");
        NetDebugHUD.ShowList("NET HUD", lines);
    }

    void UpdateClientHUD(string joinIp, int joinPort, string status = null)
    {
        var my = string.Join(", ", _localIPs);
        string firstLocal = _localIPs.FirstOrDefault() ?? "";
        bool subnetOk = LikelySameSubnet(firstLocal, joinIp);
        NetDebugHUD.ShowEssential(
            "Mode: CLIENT",
            $"My IPs: {my}",
            $"Join → {joinIp}:{joinPort}",
            $"Same subnet? {(subnetOk ? " YES" : "❌ NO")}",
            status
        );
    }
}
