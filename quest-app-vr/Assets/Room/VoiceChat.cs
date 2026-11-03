// Unity.WebRTC (com.unity.webrtc) is deprecated for Unity 6000.x.
// All WebRTC-specific code is guarded with #if UNITY_WEBRTC_AVAILABLE.
// To enable real voice chat, install a compatible WebRTC package and
// add UNITY_WEBRTC_AVAILABLE to Project Settings → Scripting Define Symbols.
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;
using MoviePlayer.Proto;

#if UNITY_WEBRTC_AVAILABLE
using Unity.WebRTC;
#endif

namespace MoviePlayer.Room
{
    /// <summary>
    /// WebRTC mesh voice chat for ≤4 participants.
    /// Voice chat is disabled when Unity.WebRTC is unavailable (Unity 6+).
    /// </summary>
    [RequireComponent(typeof(RoomClient))]
    public class VoiceChat : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────────
        [Header("References")]
        public Player.HlsPlayerController MoviePlayer;
        public AudioMixer MasterMixer;
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
        private readonly List<string> _remotePeerIds = new List<string>();

        // Local mic (used for VAD even without WebRTC)
        private AudioClip   _micClip;
        private AudioSource _micSource;
        private string      _micDevice;
        private float[]     _vadBuffer = new float[1024];

        // Remote audio / ducking
        private float _remoteRmsPeak = 0f;
        private bool  _isDucked      = false;
        private const float DuckDecay = 2f;

#if UNITY_WEBRTC_AVAILABLE
        private AudioStreamTrack _localTrack;
        private readonly Dictionary<string, RTCPeerConnection> _peers
            = new Dictionary<string, RTCPeerConnection>();
#endif

        // ─────────────────────────────────────────────────────────────────────
        #region Unity Lifecycle

        private void Awake()
        {
            _roomClient = GetComponent<RoomClient>();
        }

        private void OnEnable()
        {
            if (_roomClient == null) return;
            _roomClient.OnJoined    += HandleJoined;
            _roomClient.OnPeerLeft  += HandlePeerLeft;
#if UNITY_WEBRTC_AVAILABLE
            _roomClient.OnRtcOffer  += HandleRtcOffer;
            _roomClient.OnRtcAnswer += HandleRtcAnswer;
            _roomClient.OnRtcIce    += HandleRtcIce;
#endif
        }

        private void OnDisable()
        {
            if (_roomClient != null)
            {
                _roomClient.OnJoined    -= HandleJoined;
                _roomClient.OnPeerLeft  -= HandlePeerLeft;
#if UNITY_WEBRTC_AVAILABLE
                _roomClient.OnRtcOffer  -= HandleRtcOffer;
                _roomClient.OnRtcAnswer -= HandleRtcAnswer;
                _roomClient.OnRtcIce    -= HandleRtcIce;
#endif
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

#if UNITY_WEBRTC_AVAILABLE
            _micSource = gameObject.GetComponent<AudioSource>() ?? gameObject.AddComponent<AudioSource>();
            _micSource.clip = _micClip;
            _micSource.loop = true;
            _micSource.mute = true;
            _micSource.Play();
            _localTrack = new AudioStreamTrack(_micSource);
#endif

            Debug.Log($"[VoiceChat] Microphone started: {_micDevice}");
        }

        private void StopMicrophone()
        {
            if (!string.IsNullOrEmpty(_micDevice))
                Microphone.End(_micDevice);

#if UNITY_WEBRTC_AVAILABLE
            _localTrack?.Dispose();
            _localTrack = null;
            if (_micSource != null) { _micSource.Stop(); _micSource.clip = null; }
            _micSource = null;
#endif
            _micClip   = null;
            _micDevice = null;
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
            #if ENABLE_LEGACY_INPUT_MANAGER
            bool pttHeld = Input.GetKey(PttKey);
#else
            bool pttHeld = false; // legacy Input disabled (Input System package only); XR trigger below still works
#endif

#if UNITY_ANDROID && !UNITY_EDITOR
            // Poll XR controller triggers via Unity XR InputDevices (no Meta SDK needed).
            var handedDevices = new System.Collections.Generic.List<UnityEngine.XR.InputDevice>();
            UnityEngine.XR.InputDevices.GetDevicesWithRole(UnityEngine.XR.InputDeviceRole.LeftHanded, handedDevices);
            foreach (var dev in handedDevices)
            {
                if (dev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.triggerButton, out bool t) && t)
                    pttHeld = true;
            }
            handedDevices.Clear();
            UnityEngine.XR.InputDevices.GetDevicesWithRole(UnityEngine.XR.InputDeviceRole.RightHanded, handedDevices);
            foreach (var dev in handedDevices)
            {
                if (dev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.triggerButton, out bool t) && t)
                    pttHeld = true;
            }
#endif

