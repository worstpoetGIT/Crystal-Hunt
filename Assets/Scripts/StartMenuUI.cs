using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;   // for local IPv4s

public class StartMenuUI : MonoBehaviour
{
    [Header("Inputs")]
    public TMP_InputField GameNameInput;
    public TMP_InputField PortInput;         // 3000–5000
    public TMP_InputField TimeMinutesInput;  // 1–180
    public TMP_InputField HostIPInput;       // for Join
    public TMP_InputField PlayerIdInput;     // optional Player ID

    [Header("Buttons")]
    public Button BeginNewBtn;
    public Button JoinExistingBtn;

#if USE_FIREBASE
    public Button RefreshLobbyBtn;
#endif

    [Header("Feedback")]
    public TextMeshProUGUI StartErrorLabel;

    [Header("Track previous object (optional)")]
    public Transform TrackTarget;
    public bool TrackOnAwake = true;
    public bool PersistAcrossRuns = true;

    RectTransform _trackRect;
    Vector3 _origLocalPos, _origLocalScale;
    Vector2 _origAnchoredPos, _origSizeDelta;
    bool _isUI;

    const string PP_KEY = "Track_";
    const string PP_PLAYERID = "StartMenu_LastPlayerId";

    void Awake()
    {
        if (StartErrorLabel) StartErrorLabel.text = "";

        BeginNewBtn.onClick.RemoveAllListeners();
        BeginNewBtn.onClick.AddListener(OnBeginNew);

        JoinExistingBtn.onClick.RemoveAllListeners();
        JoinExistingBtn.onClick.AddListener(OnJoinExisting);

#if USE_FIREBASE
        if (RefreshLobbyBtn != null)
        {
            RefreshLobbyBtn.onClick.RemoveAllListeners();
            RefreshLobbyBtn.onClick.AddListener(OnRefreshLobby);
        }
#endif

        // Pre-fill last Player ID if available
        if (PlayerIdInput && PlayerPrefs.HasKey(PP_PLAYERID))
            PlayerIdInput.text = PlayerPrefs.GetString(PP_PLAYERID);

        // Helpful: show my current IPv4s on HUD right away
        NetDebugHUD.ShowEssential(
            "NET HUD",
            $"My IPs: {string.Join(", ", LocalIPs())}",
            "Set the same port on all devices (default 4000)."
        );

        // Tracking init
        if (TrackTarget != null)
        {
            _trackRect = TrackTarget.GetComponent<RectTransform>();
            _isUI = (_trackRect != null);

            if (_isUI) { _origAnchoredPos = _trackRect.anchoredPosition; _origSizeDelta = _trackRect.sizeDelta; }
            else       { _origLocalPos = TrackTarget.localPosition; _origLocalScale = TrackTarget.localScale; }

            if (TrackOnAwake)
            {
                LogTarget("Startup");
                if (PersistAcrossRuns) LoadFromPrefs();
            }
        }
    }

    // ------------------- Begin / Join -------------------

