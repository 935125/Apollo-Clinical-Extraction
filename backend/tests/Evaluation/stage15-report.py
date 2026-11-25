"""Build reviewable Stage 15 reports only from retained execution artifacts."""
import collections, hashlib, json, shutil, xml.etree.ElementTree as ET
from pathlib import Path
ROOT=Path(__file__).resolve().parents[3]
DOC=ROOT/'docs'; E=DOC/'stage15-evidence'; EV=E/'evaluation'
def read(path): return json.loads(path.read_text(encoding='utf-8-sig'))
def write(name,body): (DOC/name).write_text(body.rstrip()+'\n',encoding='utf-8')
def pct(value): return 'N/A' if value is None else f'{100*value:.2f}%'
def acc(a): return f"{a['correct']}/{a['evaluated']} ({pct(a.get('accuracy'))})"
def table(headers,rows):
    def cell(x): return str(x).replace('|','/').replace('\n',' ')
    return '\n'.join(['| '+' | '.join(headers)+' |','|'+'|'.join('---' for _ in headers)+'|']+['| '+' | '.join(cell(v) for v in row)+' |' for row in rows])+'\n'
M=read(EV/'metrics.json'); P=read(EV/'performance.json')
datasets=M['datasets']; old='stage5_independent_benchmark_35cases.json'; draft='stage15_frozen_cases.json'
variants=['original-frozen','candidates-only','investigations-only','assertions-only','terminology-only','attributes-only','sections-only','integrated','legacy-adapter','integrated-adapter']
def metric(ds,variant,category): return datasets[ds]['variants'][variant][category]

baseline_audit=E/'baseline/root-cause-audit-initial.md'
if not baseline_audit.exists(): shutil.copyfile(DOC/'APOLLO_STAGE15_LOCAL_ONNX_ROOT_CAUSE_AUDIT.md',baseline_audit)

benchmark=['# Stage 15 baseline versus upgraded benchmark','',
'Measured on 25 November 2026. All predictions were produced by the real pinned local ONNX artifact and C# components. No new Foundry requests were made. The pre-edit baseline, source snapshot, hashes and command logs are immutable under `stage15-evidence/baseline/`.', '',
'The original 35-case source/labels were retained byte-for-byte. Another 47 draft cases were source-authored and frozen before the candidate implementation executed, then used during development. Neither dataset is clinician-adjudicated or a held-out clinical assessment. The original labels omit a negated Penicillin mention and use a shorter chest-radiograph mention; strict scoring keeps these as mismatches. No GPT output supplies gold labels.', '',
'## Detection and ablations','',
'Each component-only run changes that component while retaining frozen legacy candidates/attributes/assertions for other components. Legacy window slicing is retained for individual ablations; integrated uses per-window special tokens. Component-only latency is not an independent end-to-end deployment benchmark. Both full provider adapters were also executed, including `ClinicalReview` and metadata.', '']
for ds in (old,draft):
    benchmark += [f"### {datasets[ds]['caseCount']} cases: `{ds}`",'']
    rows=[]
    for v in variants:
        for cat in ('medications','investigations'):
            x=metric(ds,v,cat)
            rows.append([v,cat,x['tp'],x['fp'],x['fn'],pct(x['precision']),pct(x['recall']),pct(x['f1'])])
    benchmark += [table(['Variant','Entity','TP','FP','FN','Precision','Recall','F1'],rows),'']
benchmark += ['## Medication attributes and linking','',
'Values are evaluated against their annotated medication mention. Missing detections count as errors. Linking is the joint entity/annotated-attribute value accuracy, not a separately adjudicated complete relation graph. Explicit null annotations are included where present. Unannotated fields do not count as correct.', '']
for ds in (old,draft):
    a=metric(ds,'legacy-adapter','medications'); b=metric(ds,'integrated-adapter','medications')
    rows=[]
    for f in ['strength','dose','dose_unit','route','frequency','duration']:
        if f in b['attributes']: rows.append([f,acc(a['attributes'][f]),acc(b['attributes'][f])])
    rows.append(['Attribute-to-annotated-drug joint accuracy',acc(a['annotatedAttributeToEntityAccuracy']),acc(b['annotatedAttributeToEntityAccuracy'])])
    benchmark += [f'### `{ds}`','',table(['Attribute','Original adapter','V2 adapter'],rows),'']
