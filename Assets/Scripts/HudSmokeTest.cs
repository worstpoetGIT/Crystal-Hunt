// HudSmokeTest.cs
using System.Collections;
using UnityEngine;

public class HudSmokeTest : MonoBehaviour
{
    IEnumerator Start()
    {
        NetDebugHUD.Log("[SMOKE] HUD online");
        for (int i = 1; i <= 5; i++)
        {
            NetDebugHUD.Log($"[SMOKE] Ping {i}");
            yield return new WaitForSeconds(0.5f);
        }
        NetDebugHUD.Log("[SMOKE] Done.");
    }
}
