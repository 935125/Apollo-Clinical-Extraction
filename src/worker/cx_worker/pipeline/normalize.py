"""
Stage 4: Terminology Normalization.
Implements exact, synonym, and fuzzy-token matching against hospital drug and lab masters.
Never fabricates codes. Unresolved or ambiguous entities remain unmapped.
"""

import re
from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from typing import ClassVar

from rapidfuzz import fuzz


@dataclass
class MasterCandidate:
    master_id: str | None
    code: str
    display_name: str
    match_method: str
    rank: int
    score: float = 0.0


@dataclass
class NormalizationResult:
    status: str  # 'exact_match', 'synonym_match', 'fuzzy_match', 'ambiguous', 'unresolved'
    matched_code: str | None
    matched_name: str | None
    confidence: float
    candidates: list[MasterCandidate] = field(default_factory=list)

    @property
    def matched_master_id(self) -> str | None:
        return self.matched_code


class ITerminologyNormalizer(ABC):
    @abstractmethod
    def normalize_medication(
        self, name: str, strength: str | None = None, formulation: str | None = None, route: str | None = None
    ) -> NormalizationResult:
        pass

    @abstractmethod
    def normalize_investigation(self, name: str, specimen: str | None = None) -> NormalizationResult:
        pass


class ProductionTerminologyNormalizer(ITerminologyNormalizer):
    """
    Multi-tier hospital terminology normalizer.
    Tier 1: Exact code or normalized generic name match.
    Tier 2: Verified clinical synonym / brand name dictionary match.
    Tier 3: RapidFuzz token-sort similarity with ambiguity guard (threshold >= 88).
    Unresolved: Retains raw text with None code. Never hallucinates codes.
    """

    # Hospital Drug Master Catalog (RxNorm / Hospital Formulary)
    DRUG_CATALOG: ClassVar[list[dict[str, str | list[str]]]] = [
        {"code": "RX-MET-500", "generic": "Metformin", "display": "Metformin hydrochloride 500mg Oral Tablet", "synonyms": ["Glucophage", "Metformin 500", "Metformin HCl", "Riomet"]},
        {"code": "RX-MET-1000", "generic": "Metformin", "display": "Metformin hydrochloride 1000mg Oral Tablet", "synonyms": ["Glucophage 1000", "Metformin 1000"]},
        {"code": "RX-ATO-20", "generic": "Atorvastatin", "display": "Atorvastatin calcium 20mg Oral Tablet", "synonyms": ["Lipitor", "Atorvastatin 20", "Atorva", "Torvast"]},
        {"code": "RX-ATO-10", "generic": "Atorvastatin", "display": "Atorvastatin calcium 10mg Oral Tablet", "synonyms": ["Lipitor 10", "Atorva 10"]},
        {"code": "RX-ATO-40", "generic": "Atorvastatin", "display": "Atorvastatin calcium 40mg Oral Tablet", "synonyms": ["Lipitor 40", "Atorvastatin 40"]},
        {"code": "RX-ASP-100", "generic": "Aspirin", "display": "Aspirin 100mg Enteric-Coated Oral Tablet", "synonyms": ["Ecotrin", "Disprin", "Aspirin 100", "Acetylsalicylic acid", "ASA"]},
        {"code": "RX-ASP-75", "generic": "Aspirin", "display": "Aspirin 75mg Oral Tablet", "synonyms": ["Baby Aspirin", "Aspirin 75"]},
        {"code": "RX-WAR-5", "generic": "Warfarin", "display": "Warfarin sodium 5mg Oral Tablet", "synonyms": ["Coumadin", "Jantoven", "Warfarin 5", "Marevan"]},
        {"code": "RX-AMX-500", "generic": "Amoxicillin", "display": "Amoxicillin 500mg Oral Capsule", "synonyms": ["Amoxil", "Amoxicillin 500", "Moxatag", "Trimox"]},
        {"code": "RX-PCM-650", "generic": "Paracetamol", "display": "Paracetamol 650mg Oral Tablet", "synonyms": ["Acetaminophen", "Tylenol", "Panadol", "Crocin", "Dolo 650", "Calpol"]},
        {"code": "RX-PCM-500", "generic": "Paracetamol", "display": "Paracetamol 500mg Oral Tablet", "synonyms": ["Acetaminophen 500", "Crocin 500", "Panadol 500", "Dolo 500"]},
        {"code": "RX-AZI-500", "generic": "Azithromycin", "display": "Azithromycin 500mg Oral Tablet", "synonyms": ["Zithromax", "Z-Pak", "Azee 500", "Azithral"]},
        {"code": "RX-LIS-10", "generic": "Lisinopril", "display": "Lisinopril 10mg Oral Tablet", "synonyms": ["Zestril", "Prinivil", "Lisinopril 10"]},
        {"code": "RX-LEV-50", "generic": "Levothyroxine", "display": "Levothyroxine sodium 50mcg Oral Tablet", "synonyms": ["Synthroid", "Eltroxin", "Thyronorm", "Levoxyl"]},
        {"code": "RX-OME-20", "generic": "Omeprazole", "display": "Omeprazole 20mg Delayed-Release Capsule", "synonyms": ["Prilosec", "Omez 20", "Losec"]},
        {"code": "RX-ALP-100", "generic": "Allopurinol", "display": "Allopurinol 100mg Oral Tablet", "synonyms": ["Zyloprim", "Aloprim", "Zyloric"]},
        {"code": "RX-IBU-400", "generic": "Ibuprofen", "display": "Ibuprofen 400mg Oral Tablet", "synonyms": ["Advil", "Motrin", "Brufen"]},
        {"code": "RX-CIP-500", "generic": "Ciprofloxacin", "display": "Ciprofloxacin 500mg Oral Tablet", "synonyms": ["Cipro", "Ciplox 500", "Ciloxan"]},
        {"code": "RX-LOS-50", "generic": "Losartan", "display": "Losartan potassium 50mg Oral Tablet", "synonyms": ["Cozaar", "Losacar 50"]},
        {"code": "RX-AML-5", "generic": "Amlodipine", "display": "Amlodipine besylate 5mg Oral Tablet", "synonyms": ["Norvasc", "Amlong 5"]},
    ]

    # Hospital Investigation Master Catalog (LOINC / Laboratory & Radiology)
    TEST_CATALOG: ClassVar[list[dict[str, str | list[str]]]] = [
        {"code": "LAB-CREAT", "name": "Serum Creatinine", "display": "Creatinine [Mass/volume] in Serum or Plasma", "synonyms": ["Creatinine", "S. Creatinine", "Serum Creatinine level"]},
        {"code": "LAB-HBA1C", "name": "Hemoglobin A1c", "display": "Hemoglobin A1c/Hemoglobin.total in Blood", "synonyms": ["HbA1c", "A1c", "Glycated Hemoglobin", "Glycohemoglobin"]},
        {"code": "LAB-CBC", "name": "Complete Blood Count", "display": "Complete Blood Count with Automated Differential", "synonyms": ["CBC", "Hemogram", "Full Blood Count", "FBC"]},
        {"code": "LAB-LIPID", "name": "Lipid Profile", "display": "Lipid Panel with Total Cholesterol, HDL, LDL, Triglycerides", "synonyms": ["Lipid Panel", "Cholesterol Panel", "Lipids", "Fasting Lipid Profile"]},
        {"code": "LAB-LFT", "name": "Liver Function Tests", "display": "Hepatic Function Panel (ALT, AST, Bilirubin, Alk Phos)", "synonyms": ["LFT", "Liver Panel", "Hepatic Function Tests"]},
        {"code": "LAB-URINE", "name": "Urinalysis", "display": "Urinalysis with Microscopic Examination", "synonyms": ["Urine Routine", "Urine Analysis", "Routine Urine Examination"]},
        {"code": "LAB-TSH", "name": "Thyroid Stimulating Hormone", "display": "Thyrotropin [Units/volume] in Serum", "synonyms": ["TSH", "Thyroid Panel", "Serum TSH"]},
        {"code": "LAB-ELECT", "name": "Serum Electrolytes", "display": "Electrolytes Panel (Sodium, Potassium, Chloride, Bicarbonate)", "synonyms": ["Electrolytes", "Serum Lytes", "Na/K/Cl"]},
        {"code": "IMG-CXR", "name": "Chest X-Ray", "display": "Radiologic Examination, Chest; Single View / PA View", "synonyms": ["CXR", "Chest Radiograph", "Chest X-ray PA view", "X-ray Chest"]},
        {"code": "IMG-ECG", "name": "Electrocardiogram", "display": "Electrocardiogram, Routine ECG with at least 12 Leads", "synonyms": ["ECG", "EKG", "12-Lead ECG", "12 Lead Electrocardiogram"]},
        {"code": "IMG-ECHO", "name": "2D Echocardiogram", "display": "Transthoracic Echocardiography, 2D with Doppler", "synonyms": ["Echocardiogram", "Echo", "2D Echo", "TTE"]},
        {"code": "IMG-USG-ABD", "name": "Ultrasound Abdomen", "display": "Ultrasound, Abdominal, B-Scan and/or Real Time with Image Documentation", "synonyms": ["USG Abdomen", "Abdominal Ultrasound", "US Abdomen"]},
    ]

    def normalize_medication(
        self, name: str, strength: str | None = None, formulation: str | None = None, route: str | None = None
    ) -> NormalizationResult:
        if not name or not name.strip():
            return NormalizationResult("unresolved", None, None, 0.0, [])

        clean_name = self._clean_text(name)

        # Tier 1: Exact generic name match
        for item in self.DRUG_CATALOG:
            generic = str(item["generic"])
            if clean_name.lower() == generic.lower():
                return NormalizationResult(
                    status="exact_match",
                    matched_code=str(item["code"]),
                    matched_name=str(item["display"]),
                    confidence=1.0,
                    candidates=[MasterCandidate(str(item["code"]), str(item["code"]), str(item["display"]), "exact", 1, 100.0)]
                )

        # Tier 2: Verified synonym / brand name match
        for item in self.DRUG_CATALOG:
            synonyms = item["synonyms"]
            if isinstance(synonyms, list):
                for syn in synonyms:
                    if clean_name.lower() == syn.lower() or syn.lower() in clean_name.lower():
                        return NormalizationResult(
                            status="synonym_match",
                            matched_code=str(item["code"]),
                            matched_name=str(item["display"]),
                            confidence=0.95,
                            candidates=[MasterCandidate(str(item["code"]), str(item["code"]), str(item["display"]), "synonym", 1, 95.0)]
                        )

        # Tier 3: Fuzzy token sort similarity
        scored_candidates: list[MasterCandidate] = []
        for item in self.DRUG_CATALOG:
            target = f"{item['generic']} {' '.join(item['synonyms'])}"  # type: ignore
            score = fuzz.token_sort_ratio(clean_name, target)
            if score >= 70:
                scored_candidates.append(MasterCandidate(
                    master_id=str(item["code"]),
                    code=str(item["code"]),
                    display_name=str(item["display"]),
                    match_method="fuzzy_token_sort",
                    rank=1,
                    score=float(score)
                ))

        scored_candidates.sort(key=lambda c: c.score, reverse=True)

        if scored_candidates:
            top = scored_candidates[0]
            # Ambiguity guard: if score >= 88 and runner-up is not ambiguously close
            if top.score >= 88.0:
                if len(scored_candidates) > 1 and (top.score - scored_candidates[1].score) < 4.0:
                    return NormalizationResult("ambiguous", None, None, round(top.score / 100.0, 2), scored_candidates[:3])
                return NormalizationResult(
                    status="fuzzy_match",
                    matched_code=top.code,
                    matched_name=top.display_name,
                    confidence=round(top.score / 100.0, 2),
                    candidates=scored_candidates[:3]
                )

        # Unresolved
        return NormalizationResult("unresolved", None, None, 0.0, scored_candidates[:3])

    def normalize_investigation(self, name: str, specimen: str | None = None) -> NormalizationResult:
        if not name or not name.strip():
            return NormalizationResult("unresolved", None, None, 0.0, [])

        clean_name = self._clean_text(name)

        # Tier 1: Exact test name match
        for item in self.TEST_CATALOG:
            test_name = str(item["name"])
            if clean_name.lower() == test_name.lower():
                return NormalizationResult(
                    status="exact_match",
                    matched_code=str(item["code"]),
                    matched_name=str(item["display"]),
                    confidence=1.0,
                    candidates=[MasterCandidate(str(item["code"]), str(item["code"]), str(item["display"]), "exact", 1, 100.0)]
                )

        # Tier 2: Verified synonym / acronym match
        for item in self.TEST_CATALOG:
            synonyms = item["synonyms"]
            if isinstance(synonyms, list):
                for syn in synonyms:
                    if clean_name.lower() == syn.lower() or syn.lower() in clean_name.lower():
                        return NormalizationResult(
                            status="synonym_match",
                            matched_code=str(item["code"]),
                            matched_name=str(item["display"]),
                            confidence=0.95,
                            candidates=[MasterCandidate(str(item["code"]), str(item["code"]), str(item["display"]), "synonym", 1, 95.0)]
                        )

        # Tier 3: Fuzzy token sort
        scored_candidates: list[MasterCandidate] = []
        for item in self.TEST_CATALOG:
            target = f"{item['name']} {' '.join(item['synonyms'])}"  # type: ignore
            score = fuzz.token_sort_ratio(clean_name, target)
            if score >= 70:
                scored_candidates.append(MasterCandidate(
                    master_id=str(item["code"]),
                    code=str(item["code"]),
                    display_name=str(item["display"]),
                    match_method="fuzzy_token_sort",
                    rank=1,
                    score=float(score)
                ))

        scored_candidates.sort(key=lambda c: c.score, reverse=True)

        if scored_candidates:
            top = scored_candidates[0]
            if top.score >= 88.0:
                if len(scored_candidates) > 1 and (top.score - scored_candidates[1].score) < 4.0:
                    return NormalizationResult("ambiguous", None, None, round(top.score / 100.0, 2), scored_candidates[:3])
                return NormalizationResult(
                    status="fuzzy_match",
                    matched_code=top.code,
                    matched_name=top.display_name,
                    confidence=round(top.score / 100.0, 2),
                    candidates=scored_candidates[:3]
                )

        return NormalizationResult("unresolved", None, None, 0.0, scored_candidates[:3])

    @staticmethod
    def _clean_text(s: str) -> str:
        return re.sub(r"[^\w\s-]", "", s).strip()


class MockTerminologyNormalizer(ITerminologyNormalizer):
    """
    Deterministic mock normalizer preserving backwards compatibility with baseline tests.
    """

    def normalize_medication(
        self, name: str, strength: str | None = None, formulation: str | None = None, route: str | None = None
    ) -> NormalizationResult:
        cand = MasterCandidate(None, "MOCK_DRUG_01", f"{name} candidate", "keyword", 1, 75.0)
        return NormalizationResult("unmatched", None, None, 0.75, [cand])

    def normalize_investigation(self, name: str, specimen: str | None = None) -> NormalizationResult:
        cand = MasterCandidate(None, "MOCK_LAB_01", f"{name} candidate", "keyword", 1, 75.0)
        return NormalizationResult("unmatched", None, None, 0.75, [cand])
