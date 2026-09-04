plugins {
    alias(libs.plugins.android.library)
    alias(libs.plugins.kotlin.android)
}

android {
    namespace = "com.optimus.voiceos.core.transport"
    compileSdk = libs.versions.compileSdk.get().toInt()

    defaultConfig {
        minSdk = libs.versions.minSdk.get().toInt()
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }

    testOptions {
        unitTests {
            // PhoneClient touches android.util.Log; let the stubs no-op so the socket
            // behaviour can be tested on the JVM.
            isReturnDefaultValues = true
        }
    }
}

dependencies {
    implementation(project(":core:protocol"))
    testImplementation(libs.junit)
    // Real org.json for unit tests; the android.jar stub throws.
    testImplementation("org.json:json:20240303")
}
