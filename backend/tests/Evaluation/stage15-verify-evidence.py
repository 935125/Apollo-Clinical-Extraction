"""Verify retained Stage 15 evidence and record current source hashes; no inference or DB access."""
import hashlib
import json
import re
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
E = ROOT / 'docs/stage15-evidence'

SOURCES = '''
Run-Apollo-Stage15-Verification.ps1
Run-Apollo-Postman-Tests.ps1
backend/Apollo.Backend.csproj
backend/Program.cs
backend/appsettings.json
backend/Clinical/LocalClinicalPipelineOptions.cs
backend/Clinical/ClinicalSectionDetector.cs
backend/Clinical/LocalClinicalScope.cs
backend/Clinical/VersionedClinicalTerminology.cs
backend/Clinical/InvestigationRecognizer.cs
backend/Clinical/MedicationCandidateClassifier.cs
backend/Clinical/ClinicalAssertionClassifier.cs
backend/Clinical/MedicationAttributeExtractor.cs
backend/Clinical/LocalEvidenceValidator.cs
backend/Clinical/UpgradedClinicalPipeline.cs
backend/Clinical/ClinicalPipelineService.UpgradedNer.cs
backend/Clinical/Terminology/clinical-recognition-v1.json
backend/Clinical/ClinicalPipelineService.cs
backend/Clinical/OnnxClinicalExtractor.cs
backend/Clinical/ClinicalResultValidator.cs
backend/Clinical/ClinicalReview.cs
backend/Clinical/ProviderDiagnostics.cs
backend/Clinical/TerminologyVerification.cs
backend/Nlp/SlidingWindowOnnxEngine.cs
backend/Models/Contracts.cs
backend/Storage/SqlExtractionRepository.cs
backend/tests/Stage15ClinicalPipelineTests.cs
backend/tests/Stage15SafetyChallengeTests.cs
backend/tests/Stage15PostgresTests.cs
backend/tests/Stage15E2EEnvironmentTests.cs
backend/tests/Evaluation/Stage15BenchmarkTests.cs
backend/tests/Evaluation/Stage15AblationTests.cs
backend/tests/Stage9EmrTests.cs
backend/tests/Evaluation/AuditRemediationTests.cs
backend/tests/Evaluation/ReliabilityMeasurementTests.cs
backend/tests/Evaluation/HeldOutBenchmarkRunner.cs
backend/tests/Evaluation/ForensicPipelineAuditTests.cs
backend/tests/Evaluation/stage15_frozen_cases.json
backend/tests/Evaluation/stage15-make-fixtures.py
backend/tests/Evaluation/stage15-score.py
backend/tests/Evaluation/stage15-report.py
backend/tests/Evaluation/stage15-inspect-supplementary.py
backend/tests/Evaluation/stage15-export-historical.sql
backend/tests/Evaluation/stage15-verify-evidence.py
backend/tests/Terminology/build-stage15-catalog.py
backend/tests/Verification/stage15-start-isolated.ps1
frontend/src/app/models/extraction.model.ts
frontend/src/app/queue/clinical-queue.ts
frontend/src/app/queue/clinical-queue.html
frontend/e2e/stage15-local-onnx-e2e.mjs
frontend/e2e/model-provenance-e2e.mjs
frontend/e2e/queue-page-size-e2e.mjs
frontend/e2e/stage13-top10-queue-e2e.mjs
frontend/e2e/stage12-1-uhid-queue-e2e.mjs
'''.split()


def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def record(path):
    return {'path': path.relative_to(ROOT).as_posix(), 'sha256': sha(path), 'bytes': path.stat().st_size}


baseline = read(E / 'baseline/source-manifest.json')
for entry in baseline:
    saved = E / 'baseline/source' / entry['Path'].replace('\\', '/')
    assert sha(saved) == entry['Sha256'].upper(), f'Frozen baseline changed: {saved}'

dataset_manifest = read(E / 'evaluation/dataset-manifest.json')
for entry in dataset_manifest['datasets']:
    path = ROOT / 'backend/tests/Evaluation' / entry['name']
    assert sha(path) == entry['sha256'].upper(), f'Frozen labels changed: {path}'
assert sha(E / 'baseline/stage5_independent_benchmark_35cases.json') == sha(
    ROOT / 'backend/tests/Evaluation/stage5_independent_benchmark_35cases.json')

ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
test_counts = {}
for name, path in {
    'backend': E / 'regression/test-results/stage15-full-final.trx',
    'ablation': E / 'evaluation/test-results/stage15-ablation-final.trx',
}.items():
    tree = ET.parse(path)
    counters = tree.find('t:ResultSummary/t:Counters', ns).attrib
    assert int(counters['failed']) == 0 and int(counters['passed']) > 0
    skipped = [x.attrib['testName'] for x in tree.findall('t:Results/t:UnitTestResult', ns)
               if x.attrib['outcome'] == 'NotExecuted']
    test_counts[name] = {'counters': counters, 'skippedNames': skipped}

