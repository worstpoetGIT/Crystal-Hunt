using System;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using UnityEngine.EventSystems;

public class EncounterUI : MonoBehaviour
{
    public static EncounterUI Instance { get; private set; }

    [Header("Root")]
    public GameObject     encounterPanel;   // full-screen overlay
    public RectTransform  modalBox;         // inner box

    [Header("Widgets")]
    public TMP_Text       titleText;
    public TMP_Text       bodyText;
    public RectTransform  buttonsRow;       // container for option buttons
    public Button         continueBtn;      // hidden until result

    [Header("Prefabs")]
    public Button         optionButtonPrefab; // simple Button (TMP child)

    // state
    string _actorA;       // host id (A = host for duel flow)
    string _actorB;       // opponent id or "Sage"
    Action _afterClose;   // next step (e.g., after duel, show riddle)

    enum Mode { None, Duel, Riddle }
    Mode _mode = Mode.None;
    int  _riddleIndex = -1;

    // --- Turn-based duel state ---
    enum DuelStage { None, HostTurn, WaitingClient, ClientTurn, WaitingResult, ShowResult }
    DuelStage _duelStage = DuelStage.None;
    int _hostChoice = -1;
    int _clientChoice = -1;

    // convenience
    bool IsHost => (NetworkManager.Instance?.isHost ?? false);
    string LocalId => GameManager.Instance?.localPlayerId;

    void Awake()
    {
        if (Instance == null) { Instance = this; DontDestroyOnLoad(gameObject); }
        else { Destroy(gameObject); return; }

        if (continueBtn) continueBtn.gameObject.SetActive(false);
        Hide(); // ensures modalOpen false at boot
    }

    void Freeze(bool on)
    {
        var gm = GameManager.Instance;
        if (gm != null) gm.modalOpen = on;   // stops movement & energy tick
    }

    void ClearButtons()
    {
        if (!buttonsRow) return;
        for (int i = buttonsRow.childCount - 1; i >= 0; i--)
            Destroy(buttonsRow.GetChild(i).gameObject);
    }

    void Show()
    {
        if (encounterPanel) encounterPanel.SetActive(true);
        Freeze(true);
    }

    public void Hide()
    {
        if (encounterPanel) encounterPanel.SetActive(false);
        if (continueBtn)    continueBtn.gameObject.SetActive(false);
        _mode = Mode.None;
        _actorA = _actorB = null;
        _riddleIndex = -1;
        _afterClose = null;

        // reset duel stage
        _duelStage = DuelStage.None;
        _hostChoice = _clientChoice = -1;

        Freeze(false);
    }

    // ---------------- Duel (turn-based) ----------------

    /// Show a Rock/Paper/Scissors duel.
    /// Host chooses first, then client. After both choose, result is shown and panel closes.
    public void ShowDuel(string playerAId, string playerBId, Action afterClose = null)
    {
        _mode = Mode.Duel;
        _actorA = playerAId; // A must be host (NetworkManager ensures this)
        _actorB = playerBId;
        _afterClose = afterClose;

        Show();

        if (titleText) titleText.text = "Duel: Rock · Paper · Scissors";

        _hostChoice = -1;
        _clientChoice = -1;

        ClearButtons();

        if (IsHost)
        {
            _duelStage = DuelStage.HostTurn;
            if (bodyText) bodyText.text = $"You encountered **{playerBId}**.\nChoose your move:";
            BuildChoiceButtons(isMyTurn: true, onClick: OnHostDuelChoice);
        }
        else
        {
            _duelStage = DuelStage.WaitingClient;
            if (bodyText) bodyText.text = $"You encountered **{playerAId}**.\nWaiting for host to choose…";
            BuildChoiceButtons(isMyTurn: false, onClick: null); // disabled until host choice arrives
        }

        if (continueBtn) continueBtn.gameObject.SetActive(false);
    }

