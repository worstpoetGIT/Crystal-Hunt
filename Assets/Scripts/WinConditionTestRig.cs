#if UNITY_EDITOR
using System.Collections;
using UnityEngine;

/// Editor-only plug-in test to verify GameManager's win condition logic.
/// Safe: defers a frame before doing any work; never included in device builds.
[DefaultExecutionOrder(1000)]
public class WinConditionTestRig : MonoBehaviour
{
    [Header("Enable / Trigger")]
    public bool enableRig = true;
    public bool autoRunOnPlay = true;
    public KeyCode hotkey = KeyCode.F7;

    public enum Scenario { CrystalWin, EnergyTieBreak, SuddenDeath, NobodyAtThrone }
    [Header("Scenario")]
    public Scenario scenario = Scenario.CrystalWin;

    [Header("Staging (only used where relevant)")]
    public string otherPlayerId = "TestClient-1";
    public int hostCrystals   = 3;
    public int otherCrystals  = 1;
    public int hostEnergy     = 8;
    public int otherEnergy    = 5;

    [Header("Timing")]
    public float timeToFinishSeconds = 2f;

    bool _running;

    void Update()
    {
        if (!enableRig || _running) return;
        if (Input.GetKeyDown(hotkey)) StartCoroutine(CoRun());
    }

    // NEW: defer one frame so we never slow EnterPlayMode
    IEnumerator Start()
    {
        if (!enableRig || !autoRunOnPlay) yield break;
        yield return null;                       // let Editor finish entering play first
        if (Application.isPlaying) yield return CoRun();
    }

    IEnumerator CoRun()
    {
        if (_running) yield break;               // NEW: re-entrancy guard
        _running = true;

        // Ensure GM exists
        var gm = GameManager.Instance ?? FindObjectOfType<GameManager>(true);
        if (gm == null)
        {
            var go = new GameObject("GameManager");
            gm = go.AddComponent<GameManager>();
            gm.SetupSampleBoundary();
        }

        // Hide Start panel so we can run clean
        var startUI = FindObjectOfType<StartMenuUI>(true);
        if (startUI) startUI.gameObject.SetActive(false);

        // Fresh game (we’ll end it early)
        gm.BeginNewGame("TEST_WIN", 4000, 10);

        // Stage players
        string hostId = gm.localPlayerId;
        var host  = gm.EnsurePlayer(hostId);
        var other = gm.EnsurePlayer(otherPlayerId);
        host.leftGame  = false;
        other.leftGame = false;

        Vector2 throne      = gm.throneRoom;
        Vector2 nearOutside = throne + new Vector2(2f, 0f);

        switch (scenario)
        {
            case Scenario.CrystalWin:
                host.pos = throne; other.pos = throne;
                SetCarry(host, hostCrystals);
                SetCarry(other, otherCrystals);
                host.energy = hostEnergy; other.energy = otherEnergy;
                break;

            case Scenario.EnergyTieBreak:
                host.pos = throne; other.pos = throne;
                SetCarry(host, 1); SetCarry(other, 1);
                host.energy = Mathf.Max(otherEnergy + 2, 5);
                other.energy = otherEnergy;
                break;

            case Scenario.SuddenDeath:
                host.pos = throne; other.pos = throne;
                SetCarry(host, 1); SetCarry(other, 1);
                host.energy = otherEnergy; other.energy = otherEnergy;
                break;

            case Scenario.NobodyAtThrone:
                host.pos = nearOutside; other.pos = nearOutside + new Vector2(1f, 0f);
                ClearCarry(host); ClearCarry(other);
                host.energy = 5; other.energy = 5;
                break;
        }

        // Apply via host path so all systems react
        gm.HostSetPlayerPosition(host.playerId, host.pos);
        gm.HostSetPlayerPosition(other.playerId, other.pos);

        // End soon so EvaluateAndFinishGame runs naturally
        gm.timeEndUtc = System.DateTimeOffset.UtcNow
            .AddSeconds(Mathf.Max(0.5f, timeToFinishSeconds))
            .ToUnixTimeMilliseconds();

        float t = 0f;
        while (gm.lastResult == null && t < timeToFinishSeconds + 3f)
        {
            t += Time.deltaTime;
            yield return null;
        }

        if (gm.lastResult == null)
            Debug.LogWarning("[WinConditionTestRig] No result yet; increase timeToFinishSeconds.");
        else
            Debug.Log($"[WinConditionTestRig] Winner={gm.lastResult.winnerId ?? "<none>"} | {gm.lastResult.reason}");

        _running = false;
    }

    // helpers
    void SetCarry(PlayerState ps, int n)
    {
        ClearCarry(ps);
        if (ps.carrying == null) ps.carrying = new System.Collections.Generic.List<string>();
        for (int i = 0; i < n; i++) ps.carrying.Add($"T_CR_{i}");
    }
    void ClearCarry(PlayerState ps) => ps.carrying?.Clear();

    // On-screen result + "Return to Start" button (Editor only HUD)
    void OnGUI()
    {
        if (!enableRig) return;
        var gm = GameManager.Instance;
        if (gm != null && gm.lastResult != null && gm.lastResult.gameOver)
        {
            GUILayout.BeginArea(new Rect(12, 12, 380, 180), GUI.skin.box);
            GUILayout.Label($"Result: {(string.IsNullOrEmpty(gm.lastResult.winnerId) ? "No winner" : gm.lastResult.winnerId)}");
            GUILayout.Label(gm.lastResult.reason);
            if (GUILayout.Button("Return to Start"))
            {
                gm.ResetToMenuState();
                gm.modalOpen = false;
                MapViewController.Instance?.ShowWorldFull();
                var start = FindObjectOfType<StartMenuUI>(true);
                if (start) start.gameObject.SetActive(true);
            }
            GUILayout.EndArea();
        }
    }
}
#endif
