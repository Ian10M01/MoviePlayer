// PeerRoom: serverless "watch together" rooms for MoviePlayerVR.
//
// Signaling goes through the free public PeerJS server (wss://0.peerjs.com/peerjs),
// media and data go peer-to-peer over WebRTC (com.unity.webrtc 3.x) with Google STUN.
//
//   Host  takes PeerJS id "mpvr-<6 digit code>".
//   Guest takes a random id "mpvr-g-<random>" and sends an OFFER to the host id.
//   Each link is one RTCPeerConnection carrying the mic audio track plus a reliable,
//   ordered RTCDataChannel ("room") for JSON messages.
//   The host is authoritative: it assigns seat slots (host = 0, guests 1, 2, ...),
//   broadcasts the roster, and relays playback/movie messages (star topology).
//   Guests additionally open direct links to each other (lower id offers) so everyone
//   hears everyone; if a guest-guest link fails, sync still works through the host.
//
// Data channel messages (JSON, field "t" is the type):
//   hello    {name}
//   roster   {members:[{id,name,slot}]}               host -> guests
//   playback {playing, positionMs, sentAtUnixMs, movieFile}
//   movie    {file}
//   ping     {t0} / pong {t0, t1}                      clock offset + RTT per link
//   full     {}                                         host -> guest, room has no free seat
//   bye      {}
//
// Not compiled behind any scripting define: Unity.WebRTC is an autoReferenced asmdef.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using MoviePlayer.Theater;
using NativeWebSocket;
using Newtonsoft.Json.Linq;
using Unity.WebRTC;
using UnityEngine;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif

namespace MoviePlayer.Room
{
    [DisallowMultipleComponent]
    public class PeerRoom : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────────
        [Header("Signaling (PeerJS server protocol)")]
        public string SignalingHost = "0.peerjs.com";
        public string SignalingPath = "/peerjs";
        public string SignalingKey  = "peerjs";
        public float  HeartbeatSec  = 5f;

        [Header("ICE")]
        public string[] StunUrls = { "stun:stun.l.google.com:19302", "stun:stun1.l.google.com:19302" };
        [Tooltip("Optional TURN relay for strict NATs, e.g. turn:host:3478")]
        public string TurnUrl;
        public string TurnUsername;
        public string TurnCredential;

        [Header("Timeouts")]
        public float JoinTimeoutSec       = 25f;
        public float MeshConnectTimeoutSec = 30f;
        public float DisconnectGraceSec   = 10f;

        [Header("Voice")]
        public int   MicSampleRate     = 48000;
        [Range(0f, 1f)] public float VoiceSpatialBlend = 1f;
        [Range(0f, 2f)] public float VoiceVolume       = 1f;

        [Tooltip("Optional; found automatically with FindAnyObjectByType when empty")]
        public TheaterEnvironment Theater;

        // ── Public API ────────────────────────────────────────────────────────
        /// <summary>True from Host()/Join() until Leave() or a fatal error (includes the connecting phase).</summary>
        public bool InRoom => _active;
        public bool IsHost { get; private set; }
        /// <summary>6-digit room code (empty when not in a room).</summary>
        public string RoomCode { get; private set; } = "";

        public bool Muted
        {
            get => _muted;
            set
            {
                _muted = value;
                try { if (_micTrack != null) _micTrack.Enabled = !value; }
                catch (Exception e) { Debug.LogWarning($"{LogTag} mute: {e.Message}"); }
            }
        }

        /// <summary>Display names of the other people in the room (not including you).</summary>
        public IReadOnlyList<string> PeerNames => _peerNames;

        public event Action<string>       OnStatus;
        public event Action<bool, long>   OnRemotePlayback;
        public event Action<string>       OnRemoteMovie;

        public void Host(string displayName)
        {
            if (_active) EndSession(null);
            BeginSession(true, displayName, null);
        }

        public void Join(string code, string displayName)
        {
            code = (code ?? "").Trim();
            if (code.Length != 6 || !IsDigits(code))
            {
                Status("Room code must be 6 digits");
                return;
            }
            if (_active) EndSession(null);
            BeginSession(false, displayName, code);
        }

        public void Leave()
        {
            if (!_active) return;
            EndSession("Left the room");
        }

        public void SendPlayback(bool playing, long positionMs)
        {
            try
            {
                if (!_active) return;
                long now = UnixMs();
                if (IsEcho(playing, positionMs, now))
                {
                    Debug.Log($"{LogTag} suppressed echo of remote playback ({playing}, {positionMs} ms)");
                    return;
                }
                RememberPlayback(playing, positionMs);
                var msg = PlaybackMsg(playing, positionMs, now);
                if (IsHost) Broadcast(msg, null);
                else        SendToHost(msg);
            }
            catch (Exception e) { Debug.LogWarning($"{LogTag} SendPlayback: {e}"); }
        }

        public void SendMovie(string fileName)
        {
            try
            {
                if (!_active || string.IsNullOrEmpty(fileName)) return;
                if (fileName == _currentMovie && Time.unscaledTime - _lastRemoteMovieAt < 5f)
                {
                    Debug.Log($"{LogTag} suppressed echo of remote movie '{fileName}'");
                    return;
                }
                _currentMovie = fileName;
                _hasPlayback = false; // new movie: old position is meaningless
                var msg = new JObject { ["t"] = "movie", ["file"] = fileName };
                if (IsHost) Broadcast(msg, null);
                else        SendToHost(msg);
            }
            catch (Exception e) { Debug.LogWarning($"{LogTag} SendMovie: {e}"); }
        }

        // ── Internals ─────────────────────────────────────────────────────────
        const string LogTag = "[PeerRoom]";
        const string IdPrefix = "mpvr-";
        const string GuestPrefix = "mpvr-g-";

        class Link
        {
            public string Id;
            public string Name;
            public bool   Initiator;
            public string ConnectionId;
            public RTCPeerConnection Pc;
            public RTCDataChannel    Dc;
            public RTCRtpSender      Sender;
            public MediaStreamTrack  RemoteTrack;
            public GameObject        VoiceGo;
            public AudioSource       Voice;
            public bool  RemoteDescSet;
            public bool  AnswerReceived;
            public readonly List<RTCIceCandidateInit> PendingIce = new List<RTCIceCandidateInit>();
            public bool  Open;
            public float CreatedAt;
            public float DisconnectedSince = -1f;
            public float NextPingAt;
            public bool  HasOffset;
            public double ClockOffsetMs; // remote clock minus local clock
            public double BestRttMs = double.MaxValue;
        }

