using System.Collections.Generic;
using System.Linq;
using System.Text;
using TMPro;
using UnityEngine;

public class NetDebugHUD : MonoBehaviour
{
    public static NetDebugHUD I { get; private set; }

    [Header("UI")]
    public TMP_Text target;
    [TextArea] public string stickyHeader = "NET HUD";
    public int maxLines = 60;              // kept for compatibility (unused in essential mode)

    [Header("Options")]
    [Tooltip("Leave OFF – we now show only curated lines.")]
    public bool mirrorUnityConsole = false;
    public bool showTimestamps     = false;

    // legacy queue (kept for compatibility with NetDebugHUD.Log)
    readonly Queue<string> _lines = new Queue<string>(256);
    string _pendingFlush;

    void Awake()
    {
        if (I != null && I != this) { Destroy(gameObject); return; }
        I = this;
        DontDestroyOnLoad(gameObject);

        if (mirrorUnityConsole)
            Application.logMessageReceived += OnLogMessage;

        // draw header once
        if (target != null) target.text = stickyHeader;
    }

    void OnDestroy()
    {
        if (I == this) I = null;
        Application.logMessageReceived -= OnLogMessage;
    }

    void LateUpdate()
    {
        if (_pendingFlush == null) return;
        if (target != null) target.text = _pendingFlush;
        _pendingFlush = null;
    }

    // ---------- Essential-only helpers (use these) ----------
    public static void ShowEssential(params string[] lines)
    {
        if (I == null || I.target == null) return;
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(I.stickyHeader)) sb.AppendLine(I.stickyHeader);
        foreach (var s in lines) if (!string.IsNullOrEmpty(s)) sb.AppendLine(s);
        I._pendingFlush = sb.ToString();
    }

    public static void ShowList(string header, IEnumerable<string> lines)
    {
        if (I == null || I.target == null) return;
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(header)) sb.AppendLine(header);
        foreach (var s in lines) if (!string.IsNullOrEmpty(s)) sb.AppendLine(s);
        I._pendingFlush = sb.ToString();
    }

    public static void Clear()
    {
        if (I == null) return;
        I._lines.Clear();
        I._pendingFlush = I.stickyHeader;
    }

    // ---------- Legacy minimal log (rarely used now) ----------
    public static void Log(string msg)
    {
        if (I == null) return;
        I.Enqueue(msg);
    }

    void OnLogMessage(string condition, string stackTrace, LogType type)
    {
        // If mirroring is enabled, still keep it short
        if (type == LogType.Exception || type == LogType.Error)
            Log($"[{type}] {condition}");
        else
            Log(condition);
    }

    void Enqueue(string raw)
    {
        var s = showTimestamps ? $"[{System.DateTime.Now:HH:mm:ss}] {raw}" : raw;
        _lines.Enqueue(s);
        while (_lines.Count > Mathf.Max(5, maxLines)) _lines.Dequeue();

        var sb = new StringBuilder(1024);
        if (!string.IsNullOrEmpty(stickyHeader)) sb.AppendLine(stickyHeader);
        foreach (var l in _lines) sb.AppendLine(l);
        _pendingFlush = sb.ToString();
    }
}
