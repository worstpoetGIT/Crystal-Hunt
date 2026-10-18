using System;
using System.Collections.Generic;
using UnityEngine;

/// Host-authoritative encounter logic:
/// - Duels: triggered by NetworkManager (host broadcasts DUEL:aId:bId)
/// - Sage riddle (R41): on entry if no opponent; or after duel if opponent present
/// - Debts/IOUs: auto-pay when debtor gains crystals (R43)
[DefaultExecutionOrder(-10)]
public class EncounterManager : MonoBehaviour
{
    public static EncounterManager Instance { get; private set; }

    [Serializable] public class Debt { public string debtor; public string creditor; public int amount = 1; }
    private readonly List<Debt> _debts = new();

    // Minimal riddle bank
    [Serializable] public class Riddle { public string q; public string[] options; public int correct; }

    [Header("Riddles (tiny bank)")]
    public List<Riddle> riddles = new()
    {
        new Riddle{ q="What has to be broken before you can use it?",
            options=new[]{"An egg","A code","A promise"}, correct=0 },
        new Riddle{ q="I’m tall when I’m young, and I’m short when I’m old. What am I?",
            options=new[]{"Candle","Tree","Shadow"}, correct=0 },
        new Riddle{ q="What month has 28 days?", options=new[]{"February","All of them","None"}, correct=1 }
    };

    // When a player enters a location that has a Sage, we remember to show a riddle
    // after any duel completes. Key = playerId, Value = locationIndex at entry.
    private readonly Dictionary<string,int> _pendingRiddleFor = new();

    // Optional host-only keyboard test path (kept harmless)
    private string _awaitingRiddleForPlayerId = null;
    private int _awaitingRiddleIndex = -1;

    void Awake()
    {
        if (Instance == null) { Instance = this; DontDestroyOnLoad(gameObject); }
        else Destroy(gameObject);
    }

    // ------------------ Public entry points ------------------

    /// Call this (HOST) right after a player ENTERS a location index.
    /// With net-side duel arbiter ON, we don't try to start the duel here.
    /// We only track pending riddles and (if alone) show the riddle immediately.
    public void OnPlayerEnteredLocation(string playerId, int locationIndex)
    {
        var gm = GameManager.Instance;
        if (!gm.players.TryGetValue(playerId, out var ps) || ps.leftGame) return;

        // Mark riddle intent if a Sage is here.
        bool npcHere = gm.npcs.Exists(n => n.locationIndex == locationIndex);
        if (npcHere) _pendingRiddleFor[playerId] = locationIndex;

        // If there's NOT another player here, show the riddle now (local device only).
        // If there's NOT another player here, show the riddle now.
if (!HasAtLeastTwoPlayersIn(locationIndex, exceptPlayerId: null) && npcHere)
{
    ShowRiddleModal(playerId, locationIndex);
    _pendingRiddleFor.Remove(playerId);
}


        // NOTE: Duel pairing is handled by NetworkManager's net-side arbiter.
        // If you switch that off, the GameManager fallback can run its own scan.
    }

    /// Call this whenever a player GAINS crystals (e.g., pickup or riddle reward).
    public void TryAutoSettleDebts(string playerId)
    {
        var gm = GameManager.Instance;
        if (!gm.players.TryGetValue(playerId, out var ps)) return;

        // Pay debts oldest-first
        for (int i = 0; i < _debts.Count && ps.carrying.Count > 0;)
        {
            var d = _debts[i];
            if (d.debtor == playerId && d.amount > 0)
            {
                // transfer one crystal
                string id = ps.carrying[ps.carrying.Count - 1];
                ps.carrying.RemoveAt(ps.carrying.Count - 1);

                if (gm.players.TryGetValue(d.creditor, out var creditor))
                    creditor.carrying.Add(id);

                d.amount--;
                if (d.amount <= 0) _debts.RemoveAt(i);
                else i++;

                UIManagerSet($"IOU settled: {playerId} paid 1 crystal to {d.creditor}.");
            }
            else i++;
        }

        NetworkManager.Instance?.BroadcastSnapshot();
    }

    // ------------------ Presence helpers ------------------