    void BuildChoiceButtons(bool isMyTurn, Action<int> onClick)
    {
        if (!optionButtonPrefab || !buttonsRow) return;

        string[] labels = { "Rock", "Paper", "Scissors" };
        Button firstBtn = null;

        for (int i = 0; i < labels.Length; i++)
        {
            var btn = Instantiate(optionButtonPrefab, buttonsRow);
            btn.gameObject.SetActive(true);

            var t = btn.GetComponentInChildren<TMP_Text>();
            if (t) t.text = labels[i];

            btn.onClick.RemoveAllListeners();
            if (isMyTurn && onClick != null)
            {
                int choice = i; // capture
                btn.onClick.AddListener(() => onClick(choice));
                btn.interactable = true;
            }
            else
            {
                btn.interactable = false;
            }

            if (!firstBtn) firstBtn = btn;
        }

        LayoutRebuilder.ForceRebuildLayoutImmediate(buttonsRow);
        if (firstBtn && EventSystem.current != null && isMyTurn)
            EventSystem.current.SetSelectedGameObject(firstBtn.gameObject);
    }

    void LockChoiceButtons()
    {
        if (!buttonsRow) return;
        for (int i = 0; i < buttonsRow.childCount; i++)
        {
            var b = buttonsRow.GetChild(i).GetComponent<Button>();
            if (b) b.interactable = false;
        }
    }

    // Host picks first
    void OnHostDuelChoice(int choice)
    {
        if (!IsHost || _duelStage != DuelStage.HostTurn) return;

        _hostChoice = choice;
        LockChoiceButtons();
        if (bodyText) bodyText.text = "Waiting for other player…";
        _duelStage = DuelStage.WaitingClient;

        // Inform clients to enable their turn
        NetworkManager.Instance?.HostSendDuelHostChoice(_actorA, _actorB, _hostChoice);
    }

    // Client picks after host
    void OnClientDuelChoice(int choice)
    {
        if (IsHost || _duelStage != DuelStage.ClientTurn) return;

        _clientChoice = choice;
        LockChoiceButtons();
        if (bodyText) bodyText.text = "Waiting for result…";
        _duelStage = DuelStage.WaitingResult;

        // Send my choice to host
        NetworkManager.Instance?.ClientSendDuelClientChoice(_actorA, _actorB, _clientChoice);
    }

    /// Called by NetworkManager on CLIENT when host choice arrives.
    public void EnableClientTurn(string aId, string bId, int aChoice)
    {
        if (_mode != Mode.Duel) return;
        if (IsHost) return; // host never receives this
        if (aId != _actorA || bId != _actorB) return;

        _hostChoice = aChoice;
        _duelStage = DuelStage.ClientTurn;

        if (bodyText) bodyText.text = "Your turn. Choose your move:";
        ClearButtons();
        BuildChoiceButtons(isMyTurn: true, onClick: OnClientDuelChoice);
    }

    /// Called by NetworkManager on BOTH devices when the result is known.
    public void ShowDuelResult(int aChoice, int bChoice, string winner, bool paidNow)
    {
        if (_mode != Mode.Duel) return;

        _duelStage = DuelStage.ShowResult;
        _hostChoice = aChoice;
        _clientChoice = bChoice;

        string[] L = { "Rock", "Paper", "Scissors" };
        string aId = _actorA, bId = _actorB;

        string msg = $"Duel: {aId}({L[aChoice]}) vs {bId}({L[bChoice]}) → {winner} wins. " +
                     (paidNow ? $"{(winner == aId ? bId : aId)} paid 1 crystal." : $"{(winner == aId ? bId : aId)} owes 1 crystal (IOU).");

        if (bodyText) bodyText.text = msg;
        ClearButtons();

        // Auto close (single-round per entry). Also expose Continue for manual close.
        if (continueBtn)
        {
            continueBtn.gameObject.SetActive(true);
            continueBtn.onClick.RemoveAllListeners();
            continueBtn.onClick.AddListener(() => { Hide(); _afterClose?.Invoke(); });
        }

        CancelInvoke(nameof(AutoCloseDuel));
        Invoke(nameof(AutoCloseDuel), 1.25f);
    }

    void AutoCloseDuel()
    {
        if (_mode == Mode.Duel)
        {
            Hide();
            _afterClose?.Invoke();
        }
    }

    // ---------------- Riddle ----------------