benchmark += ['Mass without a documented product form/quantity is preserved as administered dose in V2; product strength remains missing. Fifteen original-case strength expectations retain the old interpretation. The score is reported as observed rather than changing those annotations. No absolute chemotherapy dose is computed.', '',
'## Investigations, evidence and normalization','']
for ds in (old,draft):
    rows=[]
    for cat in ('medications','investigations'):
        a=metric(ds,'legacy-adapter',cat); b=metric(ds,'integrated-adapter',cat)
        rows += [[cat+' evidence exact offsets',acc(a['evidenceExact']),acc(b['evidenceExact'])],
                 [cat+' evidence/entity association',acc(a['evidenceEntityAssociation']),acc(b['evidenceEntityAssociation'])]]
        for key,label in [('mapped','standard-code coverage'),('unresolved','unresolved code rate')]:
            def coverage(x):
                t=x['terminology']; return f"{t.get(key,0)}/{t['extracted']} ({pct(t.get(key,0)/t['extracted'])})"
            rows.append([cat+' '+label,coverage(a),coverage(b)])
    for field in ['timing','assertion_status']:
        a=metric(ds,'legacy-adapter','investigations')['attributes'].get(field)
        b=metric(ds,'integrated-adapter','investigations')['attributes'].get(field)
        if a and b: rows.append(['investigation '+field,acc(a),acc(b)])
    benchmark += [f'### `{ds}`','',table(['Measure','Original adapter','V2 adapter'],rows),'']
benchmark += ['Standard-code coverage is not correct-mapping accuracy. No independent reference-code set exists; semantic correct mapping rate among assigned codes is NOT MEASURED. Unresolved rates can increase when valid uncoded mentions are recovered. Exact source offsets are verified independently, including in database reload tests.', '',
'## Uncertainty and interpretation','',
table(['35-case cohort','Original F1 note-bootstrap interval','Integrated F1 note-bootstrap interval'],[
    [c,'–'.join(pct(n) for n in metric(old,'original-frozen',c)['f1NoteBootstrap95']),
       '–'.join(pct(n) for n in metric(old,'integrated',c)['f1NoteBootstrap95'])] for c in ('medications','investigations')]),'',
'Intervals use 2,000 deterministic note-cluster bootstrap resamples. They quantify variation across these synthetic notes conditional on unvalidated labels, not uncertainty in clinical deployment accuracy. The zero observed errors in the draft 47-case regression set do not establish perfect assertion accuracy or generalization. No independently validated Apollo neural confidence cutoff was available or selected.', '',
'Full per-case errors, per-class precision/recall, confusion matrices, code coverage, and ablation numerators are in `stage15-evidence/evaluation/metrics.json`. Reproduce with `Run-Apollo-Stage15-Verification.ps1` and `backend/tests/Evaluation/stage15-score.py`.']
write('APOLLO_STAGE15_BASELINE_VS_UPGRADED_BENCHMARK.md','\n'.join(benchmark))

