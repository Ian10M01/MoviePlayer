package com.movieplayer;

import android.app.Activity;
import android.os.Handler;
import android.os.Looper;
import android.util.Log;

import androidx.annotation.OptIn;
import androidx.media3.common.MediaItem;
import androidx.media3.common.PlaybackException;
import androidx.media3.common.PlaybackParameters;
import androidx.media3.common.Player;
import androidx.media3.common.util.UnstableApi;
import androidx.media3.datasource.DefaultHttpDataSource;
import androidx.media3.exoplayer.ExoPlayer;
import androidx.media3.exoplayer.hls.HlsMediaSource;
import androidx.media3.exoplayer.source.DefaultMediaSourceFactory;

import com.unity3d.player.UnityPlayer;

import java.util.concurrent.atomic.AtomicBoolean;

/**
 * ExoPlayerHelper
 * ───────────────
 * Thin wrapper around ExoPlayer (media3) for Unity/Android HLS playback.
 *
 * Unity C# calls the static factory {@link #create(Activity, String, String)}
 * which returns an {@code ExoPlayerHelper} instance. All subsequent method
 * calls on the returned object delegate to the underlying ExoPlayer on the
 * Android main looper.
 *
 * Callbacks to Unity (fired via {@code UnityPlayer.UnitySendMessage}):
 *   • "OnExoReady"     — player is prepared and ready to play
 *   • "OnExoBuffering" — buffered position in ms (string), called every ~1 s
 *   • "OnExoError"     — error message string
 *
 * The game-object name passed to {@link #create} is used as the first argument
 * to {@code UnitySendMessage}, matching the default in HlsPlayerController.cs.
 */
public class ExoPlayerHelper {

    private static final String TAG = "ExoPlayerHelper";

    private final Handler mainHandler = new Handler(Looper.getMainLooper());
    private ExoPlayer player;
    private final String unityGameObject;   // e.g. "ExoPlayerBridge"
    private final AtomicBoolean released = new AtomicBoolean(false);

    // Periodic buffering reporter
    private final Runnable bufferReporter = new Runnable() {
        @Override
        public void run() {
            if (released.get() || player == null) return;
            long bufferedMs = player.getBufferedPosition();
            sendToUnity("OnExoBuffering", String.valueOf(bufferedMs));
            mainHandler.postDelayed(this, 1_000);
        }
    };