        class Member { public string Id; public string Name; public int Slot; }

        readonly Dictionary<string, Link> _links = new Dictionary<string, Link>();
        readonly Dictionary<string, Member> _members = new Dictionary<string, Member>(); // others, not me
        readonly List<string> _peerNames = new List<string>();
        readonly HashSet<string> _seated = new HashSet<string>();
        readonly System.Random _rng = new System.Random(Guid.NewGuid().GetHashCode());

        bool   _active;
        int    _epoch;
        string _displayName = "Friend";
        string _myId;
        string _hostId;
        string _token;
        int    _mySlot;
        bool   _joined;          // guest: data channel with host is open
        float  _joinDeadline;
        int    _hostIdAttempts;

        // signaling
        WebSocket _ws;
        bool  _signalOpen;
        bool  _everOpened;
        float _nextHeartbeat;
        float _reconnectAt = -1f;
        int   _reconnectAttempts;

        // mic
        bool             _muted;
        GameObject       _micGo;
        AudioSource      _micSource;
        AudioClip        _micClip;
        string           _micDevice;
        AudioStreamTrack _micTrack;
        MediaStream      _micStream;

        // sync
        string _currentMovie;
        float  _lastRemoteMovieAt = -100f;
        bool   _hasPlayback;
        bool   _pbPlaying;
        long   _pbPositionMs;
        float  _pbAt;
        bool   _remotePbPlaying;
        long   _remotePbPositionMs;
        long   _remotePbAtMs = long.MinValue;

        bool _webrtcUpdateStarted;

        // ── Unity lifecycle ───────────────────────────────────────────────────
        void Start()
        {
            EnsureWebRtcUpdate();
        }

        void EnsureWebRtcUpdate()
        {
            if (_webrtcUpdateStarted) return;
            try
            {
                StartCoroutine(WebRTC.Update());
                _webrtcUpdateStarted = true;
            }
            catch (Exception e) { Debug.LogWarning($"{LogTag} WebRTC.Update coroutine: {e.Message}"); }
        }

        void Update()
        {
            if (!_active) return;
            try { Tick(); }
            catch (Exception e) { Debug.LogError($"{LogTag} Update: {e}"); }
        }

        void OnDestroy()
        {
            try { if (_active) EndSession(null); }
            catch (Exception e) { Debug.LogWarning($"{LogTag} OnDestroy: {e.Message}"); }
        }

        void OnApplicationQuit()
        {
            try { if (_active) EndSession(null); }
            catch (Exception) { }
        }

        void Tick()
        {
            float now = Time.unscaledTime;

#if !UNITY_WEBGL || UNITY_EDITOR
            _ws?.DispatchMessageQueue();
#endif
            if (_signalOpen && now >= _nextHeartbeat)
            {
                _nextHeartbeat = now + HeartbeatSec;
                SendRaw("{\"type\":\"HEARTBEAT\"}");
            }

            if (_reconnectAt > 0f && now >= _reconnectAt)
            {
                _reconnectAt = -1f;
                ConnectSignaling();
            }

            if (!IsHost && !_joined && now >= _joinDeadline)
            {
                EndSession($"Couldn't reach room {RoomCode}. Check the code, or your network may block peer-to-peer.");
                return;
            }

            // Link health + pings
            List<string> drop = null;
            foreach (var link in _links.Values)
            {
                if (link.DisconnectedSince > 0f && now - link.DisconnectedSince > DisconnectGraceSec)
                    (drop ??= new List<string>()).Add(link.Id);
                else if (!link.Open && link.Id != _hostId && now - link.CreatedAt > MeshConnectTimeoutSec)
                    (drop ??= new List<string>()).Add(link.Id);
                else if (link.Open && now >= link.NextPingAt)
                {
                    link.NextPingAt = now + 2f;
                    SendTo(link, new JObject { ["t"] = "ping", ["t0"] = UnixMs() });
                }
            }
            if (drop != null)
                foreach (var id in drop) DropLink(id, "connection lost");
            if (!_active) return;

            KeepMicInSync();
        }

        // ── Session ───────────────────────────────────────────────────────────
        void BeginSession(bool host, string displayName, string code)
        {
            EnsureWebRtcUpdate();
            _epoch++;
            _active = true;
            IsHost = host;
            _displayName = string.IsNullOrWhiteSpace(displayName) ? "Friend" : displayName.Trim();
            if (_displayName.Length > 32) _displayName = _displayName.Substring(0, 32);
            _token = RandomAlnum(12);
            _joined = false;
            _signalOpen = false;
            _everOpened = false;
            _reconnectAttempts = 0;
            _reconnectAt = -1f;
            _hostIdAttempts = 0;
            _mySlot = 0;
            _currentMovie = null;
            _hasPlayback = false;
            _remotePbAtMs = long.MinValue;
            _members.Clear();
            RebuildPeerNames();

            if (host)
            {
                RoomCode = NewCode();
                _myId = IdPrefix + RoomCode;
                _hostId = _myId;
                Status($"Creating room {RoomCode}…");
            }
            else
            {
                RoomCode = code;
                _hostId = IdPrefix + code;
                _myId = GuestPrefix + RandomAlnum(10);
                _joinDeadline = Time.unscaledTime + JoinTimeoutSec;
                Status($"Connecting to room {RoomCode}…");
            }
            Debug.Log($"{LogTag} begin session host={host} code={RoomCode} myId={_myId}");

            SetupMicTrack();
            StartCoroutine(StartMicRoutine(_epoch));
            ConnectSignaling();
        }

        void EndSession(string status)
        {
            if (!_active) { if (status != null) Status(status); return; }
            Debug.Log($"{LogTag} end session ({status ?? "silent"})");
            _active = false;
            _epoch++;

            foreach (var link in new List<Link>(_links.Values))
            {
                try { SendTo(link, new JObject { ["t"] = "bye" }); } catch (Exception) { }
                try { SendSignal("LEAVE", link.Id, null); } catch (Exception) { }
                DisposeLink(link);
            }
            _links.Clear();
            _members.Clear();
            RebuildPeerNames();
            ClearSeats();
            CloseSignaling();
            TeardownMic();

            IsHost = false;
            _joined = false;
            RoomCode = "";
            _reconnectAt = -1f;
            if (status != null) Status(status);
        }

