// Android Auto receiver vendored from shihabal3amri/DiAuto (AGPL-3.0, see LICENSE and VENDORED.md).
// Built as a library so the DiPlay head-unit APK carries CarPlay and Android Auto side by side.
plugins {
    id("com.android.library")
}

// Optional local-only input: the Android Auto head-unit private key (raw/privkey, PEM PKCS#8).
// Ordinary source and CI builds carry no key; the handshake then fails with a clear message.
val headUnitKeyDir = providers.environmentVariable("ANDROID_AUTO_KEY_DIR")
    .orNull?.let { file(it).canonicalFile }

// Locale list for LocaleHelper, mirroring the upstream app module.
val availableLocales = file("src/main/res").listFiles { file ->
    file.isDirectory && file.name.startsWith("values-") &&
        !file.name.contains("night") && !file.name.contains("land") && !file.name.contains("port") &&
        !file.name.matches(Regex("values-[whsml]\\d+.*")) && !file.name.matches(Regex("values-v\\d+")) &&
        file.resolve("strings.xml").exists()
}?.map { it.name.removePrefix("values-") }?.sorted() ?: emptyList()

android {
    namespace = "com.andrerinas.openheadunit"
    compileSdk {
        version = release(37)
    }
    ndkVersion = "28.2.13676358"

    defaultConfig {
        minSdk = 28
        consumerProguardFiles("proguard-project.txt")

        buildConfigField("String", "AVAILABLE_LOCALES", "\"${availableLocales.joinToString(",")}\"")
        // The upstream app read these from its own application module.
        buildConfigField("String", "VERSION_NAME", "\"0.3.11\"")
        buildConfigField("int", "VERSION_CODE", "111")
        buildConfigField("String", "APPLICATION_ID", "\"com.andrerinas.headunitrevived\"")

        ndk {
            abiFilters += listOf("arm64-v8a", "armeabi-v7a", "x86_64")
        }
    }

    headUnitKeyDir?.let { sourceSets.getByName("main").res.srcDir(it) }

    externalNativeBuild {
        cmake {
            path = file("src/main/cpp/CMakeLists.txt")
            version = "3.22.1"
        }
    }

    buildFeatures {
        buildConfig = true
        aidl = true
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_11
        targetCompatibility = JavaVersion.VERSION_11
    }

    packaging {
        resources {
            excludes += listOf(
                "META-INF/DEPENDENCIES", "META-INF/LICENSE", "META-INF/LICENSE.txt", "META-INF/license.txt",
                "META-INF/NOTICE", "META-INF/NOTICE.txt", "META-INF/notice.txt", "META-INF/ASL2.0",
            )
        }
    }

    lint {
        abortOnError = false
        // rigPlay: full lint analysis of the vendored Kotlin never finished (over 90 CPU minutes under
        // AGP 9.3's K2 UAST; K1 is no longer available). The app's lint only needs this module's
        // partial results, so the vendored code gets manifest-level checks only. Upstream DiAuto does
        // not run lint in CI either.
        checkOnly += setOf("ManifestOrder", "DuplicateUsesFeature")
    }

    testOptions {
        unitTests.isIncludeAndroidResources = true
        unitTests.all { it.maxHeapSize = "1g" }
    }
}

dependencies {
    implementation("org.conscrypt:conscrypt-android:2.5.3")
    implementation(project(":androidauto:proto"))
    implementation("androidx.activity:activity-ktx:1.8.2")
    implementation("androidx.fragment:fragment-ktx:1.6.2")
    implementation("androidx.media:media:1.6.0")
    implementation("androidx.recyclerview:recyclerview:1.3.2")
    implementation("com.google.android.material:material:1.10.0")
    implementation("androidx.appcompat:appcompat:1.6.1")
    implementation("androidx.startup:startup-runtime:1.1.1")
    implementation("com.google.android.gms:play-services-nearby:19.3.0")
    implementation("androidx.lifecycle:lifecycle-extensions:2.2.0")
    implementation("androidx.core:core-ktx:1.12.0")
    implementation("androidx.lifecycle:lifecycle-viewmodel-ktx:2.6.2")
    implementation("androidx.multidex:multidex:2.0.1")
    implementation("androidx.navigation:navigation-fragment-ktx:2.3.5")
    implementation("androidx.navigation:navigation-ui-ktx:2.3.5")
    implementation("com.linkedin.dexmaker:dexmaker:2.28.3")
    // Glide without its annotation processor: the vendored code uses the plain Glide API.
    implementation("com.github.bumptech.glide:glide:4.16.0")
    implementation("com.google.zxing:core:3.5.3")
    implementation("dev.rikka.shizuku:api:13.1.5")
    implementation("dev.rikka.shizuku:provider:13.1.5")
    implementation("com.github.topjohnwu.libsu:core:6.0.0")

    testImplementation(libs.junit)
    testImplementation("org.robolectric:robolectric:4.17")
}
