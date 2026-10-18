using UnityEngine;

/// Local input -> host authoritative movement.
/// - HOST: calls GameManager.HostSetPlayerPosition (authoritative).
/// - CLIENT: sends UDP POS to host; (optional) applies light prediction locally,
///   but relies on server snapshots for truth.
public class PlayerController : MonoBehaviour
{
    [Header("Identity")]
    public string playerId = "LocalPlayer";

    [Header("Movement")]
    public float moveSpeed = 2.0f;

    // cache
    GameManager _gm;
    NetworkManager _net;

    void Start()
    {
        _gm  = GameManager.Instance;
        _net = NetworkManager.Instance;

        // If StartMenuUI already set GM/local ID, prefer that.
        if (!string.IsNullOrWhiteSpace(_gm?.localPlayerId))
            playerId = _gm.localPlayerId;
        else
            _gm.localPlayerId = playerId;

        // Ensure we exist in GM state
        _gm.EnsurePlayer(playerId);
    }

    void Update()
    {
        if (_gm == null || !_gm.IsInputAllowed) return;

        // read input
        Vector2 move = Vector2.zero;
#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
        if (TouchDpad.Current != null) move = TouchDpad.Current.Read();
#else
        Vector2 kb = new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
        move = (kb.sqrMagnitude > 0f) ? kb.normalized :
               (TouchDpad.Current != null ? TouchDpad.Current.Read() : Vector2.zero);
#endif
        if (move.sqrMagnitude <= 0f) return;

        var ps = _gm.EnsurePlayer(playerId);
        Vector2 next = ps.pos + move * moveSpeed * Time.deltaTime;

        // ----- ROLE SPLIT -----
        bool isHost = (_net != null && _net.isHost);
        if (isHost)
        {
            // Host is authoritative: run full server logic (enter/exit, duels, NPC, etc.)
            _gm.HostSetPlayerPosition(playerId, next);
        }
        else
        {
            // Client: send input to host; do NOT mutate authoritative state locally.
            int? li = ps.insideLocationIndex; // send what we currently think we're in
            _net?.ClientReportPosition(playerId, next, li);

            // Optional tiny client-side prediction (comment out if you want zero prediction):
            // ps.pos = next;  // purely visual; will be corrected by snapshots
        }
    }

    // Utility used by StartMenuUI or tests
    public void SnapTo(Vector2 p, bool broadcast = false)
    {
        var ps = GameManager.Instance.EnsurePlayer(playerId);
        ps.pos = p;
        ps.insideLocationIndex = null;
        ps.lastLocationIndex   = null;
        ps.lastEnterPos        = null;

        // Only host is allowed to broadcast authoritative state
        if (broadcast && NetworkManager.Instance != null && NetworkManager.Instance.isHost)
            NetworkManager.Instance.BroadcastSnapshot();
    }

    public void SnapToThrone(bool broadcast = false)
    {
        SnapTo(GameManager.Instance.throneRoom, broadcast);
    }
}
