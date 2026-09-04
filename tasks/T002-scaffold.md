# T002 — Repository, solution, and application scaffold

- Status: READY
- Owner: Gemini 3.8 Flash
- Technical lead: Claude Opus 5
- Reviewer: GPT-5.6 Sol
- Base: `main` at the merge commit that integrates `optimus/T001-architecture` (record the exact SHA in Evidence)
- Branch: `optimus/T002-scaffold`
- Dependencies: T001

## Goal

A buildable, testable, empty skeleton for the whole product: one .NET solution with the seven assemblies and six test projects from ADR-001 section 5, and one Gradle project with the eight Android modules from ADR-001 section 6, with automated tests that enforce the dependency direction of both. No product behavior.

## In scope

- Solution, project, and build-configuration files for the .NET side.
- Gradle project, wrapper, version catalog, and module skeletons for the Android side.
- Placeholder types and placeholder tests sufficient to prove every project compiles and every test project runs.
- Architecture tests that enforce ADR-001 sections 5 and 6.
- A short `README.md` inside `src/`, `android/`, `runners/`, and `benchmarks/` describing what each tree will hold and which task owns it.

## Out of scope

- Any protocol type, DTO, or serializer. `Optimus.Contracts` is owned by T003.
- The device endpoint, WebSocket handling, TLS, or pairing. Owned by T004 and T020.
- Hotkey, widget, audio capture, or playback. Owned by T005 and T006.
- Runner processes, pipes, GPU scheduling, or any Python. Owned by T007 to T014.
- Adapter implementations. Owned by T016 and T017.
- Any Compose screen beyond an empty scaffold activity.
- CI configuration, installers, signing, or packaging.
- Editing `AGENTS.md`, `PROJECT_PLAN.md`, `README.md`, `.gitignore`, anything under `docs/`, or any task file other than this one.

## Owned files and subsystems

The implementer may create or modify only:

- `global.json`, `Optimus.sln`, `Directory.Build.props`, `Directory.Packages.props`, `NuGet.config`, `.editorconfig`
- `src/**`
- `tests/**`
- `android/**`
- `runners/README.md`
- `benchmarks/README.md`
- `tasks/T002-scaffold.md` (Evidence and Status only)

## Required design and invariants

- ADR-001 section 5 fixes the .NET assemblies and the five dependency rules.
- ADR-001 section 6 fixes the Android modules and their two dependency rules.
- `docs/BACKLOG.md` fixes which later task owns each area; this task creates directories and placeholders only.
- No production implementation is introduced. A placeholder is a type or file whose only purpose is to make the project compile and be referenced by a test.

## Implementation requirements

### 1. Pinned toolchain

These versions are the contract. Do not upgrade, downgrade, or add a version. A needed change is a blocker to report, not a decision to make.

| Tool | Version |
| --- | --- |
| .NET SDK | `8.0.400`, `rollForward: latestFeature`, pinned in `global.json` |
| C# language | `12.0` |
| JDK | 17 |
| Gradle wrapper | `8.9`, distribution `bin` |
| Android Gradle Plugin | `8.7.3` |
| Kotlin | `2.0.21` |
| Compose compiler plugin | `org.jetbrains.kotlin.plugin.compose` version `2.0.21` |
| Compose BOM | `2024.10.01` |
| `compileSdk` / `targetSdk` | 35 |
| `minSdk` | 33 |

### 2. .NET solution layout