assertions=['# Stage 15 assertion and temporality','',
'Recognition, clinical action, assertion and terminology are separate decisions. V2 prioritizes experiencer and explicit uncertainty/negation/allergy/education/conditional language before section defaults. Shared leading cues can apply to a coordinated list; unrelated drug attributes cannot. Result markers are inspected after the test in its own scope. Appointment dates/times before a test are not laboratory values.', '',
'```mermaid\nflowchart LR\n    A[Source mention / section] --> B[Experiencer / uncertainty]\n    B --> C[Negation / allergy / education]\n    C --> D[Conditional / explicit action]\n    D --> E[Historical / completed / results]\n    E --> F[Scheduled / planned / current / unresolved]\n    F --> G[Compatible status plus detailed persisted assertion]\n```','',
table(['Medication detailed assertion','Compatible assertionStatus'],[
 ['current / continued','current_medication'],['new','new_order'],['held','held'],['discontinued','discontinued'],
 ['historical / completed_course','historical'],['negated','negated'],['allergy','allergy'],['conditional / planned','conditional'],
 ['uncertain / educational / other_experiencer / monitoring','uncertain']]),'',
table(['Investigation detailed assertion','Compatible assertionStatus'],[
 ['newly_ordered / planned / scheduled','new_order'],['completed / historical / result_documented','historical'],
 ['conditional','conditional'],['negated','negated'],['uncertain / other_experiencer','uncertain']]),'',
'The detailed value and temporality are saved in `provenance.clinicalContext`; source section, exact mention span and timing are saved alongside it. These are additive fields; no existing `new_order` or `historical` value is silently redefined. Angular renders the persisted detailed assertion when present. Missing dates are not invented, dates are not converted using an assumed encounter clock, and old source events are not automatically new prescriptions.', '',
'`Report ... on 01/12/2026 at 09:00 AM for CBC` becomes scheduled; `CBC performed yesterday showed ...` becomes a result documented in the past; `Repeat CBC if/when fever recurs` remains conditional; `No repeat CT scan required` remains negated. Completed antibiotics remain distinct from current therapy. PRN wording such as `when required` is retained without treating every PRN instruction as a future activation condition.', '',
'## Executed coarse confusion matrices','',
'Rows are draft source labels; columns are emitted compatible statuses. `<missed>` includes detection failures. `<no_gold>` includes strict false positives caused by incomplete original mention labels. These matrices and per-class rates are engineering evidence, not a claim of independently demonstrated perfect assertion accuracy.','']
for ds in (old,draft):
    for cat in ('medications','investigations'):
        for variant in ('original-frozen','integrated'):
            x=metric(ds,variant,cat); cm=x['confusionMatrix']; columns=sorted({k for r in cm.values() for k in r})
            assertions += [f'### {ds}: {cat}, {variant}','',table(['Gold / predicted']+columns,[[r]+[cm[r].get(c,0) for c in columns] for r in sorted(cm)]),'']
        rows=[]
        for cls,v in metric(ds,'integrated',cat)['perClass'].items(): rows.append([cls,v['tp'],v['fp'],v['fn'],pct(v['precision']),pct(v['recall'])])
        assertions += ['Per-class integrated results:','',table(['Class','TP','FP','FN','Precision','Recall'],rows),'']
assertions += ['Detailed-class matrices for the subset with explicit fine labels are retained in `metrics.json` under `detailedConfusionMatrix`. The legacy schema cannot represent those distinctions; this is recorded as `<not_represented>`, rather than assigning it invented detailed labels. Other detailed classes have regression assertions but no independent clinical accuracy estimate.', '',
'Remaining uncertainty: clause/tense heuristics do not establish general temporal reasoning, reference dates are not fully normalized, wrapped/multi-sentence instructions and changes over multiple visits may require review.']
write('APOLLO_STAGE15_ASSERTION_AND_TEMPORALITY.md','\n'.join(assertions))

orig=read(E/'baseline/aml-v33-historical.json')
before=next(c for c in read(E/'baseline/real-local-outputs.json')['cases'] if c['id']=='P100234-ENC-100567-v33')
after=next(c for c in read(EV/'integrated-adapter.json')['cases'] if c['id']=='P100234-ENC-100567-v33')
def context(p): return (p.get('provenance') or {}).get('clinicalContext') or {}
def full_assessment(p):
    c=context(p)
    return '; '.join(f'{key}={value}' for key,value in [('status',p.get('assertionStatus')),('detail',c.get('assertion')),('dose',p.get('dose')),('route',p.get('route')),('frequency',p.get('frequency')),('administration',c.get('administration')),('schedule',c.get('administrationSchedule'))] if value is not None)
