"""
Held-out Evaluation Dataset for Clinical AI Extraction.
Contains 20 previously unseen, highly realistic clinical narratives spanning diverse specialties.
Not used in prompt engineering, adapter design, or validation heuristic tuning.
"""

from dataclasses import dataclass
from typing import Any


@dataclass
class HeldOutGroundTruth:
    id: str
    specialty: str
    note_text: str
    expected_medications: list[dict[str, Any]]
    expected_investigations: list[dict[str, Any]]


def build_held_out_dataset() -> list[HeldOutGroundTruth]:
    cases: list[HeldOutGroundTruth] = []

    # 1. Geriatric Polypharmacy & Renal Adjustment
    cases.append(HeldOutGroundTruth(
        id="heldout-001-geriatric",
        specialty="Geriatrics / Nephrology",
        note_text="""Reason for Visit: 78-year-old male with CKD stage 3b and gout for quarterly review.
Current Meds: Allopurinol 100mg PO daily, Lisinopril 10mg daily.
Assessment: Stable eGFR of 38 mL/min. Recurrent podagra attack last week.
Plan:
1. Increase Allopurinol to 200mg PO daily after meals.
2. Hold Lisinopril 10mg if systolic blood pressure drops below 110 mmHg.
3. Order Serum Creatinine and Serum Electrolytes in 2 weeks.
4. Order Serum Uric Acid.""",
        expected_medications=[
            {"name": "Allopurinol", "strength": "200mg", "route": "PO", "frequency": "daily", "assertion_status": "new_order"},
            {"name": "Lisinopril", "strength": "10mg", "route": None, "frequency": "daily", "assertion_status": "conditional"},
        ],
        expected_investigations=[
            {"name": "Serum Creatinine", "assertion_status": "new_order"},
            {"name": "Serum Electrolytes", "assertion_status": "new_order"},
            {"name": "Serum Uric Acid", "assertion_status": "new_order"},
        ]
    ))

    # 2. Post-PCI Acute Coronary Syndrome
    cases.append(HeldOutGroundTruth(
        id="heldout-002-cardiology",
        specialty="Cardiology",
        note_text="""Subjective: 61-year-old female status-post DES placement to LAD 2 weeks ago.
Allergies: Penicillin (severe urticaria). Denies Aspirin allergy.
Medications: Metformin 500mg BD.
Plan:
1. Prescribe Aspirin 100mg PO OD after food for 12 months.
2. Prescribe Atorvastatin 40mg PO daily at night.
3. Order Fasting Lipid Profile in 6 weeks.
4. Order Complete Blood Count (CBC).
5. 2D Echocardiogram in 3 months to re-evaluate LVEF.""",
        expected_medications=[
            {"name": "Aspirin", "strength": "100mg", "route": "PO", "frequency": "OD", "duration": "12 months", "assertion_status": "new_order"},
            {"name": "Atorvastatin", "strength": "40mg", "route": "PO", "frequency": "daily", "assertion_status": "new_order"},
            {"name": "Metformin", "strength": "500mg", "route": None, "frequency": "BD", "assertion_status": "current_medication"},
        ],
        expected_investigations=[
            {"name": "Lipid Profile", "assertion_status": "new_order"},
            {"name": "Complete Blood Count", "assertion_status": "new_order"},
            {"name": "2D Echocardiogram", "assertion_status": "new_order"},
        ]
    ))

    # 3. Anticoagulation Bridging & Discontinuation
    cases.append(HeldOutGroundTruth(
        id="heldout-003-hematology",
        specialty="Hematology",
        note_text="""History: 69-year-old male on long-term anticoagulation for mechanical mitral valve presenting before elective colonoscopy.
Medication Change: Discontinue Warfarin 5mg 5 days prior to procedure.
Plan:
1. Prescribe Paracetamol 500mg PO PRN for tension headache; avoid NSAIDs like Ibuprofen.
2. Check INR on morning of procedure.
3. Order Complete Blood Count today.""",
        expected_medications=[
            {"name": "Warfarin", "strength": "5mg", "assertion_status": "discontinued"},
            {"name": "Paracetamol", "strength": "500mg", "route": "PO", "frequency": "PRN", "assertion_status": "new_order"},
            {"name": "Ibuprofen", "strength": None, "assertion_status": "negated"},
        ],
        expected_investigations=[
            {"name": "INR", "assertion_status": "new_order"},
            {"name": "Complete Blood Count", "assertion_status": "new_order"},
        ]
    ))

    # 4. Endocrinology / T2D Titration
    cases.append(HeldOutGroundTruth(
        id="heldout-004-endocrinology",
        specialty="Endocrinology",
        note_text="""Impression: Suboptimally controlled Type 2 Diabetes with microalbuminuria. Current HbA1c 8.6%.
Past Meds: Patient previously took Metformin 500mg BD, ceased last year due to severe bloating.
Plan:
1. Start Empagliflozin 10mg PO OD in the morning.
2. Start Linagliptin 5mg PO daily.
3. Order HbA1c and Serum Creatinine in 3 months.
4. Order Urinalysis for albumin-creatinine ratio.""",
        expected_medications=[
            {"name": "Empagliflozin", "strength": "10mg", "route": "PO", "frequency": "OD", "assertion_status": "new_order"},
            {"name": "Linagliptin", "strength": "5mg", "route": "PO", "frequency": "daily", "assertion_status": "new_order"},
            {"name": "Metformin", "strength": "500mg", "route": None, "frequency": "BD", "assertion_status": "historical"},
        ],
        expected_investigations=[
            {"name": "HbA1c", "assertion_status": "new_order"},
            {"name": "Serum Creatinine", "assertion_status": "new_order"},
            {"name": "Urinalysis", "assertion_status": "new_order"},
        ]
    ))

    # 5. Pulmonology / Acute Asthma Exacerbation
    cases.append(HeldOutGroundTruth(
        id="heldout-005-pulmonology",
        specialty="Pulmonology",
        note_text="""Exam: Bilateral expiratory wheezes, peak flow 65% of predicted.
Plan:
1. Prescribe Prednisolone 40mg PO OD for 5 days.
2. Salbutamol 100mcg 2 puffs inhaled QDS PRN for bronchospasm.
3. Order Chest X-ray PA view to rule out secondary consolidation.""",
        expected_medications=[
            {"name": "Prednisolone", "strength": "40mg", "route": "PO", "frequency": "OD", "duration": "5 days", "assertion_status": "new_order"},
            {"name": "Salbutamol", "strength": "100mcg", "route": "inhaled", "frequency": "QDS", "assertion_status": "new_order"},
        ],
        expected_investigations=[
            {"name": "Chest X-ray", "assertion_status": "new_order"},
        ]
    ))

    # 6. Rheumatology / Methotrexate Initiation
    cases.append(HeldOutGroundTruth(
        id="heldout-006-rheumatology",
        specialty="Rheumatology",
        note_text="""Diagnosis: Seropositive Rheumatoid Arthritis with bilateral MCP synovitis.
Plan:
1. Prescribe Methotrexate 15mg PO once weekly on Sundays.
2. Prescribe Folic Acid 5mg PO once weekly on Tuesdays (never on Methotrexate day).
3. Order baseline Liver Function Tests (LFT) and Complete Blood Count.
4. Repeat Serum Creatinine in 4 weeks.""",
        expected_medications=[
            {"name": "Methotrexate", "strength": "15mg", "route": "PO", "frequency": "weekly", "assertion_status": "new_order"},
            {"name": "Folic Acid", "strength": "5mg", "route": "PO", "frequency": "weekly", "assertion_status": "new_order"},
        ],
        expected_investigations=[
            {"name": "Liver Function Tests", "assertion_status": "new_order"},
            {"name": "Complete Blood Count", "assertion_status": "new_order"},
            {"name": "Serum Creatinine", "assertion_status": "new_order"},
        ]
    ))

    # 7. Gastroenterology / Peptic Ulcer Disease
    cases.append(HeldOutGroundTruth(
        id="heldout-007-gastroenterology",
        specialty="Gastroenterology",
        note_text="""Subjective: Endoscopy confirmed benign antral gastric ulcer. Positive rapid urease test.
Plan:
1. Prescribe Omeprazole 20mg PO BD 30 minutes before meals for 14 days.
2. Prescribe Amoxicillin 1000mg PO BD for 14 days.
3. Prescribe Clarithromycin 500mg PO BD for 14 days.
4. Order Complete Blood Count in 4 weeks to check hemoglobin stability.""",
        expected_medications=[
            {"name": "Omeprazole", "strength": "20mg", "route": "PO", "frequency": "BD", "duration": "14 days", "assertion_status": "new_order"},
            {"name": "Amoxicillin", "strength": "1000mg", "route": "PO", "frequency": "BD", "duration": "14 days", "assertion_status": "new_order"},
            {"name": "Clarithromycin", "strength": "500mg", "route": "PO", "frequency": "BD", "duration": "14 days", "assertion_status": "new_order"},
        ],
        expected_investigations=[
            {"name": "Complete Blood Count", "assertion_status": "new_order"},
        ]
    ))

    # 8. Infectious Disease / Cellulitis Oral Stepdown
    cases.append(HeldOutGroundTruth(
        id="heldout-008-infectious-disease",
        specialty="Infectious Disease",
        note_text="""Assessment: Resolving right lower extremity cellulitis following 48 hours of IV cefazolin.
Plan:
1. Step down to Cephalexin 500mg PO QID to complete a 10-day total course.
2. Paracetamol 650mg PO TDS PRN for local tenderness.
3. Order Complete Blood Count (CBC) and Erythrocyte Sedimentation Rate (ESR) if erythema spreads.""",
        expected_medications=[
            {"name": "Cephalexin", "strength": "500mg", "route": "PO", "frequency": "QID", "assertion_status": "new_order"},
            {"name": "Paracetamol", "strength": "650mg", "route": "PO", "frequency": "TDS", "assertion_status": "new_order"},
        ],
        expected_investigations=[
            {"name": "Complete Blood Count", "assertion_status": "conditional"},
            {"name": "ESR", "assertion_status": "conditional"},
        ]
    ))

    # 9. Neurology / Focal Epilepsy
    cases.append(HeldOutGroundTruth(
        id="heldout-009-neurology",
        specialty="Neurology",
        note_text="""Impression: Recurrent focal seizures with impaired awareness.
Current Therapy: Levetiracetam 500mg BD.
Plan:
1. Increase Levetiracetam to 750mg PO BD.
2. Order baseline Complete Blood Count and Liver Function Tests.
3. Order routine 30-minute EEG.""",
        expected_medications=[
            {"name": "Levetiracetam", "strength": "750mg", "route": "PO", "frequency": "BD", "assertion_status": "new_order"},
            {"name": "Levetiracetam", "strength": "500mg", "route": None, "frequency": "BD", "assertion_status": "current_medication"},
        ],
        expected_investigations=[
            {"name": "Complete Blood Count", "assertion_status": "new_order"},
            {"name": "Liver Function Tests", "assertion_status": "new_order"},
            {"name": "EEG", "assertion_status": "new_order"},
        ]
    ))

    # 10. Nephrology / CKD Stage 4
    cases.append(HeldOutGroundTruth(
        id="heldout-010-nephrology",
        specialty="Nephrology",
        note_text="""Assessment: CKD stage 4 (eGFR 24 mL/min). Serum potassium 5.4 mEq/L.
Medication Changes:
1. Discontinue Lisinopril 20mg immediately due to progressive hyperkalemia.
Plan:
1. Start Amlodipine 5mg PO daily for blood pressure control.
2. Sodium Bicarbonate 650mg PO BD for metabolic acidosis.
3. Order Serum Electrolytes and Serum Creatinine in 7 days.
4. Order 12-lead ECG.""",
        expected_medications=[
            {"name": "Lisinopril", "strength": "20mg", "assertion_status": "discontinued"},
            {"name": "Amlodipine", "strength": "5mg", "route": "PO", "frequency": "daily", "assertion_status": "new_order"},
            {"name": "Sodium Bicarbonate", "strength": "650mg", "route": "PO", "frequency": "BD", "assertion_status": "new_order"},
        ],
        expected_investigations=[
            {"name": "Serum Electrolytes", "assertion_status": "new_order"},
            {"name": "Serum Creatinine", "assertion_status": "new_order"},
            {"name": "ECG", "assertion_status": "new_order"},
        ]
    ))

    # 11. Oncology Supportive Care
    cases.append(HeldOutGroundTruth(
        id="heldout-011-oncology",
        specialty="Medical Oncology",
        note_text="""Chief Complaint: Cycle 2 adjuvant chemotherapy review for breast adenocarcinoma.
Allergies: NKDA.
Plan:
1. Prescribe Ondansetron 8mg PO BD for 3 days post-chemo.
2. Dexamethasone 4mg PO daily for 3 days.
3. Metoclopramide 10mg PO TDS PRN for breakthrough nausea.
4. Order Complete Blood Count (CBC) on day 10 to check absolute neutrophil count.""",
        expected_medications=[
            {"name": "Ondansetron", "strength": "8mg", "route": "PO", "frequency": "BD", "duration": "3 days", "assertion_status": "new_order"},
            {"name": "Dexamethasone", "strength": "4mg", "route": "PO", "frequency": "daily", "duration": "3 days", "assertion_status": "new_order"},
            {"name": "Metoclopramide", "strength": "10mg", "route": "PO", "frequency": "TDS", "assertion_status": "new_order"},
        ],
        expected_investigations=[
            {"name": "Complete Blood Count", "assertion_status": "new_order"},
        ]
    ))

    # 12. Cardiology / Heart Failure Quad Therapy
    cases.append(HeldOutGroundTruth(
        id="heldout-012-hfref",
        specialty="Heart Failure Clinic",
        note_text="""Impression: Non-ischemic dilated cardiomyopathy with NYHA Class II symptoms. EF 32%.
Plan:
1. Initiate Sacubitril/Valsartan 24/26mg PO BD.
2. Start Dapagliflozin 10mg PO daily.
3. Continue Bisoprolol 5mg PO daily.
4. Spironolactone 25mg PO daily.
5. Order Serum Electrolytes and Serum Creatinine in 14 days.""",
        expected_medications=[
            {"name": "Sacubitril/Valsartan", "strength": "24/26mg", "route": "PO", "frequency": "BD", "assertion_status": "new_order"},
            {"name": "Dapagliflozin", "strength": "10mg", "route": "PO", "frequency": "daily", "assertion_status": "new_order"},
            {"name": "Bisoprolol", "strength": "5mg", "route": "PO", "frequency": "daily", "assertion_status": "new_order"},
            {"name": "Spironolactone", "strength": "25mg", "route": "PO", "frequency": "daily", "assertion_status": "new_order"},
        ],
        expected_investigations=[
            {"name": "Serum Electrolytes", "assertion_status": "new_order"},
            {"name": "Serum Creatinine", "assertion_status": "new_order"},
        ]
    ))

    # 13. Dermatology / Biologic Screening
    cases.append(HeldOutGroundTruth(
        id="heldout-013-dermatology",
        specialty="Dermatology",
        note_text="""Assessment: Severe plaque psoriasis involving 18% BSA unresponsive to topical betamethasone.
Plan:
1. Screen for latent infections prior to initiating adalimumab therapy.
2. Order Quantiferon-TB Gold.
3. Order Hepatitis B Surface Antigen and Hepatitis C Antibody.
4. Order Complete Blood Count and Liver Function Tests.""",
        expected_medications=[
            {"name": "Betamethasone", "strength": None, "assertion_status": "historical"},
            {"name": "Adalimumab", "strength": None, "assertion_status": "conditional"},
        ],
        expected_investigations=[
            {"name": "Quantiferon-TB Gold", "assertion_status": "new_order"},
            {"name": "Hepatitis B Surface Antigen", "assertion_status": "new_order"},
            {"name": "Hepatitis C Antibody", "assertion_status": "new_order"},
            {"name": "Complete Blood Count", "assertion_status": "new_order"},
            {"name": "Liver Function Tests", "assertion_status": "new_order"},
        ]
    ))

    # 14. Psychiatry / Antidepressant Cross-Taper
    cases.append(HeldOutGroundTruth(
        id="heldout-014-psychiatry",
        specialty="Psychiatry",
        note_text="""Impression: Major depressive episode with partial response and excessive sedation on mirtazapine.
Plan:
1. Taper Mirtazapine 15mg to 7.5mg PO at night for 7 days, then cease.
2. Initiate Sertraline 50mg PO every morning with food.
3. Order baseline 12-lead ECG to evaluate QTc interval.""",
        expected_medications=[
            {"name": "Mirtazapine", "strength": "7.5mg", "route": "PO", "frequency": "night", "duration": "7 days", "assertion_status": "discontinued"},
            {"name": "Sertraline", "strength": "50mg", "route": "PO", "frequency": "morning", "assertion_status": "new_order"},
        ],
        expected_investigations=[
            {"name": "ECG", "assertion_status": "new_order"},
        ]
    ))

    # 15. Pain Medicine / Neuropathic Pain
    cases.append(HeldOutGroundTruth(
        id="heldout-015-pain",
        specialty="Pain Medicine",
        note_text="""Subjective: 54-year-old female with persistent post-herpetic neuralgia in T5 distribution.
Plan:
1. Prescribe Gabapentin 300mg PO at bedtime for 3 days, then titrate to 300mg BD.
2. Prescribe Paracetamol 650mg PO SOS.
3. Order Serum Creatinine prior to dose escalation above 900mg/day.""",
        expected_medications=[
            {"name": "Gabapentin", "strength": "300mg", "route": "PO", "frequency": "bedtime", "assertion_status": "new_order"},
            {"name": "Paracetamol", "strength": "650mg", "route": "PO", "frequency": "SOS", "assertion_status": "new_order"},
        ],
        expected_investigations=[
            {"name": "Serum Creatinine", "assertion_status": "conditional"},
        ]
    ))

    # 16. Urology / BPH
    cases.append(HeldOutGroundTruth(
        id="heldout-016-urology",
        specialty="Urology",
        note_text="""Assessment: Lower urinary tract symptoms secondary to benign prostatic hyperplasia. IPSS score 22.
Plan:
1. Prescribe Tamsulosin 0.4mg PO daily after breakfast.
2. Prescribe Finasteride 5mg PO daily.
3. Order Serum PSA (Prostate Specific Antigen) today.
4. Order Ultrasound Kidneys, Ureters, and Bladder with post-void residual.""",
        expected_medications=[
            {"name": "Tamsulosin", "strength": "0.4mg", "route": "PO", "frequency": "daily", "assertion_status": "new_order"},
            {"name": "Finasteride", "strength": "5mg", "route": "PO", "frequency": "daily", "assertion_status": "new_order"},
        ],
        expected_investigations=[
            {"name": "Serum PSA", "assertion_status": "new_order"},
            {"name": "Ultrasound", "assertion_status": "new_order"},
        ]
    ))

    # 17. Pediatric / Acute Otitis Media
    cases.append(HeldOutGroundTruth(
        id="heldout-017-pediatrics",
        specialty="Pediatrics",
        note_text="""Patient: 6-year-old child (weight 20 kg) with bulging erythematous left tympanic membrane.
Plan:
1. Prescribe Amoxicillin 800mg PO BD for 10 days (high-dose regimen 80 mg/kg/day).
2. Paracetamol oral suspension 300mg PO every 6 hours PRN for fever.
3. Review in clinic if fever persists beyond 48 hours.""",
        expected_medications=[
            {"name": "Amoxicillin", "strength": "800mg", "route": "PO", "frequency": "BD", "duration": "10 days", "assertion_status": "new_order"},
            {"name": "Paracetamol", "strength": "300mg", "route": "PO", "frequency": "PRN", "assertion_status": "new_order"},
        ],
        expected_investigations=[]
    ))

    # 18. Ophthalmology / Post-Op Cataract Taper
    cases.append(HeldOutGroundTruth(
        id="heldout-018-ophthalmology",
        specialty="Ophthalmology",
        note_text="""Post-Op Day 1: Uncomplicated phacoemulsification with posterior chamber IOL right eye.
Plan:
1. Prednisolone acetate 1% ophthalmic suspension 1 drop right eye QID for 1 week, then taper.
2. Moxifloxacin 0.5% eye drops 1 drop right eye TDS for 7 days.
3. Hold Aspirin if intraocular hemorrhage occurs.""",
        expected_medications=[
            {"name": "Prednisolone", "strength": "1%", "route": "ophthalmic", "frequency": "QID", "duration": "1 week", "assertion_status": "new_order"},
            {"name": "Moxifloxacin", "strength": "0.5%", "route": "eye drops", "frequency": "TDS", "duration": "7 days", "assertion_status": "new_order"},
            {"name": "Aspirin", "strength": None, "assertion_status": "conditional"},
        ],
        expected_investigations=[]
    ))

    # 19. Endocrinology / Post-Thyroidectomy TSH Monitoring
    cases.append(HeldOutGroundTruth(
        id="heldout-019-thyroid",
        specialty="Endocrinology",
        note_text="""History: 45-year-old female post-total thyroidectomy for multinodular goiter 6 weeks ago.
Current Meds: Levothyroxine 100mcg PO daily.
Plan:
1. Increase Levothyroxine to 125mcg PO daily in morning 30 minutes before breakfast.
2. Order Serum TSH (Thyroid Stimulating Hormone) in 6 weeks.
3. Order Serum Free T4.""",
        expected_medications=[
            {"name": "Levothyroxine", "strength": "125mcg", "route": "PO", "frequency": "daily", "assertion_status": "new_order"},
            {"name": "Levothyroxine", "strength": "100mcg", "route": "PO", "frequency": "daily", "assertion_status": "current_medication"},
        ],
        expected_investigations=[
            {"name": "Serum TSH", "assertion_status": "new_order"},
            {"name": "Free T4", "assertion_status": "new_order"},
        ]
    ))

    # 20. Allergy & Immunology / Penicillin Desensitization
    cases.append(HeldOutGroundTruth(
        id="heldout-020-allergy",
        specialty="Allergy & Clinical Immunology",
        note_text="""Diagnosis: Syphilis in penicillin-allergic pregnant patient. History of anaphylaxis to Amoxicillin.
Allergies: Amoxicillin (anaphylaxis - absolute contraindication outside ICU).
Plan:
1. Transfer to ICU for oral penicillin desensitization protocol.
2. Ensure continuous cardiac monitoring and IV access.
3. Order baseline Complete Blood Count, Serum Creatinine, and 12-lead ECG prior to protocol.""",
        expected_medications=[
            {"name": "Amoxicillin", "strength": None, "assertion_status": "negated"},
        ],
        expected_investigations=[
            {"name": "Complete Blood Count", "assertion_status": "new_order"},
            {"name": "Serum Creatinine", "assertion_status": "new_order"},
            {"name": "ECG", "assertion_status": "new_order"},
        ]
    ))

    return cases
