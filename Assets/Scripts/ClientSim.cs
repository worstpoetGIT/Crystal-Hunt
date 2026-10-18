using UnityEngine;
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;

/// <summary>
/// Toggleable simulated client that behaves like a real client:
/// - Sends HELLO:<simulatedPlayerId>
/// - Receives FullSnapshot JSON
/// - Moves smoothly toward a chosen target player's position (no teleport)
/// - Sends POS:<id>:<x>:<y>:<li|-1> at a steady rate
/// - Computes insideLocationIndex locally from the latest snapshot (like a real client)
/// </summary>
public class ClientSim : MonoBehaviour
{
    [Header("Toggle")]
    public bool enableSimulation = false;

    [Header("Connection")]
    public string hostIp = "127.0.0.1";
    [Range(3000, 5000)] public int port = 4000;

    [Header("Identity")]
    public string simulatedPlayerId = "SimPlayer-9999";

    [Header("Follow Target")]
    [Tooltip("If empty, the sim will automatically follow the first other player it sees in snapshots.")]
    public string followTargetPlayerId = "";

    [Header("Movement")]
    [Tooltip("Units per second the simulated player moves.")]
    public float moveSpeed = 3f;
    [Tooltip("How often to send POS updates (seconds). Real client is ~5–10 Hz.")]
    public float posSendInterval = 0.12f; // ~8 Hz

    // UDP
    UdpClient _udp;
    IPEndPoint _hostEP;

    // last snapshot (for inspection)
    [TextArea(2, 6)] public string lastSnapshotJson = "";
    public int lastLocations = 0;
    public int lastCrystals = 0;
    public int lastNpcs = 0;
    public int lastAssignments = 0;

    // internals
    bool _running = false;
    float _helloTimer = 0f;
    const float HelloRetrySeconds = 5f;

    // sim state
    FullSnapshot _lastSnap = null;
    Vector2 _simPos;
    bool _havePos = false;
    float _posSendTimer = 0f;
    int _lastLi = -2; // sentinel so the first calc logs
    string _resolvedFollowId = null;

    void Start()     => TryStartSim();
    void OnEnable()  => TryStartSim();

