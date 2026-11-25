"""Reproducible offline scorer. Frozen source labels, exact offsets, no LLM adjudication.

The original 35-case labels omit some non-active mentions; strict scores retain these
as FP and list them for review. No labels or predictions are edited by the scorer.
"""
import collections, hashlib, json, math, random, re, statistics
from pathlib import Path
ROOT=Path(__file__).resolve().parents[3]
E=ROOT/'docs/stage15-evidence'
OUT=E/'evaluation'

def read(p): return json.loads(p.read_text(encoding='utf-8-sig'))
def text(x): return re.sub(r'\s+',' ',str(x or '').strip().lower())
ALIASES={'cbc':'complete blood count','complete blood count (cbc)':'complete blood count',
 'cxr':'chest x-ray','chest radiograph':'chest x-ray','chest x-ray pa view':'chest x-ray',
 'ecg':'electrocardiogram','ekg':'electrocardiogram','12-lead ecg':'electrocardiogram','12 lead ecg':'electrocardiogram',
 'lft':'liver function tests','liver function tests (lft)':'liver function tests',
 'esr':'erythrocyte sedimentation rate','erythrocyte sedimentation rate (esr)':'erythrocyte sedimentation rate',
 'serum psa (prostate specific antigen)':'serum psa','serum tsh (thyroid stimulating hormone)':'serum tsh',
 'serum free t4':'free t4','fasting lipid profile':'lipid profile','hba1c':'hemoglobin a1c',
 'paracetamol':'acetaminophen','salbutamol':'albuterol','prednisolone acetate':'prednisolone'}
def name(x): return ALIASES.get(text(x),text(x))
def attr(field,value):
    v=text(value)
    if field=='route': return {'po':'oral','orally':'oral','by mouth':'oral','intravenous':'iv','sq':'sc','subcutaneous':'sc','inhalation':'inhaled','eye drops':'ophthalmic','ophthalmic suspension':'ophthalmic'}.get(v,v)
    if field=='frequency':
        for values,standard in [(['bid','bd','twice daily','twice a day'],'bid'),(['qd','od','daily','once daily','morning','every morning','in the morning'],'qd'),(['tid','tds','three times daily','tds prn'],'tid'),(['qid','qds','four times daily','qds prn'],'qid'),(['once weekly','weekly','once weekly on sundays','once weekly on tuesdays'],'weekly'),(['prn','sos','as needed','every 6 hours prn'],'prn'),(['qhs','hs','bedtime','at bedtime','at night','night'],'qhs')]:
            if v in values: return standard
    if field in ('strength','dose','dose_unit','duration'):
        if field=='duration': v=re.sub(r'^for\s+','',v)
        for i,w in enumerate(['one','two','three','four','five','six','seven','eight','nine','ten'],1): v=re.sub(r'\b'+w+r'\b',str(i),v)
        v=re.sub(r'\b(tablets|tabs|tab)\b','tablet',v)
        v=re.sub(r'\b(units|capsules|days|weeks|months)\b',lambda m:m[0][:-1],v)
        v=re.sub(r'\s+','',v)
    return v
def fields(p):
    result={re.sub(r'([A-Z])',lambda m:'_'+m[0].lower(),k):v for k,v in p.items() if isinstance(v,(str,type(None)))}
    context=(p.get('provenance') or {}).get('clinicalContext') or {}
    result.update(detailed_assertion=context.get('assertion'),administration=context.get('administration'),
      start_timing=context.get('startTiming'),stop_timing=context.get('stopTiming'),prn_indication=context.get('prnIndication'),
      normalization_status=p.get('normalization',{}).get('status'))
    return result
def span(p):
    c=(p.get('provenance') or {}).get('clinicalContext')
    if c: return c['sourceStart']
    e=p['evidence']; offset=e['quote'].lower().find(p['name'].lower())
    return e['start']+offset if offset>=0 and e['start'] is not None else None