    void OnBeginNew()
{
    var name    = string.IsNullOrWhiteSpace(GameNameInput.text) ? "CrystalGame" : GameNameInput.text.Trim();
    int port    = ParseClamped(PortInput.text, 4000, 3000, 5000);
    int minutes = ParseClamped(TimeMinutesInput.text, 10, 1, 180);

    // --- NEW: host player ID from input (or random fallback) ---
    string hostId = !string.IsNullOrWhiteSpace(PlayerIdInput?.text)
        ? PlayerIdInput.text.Trim()
        : ("Host-" + UnityEngine.Random.Range(1000, 9999));

    // Persist the typed host ID (so it auto-fills next time)
    if (PlayerIdInput)
    {
        PlayerPrefs.SetString(PP_PLAYERID, hostId);
        PlayerPrefs.Save();
    }

    // Apply the ID to GM/PC BEFORE starting the game so the host's local state uses it
    var gm = FindObjectOfType<GameManager>(true);
    if (gm) gm.localPlayerId = hostId;

    // Create game state (world, players, timers, etc.)
    if (gm != null) gm.BeginNewGame(name, port, minutes);
    else Debug.LogWarning("[StartMenuUI] GameManager not found; BeginNewGame skipped.");

    // Start UDP host
    var net = FindObjectOfType<NetworkManager>(true);
    bool ok = (NetworkManager.Instance != null)
                ? NetworkManager.Instance.StartHost(port)
                : (net != null && net.StartHost(port));
    if (!ok) { if (StartErrorLabel) StartErrorLabel.text = "Failed to start host (port in use/invalid)."; return; }

    // Apply ID to local PlayerController (authoritative local input path)
    var pc = FindObjectOfType<PlayerController>(true);
    if (pc)
    {
        pc.playerId = hostId;
        pc.enabled  = true;
        pc.SnapToThrone(false);
    }

    // Pre-fill Host IP for convenience
    string myFirstIp = LocalIPs().FirstOrDefault() ?? "";
    if (HostIPInput) HostIPInput.text = myFirstIp;

#if USE_FIREBASE
    // Publish to Firebase (FirebaseLobby already guards against loopback)
    string hostIpForLobby = string.IsNullOrWhiteSpace(myFirstIp) ? "0.0.0.0" : myFirstIp;
    FirebaseLobby.Instance?.HostPublishLobby(name, hostIpForLobby, port, ttlSeconds: 900);
#endif

    SaveToPrefs();

    // HUD essentials (NetworkManager will also keep it fresh)
    NetDebugHUD.ShowEssential(
        "Mode: HOST",
        $"Game: {name}",
        $"Port: {port}",
        $"Player: {hostId}",
        $"My IPs: {string.Join(", ", LocalIPs())}",
        "Clients must enter the IP shown above."
    );

    gameObject.SetActive(false);
}

void OnJoinExisting()
{
    if (string.IsNullOrWhiteSpace(HostIPInput?.text) || !IPAddress.TryParse(HostIPInput.text.Trim(), out _))
    { if (StartErrorLabel) StartErrorLabel.text = "Enter a valid Host IP (e.g., 192.168.43.1)."; return; }

    if (string.IsNullOrWhiteSpace(PortInput?.text))
    { if (StartErrorLabel) StartErrorLabel.text = "Enter a port (3000–5000)."; return; }
    if (!int.TryParse(PortInput.text, out var port) || port < 3000 || port > 5000)
    { if (StartErrorLabel) StartErrorLabel.text = "Port must be 3000–5000."; return; }

    string ip = HostIPInput.text.Trim();
    if (StartErrorLabel) StartErrorLabel.text = "";

    var net = FindObjectOfType<NetworkManager>(true);
    if (net == null)
    { if (StartErrorLabel) StartErrorLabel.text = "NetworkManager not found."; return; }

    bool ok = (NetworkManager.Instance != null)
                ? NetworkManager.Instance.StartClient(ip, port)
                : net.StartClient(ip, port);
    if (!ok)
    { if (StartErrorLabel) StartErrorLabel.text = "Failed to init client (bad IP/port?)."; return; }

    // Player identity (optional)
    string pid = !string.IsNullOrWhiteSpace(PlayerIdInput?.text)
        ? PlayerIdInput.text.Trim()
        : ("Player-" + UnityEngine.Random.Range(1000, 9999));

    if (PlayerIdInput)
    {
        PlayerPrefs.SetString(PP_PLAYERID, pid);
        PlayerPrefs.Save();
    }

    // Tell the network layer (client sends HELLO → host may suffix to avoid collisions)
    if (NetworkManager.Instance != null) NetworkManager.Instance.SendHello(pid);
    else net.SendHello(pid);

    // Apply locally so input & UI use the same ID
    var pc = FindObjectOfType<PlayerController>(true);
    if (pc) pc.playerId = pid;
    var gm2 = FindObjectOfType<GameManager>(true);
    if (gm2) gm2.localPlayerId = pid;

    SaveToPrefs();

    // Prime world map so it shows instantly
    var maps = FindObjectOfType<MapboxStaticImagesBinder>(true);
    if (maps) maps.InitAndFetchWorld();

    // HUD essentials (client-side)
    string myIPs = string.Join(", ", LocalIPs());
    bool sameSubnet = LikelySameSubnet(LocalIPs().FirstOrDefault(), ip);
    NetDebugHUD.ShowEssential(
        "Mode: CLIENT",
        $"My IPs: {myIPs}",
        $"Join → {ip}:{port}",
        $"Same subnet? {(sameSubnet ? " YES" : " NO")}",
        $"Player: {pid}"
    );

    gameObject.SetActive(false);
}

#if USE_FIREBASE
    // ---------- Firebase: refresh + compact HUD list ----------
    void OnRefreshLobby()
    {
        if (FirebaseLobby.Instance == null) return;

        FirebaseLobby.Instance.LoadLobbyAsync(list =>
        {
            if (list.Count > 0)
            {
                // Fill the first one into inputs (you can tap Join immediately)
                if (HostIPInput)   HostIPInput.text   = list[0].ip;
                if (PortInput)     PortInput.text     = list[0].port.ToString();
                if (GameNameInput) GameNameInput.text = list[0].name;

                var lines = list.Select(e => $"{e.name}  {e.ip}:{e.port}");
                NetDebugHUD.ShowList("Available games:", lines);
            }
            else
            {
                NetDebugHUD.ShowEssential("Available games:", "(none)");
            }
        });
    }
#endif

