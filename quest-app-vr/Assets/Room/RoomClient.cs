using System;
using System.Collections;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using MoviePlayer.Proto;

// NativeWebSocket (https://github.com/endel/NativeWebSocket) is used on
// Android/Quest for WebSocket support without needing a .NET WebSocket polyfill.
// In the Unity Editor we fall back to System.Net.WebSockets.ClientWebSocket.
#if !UNITY_EDITOR && UNITY_ANDROID
using NativeWebSocket;
#else
using System.Net.WebSockets;
#endif

namespace MoviePlayer.Room
{
    /// <summary>
    /// Maintains a WebSocket connection to the cloud sync server and
    /// dispatches typed message events to other components.
    ///
    /// Message envelope schema (JSON):
    ///   { "type": "...", "payload": "{...}" }
    ///
    /// Outgoing message types sent by this client:
    ///   play_state | play | pause | seek | buffer_status | ping
    ///   rtc_offer  | rtc_answer | rtc_ice
    ///
    /// Incoming message types handled:
    ///   joined | play_state | play | peer_buffer | peer_left
    ///   rtc_offer | rtc_answer | rtc_ice | pong
    /// </summary>
    public class RoomClient : MonoBehaviour
    {
        // ── Events ────────────────────────────────────────────────────────────
        public event Action<RoomState>       OnJoined;
        public event Action<PlayStateMsg>    OnPlayState;
        public event Action<PlayMsg>         OnPlay;
        public event Action<PeerBufferMsg>   OnPeerBuffer;
        public event Action<string>          OnPeerLeft;
        public event Action<PeerJoinedMsg>   OnPeerJoined;
        // WebRTC signaling
        public event Action<RtcOfferMsg>     OnRtcOffer;
        public event Action<RtcAnswerMsg>    OnRtcAnswer;
        public event Action<RtcIceMsg>       OnRtcIce;

        // ── Inspector ─────────────────────────────────────────────────────────
        [Header("Connection")]
        public float HeartbeatIntervalSec = 15f;
        public float ReconnectBaseDelaySec = 1f;
        public float ReconnectMaxDelaySec  = 30f;

        // ── State ─────────────────────────────────────────────────────────────
        public bool IsConnected { get; private set; }
        public string DeviceId  { get; private set; }

        private string _wsUrl;
        private string _token;
        private bool   _shouldReconnect = true;
        private float  _heartbeatTimer  = 0f;
        private int    _reconnectAttempt = 0;

        // Thread-safe queue for dispatching to Unity main thread
        private readonly System.Collections.Concurrent.ConcurrentQueue<string>
            _inboundQueue = new System.Collections.Concurrent.ConcurrentQueue<string>();

#if !UNITY_EDITOR && UNITY_ANDROID
        private WebSocket _ws;
#else
        private ClientWebSocket _ws;
        private CancellationTokenSource _cts;
#endif

        // ─────────────────────────────────────────────────────────────────────
        #region Public API

        public void ConnectAsync(string wsUrl, string token)
        {
            _wsUrl = wsUrl;
            _token = token;
            _shouldReconnect = true;
            _reconnectAttempt = 0;
            StartCoroutine(ConnectLoop());
        }

        public void Disconnect()
        {
            _shouldReconnect = false;
            StopAllCoroutines();
            CloseSocket();
        }

        // HOST outgoing
        public void SendPlayState(long tMs, bool playing, long hostWallMs)
        {
            var msg = new PlayStateMsg
            {
                position_ms   = tMs,
                playing       = playing,
                host_wall_ms  = hostWallMs
            };
            Send("play_state", JsonUtility.ToJson(msg));
        }

        public void SendPlay(long positionMs, long wallStartMs)
        {
            var msg = new PlayMsg { position_ms = positionMs, wall_start_ms = wallStartMs };
            Send("play", JsonUtility.ToJson(msg));
        }