targets=['Cytarabine','Daunorubicin','Levofloxacin','Acyclovir','Midostaurin','Meropenem','Posaconazole','Ondansetron']
rows=[]
for name in targets:
    raw=[p for p in before['raw'] if p['category']=='MEDICATION' and p['text'].lower()==name.lower()]
    rejected=[r['reason'] for r in before.get('reviewCandidates',[]) if r['name'].lower()==name.lower()]
    old_items=[p for p in before['result']['medications'] if p['name'].lower()==name.lower()]
    new_items=[p for p in after['result']['medications'] if p['name'].lower()==name.lower()]
    rows.append([name,orig['note_text'].lower().count(name.lower()),', '.join(str(r['confidence']) for r in raw) or 'Not detected',
        ', '.join(rejected) or ('Accepted' if old_items else 'No neural candidate'),
        ' / '.join(p['assertionStatus'] for p in old_items) or 'Absent',
        ' / '.join(full_assessment(p) for p in new_items) or 'Absent'])
aml=['# Stage 15 original AML v33 forensic comparison','',
f"Identity: `P100234 / ENC-100567 / v33`, note `{orig['noteId']}`, extraction `{orig['extractionId']}`. PostgreSQL marks the source record synthetic. Exact source SHA-256: `{orig['sourceHash']}`. A read-only post-verification query confirmed the same source hash and extraction ID. The original record was not resubmitted, signed again, replaced or overwritten.", '',
'The original persisted result contains Posaconazole and Ondansetron plus one historical CBC. Offline V2 re-evaluation is a separate synthetic forensic artifact, not an update to that historical record. The old Angular selection therefore still shows the old persisted result. A separate new synthetic note was processed through the real V2 provider, isolated PostgreSQL and Angular to verify display of actual persisted V2 entities.', '',
table(['Medication','Literal source mentions','Original ONNX scores','Original filter outcome','Original output','V2 source-grounded assessment'],rows),'',
'Acyclovir, Midostaurin and Meropenem were present but not detected as medications by the original model. They cannot be described as high-scoring candidates rejected by the prefix gate. Cytarabine (both mentions), Daunorubicin and Levofloxacin were detected and rejected for the missing prefix. V2 catalog/clinical recognition recovers missed mentions without fabricating NER scores. The two Cytarabine mentions remain separate: administered induction treatment versus future consolidation planning.', '',
'## Investigations','']
rows=[]
for name in ['bone marrow aspirate','bone marrow biopsy','Blood cultures','Cytogenetics','CBC','renal function panel','liver function panel']:
    literal=name.lower() in orig['note_text'].lower()
    old_items=[p for p in before['result']['investigations'] if p['name'].lower()==name.lower()]
    new_items=[p for p in after['result']['investigations'] if p['name'].lower()==name.lower()]
    if 'function panel' in name:
        new_items=[p for p in after['result']['investigations'] if p['name'].lower()=='renal/liver function panel']
        presence='Within combined renal/liver phrase' if literal else 'No separate literal phrase; combined renal/liver panel present'
    else: presence='Yes' if literal else 'No'
    rows.append([name,presence,' / '.join(p['assertionStatus'] for p in old_items) or 'Absent',
                 ' / '.join(p['name']+': '+p['assertionStatus']+' / '+str(context(p).get('assertion')) for p in new_items) or 'Absent'])
