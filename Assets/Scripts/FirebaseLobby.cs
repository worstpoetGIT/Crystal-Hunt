#if USE_FIREBASE
using System;
using System.Collections.Generic;
using Firebase.Analytics;
using Firebase.Database;
using Firebase.Extensions;
using UnityEngine;
using System.Net;                     // NEW
using System.Net.NetworkInformation; // NEW

public class FirebaseLobby : MonoBehaviour
{
    public static FirebaseLobby Instance { get; private set; }
    DatabaseReference _db;
    string _lobbyKey;        // path: lobbies/<lobbyKey>

    [Serializable]
    public class LobbyEntry
    {
        public string gameName;
        public string hostIP;
        public int    port;
        public long   expiresAt; // unix ms
    }

    void Awake()
    {
        if (Instance == null) { Instance = this; DontDestroyOnLoad(gameObject); }
        else { Destroy(gameObject); return; }
        if (!FirebaseInit.Ready) return;
        _db = FirebaseDatabase.DefaultInstance.RootReference;
    }

    void EnsureDb()
    {
        if (!FirebaseInit.Ready) return;
        if (_db == null) _db = FirebaseDatabase.DefaultInstance.RootReference;
    }

    // ---------------- LOBBY ----------------

    public void HostPublishLobby(string gameName, string hostIp, int port, int ttlSeconds = 600)
    {
        EnsureDb();
        if (!FirebaseInit.Ready || _db == null) return;

        // ---- Guard: never publish loopback/unspecified/missing IPs ----
        hostIp = SanitizeHostIp(hostIp);
        if (string.IsNullOrEmpty(hostIp))
        {
            Debug.LogWarning("[FirebaseLobby] No valid LAN IP found; not publishing lobby.");
            return; // hard-stop if we truly can't find an IP
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var entry = new LobbyEntry {
            gameName = string.IsNullOrWhiteSpace(gameName) ? "CrystalGame" : gameName.Trim(),
            hostIP   = hostIp,
            port     = port,
            expiresAt= now + ttlSeconds*1000L
        };

        if (string.IsNullOrEmpty(_lobbyKey))
            _lobbyKey = _db.Child("lobbies").Push().Key;

        string json = JsonUtility.ToJson(entry);
        _db.Child("lobbies").Child(_lobbyKey).SetRawJsonValueAsync(json);
        Debug.Log($"[FirebaseLobby] Published lobby {_lobbyKey} {entry.gameName} @ {entry.hostIP}:{entry.port}");
    }

    public void HostUnpublishLobby()
    {
        EnsureDb();
        if (!FirebaseInit.Ready || _db == null || string.IsNullOrEmpty(_lobbyKey)) return;
        _db.Child("lobbies").Child(_lobbyKey).RemoveValueAsync();
        Debug.Log("[FirebaseLobby] Unpublished lobby");
        _lobbyKey = null;
    }

    public void RefreshExpiry(int extraSeconds = 600)
    {
        EnsureDb();
        if (!FirebaseInit.Ready || _db == null || string.IsNullOrEmpty(_lobbyKey)) return;
        long newExp = DateTimeOffset.UtcNow.AddSeconds(extraSeconds).ToUnixTimeMilliseconds();
        _db.Child("lobbies").Child(_lobbyKey).Child("expiresAt").SetValueAsync(newExp);
    }

    public void ClientFetchLobbies(Action<List<LobbyEntry>> callback)
    {
        EnsureDb();
        if (!FirebaseInit.Ready || _db == null) { callback?.Invoke(new List<LobbyEntry>()); return; }
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _db.Child("lobbies").GetValueAsync().ContinueWithOnMainThread(t =>
        {
            var list = new List<LobbyEntry>();
            if (t.IsCompletedSuccessfully && t.Result != null && t.Result.Exists)
            {
                foreach (var child in t.Result.Children)
                {
                    try {
                        var e = JsonUtility.FromJson<LobbyEntry>(child.GetRawJsonValue());
                        if (e != null && e.expiresAt > now) list.Add(e);
                    } catch { /* skip */ }
                }
            }
            callback?.Invoke(list);
        });
    }

    public void LoadLobbyAsync(Action<List<(string name, string ip, int port)>> onDone)
    {
        EnsureDb();
        var result = new List<(string, string, int)>();

        if (!FirebaseInit.Ready || _db == null)
        {
            onDone?.Invoke(result);
            return;
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        _db.Child("lobbies").GetValueAsync().ContinueWithOnMainThread(t =>
        {
            try
            {
                if (t.IsCompletedSuccessfully && t.Result != null && t.Result.Exists)
                {
                    foreach (var child in t.Result.Children)
                    {
                        string name = child.Child("gameName")?.Value?.ToString() ?? "";
                        string ip   = child.Child("hostIP")?.Value?.ToString() ?? "";
                        int port    = 0; int.TryParse(child.Child("port")?.Value?.ToString(), out port);

                        long expMs = 0; long.TryParse(child.Child("expiresAt")?.Value?.ToString(), out expMs);
                        if (expMs > now && !string.IsNullOrEmpty(ip) && port > 0)
                            result.Add((name, ip, port));
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[FirebaseLobby] LoadLobbyAsync failed: " + ex.Message);
            }

            onDone?.Invoke(result);
        });
    }

    // ---------------- ANALYTICS ----------------

    public void LogDuel(string aId, string bId, string winnerId, bool paidNow)
    {
        if (!FirebaseInit.Ready) return;
        FirebaseAnalytics.LogEvent("duel_result",
            new Parameter("a_id", aId ?? "null"),
            new Parameter("b_id", bId ?? "null"),
            new Parameter("winner", winnerId ?? "null"),
            new Parameter("paid_now", paidNow ? 1 : 0));
    }

    public void LogRiddle(string playerId, int riddleIndex, bool correct)
    {
        if (!FirebaseInit.Ready) return;
        FirebaseAnalytics.LogEvent("riddle_result",
            new Parameter("player_id", playerId ?? "null"),
            new Parameter("riddle_idx", riddleIndex),
            new Parameter("correct", correct ? 1 : 0));
    }

    void OnApplicationQuit()  { HostUnpublishLobby(); }
    void OnDisable()          { /* no-op */ }

    // ---------- Helpers ----------
    static string SanitizeHostIp(string hostIp)
    {
        string ip = (hostIp ?? "").Trim();
        if (!IPAddress.TryParse(ip, out var parsed) ||
            IPAddress.IsLoopback(parsed) ||
            parsed.Equals(IPAddress.Any) ||
            parsed.Equals(IPAddress.IPv6Any) ||
            ip == "0.0.0.0" || ip == "127.0.0.1")
        {
            string lan = FirstLanIPv4();
            if (!string.IsNullOrEmpty(lan)) return lan;
            return ""; // force caller to bail if we can't find a real IP
        }
        return ip;
    }

    static string FirstLanIPv4()
    {
        string fallback = "";
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                var props = ni.GetIPProperties();
                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(ua.Address)) continue;
                    var n = ni.Name.ToLowerInvariant();
                    if (n.Contains("wlan") || n.Contains("wifi") || n.Contains("ap"))
                        return ua.Address.ToString(); // prefer Wi-Fi/AP
                    fallback = ua.Address.ToString();
                }
            }
        }
        catch { }
        return fallback;
    }
}
#endif
