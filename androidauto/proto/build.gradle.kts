// Generated Android Auto protobuf classes (DiAuto app/src/main/proto, protoc 25.1) as a plain JVM
// library. rigPlay: kept out of the Android module so lint and Kotlin resolution see compiled
// classes instead of 100k lines of generated source, which made :androidauto:lintAnalyzeDebug
// run for over an hour.
plugins {
    id("java-library")
}

java {
    sourceCompatibility = JavaVersion.VERSION_11
    targetCompatibility = JavaVersion.VERSION_11
}

tasks.withType<JavaCompile>().configureEach {
    options.compilerArgs.add("-Xlint:none")
    options.isWarnings = false
}

dependencies {
    api("com.google.protobuf:protobuf-java:3.25.1")
}