            bool speaking;
            if (PushToTalkMode)
                speaking = pttHeld;
            else
                speaking = GetMicRms() > VadThresholdRms;

            IsSpeaking = speaking;
            IsMuted    = !speaking;

#if UNITY_WEBRTC_AVAILABLE
            if (_localTrack != null)
                _localTrack.Enabled = speaking;
#endif
        }

        private void UpdateDucking()
        {
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

            if (state.turn != null)
            {
                TurnUrl        = state.turn.url;
                TurnUsername   = state.turn.username;
                TurnCredential = state.turn.credential;
            }

            StartMicrophone();

#if UNITY_WEBRTC_AVAILABLE
            if (state.seats != null)
            {
                foreach (var seat in state.seats)
                {
                    if (seat.device_id == _localDeviceId) continue;
                    if (seat.connected)
                        StartCoroutine(CreateOfferForPeer(seat.device_id));
                }
            }
#else
            Debug.LogWarning("[VoiceChat] WebRTC unavailable — voice chat disabled.");
#endif
        }

        private void HandlePeerLeft(string deviceId)
        {
#if UNITY_WEBRTC_AVAILABLE
            if (_peers.TryGetValue(deviceId, out var pc))
            {
                pc.Close();
                pc.Dispose();
                _peers.Remove(deviceId);
            }
#endif
            _remotePeerIds.Remove(deviceId);
            Debug.Log($"[VoiceChat] Peer left: {deviceId}");
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region WebRTC (conditional)

#if UNITY_WEBRTC_AVAILABLE
        private void HandleRtcOffer(RtcOfferMsg msg)
        {
            if (msg.to_device_id != _localDeviceId) return;
            StartCoroutine(HandleOfferCoroutine(msg));
        }

        private void HandleRtcAnswer(RtcAnswerMsg msg)
        {
            if (msg.to_device_id != _localDeviceId) return;
            if (!_peers.TryGetValue(msg.from_device_id, out var pc)) return;
            var desc = new RTCSessionDescription { type = RTCSdpType.Answer, sdp = msg.sdp };
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

            if (_localTrack != null)
                pc.AddTrack(_localTrack);

            pc.OnTrack += evt =>
            {
                if (evt.Track is AudioStreamTrack audioTrack)
                    AttachRemoteAudio(remotePeerId, audioTrack);
            };

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

            var offerDesc   = offerOp.Desc;
            var localDescOp = pc.SetLocalDescription(ref offerDesc);
            yield return localDescOp;
            if (localDescOp.IsError) { Debug.LogError("[VoiceChat] SetLocalDesc error"); yield break; }

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
            if (answerOp.IsError) { Debug.LogError("[VoiceChat] CreateAnswer error"); yield break; }

            var answerDesc  = answerOp.Desc;
            var localDescOp = pc.SetLocalDescription(ref answerDesc);
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
            src.loop         = true;
            src.spatialBlend = 0f;
            src.Play();

            var reporter = go.AddComponent<RemoteAudioRmsReporter>();
            reporter.VoiceChat = this;

            Debug.Log($"[VoiceChat] Remote audio attached for peer {peerId}");
        }
#endif // UNITY_WEBRTC_AVAILABLE

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Tear-down

        private void TearDown()
        {
            StopMicrophone();
#if UNITY_WEBRTC_AVAILABLE
            foreach (var kv in _peers) { kv.Value.Close(); kv.Value.Dispose(); }
            _peers.Clear();
#endif
            _remotePeerIds.Clear();
        }

        #endregion
    }

    // ─────────────────────────────────────────────────────────────────────────
    /// <summary>Helper attached to each remote AudioSource for ducking RMS.</summary>
    internal class RemoteAudioRmsReporter : MonoBehaviour
    {
        public VoiceChat VoiceChat;
        private float[] _buf = new float[512];

        private void OnAudioFilterRead(float[] data, int channels)
        {
            float sum = 0f;
            foreach (float s in data) sum += s * s;
            VoiceChat?.ReportRemoteAudioRms(Mathf.Sqrt(sum / data.Length));
        }
    }
}
