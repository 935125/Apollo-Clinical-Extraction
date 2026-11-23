# Stage 6 failure inventory

Engineering triage, not independent clinical adjudication. Every evaluator failure, including attributes on missed entities, has a row in the linked JSON/CSV. Counts overlap at the entity level because one omission produces multiple attribute failures.

| Case | Original failures | After failures | Original primary causes |
|---|---:|---:|---|
| stage5-001-cardio-afib-multi | 4 | 0 | Incorrect attribute binding, Unsupported model capability |
| stage5-002-cardio-hf-baseline | 22 | 2 | Assertion classification error, Evaluation contract mismatch, Incorrect attribute binding, Missing source information, Unsupported model capability |
| stage5-003-cardio-acs-negated | 14 | 5 | Clinical scope error, Evaluation contract mismatch, Incorrect attribute binding, Normalization issue, Unsupported model capability |
| stage5-004-cardio-htn-titration | 5 | 0 | Assertion classification error, Incorrect attribute binding, Unsupported model capability |
| stage5-005-cardio-ntprobnp | 15 | 9 | Evaluation contract mismatch, Unsupported model capability |
| stage5-006-pulm-cap-admission-fever | 9 | 4 | Incorrect attribute binding, Unsupported model capability |
| stage5-007-pulm-asthma-inhaler | 8 | 6 | Evaluation contract mismatch, Incorrect attribute binding, Unsupported model capability |
| stage5-008-pulm-copd-iv | 21 | 4 | Evaluation contract mismatch, Incorrect attribute binding, Unsupported model capability |
| stage5-009-pulm-pe-anticoag | 17 | 12 | Evaluation contract mismatch, Incorrect attribute binding, Unsupported model capability |
| stage5-010-endo-dm2-titration | 14 | 10 | Assertion classification error, Evaluation contract mismatch, Incorrect attribute binding, Unsupported model capability |
| stage5-011-endo-hypothyroid-indian | 6 | 0 | Incorrect attribute binding, Unsupported model capability |
| stage5-012-endo-insulin-basal | 18 | 15 | Evaluation contract mismatch, Incorrect attribute binding, Unsupported model capability |
| stage5-013-endo-gout-allopurinol | 16 | 11 | Clinical scope error, Evaluation contract mismatch, Incorrect attribute binding, Unsupported model capability |
| stage5-014-peds-otitis-weight | 4 | 0 | Incorrect attribute binding, Normalization issue, Unsupported model capability |
| stage5-015-peds-croup-wheeze | 2 | 0 | Incorrect attribute binding, Unsupported model capability |
| stage5-016-peds-uti-culture | 12 | 8 | Evaluation contract mismatch, Incorrect attribute binding, Unsupported model capability |
| stage5-017-peds-epilepsy-neuro | 9 | 7 | Evaluation contract mismatch, Incorrect attribute binding, Unsupported model capability |
| stage5-018-onc-methotrexate-weekly | 5 | 0 | Incorrect attribute binding, Unsupported model capability |
| stage5-019-onc-qtc-interaction | 7 | 0 | Incorrect attribute binding, Unsupported model capability |
| stage5-020-onc-febrile-neutropenia | 17 | 6 | Evaluation contract mismatch, Incorrect attribute binding, Unsupported model capability |
| stage5-021-onc-prostate-psa | 11 | 0 | Evaluation contract mismatch, Incorrect attribute binding, Unsupported model capability |
| stage5-022-nephro-conflicting-scr | 22 | 4 | Assertion classification error, Clinical scope error, Evaluation contract mismatch, Incorrect attribute binding, Unsupported model capability |
| stage5-023-nephro-ckd-anemia | 11 | 5 | Assertion classification error, Incorrect attribute binding, Missing source information, Unsupported model capability |
| stage5-024-nephro-hypokalemia | 9 | 4 | Assertion classification error, Evaluation contract mismatch, Incorrect attribute binding, Missing source information, Unsupported model capability |
| stage5-025-nephro-nsaid-avoidance | 5 | 0 | Incorrect attribute binding, Unsupported model capability |
| stage5-026-neuro-stroke-secondary | 9 | 5 | Incorrect attribute binding, Normalization issue, Unsupported model capability |
| stage5-027-neuro-migraine-prn | 17 | 4 | Evaluation contract mismatch, Normalization issue |
| stage5-028-neuro-neuropathy-preg | 9 | 6 | Evaluation contract mismatch, Incorrect attribute binding, Unsupported model capability |
| stage5-029-neuro-seizure-titration | 5 | 0 | Assertion classification error, Incorrect attribute binding, Unsupported model capability |
| stage5-030-genmed-gerd-anemia | 8 | 8 | Unsupported model capability |
| stage5-031-genmed-uti-nitrofurantoin | 2 | 0 | Unsupported model capability |
| stage5-032-genmed-ophthalmic-postop | 4 | 4 | Unsupported model capability |
| stage5-033-genmed-cellulitis-allergy | 5 | 0 | Incorrect attribute binding, Unsupported model capability |
| stage5-034-genmed-multimorbid-polypharmacy | 7 | 0 | Incorrect attribute binding, Unsupported model capability |
| stage5-035-genmed-unresolved-oov | 13 | 11 | Evaluation contract mismatch, Incorrect attribute binding, Normalization issue, Unsupported model capability |
