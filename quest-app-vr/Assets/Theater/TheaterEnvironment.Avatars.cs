using System.Collections.Generic;
using UnityEngine;
using TMPro;
using MoviePlayer.Proto;
using MoviePlayer.Room;

namespace MoviePlayer.Theater
{
    /// <summary>
    /// Friend avatars in theater seats. Each avatar is a capsule body, a sphere
    /// head and a world-space name tag that faces the viewer.
    ///
    /// Seat indices here are theater seat indices (see GetSeatPosition). Seat 0
    /// is reserved for the viewer and is never given to another member.
    ///
    /// RoomClient wiring (automatic when a RoomClient is found on this object
    /// or anywhere in the scene):
    ///   OnJoined     → seats every connected remote member from RoomState.seats
    ///   OnPeerJoined → seats the new member in the first free seat
    ///   OnPeerLeft   → removes that member's avatar
    /// </summary>
    public partial class TheaterEnvironment
    {
        /// <summary>One remote member's avatar and its occupied seat.</summary>
        private class MemberAvatar
        {
            public string      MemberId;
            public int         Seat;
            public GameObject  Root;
            public Transform   Label;
            public Material    Mat;
        }

        private readonly Dictionary<string, MemberAvatar> _avatars = new Dictionary<string, MemberAvatar>();
        private string[] _seatOccupant;   // member id per seat index, null when free
        private RoomClient _roomClient;

        // ─────────────────────────────────────────────────────────────────────
        #region Public API

        /// <summary>
        /// Shows an avatar for a room member in a theater seat. Moves the avatar
        /// if it already exists. Any other member sitting in that seat is removed.
        /// </summary>
        /// <param name="memberId">Stable device or member id.</param>
        /// <param name="displayName">Text shown on the name tag.</param>
        /// <param name="seatIndex">Theater seat index, 1 to SeatCount-1. Seat 0 is the viewer.</param>
        public void SetMemberSeated(string memberId, string displayName, int seatIndex)
        {
            if (string.IsNullOrEmpty(memberId)) return;

            if (seatIndex <= 0 || seatIndex >= _seatAnchors.Count)
            {
                Debug.LogWarning($"[TheaterEnvironment] Seat {seatIndex} is invalid for '{memberId}' " +
                                 $"(valid range 1..{_seatAnchors.Count - 1}).");
                return;
            }

            EnsureOccupancyTable();

            MemberAvatar avatar;
            if (_avatars.TryGetValue(memberId, out avatar))
            {
                // Free the seat this member is leaving
                if (avatar.Seat != seatIndex && _seatOccupant[avatar.Seat] == memberId)
                    _seatOccupant[avatar.Seat] = null;
            }
            else
            {
                avatar = CreateAvatar(memberId);
                _avatars[memberId] = avatar;
            }

            // Evict whoever is currently in the target seat
            string previous = _seatOccupant[seatIndex];
            if (previous != null && previous != memberId)
                RemoveMember(previous);

            _seatOccupant[seatIndex] = memberId;
            avatar.Seat = seatIndex;

            var anchor = _seatAnchors[seatIndex];
            avatar.Root.transform.SetPositionAndRotation(anchor.position, anchor.rotation);

            var tmp = avatar.Label.GetComponent<TextMeshPro>();
            if (tmp != null)
                tmp.text = string.IsNullOrEmpty(displayName) ? memberId : displayName;

            avatar.Root.SetActive(true);
        }

