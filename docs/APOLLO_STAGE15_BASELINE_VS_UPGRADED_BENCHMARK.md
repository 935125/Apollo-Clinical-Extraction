# Stage 15 baseline versus upgraded benchmark

Measured on 25 November 2026. All predictions were produced by the real pinned local ONNX artifact and C# components. No new Foundry requests were made. The pre-edit baseline, source snapshot, hashes and command logs are immutable under `stage15-evidence/baseline/`.

The original 35-case source/labels were retained byte-for-byte. Another 47 draft cases were source-authored and frozen before the candidate implementation executed, then used during development. Neither dataset is clinician-adjudicated or a held-out clinical assessment. The original labels omit a negated Penicillin mention and use a shorter chest-radiograph mention; strict scoring keeps these as mismatches. No GPT output supplies gold labels.

## Detection and ablations

Each component-only run changes that component while retaining frozen legacy candidates/attributes/assertions for other components. Legacy window slicing is retained for individual ablations; integrated uses per-window special tokens. Component-only latency is not an independent end-to-end deployment benchmark. Both full provider adapters were also executed, including `ClinicalReview` and metadata.

### 35 cases: `stage5_independent_benchmark_35cases.json`

| Variant | Entity | TP | FP | FN | Precision | Recall | F1 |
|---|---|---|---|---|---|---|---|
| original-frozen | medications | 72 | 0 | 16 | 100.00% | 81.82% | 90.00% |
| original-frozen | investigations | 75 | 1 | 4 | 98.68% | 94.94% | 96.77% |
| candidates-only | medications | 88 | 1 | 0 | 98.88% | 100.00% | 99.44% |
| candidates-only | investigations | 75 | 1 | 4 | 98.68% | 94.94% | 96.77% |
| investigations-only | medications | 72 | 0 | 16 | 100.00% | 81.82% | 90.00% |
| investigations-only | investigations | 78 | 1 | 1 | 98.73% | 98.73% | 98.73% |
| assertions-only | medications | 72 | 0 | 16 | 100.00% | 81.82% | 90.00% |
| assertions-only | investigations | 75 | 1 | 4 | 98.68% | 94.94% | 96.77% |
| terminology-only | medications | 72 | 0 | 16 | 100.00% | 81.82% | 90.00% |
| terminology-only | investigations | 75 | 1 | 4 | 98.68% | 94.94% | 96.77% |
| attributes-only | medications | 72 | 0 | 16 | 100.00% | 81.82% | 90.00% |
| attributes-only | investigations | 75 | 1 | 4 | 98.68% | 94.94% | 96.77% |
| sections-only | medications | 72 | 0 | 16 | 100.00% | 81.82% | 90.00% |
| sections-only | investigations | 75 | 1 | 4 | 98.68% | 94.94% | 96.77% |
| integrated | medications | 88 | 1 | 0 | 98.88% | 100.00% | 99.44% |
| integrated | investigations | 78 | 1 | 1 | 98.73% | 98.73% | 98.73% |
| legacy-adapter | medications | 72 | 0 | 16 | 100.00% | 81.82% | 90.00% |
| legacy-adapter | investigations | 75 | 1 | 4 | 98.68% | 94.94% | 96.77% |
| integrated-adapter | medications | 88 | 1 | 0 | 98.88% | 100.00% | 99.44% |
| integrated-adapter | investigations | 78 | 1 | 1 | 98.73% | 98.73% | 98.73% |


### 47 cases: `stage15_frozen_cases.json`

| Variant | Entity | TP | FP | FN | Precision | Recall | F1 |
|---|---|---|---|---|---|---|---|
| original-frozen | medications | 18 | 0 | 17 | 100.00% | 51.43% | 67.92% |
| original-frozen | investigations | 23 | 2 | 20 | 92.00% | 53.49% | 67.65% |
| candidates-only | medications | 35 | 0 | 0 | 100.00% | 100.00% | 100.00% |
| candidates-only | investigations | 23 | 2 | 20 | 92.00% | 53.49% | 67.65% |
| investigations-only | medications | 18 | 0 | 17 | 100.00% | 51.43% | 67.92% |
| investigations-only | investigations | 43 | 0 | 0 | 100.00% | 100.00% | 100.00% |
| assertions-only | medications | 18 | 0 | 17 | 100.00% | 51.43% | 67.92% |
| assertions-only | investigations | 23 | 2 | 20 | 92.00% | 53.49% | 67.65% |
| terminology-only | medications | 18 | 0 | 17 | 100.00% | 51.43% | 67.92% |
| terminology-only | investigations | 23 | 2 | 20 | 92.00% | 53.49% | 67.65% |
| attributes-only | medications | 18 | 0 | 17 | 100.00% | 51.43% | 67.92% |
| attributes-only | investigations | 23 | 2 | 20 | 92.00% | 53.49% | 67.65% |
| sections-only | medications | 18 | 0 | 17 | 100.00% | 51.43% | 67.92% |
| sections-only | investigations | 23 | 2 | 20 | 92.00% | 53.49% | 67.65% |
| integrated | medications | 35 | 0 | 0 | 100.00% | 100.00% | 100.00% |
| integrated | investigations | 43 | 0 | 0 | 100.00% | 100.00% | 100.00% |
| legacy-adapter | medications | 18 | 0 | 17 | 100.00% | 51.43% | 67.92% |
| legacy-adapter | investigations | 23 | 2 | 20 | 92.00% | 53.49% | 67.65% |
| integrated-adapter | medications | 35 | 0 | 0 | 100.00% | 100.00% | 100.00% |
| integrated-adapter | investigations | 43 | 0 | 0 | 100.00% | 100.00% | 100.00% |