```
global.json
Optimus.sln
Directory.Build.props
Directory.Packages.props
NuGet.config
.editorconfig
src/
  Optimus.Contracts/Optimus.Contracts.csproj
  Optimus.Client/Optimus.Client.csproj
  Optimus.Inference/Optimus.Inference.csproj
  Optimus.Providers/Optimus.Providers.csproj
  Optimus.Core/Optimus.Core.csproj
  Optimus.Service/Optimus.Service.csproj
  Optimus.Shell/Optimus.Shell.csproj
  README.md
tests/
  Optimus.Architecture.Tests/Optimus.Architecture.Tests.csproj
  Optimus.Contracts.Tests/Optimus.Contracts.Tests.csproj
  Optimus.Inference.Tests/Optimus.Inference.Tests.csproj
  Optimus.Providers.Tests/Optimus.Providers.Tests.csproj
  Optimus.Core.Tests/Optimus.Core.Tests.csproj
  Optimus.Service.Tests/Optimus.Service.Tests.csproj
```

Target frameworks and SDKs:

| Project | SDK | TargetFramework | Extra properties |
| --- | --- | --- | --- |
| `Optimus.Contracts` | `Microsoft.NET.Sdk` | `net8.0` | none |
| `Optimus.Client` | `Microsoft.NET.Sdk` | `net8.0` | none |
| `Optimus.Inference` | `Microsoft.NET.Sdk` | `net8.0-windows` | none |
| `Optimus.Providers` | `Microsoft.NET.Sdk` | `net8.0-windows` | none |
| `Optimus.Core` | `Microsoft.NET.Sdk` | `net8.0-windows` | none |
| `Optimus.Service` | `Microsoft.NET.Sdk.Web` | `net8.0-windows` | `<OutputType>Exe</OutputType>` |
| `Optimus.Shell` | `Microsoft.NET.Sdk` | `net8.0-windows` | `<OutputType>WinExe</OutputType>`, `<UseWPF>true</UseWPF>` |
| all test projects | `Microsoft.NET.Sdk` | `net8.0-windows` | `<IsPackable>false</IsPackable>` |

`ProjectReference` sets, exactly and exhaustively:

| Project | References |
| --- | --- |
| `Optimus.Contracts` | (none) |
| `Optimus.Client` | `Optimus.Contracts` |
| `Optimus.Inference` | `Optimus.Contracts` |
| `Optimus.Providers` | `Optimus.Contracts` |
| `Optimus.Core` | `Optimus.Contracts`, `Optimus.Inference`, `Optimus.Providers` |
| `Optimus.Service` | `Optimus.Contracts`, `Optimus.Core`, `Optimus.Inference`, `Optimus.Providers` |
| `Optimus.Shell` | `Optimus.Contracts`, `Optimus.Client` |
| `Optimus.Architecture.Tests` | (none) |
| `Optimus.Contracts.Tests` | `Optimus.Contracts` |
| `Optimus.Inference.Tests` | `Optimus.Inference` |
| `Optimus.Providers.Tests` | `Optimus.Providers` |
| `Optimus.Core.Tests` | `Optimus.Core` |
| `Optimus.Service.Tests` | `Optimus.Service` |

`Optimus.Architecture.Tests` deliberately references no product project; it reads `.csproj` files from disk.

### 3. `Directory.Build.props`

Applies to every project:

```xml
<Project>
  <PropertyGroup>
    <LangVersion>12.0</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <WarningLevel>9999</WarningLevel>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <AnalysisLevel>latest-Recommended</AnalysisLevel>
    <EnableNETAnalyzers>true</EnableNETAnalyzers>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <RootNamespace>$(MSBuildProjectName)</RootNamespace>
    <NeutralLanguage>en-US</NeutralLanguage>
    <Deterministic>true</Deterministic>
  </PropertyGroup>
</Project>
```

If `TreatWarningsAsErrors` causes a build failure in generated WPF or Web SDK code, suppress the specific diagnostic id in that one project with a comment naming the id and the reason. Do not disable `TreatWarningsAsErrors` and do not add a blanket `NoWarn`.

### 4. Dependency restrictions

`Directory.Packages.props` lists exactly these `PackageVersion` entries and no others:

| Package | Version | Used by |
| --- | --- | --- |
| `Microsoft.NET.Test.Sdk` | `17.11.1` | all test projects |
| `xunit` | `2.9.2` | all test projects |
| `xunit.runner.visualstudio` | `2.8.2` | all test projects |
| `Microsoft.Extensions.Hosting` | `8.0.1` | `Optimus.Service` |