        public void SendPause(long tMs)
        {
            var msg = new PlayStateMsg
            {
                position_ms  = tMs,
                playing      = false,
                host_wall_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            Send("pause", JsonUtility.ToJson(msg));
        }

        public void SendSeek(long tMs)
        {
            var msg = new PlayStateMsg
            {
                position_ms  = tMs,
                playing      = false,
                host_wall_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            Send("seek", JsonUtility.ToJson(msg));
        }

        // GUEST outgoing
        public void SendBufferStatus(float bufferedSeconds, bool ready)
        {
            var msg = new BufferStatusMsg
            {
                buffered_s = bufferedSeconds,
                ready      = ready
            };
            Send("buffer_status", JsonUtility.ToJson(msg));
        }

        // WebRTC signaling
        public void SendRtcOffer(RtcOfferMsg msg)   => Send("rtc_offer",  JsonUtility.ToJson(msg));
        public void SendRtcAnswer(RtcAnswerMsg msg) => Send("rtc_answer", JsonUtility.ToJson(msg));
        public void SendRtcIce(RtcIceMsg msg)       => Send("rtc_ice",    JsonUtility.ToJson(msg));

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Unity Lifecycle

        private void Update()
        {
#if !UNITY_EDITOR && UNITY_ANDROID
            _ws?.DispatchMessageQueue();
#endif

            // Drain inbound queue on main thread
            while (_inboundQueue.TryDequeue(out string raw))
                DispatchMessage(raw);

            // Heartbeat
            if (IsConnected)
            {
                _heartbeatTimer += Time.deltaTime;
                if (_heartbeatTimer >= HeartbeatIntervalSec)
                {
                    _heartbeatTimer = 0f;
                    Send("ping", "{}");
                }
            }
        }

        private void OnDestroy()
        {
            _shouldReconnect = false;
            CloseSocket();
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Connection Loop

        private IEnumerator ConnectLoop()
        {
            while (_shouldReconnect)
            {
                yield return StartCoroutine(ConnectOnce());

                if (!_shouldReconnect) yield break;

                // Exponential backoff
                float delay = Mathf.Min(
                    ReconnectBaseDelaySec * Mathf.Pow(2f, _reconnectAttempt),
                    ReconnectMaxDelaySec);
                _reconnectAttempt++;
                Debug.Log($"[RoomClient] Reconnecting in {delay:F1}s (attempt {_reconnectAttempt})");
                yield return new WaitForSeconds(delay);
            }
        }

        private IEnumerator ConnectOnce()
        {
            string url = $"{_wsUrl}?token={Uri.EscapeDataString(_token)}";
            Debug.Log($"[RoomClient] Connecting to {url}");

#if !UNITY_EDITOR && UNITY_ANDROID
            _ws = new WebSocket(url);

            _ws.OnOpen    += () => { IsConnected = true; _reconnectAttempt = 0; _heartbeatTimer = 0f; };
            _ws.OnMessage += bytes => _inboundQueue.Enqueue(Encoding.UTF8.GetString(bytes));
            _ws.OnError   += err  => Debug.LogWarning($"[RoomClient] WS Error: {err}");
            _ws.OnClose   += code => { IsConnected = false; Debug.Log($"[RoomClient] WS Closed: {code}"); };

            // NativeWebSocket Connect is async; yield until connected or failed
            var connectTask = _ws.Connect();
            yield return new WaitUntil(() => connectTask.IsCompleted);

            if (_ws.State != WebSocketState.Open)
            {
                Debug.LogWarning("[RoomClient] Connection failed.");
                yield break;
            }

            // Stay connected until socket closes
            yield return new WaitUntil(() => _ws.State != WebSocketState.Open);
#else
            // Editor / non-Android fallback: System.Net.WebSockets
            _cts = new CancellationTokenSource();
            _ws  = new ClientWebSocket();

            var connectTask = _ws.ConnectAsync(new Uri(url), _cts.Token);
            while (!connectTask.IsCompleted)
                yield return null;

            if (_ws.State != System.Net.WebSockets.WebSocketState.Open)
            {
                Debug.LogWarning("[RoomClient] Connection failed.");
                yield break;
            }

            IsConnected = true;
            _reconnectAttempt = 0;
            _heartbeatTimer   = 0f;

            // Start background receive loop
            _ = ReceiveLoopAsync(_cts.Token);

            // Wait until socket is no longer open
            yield return new WaitUntil(() =>
                _ws.State != System.Net.WebSockets.WebSocketState.Open);

            IsConnected = false;
#endif
        }

#if UNITY_EDITOR || !UNITY_ANDROID
        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            var buffer = new byte[8192];
            try
            {
                while (_ws.State == System.Net.WebSockets.WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var sb     = new StringBuilder();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", ct);
                            return;
                        }
                        sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    } while (!result.EndOfMessage);

                    _inboundQueue.Enqueue(sb.ToString());
                }
            }
            catch (OperationCanceledException) { /* normal shutdown */ }
            catch (Exception e)
            {
                Debug.LogWarning($"[RoomClient] Receive error: {e.Message}");
            }
        }
#endif

