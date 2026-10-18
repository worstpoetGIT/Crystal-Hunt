using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

public static class NetUtil
{
    public static string FirstLanIPv4()
    {
        string fallback = "0.0.0.0";
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                var props = ni.GetIPProperties();
                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(ua.Address)) continue;
                    // Prefer Wi-Fi/AP names when present, else return first usable IPv4
                    var n = ni.Name.ToLower();
                    if (n.Contains("wlan") || n.Contains("wifi") || n.Contains("ap"))
                        return ua.Address.ToString();
                    fallback = ua.Address.ToString();
                }
            }
        } catch { }
        return fallback;
    }

    public static List<string> AllIPv4()
    {
        var list = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(ua.Address))
                        list.Add($"{ni.Name}:{ua.Address}");
                }
            }
        } catch { }
        return list;
    }
}
