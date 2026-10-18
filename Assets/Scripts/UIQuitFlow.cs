using UnityEngine;

public static class UIQuitFlow
{
    /// <summary>
    /// Cleanly return to the Start Panel from any state (gameplay, modal, etc.).
    /// Does not destroy objects; just resets UI/input to a safe menu state.
    /// </summary>
    public static void ReturnToStartPanel()
    {
        var gm = GameManager.Instance;
        if (gm != null)
        {
            gm.gameStarted = false;  // stop gameplay loop
            gm.modalOpen   = false;  // ensure input is not frozen
        }

        // Close any encounter modal if it was up
        var enc = EncounterUI.Instance ?? Object.FindObjectOfType<EncounterUI>(true);
        enc?.Hide();

        // Reset map view (world shown behind menus)
        MapViewController.Instance?.ShowWorldFull();

        // Hide options menu if present
        var ui = Object.FindObjectOfType<UIManager>(true);
        if (ui != null && ui.optionsMenu != null) ui.optionsMenu.SetActive(false);

        // Disable movement until a new session is started
        var pc = Object.FindObjectOfType<PlayerController>(true);
        if (pc != null) pc.enabled = false;

        // Show the Start panel
        var start = Object.FindObjectOfType<StartMenuUI>(true);
        if (start != null) start.gameObject.SetActive(true);

        // If you publish host lobbies, unpublish when returning to start (optional)
        #if USE_FIREBASE
        FirebaseLobby.Instance?.HostUnpublishLobby();
        #endif

        Debug.Log("[UIQuitFlow] Returned to Start Panel.");
    }
}