Rules:

- No other NuGet package may be added in this task, including mocking, assertion, architecture-testing, logging, or serialization libraries. `System.Text.Json` comes from the framework and needs no package.
- `NuGet.config` pins `nuget.org` as the only source and clears inherited sources.
- No `PackageReference` may carry an inline `Version`; central management supplies it.
- Adding any package outside this table is a blocker to report to Claude Opus 5, not a decision to make.

### 5. Placeholder content, .NET

Exactly one file per product project, no more:

| Project | File | Content |
| --- | --- | --- |
| `Optimus.Contracts` | `ProtocolVersion.cs` | `public static class ProtocolVersion` with `public const int Major = 1;`, `public const int Minor = 0;`, `public const string Current = "1.0";`, `public const string SubProtocol = "optimus.v1";` |
| `Optimus.Client` | `ClientPlaceholder.cs` | `public static class ClientPlaceholder` with `public const string OwnedBy = "T004";` |
| `Optimus.Inference` | `InferencePlaceholder.cs` | same shape, `OwnedBy = "T007"` |
| `Optimus.Providers` | `ProvidersPlaceholder.cs` | same shape, `OwnedBy = "T016"` |
| `Optimus.Core` | `CorePlaceholder.cs` | same shape, `OwnedBy = "T018"` |
| `Optimus.Service` | `Program.cs` | See below |
| `Optimus.Shell` | `App.xaml`, `App.xaml.cs` | See below |

`Optimus.Service/Program.cs` requirements:

- Declares an explicit `public static class Program` with `public static int Main(string[] args)`. Do not use top-level statements; `Optimus.Service.Tests` references the type directly.
- Builds a `WebApplication` with Kestrel configured to listen on `127.0.0.1` port `0` only. It must never call `UseUrls` with a wildcard host and must never bind `0.0.0.0` or `[::]`.
- Maps no endpoints.
- If `args` contains `--smoke`, it starts the host, writes the bound port to stdout as `listening:<port>`, stops the host, and returns exit code `0` without blocking.
- Otherwise it runs the host normally.

`Optimus.Shell/App.xaml.cs` requirements:

- Overrides `OnStartup`. If the command line contains `--smoke`, it calls `Shutdown(0)` before creating any window and returns.
- Otherwise it creates no window either; the widget is T005. A comment names T005 as the owner.
- No `StartupUri` in `App.xaml`.

### 6. Test projects, .NET

Every test project contains at least one real assertion. Placeholder tests that assert `true` are not acceptable.

- `Optimus.Contracts.Tests/ProtocolVersionTests.cs`: asserts `Current == $"{Major}.{Minor}"` and `SubProtocol == "optimus.v1"`.
- `Optimus.Inference.Tests`, `Optimus.Providers.Tests`, `Optimus.Core.Tests`: one test each asserting the placeholder `OwnedBy` value, so the reference graph is exercised.
- `Optimus.Service.Tests/ScaffoldBoundaryTests.cs`: does not start the host. It asserts that `typeof(Program).Assembly.GetTypes()` contains no type whose name ends in `Endpoint`, `Hub`, `Controller`, or `Middleware`, which fails if endpoint implementation leaks into this task.
- `Optimus.Architecture.Tests/DotNetDependencyRuleTests.cs`: see section 7.

### 7. Architecture tests, .NET

`Optimus.Architecture.Tests` must:

1. Locate the repository root by walking up from `AppContext.BaseDirectory` until a directory containing `Optimus.sln` is found. Fail with a clear message if not found.
2. Parse every `src/**/*.csproj` with `System.Xml.Linq` and build a map of project name to referenced project names.
3. Assert each rule from ADR-001 section 5 as its own `[Fact]`, named after the rule:
   - `Contracts_HasNoProjectReferences`
   - `NothingReferencesShellOrService`
   - `InferenceAndProvidersAreIndependentOfEachOtherAndOfCore`
   - `ShellDoesNotReferenceCoreInferenceOrProviders`
   - `ClientReferencesOnlyContracts`