        private void CloseSocket()
        {
            IsConnected = false;
#if !UNITY_EDITOR && UNITY_ANDROID
            _ws?.Close();
#else
            try { _cts?.Cancel(); _ws?.Abort(); } catch { }
#endif
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Send / Receive

        private void Send(string type, string payload)
        {
            if (!IsConnected)
            {
                Debug.LogWarning($"[RoomClient] Cannot send '{type}' — not connected.");
                return;
            }

            var env  = new WsEnvelope { type = type, payload = payload };
            var json = JsonUtility.ToJson(env);
            var data = Encoding.UTF8.GetBytes(json);

#if !UNITY_EDITOR && UNITY_ANDROID
            _ = _ws.Send(data);
#else
            if (_ws?.State == System.Net.WebSockets.WebSocketState.Open)
            {
                _ = _ws.SendAsync(
                    new ArraySegment<byte>(data),
                    WebSocketMessageType.Text, true,
                    _cts?.Token ?? CancellationToken.None);
            }
#endif
        }

        private void DispatchMessage(string raw)
        {
            WsEnvelope env;
            try { env = JsonUtility.FromJson<WsEnvelope>(raw); }
            catch (Exception e)
            {
                Debug.LogWarning($"[RoomClient] Failed to parse envelope: {e.Message}\n{raw}");
                return;
            }

            try
            {
                switch (env.type)
                {
                    case "joined":
                        var state = JsonUtility.FromJson<RoomState>(env.payload);
                        DeviceId = state.device_id;
                        OnJoined?.Invoke(state);
                        break;

                    case "play_state":
                        OnPlayState?.Invoke(JsonUtility.FromJson<PlayStateMsg>(env.payload));
                        break;

                    case "play":
                        OnPlay?.Invoke(JsonUtility.FromJson<PlayMsg>(env.payload));
                        break;

                    // pause and seek carry a PlayStateMsg payload (playing=false).
                    // Dispatch as OnPlayState so SyncEngine/PreCacheLobby can react.
                    case "pause":
                    case "seek":
                        OnPlayState?.Invoke(JsonUtility.FromJson<PlayStateMsg>(env.payload));
                        break;

                    case "peer_buffer":
                        OnPeerBuffer?.Invoke(JsonUtility.FromJson<PeerBufferMsg>(env.payload));
                        break;

                    case "peer_left":
                        OnPeerLeft?.Invoke(JsonUtility.FromJson<PeerLeftMsg>(env.payload).device_id);
                        break;

                    case "peer_joined":
                        OnPeerJoined?.Invoke(JsonUtility.FromJson<PeerJoinedMsg>(env.payload));
                        break;

                    case "rtc_offer":
                        OnRtcOffer?.Invoke(JsonUtility.FromJson<RtcOfferMsg>(env.payload));
                        break;

                    case "rtc_answer":
                        OnRtcAnswer?.Invoke(JsonUtility.FromJson<RtcAnswerMsg>(env.payload));
                        break;

                    case "rtc_ice":
                        OnRtcIce?.Invoke(JsonUtility.FromJson<RtcIceMsg>(env.payload));
                        break;

                    case "pong":
                        // heartbeat ACK — no action needed
                        break;

                    default:
                        Debug.LogWarning($"[RoomClient] Unknown message type: {env.type}");
                        break;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[RoomClient] Error dispatching '{env.type}': {e.Message}");
            }
        }

        #endregion
    }
}