def slice16(s,start,end): return s.encode('utf-16-le')[2*start:2*end].decode('utf-16-le',errors='replace')
def evidence_ok(note,e):
    return isinstance(e.get('start'),int) and isinstance(e.get('end'),int) and 0<=e['start']<e['end']<=len(note.encode('utf-16-le'))//2 and slice16(note,e['start'],e['end'])==e['quote']
def rates(tp,fp,fn):
    return dict(tp=tp,fp=fp,fn=fn,precision=tp/(tp+fp) if tp+fp else None,recall=tp/(tp+fn) if tp+fn else None,
                f1=2*tp/(2*tp+fp+fn) if 2*tp+fp+fn else None)
def accuracy(c,n): return dict(correct=c,evaluated=n,accuracy=c/n if n else None)
def ci(rows):
    rng=random.Random(1501); values=[]
    for _ in range(2000):
        sample=[rows[rng.randrange(len(rows))] for __ in rows]
        tp,fp,fn=(sum(r[i] for r in sample) for i in range(3)); values.append(rates(tp,fp,fn)['f1'] or 0)
    values.sort(); return [values[49],values[1949]]

gold={}
datasets={}
for file in ('stage5_independent_benchmark_35cases.json','stage15_frozen_cases.json'):
    for c in read(ROOT/'backend/tests/Evaluation'/file): gold[c['id']]=c; datasets[c['id']]=file

