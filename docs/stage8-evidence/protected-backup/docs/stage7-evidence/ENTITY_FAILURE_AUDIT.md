# Original entity failure audit

Engineering annotations, not independent clinical gold. Full source/raw/final API results and safety rationale are in original-36-entity-failures.json.

| Case | Type | Kind | Entity | Exact cause | Still fails |
|---|---|---|---|---|---|
| stage5-003-cardio-acs-negated | INVESTIGATION | FN | Troponin I | CATALOG_TRUNCATED_SOURCE_QUALIFIERS | False |
| stage5-003-cardio-acs-negated | INVESTIGATION | FP | Troponin | CATALOG_TRUNCATED_SOURCE_QUALIFIERS | False |
| stage5-005-cardio-ntprobnp | INVESTIGATION | FN | NT-proBNP | CATALOG_ENTITY_OMISSION | True |
| stage5-005-cardio-ntprobnp | INVESTIGATION | FN | Chest X-ray | FROZEN_MATCHING_CONTRACT_DIFFERS_ON_DOCUMENTED_PA_VIEW | True |
| stage5-005-cardio-ntprobnp | INVESTIGATION | FP | Chest X-ray (PA view) | FROZEN_MATCHING_CONTRACT_DIFFERS_ON_DOCUMENTED_PA_VIEW | True |
| stage5-006-pulm-cap-admission-fever | INVESTIGATION | FN | Sputum culture | CATALOG_ENTITY_OMISSION | False |
| stage5-007-pulm-asthma-inhaler | MEDICATION | FN | Montelukast | RAW_MODEL_OMISSION_AND_NO_CATALOG_RECOVERY | True |
| stage5-008-pulm-copd-iv | MEDICATION | FN | Theophylline | NO_EXPLICIT_PRESCRIPTION_PREFIX | True |
| stage5-009-pulm-pe-anticoag | MEDICATION | FN | Enoxaparin | BELOW_NEURAL_CONFIDENCE_THRESHOLD | True |
| stage5-009-pulm-pe-anticoag | MEDICATION | FN | Apixaban | RAW_MODEL_OMISSION_AND_NO_CATALOG_RECOVERY | True |
| stage5-010-endo-dm2-titration | MEDICATION | FN | Glimepiride | BELOW_NEURAL_CONFIDENCE_THRESHOLD | True |
| stage5-010-endo-dm2-titration | INVESTIGATION | FN | Fasting Blood Sugar | CATALOG_ENTITY_OMISSION | False |
| stage5-012-endo-insulin-basal | MEDICATION | FN | Insulin Glargine | RAW_MODEL_OMISSION_AND_NO_CATALOG_RECOVERY | True |
| stage5-012-endo-insulin-basal | MEDICATION | FN | Sitagliptin | RAW_MODEL_OMISSION_AND_NO_CATALOG_RECOVERY | True |
| stage5-012-endo-insulin-basal | INVESTIGATION | FN | Fasting Blood Sugar | CATALOG_ENTITY_OMISSION | False |
| stage5-013-endo-gout-allopurinol | MEDICATION | FN | Colchicine | RAW_MODEL_OMISSION_AND_NO_CATALOG_RECOVERY | True |
| stage5-013-endo-gout-allopurinol | MEDICATION | FN | Febuxostat | RAW_MODEL_OMISSION_AND_NO_CATALOG_RECOVERY | True |
| stage5-013-endo-gout-allopurinol | INVESTIGATION | FP | uric acid | CONDITIONAL_THRESHOLD_IS_NOT_A_TEST_ORDER | False |
| stage5-016-peds-uti-culture | MEDICATION | FN | Cefixime | BELOW_NEURAL_CONFIDENCE_THRESHOLD | True |
| stage5-017-peds-epilepsy-neuro | MEDICATION | FN | Cholecalciferol | RAW_MODEL_OMISSION_AND_NO_CATALOG_RECOVERY | True |
| stage5-020-onc-febrile-neutropenia | MEDICATION | FN | Filgrastim | RAW_MODEL_OMISSION_AND_NO_CATALOG_RECOVERY | True |
| stage5-022-nephro-conflicting-scr | MEDICATION | FN | Ramipril | BELOW_NEURAL_CONFIDENCE_THRESHOLD | True |
| stage5-022-nephro-conflicting-scr | INVESTIGATION | FP | potassium | CONDITIONAL_THRESHOLD_IS_NOT_A_TEST_ORDER | False |
| stage5-023-nephro-ckd-anemia | INVESTIGATION | FN | Hemoglobin | CATALOG_ENTITY_OMISSION | True |
| stage5-024-nephro-hypokalemia | MEDICATION | FN | Hydrochlorothiazide | BELOW_NEURAL_CONFIDENCE_THRESHOLD | True |
| stage5-026-neuro-stroke-secondary | INVESTIGATION | FN | MRI Brain | CATALOG_TRUNCATED_SOURCE_QUALIFIERS | False |
| stage5-026-neuro-stroke-secondary | INVESTIGATION | FP | MRI | CATALOG_TRUNCATED_SOURCE_QUALIFIERS | False |
| stage5-027-neuro-migraine-prn | INVESTIGATION | FN | CT Head | CATALOG_TRUNCATED_SOURCE_QUALIFIERS | False |
| stage5-027-neuro-migraine-prn | INVESTIGATION | FP | CT | CATALOG_TRUNCATED_SOURCE_QUALIFIERS | False |
| stage5-028-neuro-neuropathy-preg | MEDICATION | FN | Pregabalin | RAW_MODEL_OMISSION_AND_NO_CATALOG_RECOVERY | True |
| stage5-030-genmed-gerd-anemia | INVESTIGATION | FN | Hemoglobin | CATALOG_ENTITY_OMISSION | True |
| stage5-030-genmed-gerd-anemia | INVESTIGATION | FN | Platelet count | CATALOG_ENTITY_OMISSION | False |
| stage5-032-genmed-ophthalmic-postop | INVESTIGATION | FN | Fasting Blood Sugar | CATALOG_ENTITY_OMISSION | False |
| stage5-035-genmed-unresolved-oov | MEDICATION | FN | Pirfenidone | BELOW_NEURAL_CONFIDENCE_THRESHOLD | True |
| stage5-035-genmed-unresolved-oov | INVESTIGATION | FN | High-resolution CT Thorax | CATALOG_TRUNCATED_SOURCE_QUALIFIERS | False |
| stage5-035-genmed-unresolved-oov | INVESTIGATION | FP | CT | CATALOG_TRUNCATED_SOURCE_QUALIFIERS | False |
