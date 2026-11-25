"""Freeze source-authored synthetic labels before executing the candidate implementation.

This is a draft engineering annotation set, not clinician-adjudicated ground truth.
No labels are read from predictions. Run once; existing fixture files are never replaced.
"""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
OUT = Path(__file__).with_name('stage15_frozen_cases.json')
cases = []

def add(cid, note, meds=(), invs=(), critical=False):
    def labels(items):
        result = []
        cursor = {}
        for item in items:
            value = dict(item)
            name = value['name']
            start = note.lower().index(name.lower(), cursor.get(name.lower(), 0))
            cursor[name.lower()] = start + len(name)
            # All synthetic strings use BMP characters; C# UTF-16 offsets equal these indices.
            value.update(mention_start=start, mention_end=start+len(name))
            result.append(value)
        return result
    cases.append(dict(id=cid, note_text=note, expected_medications=labels(meds),
                      expected_investigations=labels(invs), critical=critical))

def m(name, assertion, **kw): return dict(name=name, assertion_status=assertion, **kw)

add('allergy', 'ALLERGY HISTORY:\nAspirin (anaphylaxis).', [m('Aspirin','allergy')], critical=True)
add('negated', 'Patient is not taking Ibuprofen 400 mg PO daily.', [m('Ibuprofen','negated')], critical=True)
add('historical', 'Drug History:\nPreviously received Allopurinol 100 mg PO daily last year.', [m('Allopurinol','historical')], critical=True)
add('discontinued', 'Discontinued Medications:\nWarfarin 5 mg PO once daily was stopped.', [m('Warfarin','discontinued')], critical=True)
add('completed', 'Completed a course of Amoxicillin 500 mg PO three times daily for 5 days.', [m('Amoxicillin','historical', detailed_assertion='completed_course')], critical=True)
add('conditional-rx', 'Start Acyclovir 400 mg PO twice daily if vesicles recur.', [m('Acyclovir','conditional')], critical=True)
add('education', 'Patient education: Aspirin can cause bleeding; this is an educational example.', [m('Aspirin','uncertain', detailed_assertion='educational')], critical=True)
add('hypothetical', 'Differential treatment options: consider Midostaurin 50 mg PO twice daily.', [m('Midostaurin','conditional')], critical=True)
add('held', 'Withheld Apixaban 5 mg PO twice daily pending review.', [m('Apixaban','held')], critical=True)
add('current', 'Current Medications:\nLevofloxacin 500 mg PO once daily.', [m('Levofloxacin','current_medication',strength=None,dose='500 mg',dose_unit='mg',route='PO',frequency='QD')])
add('continued', 'Continue Metformin 500 mg, 1 tablet PO twice daily.', [m('Metformin','current_medication',detailed_assertion='continued',strength='500 mg',dose='1 tablet',route='PO',frequency='BID')])
add('new', 'Started Meropenem 1 g IV every 8 hours for 10 days.', [m('Meropenem','new_order',strength=None,dose='1 g',route='IV',frequency='Q8H',duration='10 days')])
add('uncertain', 'It is unclear whether the patient takes Acyclovir.', [m('Acyclovir','uncertain')], critical=True)
add('family', 'Family History:\nMother takes Warfarin 5 mg daily.', [m('Warfarin','uncertain',detailed_assertion='other_experiencer')], critical=True)
add('avoid', 'Avoid Ibuprofen because of renal impairment.', [m('Ibuprofen','negated')], critical=True)
add('no-allergy', 'Patient denies Aspirin allergy.', [m('Aspirin','negated')], critical=True)
add('chemo-linking', 'Chemotherapy Regimen:\nCytarabine 100 mg/m2 continuous IV infusion + Daunorubicin 60 mg/m2 IV push.', [m('Cytarabine','current_medication',strength=None,dose='100 mg/m2',dose_unit='mg/m2',route='IV',administration='continuous infusion'),m('Daunorubicin','current_medication',strength=None,dose='60 mg/m2',dose_unit='mg/m2',route='IV',administration='IV push')], critical=True)
add('different-doses', 'Start Acyclovir 400 mg PO twice daily and start Meropenem 1 g IV every 8 hours for 10 days.', [m('Acyclovir','new_order',dose='400 mg',route='PO',frequency='BID',duration=None),m('Meropenem','new_order',dose='1 g',route='IV',frequency='Q8H',duration='10 days')], critical=True)
add('no-copy', 'Current Medications:\nMetformin 500 mg, 1 tablet PO twice daily.\nAcyclovir.', [m('Metformin','current_medication',strength='500 mg',dose='1 tablet',route='PO',frequency='BID'),m('Acyclovir','current_medication',strength=None,dose=None,route=None,frequency=None)], critical=True)
add('concentration', 'Give Acyclovir 100 mg/mL solution, administer 5 mL IV every 8 hours.', [m('Acyclovir','new_order',strength='100 mg/mL',dose='5 mL',dose_unit='mL',route='IV',frequency='Q8H')], critical=True)
add('weight', 'Start Meropenem 20 mg/kg IV every 8 hours.', [m('Meropenem','new_order',strength=None,dose='20 mg/kg',dose_unit='mg/kg',route='IV')], critical=True)
add('similar-names', 'Start Vincristine 1 mg IV.\nWithhold Vinblastine.', [m('Vincristine','new_order',dose='1 mg',route='IV'),m('Vinblastine','held')], critical=True)
add('brand-ambiguous', 'Continue Glycomet GP 1 tablet PO daily.', [m('Glycomet GP','current_medication',normalization_status='uncertain')], critical=True)
add('synonym', 'Start Acetaminophen 500 mg, 1 tablet PO once daily.', [m('Acetaminophen','new_order',normalization_status='matched')])
add('unknown-rx', 'Medication Orders:\nZorvaximab 2 mg/kg IV weekly.', [m('Zorvaximab','current_medication',normalization_status='unmatched',dose='2 mg/kg',route='IV')])
add('chemical', 'Occupational exposure to benzene and ethanol was discussed. Serum sodium 140 mmol/L.', [], [m('Serum sodium','historical')], critical=True)
add('acronyms', 'CT is the initials of the technician. The patient lives in PO district.', critical=True)
add('future-cbc', 'Report to Day Care Oncology Wing on 01/12/2026 at 09:00 AM for CBC.', invs=[m('CBC','new_order',detailed_assertion='scheduled',timing='01/12/2026 at 09:00 AM')], critical=True)
add('future-list', 'Follow-Up Plan:\nReport on 01/12/2026 for CBC and renal function tests.', invs=[m('CBC','new_order',detailed_assertion='scheduled'),m('renal function tests','new_order',detailed_assertion='scheduled')], critical=True)
add('performed', 'CBC performed yesterday showed thrombocytopenia.', invs=[m('CBC','historical',detailed_assertion='result_documented')], critical=True)
add('completed-test', 'Bone marrow biopsy was performed yesterday.', invs=[m('Bone marrow biopsy','historical',detailed_assertion='completed')])
add('old-test', 'Prior Investigations:\nMRI performed last year.', invs=[m('MRI','historical',detailed_assertion='historical')])
add('conditional-test', 'Repeat CBC if fever persists.', invs=[m('CBC','conditional')], critical=True)
add('negated-test', 'No repeat CT scan required.', invs=[m('CT scan','negated')], critical=True)
add('planned-test', 'Plan for bone marrow aspirate next week.', invs=[m('bone marrow aspirate','new_order',detailed_assertion='planned')])
add('ordered-test', 'Order flow cytometry today.', invs=[m('flow cytometry','new_order',detailed_assertion='newly_ordered')])
add('uncertain-test', 'It is unclear whether cytogenetics was performed.', invs=[m('cytogenetics','uncertain')], critical=True)
add('oncology-tests', 'Investigations Performed:\nBone marrow aspirate and bone marrow biopsy confirmed AML. Blood cultures grew E. coli. Cytogenetics showed a normal karyotype.', invs=[m('Bone marrow aspirate','historical'),m('bone marrow biopsy','historical'),m('Blood cultures','historical'),m('Cytogenetics','historical')])
add('panels', 'Obtain renal function panel, liver function panel, serum electrolytes, and serum uric acid tomorrow.', invs=[m(n,'new_order') for n in ['renal function panel','liver function panel','serum electrolytes','serum uric acid']])
add('counts', 'Check full blood count, hemoglobin, platelet count, differential count and peripheral smear.', invs=[m(n,'new_order') for n in ['full blood count','hemoglobin','platelet count','differential count','peripheral smear']])
add('micro', 'Order urine culture, culture and sensitivity, stool antigen testing and molecular mutation testing.', invs=[m(n,'new_order') for n in ['urine culture','culture and sensitivity','stool antigen testing','molecular mutation testing']])
add('imaging', 'Order chest radiograph, ultrasound abdomen, CT chest, MRI brain, ECG, echocardiography and spirometry.', invs=[m(n,'new_order') for n in ['chest radiograph','ultrasound abdomen','CT chest','MRI brain','ECG','echocardiography','spirometry']])
add('biochem', 'Check serum creatinine, blood glucose, HbA1c, lipid profile and CRP.', invs=[m(n,'new_order') for n in ['serum creatinine','blood glucose','HbA1c','lipid profile','CRP']])
add('prn-timing', 'Medication Orders:\nStart Ondansetron 8 mg PO SOS for nausea from 01/12/2026 until 03/12/2026.', [m('Ondansetron','new_order',dose='8 mg',route='PO',frequency='PRN',prn_indication='nausea',start_timing='01/12/2026',stop_timing='03/12/2026')])
add('numbered-sections', '1. DRUG HISTORY:\nAllopurinol 100 mg daily.\n2. DISCHARGE MEDICATIONS:\nAcyclovir 400 mg PO BD.\n3. FOLLOW-UP INSTRUCTIONS:\nCBC next week.', [m('Allopurinol','historical'),m('Acyclovir','current_medication')], [m('CBC','new_order',detailed_assertion='planned')], critical=True)
add('mixed-assertions', 'Stop Warfarin and continue Aspirin 75 mg PO daily.', [m('Warfarin','discontinued'),m('Aspirin','current_medication',route='PO')], critical=True)
add('long-note', 'Hospital Course:\n' + ('Routine observation was uneventful. ' * 350) + '\nDischarge Medications:\nAcyclovir 400 mg PO twice daily.\nFollow-Up Plan:\nCBC next week.', [m('Acyclovir','current_medication',dose='400 mg',route='PO')], [m('CBC','new_order',detailed_assertion='planned')])

if OUT.exists(): raise SystemExit('Frozen dataset already exists; refusing overwrite')
OUT.write_text(json.dumps(cases, indent=2, ensure_ascii=False)+'\n', encoding='utf-8')
print(f'Frozen {len(cases)} draft engineering cases; labels authored before candidate execution.')
