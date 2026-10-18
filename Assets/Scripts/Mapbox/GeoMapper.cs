using UnityEngine;

public class GeoMapper : MonoBehaviour
{
    [Header("Ranges (km) must match your UI")]
    public float worldRangeKm = 3f;   // half-extent for world view
    public float localRangeKm = 1f;   // half-extent for local view
    public float offsetKmFromPlayer = 3f; // center the world view ~3km away

    [Header("Fallback (Dublin, IE)")]
    public double fallbackLat = 53.3498;
    public double fallbackLon = -6.2603;

    [HideInInspector] public double centerLat, centerLon, latMin, latMax, lonMin, lonMax;

    const double KM_PER_DEG_LAT = 110.574;
    static double KmPerDegLon(double latDeg) => 111.320 * Mathf.Cos((float)(latDeg * Mathf.Deg2Rad));

    public void SetupFromGpsOrFallback(double playerLat, double playerLon, bool haveGps)
    {
        double srcLat = haveGps ? playerLat : fallbackLat;
        double srcLon = haveGps ? playerLon : fallbackLon;

        // shift "east" by offsetKmFromPlayer (privacy-friendly world center)
        centerLat = srcLat;
        centerLon = srcLon + (offsetKmFromPlayer / KmPerDegLon(srcLat));

        // compute world geo-rect for ±worldRangeKm (used for world→lat/lon mapping)
        double dLat = worldRangeKm / KM_PER_DEG_LAT;
        double dLon = worldRangeKm / KmPerDegLon(centerLat);
        latMin = centerLat - dLat; latMax = centerLat + dLat;
        lonMin = centerLon - dLon; lonMax = centerLon + dLon;
    }

    // Normalize game coords using the *existing* GameManager boundary (0..1)
    public Vector2 To01(Vector2 world)
    {
        var gm = GameManager.Instance;
        gm.GetWorldMinMax(out var wmin, out var wmax);
        return new Vector2(
            Mathf.InverseLerp(wmin.x, wmax.x, world.x),
            Mathf.InverseLerp(wmin.y, wmax.y, world.y)
        );
    }

    // Convert game coords → (lat,lon) within the world geo-rect
    public (double lat, double lon) WorldToLatLon(Vector2 world)
    {
        var n = To01(world);
        double lat = Mathf.Lerp((float)latMin, (float)latMax, n.y);
        double lon = Mathf.Lerp((float)lonMin, (float)lonMax, n.x);
        return (lat, lon);
    }
}