        // ── Signaling (PeerJS) ────────────────────────────────────────────────
        void ConnectSignaling()
        {
            CloseSignaling();
            if (!_active) return;
            string url = $"wss://{SignalingHost}{SignalingPath}?key={Uri.EscapeDataString(SignalingKey)}" +
                         $"&id={Uri.EscapeDataString(_myId)}&token={_token}&version=1.5.4";
            WebSocket ws;
            try { ws = new WebSocket(url); }
            catch (Exception e)
            {
                Debug.LogError($"{LogTag} websocket create failed: {e.Message}");
                ScheduleReconnect();
                return;
            }
            _ws = ws;
            ws.OnOpen += () => { if (ws == _ws) Debug.Log($"{LogTag} signaling socket open ({_myId})"); };
            ws.OnError += err => { if (ws == _ws) Debug.LogWarning($"{LogTag} signaling error: {err}"); };
            ws.OnClose += code =>
            {
                if (ws != _ws) return;
                Debug.LogWarning($"{LogTag} signaling closed: {code}");
                _signalOpen = false;
                _ws = null;
                OnSignalingLost();
            };
            ws.OnMessage += bytes =>
            {
                if (ws != _ws) return;
                try { HandleSignal(Encoding.UTF8.GetString(bytes)); }
                catch (Exception e) { Debug.LogError($"{LogTag} signal handling: {e}"); }
            };
            try
            {
                ws.Connect().ContinueWith(t =>
                {
                    if (t.IsFaulted) Debug.LogWarning($"{LogTag} signaling connect task: {t.Exception?.GetBaseException().Message}");
                });
            }
            catch (Exception e)
            {
                Debug.LogError($"{LogTag} signaling connect: {e.Message}");
                _ws = null;
                ScheduleReconnect();
            }
        }

        void CloseSignaling()
        {
            var ws = _ws;
            _ws = null;
            _signalOpen = false;
            if (ws == null) return;
            try
            {
                if (ws.State == WebSocketState.Open) _ = ws.Close();
                else ws.CancelConnection();
            }
            catch (Exception e) { Debug.LogWarning($"{LogTag} close signaling: {e.Message}"); }
        }

        void OnSignalingLost()
        {
            if (!_active) return;
            if (IsHost && !_everOpened && _hostIdAttempts >= 5)
            {
                EndSession("Couldn't reach the signaling server (0.peerjs.com). Check your internet connection.");
                return;
            }
            if (_links.Count == 0)
                Status(_everOpened ? "Signaling connection lost — reconnecting…" : "Can't reach signaling server — retrying…");
            ScheduleReconnect();
        }

        void ScheduleReconnect()
        {
            if (!_active) return;
            _reconnectAttempts++;
            if (IsHost && !_everOpened) _hostIdAttempts++;
            float delay = Mathf.Min(30f, 2f * Mathf.Pow(2f, Mathf.Min(4, _reconnectAttempts - 1)));
            _reconnectAt = Time.unscaledTime + delay;
            Debug.Log($"{LogTag} signaling reconnect in {delay:0}s (attempt {_reconnectAttempts})");
        }

        void SendSignal(string type, string dst, JObject payload)
        {
            var msg = new JObject { ["type"] = type, ["dst"] = dst };
            if (payload != null) msg["payload"] = payload;
            SendRaw(msg.ToString(Newtonsoft.Json.Formatting.None));
        }

        void SendRaw(string text)
        {
            var ws = _ws;
            if (ws == null) return;
            try
            {
                if (ws.State != WebSocketState.Open) return;
                ws.SendText(text).ContinueWith(t =>
                {
                    if (t.IsFaulted) Debug.LogWarning($"{LogTag} signaling send: {t.Exception?.GetBaseException().Message}");
                });
            }
            catch (Exception e) { Debug.LogWarning($"{LogTag} signaling send: {e.Message}"); }
        }

        void HandleSignal(string text)
        {
            if (!_active) return;
            JObject msg;
            try { msg = JObject.Parse(text); }
            catch (Exception) { Debug.LogWarning($"{LogTag} bad signaling json: {text}"); return; }

            string type = (string)msg["type"];
            string src = (string)msg["src"];
            var payload = msg["payload"] as JObject;

            switch (type)
            {
                case "OPEN":
                    _signalOpen = true;
                    _everOpened = true;
                    _reconnectAttempts = 0;
                    _nextHeartbeat = Time.unscaledTime + HeartbeatSec;
                    Debug.Log($"{LogTag} signaling OPEN as {_myId}");
                    if (IsHost) UpdateRoomStatus();
                    else if (!_links.ContainsKey(_hostId)) StartLink(_hostId);
                    break;

                case "HEARTBEAT":
                    break;

                case "ID-TAKEN":
                    Debug.LogWarning($"{LogTag} id {_myId} taken");
                    CloseSignaling();
                    if (IsHost && !_everOpened)
                    {
                        if (++_hostIdAttempts > 5) { EndSession("Couldn't create a room. Try again."); return; }
                        RoomCode = NewCode();
                        _myId = IdPrefix + RoomCode;
                        _hostId = _myId;
                        Status($"Creating room {RoomCode}…");
                        ConnectSignaling();
                    }
                    else if (IsHost)
                    {
                        // Our old socket may still be registered after a network drop; it expires soon.
                        ScheduleReconnect();
                    }
                    else
                    {
                        _myId = GuestPrefix + RandomAlnum(10);
                        ConnectSignaling();
                    }
                    break;

                case "INVALID-KEY":
                case "ERROR":
                {
                    string err = (string)payload?["msg"] ?? type;
                    Debug.LogError($"{LogTag} signaling {type}: {err}");
                    if (!_everOpened && _links.Count == 0) EndSession($"Signaling server error: {err}");
                    break;
                }

                case "OFFER":     HandleOffer(src, payload); break;
                case "ANSWER":    HandleAnswer(src, payload); break;
                case "CANDIDATE": HandleCandidate(src, payload); break;

                case "LEAVE":
                    if (!string.IsNullOrEmpty(src) && _links.ContainsKey(src)) DropLink(src, "left");
                    break;

                case "EXPIRE":
                    // A message we sent to `src` could not be delivered: that peer is not online.
                    if (!IsHost && src == _hostId && !_joined)
                        EndSession($"Room {RoomCode} not found. Check the code with your friend.");
                    else if (!string.IsNullOrEmpty(src) && _links.TryGetValue(src, out var l) && !l.Open)
                        DropLink(src, "unreachable");
                    break;

                default:
                    Debug.Log($"{LogTag} unhandled signaling message {type}");
                    break;
            }
        }