4. Add `AllProductProjectsAreInTheSolution`: every `src/**/*.csproj` and `tests/**/*.csproj` appears in `Optimus.sln`.
5. Add `NoProjectDeclaresAnInlinePackageVersion`: no `PackageReference` element in any `.csproj` has a `Version` attribute.
6. Add `OnlyApprovedPackagesAreDeclared`: the set of `PackageVersion` `Include` values in `Directory.Packages.props` equals exactly the four packages in section 4.

Failure messages must name the offending project and the rule.

### 8. Android project layout

```
android/
  settings.gradle.kts
  build.gradle.kts
  gradle.properties
  gradle/libs.versions.toml
  gradle/wrapper/gradle-wrapper.properties
  gradle/wrapper/gradle-wrapper.jar
  gradlew
  gradlew.bat
  README.md
  app/build.gradle.kts
  app/src/main/AndroidManifest.xml
  app/src/main/kotlin/com/optimus/voiceos/MainActivity.kt
  core/protocol/build.gradle.kts
  core/transport/build.gradle.kts
  core/audio/build.gradle.kts
  core/security/build.gradle.kts
  feature/talk/build.gradle.kts
  feature/sessions/build.gradle.kts
  feature/pairing/build.gradle.kts
```

Module types and dependencies, exactly:

| Module | Plugin | Depends on |
| --- | --- | --- |
| `:core:protocol` | `java-library` + `org.jetbrains.kotlin.jvm` | (none) |
| `:core:transport` | `com.android.library` + Kotlin | `:core:protocol` |
| `:core:audio` | `com.android.library` + Kotlin | `:core:protocol` |
| `:core:security` | `com.android.library` + Kotlin | `:core:protocol` |
| `:feature:pairing` | `com.android.library` + Kotlin + Compose | `:core:protocol`, `:core:transport`, `:core:security` |
| `:feature:talk` | `com.android.library` + Kotlin + Compose | `:core:protocol`, `:core:transport`, `:core:audio` |
| `:feature:sessions` | `com.android.library` + Kotlin + Compose | `:core:protocol`, `:core:transport`, `:core:security` |
| `:app` | `com.android.application` + Kotlin + Compose | all three `:feature:*` modules |

`:core:protocol` is a plain JVM library on purpose, so protocol vectors from T003 can run without an emulator.

Manifest and application requirements for `:app`:

- `android:allowBackup="false"`.
- `android:dataExtractionRules` referencing an XML that excludes all app storage from both cloud backup and device transfer.
- No `INTERNET` permission is declared in this task. T021 adds it with its transport work.
- `MainActivity` is an empty `ComponentActivity` that sets a Compose surface containing a single `Text("Optimus Voice OS")`. No navigation, no view models, no networking.

`gradle.properties` sets `org.gradle.jvmargs=-Xmx3g`, `android.useAndroidX=true`, `org.gradle.parallel=true`, `org.gradle.caching=true`, and `kotlin.code.style=official`.

All versions live in `gradle/libs.versions.toml`. No module may declare a literal version string.

The Android SDK location must come from the `ANDROID_HOME` environment variable. Do not create or commit `local.properties`.

### 9. Android tests and module-boundary check

- One JUnit 4 unit test per module, each with a real assertion. For `:core:protocol`, assert a `ProtocolVersion` object with the same four constants as the .NET placeholder, and assert `current == "$major.$minor"`. For the other modules, assert the module's `OwnedBy` constant, mirroring the .NET placeholders.
- Root `build.gradle.kts` registers a task `checkModuleBoundaries` that inspects every subproject's `api`, `implementation`, and `compileOnly` configurations for `ProjectDependency` entries and fails when:
  - `:core:protocol` declares any project dependency, or
  - any `:feature:*` module depends on another `:feature:*` module, or
  - any `:core:*` module depends on a `:feature:*` module or on `:app`.
