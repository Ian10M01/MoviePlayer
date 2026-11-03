using System;

namespace MoviePlayer.Proto
{
    // ─── Envelope ────────────────────────────────────────────────────────────────

    [Serializable]
    public class WsEnvelope
    {
        /// <summary>
        /// Message type discriminator.
        /// Values: joined | play_state | play | peer_buffer | peer_left |
        ///         rtc_offer | rtc_answer | rtc_ice | ping | pong
        /// </summary>
        public string type;
        /// <summary>JSON payload — deserialize based on `type`.</summary>
        public string payload;
    }

    // ─── Room State ───────────────────────────────────────────────────────────────

    [Serializable]
    public class RoomState
    {
        public string room_code;
        public string device_id;        // this client's assigned ID
        public string host_device_id;
        public SeatInfo[] seats;
        public string hls_url;
        public string manifest_id;
        public MediaManifest manifest;
        /// <summary>TURN credentials fetched at join time.</summary>
        public TurnCredentials turn;
    }

    [Serializable]
    public class SeatInfo
    {
        public string device_id;
        public string display_name;
        /// <summary>host | guest</summary>
        public string role;
        public int seat_index;          // 0-3
        public bool connected;
    }

    [Serializable]
    public class TurnCredentials
    {
        public string url;
        public string username;
        public string credential;
    }

    // ─── Playback Control (HOST → all) ───────────────────────────────────────────

    /// <summary>Sent by host on pause/seek/rate change.</summary>
    [Serializable]
    public class PlayStateMsg
    {
        public long position_ms;
        public bool playing;
        /// <summary>Host wall-clock UTC ms at the moment of send.</summary>
        public long host_wall_ms;
    }

    /// <summary>Sent by host to trigger synchronized play across all guests.</summary>
    [Serializable]
    public class PlayMsg
    {
        /// <summary>UTC ms at which all clients should call Play().</summary>
        public long wall_start_ms;
        public long position_ms;
    }

    // ─── Buffer Status (GUEST → host) ────────────────────────────────────────────

    [Serializable]
    public class BufferStatusMsg
    {
        public string device_id;
        public float buffered_seconds;
        public bool ready;
    }

    // ─── Peer Events ─────────────────────────────────────────────────────────────

    [Serializable]
    public class PeerLeftMsg
    {
        public string device_id;
    }

    // ─── WebRTC Signaling ────────────────────────────────────────────────────────

    [Serializable]
    public class RtcOfferMsg
    {
        public string from_device_id;
        public string to_device_id;
        public string sdp;
        public string type;             // "offer"
    }

    [Serializable]
    public class RtcAnswerMsg
    {
        public string from_device_id;
        public string to_device_id;
        public string sdp;
        public string type;             // "answer"
    }

    [Serializable]
    public class RtcIceMsg
    {
        public string from_device_id;
        public string to_device_id;
        public string candidate;
        public string sdp_mid;
        public int sdp_mline_index;
    }

    // ─── Peer Buffer (for LobbyUI) ───────────────────────────────────────────────

    [Serializable]
    public class PeerBufferStatus
    {
        public string device_id;
        public string display_name;
        public float buffered_seconds;
        public bool ready;
        public float last_update_time;  // Time.realtimeSinceStartup
    }
}