        // ── Peer links ────────────────────────────────────────────────────────
        RTCConfiguration BuildConfig()
        {
            var servers = new List<RTCIceServer>();
            if (StunUrls != null && StunUrls.Length > 0) servers.Add(new RTCIceServer { urls = StunUrls });
            if (!string.IsNullOrEmpty(TurnUrl))
                servers.Add(new RTCIceServer
                {
                    urls = new[] { TurnUrl },
                    username = TurnUsername,
                    credential = TurnCredential,
                    credentialType = RTCIceCredentialType.Password
                });
            return new RTCConfiguration { iceServers = servers.ToArray() };
        }

        Link CreateLink(string peerId, bool initiator, string connectionId)
        {
            var link = new Link
            {
                Id = peerId,
                Initiator = initiator,
                ConnectionId = connectionId ?? ("mc_" + RandomAlnum(12)),
                CreatedAt = Time.unscaledTime,
            };
            var config = BuildConfig();
            var pc = new RTCPeerConnection(ref config);
            link.Pc = pc;
            _links[peerId] = link;

            pc.OnIceCandidate = c =>
            {
                if (!IsCurrent(link) || c == null || string.IsNullOrEmpty(c.Candidate)) return;
                SendSignal("CANDIDATE", link.Id, new JObject
                {
                    ["candidate"] = new JObject
                    {
                        ["candidate"] = c.Candidate,
                        ["sdpMid"] = c.SdpMid,
                        ["sdpMLineIndex"] = c.SdpMLineIndex,
                    },
                    ["type"] = "media",
                    ["connectionId"] = link.ConnectionId,
                });
            };
            pc.OnConnectionStateChange = s => OnPcState(link, s);
            pc.OnTrack = e => OnRemoteTrack(link, e);
            pc.OnDataChannel = ch =>
            {
                if (!IsCurrent(link)) return;
                if (link.Dc == null) AttachDc(link, ch);
            };

            try
            {
                if (_micTrack != null) link.Sender = pc.AddTrack(_micTrack, _micStream);
            }
            catch (Exception e) { Debug.LogWarning($"{LogTag} add mic track: {e.Message}"); }

            if (initiator)
            {
                var dc = pc.CreateDataChannel("room", new RTCDataChannelInit { ordered = true });
                AttachDc(link, dc);
            }
            return link;
        }

        void StartLink(string peerId)
        {
            if (!_active || _links.ContainsKey(peerId)) return;
            Debug.Log($"{LogTag} connecting to {peerId}");
            Link link;
            try { link = CreateLink(peerId, true, null); }
            catch (Exception e)
            {
                Debug.LogError($"{LogTag} create link {peerId}: {e}");
                if (peerId == _hostId && !IsHost) EndSession("Couldn't start voice/peer connection");
                return;
            }
            StartCoroutine(OfferRoutine(link, _epoch));
        }

        IEnumerator OfferRoutine(Link link, int epoch)
        {
            var op = link.Pc.CreateOffer();
            yield return op;
            if (!IsCurrent(link, epoch)) yield break;
            if (op.IsError) { LinkError(link, "createOffer", op.Error.message); yield break; }

            var desc = op.Desc;
            var setOp = link.Pc.SetLocalDescription(ref desc);
            yield return setOp;
            if (!IsCurrent(link, epoch)) yield break;
            if (setOp.IsError) { LinkError(link, "setLocalDescription(offer)", setOp.Error.message); yield break; }

            SendSignal("OFFER", link.Id, new JObject
            {
                ["sdp"] = new JObject { ["type"] = "offer", ["sdp"] = desc.sdp },
                ["type"] = "media",
                ["connectionId"] = link.ConnectionId,
                ["browser"] = "unity",
                ["metadata"] = new JObject { ["app"] = "mpvr", ["room"] = RoomCode, ["name"] = _displayName },
            });
        }

        void HandleOffer(string src, JObject payload)
        {
            if (string.IsNullOrEmpty(src) || payload == null) return;
            string sdp = (string)payload["sdp"]?["sdp"];
            if (string.IsNullOrEmpty(sdp)) return;
            string app = (string)payload["metadata"]?["app"];
            if (app != null && app != "mpvr") { Debug.LogWarning($"{LogTag} ignoring offer from non-app peer {src}"); return; }
            if (!IsHost && src != _hostId && !src.StartsWith(GuestPrefix, StringComparison.Ordinal)) return;

            if (_links.TryGetValue(src, out var existing))
            {
                // Glare: both sides offered. Lower id is the designated offerer.
                if (existing.Initiator && string.CompareOrdinal(_myId, src) < 0 && !existing.AnswerReceived)
                {
                    Debug.Log($"{LogTag} glare with {src}: keeping our offer");
                    return;
                }
                Debug.Log($"{LogTag} replacing link with {src} (new offer)");
                _links.Remove(src);
                DisposeLink(existing);
            }

            Link link;
            try { link = CreateLink(src, false, (string)payload["connectionId"]); }
            catch (Exception e) { Debug.LogError($"{LogTag} create link {src}: {e}"); return; }
            Debug.Log($"{LogTag} offer from {src}");
            StartCoroutine(AnswerRoutine(link, sdp, _epoch));
        }

        IEnumerator AnswerRoutine(Link link, string offerSdp, int epoch)
        {
            var remote = new RTCSessionDescription { type = RTCSdpType.Offer, sdp = offerSdp };
            var setRemote = link.Pc.SetRemoteDescription(ref remote);
            yield return setRemote;
            if (!IsCurrent(link, epoch)) yield break;
            if (setRemote.IsError) { LinkError(link, "setRemoteDescription(offer)", setRemote.Error.message); yield break; }
            link.RemoteDescSet = true;
            FlushIce(link);

            var op = link.Pc.CreateAnswer();
            yield return op;
            if (!IsCurrent(link, epoch)) yield break;
            if (op.IsError) { LinkError(link, "createAnswer", op.Error.message); yield break; }

            var desc = op.Desc;
            var setLocal = link.Pc.SetLocalDescription(ref desc);
            yield return setLocal;
            if (!IsCurrent(link, epoch)) yield break;
            if (setLocal.IsError) { LinkError(link, "setLocalDescription(answer)", setLocal.Error.message); yield break; }

            SendSignal("ANSWER", link.Id, new JObject
            {
                ["sdp"] = new JObject { ["type"] = "answer", ["sdp"] = desc.sdp },
                ["type"] = "media",
                ["connectionId"] = link.ConnectionId,
                ["browser"] = "unity",
            });
        }

