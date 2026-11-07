# Build Lantern

## Windows

Install the .NET SDK specified in `global.json` and use PowerShell 7. Run:

```powershell
dotnet build Lantern.sln
./tools/build-release.ps1 -DownloadFfmpegOnSetup
```

The script creates `dist/Lantern` and `dist/Lantern-win-x64.zip` with the .NET runtime and MediaMTX. It fetches and verifies the pinned MediaMTX build when needed. The Windows release setup downloads the pinned FFmpeg tools from their distributor and verifies the archive before installing them. For local offline packaging, pass `-FfmpegRoot <folder>` instead, with FFmpeg and ffprobe in `bin` and the distributor's `LICENSE` and `README.txt` alongside it.

## Android

Set up a JDK (17 or later) and Android SDK platform 35. Set `ANDROID_HOME` or create `android/local.properties` with your SDK path. From the `android` directory:

```powershell
.\gradlew.bat testDebugUnitTest assembleDebug assembleRelease
```

APKs are under `android/app/build/outputs/apk`. Release signing uses a local `android/keystore.properties` file. Never commit signing material. A build without signing configuration produces an unsigned release APK.

## Checks

- `dotnet run --project tools/Verification/Lantern.Verification.csproj` checks compatibility, finite media preparation, discovery, and process cleanup.
- `tools/smoke/http-acceptance.py` exercises media and authentication against the verification fixture library.
- `tools/smoke/packaged-mirror-check.py` checks the Windows package after setup. It measures the local decoded stream, while Android device tests measure receiver behavior.
- Android tests include JVM tests and the `VerificationRunner` for playback and receiver checks.

Windows owns capture and the local relay. Android only receives the screen. Compatible videos play directly and incompatible videos are prepared as complete, seekable MP4 files before playback. Keep publisher audio and video filter graphs separate, and send each AAC access unit separately over RTSP.
