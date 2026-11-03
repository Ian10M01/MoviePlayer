# Building the ExoPlayer AAR

This Gradle module compiles the thin ExoPlayer wrapper that Unity calls via JNI.
Build time: ~3 minutes on a laptop.

---

## Prerequisites

| Tool | Version | Download |
|------|---------|----------|
| Android Studio | Hedgehog 2023.1 or later | [developer.android.com/studio](https://developer.android.com/studio) |
| Android SDK | API 34 (install via SDK Manager in Android Studio) | — |
| Java 11 | Bundled with Android Studio | — |
| Unity | 2022.3 LTS or 6000.x | (same install you use for the Quest app) |

---

## Step 1 — Copy the Unity stub JAR

Unity's Android build requires a stub JAR so the compiler can resolve
`com.unity3d.player.UnityPlayer`.  Find it in your Unity installation:

**Windows (typical Unity Hub path):**
```
C:\Program Files\Unity\Hub\Editor\<VERSION>\Editor\Data\PlaybackEngines\AndroidPlayer\Variations\il2cpp\Release\Classes\unity-classes.jar
```

**Mac:**
```
/Applications/Unity/Hub/Editor/<VERSION>/PlaybackEngines/AndroidPlayer/Variations/il2cpp/Release/Classes/unity-classes.jar
```

Copy that file to:
```
quest-app/ExoPlayerPlugin/libs/unity-classes.jar
```

> **Note:** This JAR is only used at compile-time (`compileOnly`). It is
> intentionally **not** bundled in the output AAR, because Unity provides the
> real implementation at runtime.

---

## Step 2 — Open the module in Android Studio

1. Open Android Studio.
2. Choose **File → Open**.
3. Navigate to `quest-app/ExoPlayerPlugin` and click **OK**.
4. Wait for Gradle sync to finish (it will download media3 dependencies).

---

## Step 3 — Build the AAR

Either:

**Option A — GUI**
- In the menu bar: **Build → Make Module 'ExoPlayerPlugin'**

**Option B — terminal**
```bash
# macOS / Linux
cd quest-app/ExoPlayerPlugin
./gradlew assembleRelease

# Windows (PowerShell)
cd quest-app\ExoPlayerPlugin
.\gradlew.bat assembleRelease
```

---

## Step 4 — Copy the AAR into Unity

After a successful build the AAR is at:
```
quest-app/ExoPlayerPlugin/build/outputs/aar/ExoPlayerPlugin-release.aar
```

Copy it to:
```
quest-app/Assets/Plugins/Android/ExoPlayerPlugin.aar
```

> Unity will automatically include it when building the Android APK.

---

## Step 5 — Verify in Unity

1. Open the `quest-app` Unity project.
2. In the **Project** window expand `Assets/Plugins/Android/`.
3. Click `ExoPlayerPlugin.aar` and confirm the **Inspector** shows:
   - Platform: **Android**
   - CPU: **Any**
4. Build the APK (**File → Build Settings → Android → Build**) and sideload
   both Quests using `adb install -r MoviePlayer.apk`.

---

## Troubleshooting

| Problem | Fix |
|---------|-----|
| `Cannot find symbol: UnityPlayer` | `libs/unity-classes.jar` is missing or wrong path |
| `Duplicate class androidx.media3…` | Another AAR in `Assets/Plugins/Android/` already bundles media3 — remove duplicates |
| Gradle sync fails with proxy error | Android Studio → Settings → HTTP Proxy → No proxy |