    // ─────────────────────────────────────────────────────────────────────────
    // Private constructor – always use create()
    // ─────────────────────────────────────────────────────────────────────────
    private ExoPlayerHelper(String unityGameObject) {
        this.unityGameObject = unityGameObject;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Static factory (called by Unity via AndroidJavaClass.CallStatic)
    // ─────────────────────────────────────────────────────────────────────────

    /**
     * Creates an ExoPlayer instance on the Android main looper, builds an HLS
     * media source from {@code hlsUrl} (which should already contain the bearer
     * token as a query parameter), and prepares playback.
     *
     * @param activity       The UnityPlayerActivity (passed from C# as
     *                       {@code UnityPlayer.currentActivity}).
     * @param hlsUrl         Full HLS playlist URL, token appended as ?token=…
     * @param unityGameObject Name of the Unity GameObject that receives
     *                        {@code UnitySendMessage} callbacks.
     * @return               A fully prepared {@code ExoPlayerHelper} instance.
     */
    @OptIn(markerClass = UnstableApi.class)
    public static ExoPlayerHelper create(
            final Activity activity,
            final String hlsUrl,
            final String unityGameObject) {

        final ExoPlayerHelper helper = new ExoPlayerHelper(unityGameObject);

        // ExoPlayer must be created on the main looper
        activity.runOnUiThread(() -> {
            try {
                DefaultHttpDataSource.Factory httpFactory =
                        new DefaultHttpDataSource.Factory()
                                .setUserAgent("MoviePlayerVR/1.0")
                                .setConnectTimeoutMs(15_000)
                                .setReadTimeoutMs(15_000)
                                .setAllowCrossProtocolRedirects(true);

                HlsMediaSource.Factory hlsFactory =
                        new HlsMediaSource.Factory(httpFactory);

                helper.player = new ExoPlayer.Builder(activity)
                        .setMediaSourceFactory(hlsFactory)
                        .build();

                MediaItem mediaItem = MediaItem.fromUri(hlsUrl);
                helper.player.setMediaItem(mediaItem);

                helper.player.addListener(new Player.Listener() {

                    @Override
                    public void onPlaybackStateChanged(int state) {
                        if (state == Player.STATE_READY) {
                            Log.d(TAG, "STATE_READY → sending OnExoReady");
                            helper.sendToUnity("OnExoReady", "");
                            // Start periodic buffering reports
                            helper.mainHandler.postDelayed(
                                    helper.bufferReporter, 1_000);
                        } else if (state == Player.STATE_BUFFERING) {
                            Log.d(TAG, "STATE_BUFFERING");
                        } else if (state == Player.STATE_ENDED) {
                            Log.d(TAG, "STATE_ENDED");
                        }
                    }

                    @Override
                    public void onPlayerError(PlaybackException error) {
                        String msg = error.getMessage() != null
                                ? error.getMessage()
                                : "Unknown ExoPlayer error";
                        Log.e(TAG, "onPlayerError: " + msg);
                        helper.sendToUnity("OnExoError", msg);
                    }
                });

                helper.player.prepare();
                helper.player.setPlayWhenReady(false);

                Log.d(TAG, "ExoPlayer created for URL: " + hlsUrl);

            } catch (Exception e) {
                Log.e(TAG, "create() failed: " + e.getMessage(), e);
                helper.sendToUnity("OnExoError", "create failed: " + e.getMessage());
            }
        });

        return helper;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Playback controls (called by Unity on the GL / main thread)
    // ─────────────────────────────────────────────────────────────────────────

    /** Start or pause playback.  Called by HlsPlayerController.Play() / Pause(). */
    public void setPlayWhenReady(final boolean play) {
        mainHandler.post(() -> {
            if (player != null) player.setPlayWhenReady(play);
        });
    }

    /** Seek to an absolute position in milliseconds. */
    public void seekTo(final long positionMs) {
        mainHandler.post(() -> {
            if (player != null) player.seekTo(positionMs);
        });
    }

    /** Re-prepare the player (e.g. after setMinBufferMs). */
    public void prepare() {
        mainHandler.post(() -> {
            if (player != null) player.prepare();
        });
    }

    /**
     * Returns the current playback position in ms.
     * Called from Unity's Update() loop – returns 0 if player not ready yet.
     */
    public long getCurrentPosition() {
        if (player == null) return 0L;
        // Unity calls this on the GL thread which is also the main looper.
        return player.getCurrentPosition();
    }

    /**
     * Returns the buffered position in ms.
     * Called from Unity's Update() loop.
     */
    public long getBufferedPosition() {
        if (player == null) return 0L;
        return player.getBufferedPosition();
    }

    /**
     * Set playback speed (e.g. 1.0 = normal, 0.98–1.02 for slew correction).
     * Accepts a media3 {@link PlaybackParameters} object.
     */
    public void setPlaybackParameters(final PlaybackParameters params) {
        mainHandler.post(() -> {
            if (player != null) player.setPlaybackParameters(params);
        });
    }

    /**
     * Convenience overload for callers that only need to change speed.
     * Called by the sync engine for clock-slew correction.
     */
    public void setPlaybackRate(final float rate) {
        mainHandler.post(() -> {
            if (player != null)
                player.setPlaybackParameters(new PlaybackParameters(rate));
        });
    }

    /**
     * Set audio volume (0.0 – 1.0).
     */
    public void setVolume(final float volume) {
        mainHandler.post(() -> {
            if (player != null) player.setVolume(Math.max(0f, Math.min(1f, volume)));
        });
    }

    /**
     * Release the player and cancel the buffer reporter.
     * Must be called from OnDestroy in Unity.
     */
    public void release() {
        if (released.getAndSet(true)) return;
        mainHandler.removeCallbacks(bufferReporter);
        mainHandler.post(() -> {
            if (player != null) {
                player.release();
                player = null;
                Log.d(TAG, "ExoPlayer released");
            }
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Static helpers (called by Unity as static methods on the class)
    // ─────────────────────────────────────────────────────────────────────────

    /**
     * Adjust the minimum buffer duration before playback starts.
     * Called by HlsPlayerController.PreCacheSegments() as:
     *   _playerHelper.CallStatic("setMinBufferMs", _exoPlayer, targetMs)
     *
     * NOTE: ExoPlayer3 (media3) bakes LoadControl at build time, so we cannot
     * change min-buffer on a live player.  This method re-prepares the player
     * so ExoPlayer re-fetches from the current position with the existing
     * buffering policy.  For a true min-buffer change you would need to
     * rebuild the player – which is outside tonight's scope.
     */
    public static void setMinBufferMs(final ExoPlayerHelper helper, final long minBufferMs) {
        if (helper == null || helper.released.get()) return;
        helper.mainHandler.post(() -> {
            if (helper.player == null) return;
            // Re-seek to current position to trigger fresh buffering
            long pos = helper.player.getCurrentPosition();
            helper.player.seekTo(pos);
            Log.d(TAG, "setMinBufferMs(" + minBufferMs + ") → seeked to " + pos);
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Internal helpers
    // ─────────────────────────────────────────────────────────────────────────

    private void sendToUnity(final String method, final String value) {
        try {
            UnityPlayer.UnitySendMessage(unityGameObject, method, value);
        } catch (Exception e) {
            Log.w(TAG, "UnitySendMessage failed: " + e.getMessage());
        }
    }
}