        void HandleAnswer(string src, JObject payload)
        {
            if (string.IsNullOrEmpty(src) || payload == null) return;
            if (!_links.TryGetValue(src, out var link) || !link.Initiator || link.AnswerReceived) return;
            string sdp = (string)payload["sdp"]?["sdp"];
            if (string.IsNullOrEmpty(sdp)) return;
            StartCoroutine(ApplyAnswerRoutine(link, sdp, _epoch));
        }

        IEnumerator ApplyAnswerRoutine(Link link, string sdp, int epoch)
        {
            link.AnswerReceived = true; // block duplicate answers while applying
            var remote = new RTCSessionDescription { type = RTCSdpType.Answer, sdp = sdp };
            var op = link.Pc.SetRemoteDescription(ref remote);
            yield return op;
            if (!IsCurrent(link, epoch)) yield break;
            if (op.IsError) { LinkError(link, "setRemoteDescription(answer)", op.Error.message); yield break; }
            link.RemoteDescSet = true;
            FlushIce(link);
        }

        void HandleCandidate(string src, JObject payload)
        {
            if (string.IsNullOrEmpty(src) || payload == null) return;
            if (!_links.TryGetValue(src, out var link)) return;
            var c = payload["candidate"] as JObject;
            string cand = (string)c?["candidate"];
            if (string.IsNullOrEmpty(cand)) return;
            var init = new RTCIceCandidateInit
            {
                candidate = cand,
                sdpMid = (string)c["sdpMid"],
                sdpMLineIndex = c["sdpMLineIndex"]?.Type == JTokenType.Integer ? (int?)(int)c["sdpMLineIndex"] : null,
            };
            if (link.RemoteDescSet)
                AddIce(link, init);
            else
                link.PendingIce.Add(init);
        }

        void FlushIce(Link link)
        {
            if (link.PendingIce.Count == 0) return;
            var pending = new List<RTCIceCandidateInit>(link.PendingIce);
            link.PendingIce.Clear();
            foreach (var init in pending) AddIce(link, init);
        }

        void AddIce(Link link, RTCIceCandidateInit init)
        {
            try
            {
                var candidate = new RTCIceCandidate(init);
                if (!link.Pc.AddIceCandidate(candidate))
                    Debug.LogWarning($"{LogTag} AddIceCandidate rejected for {link.Id}");
            }
            catch (Exception e) { Debug.LogWarning($"{LogTag} bad ICE candidate from {link.Id}: {e.Message}"); }
        }

        void OnPcState(Link link, RTCPeerConnectionState state)
        {
            if (!IsCurrent(link)) return;
            Debug.Log($"{LogTag} {link.Id} connection {state}");
            switch (state)
            {
                case RTCPeerConnectionState.Connected:
                    link.DisconnectedSince = -1f;
                    break;
                case RTCPeerConnectionState.Disconnected:
                    if (link.DisconnectedSince < 0f) link.DisconnectedSince = Time.unscaledTime;
                    break;
                case RTCPeerConnectionState.Failed:
                    if (!IsHost && link.Id == _hostId && !_joined)
                        EndSession("Couldn't connect to the host (network blocks peer-to-peer; no TURN relay configured).");
                    else
                        DropLink(link.Id, "connection failed");
                    break;
            }
        }

        void LinkError(Link link, string what, string error)
        {
            Debug.LogError($"{LogTag} {what} failed for {link.Id}: {error}");
            if (!IsHost && link.Id == _hostId && !_joined) EndSession($"Connection to room failed ({what})");
            else DropLink(link.Id, what + " failed");
        }

        bool IsCurrent(Link link) => _active && link != null && _links.TryGetValue(link.Id, out var l) && l == link;
        bool IsCurrent(Link link, int epoch) => epoch == _epoch && IsCurrent(link);

        void DropLink(string id, string reason)
        {
            if (!_links.TryGetValue(id, out var link)) return;
            Debug.Log($"{LogTag} dropping {id}: {reason}");
            _links.Remove(id);
            DisposeLink(link);

            if (!_active) return;
            if (!IsHost && id == _hostId)
            {
                EndSession(_joined ? "The host left the room" : $"Couldn't connect to room {RoomCode}");
                return;
            }
            if (IsHost)
            {
                if (_members.Remove(id))
                {
                    BroadcastRoster();
                    RebuildPeerNames();
                    RefreshSeats();
                }
                UpdateRoomStatus();
            }
            // Guest-guest link lost: member stays in roster (host is authoritative); voice from them stops.
        }

        void DisposeLink(Link link)
        {
            try { if (link.Voice != null) link.Voice.Stop(); } catch (Exception) { }
            try { if (link.VoiceGo != null) Destroy(link.VoiceGo); } catch (Exception) { }
            link.Voice = null;
            link.VoiceGo = null;
            try { link.Dc?.Close(); } catch (Exception) { }
            try { link.Pc?.Close(); } catch (Exception) { }
            try { link.Dc?.Dispose(); } catch (Exception) { }
            try { link.Pc?.Dispose(); } catch (Exception) { }
            link.Dc = null;
            link.Pc = null;
            link.RemoteTrack = null;
            link.Open = false;
        }

        // ── Data channel ──────────────────────────────────────────────────────
        void AttachDc(Link link, RTCDataChannel dc)
        {
            link.Dc = dc;
            dc.OnOpen = () => { if (IsCurrent(link)) OnLinkOpen(link); };
            dc.OnClose = () => { if (IsCurrent(link)) DropLink(link.Id, "data channel closed"); };
            dc.OnMessage = bytes =>
            {
                if (!IsCurrent(link)) return;
                try { HandleData(link, Encoding.UTF8.GetString(bytes)); }
                catch (Exception e) { Debug.LogError($"{LogTag} data from {link.Id}: {e}"); }
            };
            try { if (dc.ReadyState == RTCDataChannelState.Open) OnLinkOpen(link); }
            catch (Exception) { }
        }

