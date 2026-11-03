// DebugOverlay.cs — Shows the last N Unity log lines in a world-space panel.
// Attach to any active GameObject; the DebugText TMP sibling is auto-found.

using System.Collections.Generic;
using UnityEngine;
using TMPro;

namespace MoviePlayer.App
{
    /// <summary>
    /// Subscribes to Application.logMessageReceived and renders the last
    /// 12 entries in a TextMeshProUGUI component named "DebugText" found
    /// anywhere in this component's children.
    /// </summary>
    public class DebugOverlay : MonoBehaviour
    {
        [Tooltip("Maximum log lines to display.")]
        public int MaxLines = 12;

        private readonly Queue<string> _lines = new Queue<string>();
        private TextMeshProUGUI _label;

        // ── Lifecycle ─────────────────────────────────────────────────────────

        private void Awake()
        {
            _label = GetComponentInChildren<TextMeshProUGUI>(true);
            // Subscribe before other Start() calls so "[App] Started" is captured.
            Application.logMessageReceived += OnLog;
        }

        private void Start()
        {
            OnLog("[App] Scene started", null, LogType.Log);
        }

        private void OnDestroy()
        {
            Application.logMessageReceived -= OnLog;
        }

        // ── Log handler ───────────────────────────────────────────────────────

        private void OnLog(string condition, string stackTrace, LogType type)
        {
            string prefix = type switch
            {
                LogType.Error     => "<color=#FF6060>ERR</color>",
                LogType.Warning   => "<color=#FFD060>WRN</color>",
                LogType.Exception => "<color=#FF4040>EXC</color>",
                _                 => "<color=#90FF90>LOG</color>"
            };

            _lines.Enqueue($"{prefix} {condition}");
            while (_lines.Count > MaxLines) _lines.Dequeue();

            if (_label != null)
                _label.text = string.Join("\n", _lines);
        }
    }
}
