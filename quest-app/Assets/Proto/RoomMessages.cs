using System;

namespace MoviePlayer.Proto
{
    // ─── Envelope ────────────────────────────────────────────────────────────────

    [Serializable]
    public class WsEnvelope
    {
        /// <summary>
        /// Message type discriminator.
        /// Values: joined | play_state | play | pause | seek | peer_buffer | peer_left |
        ///         peer_joined | rtc_offer | rtc_answer | rtc_ice | ping | pong
        /// </summary>
        public string type;
        /// <summary>JSON payload string — deserialize with JsonUtility.FromJson based on `type`.</summary>
        public string payload;
    }

    // ─── Room State (received in the 'joined' WebSocket message) ─────────────────

    [Serializable]
    public class RoomState
    {
        public string room_code;          // kept for LobbyUI.SetRoomCode
        public string device_id;          // this client's server-assigned ID
        public string host_device_id;
        public SeatInfo[] seats;
        public string hls_url;            // kept for host-side compatibility
        public string manifest_id;
        /// <summary>Playback position snapshot at the moment this client joined.</summary>
        public PlayState play_state;
        /// <summary>TURN relay credentials (null if WebRTC not configured).</summary>
        public TurnConfig turn;
    }

    [Serializable]
    public class SeatInfo
    {
        public string device_id;
        public string display_name;
        /// <summary>host | guest</summary>
        public string role;
        public int seat_index;            // 0-based
        public bool connected;
    }

    [Serializable]
    public class TurnConfig
    {
        public string url;
        public string username;
        public string credential;
    }

    // ─── Playback State snapshot (embedded in 'joined' message) ──────────────────

    /// <summary>
    /// Playback position snapshot at join time.
    /// Note: sync messages use <see cref="PlayStateMsg"/> which carries host_wall_ms.
    /// </summary>
    [Serializable]
    public class PlayState
    {
        public long position_ms;
        public bool playing;
        /// <summary>Host wall-clock UTC ms when this snapshot was captured.</summary>
        public long wall_start_ms;
    }

    // ─── Playback Control (HOST → all) ───────────────────────────────────────────

    /// <summary>Sent by host on pause / seek / periodic sync tick.</summary>
    [Serializable]
    public class PlayStateMsg
    {
        public long position_ms;
        public bool playing;
        /// <summary>Host wall-clock UTC ms at the moment of send.</summary>
        public long host_wall_ms;
    }

    /// <summary>Sent by host to trigger wall-clock-synchronized play across all guests.</summary>
    [Serializable]
    public class PlayMsg
    {
        /// <summary>UTC ms at which all clients should call Play().</summary>
        public long wall_start_ms;
        public long position_ms;
    }

    // ─── Buffer Status (GUEST → server, server re-broadcasts as peer_buffer) ──────

    /// <summary>
    /// Sent by the guest via type="buffer_status".
    /// The server injects device_id and re-broadcasts as type="peer_buffer" (PeerBufferMsg).
    /// </summary>
    [Serializable]
    public class BufferStatusMsg
    {
        public float buffered_s;
        public bool  ready;
    }

    // ─── Peer Events (SERVER → remaining peers) ───────────────────────────────────

    /// <summary>Received by the host when a guest reports their buffer state.</summary>
    [Serializable]
    public class PeerBufferMsg
    {
        public string device_id;
        public float  buffered_s;
        public bool   ready;
    }

    [Serializable]
    public class PeerLeftMsg
    {
        public string device_id;
    }

    [Serializable]
    public class PeerJoinedMsg
    {
        public string device_id;
        public string role;
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
        public int    sdp_mline_index;
    }

    // ─── HTTP DTOs ───────────────────────────────────────────────────────────────

    [Serializable]
    public class CreateRoomResponse
    {
        public string room_code;
        public string room_token;
        public string hls_url;
    }

    [Serializable]
    public class JoinRoomResponse
    {
        public string room_token;
        public string hls_url;
        public string media_id;
        public string media_name;
    }

    // ─── Peer Buffer Status (LobbyUI display model) ───────────────────────────────

    [Serializable]
    public class PeerBufferStatus
    {
        public string device_id;
        public string display_name;
        public float  buffered_s;
        public bool   ready;
        public float  last_update_time;  // Time.realtimeSinceStartup
    }
}
