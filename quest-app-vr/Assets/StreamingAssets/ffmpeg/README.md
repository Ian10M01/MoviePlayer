# On-device ffmpeg (Quest arm64)

The file `arm64-v8a/ffmpeg` is a static Android arm64 executable (about 27 MB).

- Build: [yearsyan/ffmpeg-android-build v7.1-beta.16](https://github.com/yearsyan/ffmpeg-android-build/releases/tag/v7.1-beta.16)
- Download: `ffmpeg_android_aarch64_gpl.tar.gz` (the `bin/ffmpeg` entry, not the dynamic build)
- Direct URL: https://github.com/yearsyan/ffmpeg-android-build/releases/download/v7.1-beta.16/ffmpeg_android_aarch64_gpl.tar.gz

This is the GPL variant because it links **libx264**. FFmpeg’s own AAC encoder (not fdk-aac) is used for AAC-LC.

License: GPL-2.0-or-later for this binary.

- FFmpeg source: https://ffmpeg.org/download.html (source corresponding to the 7.1 line used by that release)
- libx264: https://code.videolan.org/videolan/x264 (GPL-2.0-or-later)
- Build scripts: https://github.com/yearsyan/ffmpeg-android-build

The player copies it out of StreamingAssets on first launch, marks it executable, and uses it to probe files and to transcode audio or video that Android MediaPlayer cannot play (TrueHD, DTS, DTS-HD, Atmos, EAC3, AC3, FLAC, Opus, PCM, Vorbis, VC-1, MPEG-2, and other non-H.264/HEVC video).