        void OnLinkOpen(Link link)
        {
            if (link.Open) return;
            link.Open = true;
            link.NextPingAt = Time.unscaledTime + 0.2f;
            Debug.Log($"{LogTag} data channel open with {link.Id}");
            SendTo(link, new JObject { ["t"] = "hello", ["name"] = _displayName });
            if (!IsHost && link.Id == _hostId)
            {
                _joined = true;
                Status($"Joined room {RoomCode}…");
            }
        }

        void SendTo(Link link, JObject msg)
        {
            if (link == null || !link.Open || link.Dc == null) return;
            try
            {
                if (link.Dc.ReadyState != RTCDataChannelState.Open) return;
                link.Dc.Send(msg.ToString(Newtonsoft.Json.Formatting.None));
            }
            catch (Exception e) { Debug.LogWarning($"{LogTag} send to {link.Id}: {e.Message}"); }
        }

        void SendToHost(JObject msg)
        {
            if (_links.TryGetValue(_hostId, out var host)) SendTo(host, msg);
        }

        void Broadcast(JObject msg, string exceptId)
        {
            foreach (var link in _links.Values)
            {
                if (link.Id == exceptId) continue;
                if (IsHost && !_members.ContainsKey(link.Id)) continue; // not seated yet / room full
                SendTo(link, msg);
            }
        }

        void HandleData(Link link, string text)
        {
            var msg = JObject.Parse(text);
            string t = (string)msg["t"];
            switch (t)
            {
                case "hello":     OnHello(link, (string)msg["name"]); break;
                case "roster":    if (!IsHost && link.Id == _hostId) OnRoster(msg["members"] as JArray); break;
                case "playback":  OnPlaybackMsg(link, msg); break;
                case "movie":     OnMovieMsg(link, (string)msg["file"]); break;
                case "ping":
                    SendTo(link, new JObject { ["t"] = "pong", ["t0"] = msg["t0"], ["t1"] = UnixMs() });
                    break;
                case "pong":      OnPong(link, msg); break;
                case "full":
                    if (!IsHost && link.Id == _hostId) EndSession($"Room {RoomCode} is full");
                    break;
                case "bye":       DropLink(link.Id, "said bye"); break;
                default:          Debug.Log($"{LogTag} unknown data message '{t}' from {link.Id}"); break;
            }
        }

        void OnHello(Link link, string name)
        {
            link.Name = string.IsNullOrWhiteSpace(name) ? "Friend" : name.Trim();
            if (link.Name.Length > 32) link.Name = link.Name.Substring(0, 32);
            Debug.Log($"{LogTag} hello from {link.Id} ({link.Name})");

            if (IsHost)
            {
                if (!_members.TryGetValue(link.Id, out var m))
                {
                    int slot = FreeSlot();
                    if (slot < 0)
                    {
                        Debug.LogWarning($"{LogTag} room full, rejecting {link.Id}");
                        SendTo(link, new JObject { ["t"] = "full" });
                        return;
                    }
                    m = new Member { Id = link.Id, Slot = slot };
                    _members[link.Id] = m;
                }
                m.Name = link.Name;
                BroadcastRoster();
                RebuildPeerNames();
                RefreshSeats();
                UpdateRoomStatus();

                if (!string.IsNullOrEmpty(_currentMovie))
                    SendTo(link, new JObject { ["t"] = "movie", ["file"] = _currentMovie });
                if (_hasPlayback)
                {
                    long pos = _pbPositionMs + (_pbPlaying ? (long)((Time.unscaledTime - _pbAt) * 1000f) : 0);
                    SendTo(link, PlaybackMsg(_pbPlaying, pos, UnixMs()));
                }
            }
            else if (_members.TryGetValue(link.Id, out var m) && string.IsNullOrEmpty(m.Name))
            {
                m.Name = link.Name;
                RebuildPeerNames();
                RefreshSeats();
            }
        }

        int MaxSlots()
        {
            var theater = GetTheater();
            return theater != null && theater.SeatCount > 1 ? theater.SeatCount - 1 : 8;
        }

        int FreeSlot()
        {
            int max = MaxSlots();
            for (int s = 1; s <= max; s++)
            {
                bool used = false;
                foreach (var m in _members.Values) if (m.Slot == s) { used = true; break; }
                if (!used) return s;
            }
            return -1;
        }

        void BroadcastRoster()
        {
            var arr = new JArray { new JObject { ["id"] = _myId, ["name"] = _displayName, ["slot"] = 0 } };
            foreach (var m in _members.Values)
                arr.Add(new JObject { ["id"] = m.Id, ["name"] = m.Name ?? "Friend", ["slot"] = m.Slot });
            Broadcast(new JObject { ["t"] = "roster", ["members"] = arr }, null);
        }

        void OnRoster(JArray members)
        {
            if (members == null) return;
            var seen = new HashSet<string>();
            _members.Clear();
            foreach (var tok in members)
            {
                if (!(tok is JObject o)) continue;
                string id = (string)o["id"];
                if (string.IsNullOrEmpty(id)) continue;
                int slot = o["slot"]?.Type == JTokenType.Integer ? (int)o["slot"] : -1;
                if (id == _myId) { _mySlot = slot; continue; }
                _members[id] = new Member { Id = id, Name = (string)o["name"], Slot = slot };
                seen.Add(id);
            }

            // Mesh: guests connect to each other; lower id offers.
            foreach (var id in seen)
            {
                if (id == _hostId || _links.ContainsKey(id)) continue;
                if (_signalOpen && string.CompareOrdinal(_myId, id) < 0) StartLink(id);
            }
            foreach (var id in new List<string>(_links.Keys))
                if (id != _hostId && !seen.Contains(id)) DropLink(id, "left room");

            RebuildPeerNames();
            RefreshSeats();
            UpdateRoomStatus();
        }

        void OnPong(Link link, JObject msg)
        {
            long t3 = UnixMs();
            long t0 = msg["t0"]?.Type == JTokenType.Integer ? (long)msg["t0"] : 0;
            long t1 = msg["t1"]?.Type == JTokenType.Integer ? (long)msg["t1"] : 0;
            if (t0 <= 0 || t1 <= 0) return;
            double rtt = t3 - t0;
            if (rtt < 0 || rtt > 10000) return;
            double offset = t1 - (t0 + t3) / 2.0;
            // Prefer low-RTT samples; slowly forget the best so the estimate can track changes.
            link.BestRttMs = Math.Min(link.BestRttMs * 1.05, double.MaxValue);
            if (!link.HasOffset || rtt <= link.BestRttMs * 1.5)
            {
                link.ClockOffsetMs = link.HasOffset ? link.ClockOffsetMs * 0.7 + offset * 0.3 : offset;
                link.HasOffset = true;
            }
            if (rtt < link.BestRttMs) link.BestRttMs = rtt;
        }

