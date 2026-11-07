# Lantern Android TV - R8 rules.
#
# Compose, AndroidX and Media3 all ship consumer rules that R8 applies automatically, so this file
# only pins the entry points the manifest instantiates by name.

# Application and the activities are created by the framework, never referenced from code.
-keep class com.lantern.tv.LanternApp { *; }
-keep class com.lantern.tv.MainActivity { *; }
-keep class com.lantern.tv.player.PlayerActivity { *; }
-keep class com.lantern.tv.mirror.MirrorActivity { *; }

# Media3 resolves some components (renderers, extractors) reflectively on older players.
-keepclassmembers class * implements androidx.media3.common.Player { *; }

# org.json is part of the platform: never warn about it.
-dontwarn org.json.**
