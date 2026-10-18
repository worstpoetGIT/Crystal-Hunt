#if USE_FIREBASE
using Firebase;
using Firebase.Analytics;
using Firebase.Database;   // Added for Realtime DB
using UnityEngine;

public class FirebaseInit : MonoBehaviour
{
    public static bool Ready { get; private set; }

    void Awake()
    {
        DontDestroyOnLoad(gameObject);
        Debug.Log("[FirebaseInit] Awake - starting dependency check...");

        FirebaseApp.CheckAndFixDependenciesAsync().ContinueWith(t =>
        {
            Ready = (t.Result == DependencyStatus.Available);
            Debug.Log($"[FirebaseInit] Dependency check result: {t.Result}");

            if (Ready)
            {
                Debug.Log("[FirebaseInit] Firebase Ready ");

                // Analytics log
                FirebaseAnalytics.LogEvent(FirebaseAnalytics.EventAppOpen);
                Debug.Log("[FirebaseInit] Analytics EventAppOpen sent.");

                // Test write to Realtime DB
                var db = FirebaseDatabase.DefaultInstance;
                Debug.Log("[FirebaseInit] Attempting test write to /debug/testWrite...");

                db.RootReference.Child("debug").Child("testWrite")
                    .SetValueAsync("Hello Firebase from Unity")
                    .ContinueWith(writeTask =>
                    {
                        if (writeTask.IsCompletedSuccessfully)
                            Debug.Log("[FirebaseInit] Test write SUCCESS  (check Firebase Console).");
                        else
                            Debug.LogError($"[FirebaseInit] Test write FAILED : {writeTask.Exception}");
                    });
            }
            else
            {
                Debug.LogWarning($"[FirebaseInit] Firebase dependencies not available: {t.Result}");
            }
        });
    }
}
#endif
