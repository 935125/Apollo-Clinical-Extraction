# Stage 15 final evaluation report

25 November 2026. A modular `CandidateV2` local pipeline is implemented and engineering-verified. Foundry remains the primary provider; the checked-in local default remains `Legacy`. No new billable Foundry inference was made. Clinical activation remains blocked on independent clinical adjudication and reference-code validation.

## A. Root causes

The frozen original `ClinicalPipelineService.cs` rejects unmatched neural medication candidates at lines 157–160 unless both the 0.85 threshold and a narrow prescription-prefix regex match. High chemical confidence alone is not medication intent. Section guards at 91/149 also suppress candidates. CBC becomes historical at 434–438 because `09:00 AM` matches a whole-line result-value regex and no narrow order verb is present. Dedicated investigation coverage and follow-up sections were incomplete. PostgreSQL reload also reconstructed some provenance instead of retaining the accepted result. [Exact audit and corrected source references](APOLLO_STAGE15_LOCAL_ONNX_ROOT_CAUSE_AUDIT.md).

## B. Model capability

The deployed artifact is a small INT8 BiLSTM trained for BC5CDR chemical/disease recognition, with renamed labels; it is not a clinical order, investigation or relation model. Unused label slots do not establish trained dosage/anatomy capability. Existing model/tokenizer hashes remain pinned. V2 changes clinical processing and complete window construction, not model weights. Candidate replacements were researched using primary model cards; their local accuracy, export, RAM and CPU performance remain unmeasured. A training-manifest tokenizer hash discrepancy remains unresolved. [Model assessment](APOLLO_STAGE15_LOCAL_MODEL_ASSESSMENT.md).

## C. Implementation

New components cover sections, medication candidates, investigations, assertions, source-local attributes/relations, versioned terminology and evidence validation. Fine assertions and raw model scores are additive context; actual provider metadata survives persistence. Unknown standard codes preserve mentions. The Angular table uses saved context. [Architecture](APOLLO_STAGE15_CLINICAL_EXTRACTION_ARCHITECTURE.md), [complete source change list, commands and rollback](APOLLO_STAGE15_IMPLEMENTATION_AND_VERIFICATION.md), [source/evidence hashes](stage15-evidence/implementation-manifest.json).

## D. Medication evaluation

The unchanged historical 35-case synthetic benchmark measured:

| Version | TP / FP / FN | Precision | Recall | F1 |
|---|---|---|---|---|
| Original | 72 / 0 / 16 | 100.00% | 81.82% | 90.00% |
| CandidateV2 | 88 / 1 / 0 | 98.88% | 100.00% | 99.44% |

Annotated medication-to-attribute value accuracy improved from 287/342 to 327/342. Dose improved 60/69→69/69, route 67/79→79/79, frequency 67/81→81/81 and duration 21/25→25/25. Strength remains 73/88: several original labels conflate administered dose and product strength. The source-grounded negated Penicillin mention omitted from old gold remains a strict false positive. No labels were edited to remove errors.

The additional 47 source-authored draft cases measured medication F1 67.92%→100% (35 TP, no FP/FN after). They were used during development and are **not independent held-out clinical evidence**. [Counts, attributes, ablations, bootstrap intervals and all mismatches](APOLLO_STAGE15_BASELINE_VS_UPGRADED_BENCHMARK.md).

## E. Investigation evaluation

| Version, original 35 cases | TP / FP / FN | Precision | Recall | F1 |
|---|---|---|---|---|
| Original | 75 / 1 / 4 | 98.68% | 94.94% | 96.77% |
| CandidateV2 | 78 / 1 / 1 | 98.73% | 98.73% | 98.73% |

The qualified `Chest X-ray (PA view)` versus shorter gold name remains an FP/FN pair under strict scoring. Additional 47-case investigation F1 improved 67.65%→100% (43 TP). Source-specific specimen omissions remain; broad tests are not assigned more specific codes without support. [Investigation design](APOLLO_STAGE15_INVESTIGATION_EXTRACTION_REDESIGN.md) and [measured benchmark](APOLLO_STAGE15_BASELINE_VS_UPGRADED_BENCHMARK.md).

## F. Assertions

The 47 draft cases improved coarse assertion checks from 13/35→35/35 medications and 21/43→43/43 investigations. Fine states include scheduled, planned, result documented, completed course, continued, held and other experiencer. The original 35-case investigation assertion result is 78/79; the strict radiograph mismatch remains missed. Missing entities are included in confusion matrices. These development results do not establish perfect clinical assertion accuracy. [Before/after matrices and per-class precision/recall](APOLLO_STAGE15_ASSERTION_AND_TEMPORALITY.md).

