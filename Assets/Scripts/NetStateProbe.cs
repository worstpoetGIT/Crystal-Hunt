using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

/// Drop-in, no-dependency polling probe that:
/// - Audits IDs (GM.localPlayerId, PlayerController.playerId)
/// - Prints all players (pos, LI, left, carry) every 0.25s
/// - Detects large deltas (teleports / resets to throne)
/// - Optional file logging on device
/// - Optional hooks (see NetTraceHooks below) for exact send/recv counters
[DefaultExecutionOrder(100)]
public class NetStateProbe : MonoBehaviour
{
    [Header("Output")]
    public bool logToUnityConsole = false;
    public bool showOnNetHUD = true;
    public string hudTitle = "NET TRACE";
    [Tooltip("Write samples to file in Application.persistentDataPath.")]
    public bool writeFile = true;

    [Header("Sampling")]
    [Range(0.05f, 1.0f)] public float sampleInterval = 0.25f;
    [Tooltip("Delta distance that counts as suspicious teleport.")]
    public float teleportThreshold = 2.5f;

    [Header("Extras")]
    public bool warnOnIdMismatch = true;
    public bool warnOnSinglePlayer = true; // warn if only 1 player is seen while expecting 2+

    float _timer;
    string _filePath;
    readonly Dictionary<string, Vector2> _lastPos = new();
    readonly Dictionary<string, int?> _lastLi = new();

    void Start()
    {
        if (writeFile)
        {
            var ts = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            _filePath = Path.Combine(Application.persistentDataPath, $"nettrace-{ts}.log");
            SafeAppend($"\n=== NetStateProbe started {DateTime.UtcNow:u} ===\n");
            SafeAppend($"Device: {SystemInfo.deviceModel}  OS:{SystemInfo.operatingSystem}\n");
        }
    }

    void Update()
    {
        _timer += Time.unscaledDeltaTime;
        if (_timer < sampleInterval) return;
        _timer = 0f;

        var net = NetworkManager.Instance;
        var gm  = GameManager.Instance;
        if (gm == null) return;

        string role = (net != null && net.isHost) ? "HOST" : "CLIENT";
        string localId = gm.localPlayerId ?? "(null)";
        string pcId    = FindObjectOfType<PlayerController>(true)?.playerId ?? "(no PlayerController)";
        int playerCount = gm.players?.Count ?? 0;

        // Build per-player lines
        var lines = new List<string>(8);
        lines.Add($"{role} | running:{gm.IsRunning}  now:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");
        lines.Add($"IDs: GM.local='{localId}'  PC.id='{pcId}'");
        if (net != null) lines.Add($"Port:{net.port}  useNetArbiter:{net.useNetSideDuelArbiter}");
        lines.Add($"Players:{playerCount}");

        // Detect id mismatch
        if (warnOnIdMismatch && pcId != "(no PlayerController)" && !string.Equals(localId, pcId, StringComparison.Ordinal))
            lines.Add("!! ID MISMATCH: PlayerController.playerId != GameManager.localPlayerId");

        // Compact table
        foreach (var kv in gm.players.OrderBy(k => k.Key))
        {
            var p  = kv.Value;
            string id = kv.Key;
            var pos = p.pos;
            string li = p.insideLocationIndex.HasValue ? p.insideLocationIndex.Value.ToString() : "-";
            string me = id == localId ? "*" : " ";
            int carry = p.carrying?.Count ?? 0;

            lines.Add($"{me}{id}  x:{pos.x,6:0.00} y:{pos.y,6:0.00}  LI:{li,2}  left:{p.leftGame}  E:{p.energy,2}  C:{carry}");

            // Teleport / throne reset detector
            if (_lastPos.TryGetValue(id, out var lp))
            {
                float d = (pos - lp).magnitude;
                bool liChanged = !_lastLi.TryGetValue(id, out var lli) || lli != p.insideLocationIndex;

                if (d >= teleportThreshold)
                {
                    lines.Add($"   -> Δpos {d:0.00} (prev {lp.x:0.00},{lp.y:0.00})  LI change:{liChanged}");
                }
            }
            _lastPos[id] = pos;
            _lastLi[id] = p.insideLocationIndex;
        }

        // Simple sanity warnings
        if (warnOnSinglePlayer && playerCount <= 1)
            lines.Add("!! Only one player present. If 2 devices are connected, the remote ID may be colliding or not added.");

        // Hook counters (only visible if you also wire NetworkManager to call NetTraceHooks)
        if (NetTraceHooks.Enabled)
        {
            lines.Add($"Hooks: hostPOS:{NetTraceHooks.HostPosCount}  hostSnapSent:{NetTraceHooks.HostSnapSent}  clientSnapApplied:{NetTraceHooks.ClientSnapApplied}");
        }

        // Emit
        var block = string.Join("\n", lines);
        if (logToUnityConsole) Debug.Log("[NetStateProbe]\n" + block);
        if (showOnNetHUD) SafeHUD(hudTitle, lines);
        if (writeFile) SafeAppend(block + "\n");
    }

    // ------------- helpers -------------
    static void SafeHUD(string title, List<string> lines)
    {
        try { NetDebugHUD.ShowList(title, lines); } catch { /* HUD is optional */ }
    }

    void SafeAppend(string s)
    {
        if (string.IsNullOrEmpty(_filePath)) return;
        try { File.AppendAllText(_filePath, s); } catch { }
    }
}

/// Optional static hooks you can CALL from NetworkManager (1–2 lines) to get exact counters.
/// If you don't call these, NetStateProbe still works via polling.
public static class NetTraceHooks
{
    public static bool Enabled => true;

    public static int HostPosCount { get; private set; }
    public static int HostSnapSent { get; private set; }
    public static int ClientSnapApplied { get; private set; }

    public static void HostReceivedPOS(string pid, float x, float y, int li)
    {
        HostPosCount++;
        try { NetDebugHUD.Log($"[HOOK] POS {pid} -> ({x:0.00},{y:0.00}) LI:{li}"); } catch { }
    }

    public static void HostSentSnapshot(int players, int bytes)
    {
        HostSnapSent++;
        try { NetDebugHUD.Log($"[HOOK] Sent snapshot  players:{players}  bytes:{bytes}"); } catch { }
    }

    public static void ClientAppliedSnapshot(int players)
    {
        ClientSnapApplied++;
        try { NetDebugHUD.Log($"[HOOK] Applied snapshot  players:{players}"); } catch { }
    }
}