## Medication attributes and linking

Values are evaluated against their annotated medication mention. Missing detections count as errors. Linking is the joint entity/annotated-attribute value accuracy, not a separately adjudicated complete relation graph. Explicit null annotations are included where present. Unannotated fields do not count as correct.

### `stage5_independent_benchmark_35cases.json`

| Attribute | Original adapter | V2 adapter |
|---|---|---|
| strength | 72/88 (81.82%) | 73/88 (82.95%) |
| dose | 60/69 (86.96%) | 69/69 (100.00%) |
| route | 67/79 (84.81%) | 79/79 (100.00%) |
| frequency | 67/81 (82.72%) | 81/81 (100.00%) |
| duration | 21/25 (84.00%) | 25/25 (100.00%) |
| Attribute-to-annotated-drug joint accuracy | 287/342 (83.92%) | 327/342 (95.61%) |


### `stage15_frozen_cases.json`

| Attribute | Original adapter | V2 adapter |
|---|---|---|
| strength | 2/9 (22.22%) | 9/9 (100.00%) |
| dose | 2/15 (13.33%) | 15/15 (100.00%) |
| dose_unit | 0/5 (0.00%) | 5/5 (100.00%) |
| route | 5/16 (31.25%) | 16/16 (100.00%) |
| frequency | 3/9 (33.33%) | 9/9 (100.00%) |
| duration | 0/3 (0.00%) | 3/3 (100.00%) |
| Attribute-to-annotated-drug joint accuracy | 12/52 (23.08%) | 52/52 (100.00%) |


Mass without a documented product form/quantity is preserved as administered dose in V2; product strength remains missing. Fifteen original-case strength expectations retain the old interpretation. The score is reported as observed rather than changing those annotations. No absolute chemotherapy dose is computed.

## Investigations, evidence and normalization

### `stage5_independent_benchmark_35cases.json`

| Measure | Original adapter | V2 adapter |
|---|---|---|
| medications evidence exact offsets | 72/72 (100.00%) | 89/89 (100.00%) |
| medications evidence/entity association | 72/72 (100.00%) | 89/89 (100.00%) |
| medications standard-code coverage | 60/72 (83.33%) | 60/89 (67.42%) |
| medications unresolved code rate | 12/72 (16.67%) | 29/89 (32.58%) |
| investigations evidence exact offsets | 76/76 (100.00%) | 79/79 (100.00%) |
| investigations evidence/entity association | 76/76 (100.00%) | 79/79 (100.00%) |
| investigations standard-code coverage | 32/76 (42.11%) | 32/79 (40.51%) |
| investigations unresolved code rate | 44/76 (57.89%) | 47/79 (59.49%) |
| investigation timing | 66/70 (94.29%) | 69/70 (98.57%) |
| investigation assertion_status | 75/79 (94.94%) | 78/79 (98.73%) |


### `stage15_frozen_cases.json`

| Measure | Original adapter | V2 adapter |
|---|---|---|
| medications evidence exact offsets | 18/18 (100.00%) | 35/35 (100.00%) |
| medications evidence/entity association | 18/18 (100.00%) | 35/35 (100.00%) |
| medications standard-code coverage | 16/18 (88.89%) | 16/35 (45.71%) |
| medications unresolved code rate | 2/18 (11.11%) | 19/35 (54.29%) |
| investigations evidence exact offsets | 25/25 (100.00%) | 43/43 (100.00%) |
| investigations evidence/entity association | 25/25 (100.00%) | 43/43 (100.00%) |
| investigations standard-code coverage | 7/25 (28.00%) | 7/43 (16.28%) |
| investigations unresolved code rate | 18/25 (72.00%) | 36/43 (83.72%) |
| investigation timing | 0/1 (0.00%) | 1/1 (100.00%) |
| investigation assertion_status | 21/43 (48.84%) | 43/43 (100.00%) |


Standard-code coverage is not correct-mapping accuracy. No independent reference-code set exists; semantic correct mapping rate among assigned codes is NOT MEASURED. Unresolved rates can increase when valid uncoded mentions are recovered. Exact source offsets are verified independently, including in database reload tests.

## Uncertainty and interpretation

| 35-case cohort | Original F1 note-bootstrap interval | Integrated F1 note-bootstrap interval |
|---|---|---|
| medications | 84.21%–94.80% | 98.27%–100.00% |
| investigations | 91.60%–100.00% | 95.77%–100.00% |


Intervals use 2,000 deterministic note-cluster bootstrap resamples. They quantify variation across these synthetic notes conditional on unvalidated labels, not uncertainty in clinical deployment accuracy. The zero observed errors in the draft 47-case regression set do not establish perfect assertion accuracy or generalization. No independently validated Apollo neural confidence cutoff was available or selected.

Full per-case errors, per-class precision/recall, confusion matrices, code coverage, and ablation numerators are in `stage15-evidence/evaluation/metrics.json`. Reproduce with `Run-Apollo-Stage15-Verification.ps1` and `backend/tests/Evaluation/stage15-score.py`.