## G. Clinical safety

Zero critical unsafe-active classifications were observed in the annotated frozen checks. All 246 entities emitted across the 82 scored notes had exact source evidence and entity association. Separate adversarial tests cover allergy/negation/history, conditional/educational text, laboratory analytes versus therapeutic salts, non-drug pathology terms, adjacent attribute isolation, mathematical doses, chemotherapy days and unsupported ingredient normalization. Prednisolone acetate cannot silently inherit the base ingredient code. Independent mapping accuracy is not measured. [Safety report](APOLLO_STAGE15_CLINICAL_SAFETY_REGRESSION.md) and [terminology limitations](APOLLO_STAGE15_TERMINOLOGY_NORMALIZATION.md).

## H. Original AML v33

The original synthetic P100234 / ENC-100567 / v33 remains unchanged: two saved medications and one historical CBC. In the original raw inference, Cytarabine, Daunorubicin and Levofloxacin were detected and filtered; Acyclovir, Midostaurin and Meropenem were not detected. V2 offline evaluation yields ten medication mentions and eight investigations, preserving four current discharge medicines, historical induction/completed treatment and separate planned Cytarabine. CBC and the exact combined `renal/liver function panel` are scheduled. No separate nonexistent source quote or BSA-derived dose is invented. [Full medication-by-medication trace and source identity](APOLLO_STAGE15_AML_V33_FORENSICS.md). The new browser screenshot represents a fresh isolated fixture, not an overwritten v33 result.

## I. Performance

On the actual Windows 11 / i5-13420H / 16 GB CPU machine, 30 warm AML runs averaged 21.32 ms Legacy and 23.17 ms V2; V2 p95 was 27.24 ms and mean neural Run time 9.06 ms. Overall testhost peak working set was 162.71 MiB, including .NET/xUnit and multiple sessions. Fresh-session initialization/first extraction, long notes, CPU use, concurrency and independent PostgreSQL timings are recorded. Small shared-runtime measurements do not establish production capacity or a general speed difference. [Measured performance and scope](APOLLO_STAGE15_PERFORMANCE_RESULTS.md).

## J. Provider compatibility and historical preservation

Release routing diagnostics show MicrosoftFoundry primary, LocalOnnx fallback, local Legacy default and no provider invocation. Offline/mocked Foundry contracts remain covered. The new persisted browser flow uses real ONNX, real PostgreSQL and simulated messaging; it does not verify a real Azure broker. Read-only inspection found original application counts unchanged at 41 Foundry and 31 ONNX extractions, with the original AML source hash/extraction identity intact. Nine saved synthetic Foundry outputs were supplementary comparisons, never gold labels. Existing privacy, CSRF, authorization and artifact-integrity gates remain active.

## K. Test results

PASS: backend build (0 warnings/errors), backend suite (305 passed, 13 skipped, 0 failed), explicit ablation (1 passed), Angular (55 passed and production build), Postman (34 requests/41 assertions), new real-local browser flow (19 checks), existing provenance/page-size/top-ten browser checks, and Development Swagger/OpenAPI endpoints. Final executed engineering failures: none. Earlier development failures remain visible in retained logs. All 35 frozen original source snapshots and both dataset hashes were verified unchanged; 54 current source/fixture/tool hashes are recorded, and tested/served assemblies match.

BLOCKED: independent clinical clearance and reference-code adjudication. NOT TESTED: new live Foundry inference, shared real Azure Service Bus smoke, replacement-model benchmarking and obsolete single-row queue assumptions. Skips are itemized rather than counted as passes. [Status table and exact commands](APOLLO_STAGE15_IMPLEMENTATION_AND_VERIFICATION.md#executed-verification).

## L. Remaining limitations and readiness

The 35 historical and 47 new cases are finite engineering-authored synthetic development sets; neither is clinician-adjudicated. No calibrated decision threshold, comprehensive Indian brand/salt mapping, general temporal reasoning or independently measured relation/code accuracy is claimed. Wrapped multiline regimens, unfamiliar shorthand and finite terminology remain risks. The model-training tokenizer manifest requires reconciliation. Replacement models need a new adapter, licensing/data review and real local evaluation before selection.

The implementation is available for controlled engineering evaluation with `LocalOnnx__PipelineVersion=CandidateV2`. Keep Legacy as the default until independent evidence supports activation. Existing historical data does not need rewriting, and no paid model call is needed to reproduce the local evidence.