        // ── Playback / movie sync ─────────────────────────────────────────────
        JObject PlaybackMsg(bool playing, long positionMs, long sentAt) => new JObject
        {
            ["t"] = "playback",
            ["playing"] = playing,
            ["positionMs"] = Math.Max(0, positionMs),
            ["sentAtUnixMs"] = sentAt,
            ["movieFile"] = _currentMovie ?? "",
        };

        void OnPlaybackMsg(Link link, JObject msg)
        {
            if (!IsHost && link.Id != _hostId) return;          // guests only follow the host
            if (IsHost && !_members.ContainsKey(link.Id)) return;

            bool playing = (bool?)msg["playing"] ?? false;
            long pos = msg["positionMs"]?.Type == JTokenType.Integer ? (long)msg["positionMs"] : 0;
            long sentAt = msg["sentAtUnixMs"]?.Type == JTokenType.Integer ? (long)msg["sentAtUnixMs"] : 0;
            string movie = (string)msg["movieFile"];

            long now = UnixMs();
            if (playing && sentAt > 0)
            {
                double elapsed = now - sentAt + (link.HasOffset ? link.ClockOffsetMs : 0);
                double max = link.HasOffset ? 5000 : 2000;
                if (elapsed < 0) elapsed = 0;
                if (elapsed > max) elapsed = max;
                pos += (long)elapsed;
            }

            if (!string.IsNullOrEmpty(movie) && movie != _currentMovie)
            {
                _currentMovie = movie;
                _lastRemoteMovieAt = Time.unscaledTime;
                SafeInvoke(OnRemoteMovie, movie);
                if (IsHost) Broadcast(new JObject { ["t"] = "movie", ["file"] = movie }, link.Id);
            }

            RememberPlayback(playing, pos);
            _remotePbPlaying = playing;
            _remotePbPositionMs = pos;
            _remotePbAtMs = now;

            if (IsHost) Broadcast(PlaybackMsg(playing, pos, now), link.Id);

            try { OnRemotePlayback?.Invoke(playing, pos); }
            catch (Exception e) { Debug.LogError($"{LogTag} OnRemotePlayback handler: {e}"); }
        }

        void OnMovieMsg(Link link, string file)
        {
            if (string.IsNullOrEmpty(file)) return;
            if (!IsHost && link.Id != _hostId) return;
            if (IsHost && !_members.ContainsKey(link.Id)) return;
            if (file == _currentMovie) return; // already playing it (breaks echo loops)
            _currentMovie = file;
            _hasPlayback = false;
            _lastRemoteMovieAt = Time.unscaledTime;
            if (IsHost) Broadcast(new JObject { ["t"] = "movie", ["file"] = file }, link.Id);
            SafeInvoke(OnRemoteMovie, file);
        }

        void RememberPlayback(bool playing, long positionMs)
        {
            _hasPlayback = true;
            _pbPlaying = playing;
            _pbPositionMs = positionMs;
            _pbAt = Time.unscaledTime;
        }

        // A local player that just applied a remote state usually reports it straight back.
        bool IsEcho(bool playing, long positionMs, long nowMs)
        {
            if (_remotePbAtMs == long.MinValue) return false;
            long age = nowMs - _remotePbAtMs;
            if (age < 0 || age > 1500) return false;
            if (playing != _remotePbPlaying) return false;
            long expected = _remotePbPositionMs + (playing ? age : 0);
            return Math.Abs(positionMs - expected) < 1500;
        }

        // ── Seats ─────────────────────────────────────────────────────────────
        TheaterEnvironment GetTheater()
        {
            if (Theater == null)
            {
                try { Theater = FindAnyObjectByType<TheaterEnvironment>(FindObjectsInactive.Include); }
                catch (Exception) { }
            }
            return Theater;
        }

        int SeatFor(Member m)
        {
            if (m.Slot < 0) return -1;
            // Everyone sees themselves in seat 0, so the host sits where I would be.
            if (m.Slot == 0) return _mySlot > 0 ? _mySlot : -1;
            return m.Slot;
        }

        void RefreshSeats()
        {
            var theater = GetTheater();
            var wanted = new HashSet<string>();
            foreach (var m in _members.Values)
            {
                int seat = SeatFor(m);
                if (seat <= 0) continue;
                wanted.Add(m.Id);
                if (theater != null)
                {
                    try
                    {
                        if (seat < theater.SeatCount)
                        {
                            theater.SetMemberSeated(m.Id, string.IsNullOrEmpty(m.Name) ? "Friend" : m.Name, seat);
                            _seated.Add(m.Id);
                        }
                    }
                    catch (Exception e) { Debug.LogWarning($"{LogTag} seat {m.Id}: {e.Message}"); }
                }
                if (_links.TryGetValue(m.Id, out var link)) PlaceVoice(link, seat);
            }
            foreach (var id in new List<string>(_seated))
            {
                if (wanted.Contains(id)) continue;
                _seated.Remove(id);
                try { theater?.RemoveMember(id); }
                catch (Exception e) { Debug.LogWarning($"{LogTag} unseat {id}: {e.Message}"); }
            }
        }

        void ClearSeats()
        {
            var theater = GetTheater();
            foreach (var id in _seated)
            {
                try { theater?.RemoveMember(id); } catch (Exception) { }
            }
            _seated.Clear();
        }

        void PlaceVoice(Link link, int seat)
        {
            if (link.VoiceGo == null) return;
            var theater = GetTheater();
            if (theater != null && seat > 0) link.VoiceGo.transform.position = theater.GetSeatPosition(seat);
        }

        void RebuildPeerNames()
        {
            _peerNames.Clear();
            var list = new List<Member>(_members.Values);
            list.Sort((a, b) => a.Slot.CompareTo(b.Slot));
            foreach (var m in list) _peerNames.Add(string.IsNullOrEmpty(m.Name) ? "Friend" : m.Name);
        }

