// Lantern Android TV client.
//
// Plugin versions are pinned here and applied by the modules, which keeps app/build.gradle.kts
// focused on the app itself.
plugins {
    id("com.android.application") version "8.7.3" apply false
    id("org.jetbrains.kotlin.android") version "2.0.21" apply false
    id("org.jetbrains.kotlin.plugin.compose") version "2.0.21" apply false
}

tasks.register<Delete>("clean") {
    delete(rootProject.layout.buildDirectory)
}
