using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;
using Unity.WebRTC;
using MoviePlayer.Proto;

namespace MoviePlayer.Room
{
    /// <summary>
    /// WebRTC mesh voice chat for ≤4 participants.
    ///
    /// Mic input
    /// ─────────
    /// A Unity Microphone.Start() AudioClip is fed into an AudioStreamTrack.
    /// Push-to-talk: Space / controller primary trigger gates the track.
    /// VAD gate: simple RMS threshold (±15ms window) when PTT is off.
    ///
    /// Movie ducking
    /// ─────────────
    /// When any remote peer is speaking above DuckThresholdRms the HLS player
    /// volume is reduced to DuckedVolume via HlsPlayerController.SetVolume().
    ///
    /// Signaling
    /// ─────────
    /// Offer/answer/ICE is sent over the existing RoomClient WebSocket as
    /// JSON messages of types rtc_offer / rtc_answer / rtc_ice.
    /// </summary>
    [RequireComponent(typeof(RoomClient))]
    public class VoiceChat : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────────
        [Header("References")]
        public Player.HlsPlayerController MoviePlayer;
        public AudioMixer MasterMixer;          // assign the mixer that controls movie audio
        [Tooltip("AudioMixer exposed parameter name for movie volume")]
        public string MovieVolumeParam = "MovieVolume";

        [Header("Push-to-talk")]
        public bool PushToTalkMode = true;
        [Tooltip("Keyboard fallback (Space). VR trigger is polled via OVRInput.")]
        public KeyCode PttKey = KeyCode.Space;

        [Header("VAD")]
        [Range(0f, 0.1f)]
        public float VadThresholdRms = 0.01f;

        [Header("Movie Ducking")]
        [Range(0f, 1f)]
        public float DuckedVolume = 0.4f;
        [Range(0f, 0.1f)]
        public float DuckThresholdRms = 0.02f;

        [Header("TURN")]
        public string TurnUrl;
        public string TurnUsername;
        public string TurnCredential;

        // ── Public State ──────────────────────────────────────────────────────
        public bool IsMuted    { get; private set; } = true;
        public bool IsSpeaking { get; private set; } = false;
        public IReadOnlyList<string> RemotePeers => _remotePeerIds;

        // ── Private ───────────────────────────────────────────────────────────
        private RoomClient _roomClient;
        private string     _localDeviceId = string.Empty;

        // One RTCPeerConnection per remote peer (mesh topology)
        private readonly Dictionary<string, RTCPeerConnection> _peers
            = new Dictionary<string, RTCPeerConnection>();
        private readonly List<string> _remotePeerIds = new List<string>();

        // Local mic
        private AudioStreamTrack _localTrack;
        private AudioClip        _micClip;
        private string           _micDevice;
        private float[]          _vadBuffer = new float[1024];

        // Remote audio analysis for ducking
        private float _remoteRmsPeak = 0f;
        private bool  _isDucked      = false;
        private const float DuckDecay = 2f; // seconds to unduck after silence

        // ─────────────────────────────────────────────────────────────────────
        #region Unity Lifecycle

        private void Awake()
        {
            _roomClient = GetComponent<RoomClient>();
        }

        private void OnEnable()
        {
            if (_roomClient == null) return;
            _roomClient.OnJoined     += HandleJoined;
            _roomClient.OnPeerLeft   += HandlePeerLeft;
            _roomClient.OnRtcOffer   += HandleRtcOffer;
            _roomClient.OnRtcAnswer  += HandleRtcAnswer;
            _roomClient.OnRtcIce     += HandleRtcIce;
        }

        private void OnDisable()
        {
            if (_roomClient != null)
            {
                _roomClient.OnJoined    -= HandleJoined;
                _roomClient.OnPeerLeft  -= HandlePeerLeft;
                _roomClient.OnRtcOffer  -= HandleRtcOffer;
                _roomClient.OnRtcAnswer -= HandleRtcAnswer;
                _roomClient.OnRtcIce    -= HandleRtcIce;
            }

            TearDown();
        }