browsers = {}
for filename in ['stage15-local-onnx-results.json', 'model-provenance-e2e-results.json',
                 'queue-page-size-e2e-results.json', 'stage13-e2e-results.json']:
    data = read(E / 'playwright' / filename)
    assert data['status'] == 'PASS', filename
    browsers[filename] = {'status': data['status'], 'checks': len(data.get('checks', data.get('results', [])))}

integrated = read(E / 'evaluation/integrated-adapter.json')
assert len(integrated['cases']) == 83 and integrated['liveFoundryCalls'] == 0
supplementary = read(E / 'evaluation/supplementary-persisted-inspection.json')
original = read(E / 'baseline/aml-v33-historical.json')
assert supplementary['amlIdentity']['sourceHash'] == original['sourceHash']
assert supplementary['amlIdentity']['extractionId'] == original['extractionId']
assert supplementary['applicationAdapterCounts'] == {'microsoft_foundry': 41, 'onnx_neurosymbolic_int8': 31}
assert read(E / 'integration/swagger-final.json')['status'] == 'PASS'

integrity = (ROOT / 'backend/Nlp/OnnxArtifactIntegrity.cs').read_text()
pinned = []
for name, relative in [('ModelSha256', 'backend/artifacts/bc5cdr_latest/onnx/model_int8.onnx'),
                       ('TokenizerSha256', 'backend/artifacts/bc5cdr_latest/clinical_tokenizer.json')]:
    expected = re.search(rf'{name}\s*=\s*"([a-f0-9]+)"', integrity).group(1).upper()
    path = ROOT / relative
    assert sha(path) == expected, f'Pinned artifact mismatch: {relative}'
    pinned.append(record(path))

assert sha(ROOT / 'backend/bin/Stage15/net10.0/Apollo.Backend.dll') == sha(
    ROOT / 'backend/bin/Stage15Serve/net10.0/Apollo.Backend.dll'), 'Served and tested assemblies differ'

required_reports = ['LOCAL_ONNX_ROOT_CAUSE_AUDIT', 'CLINICAL_EXTRACTION_ARCHITECTURE',
    'MEDICATION_EXTRACTION_REDESIGN', 'INVESTIGATION_EXTRACTION_REDESIGN', 'ASSERTION_AND_TEMPORALITY',
    'TERMINOLOGY_NORMALIZATION', 'BASELINE_VS_UPGRADED_BENCHMARK', 'CLINICAL_SAFETY_REGRESSION',
    'PERFORMANCE_RESULTS', 'IMPLEMENTATION_AND_VERIFICATION']
for name in required_reports:
    assert (ROOT / f'docs/APOLLO_STAGE15_{name}.md').is_file(), name

artifacts = [E / 'baseline/source-manifest.json', E / 'baseline/real-local-outputs.json',
    E / 'evaluation/metrics.json', E / 'evaluation/performance.json', E / 'evaluation/dataset-manifest.json',
    E / 'evaluation/integrated-adapter.json', E / 'evaluation/supplementary-persisted-inspection.json',
    E / 'integration/real-postgres-roundtrip.json', E / 'integration/swagger-final.json', E / 'final-verification-command.log',
    E / 'release-build-command.log', E / 'routing-final-command.log', E / 'postman-final-command.log',
    E / 'angular-test-command.log', E / 'angular-build-command.log',
    E / 'playwright/stage15-local-onnx-results.json', E / 'playwright/stage15-real-persisted-result.png',
    E / 'regression/test-results/stage15-full-final.trx', E / 'evaluation/test-results/stage15-ablation-final.trx']

result = {
    'date': '2026-11-25',
    'scope': 'Current Stage 15 source/evidence hashes; synthetic development engineering verification, not clinical clearance',
    'baselineSnapshotFilesVerified': len(baseline),
    'baselineManifest': 'docs/stage15-evidence/baseline/source-manifest.json',
    'sources': [record(ROOT / path) for path in sorted(SOURCES)],
    'reports': [record(path) for path in sorted((ROOT / 'docs').glob('APOLLO_STAGE15_*.md'))],
    'pinnedArtifacts': pinned,
    'testedAssembly': record(ROOT / 'backend/bin/Stage15/net10.0/Apollo.Backend.dll'),
    'releaseAssembly': record(ROOT / 'backend/bin/Release/net10.0/Apollo.Backend.dll'),
    'artifacts': [record(path) for path in artifacts],
    'tests': test_counts,
    'browserChecks': browsers,
    'liveFoundryCalls': 0,
    'costEvidence': 'Offline local routing and invalid test Foundry endpoint/key; unchanged application adapter counts; not a cloud billing audit',
    'originalApplicationAdapterCounts': supplementary['applicationAdapterCounts'],
    'clinicalDeploymentClearance': 'BLOCKED: independent clinical and reference-code adjudication unavailable',
    'realAzureBrokerSmoke': 'NOT TESTED: shared-queue cloud smoke deliberately excluded',
    'replacementModels': 'NOT TESTED locally; researched only',
}
(E / 'implementation-manifest.json').write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
print(f'PASS: {len(baseline)} original source snapshots and both frozen datasets unchanged; '
      f'{len(SOURCES)} Stage 15 source/fixture/tool hashes recorded; browser/TRX/artifact checks valid.')
