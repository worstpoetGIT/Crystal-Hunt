using System.Collections;
using UnityEngine;

public static class AndroidLocationPermission
{
#if UNITY_ANDROID && !UNITY_EDITOR
    public static IEnumerator CoRequest()
    {
        if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.FineLocation))
        {
            UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.FineLocation);
            // give the OS a frame or two
            float t = 0f;
            while (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.FineLocation) && t < 3f)
            { t += Time.unscaledDeltaTime; yield return null; }
        }
    }
#else
    public static IEnumerator CoRequest() { yield break; }
#endif
}