        private void Update()
        {
            UpdatePTT();
            UpdateDucking();
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Microphone

        private void StartMicrophone()
        {
            if (Microphone.devices.Length == 0)
            {
                Debug.LogWarning("[VoiceChat] No microphone found.");
                return;
            }

            _micDevice = Microphone.devices[0];
            _micClip   = Microphone.Start(_micDevice, loop: true, lengthSec: 1, frequency: 48000);
            _localTrack = new AudioStreamTrack(_micClip);

            Debug.Log($"[VoiceChat] Microphone started: {_micDevice}");
        }

        private void StopMicrophone()
        {
            if (!string.IsNullOrEmpty(_micDevice))
                Microphone.End(_micDevice);

            _localTrack?.Dispose();
            _localTrack = null;
            _micClip    = null;
            _micDevice  = null;
        }

        private float GetMicRms()
        {
            if (_micClip == null) return 0f;
            int pos = Microphone.GetPosition(_micDevice);
            if (pos < _vadBuffer.Length) return 0f;

            _micClip.GetData(_vadBuffer, pos - _vadBuffer.Length);
            float sum = 0f;
            foreach (float s in _vadBuffer) sum += s * s;
            return Mathf.Sqrt(sum / _vadBuffer.Length);
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region PTT / VAD / Ducking

        private void UpdatePTT()
        {
            bool pttHeld = Input.GetKey(PttKey);

#if UNITY_ANDROID && !UNITY_EDITOR
            // OVRInput primary index trigger
            pttHeld = pttHeld
                || OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger)
                || OVRInput.Get(OVRInput.Button.SecondaryIndexTrigger);
#endif

            bool speaking;
            if (PushToTalkMode)
                speaking = pttHeld;
            else
                speaking = GetMicRms() > VadThresholdRms;

            IsSpeaking = speaking;
            IsMuted    = !speaking;

            if (_localTrack != null)
                _localTrack.Enabled = speaking;
        }

        private void UpdateDucking()
        {
            // Decay peak
            _remoteRmsPeak = Mathf.Max(0f, _remoteRmsPeak - Time.deltaTime * DuckDecay);

            bool shouldDuck = _remoteRmsPeak > DuckThresholdRms;

            if (shouldDuck == _isDucked) return;
            _isDucked = shouldDuck;

            if (MoviePlayer != null)
                MoviePlayer.SetVolume(shouldDuck ? DuckedVolume : 1f);

            if (MasterMixer != null)
            {
                float db = shouldDuck
                    ? 20f * Mathf.Log10(DuckedVolume + Mathf.Epsilon)
                    : 0f;
                MasterMixer.SetFloat(MovieVolumeParam, db);
            }

            Debug.Log($"[VoiceChat] Movie audio {(shouldDuck ? "ducked" : "restored")}.");
        }

        // Called by AudioSource.OnAudioFilterRead on each remote AudioSource
        internal void ReportRemoteAudioRms(float rms)
        {
            _remoteRmsPeak = Mathf.Max(_remoteRmsPeak, rms);
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Room Events

        private void HandleJoined(RoomState state)
        {
            _localDeviceId = state.device_id;

            // Store TURN creds if provided
            if (state.turn != null)
            {
                TurnUrl        = state.turn.url;
                TurnUsername   = state.turn.username;
                TurnCredential = state.turn.credential;
            }

            StartMicrophone();

            // Initiate offers to all existing peers (caller side)
            if (state.seats != null)
            {
                foreach (var seat in state.seats)
                {
                    if (seat.device_id == _localDeviceId) continue;
                    if (seat.connected)
                        StartCoroutine(CreateOfferForPeer(seat.device_id));
                }
            }
        }

        private void HandlePeerLeft(string deviceId)
        {
            if (_peers.TryGetValue(deviceId, out var pc))
            {
                pc.Close();
                pc.Dispose();
                _peers.Remove(deviceId);
                _remotePeerIds.Remove(deviceId);
                Debug.Log($"[VoiceChat] Peer left, connection closed: {deviceId}");
            }
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region WebRTC Signaling Handlers

        private void HandleRtcOffer(RtcOfferMsg msg)
        {
            if (msg.to_device_id != _localDeviceId) return;
            StartCoroutine(HandleOfferCoroutine(msg));
        }

        private void HandleRtcAnswer(RtcAnswerMsg msg)
        {
            if (msg.to_device_id != _localDeviceId) return;
            if (!_peers.TryGetValue(msg.from_device_id, out var pc)) return;

            var desc = new RTCSessionDescription
            {
                type = RTCSdpType.Answer,
                sdp  = msg.sdp
            };
            StartCoroutine(SetRemoteDescription(pc, desc));
        }

        private void HandleRtcIce(RtcIceMsg msg)
        {
            if (msg.to_device_id != _localDeviceId) return;
            if (!_peers.TryGetValue(msg.from_device_id, out var pc)) return;

            pc.AddIceCandidate(new RTCIceCandidate(new RTCIceCandidateInit
            {
                candidate     = msg.candidate,
                sdpMid        = msg.sdp_mid,
                sdpMLineIndex = msg.sdp_mline_index
            }));
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region RTCPeerConnection Helpers

        private RTCPeerConnection CreatePeerConnection(string remotePeerId)
        {
            var config = new RTCConfiguration
            {
                iceServers = new[]
                {
                    new RTCIceServer
                    {
                        urls       = new[] { TurnUrl ?? "stun:stun.l.google.com:19302" },
                        username   = TurnUsername,
                        credential = TurnCredential
                    }
                }
            };

            var pc = new RTCPeerConnection(ref config);
            _peers[remotePeerId] = pc;
            if (!_remotePeerIds.Contains(remotePeerId))
                _remotePeerIds.Add(remotePeerId);

            // Add local mic track
            if (_localTrack != null)
                pc.AddTrack(_localTrack);

            // Incoming remote track → attach to AudioSource
            pc.OnTrack += evt =>
            {
                if (evt.Track is AudioStreamTrack audioTrack)
                    AttachRemoteAudio(remotePeerId, audioTrack);
            };

            // ICE candidate → relay via WebSocket
            pc.OnIceCandidate += candidate =>
            {
                _roomClient.SendRtcIce(new RtcIceMsg
                {
                    from_device_id  = _localDeviceId,
                    to_device_id    = remotePeerId,
                    candidate       = candidate.Candidate,
                    sdp_mid         = candidate.SdpMid,
                    sdp_mline_index = candidate.SdpMLineIndex ?? 0
                });
            };

            pc.OnConnectionStateChange += state =>
                Debug.Log($"[VoiceChat] {remotePeerId} → {state}");

            return pc;
        }

        private IEnumerator CreateOfferForPeer(string remotePeerId)
        {
            var pc = CreatePeerConnection(remotePeerId);

            var offerOp = pc.CreateOffer();
            yield return offerOp;
            if (offerOp.IsError) { Debug.LogError($"[VoiceChat] CreateOffer error: {offerOp.Error.message}"); yield break; }

            var localDescOp = pc.SetLocalDescription(ref offerOp.Desc);
            yield return localDescOp;
            if (localDescOp.IsError) { Debug.LogError($"[VoiceChat] SetLocalDesc error"); yield break; }

            _roomClient.SendRtcOffer(new RtcOfferMsg
            {
                from_device_id = _localDeviceId,
                to_device_id   = remotePeerId,
                sdp            = offerOp.Desc.sdp,
                type           = "offer"
            });
        }

        private IEnumerator HandleOfferCoroutine(RtcOfferMsg msg)
        {
            string peerId = msg.from_device_id;
            var pc = CreatePeerConnection(peerId);

            var remoteDesc = new RTCSessionDescription { type = RTCSdpType.Offer, sdp = msg.sdp };
            yield return SetRemoteDescription(pc, remoteDesc);

            var answerOp = pc.CreateAnswer();
            yield return answerOp;
            if (answerOp.IsError) { Debug.LogError($"[VoiceChat] CreateAnswer error"); yield break; }

            var localDescOp = pc.SetLocalDescription(ref answerOp.Desc);
            yield return localDescOp;

            _roomClient.SendRtcAnswer(new RtcAnswerMsg
            {
                from_device_id = _localDeviceId,
                to_device_id   = peerId,
                sdp            = answerOp.Desc.sdp,
                type           = "answer"
            });
        }

        private IEnumerator SetRemoteDescription(RTCPeerConnection pc, RTCSessionDescription desc)
        {
            var op = pc.SetRemoteDescription(ref desc);
            yield return op;
            if (op.IsError)
                Debug.LogError($"[VoiceChat] SetRemoteDescription error: {op.Error.message}");
        }

        private void AttachRemoteAudio(string peerId, AudioStreamTrack track)
        {
            var go = new GameObject($"RemoteAudio_{peerId}");
            go.transform.SetParent(transform, false);

            var src = go.AddComponent<AudioSource>();
            src.SetTrack(track);
            src.loop    = true;
            src.spatialBlend = 0f; // 2D for voice
            src.Play();

            // Attach an RMS reporter for ducking
            var reporter = go.AddComponent<RemoteAudioRmsReporter>();
            reporter.VoiceChat = this;

            Debug.Log($"[VoiceChat] Remote audio attached for peer {peerId}");
        }

        private void TearDown()
        {
            StopMicrophone();
            foreach (var kv in _peers)
            {
                kv.Value.Close();
                kv.Value.Dispose();
            }
            _peers.Clear();
            _remotePeerIds.Clear();
        }

        #endregion
    }

    // ─────────────────────────────────────────────────────────────────────────
    /// <summary>
    /// Lightweight helper attached to each remote AudioSource.
    /// Reports RMS to VoiceChat for movie ducking.
    /// </summary>
    internal class RemoteAudioRmsReporter : MonoBehaviour
    {
        public VoiceChat VoiceChat;
        private float[] _buf = new float[512];

        private void OnAudioFilterRead(float[] data, int channels)
        {
            // RMS of this audio block
            float sum = 0f;
            foreach (float s in data) sum += s * s;
            float rms = Mathf.Sqrt(sum / data.Length);
            VoiceChat?.ReportRemoteAudioRms(rms);
        }
    }
}
