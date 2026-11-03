package com.movieplayer.vrcinema;

import com.unity3d.player.UnityPlayerActivity;

/**
 * Immersive Quest activity. The launcher activity stays a resizable 2D panel;
 * this one carries com.oculus.intent.category.VR and runs in its own process
 * so Unity can start OpenXR without leaving the panel window.
 */
public class TheaterActivity extends UnityPlayerActivity {
}
