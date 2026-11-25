# Stage 15 measured performance

Actual machine: Windows 11 Pro, Intel Core i5-13420H, 12 logical processors, approximately 16 GB RAM, CPU execution. This differs from the illustrative i7 target in the request. No dedicated GPU, Docker, administrator installation or new inference runtime was required.

Pinned INT8 graph: 3,246,078 bytes; tokenizer: 474,524 bytes. Initial testhost tokenizer/session initialization: 104.79 ms. The first-ever measured candidate-only extraction, including JIT/static initialization, took 274.38 ms; it is not a full-V2 cold-start measurement.

## Fresh-session cold measurements

| Mode | Session/tokenizer initialization ms | First extraction ms | ONNX Run ms | Windows |
|---|---|---|---|---|
| Legacy | 27.51 | 40.48 | 30.58 | 2 |
| CandidateV2 | 37.07 | 27.39 | 9.00 | 2 |


These are new ONNX sessions after process/JIT and OS file caches have warmed; the OS cache was not cleared. Cold pipeline measurements exclude database persistence and HTTP.

## Warm original AML note, 30 repetitions per pipeline

| Mode | Mean ms | p50 ms | p95 ms | Mean ONNX Run ms | All-logical-CPU utilization | Observed process peak MiB |
|---|---|---|---|---|---|---|
| Legacy | 25.41 | 23.24 | 37.54 | 10.61 | 90.08% | 173.50 |
| CandidateV2 | 27.23 | 25.71 | 35.08 | 10.26 | 88.44% | 174.80 |


Overall testhost peak after ablations, concurrency and fresh-session checks: 202.05 MiB. Peak working set includes .NET/xUnit, catalog/regex structures, managed/native ONNX memory and multiple test sessions. It is not a model-only allocation or an isolated production-service memory bound.

## Long notes and concurrency

| Mode | Notes | Characters/note | Content tokens/note | Mean ms | Observed sequential notes/second |
|---|---|---|---|---|---|
| Legacy | 5 | 12704 | 1775 | 40.95 | 24.42 |
| CandidateV2 | 5 | 12704 | 1775 | 56.66 | 17.65 |


Eight concurrent short requests completed in 8.86 ms with distinct correlations; output isolation is also asserted separately in tests. This short observation does not establish production capacity or an SLA. V2 is slower on the measured long-note cohort because it adds clinical processing; improved recall is not described as an inference speedup.

Loaded sessions and immutable terminology resources are reused. Every supported content token is evaluated; V2 uses per-window CLS/SEP and positive overlap coverage. Model/graph/tokenizer corruption fails closed. A real configured timeout, cancellation and subsequent session reuse are exercised by automated tests.

## Persistence

One isolated PostgreSQL measurement: API extraction plus reload 2646.52 ms; a separate persistence-only insert 343.12 ms; reload 399.15 ms. The insert uses a new synthetic extraction identity, not an overwrite. Connections in the test use pooling disabled; these are single observations, not latency distributions or production database estimates.

Raw results: `stage15-evidence/evaluation/performance.json` and `integration/real-postgres-roundtrip.json`. CPU utilization is process CPU time divided by elapsed wall time and logical processor count. Replacement-model latency, memory, quantized accuracy and deployment costs are NOT TESTED.