    void Update()
    {
        // Allow toggling at runtime
        if (enableSimulation && !_running)
            TryStartSim();
        else if (!enableSimulation && _running)
        {
            Cleanup();
            _running = false;
            return;
        }

        if (!_running || _udp == null) return;

        // --- Receive snapshots ---
        while (_udp.Available > 0)
        {
            IPEndPoint any = new IPEndPoint(IPAddress.Any, 0);
            byte[] bytes;
            try { bytes = _udp.Receive(ref any); }
            catch { break; }

            string text = Encoding.UTF8.GetString(bytes);
            if (string.IsNullOrEmpty(text)) continue;

            // Snapshot JSON?
            if (text[0] == '{')
            {
                lastSnapshotJson = text;
                try
                {
                    var snap = JsonUtility.FromJson<FullSnapshot>(text);
                    _lastSnap = snap;

                    lastLocations   = snap.locations?.Length   ?? 0;
                    lastCrystals    = snap.crystals?.Length    ?? 0;
                    lastNpcs        = snap.npcs?.Length        ?? 0;
                    lastAssignments = snap.playerAssignments?.Length ?? 0;

                    // Stop HELLO retries after first snapshot
                    _helloTimer = -999f;

                    // Initialize sim starting pos to assigned target (if provided once)
                    if (!_havePos)
                    {
                        bool foundAssign = false;
                        if (snap.playerAssignments != null)
                        {
                            for (int i = 0; i < snap.playerAssignments.Length; i++)
                            {
                                var a = snap.playerAssignments[i];
                                if (a.playerId == simulatedPlayerId)
                                {
                                    _simPos = a.initialTarget;
                                    foundAssign = true;
                                    Debug.Log($"[ClientSim] Spawn at assignment {a.initialTarget} (deadline {a.arrivalDeadlineUtc})");
                                    break;
                                }
                            }
                        }
                        if (!foundAssign)
                        {
                            _simPos = snap.throne; // fallback
                            Debug.Log($"[ClientSim] Spawn at throne {_simPos}");
                        }
                        _havePos = true;
                        _lastLi = -2; // force recalc log
                    }

                    // Choose follow target if not explicitly set/resolved
                    if (string.IsNullOrEmpty(_resolvedFollowId))
                    {
                        if (!string.IsNullOrEmpty(followTargetPlayerId) && snap.players != null)
                        {
                            for (int i = 0; i < snap.players.Length; i++)
                            {
                                if (snap.players[i].playerId == followTargetPlayerId)
                                {
                                    _resolvedFollowId = followTargetPlayerId;
                                    break;
                                }
                            }
                        }
                        if (string.IsNullOrEmpty(_resolvedFollowId) && snap.players != null)
                        {
                            for (int i = 0; i < snap.players.Length; i++)
                            {
                                var pl = snap.players[i];
                                if (pl.playerId != simulatedPlayerId)
                                {
                                    _resolvedFollowId = pl.playerId;
                                    break;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[ClientSim] Snapshot parse fail: " + ex.Message);
                }
                continue;
            }

            // (We ignore plain-text DUEL broadcasts here; not needed for the sim to behave like a client.)
        }

        // --- Move towards target & send POS like a real client ---
        if (_lastSnap != null && _havePos)
        {
            Vector2 target = ChooseTargetPosition(_lastSnap);
            if ((target - _simPos).sqrMagnitude > 0.0001f)
            {
                var step = moveSpeed * Time.deltaTime;
                var to   = target - _simPos;
                var len  = to.magnitude;
                if (len <= step) _simPos = target;
                else             _simPos += to / len * step;
            }

            // Compute insideLocationIndex locally (like a real client would)
            int li = ComputeLocationIndex(_simPos, _lastSnap);
            if (li != _lastLi)
            {
                Debug.Log($"[ClientSim] insideLocationIndex: {(_lastLi < 0 ? "-1" : _lastLi.ToString())} → {(li < 0 ? "-1" : li.ToString())}");
                _lastLi = li;
            }

            _posSendTimer += Time.deltaTime;
            if (_posSendTimer >= posSendInterval)
            {
                _posSendTimer = 0f;
                SendPOS(simulatedPlayerId, _simPos, li);
            }
        }

        // --- Auto-resend HELLO until the first snapshot arrives ---
        if (_helloTimer >= 0f) // negative value disables retries
        {
            _helloTimer += Time.deltaTime;
            if (_helloTimer >= HelloRetrySeconds)
            {
                _helloTimer = 0f;
                SendHello();
            }
        }
    }

    void OnDisable()         { Cleanup(); _running = false; }
    void OnDestroy()         { Cleanup(); _running = false; }
    void OnApplicationQuit() { Cleanup(); }

    // ---------- Helpers ----------

    void TryStartSim()
    {
        if (_running || !enableSimulation) return;

        try
        {
            _udp = new UdpClient();                 // ephemeral local port
            _udp.Client.Blocking = false;
            _hostEP = new IPEndPoint(IPAddress.Parse(hostIp), port);

            // Reset state
            lastSnapshotJson = "";
            lastLocations = lastCrystals = lastNpcs = lastAssignments = 0;
            _helloTimer = 0f;                       // begin retry window
            _lastSnap = null;
            _havePos = false;
            _resolvedFollowId = null;
            _posSendTimer = 0f;
            _lastLi = -2;

            SendHello();
            _running = true;
            Debug.Log($"[ClientSim] Started; sending HELLO to {hostIp}:{port} as '{simulatedPlayerId}'");
        }
        catch (Exception ex)
        {
            Debug.LogError("[ClientSim] " + ex.Message);
            enableSimulation = false;
            _running = false;
            Cleanup();
        }
    }

    void Cleanup()
    {
        try { _udp?.Close(); } catch { /* ignore */ }
        _udp = null;
    }

    [ContextMenu("Send HELLO")]
    public void SendHello()
    {
        if (_udp == null || _hostEP == null) return;
        var msg = Encoding.UTF8.GetBytes("HELLO:" + simulatedPlayerId);
        _udp.Send(msg, msg.Length, _hostEP);
        Debug.Log($"[ClientSim] HELLO sent for {simulatedPlayerId}");
    }

    void SendPOS(string playerId, Vector2 pos, int li)
    {
        if (_udp == null || _hostEP == null) return;
        string liStr = (li >= 0) ? li.ToString() : "-1";
        string line = $"POS:{playerId}:{pos.x:F3}:{pos.y:F3}:{liStr}";
        var bytes = Encoding.UTF8.GetBytes(line);
        try { _udp.Send(bytes, bytes.Length, _hostEP); } catch { }
        // Debug.Log($"[ClientSim] POS → {line}");
    }

    Vector2 ChooseTargetPosition(FullSnapshot snap)
    {
        // If we have a specific target id and it's present, follow its current position.
        if (!string.IsNullOrEmpty(_resolvedFollowId) && snap.players != null)
        {
            for (int i = 0; i < snap.players.Length; i++)
            {
                var pl = snap.players[i];
                if (pl.playerId == _resolvedFollowId)
                {
                    if (pl.insideLocationIndexOrMinus1 >= 0 &&
                        snap.locations != null &&
                        pl.insideLocationIndexOrMinus1 < snap.locations.Length)
                    {
                        var lr = snap.locations[pl.insideLocationIndexOrMinus1];
                        return new Vector2((lr.bl.x + lr.tr.x) * 0.5f, (lr.bl.y + lr.tr.y) * 0.5f);
                    }
                    return pl.pos; // otherwise, follow their last known pos
                }
            }
        }

        // Fallbacks: someone else, then throne
        if (snap.players != null)
        {
            for (int i = 0; i < snap.players.Length; i++)
            {
                var other = snap.players[i];
                if (other.playerId != simulatedPlayerId)
                {
                    if (other.insideLocationIndexOrMinus1 >= 0 &&
                        snap.locations != null &&
                        other.insideLocationIndexOrMinus1 < snap.locations.Length)
                    {
                        var lr = snap.locations[other.insideLocationIndexOrMinus1];
                        return new Vector2((lr.bl.x + lr.tr.x) * 0.5f, (lr.bl.y + lr.tr.y) * 0.5f);
                    }
                    return other.pos;
                }
            }
        }

        return snap.throne; // last resort
    }

    int ComputeLocationIndex(Vector2 p, FullSnapshot snap)
    {
        if (snap.locations == null || snap.locations.Length == 0) return -1;
        for (int i = 0; i < snap.locations.Length; i++)
        {
            var lr = snap.locations[i];
            if (p.x >= lr.bl.x && p.x <= lr.tr.x && p.y >= lr.bl.y && p.y <= lr.tr.y)
                return i;
        }
        return -1;
        }
}