    // ------------------- Helpers -------------------

    int ParseClamped(string s, int def, int min, int max)
    {
        if (!int.TryParse(s, out var v)) v = def;
        return Mathf.Clamp(v, min, max);
    }

    public void OpenStartPanel()
    {
        gameObject.SetActive(true);
        transform.SetAsLastSibling();

        var cg = GetComponent<CanvasGroup>() ?? gameObject.AddComponent<CanvasGroup>();
        cg.alpha = 1f; cg.interactable = true; cg.blocksRaycasts = true;

        if (StartErrorLabel) StartErrorLabel.text = "";

        var pc = FindObjectOfType<PlayerController>(true);
        if (pc) pc.enabled = false;

        // Refresh HUD when panel is shown again
        NetDebugHUD.ShowEssential(
            "NET HUD",
            $"My IPs: {string.Join(", ", LocalIPs())}",
            "Host: press Begin New. Client: enter host IP + Join."
        );
    }

    void LogTarget(string tag)
    {
        if (TrackTarget == null) return;
        if (_isUI) Debug.Log($"[Track:{tag}] UI '{TrackTarget.name}' pos={_trackRect.anchoredPosition}, size={_trackRect.sizeDelta}");
        else       Debug.Log($"[Track:{tag}] GO '{TrackTarget.name}' pos={TrackTarget.localPosition}, scale={TrackTarget.localScale}");
    }

    public void SaveToPrefs()
    {
        if (!PersistAcrossRuns || TrackTarget == null) return;
        string key = PP_KEY + TrackTarget.name;

        if (_isUI)
        {
            PlayerPrefs.SetFloat(key + "_ax", _trackRect.anchoredPosition.x);
            PlayerPrefs.SetFloat(key + "_ay", _trackRect.anchoredPosition.y);
            PlayerPrefs.SetFloat(key + "_sx", _trackRect.sizeDelta.x);
            PlayerPrefs.SetFloat(key + "_sy", _trackRect.sizeDelta.y);
        }
        else
        {
            var p = TrackTarget.localPosition;
            var s = TrackTarget.localScale;
            PlayerPrefs.SetFloat(key + "_px", p.x);
            PlayerPrefs.SetFloat(key + "_py", p.y);
            PlayerPrefs.SetFloat(key + "_pz", p.z);
            PlayerPrefs.SetFloat(key + "_sx", s.x);
            PlayerPrefs.SetFloat(key + "_sy", s.y);
            PlayerPrefs.SetFloat(key + "_sz", s.z);
        }
        PlayerPrefs.Save();
        LogTarget("Saved");
    }

    public void LoadFromPrefs()
    {
        if (!PersistAcrossRuns || TrackTarget == null) return;
        string key = PP_KEY + TrackTarget.name;

        if (_isUI)
        {
            if (PlayerPrefs.HasKey(key + "_ax"))
            {
                var ax = PlayerPrefs.GetFloat(key + "_ax");
                var ay = PlayerPrefs.GetFloat(key + "_ay");
                var sx = PlayerPrefs.GetFloat(key + "_sx");
                var sy = PlayerPrefs.GetFloat(key + "_sy");
                _trackRect.anchoredPosition = new Vector2(ax, ay);
                _trackRect.sizeDelta       = new Vector2(sx, sy);
                LogTarget("Loaded(UI)");
            }
        }
        else
        {
            if (PlayerPrefs.HasKey(key + "_px"))
            {
                var px = PlayerPrefs.GetFloat(key + "_px");
                var py = PlayerPrefs.GetFloat(key + "_py");
                var pz = PlayerPrefs.GetFloat(key + "_pz");
                var sx = PlayerPrefs.GetFloat(key + "_sx");
                var sy = PlayerPrefs.GetFloat(key + "_sy");
                var sz = PlayerPrefs.GetFloat(key + "_sz");
                TrackTarget.localPosition = new Vector3(px, py, pz);
                TrackTarget.localScale    = new Vector3(sx, sy, sz);
                LogTarget("Loaded(GO)");
            }
        }
    }

    public void RestoreOriginal()
    {
        if (TrackTarget == null) return;
        if (_isUI) { _trackRect.anchoredPosition = _origAnchoredPos; _trackRect.sizeDelta = _origSizeDelta; }
        else       { TrackTarget.localPosition = _origLocalPos;      TrackTarget.localScale = _origLocalScale; }
        LogTarget("RestoredOriginal");
    }

#if USE_FIREBASE
    void OnApplicationQuit()
    {
        FirebaseLobby.Instance?.HostUnpublishLobby();
    }
#endif

    // -------- Local IPv4 + subnet helpers (no extra scripts required) --------
    static string[] LocalIPs()
    {
        var list = new System.Collections.Generic.List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
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
}
