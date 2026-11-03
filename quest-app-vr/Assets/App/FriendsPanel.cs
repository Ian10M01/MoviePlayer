// FriendsPanel.cs — "Watch together" panel: host a room, join one with a 6-digit code typed
// on an in-VR keypad, mute the mic, leave. Talks to PeerRoom; built on the UI canvas at runtime.

using System;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using MoviePlayer.Room;

namespace MoviePlayer.App
{
    public class FriendsPanel : MonoBehaviour
    {
        public PeerRoom       Room;
        public PlayerControls Ui;   // supplies the shared button/panel style
        public Action         OnClosed;

        static readonly Color ButtonColor = new Color(1f, 1f, 1f, 0.08f);
        static readonly Color Accent      = new Color(0.298f, 0.553f, 1f, 1f);
        static readonly Color Danger      = new Color(0.85f, 0.25f, 0.3f, 1f);

        private GameObject _panel, _keypad, _main;
        private TMP_Text   _status, _codeLabel, _muteLabel, _people;
        private string     _code = "";

        public bool IsOpen => _panel != null && _panel.activeSelf;

        public void Build(Transform canvas)
        {
            _panel = Ui.MakePanel(canvas, "FriendsPanel", new Vector2(900f, 860f), new Vector2(0f, 100f));
            _panel.SetActive(false);

            var title = PlayerControls.MakeLabel(_panel.transform, "Watch together", 40, 0f, TextAlignmentOptions.Center);
            Place(title.rectTransform, new Vector2(0f, 370f), new Vector2(800f, 60f));
            title.fontStyle = FontStyles.Bold;

            _status = PlayerControls.MakeLabel(_panel.transform, "Not in a room", 26, 0f, TextAlignmentOptions.Center);
            _status.textWrappingMode = TextWrappingModes.Normal;
            Place(_status.rectTransform, new Vector2(0f, 300f), new Vector2(820f, 70f));

            _people = PlayerControls.MakeLabel(_panel.transform, "", 24, 0f, TextAlignmentOptions.Center);
            _people.color = new Color(1f, 1f, 1f, 0.6f);
            Place(_people.rectTransform, new Vector2(0f, 245f), new Vector2(820f, 40f));

            // Main buttons
            _main = new GameObject("Main", typeof(RectTransform));
            _main.transform.SetParent(_panel.transform, false);
            Place((RectTransform)_main.transform, new Vector2(0f, -40f), new Vector2(820f, 500f));
            var grid = _main.AddComponent<VerticalLayoutGroup>();
            grid.spacing = 18f;
            grid.childControlWidth = grid.childControlHeight = true;
            grid.childForceExpandWidth = true;
            grid.childForceExpandHeight = false;

            BigButton(_main.transform, "Host a room", Accent, () => Room.Host(DisplayName()));
            BigButton(_main.transform, "Join with code", ButtonColor, () => ShowKeypad(true));
            _muteLabel = BigButton(_main.transform, "Mute mic", ButtonColor, ToggleMute);
            BigButton(_main.transform, "Leave room", Danger, () => Room.Leave());
            BigButton(_main.transform, "Close", ButtonColor, () => { Show(false); OnClosed?.Invoke(); });

            BuildKeypad();

            if (Room != null) Room.OnStatus += s => { if (_status != null) _status.text = s; RefreshPeople(); };
        }

        public void Show(bool show)
        {
            _panel.SetActive(show);
            if (show) { ShowKeypad(false); RefreshPeople(); }
        }

        public void Toggle() => Show(!IsOpen);

        private void BuildKeypad()
        {
            _keypad = new GameObject("Keypad", typeof(RectTransform));
            _keypad.transform.SetParent(_panel.transform, false);
            Place((RectTransform)_keypad.transform, new Vector2(0f, -60f), new Vector2(560f, 600f));

            _codeLabel = PlayerControls.MakeLabel(_keypad.transform, "______", 56, 0f, TextAlignmentOptions.Center);
            _codeLabel.characterSpacing = 20f;
            Place(_codeLabel.rectTransform, new Vector2(0f, 250f), new Vector2(560f, 80f));

            var keys = new GameObject("Keys", typeof(RectTransform), typeof(GridLayoutGroup));
            keys.transform.SetParent(_keypad.transform, false);
            Place((RectTransform)keys.transform, new Vector2(0f, -40f), new Vector2(480f, 460f));
            var g = keys.GetComponent<GridLayoutGroup>();
            g.cellSize = new Vector2(150f, 100f);
            g.spacing = new Vector2(15f, 15f);
            g.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            g.constraintCount = 3;

            foreach (var d in new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9" })
                Key(keys.transform, d, ButtonColor, () => Type(d));
            Key(keys.transform, "Back", ButtonColor, () => { if (_code.Length > 0) _code = _code[..^1]; UpdateCode(); });
            Key(keys.transform, "0", ButtonColor, () => Type("0"));
            Key(keys.transform, "Join", Accent, () =>
            {
                if (_code.Length != 6) return;
                Room.Join(_code, DisplayName());
                ShowKeypad(false);
            });

            _keypad.SetActive(false);
        }

        private void ShowKeypad(bool show)
        {
            _keypad.SetActive(show);
            _main.SetActive(!show);
            if (show) { _code = ""; UpdateCode(); }
        }

        private void Type(string digit)
        {
            if (_code.Length < 6) _code += digit;
            UpdateCode();
        }

        private void UpdateCode() => _codeLabel.text = _code.PadRight(6, '_');

        private void ToggleMute()
        {
            Room.Muted = !Room.Muted;
            _muteLabel.text = Room.Muted ? "Unmute mic" : "Mute mic";
        }

        private void RefreshPeople()
        {
            if (_people == null || Room == null) return;
            _people.text = Room.InRoom
                ? $"Code {Room.RoomCode}  ·  " + (Room.PeerNames.Count == 0 ? "waiting for friends" : string.Join(", ", Room.PeerNames))
                : "";
            if (_muteLabel != null) _muteLabel.text = Room.Muted ? "Unmute mic" : "Mute mic";
        }

        private void Update()
        {
            if (IsOpen && Time.frameCount % 30 == 0) RefreshPeople();
        }

        private TMP_Text BigButton(Transform parent, string text, Color bg, Action onClick)
        {
            var label = Ui.MakeButton(parent, text, bg, onClick, 0f);
            label.fontSize = 30;
            label.transform.parent.GetComponent<LayoutElement>().preferredHeight = 82f;
            return label;
        }

        private void Key(Transform parent, string text, Color bg, Action onClick)
        {
            var label = Ui.MakeButton(parent, text, bg, onClick, 0f);
            label.fontSize = 34;
        }

        private static void Place(RectTransform rt, Vector2 pos, Vector2 size)
        {
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
        }

        private static string DisplayName()
        {
            string name = PlayerPrefs.GetString("displayName", "");
            if (string.IsNullOrEmpty(name))
            {
                string id = SystemInfo.deviceUniqueIdentifier ?? "0000";
                name = "Quest " + id.Substring(Math.Max(0, id.Length - 4)).ToUpperInvariant();
                PlayerPrefs.SetString("displayName", name);
            }
            return name;
        }
    }
}
