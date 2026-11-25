# Stage 15 clinical safety regression

Observed critical unsafe-active classifications in the frozen annotated checks: 0. This count is conditional on the draft labels and covered cases; it is not a claim that the engine is clinically error-free.

Mandatory adversarial coverage includes aspirin allergy, negated ibuprofen, historical allopurinol, discontinued anticoagulation, completed antibiotic courses, conditional prescriptions, future follow-up tests, prior results, differing adjacent doses/routes, similar drug names, ambiguous brands, ambiguous abbreviations, mg/m2 regimens, educational text and hypothetical treatment plans. Separate challenge tests additionally cover trailing negation, `when` conditions, PRN wording, therapeutic salts versus analytes, laboratory concentrations, blast findings and chemotherapy-day linking.

Development failures were retained in command logs and repaired before verification. They included: `continuous` being interpreted as a continue verb; loss of a shared scheduling cue; pathological `blasts` leaking into a chemotherapy section; diagnostic thresholds becoming medication candidates; and a grammatical `administer 5 mL` phrase becoming a second drug candidate. Tests now lock down each safety boundary. They do not weaken privacy, Foundry evidence rejection or terminology specificity.

The strict 35-case score retains a false positive for the source-grounded, negated Penicillin mention omitted from old gold. The qualified chest-radiograph phrase produces a strict name mismatch. These remain errors in the published metric and are disclosed for adjudication. Neither is silently relabeled away.

New medication mentions are not automatically active orders. Completed, historical, allergic, negated, held, conditional, educational, monitoring and other-experiencer states remain explicit. Unknown codes never remove an otherwise defensible mention, and never become invented RxCUIs/LOINC identifiers. The local evidence gate checks exact quotes, entity offsets and attribute bounds; tampered evidence tests fail closed.

## Release decision

Engineering regression gate: PASS on the executed local checks. Clinical deployment clearance: BLOCKED on independent clinician adjudication, real-world validation and reference-code mapping assessment. Default configuration remains `Legacy`; `CandidateV2` is enabled only in the isolated evaluated test instance. Both versions remain available. No automatic historical re-extraction or schema-destructive migration is part of activation.

No GPT-equivalent accuracy, perfect assertion accuracy, comprehensive Indian-brand coverage or general clinical temporal reasoning is claimed. Finite catalogs, heuristic context, wrapped attributes and unfamiliar shorthand remain material limitations. New model candidates were researched, not substituted without measurement.
