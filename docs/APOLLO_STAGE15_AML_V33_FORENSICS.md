# Stage 15 original AML v33 forensic comparison

Identity: `P100234 / ENC-100567 / v33`, note `7b83e7bc-c817-4284-9306-8318b107adbb`, extraction `f0ee1284-6b16-4ad7-918e-6df369eccc34`. PostgreSQL marks the source record synthetic. Exact source SHA-256: `1CDB7B4312F0BCCD0A313B9F926D0D1F68FC1E94ACE7E86CE21CE7C34197F3A1`. A read-only post-verification query confirmed the same source hash and extraction ID. The original record was not resubmitted, signed again, replaced or overwritten.

The original persisted result contains Posaconazole and Ondansetron plus one historical CBC. Offline V2 re-evaluation is a separate synthetic forensic artifact, not an update to that historical record. The old Angular selection therefore still shows the old persisted result. A separate new synthetic note was processed through the real V2 provider, isolated PostgreSQL and Angular to verify display of actual persisted V2 entities.

| Medication | Literal source mentions | Original ONNX scores | Original filter outcome | Original output | V2 source-grounded assessment |
|---|---|---|---|---|---|
| Cytarabine | 2 | 0.9986, 0.9993 | NO_EXPLICIT_PRESCRIPTION_PREFIX, NO_EXPLICIT_PRESCRIPTION_PREFIX | Absent | status=historical; detail=historical; dose=100 mg/m2; route=IV; administration=continuous infusion; schedule=Days 1-7 / status=conditional; detail=planned |
| Daunorubicin | 1 | 0.9938 | NO_EXPLICIT_PRESCRIPTION_PREFIX | Absent | status=historical; detail=historical; dose=60 mg/m2; route=IV; administration=IV push; schedule=Days 1-3 |
| Levofloxacin | 1 | 0.9984 | NO_EXPLICIT_PRESCRIPTION_PREFIX | Absent | status=current_medication; detail=current; dose=500 mg; route=PO; frequency=QD |
| Acyclovir | 1 | Not detected | No neural candidate | Absent | status=current_medication; detail=current; dose=400 mg; route=PO; frequency=BID |
| Midostaurin | 1 | Not detected | No neural candidate | Absent | status=historical; detail=historical; dose=50 mg; route=PO; frequency=BID; schedule=Days 8-21 |
| Meropenem | 1 | Not detected | No neural candidate | Absent | status=historical; detail=completed_course; dose=1 g; route=IV; frequency=Q8H |
| Posaconazole | 1 | 0.9868 | Accepted | current_medication | status=current_medication; detail=current; dose=300 mg; route=PO; frequency=QD |
| Ondansetron | 1 | 0.9031 | Accepted | current_medication | status=current_medication; detail=current; dose=8 mg; route=PO; frequency=PRN |


Acyclovir, Midostaurin and Meropenem were present but not detected as medications by the original model. They cannot be described as high-scoring candidates rejected by the prefix gate. Cytarabine (both mentions), Daunorubicin and Levofloxacin were detected and rejected for the missing prefix. V2 catalog/clinical recognition recovers missed mentions without fabricating NER scores. The two Cytarabine mentions remain separate: administered induction treatment versus future consolidation planning.

## Investigations

| Audit target | Actually in source | Original persisted/core output | V2 offline result |
|---|---|---|---|
| bone marrow aspirate | Yes | Absent | bone marrow aspirate: historical / result_documented |
| bone marrow biopsy | Yes | Absent | bone marrow biopsy: historical / result_documented |
| Blood cultures | Yes | Absent | Blood cultures: historical / result_documented |
| Cytogenetics | Yes | Absent | Cytogenetics: historical / result_documented |
| CBC | Yes | historical | CBC: new_order / scheduled |
| renal function panel | No separate literal phrase; combined renal/liver panel present | Absent | renal/liver function panel: new_order / scheduled |
| liver function panel | Within combined renal/liver phrase | Absent | renal/liver function panel: new_order / scheduled |


BC5CDR has no investigation output label; investigation detection here is attributed to rules/catalogs, not to disease/chemical overlap. Platelets and Hemoglobin are additionally recognized as documented hospital-course results. `renal/liver function panel` stays one exact source phrase with unresolved terminology, not two invented quotations or specific panel codes.

The direct CBC root cause is the legacy whole-line numeric-result regex: `09:00 AM` satisfies its colon-plus-number pattern; the historical ternary then wins because the appointment has no narrow order verb. Missing follow-up section recognition contributes. This is not evidence that an absolute appointment date is inherently historical, nor solely an ONNX/ContextEngine performance problem.

V2 emits 10 medication mentions and 8 investigation mentions in this offline assessment. No mention is assumed to be a new prescription simply because it was recovered. Benzydamine is a historical hospital-course mention; the falsely labeled neural `blasts` candidate is rejected as a pathology finding. Dose/route, method and chemotherapy days are source-local; no BSA-derived milligrams or undocumented regimen days are added.

Exact quote/mention/attribute offsets are validated before a local provider result is accepted. Standard identifiers for newly recognized oncology terms remain unresolved. The detailed per-candidate before/after traces and output are in `baseline/real-local-outputs.json`, `evaluation/integrated.json` and `evaluation/integrated-adapter.json`. The historical PostgreSQL export is `baseline/aml-v33-historical.json`.

Storage/display distinction: the original v33 remains unchanged in the application database/UI. The new browser fixture is a fresh synthetic note in `cx_audit_test`; its saved output and screenshot are in `playwright/stage15-local-onnx-results.json` and `stage15-real-persisted-result.png`. This avoids replaying historical signed events to improve the visible result.