- The failure message names the offending module pair.
- `checkModuleBoundaries` is wired as a dependency of the root `check` task so `gradlew check` covers it.

Pinned test dependencies, declared in the version catalog:

| Dependency | Version |
| --- | --- |
| `junit:junit` | `4.13.2` |
| `androidx.core:core-ktx` | `1.13.1` |
| `androidx.activity:activity-compose` | `1.9.3` |
| `androidx.compose:compose-bom` | `2024.10.01` |
| `androidx.compose.ui:ui`, `ui-tooling-preview`, `androidx.compose.material3:material3` | from the BOM, no explicit version |

No other Android dependency may be added in this task.

### 10. Tree READMEs

Each of `src/README.md`, `android/README.md`, `runners/README.md`, `benchmarks/README.md` is at most 15 lines and states what the tree holds, which ADR governs it, and which task from `docs/BACKLOG.md` owns the first real implementation. `runners/` and `benchmarks/` contain the README only.

## Acceptance criteria

1. `dotnet build Optimus.sln -c Release` succeeds with zero warnings.
2. `dotnet test Optimus.sln -c Release` runs every test project and all tests pass.
3. `dotnet format --verify-no-changes` reports no changes.
4. All eight architecture tests in section 7 exist (five ADR-001 rules plus solution membership, no inline package versions, and approved-packages-only), and each fails when its rule is deliberately violated. The implementer demonstrates this for at least two rules by temporarily introducing a violation, recording the failure output in Evidence, and reverting it.
5. `gradlew assembleDebug` produces a debug APK.
6. `gradlew test` runs a unit test in every module and all pass.
7. `gradlew checkModuleBoundaries` passes, and fails when a boundary is deliberately violated. Demonstrate once, record the output, and revert.
8. `Optimus.Service --smoke` exits 0 and prints a `listening:<port>` line for a loopback-only binding. `netstat -ano` during the run, or the printed binding, shows `127.0.0.1` and never `0.0.0.0`.
9. `Optimus.Shell --smoke` exits 0 and creates no window.
10. `Directory.Packages.props` contains exactly the four packages in section 4 and no `PackageReference` carries an inline version.
11. `git status --short` shows no untracked build output, no `local.properties`, no `.gradle/`, no `bin/` or `obj/`.
12. `git diff --check` reports nothing.
13. No file outside the ownership list in this contract is modified.
14. No protocol type, endpoint, runner, adapter, hotkey, audio, or Compose screen beyond `MainActivity` exists in the diff.

## Required tests and commands

Run exactly these, in this order, from the repository root:

```powershell
git rev-parse HEAD
dotnet --version
dotnet build .\Optimus.sln -c Release
dotnet test .\Optimus.sln -c Release
dotnet format .\Optimus.sln --verify-no-changes
dotnet run --project .\src\Optimus.Service\Optimus.Service.csproj -c Release -- --smoke
dotnet run --project .\src\Optimus.Shell\Optimus.Shell.csproj -c Release -- --smoke
cd .\android
.\gradlew.bat --no-daemon assembleDebug
.\gradlew.bat --no-daemon test
.\gradlew.bat --no-daemon checkModuleBoundaries
cd ..
git diff --check
git status --short
```

Record the exit code and the last 20 lines of output for each command in Evidence. If a command cannot run because a prerequisite is missing on the machine, record that fact explicitly and do not claim the check passed.

## Evidence

- Commit:
- Base commit:
- Commands executed:
- Results:
- Deliberate-violation demonstrations (architecture rules and module boundaries):
- Benchmarks/artifacts: none required for this task
- Known limitations:

## Review history

- Review report:
- Verdict:
