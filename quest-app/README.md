# MoviePlayer Quest App — Setup Guide

## Prerequisites

| Tool | Version |
|---|---|
| Unity | **6000.0.23f1** (Unity 6) |
| Meta XR SDK | ≥ 65 (via Unity Package Manager) |
| Android Build Support | installed via Unity Hub |
| Android NDK / JDK | bundled with Unity or standalone |
| Quest device | Meta Quest 3 (or Quest 2/Pro) |
| ADB | in PATH for sideloading |

---

## 1 — Open the project

1. Launch **Unity Hub**.
2. Click **Add** → **Add project from disk** → select `quest-app/`.
3. Unity will auto-import packages from `Packages/manifest.json`.  
   This takes 3–10 minutes on first open.

---

## 2 — Import Meta XR SDK

The Meta XR All-in-One SDK is not distributed via the standard Unity registry.

1. In Unity, open **Window → Package Manager**.
2. Click the **+** dropdown → **Add package by name**.
3. Enter: `com.meta.xr.sdk.all` (or install from the [Meta Developer site](https://developer.oculus.com/downloads/unity/)).
4. Alternatively, install individual packages:
   - `com.meta.xr.sdk.core`
   - `com.meta.xr.sdk.interaction`

---

## 3 — Configure OVRCameraRig

1. In the **Hierarchy**, create a new **XR Origin** via  
   **GameObject → XR → XR Origin (VR)** (or drag the `OVRCameraRig` prefab from Meta XR).
2. Attach the following scripts to the XR Origin root (or a child):
   - `StereoScreenRenderer`
   - `ClarityStack`
   - `HlsPlayerController`
3. Create a **Canvas** (World Space) for UI and attach `LobbyUI`, `AppController` references.

---

## 4 — Build Settings

1. **File → Build Settings**
2. Switch platform to **Android**.
3. In **Player Settings**:
   - **Company Name / Product Name**: fill in as desired.
   - **Minimum API Level**: API 29 (Android 10).
   - **Target API Level**: Automatic (highest installed).
   - **Scripting Backend**: IL2CPP.
   - **Target Architectures**: ARM64.
4. In **XR Plug-in Management** (Edit → Project Settings → XR Plug-in Management):
   - Android tab → check **OpenXR**.
   - Add **Meta Quest feature set** (or tick Meta Quest Support).
   - Check **Meta Quest 3** in the supported devices list.

---

## 5 — AppController Inspector

Select the `AppController` GameObject and set:

| Field | Value |
|---|---|
| `CloudWsUrl` | `wss://your-cloud-server/ws` |
| `CloudApiUrl` | `https://your-cloud-server` |

The **Home Node URL** is entered at first app launch (saved to PlayerPrefs).

---

## 6 — Clarity / RCAS Shader

The `ClarityShader.shader` is a URP shader.

1. Create a new **Material** in `Assets/Player/`.
2. Set its **Shader** to `MoviePlayer/ClarityShader`.
3. Assign this material to `ClarityStack.ClarityMaterial` and to the  
   `StereoScreenRenderer.ScreenMaterial` in the Inspector.

---

## 7 — NativeWebSocket

`NativeWebSocket` is pulled via git URL from `Packages/manifest.json`.  
If Unity cannot resolve it (firewall / no git):

1. Download the `.unitypackage` from https://github.com/endel/NativeWebSocket/releases
2. **Assets → Import Package → Custom Package** and import.

---

## 8 — Build and Sideload

```powershell
# Connect Quest via USB with developer mode ON
adb devices          # confirm device appears

# Build from Unity: File → Build Settings → Build
# Then sideload:
adb install -r "path\to\MoviePlayer.apk"
```

Or use **Build and Run** directly from Unity with the headset connected.

---

## 9 — ExoPlayer Java Plugin (Required)

`HlsPlayerController.cs` delegates to a Java helper class  
`com.movieplayer.ExoPlayerHelper`.  This thin wrapper must be compiled as an
Android plugin (`.aar`) and placed in `Assets/Plugins/Android/`.

Minimum API the helper must expose:

```java
// ExoPlayerHelper.java (pseudo-code)
public class ExoPlayerHelper {
    public static SimpleExoPlayer create(Activity activity, String hlsUrl, String unityGameObject);
    public static void setMinBufferMs(SimpleExoPlayer player, long ms);
}
```

The player sends callbacks to Unity via:
```java
UnityPlayer.UnitySendMessage(gameObject, "OnExoReady",    "");
UnityPlayer.UnitySendMessage(gameObject, "OnExoBuffering", String.valueOf(bufferedMs));
UnityPlayer.UnitySendMessage(gameObject, "OnExoError",     errorMessage);
```

See the [ExoPlayer documentation](https://exoplayer.dev/) for full setup.

---

## Architecture Overview

```
AppController
 ├── HlsPlayerController   (ExoPlayer via JNI)
 ├── StereoScreenRenderer  (UV-split quad / sphere)
 ├── ClarityStack          (Meta SR + RCAS shader)
 ├── TheaterEnvironment    (hall geometry + seats)
 ├── RoomClient            (WebSocket to cloud)
 │    ├── SyncEngine       (drift correction, guest only)
 │    ├── PreCacheLobby    (60s pre-cache + peer buffer status)
 │    ├── LobbyUI          (uGUI overlay)
 │    └── VoiceChat        (WebRTC mesh, PTT/VAD, duck)
 └── [Proto]               (MediaManifest, RoomMessages)
```