aml += [table(['Audit target','Actually in source','Original persisted/core output','V2 offline result'],rows),'',
'BC5CDR has no investigation output label; investigation detection here is attributed to rules/catalogs, not to disease/chemical overlap. Platelets and Hemoglobin are additionally recognized as documented hospital-course results. `renal/liver function panel` stays one exact source phrase with unresolved terminology, not two invented quotations or specific panel codes.', '',
'The direct CBC root cause is the legacy whole-line numeric-result regex: `09:00 AM` satisfies its colon-plus-number pattern; the historical ternary then wins because the appointment has no narrow order verb. Missing follow-up section recognition contributes. This is not evidence that an absolute appointment date is inherently historical, nor solely an ONNX/ContextEngine performance problem.', '',
f"V2 emits {len(after['result']['medications'])} medication mentions and {len(after['result']['investigations'])} investigation mentions in this offline assessment. No mention is assumed to be a new prescription simply because it was recovered. Benzydamine is a historical hospital-course mention; the falsely labeled neural `blasts` candidate is rejected as a pathology finding. Dose/route, method and chemotherapy days are source-local; no BSA-derived milligrams or undocumented regimen days are added.", '',
'Exact quote/mention/attribute offsets are validated before a local provider result is accepted. Standard identifiers for newly recognized oncology terms remain unresolved. The detailed per-candidate before/after traces and output are in `baseline/real-local-outputs.json`, `evaluation/integrated.json` and `evaluation/integrated-adapter.json`. The historical PostgreSQL export is `baseline/aml-v33-historical.json`.', '',
'Storage/display distinction: the original v33 remains unchanged in the application database/UI. The new browser fixture is a fresh synthetic note in `cx_audit_test`; its saved output and screenshot are in `playwright/stage15-local-onnx-results.json` and `stage15-real-persisted-result.png`. This avoids replaying historical signed events to improve the visible result.']
write('APOLLO_STAGE15_AML_V33_FORENSICS.md','\n'.join(aml))

perf=['# Stage 15 measured performance','',
'Actual machine: Windows 11 Pro, Intel Core i5-13420H, 12 logical processors, approximately 16 GB RAM, CPU execution. This differs from the illustrative i7 target in the request. No dedicated GPU, Docker, administrator installation or new inference runtime was required.', '',
f"Pinned INT8 graph: {P['artifactBytes']:,} bytes; tokenizer: {P['tokenizerBytes']:,} bytes. Initial testhost tokenizer/session initialization: {P['initializationMs']:.2f} ms. The first-ever measured candidate-only extraction, including JIT/static initialization, took {P['firstColdExtractionMs']:.2f} ms; it is not a full-V2 cold-start measurement.", '',
'## Fresh-session cold measurements','',
table(['Mode','Session/tokenizer initialization ms','First extraction ms','ONNX Run ms','Windows'],[[x['mode'],f"{x['initializationMs']:.2f}",f"{x['firstExtractionMs']:.2f}",f"{x['inferenceMs']:.2f}",x['windowsExecuted']] for x in P['coldSessionMeasurements']]),'',
'These are new ONNX sessions after process/JIT and OS file caches have warmed; the OS cache was not cleared. Cold pipeline measurements exclude database persistence and HTTP.', '',
'## Warm original AML note, 30 repetitions per pipeline','',
table(['Mode','Mean ms','p50 ms','p95 ms','Mean ONNX Run ms','All-logical-CPU utilization','Observed process peak MiB'],[[x['mode'],f"{x['meanMs']:.2f}",f"{x['p50Ms']:.2f}",f"{x['p95Ms']:.2f}",f"{x['meanInferenceMs']:.2f}",pct(x['cpuUtilizationAllLogicalProcessors']),f"{x['peakWorkingSetBytes']/1048576:.2f}"] for x in P['measurements']]),'',
f"Overall testhost peak after ablations, concurrency and fresh-session checks: {P['overallPeakWorkingSetBytes']/1048576:.2f} MiB. Peak working set includes .NET/xUnit, catalog/regex structures, managed/native ONNX memory and multiple test sessions. It is not a model-only allocation or an isolated production-service memory bound.", '',
'## Long notes and concurrency','',
table(['Mode','Notes','Characters/note','Content tokens/note','Mean ms','Observed sequential notes/second'],[[x['mode'],x['notes'],x['charLength'],x['tokens'],f"{x['meanMs']:.2f}",f"{x['observedSequentialNotesPerSecond']:.2f}"] for x in P['longNoteMeasurements']]),'',
f"Eight concurrent short requests completed in {P['concurrency']['elapsedMs']:.2f} ms with distinct correlations; output isolation is also asserted separately in tests. This short observation does not establish production capacity or an SLA. V2 is slower on the measured long-note cohort because it adds clinical processing; improved recall is not described as an inference speedup.", '',
'Loaded sessions and immutable terminology resources are reused. Every supported content token is evaluated; V2 uses per-window CLS/SEP and positive overlap coverage. Model/graph/tokenizer corruption fails closed. A real configured timeout, cancellation and subsequent session reuse are exercised by automated tests.', '']
pg=read(E/'integration/real-postgres-roundtrip.json')
perf += ['## Persistence','',
f"One isolated PostgreSQL measurement: API extraction plus reload {pg['apiAndReloadMs']:.2f} ms; a separate persistence-only insert {pg['persistenceOnlyMs']:.2f} ms; reload {pg['reloadOnlyMs']:.2f} ms. The insert uses a new synthetic extraction identity, not an overwrite. Connections in the test use pooling disabled; these are single observations, not latency distributions or production database estimates.", '',
'Raw results: `stage15-evidence/evaluation/performance.json` and `integration/real-postgres-roundtrip.json`. CPU utilization is process CPU time divided by elapsed wall time and logical processor count. Replacement-model latency, memory, quantized accuracy and deployment costs are NOT TESTED.']
write('APOLLO_STAGE15_PERFORMANCE_RESULTS.md','\n'.join(perf))

