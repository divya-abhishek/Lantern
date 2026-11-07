import java.util.Properties

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
}

// Release signing for the sideloaded projector build.
//
// The keystore and its passwords are machine-local and are never committed: keystore.properties is
// listed in ../.gitignore, as is the keystore itself. The file is optional so that a fresh checkout
// still configures and builds (the release APK is then simply unsigned, as before).
// Regenerate with:
//   keytool -genkeypair -keystore keystore/lantern-release.keystore -alias lantern \
//     -keyalg RSA -keysize 4096 -validity 10000 -storetype PKCS12
val keystorePropertiesFile = rootProject.file("keystore.properties")
val keystoreProperties = Properties().apply {
    if (keystorePropertiesFile.exists()) {
        keystorePropertiesFile.inputStream().use { load(it) }
    }
}
val hasReleaseKeystore = keystorePropertiesFile.exists() &&
    !keystoreProperties.getProperty("storeFile").isNullOrBlank()

android {
    namespace = "com.lantern.tv"
    compileSdk = 35

    defaultConfig {
        applicationId = "com.lantern.tv"
        minSdk = 24
        targetSdk = 35
        versionCode = 1
        versionName = "1.0.0"
        testInstrumentationRunner = "com.lantern.tv.VerificationRunner"
        // Android TV / projector only: no phone requirements leak into the manifest.
        resourceConfigurations += listOf("en")
    }

    buildFeatures {
        compose = true
        buildConfig = true
    }

    // The projector ships 1 GB of RAM: keep the dex small and unpacked.
    packaging {
        resources.excludes += setOf(
            "META-INF/AL2.0",
            "META-INF/LGPL2.1",
            "META-INF/*.kotlin_module",
        )
    }

    signingConfigs {
        create("release") {
            if (hasReleaseKeystore) {
                // Paths in keystore.properties are relative to the android/ project root.
                storeFile = rootProject.file(keystoreProperties.getProperty("storeFile"))
                storePassword = keystoreProperties.getProperty("storePassword")
                keyAlias = keystoreProperties.getProperty("keyAlias")
                keyPassword = keystoreProperties.getProperty("keyPassword")
                // v2 is what Android 7.0+ (minSdk 24) actually verifies at install time; v3 is added
                // so the signing key can be rotated later without breaking updates on Android 9+.
                // v1 (JAR signing) stays off: it is only needed below minSdk 24, so AGP's default
                // for this minSdk is left alone.
                enableV2Signing = true
                enableV3Signing = true
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
            // Only wired up when the local keystore is present, so app-release.apk is signed and
            // installable on this machine while remaining unsigned (not broken) elsewhere.
            signingConfig = if (hasReleaseKeystore) signingConfigs.getByName("release") else null
        }
        debug {
            isMinifyEnabled = false
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }
}

dependencies {
    // Compose (UI only; no TV material library, focus is handled explicitly and cheaply).
    implementation(platform("androidx.compose:compose-bom:2024.10.01"))
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.foundation:foundation")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.activity:activity-compose:1.9.3")
    implementation("androidx.core:core-ktx:1.15.0")
    implementation("androidx.lifecycle:lifecycle-runtime-ktx:2.8.7")
    // collectAsStateWithLifecycle: collect only while the screen is actually on.
    implementation("androidx.lifecycle:lifecycle-runtime-compose:2.8.7")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.8.1")

    // Finite HTTP media (original or completed MP4) and a separate RTSP mirror source.
    implementation("androidx.media3:media3-exoplayer:1.5.1")
    // Screen mirroring is RTSP (RtspMediaSource). It is its own Media3 module: without it the
    // mirror screen has no way to open the relay's stream, and normal Media Cast does not need it.
    implementation("androidx.media3:media3-exoplayer-rtsp:1.5.1")
    implementation("androidx.media3:media3-ui:1.5.1")
    // Unit tests: the wire contracts are verified against payloads captured from a real server
    // (see tools/smoke/backend-smoke.ps1), so a server field rename fails here instead of on the
    // projector. org.json is not on the JVM test classpath by default, hence the test dependency.
    testImplementation("junit:junit:4.13.2")
    testImplementation("org.json:json:20240303")
}