    bool HasAtLeastTwoPlayersIn(int li, string exceptPlayerId)
    {
        var gm = GameManager.Instance;
        int count = 0;
        foreach (var kv in gm.players)
        {
            var p = kv.Value;
            if (p.leftGame) continue;
            if (p.insideLocationIndex != li) continue;
            if (!string.IsNullOrEmpty(exceptPlayerId) && p.playerId == exceptPlayerId) continue;
            count++;
            if (count >= 2) return true;
        }
        return false;
    }

    int? GetCommonLocation(string aId, string bId)
    {
        var gm = GameManager.Instance;
        if (!gm.players.TryGetValue(aId, out var A) || !gm.players.TryGetValue(bId, out var B))
            return null;
        if (!A.insideLocationIndex.HasValue || !B.insideLocationIndex.HasValue)
            return null;
        return (A.insideLocationIndex.Value == B.insideLocationIndex.Value)
            ? A.insideLocationIndex.Value
            : (int?)null;
    }

    // ------------------ Riddle helpers ------------------

    void ShowRiddleModal(string playerId, int li)
{
    int idx = UnityEngine.Random.Range(0, riddles.Count);

    var net = NetworkManager.Instance;
    var gm  = GameManager.Instance;

    // If we have a network host, let it deliver the riddle to the correct device
    if (net != null && net.isHost)
    {
        // host local vs remote handled inside HostSendRiddleTo
        net.HostSendRiddleTo(playerId, li, idx);
        return;
    }

    // Offline / single-device fallback (kept for editor tests)
    EncounterUI.Instance?.ShowRiddle(playerId, idx, "Sage", afterClose: null);
}


    // ------------------ Keyboard-only riddle test path (optional) ------------------

    void TriggerRiddle_TestOnly(string playerId)
    {
        if (riddles.Count == 0) return;
        _awaitingRiddleIndex = UnityEngine.Random.Range(0, riddles.Count);
        _awaitingRiddleForPlayerId = playerId;

        var r = riddles[_awaitingRiddleIndex];
        string opts = "";
        for (int i = 0; i < r.options.Length; i++)
            opts += $"\n {i + 1}. {r.options[i]}";
        UIManagerSet($"Sage asks: {r.q}{opts}  (press 1..{r.options.Length})");
    }

    void Update()
    {
        // Host-only key input to answer riddles during testing
        if (string.IsNullOrEmpty(_awaitingRiddleForPlayerId)) return;

        var r = riddles[_awaitingRiddleIndex];
        for (int i = 0; i < r.options.Length; i++)
        {
            if (Input.GetKeyDown(KeyCode.Alpha1 + i))
            {
                bool correct = (i == r.correct);
                HandleRiddleAnswer(_awaitingRiddleForPlayerId, correct);
                _awaitingRiddleForPlayerId = null;
                _awaitingRiddleIndex = -1;
                break;
            }
        }
    }

    // ------------------ Riddle resolution ------------------

    void HandleRiddleAnswer(string playerId, bool correct)
    {
        var gm = GameManager.Instance;
        if (!gm.players.TryGetValue(playerId, out var ps)) return;

        if (correct)
        {
            // Reward: +1 crystal (create a new id)
            string cid = $"Riddle_{Guid.NewGuid().ToString("N").Substring(0, 6)}";
            ps.carrying.Add(cid);

            // Optional: remove one crystal from this location to reflect an exchange
            if (ps.insideLocationIndex.HasValue)
            {
                int li = ps.insideLocationIndex.Value;
                int idx = gm.crystals.FindIndex(cc => cc.locationIndex == li);
                if (idx >= 0) gm.crystals.RemoveAt(idx);
            }

            UIManagerSet("Correct! The Sage gives you 1 crystal.");
            TryAutoSettleDebts(playerId); // might pay off an IOU
        }
        else
        {
            UIManagerSet("Wrong answer. No reward.");
        }

#if USE_FIREBASE
        try {
            Firebase.Analytics.FirebaseAnalytics.LogEvent(
                "riddle_answer",
                new Firebase.Analytics.Parameter("player", playerId),
                new Firebase.Analytics.Parameter("correct", correct ? 1 : 0)
            );
        } catch {}
#endif

        NetworkManager.Instance?.BroadcastSnapshot();
    }

