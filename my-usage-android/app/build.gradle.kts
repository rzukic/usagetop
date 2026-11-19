import java.util.Properties

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

// The project's one version number, which release.ps1 passes in:
//   ./gradlew assembleRelease -PreleaseVersion=1.2.1
// Without it this is a development build.
val releaseVersion = (findProperty("releaseVersion") as String?)?.trim().orEmpty()

// Android orders installs by a single number that must rise with every release.
// 1.2.1 becomes 1020199 and 1.2.1-beta.3 becomes 1020103, so a release sits
// above all of its own betas and below the next version's first one.
fun versionCodeOf(version: String): Int {
    val match = Regex("""^(\d+)\.(\d+)\.(\d+)(-.*)?$""").matchEntire(version)
        ?: throw GradleException("releaseVersion '$version' is not a version like 1.2.0 or 1.2.1-beta.1")
    val (major, minor, patch) = match.destructured.toList().take(3).map(String::toInt)
    val stage = match.groupValues[4].let { suffix ->
        if (suffix.isEmpty()) 99
        else (Regex("""(\d+)$""").find(suffix)?.value?.toInt() ?: 0).coerceAtMost(98)
    }
    return major * 1_000_000 + minor * 10_000 + patch * 100 + stage
}

// The release key lives outside the repository: a properties file naming the
// keystore, its alias and passwords, at USAGETOP_SIGNING or in the home folder.
// Without one a release build still works, but comes out unsigned.
val signingFile = file(
    System.getenv("USAGETOP_SIGNING") ?: "${System.getProperty("user.home")}/.usagetop/signing.properties"
)
val signing = Properties().apply { if (signingFile.isFile) signingFile.inputStream().use { load(it) } }

android {
    namespace = "io.github.rzukic.usagetop"
    compileSdk = 34

    defaultConfig {
        applicationId = "io.github.rzukic.usagetop"
        minSdk = 26
        targetSdk = 34
        versionCode = if (releaseVersion.isEmpty()) 1 else versionCodeOf(releaseVersion)
        versionName = releaseVersion.ifEmpty { "dev" }
    }

    signingConfigs {
        if (signing.getProperty("storeFile") != null) {
            create("release") {
                storeFile = file(signing.getProperty("storeFile"))
                storePassword = signing.getProperty("storePassword")
                keyAlias = signing.getProperty("keyAlias")
                keyPassword = signing.getProperty("keyPassword")
            }
        }
    }

    buildTypes {
        release {
            signingConfig = signingConfigs.findByName("release")
            isMinifyEnabled = false
            proguardFiles(
                getDefaultProguardFile("proguard-android-optimize.txt"),
                "proguard-rules.pro"
            )
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }

    buildFeatures {
        viewBinding = true
    }
}

dependencies {
    implementation("androidx.core:core-ktx:1.13.1")
    implementation("androidx.appcompat:appcompat:1.7.0")
    implementation("androidx.browser:browser:1.8.0")
    implementation("com.google.android.material:material:1.12.0")
    implementation("androidx.lifecycle:lifecycle-runtime-ktx:2.8.4")
    implementation("androidx.work:work-runtime-ktx:2.9.0")
    implementation("androidx.security:security-crypto:1.1.0-alpha06")
    implementation("com.squareup.okhttp3:okhttp:4.12.0")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.8.1")

    testImplementation("junit:junit:4.13.2")
    // The real org.json, so the provider parsers can be tested off-device.
    testImplementation("org.json:json:20240303")
}