    public void ShowRiddle(string playerId, int riddleIndex, string npcName, Action afterClose = null)
    {
        _mode = Mode.Riddle;
        _actorA = playerId;
        _actorB = npcName;        // label only
        _afterClose = afterClose;
        _riddleIndex = riddleIndex;

        var em = EncounterManager.Instance;
        if (em == null || em.riddles == null || _riddleIndex < 0 || _riddleIndex >= em.riddles.Count)
        {
            Debug.LogWarning("[EncounterUI] Riddle index out of range.");
            return;
        }
        var r = em.riddles[_riddleIndex];

        Show();
        if (titleText) titleText.text = "Sage's Riddle";
        if (bodyText)  bodyText.text  = r.q;

        ClearButtons();

        if (optionButtonPrefab && buttonsRow)
        {
            Button firstBtn = null;

            for (int i = 0; i < r.options.Length; i++)
            {
                var btn = Instantiate(optionButtonPrefab, buttonsRow);
                btn.GetComponentInChildren<TMP_Text>().text = r.options[i];
                int idx = i;
                btn.onClick.RemoveAllListeners();
                btn.onClick.AddListener(() => OnRiddleChoice(idx));

                if (!firstBtn) firstBtn = btn;
            }

            LayoutRebuilder.ForceRebuildLayoutImmediate(buttonsRow);
            if (firstBtn && EventSystem.current != null)
                EventSystem.current.SetSelectedGameObject(firstBtn.gameObject);
        }

        if (continueBtn) continueBtn.gameObject.SetActive(false);
    }

    void OnRiddleChoice(int optionIndex)
    {
        var em = EncounterManager.Instance;
        bool correct = em.HandleRiddleAnswerFromUI(_actorA, _riddleIndex, optionIndex);

        if (bodyText) bodyText.text = correct
            ? "Correct! The Sage gives you 1 crystal."
            : "Wrong answer. No reward.";

        ClearButtons();
        if (continueBtn)
        {
            continueBtn.gameObject.SetActive(true);
            continueBtn.onClick.RemoveAllListeners();
            continueBtn.onClick.AddListener(() =>
            {
                Hide();
                _afterClose?.Invoke();
            });
        }
    }

    // ---------- Reusable modal for misc ----------
    public struct Choice
    {
        public string label;
        public Action onClick;
        public Choice(string label, Action onClick) { this.label = label; this.onClick = onClick; }
    }

    public void ShowChoices(string title, string body, params Choice[] options)
    {
        Show(); // opens panel + freezes input

        if (titleText) titleText.text = title ?? "";
        if (bodyText)  bodyText.text  = body  ?? "";

        ClearButtons();
        if (continueBtn) continueBtn.gameObject.SetActive(false);
        if (buttonsRow)  buttonsRow.gameObject.SetActive(true);

        if (options == null || options.Length == 0)
            options = new[] { new Choice("OK", null) };

        Button firstBtn = null;

        foreach (var opt in options)
        {
            if (!optionButtonPrefab || !buttonsRow) break;

            var btn = Instantiate(optionButtonPrefab, buttonsRow);
            btn.name = "Opt_" + (string.IsNullOrEmpty(opt.label) ? "OK" : opt.label);
            btn.gameObject.SetActive(true);

            var rt = btn.GetComponent<RectTransform>();
            if (rt)
            {
                rt.localScale = Vector3.one;
                rt.anchoredPosition3D = Vector3.zero;
                if (rt.sizeDelta.x < 160f || rt.sizeDelta.y < 40f)
                    rt.sizeDelta = new Vector2(180f, 44f);
            }

            var t = btn.GetComponentInChildren<TMP_Text>();
            if (t)
            {
                t.text = string.IsNullOrEmpty(opt.label) ? "OK" : opt.label;
                t.enableWordWrapping = false;
                t.overflowMode = TextOverflowModes.Ellipsis;
                t.alignment = TextAlignmentOptions.Center;
            }

            btn.onClick.RemoveAllListeners();
            btn.onClick.AddListener(() =>
            {
                Hide(); // closes + unfreezes input
                try { opt.onClick?.Invoke(); }
                catch (Exception ex) { Debug.LogWarning("[EncounterUI] ShowChoices action exception: " + ex.Message); }
            });

            if (!firstBtn) firstBtn = btn;
        }

        if (buttonsRow) LayoutRebuilder.ForceRebuildLayoutImmediate(buttonsRow);

        if (firstBtn && EventSystem.current != null)
            EventSystem.current.SetSelectedGameObject(firstBtn.gameObject);
    }
}