    // Called from EncounterUI for button-based riddles
    public bool HandleRiddleAnswerFromUI(string playerId, int riddleIdx, int optionIndex)
    {
        if (riddleIdx < 0 || riddleIdx >= riddles.Count)
        {
            Debug.LogWarning($"Riddle idx out of range: {riddleIdx}");
            return false;
        }
        var r = riddles[riddleIdx];
        if (r.options == null || optionIndex < 0 || optionIndex >= r.options.Length)
        {
            Debug.LogWarning($"Option idx out of range: {optionIndex}");
            return false;
        }

        bool correct = (optionIndex == r.correct);

#if USE_FIREBASE
        FirebaseLobby.Instance?.LogRiddle(playerId, riddleIdx, correct);
#endif

        HandleRiddleAnswer(playerId, correct);
        return correct;
    }

    // ------------------ Duel resolution (called by EncounterUI) ------------------

    /// Resolve Rock/Paper/Scissors from UI choices.
    /// Handles:
    /// - crystal transfer or IOU
    /// - analytics logs (optional)
    /// - broadcast snapshot
    /// - riddle chaining for whichever entrant had a pending riddle here
    public (string winner, string loser, bool paidNow, string text)
ResolveDuelWithChoices(string aId, string bId, int aChoice, int bChoice)
{
    var gm = GameManager.Instance;

    string[] L = { "Rock", "Paper", "Scissors" };
    bool Beats(int x, int y) => (x == 0 && y == 2) || (x == 1 && y == 0) || (x == 2 && y == 1);

    if (aChoice == bChoice) bChoice = UnityEngine.Random.Range(0, 3);

    string winner, loser;
    if (Beats(aChoice, bChoice)) { winner = aId; loser = bId; }
    else if (Beats(bChoice, aChoice)) { winner = bId; loser = aId; }
    else { winner = aId; loser = bId; }

    bool paidNow = TransferOrCreateDebt(loser, winner);

    //  NEW: this duel "consumed" both entrants' duel for this entry
    if (gm.players.TryGetValue(aId, out var A)) A.dueledSinceEntry = true;
    if (gm.players.TryGetValue(bId, out var B)) B.dueledSinceEntry = true;

    string msg = $"Duel: {aId}({L[aChoice]}) vs {bId}({L[bChoice]}) → {winner} wins. " +
                 (paidNow ? $"{loser} paid 1 crystal." : $"{loser} owes 1 crystal (IOU).)");
    UIManagerSet(msg);

    //  UPDATED: Riddle dispatch to EVERY player in this location who has pending riddle
    int li = GetCommonLocation(aId, bId) ?? -1;
    if (NetworkManager.Instance != null && NetworkManager.Instance.isHost && li >= 0)
    {
        var targets = new List<string>();

        // Gather everyone here with a pending riddle
        foreach (var kv in gm.players)
        {
            var ps = kv.Value;
            if (ps.leftGame) continue;
            if (ps.insideLocationIndex != li) continue;
            if (_pendingRiddleFor.TryGetValue(ps.playerId, out int gli) && gli == li)
                targets.Add(ps.playerId);
        }

        // Send/Show riddle independently for each target
        foreach (var id in targets)
        {
            _pendingRiddleFor.Remove(id);
            int ridx = UnityEngine.Random.Range(0, riddles.Count);

            if (gm.localPlayerId == id)
                EncounterUI.Instance?.ShowRiddle(id, ridx, "Sage");
            else
                NetworkManager.Instance.HostSendRiddleTo(id, li, ridx);
        }
    }

    NetworkManager.Instance?.BroadcastSnapshot();
    return (winner, loser, paidNow, msg);
}

    // ------------------ Core duel helpers ------------------

    bool TransferOrCreateDebt(string fromLoser, string toWinner)
    {
        var gm = GameManager.Instance;
        if (!gm.players.TryGetValue(fromLoser, out var L)) return false;

        if (L.carrying.Count > 0)
        {
            // immediate payment
            string id = L.carrying[L.carrying.Count - 1];
            L.carrying.RemoveAt(L.carrying.Count - 1);

            if (gm.players.TryGetValue(toWinner, out var W))
                W.carrying.Add(id);

            return true;
        }
        // create IOU (promise of first crystal)
        _debts.Add(new Debt { debtor = fromLoser, creditor = toWinner, amount = 1 });
        return false;
    }

    // ------------------ UI helper ------------------

    void UIManagerSet(string text)
    {
        var ui = FindObjectOfType<UIManager>(true);
        if (ui) ui.SetRivalInfo(text);
        Debug.Log("[Encounter] " + text);
    }
}
