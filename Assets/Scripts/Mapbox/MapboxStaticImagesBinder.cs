using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Networking;
using System.Collections;

public class MapboxStaticImagesBinder : MonoBehaviour
{
    [Header("Assign in Inspector")]
    public RawImage worldImage;           // MapImage_World
    public RawImage localImage;           // MapImage_Local
    public GeoMapper geo;                 // GeoMapper component
    public MapViewController view;        // existing MapViewController (ranges)

    [Header("Mapbox")]
    public string accessToken;
    public string styleId = "mapbox/streets-v12";
    [Tooltip("Max texture edge (px) requested for full-screen states")]
    public int worldMaxSize = 1280;
    public int localMaxSize = 1024;
    [Tooltip("Use retina images (@2x). Increases download size.")]
    public bool retina = true;

    [Header("Auto re-fetch when panel grows")]
    [Tooltip("If the panel gets > this factor bigger than the image, re-download once.")]
    public float refetchFactor = 1.25f;

    // caches
    int _lastWorldW, _lastWorldH;
    int _lastLocalW, _lastLocalH;
    double _worldLat, _worldLon; float _worldZoom;
    int? _lastLi = null;

    // PlayerPrefs keys (optional: persist last good GPS)
    const string PP_LAT = "Mapbox_LastLat";
    const string PP_LON = "Mapbox_LastLon";

    private IEnumerator Start()
    {
        // instead of duplicating the GPS+fetch logic here:
        yield return CoInitAndFetchWorld(true);
    }

    void LateUpdate()
    {
        var gm = GameManager.Instance;
        if (!gm || !gm.players.TryGetValue(gm.localPlayerId, out var me) || me == null) return;

        // --- LOCAL: re-center when entering a different location ---
        var li = me.insideLocationIndex;
        if (li != _lastLi)
        {
            _lastLi = li;
            if (li.HasValue && li.Value >= 0 && li.Value < gm.locationRects.Count)
            {
                var lr = gm.locationRects[li.Value];
                Vector2 wCenter = (lr.bl + lr.tr) * 0.5f;
                var (lat, lon) = geo.WorldToLatLon(wCenter);
                float z = ZoomForHalfRangeKm(lat,
                    view ? view.localRangeKm : geo.localRangeKm,
                    localMaxSize);
                StartCoroutine(LoadStaticImage(localImage, lat, lon, z, false, localMaxSize, localMaxSize));
            }
        }

        // --- Re-fetch world once if it has grown significantly (e.g., mini -> full) ---
        if (worldImage && worldImage.texture)
        {
            var r = worldImage.rectTransform.rect;
            if (r.width > _lastWorldW * refetchFactor || r.height > _lastWorldH * refetchFactor)
                StartCoroutine(LoadStaticImage(worldImage, _worldLat, _worldLon, _worldZoom, true, worldMaxSize, worldMaxSize));
        }

        // --- Re-fetch local once if it has grown significantly (e.g., opens to full) ---
        if (localImage && localImage.texture)
        {
            var r = localImage.rectTransform.rect;
            if (r.width > _lastLocalW * refetchFactor || r.height > _lastLocalH * refetchFactor)
            {
                if (_lastLi.HasValue && _lastLi.Value >= 0 && _lastLi.Value < gm.locationRects.Count)
                {
                    var lr = gm.locationRects[_lastLi.Value];
                    Vector2 wCenter = (lr.bl + lr.tr) * 0.5f;
                    var (lat, lon) = geo.WorldToLatLon(wCenter);
                    float z = ZoomForHalfRangeKm(lat,
                        view ? view.localRangeKm : geo.localRangeKm,
                        localMaxSize);
                    StartCoroutine(LoadStaticImage(localImage, lat, lon, z, false, localMaxSize, localMaxSize));
                }
            }
        }
    }