def evaluate(rows,ids):
    totals={}
    for category in ('medications','investigations'):
        tp=fp=fn=0; attrs=collections.defaultdict(lambda:[0,0]); cm=collections.defaultdict(collections.Counter); fine=collections.defaultdict(collections.Counter)
        extras=[]; misses=[]; errors=[]; boot=[]; offsets=[0,0]; association=[0,0]; terminology=collections.Counter(); safety=[]; linking=[0,0]
        for row in rows:
            cid=row['id']
            if cid not in ids: continue
            c=gold[cid]; note=c['note_text']; expected=c['expected_'+category]; predicted=row['result'][category]
            used=set(); pairs=[]
            for g in expected:
                selected=next((i for i,p in enumerate(predicted) if i not in used and name(p['name'])==name(g['name']) and ('mention_start' not in g or span(p)==g['mention_start'])),None)
                if selected is not None: used.add(selected)
                pairs.append((g,predicted[selected] if selected is not None else None))
            a=len(used); b=len(predicted)-a; d=len(expected)-a; tp+=a; fp+=b; fn+=d; boot.append((a,b,d))
            for g,p in pairs:
                actual=fields(p) if p else {}
                status=actual.get('assertion_status') if p else '<missed>'
                cm[g.get('assertion_status','<unlabeled>')][status]+=1
                if 'detailed_assertion' in g: fine[g['detailed_assertion']][actual.get('detailed_assertion') or ('<not_represented>' if p else '<missed>')]+=1
                if p is None: misses.append(dict(id=cid,name=g['name']))
                for key,value in g.items():
                    if key in ('name','mention_start','mention_end','evidence_start','evidence_end','evidence_quote'): continue
                    correct=p is not None and attr(key,value)==attr(key,actual.get(key))
                    attrs[key][0]+=correct; attrs[key][1]+=1
                    if key in ('strength','dose','route','frequency','duration'):
                        linking[0]+=correct; linking[1]+=1
                    if not correct: errors.append(dict(id=cid,name=g['name'],field=key,expected=value,actual=actual.get(key)))
                if c.get('critical') and g.get('assertion_status') not in ('current_medication','new_order') and status in ('current_medication','new_order'):
                    safety.append(dict(id=cid,name=g['name'],expected=g['assertion_status'],actual=status))
            for i,p in enumerate(predicted):
                ev=p['evidence']; offsets[1]+=1; offsets[0]+=evidence_ok(note,ev)
                context=(p.get('provenance') or {}).get('clinicalContext') or {}
                mention=span(p)
                association[1]+=1; association[0]+=evidence_ok(note,ev) and mention is not None and slice16(note,mention,mention+len(p['name'].encode('utf-16-le'))//2)==p['name']
                n=p.get('normalization',{}); terminology['extracted']+=1
                terminology['mapped' if n.get('matchedCode') else 'unresolved']+=1
                terminology['ambiguous']+=n.get('semanticStatus')=='ambiguous'
                if i not in used:
                    extras.append(dict(id=cid,name=p['name'],status=p['assertionStatus'],evidence=p['evidence']['quote']))
                    cm['<no_gold>'][p['assertionStatus']]+=1
                    if c.get('critical') and not expected and p['assertionStatus'] in ('current_medication','new_order'):
                        safety.append(dict(id=cid,name=p['name'],expected='<no_medication>',actual=p['assertionStatus']))
        classes=sorted(set(cm)|{actual for counts in cm.values() for actual in counts})
        perclass={}
        for cls in classes:
            if cls.startswith('<'): continue
            t=cm[cls][cls]; f=sum(counts[cls] for expected,counts in cm.items() if expected!=cls); n=sum(v for actual,v in cm[cls].items() if actual!=cls)
            perclass[cls]={**rates(t,f,n),'classAccuracy': t/sum(cm[cls].values()) if sum(cm[cls].values()) else None}
        totals[category]={**rates(tp,fp,fn), 'f1NoteBootstrap95':ci(boot),'attributes':{k:accuracy(*v) for k,v in attrs.items()},
            'annotatedAttributeToEntityAccuracy':accuracy(*linking),'confusionMatrix':{k:dict(v) for k,v in cm.items()},'perClass':perclass,
            'detailedConfusionMatrix':{k:dict(v) for k,v in fine.items()},'evidenceExact':accuracy(*offsets),'evidenceEntityAssociation':accuracy(*association),
            'terminology':dict(terminology),'criticalUnsafeActive':safety,'falsePositives':extras,'falseNegatives':misses,'attributeErrors':errors}
    return totals

variants={'original-frozen':read(E/'baseline/real-local-outputs.json')['cases']}
variants.update({p.stem:read(p)['cases'] for p in OUT.glob('*.json') if p.stem in ('candidates-only','investigations-only','assertions-only','terminology-only','attributes-only','sections-only','integrated','legacy-adapter','integrated-adapter')})
report={'classification':'Frozen draft engineering evaluation; not held-out after development use; no clinician adjudication; no model outputs used as gold',
 'intervalMethod':'2,000 deterministic note-cluster bootstrap resamples; intervals conditional on these synthetic notes and unvalidated labels',
 'mappingCorrectness':'NOT MEASURED against independently reviewed reference codes; coverage is not correctness',
 'datasets':{}}
for dataset in sorted(set(datasets.values())):
    ids={i for i,d in datasets.items() if d==dataset}
    report['datasets'][dataset]={'caseCount':len(ids),'variants':{v:evaluate(rows,ids) for v,rows in variants.items()}}
OUT.mkdir(exist_ok=True)
(OUT/'metrics.json').write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
for ds,d in report['datasets'].items():
    print(ds,d['caseCount'])
    for variant,v in d['variants'].items():
        print(variant,'; '.join(f'{c}: TP={x["tp"]} FP={x["fp"]} FN={x["fn"]} P/R/F1={x["precision"]:.4f}/{x["recall"]:.4f}/{x["f1"]:.4f}' for c,x in v.items()))
    integrated=d['variants']['integrated']
    print('Critical unsafe active:',sum(len(x['criticalUnsafeActive']) for x in integrated.values()))
    for category,v in integrated.items():
        print(category,'attribute errors:',len(v['attributeErrors']),'extra mentions:',[(e['id'],e['name'],e['status']) for e in v['falsePositives']])