        /// <summary>Removes a member's avatar and frees their seat. Does nothing if the member is not shown.</summary>
        public void RemoveMember(string memberId)
        {
            if (string.IsNullOrEmpty(memberId)) return;

            MemberAvatar avatar;
            if (!_avatars.TryGetValue(memberId, out avatar)) return;

            _avatars.Remove(memberId);

            if (_seatOccupant != null && avatar.Seat >= 0 && avatar.Seat < _seatOccupant.Length
                && _seatOccupant[avatar.Seat] == memberId)
            {
                _seatOccupant[avatar.Seat] = null;
            }

            Destroy(avatar.Root);
            if (avatar.Mat != null) Destroy(avatar.Mat);
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Billboard

        private void LateUpdate()
        {
            if (_avatars.Count == 0) return;

            var cam = Camera.main;
            if (cam == null) return;

            // Name tags face the viewer. Their front faces the camera.
            Vector3 camPos = cam.transform.position;
            foreach (var avatar in _avatars.Values)
            {
                Transform label = avatar.Label;
                Vector3 toLabel = label.position - camPos;
                if (toLabel.sqrMagnitude > 0.0001f)
                    label.rotation = Quaternion.LookRotation(toLabel);
            }
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region RoomClient Wiring

        private void SubscribeToRoomClient()
        {
            _roomClient = GetComponent<RoomClient>();
            if (_roomClient == null)
                _roomClient = FindAnyObjectByType<RoomClient>();

            if (_roomClient == null)
            {
                Debug.Log("[TheaterEnvironment] No RoomClient found. Call SetMemberSeated / RemoveMember manually.");
                return;
            }

            _roomClient.OnJoined     += HandleRoomJoined;
            _roomClient.OnPeerJoined += HandlePeerJoined;
            _roomClient.OnPeerLeft   += HandlePeerLeft;
        }

        private void UnsubscribeFromRoomClient()
        {
            if (_roomClient == null) return;

            _roomClient.OnJoined     -= HandleRoomJoined;
            _roomClient.OnPeerJoined -= HandlePeerJoined;
            _roomClient.OnPeerLeft   -= HandlePeerLeft;
            _roomClient = null;
        }

        /// <summary>Seats everyone already in the room. Server seat s maps to theater seat s + ServerSeatIndexOffset.</summary>
        private void HandleRoomJoined(RoomState state)
        {
            // A fresh join replaces the whole roster
            var ids = new List<string>(_avatars.Keys);
            foreach (var id in ids) RemoveMember(id);

            if (state.seats == null) return;

            foreach (var seat in state.seats)
            {
                if (seat == null || !seat.connected) continue;
                if (seat.device_id == _roomClient.DeviceId) continue;   // that is the viewer

                string name = string.IsNullOrEmpty(seat.display_name) ? seat.device_id : seat.display_name;
                SetMemberSeated(seat.device_id, name, seat.seat_index + ServerSeatIndexOffset);
            }
        }

        /// <summary>peer_joined has no seat index, so the new member takes the first free seat.</summary>
        private void HandlePeerJoined(PeerJoinedMsg msg)
        {
            if (msg == null || string.IsNullOrEmpty(msg.device_id)) return;
            if (msg.device_id == _roomClient.DeviceId) return;
            if (_avatars.ContainsKey(msg.device_id)) return;

            EnsureOccupancyTable();

            for (int seat = 1; seat < _seatOccupant.Length; seat++)
            {
                if (_seatOccupant[seat] != null) continue;

                string label = msg.role == "host" ? "Host" : "Guest";
                SetMemberSeated(msg.device_id, label, seat);
                return;
            }

            Debug.LogWarning($"[TheaterEnvironment] No free seat for '{msg.device_id}'.");
        }

        private void HandlePeerLeft(string deviceId)
        {
            RemoveMember(deviceId);
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Avatar Construction

        private void EnsureOccupancyTable()
        {
            if (_seatOccupant == null || _seatOccupant.Length != _seatAnchors.Count)
                _seatOccupant = new string[_seatAnchors.Count];
        }

        /// <summary>Builds the avatar hierarchy with the head at the seat anchor origin.</summary>
        private MemberAvatar CreateAvatar(string memberId)
        {
            var root = new GameObject($"Avatar_{memberId}");
            root.transform.SetParent(_avatarRoot.transform, false);
            root.SetActive(false);   // activated by SetMemberSeated after placement

            Material mat = CreateLitMaterial(AvatarColorFor(memberId), 0.3f);

            // Torso sits from the cushion up to the neck. Head at the anchor origin.
            SpawnPrimitive("Body", PrimitiveType.Capsule, root.transform,
                new Vector3(0f, -0.42f, 0f), new Vector3(0.6f, 0.3f, 0.6f), mat);
            SpawnPrimitive("Head", PrimitiveType.Sphere, root.transform,
                Vector3.zero, Vector3.one * 0.22f, mat);

            // Name tag above the head. Scale 0.01 is the standard world-space TMP scale.
            var labelGo = new GameObject("NameLabel");
            labelGo.transform.SetParent(root.transform, false);
            labelGo.transform.localPosition = new Vector3(0f, 0.35f, 0f);
            labelGo.transform.localScale    = Vector3.one * 0.01f;

            var tmp = labelGo.AddComponent<TextMeshPro>();
            tmp.text               = memberId;
            tmp.fontSize           = 36f;
            tmp.alignment          = TextAlignmentOptions.Center;
            tmp.color              = Color.white;
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.overflowMode       = TextOverflowModes.Ellipsis;
            tmp.rectTransform.sizeDelta = new Vector2(200f, 50f);

            return new MemberAvatar
            {
                MemberId = memberId,
                Seat     = -1,
                Root     = root,
                Label    = labelGo.transform,
                Mat      = mat
            };
        }

        /// <summary>Stable pastel colour per member, derived from a deterministic string hash.</summary>
        private static Color AvatarColorFor(string memberId)
        {
            int hash = 0;
            foreach (char c in memberId)
                hash = unchecked(hash * 31 + c);

            float hue = (hash & 0x7fffffff) % 360 / 360f;
            return Color.HSVToRGB(hue, 0.55f, 0.95f);
        }

        #endregion
    }
}
