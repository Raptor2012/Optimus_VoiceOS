# Local model evaluation harness

This directory contains the fixed, repeatable workflow set used to compare the
Gemma 4 E2B baseline with the Qwen3.5-4B and Holo3.1-4B Q4_K_M candidates on the
target i9-14900HX / RTX 4070 Laptop machine.

The harness deliberately does not send prompts to a provider. It supplies a stable
scenario catalog and gate evaluator to a local runner, so a run can be recorded
without credentials or durable conversation data.

## Run

```powershell
dotnet test tools/model-eval/Optimus.ModelEval.Tests/Optimus.ModelEval.Tests.csproj
dotnet run --project tools/model-eval/Optimus.ModelEval/Optimus.ModelEval.csproj -- --help
```

The test project verifies the catalog and the selection gates. A product-specific
runner can call `EvaluationHarness.RunAsync` with a local model adapter and write
the returned `EvaluationReport` to an ignored results directory.

Metrics are measured from end-of-speech to the first emitted action, through the
last expected action, and as peak VRAM in MiB. Do not include model download time
or cloud/provider calls in a run.
