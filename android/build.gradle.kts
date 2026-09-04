plugins {
    alias(libs.plugins.android.application) apply false
    alias(libs.plugins.android.library) apply false
    alias(libs.plugins.kotlin.android) apply false
    alias(libs.plugins.kotlin.jvm) apply false
    alias(libs.plugins.compose.compiler) apply false
}

tasks.register("checkModuleBoundaries") {
    group = "verification"
    description = "Enforces ADR-001 section 6 module dependency boundaries."

    doLast {
        val targetConfigs = setOf("api", "implementation", "compileOnly")
        for (subproject in subprojects) {
            val fromPath = subproject.path
            for (configName in targetConfigs) {
                val config = subproject.configurations.findByName(configName) ?: continue
                for (dep in config.dependencies) {
                    if (dep is org.gradle.api.artifacts.ProjectDependency) {
                        val toPath = dep.dependencyProject.path

                        // 1. :core:protocol declares no project dependency
                        if (fromPath == ":core:protocol") {
                            throw GradleException(
                                "Module boundary violation: ':core:protocol' must not declare any project dependencies, but depends on '$toPath' (rule: ADR-001 section 6)."
                            )
                        }

                        // 2. Any :feature:* module depends on another :feature:* module
                        if (fromPath.startsWith(":feature:") && toPath.startsWith(":feature:")) {
                            throw GradleException(
                                "Module boundary violation: Feature module '$fromPath' must not depend on feature module '$toPath' (rule: ADR-001 section 6)."
                            )
                        }

                        // 3. Any :core:* module depends on a :feature:* module or on :app
                        if (fromPath.startsWith(":core:") && (toPath.startsWith(":feature:") || toPath == ":app")) {
                            throw GradleException(
                                "Module boundary violation: Core module '$fromPath' must not depend on '$toPath' (rule: ADR-001 section 6)."
                            )
                        }
                    }
                }
            }
        }
    }
}

tasks.register("check") {
    group = "verification"
    description = "Runs all verification checks including module boundaries."
    dependsOn("checkModuleBoundaries")
}