critical=sum(len(metric(ds,'integrated',cat)['criticalUnsafeActive']) for ds in (old,draft) for cat in ('medications','investigations'))
safety=['# Stage 15 clinical safety regression','',
f'Observed critical unsafe-active classifications in the frozen annotated checks: {critical}. This count is conditional on the draft labels and covered cases; it is not a claim that the engine is clinically error-free.', '',
'Mandatory adversarial coverage includes aspirin allergy, negated ibuprofen, historical allopurinol, discontinued anticoagulation, completed antibiotic courses, conditional prescriptions, future follow-up tests, prior results, differing adjacent doses/routes, similar drug names, ambiguous brands, ambiguous abbreviations, mg/m2 regimens, educational text and hypothetical treatment plans. Separate challenge tests additionally cover trailing negation, `when` conditions, PRN wording, therapeutic salts versus analytes, laboratory concentrations, blast findings and chemotherapy-day linking.', '',
'Development failures were retained in command logs and repaired before verification. They included: `continuous` being interpreted as a continue verb; loss of a shared scheduling cue; pathological `blasts` leaking into a chemotherapy section; diagnostic thresholds becoming medication candidates; and a grammatical `administer 5 mL` phrase becoming a second drug candidate. Tests now lock down each safety boundary. They do not weaken privacy, Foundry evidence rejection or terminology specificity.', '',
'The strict 35-case score retains a false positive for the source-grounded, negated Penicillin mention omitted from old gold. The qualified chest-radiograph phrase produces a strict name mismatch. These remain errors in the published metric and are disclosed for adjudication. Neither is silently relabeled away.', '',
'New medication mentions are not automatically active orders. Completed, historical, allergic, negated, held, conditional, educational, monitoring and other-experiencer states remain explicit. Unknown codes never remove an otherwise defensible mention, and never become invented RxCUIs/LOINC identifiers. The local evidence gate checks exact quotes, entity offsets and attribute bounds; tampered evidence tests fail closed.', '',
'## Release decision','',
'Engineering regression gate: PASS on the executed local checks. Clinical deployment clearance: BLOCKED on independent clinician adjudication, real-world validation and reference-code mapping assessment. Default configuration remains `Legacy`; `CandidateV2` is enabled only in the isolated evaluated test instance. Both versions remain available. No automatic historical re-extraction or schema-destructive migration is part of activation.', '',
'No GPT-equivalent accuracy, perfect assertion accuracy, comprehensive Indian-brand coverage or general clinical temporal reasoning is claimed. Finite catalogs, heuristic context, wrapped attributes and unfamiliar shorthand remain material limitations. New model candidates were researched, not substituted without measurement.']
write('APOLLO_STAGE15_CLINICAL_SAFETY_REGRESSION.md','\n'.join(safety))

print('Generated benchmark, assertion/confusion, AML forensic, performance and safety reports from retained artifacts.')