        void UpdateRoomStatus()
        {
            if (!_active) return;
            if (!IsHost && !_joined) return;
            int people = 1 + _members.Count;
            if (people <= 1)
                Status($"In room {RoomCode} — waiting for friends (share the code)");
            else
                Status($"In room {RoomCode} — {people} people");
        }

        // ── Voice ─────────────────────────────────────────────────────────────
        void SetupMicTrack()
        {
            TeardownMic();
            try
            {
                _micGo = new GameObject("PeerRoom Mic");
                _micGo.transform.SetParent(transform, false);
                _micSource = _micGo.AddComponent<AudioSource>();
                _micSource.playOnAwake = false;
                _micSource.loop = true;
                _micSource.spatialBlend = 0f;
                _micSource.volume = 1f;
                _micTrack = new AudioStreamTrack(_micSource) { Loopback = false };
                _micTrack.Enabled = !_muted;
                _micStream = new MediaStream();
            }
            catch (Exception e)
            {
                Debug.LogError($"{LogTag} mic track setup failed (voice send disabled): {e}");
                _micTrack = null;
            }
        }

        IEnumerator StartMicRoutine(int epoch)
        {
#if UNITY_ANDROID
            if (Application.platform == RuntimePlatform.Android &&
                !Permission.HasUserAuthorizedPermission(Permission.Microphone))
            {
                bool answered = false;
                var callbacks = new PermissionCallbacks();
                callbacks.PermissionGranted += _ => answered = true;
                callbacks.PermissionDenied += _ => answered = true;
                Permission.RequestUserPermission(Permission.Microphone, callbacks);
                float until = Time.realtimeSinceStartup + 30f;
                while (!answered && Time.realtimeSinceStartup < until) yield return null;
                if (epoch != _epoch) yield break;
                if (!Permission.HasUserAuthorizedPermission(Permission.Microphone))
                {
                    Debug.LogWarning($"{LogTag} microphone permission denied");
                    Status("Microphone permission denied — you can listen but not talk");
                    yield break;
                }
            }
#endif
            if (epoch != _epoch || _micSource == null) yield break;

            string[] devices;
            try { devices = Microphone.devices; }
            catch (Exception e) { Debug.LogWarning($"{LogTag} Microphone.devices: {e.Message}"); yield break; }
            if (devices == null || devices.Length == 0)
            {
                Debug.LogWarning($"{LogTag} no microphone found");
                yield break;
            }

            _micDevice = null; // default device
            try { _micClip = Microphone.Start(_micDevice, true, 1, MicSampleRate); }
            catch (Exception e) { Debug.LogError($"{LogTag} Microphone.Start: {e.Message}"); yield break; }
            if (_micClip == null) { Debug.LogError($"{LogTag} Microphone.Start returned null"); yield break; }

            float deadline = Time.realtimeSinceStartup + 3f;
            while (Microphone.GetPosition(_micDevice) <= 0 && Time.realtimeSinceStartup < deadline)
                yield return null;
            if (epoch != _epoch || _micSource == null) yield break;

            _micSource.clip = _micClip;
            _micSource.loop = true;
            _micSource.Play();
            Debug.Log($"{LogTag} microphone started ({_micClip.frequency} Hz)");
        }

        // The mic clip is a 1 s ring buffer; keep the reading head ~60 ms behind the writing head.
        void KeepMicInSync()
        {
            if (_micSource == null || _micClip == null || !_micSource.isPlaying) return;
            int len = _micClip.samples;
            if (len <= 0) return;
            int write = Microphone.GetPosition(_micDevice);
            int read = _micSource.timeSamples;
            int lag = (write - read + len) % len;
            int freq = _micClip.frequency;
            if (lag > freq * 3 / 10 || lag < freq / 100)
                _micSource.timeSamples = (write - freq * 6 / 100 + len) % len;
        }

        void TeardownMic()
        {
            try { if (Microphone.IsRecording(_micDevice)) Microphone.End(_micDevice); } catch (Exception) { }
            try { _micTrack?.Dispose(); } catch (Exception) { }
            try { _micStream?.Dispose(); } catch (Exception) { }
            try { if (_micGo != null) Destroy(_micGo); } catch (Exception) { }
            _micTrack = null;
            _micStream = null;
            _micGo = null;
            _micSource = null;
            if (_micClip != null) { try { Destroy(_micClip); } catch (Exception) { } }
            _micClip = null;
        }

        void OnRemoteTrack(Link link, RTCTrackEvent e)
        {
            if (!IsCurrent(link)) return;
            try
            {
                if (!(e.Track is AudioStreamTrack audio)) return;
                link.RemoteTrack = audio;
                if (link.VoiceGo == null)
                {
                    link.VoiceGo = new GameObject($"PeerRoom Voice {link.Id}");
                    link.VoiceGo.transform.SetParent(transform, false);
                    link.Voice = link.VoiceGo.AddComponent<AudioSource>();
                }
                var src = link.Voice;
                src.playOnAwake = false;
                src.spatialBlend = VoiceSpatialBlend;
                src.minDistance = 2f;
                src.maxDistance = 30f;
                src.rolloffMode = AudioRolloffMode.Linear;
                src.dopplerLevel = 0f;
                src.volume = VoiceVolume;
                src.SetTrack(audio);
                src.loop = true;
                src.Play();
                if (_members.TryGetValue(link.Id, out var m)) PlaceVoice(link, SeatFor(m));
                Debug.Log($"{LogTag} receiving voice from {link.Id}");
            }
            catch (Exception ex) { Debug.LogError($"{LogTag} remote track: {ex}"); }
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        void Status(string s)
        {
            Debug.Log($"{LogTag} status: {s}");
            SafeInvoke(OnStatus, s);
        }

        static void SafeInvoke(Action<string> evt, string arg)
        {
            if (evt == null) return;
            try { evt(arg); }
            catch (Exception e) { Debug.LogError($"{LogTag} event handler: {e}"); }
        }

        static long UnixMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        static bool IsDigits(string s)
        {
            foreach (char c in s) if (c < '0' || c > '9') return false;
            return true;
        }

        string NewCode() => _rng.Next(100000, 1000000).ToString();

        string RandomAlnum(int n)
        {
            const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
            var sb = new StringBuilder(n);
            for (int i = 0; i < n; i++) sb.Append(chars[_rng.Next(chars.Length)]);
            return sb.ToString();
        }
    }
}