    IEnumerator LoadStaticImage(RawImage target, double lat, double lon, float zoom, bool isWorld, int maxW, int maxH)
    {
        if (!target || string.IsNullOrEmpty(accessToken)) yield break;

        // use max size to stay crisp; clamp to target rect (in case panel is smaller)
        int tW = Mathf.Min(maxW, Mathf.RoundToInt(Mathf.Max(512, target.rectTransform.rect.width)));
        int tH = Mathf.Min(maxH, Mathf.RoundToInt(Mathf.Max(512, target.rectTransform.rect.height)));

        string size = retina ? $"{tW}x{tH}@2x" : $"{tW}x{tH}";
        string url = $"https://api.mapbox.com/styles/v1/{styleId}/static/{lon:F6},{lat:F6},{zoom:F2}/{size}?access_token={accessToken}";

        using (var req = UnityWebRequestTexture.GetTexture(url))
        {
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning("[Mapbox] Image load failed: " + req.error);
                yield break;
            }
            var tex = DownloadHandlerTexture.GetContent(req);
            target.texture = tex;
            target.color = Color.white;

            // preserve aspect: fill parent without distortion
            var arf = target.GetComponent<AspectRatioFitter>() ?? target.gameObject.AddComponent<AspectRatioFitter>();
            arf.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent; // covers panel; may crop a little
            arf.aspectRatio = (float)tex.width / tex.height;

            if (isWorld) { _lastWorldW = tex.width; _lastWorldH = tex.height; }
            else { _lastLocalW = tex.width; _lastLocalH = tex.height; }
        }
    }

    // WebMercator: compute a zoom that shows ~±halfRangeKm across the image width
    float ZoomForHalfRangeKm(double atLatDeg, float halfRangeKm, int widthPx)
    {
        double metersAcross = Mathf.Max(0.1f, halfRangeKm) * 2 * 1000.0;
        double mPerPxTarget = metersAcross / Mathf.Max(256, widthPx);
        double mPerPxAtZ0 = 156543.03392 * Mathf.Cos((float)(atLatDeg * Mathf.Deg2Rad));
        double z = Mathf.Log((float)(mPerPxAtZ0 / mPerPxTarget), 2f);
        return Mathf.Clamp((float)z, 3f, 20f);
    }

    // ---------------- PUBLIC API ----------------

    public void InitAndFetchWorld(bool reacquireGps = true)
    {
        StartCoroutine(CoInitAndFetchWorld(reacquireGps));
    }

    IEnumerator CoInitAndFetchWorld(bool reacquireGps)
    {
        // 1) Defaults = Dublin fallback (always safe)
        double lat = geo ? geo.fallbackLat : 53.3498;   // Dublin
        double lon = geo ? geo.fallbackLon : -6.2603;
        bool haveGps = false;

        // 2) Try last known good GPS as a secondary fallback (nice UX between runs)
        if (PlayerPrefs.HasKey(PP_LAT) && PlayerPrefs.HasKey(PP_LON))
        {
            lat = PlayerPrefs.GetFloat(PP_LAT);
            lon = PlayerPrefs.GetFloat(PP_LON);
        }

#if UNITY_ANDROID || UNITY_IOS
        if (reacquireGps)
        {
    #if UNITY_ANDROID && !UNITY_EDITOR
            // Ask for permission politely
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.FineLocation))
            {
                UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.FineLocation);
                float waitPerm = 0f;
                while (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.FineLocation) && waitPerm < 3f)
                { waitPerm += Time.unscaledDeltaTime; yield return null; }
            }
    #endif
            if (Input.location.isEnabledByUser)
            {
                Input.location.Start(1f, 1f);
                float t = 0f;
                // wait up to ~5s for GPS to init
                while (Input.location.status == LocationServiceStatus.Initializing && t < 5f)
                { t += Time.unscaledDeltaTime; yield return null; }

                if (Input.location.status == LocationServiceStatus.Running)
                {
                    var ld = Input.location.lastData;
                    if (ld.latitude != 0 || ld.longitude != 0)
                    {
                        lat = ld.latitude;
                        lon = ld.longitude;
                        haveGps = true;

                        // persist as last good
                        PlayerPrefs.SetFloat(PP_LAT, (float)lat);
                        PlayerPrefs.SetFloat(PP_LON, (float)lon);
                        PlayerPrefs.Save();
                    }
                }

                // Stop GPS to save battery; you’ll re-start next time you refetch.
                try { Input.location.Stop(); } catch {}
            }
        }
#endif

        // 3) Feed into your GeoMapper (it will position the world center ~3km offset etc.)
        if (geo) geo.SetupFromGpsOrFallback(lat, lon, haveGps);

        _worldLat  = geo ? geo.centerLat : lat;
        _worldLon  = geo ? geo.centerLon : lon;
        _worldZoom = ZoomForHalfRangeKm(_worldLat,
            view ? view.worldRangeKm : (geo ? geo.worldRangeKm : 3f),
            worldMaxSize);

        // 4) Fetch the world image at max intended size (stays crisp when expanding)
        yield return LoadStaticImage(worldImage, _worldLat, _worldLon, _worldZoom, true, worldMaxSize, worldMaxSize);
    }

    public void ResetMaps()
    {
        StopAllCoroutines();

        if (worldImage)
        {
            worldImage.texture = null;
            var c = worldImage.color; worldImage.color = new Color(c.r, c.g, c.b, 0f);
        }
        if (localImage)
        {
            localImage.texture = null;
            var c = localImage.color; localImage.color = new Color(c.r, c.g, c.b, 0f);
        }

        _lastLi = null;
        _lastWorldW = _lastWorldH = _lastLocalW = _lastLocalH = 0;
    }
}
