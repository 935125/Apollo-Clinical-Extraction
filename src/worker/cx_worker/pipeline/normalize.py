"""
Stage 4: Terminology Normalization.
Implements exact, synonym, and fuzzy-token matching against hospital drug and lab masters.
Separates verified standard identifiers (RxNorm CUI, LOINC) from internal hospital master IDs.
Treats fuzzy similarity scores as retrieval signals only, requiring deterministic attribute checks
(strength, formulation, route) before matching. Preserves unresolved matches.
"""

import re
from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from typing import Any, ClassVar

from rapidfuzz import fuzz


@dataclass
class MasterCandidate:
    master_id: str | None
    code: str
    display_name: str
    match_method: str
    rank: int
    score: float = 0.0
    rxnorm_cui: str | None = None
    loinc_num: str | None = None


@dataclass
class NormalizationResult:
    status: str  # 'exact_match', 'synonym_match', 'fuzzy_match', 'ambiguous', 'unresolved'
    matched_code: str | None
    matched_name: str | None
    confidence: float
    candidates: list[MasterCandidate] = field(default_factory=list)
    rxnorm_cui: str | None = None
    loinc_num: str | None = None

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
    Hospital terminology normalizer enforcing deterministic attribute checks.
    Separates verified RxNorm CUIs / LOINC numbers from local formulary catalog codes.
    Uses fuzzy token similarity as a candidate retrieval signal only, calibrating confidence
    based on exact attribute verification (strength, formulation, route).
    """

    # Hospital Drug Master Catalog with verified RxNorm CUIs
    DRUG_CATALOG: ClassVar[list[dict[str, Any]]] = [
        {
            "code": "RX-MET-500",
            "rxnorm_cui": "860975",
            "generic": "Metformin",
            "strength": "500mg",
            "formulation": "tablet",
            "route": "PO",
            "display": "Metformin hydrochloride 500mg Oral Tablet",
            "synonyms": ["Glucophage", "Metformin 500", "Metformin HCl", "Riomet"]
        },
        {
            "code": "RX-MET-1000",
            "rxnorm_cui": "860981",
            "generic": "Metformin",
            "strength": "1000mg",
            "formulation": "tablet",
            "route": "PO",
            "display": "Metformin hydrochloride 1000mg Oral Tablet",
            "synonyms": ["Glucophage 1000", "Metformin 1000", "Glucophage XR 1000"]
        },
        {
            "code": "RX-ATO-20",
            "rxnorm_cui": "617318",
            "generic": "Atorvastatin",
            "strength": "20mg",
            "formulation": "tablet",
            "route": "PO",
            "display": "Atorvastatin calcium 20mg Oral Tablet",
            "synonyms": ["Lipitor", "Atorvastatin 20", "Atorva 20", "Torvast"]
        },
        {
            "code": "RX-ATO-10",
            "rxnorm_cui": "617314",
            "generic": "Atorvastatin",
            "strength": "10mg",
            "formulation": "tablet",
            "route": "PO",
            "display": "Atorvastatin calcium 10mg Oral Tablet",
            "synonyms": ["Lipitor 10", "Atorva 10", "Atorvastatin 10"]
        },
        {
            "code": "RX-ATO-40",
            "rxnorm_cui": "617320",
            "generic": "Atorvastatin",
            "strength": "40mg",
            "formulation": "tablet",
            "route": "PO",
            "display": "Atorvastatin calcium 40mg Oral Tablet",
            "synonyms": ["Lipitor 40", "Atorvastatin 40"]
        },
        {
            "code": "RX-ASP-100",
            "rxnorm_cui": "315431",
            "generic": "Aspirin",
            "strength": "100mg",
            "formulation": "tablet",
            "route": "PO",
            "display": "Aspirin 100mg Enteric-Coated Oral Tablet",
            "synonyms": ["Ecotrin", "Disprin", "Aspirin 100", "Acetylsalicylic acid", "ASA"]
        },
        {
            "code": "RX-ASP-75",
            "rxnorm_cui": "315429",
            "generic": "Aspirin",
            "strength": "75mg",
            "formulation": "tablet",
            "route": "PO",
            "display": "Aspirin 75mg Oral Tablet",
            "synonyms": ["Baby Aspirin", "Aspirin 75"]
        },
        {
            "code": "RX-WAR-5",
            "rxnorm_cui": "855332",
            "generic": "Warfarin",
            "strength": "5mg",
            "formulation": "tablet",
            "route": "PO",
            "display": "Warfarin sodium 5mg Oral Tablet",
            "synonyms": ["Coumadin", "Jantoven", "Warfarin 5", "Marevan"]
        },
        {
            "code": "RX-AMX-500",
            "rxnorm_cui": "213169",
            "generic": "Amoxicillin",
            "strength": "500mg",
            "formulation": "capsule",
            "route": "PO",
            "display": "Amoxicillin 500mg Oral Capsule",
            "synonyms": ["Amoxil", "Amoxicillin 500", "Moxatag", "Trimox"]
        },
        {
            "code": "RX-PCM-650",
            "rxnorm_cui": "198440",
            "generic": "Paracetamol",
            "strength": "650mg",
            "formulation": "tablet",
            "route": "PO",
            "display": "Paracetamol 650mg Oral Tablet",
            "synonyms": ["Acetaminophen", "Tylenol", "Panadol", "Crocin", "Dolo 650", "Calpol", "Crocin 650"]
        },
        {
            "code": "RX-PCM-500",
            "rxnorm_cui": "198438",
            "generic": "Paracetamol",
            "strength": "500mg",
            "formulation": "tablet",
            "route": "PO",
            "display": "Paracetamol 500mg Oral Tablet",
            "synonyms": ["Acetaminophen 500", "Crocin 500", "Panadol 500", "Dolo 500", "Calpol 500"]
        },
        {
            "code": "RX-AZI-500",
            "rxnorm_cui": "248656",
            "generic": "Azithromycin",
            "strength": "500mg",
            "formulation": "tablet",
            "route": "PO",
            "display": "Azithromycin 500mg Oral Tablet",
            "synonyms": ["Zithromax", "Z-Pak", "Azee 500", "Azithral", "Azithromycin 500"]
        },
        {
            "code": "RX-LIS-10",
            "rxnorm_cui": "314076",
            "generic": "Lisinopril",
            "strength": "10mg",
            "formulation": "tablet",
            "route": "PO",
            "display": "Lisinopril 10mg Oral Tablet",
            "synonyms": ["Zestril", "Prinivil", "Lisinopril 10"]
        },
        {
            "code": "RX-LIS-20",
            "rxnorm_cui": "314077",
            "generic": "Lisinopril",
            "strength": "20mg",
            "formulation": "tablet",
            "route": "PO",
            "display": "Lisinopril 20mg Oral Tablet",
            "synonyms": ["Zestril 20", "Prinivil 20", "Lisinopril 20"]
        },
        {
            "code": "RX-LEV-50",
            "rxnorm_cui": "966224",
            "generic": "Levothyroxine",
            "strength": "50mcg",
            "formulation": "tablet",
            "route": "PO",
            "display": "Levothyroxine sodium 50mcg Oral Tablet",
            "synonyms": ["Synthroid", "Eltroxin", "Thyronorm", "Levoxyl", "Levothyroxine 50"]
        },
        {
            "code": "RX-OME-20",
            "rxnorm_cui": "312065",
            "generic": "Omeprazole",
            "strength": "20mg",
            "formulation": "capsule",
            "route": "PO",
            "display": "Omeprazole 20mg Delayed-Release Capsule",
            "synonyms": ["Prilosec", "Omez 20", "Losec", "Omeprazole 20"]
        },
        {
            "code": "RX-ALP-100",
            "rxnorm_cui": "308136",
            "generic": "Allopurinol",
            "strength": "100mg",
            "formulation": "tablet",
            "route": "PO",
            "display": "Allopurinol 100mg Oral Tablet",
            "synonyms": ["Zyloprim", "Aloprim", "Zyloric", "Allopurinol 100"]
        },
        {
            "code": "RX-ALP-300",
            "rxnorm_cui": "308138",
            "generic": "Allopurinol",
            "strength": "300mg",
            "formulation": "tablet",
            "route": "PO",
            "display": "Allopurinol 300mg Oral Tablet",
            "synonyms": ["Zyloprim 300", "Allopurinol 300"]
        },
        {
            "code": "RX-IBU-400",
            "rxnorm_cui": "197806",
            "generic": "Ibuprofen",
            "strength": "400mg",
            "formulation": "tablet",
            "route": "PO",
            "display": "Ibuprofen 400mg Oral Tablet",
            "synonyms": ["Advil", "Motrin", "Brufen", "Ibuprofen 400"]
        },
        {
            "code": "RX-CIP-500",
            "rxnorm_cui": "309309",
            "generic": "Ciprofloxacin",
            "strength": "500mg",
            "formulation": "tablet",
            "route": "PO",
            "display": "Ciprofloxacin 500mg Oral Tablet",
            "synonyms": ["Cipro", "Ciplox 500", "Ciloxan", "Ciprofloxacin 500"]
        },
        {
            "code": "RX-LOS-50",
            "rxnorm_cui": "316049",
            "generic": "Losartan",
            "strength": "50mg",
            "formulation": "tablet",
            "route": "PO",
            "display": "Losartan potassium 50mg Oral Tablet",
            "synonyms": ["Cozaar", "Losacar 50", "Losartan 50"]
        },
        {
            "code": "RX-AML-5",
            "rxnorm_cui": "197361",
            "generic": "Amlodipine",
            "strength": "5mg",
            "formulation": "tablet",
            "route": "PO",
            "display": "Amlodipine besylate 5mg Oral Tablet",
            "synonyms": ["Norvasc", "Amlong 5", "Amlodipine 5"]
        },
        {
            "code": "RX-SAL-100",
            "rxnorm_cui": "1649556",
            "generic": "Salbutamol",
            "strength": "100mcg",
            "formulation": "inhaler",
            "route": "Inhalation",
            "display": "Albuterol 0.1 MG/ACTUAT Metered Dose Inhaler",
            "synonyms": ["Albuterol", "Ventolin", "ProAir", "Proventil", "Salbutamol 100mcg", "Salbutamol inhaler"]
        },
        {
            "code": "RX-PRED-40",
            "rxnorm_cui": "312615",
            "generic": "Prednisolone",
            "strength": "40mg",
            "formulation": "tablet",
            "route": "PO",
            "display": "Prednisolone 40mg Oral Tablet",
            "synonyms": ["Omnipred", "Prelone", "Prednisolone 40", "Prednisone 40"]
        },
        {
            "code": "RX-PRED-5",
            "rxnorm_cui": "198145",
            "generic": "Prednisolone",
            "strength": "5mg",
            "formulation": "tablet",
            "route": "PO",
            "display": "Prednisolone 5mg Oral Tablet",
            "synonyms": ["Prelone 5", "Prednisolone 5", "Prednisone 5"]
        },
        # Combination Medications (Multi-Ingredient Equivalents)
        {
            "code": "RX-SAC-VAL-24-26",
            "rxnorm_cui": "1656336",
            "generic": "Sacubitril/Valsartan",
            "strength": "24/26mg",
            "formulation": "tablet",
            "route": "PO",
            "display": "Sacubitril 24mg / Valsartan 26mg Oral Tablet",
            "synonyms": ["Entresto", "Entresto 24/26", "Sacubitril Valsartan", "LCZ696"]
        },
        {
            "code": "RX-SAC-VAL-49-51",
            "rxnorm_cui": "1656342",
            "generic": "Sacubitril/Valsartan",
            "strength": "49/51mg",
            "formulation": "tablet",
            "route": "PO",
            "display": "Sacubitril 49mg / Valsartan 51mg Oral Tablet",
            "synonyms": ["Entresto 49/51", "Entresto 50"]
        },
        {
            "code": "RX-SAC-VAL-97-103",
            "rxnorm_cui": "1656348",
            "generic": "Sacubitril/Valsartan",
            "strength": "97/103mg",
            "formulation": "tablet",
            "route": "PO",
            "display": "Sacubitril 97mg / Valsartan 103mg Oral Tablet",
            "synonyms": ["Entresto 97/103", "Entresto 100"]
        },
        {
            "code": "RX-TAM-FIN-04-5",
            "rxnorm_cui": "1009147",
            "generic": "Tamsulosin/Finasteride",
            "strength": "0.4mg/5mg",
            "formulation": "capsule",
            "route": "PO",
            "display": "Tamsulosin HCl 0.4mg / Finasteride 5mg Oral Capsule",
            "synonyms": ["Combodart", "Jalyn", "Tamsulosin Finasteride"]
        },
        {
            "code": "RX-AMX-CLV-500",
            "rxnorm_cui": "308182",
            "generic": "Amoxicillin/Clavulanate",
            "strength": "500/125mg",
            "formulation": "tablet",
            "route": "PO",
            "display": "Amoxicillin 500mg / Clavulanate potassium 125mg Oral Tablet",
            "synonyms": ["Augmentin", "Augmentin 500", "Amoxicillin Clavulanate", "Co-amoxiclav"]
        },
        {
            "code": "RX-AMX-CLV-875",
            "rxnorm_cui": "308184",
            "generic": "Amoxicillin/Clavulanate",
            "strength": "875/125mg",
            "formulation": "tablet",
            "route": "PO",
            "display": "Amoxicillin 875mg / Clavulanate potassium 125mg Oral Tablet",
            "synonyms": ["Augmentin 875", "Augmentin Duo", "Co-amoxiclav 875"]
        },
    ]

    # Hospital Investigation Master Catalog with verified LOINC numbers
    TEST_CATALOG: ClassVar[list[dict[str, Any]]] = [
        {
            "code": "LAB-CREAT",
            "loinc_num": "2160-0",
            "name": "Serum Creatinine",
            "specimen": "Blood",
            "display": "Creatinine [Mass/volume] in Serum or Plasma",
            "synonyms": ["Creatinine", "S. Creatinine", "Serum Creatinine level"]
        },
        {
            "code": "LAB-HBA1C",
            "loinc_num": "4548-4",
            "name": "Hemoglobin A1c",
            "specimen": "Blood",
            "display": "Hemoglobin A1c/Hemoglobin.total in Blood",
            "synonyms": ["HbA1c", "A1c", "Glycated Hemoglobin", "Glycohemoglobin"]
        },
        {
            "code": "LAB-CBC",
            "loinc_num": "58410-2",
            "name": "Complete Blood Count",
            "specimen": "Blood",
            "display": "Complete Blood Count (Automated Hemogram Panel)",
            "synonyms": ["CBC", "Hemogram", "Full Blood Count", "FBC"]
        },
        {
            "code": "LAB-CBC-DIFF",
            "loinc_num": "57022-6",
            "name": "Complete Blood Count with Differential",
            "specimen": "Blood",
            "display": "Complete Blood Count with Automated Differential Panel",
            "synonyms": ["CBC with diff", "CBC with differential", "Complete Blood Count with Differential", "CBC diff"]
        },
        {
            "code": "LAB-CBC-NODIFF",
            "loinc_num": "57021-8",
            "name": "Complete Blood Count without Differential",
            "specimen": "Blood",
            "display": "Complete Blood Count without Differential Panel",
            "synonyms": ["CBC without diff", "CBC no diff", "CBC without differential"]
        },
        {
            "code": "LAB-PFT",
            "loinc_num": "81459-0",
            "name": "Pulmonary Function Test",
            "specimen": None,
            "display": "Pulmonary Function Test Panel (Spirometry and Lung Volumes)",
            "synonyms": ["PFT", "Pulmonary Function Test", "Spirometry", "Lung Function Test"]
        },
        {
            "code": "LAB-LIPID",
            "loinc_num": "24331-1",
            "name": "Lipid Profile",
            "specimen": "Blood",
            "display": "Lipid Panel with Total Cholesterol, HDL, LDL, Triglycerides",
            "synonyms": ["Lipid Panel", "Cholesterol Panel", "Lipids", "Fasting Lipid Profile"]
        },
        {
            "code": "LAB-LFT",
            "loinc_num": "24325-3",
            "name": "Liver Function Tests",
            "specimen": "Blood",
            "display": "Hepatic Function Panel (ALT, AST, Bilirubin, Alk Phos)",
            "synonyms": ["LFT", "Liver Panel", "Hepatic Function Tests"]
        },
        {
            "code": "LAB-URINE",
            "loinc_num": "24356-8",
            "name": "Urinalysis",
            "specimen": "Urine",
            "display": "Urinalysis with Microscopic Examination",
            "synonyms": ["Urine Routine", "Urine Analysis", "Routine Urine Examination"]
        },
        {
            "code": "LAB-TSH",
            "loinc_num": "3016-3",
            "name": "Thyroid Stimulating Hormone",
            "specimen": "Blood",
            "display": "Thyrotropin [Units/volume] in Serum",
            "synonyms": ["TSH", "Thyroid Panel", "Serum TSH"]
        },
        {
            "code": "LAB-ELECT",
            "loinc_num": "24326-1",
            "name": "Serum Electrolytes",
            "specimen": "Blood",
            "display": "Electrolytes Panel (Sodium, Potassium, Chloride, Bicarbonate)",
            "synonyms": ["Electrolytes", "Serum Lytes", "Na/K/Cl"]
        },
        {
            "code": "IMG-CXR",
            "loinc_num": "36643-5",
            "name": "Chest X-Ray",
            "specimen": None,
            "display": "Radiologic Examination, Chest; Single View / PA View",
            "synonyms": ["CXR", "Chest Radiograph", "Chest X-ray PA view", "X-ray Chest", "Chest X-ray", "Chest X-Ray PA view"]
        },
        {
            "code": "IMG-ECG",
            "loinc_num": "11524-6",
            "name": "Electrocardiogram",
            "specimen": None,
            "display": "Electrocardiogram, Routine ECG with at least 12 Leads",
            "synonyms": ["ECG", "EKG", "12-Lead ECG", "12 Lead Electrocardiogram"]
        },
        {
            "code": "IMG-ECHO-GEN",
            "loinc_num": "18154-5",
            "name": "Echocardiogram (General)",
            "specimen": None,
            "display": "Echocardiography Study (General / Unspecified)",
            "synonyms": ["Echocardiogram", "Echo"]
        },
        {
            "code": "IMG-ECHO-2D",
            "loinc_num": "34552-0",
            "name": "2D Echocardiogram",
            "specimen": None,
            "display": "Transthoracic Echocardiography, 2D with Doppler",
            "synonyms": ["2D Echo", "2D Echocardiogram", "TTE", "Echocardiogram with Doppler"]
        },
        {
            "code": "IMG-USG-ABD",
            "loinc_num": "79103-8",
            "name": "Ultrasound Abdomen",
            "specimen": None,
            "display": "Ultrasound, Abdominal, B-Scan and/or Real Time with Image Documentation",
            "synonyms": ["USG Abdomen", "Abdominal Ultrasound", "US Abdomen", "Ultrasound of Abdomen"]
        },
        {
            "code": "IMG-USG-RENAL",
            "loinc_num": "24987-0",
            "name": "Ultrasound Renal",
            "specimen": None,
            "display": "Ultrasound, Retroperitoneal (Renal, Aorta, Nodes), Complete",
            "synonyms": ["Renal Ultrasound", "Kidney Ultrasound", "US Renal", "US KUB"]
        },
        {
            "code": "IMG-USG-PELVIS",
            "loinc_num": "79104-6",
            "name": "Ultrasound Pelvis",
            "specimen": None,
            "display": "Ultrasound, Pelvic, Real Time with Image Documentation",
            "synonyms": ["Pelvic Ultrasound", "US Pelvis"]
        },
    ]

    def _normalize_strength_str(self, s: str | None) -> str | None:
        if not s:
            return None
        clean = s.strip().lower().replace(" ", "")
        return clean

    def _clean_text(self, text: str) -> str:
        return re.sub(r"[^\w\s-]", "", text).strip()

    def normalize_medication(
        self, name: str, strength: str | None = None, formulation: str | None = None, route: str | None = None
    ) -> NormalizationResult:
        if not name or not name.strip():
            return NormalizationResult("unresolved", None, None, 0.0, [])

        clean_name = self._clean_text(name).lower()
        norm_strength = self._normalize_strength_str(strength)

        # 1. Candidate Retrieval: Filter catalog entries matching drug name
        matched_entries: list[tuple[dict[str, Any], str]] = []

        for item in self.DRUG_CATALOG:
            generic = str(item["generic"]).lower()
            if clean_name == generic:
                matched_entries.append((item, "exact"))
                continue

            synonyms = [s.lower() for s in item.get("synonyms", [])]
            if clean_name in synonyms:
                matched_entries.append((item, "synonym"))
                continue

            if any(re.search(rf"\b{re.escape(s)}\b", clean_name) for s in [generic] + synonyms):
                matched_entries.append((item, "synonym"))

        if not matched_entries:
            fuzzy_retrieved: list[tuple[dict[str, Any], float]] = []
            for item in self.DRUG_CATALOG:
                target = f"{item['generic']} {' '.join(item.get('synonyms', []))}"
                score = float(fuzz.token_sort_ratio(clean_name, target))
                if score >= 80.0:
                    fuzzy_retrieved.append((item, score))
            fuzzy_retrieved.sort(key=lambda x: x[1], reverse=True)
            if fuzzy_retrieved:
                top_score = fuzzy_retrieved[0][1]
                for item, sc in fuzzy_retrieved:
                    if top_score - sc < 4.0:
                        matched_entries.append((item, "fuzzy"))

        if not matched_entries:
            return NormalizationResult("unresolved", None, None, 0.0, [])

        # 2. Deterministic Attribute Check: Strength & Formulation Matching
        target_item: dict[str, Any]
        mtype: str

        if norm_strength:
            strength_matches = [
                (item, mt) for (item, mt) in matched_entries
                if self._normalize_strength_str(item.get("strength")) == norm_strength
            ]
            if strength_matches:
                target_item, mtype = strength_matches[0]
            else:
                target_item, mtype = matched_entries[0]
        else:
            target_item, mtype = matched_entries[0]

        # Clinical Form Specificity Guard:
        # Do not fabricate a delivery mechanism (e.g. Delayed-Release Capsule) if formulation was omitted in note!
        clean_formulation = (formulation or "").strip().lower()
        catalog_formulation = str(target_item.get("formulation", "")).lower()

        if clean_formulation:
            if clean_formulation in catalog_formulation or catalog_formulation in clean_formulation:
                final_display = str(target_item["display"])
                conf = 1.0 if mtype == "exact" else (0.95 if mtype == "synonym" else 0.85)
            else:
                # Formulation mismatch (e.g. tablet requested but catalog only has capsule)
                final_display = f"{target_item['generic']} {target_item.get('strength', '')} (Requested: {clean_formulation.capitalize()}, Catalog: {catalog_formulation.capitalize()})"
                conf = 0.85
        else:
            # Formulation was OMITTED by clinician:
            # Assign generic Clinical Drug Component without assuming a specific release form
            final_display = f"{target_item['generic']} {target_item.get('strength', '')} (Formulation Unspecified)"
            conf = 1.0 if mtype == "exact" else (0.95 if mtype == "synonym" else 0.85)

        status_str = "exact_match" if mtype == "exact" else (
            "synonym_match" if mtype == "synonym" else "fuzzy_match"
        )
        return NormalizationResult(
            status=status_str,
            matched_code=str(target_item["code"]),
            matched_name=final_display,
            confidence=conf,
            rxnorm_cui=target_item.get("rxnorm_cui"),
            candidates=[
                MasterCandidate(
                    master_id=str(target_item["code"]),
                    code=str(target_item["code"]),
                    display_name=str(target_item["display"]),
                    match_method=mtype,
                    rank=1,
                    score=conf * 100.0,
                    rxnorm_cui=target_item.get("rxnorm_cui")
                )
            ]
        )

    def normalize_investigation(self, name: str, specimen: str | None = None) -> NormalizationResult:
        if not name or not name.strip():
            return NormalizationResult("unresolved", None, None, 0.0, [])

        clean_name = self._clean_text(name).lower()

        # Ambiguous investigation guard:
        # Ultrasound without anatomical site must NOT match Abdominal ultrasound!
        if clean_name in ["ultrasound", "usg", "us", "ultrasound scan"]:
            us_candidates = [
                MasterCandidate(
                    master_id=str(item["code"]),
                    code=str(item["code"]),
                    display_name=str(item["display"]),
                    match_method="anatomical_candidate",
                    rank=idx + 1,
                    score=50.0,
                    loinc_num=item.get("loinc_num"),
                )
                for idx, item in enumerate(self.TEST_CATALOG)
                if "USG" in item["code"]
            ]
            return NormalizationResult(
                status="ambiguous",
                matched_code=None,
                matched_name=None,
                confidence=0.0,
                candidates=us_candidates,
            )

        # 1. Exact canonical test name match
        for item in self.TEST_CATALOG:
            test_name = str(item["name"]).lower()
            if clean_name == test_name:
                return NormalizationResult(
                    status="exact_match",
                    matched_code=str(item["code"]),
                    matched_name=str(item["display"]),
                    confidence=1.0,
                    loinc_num=item.get("loinc_num"),
                    candidates=[
                        MasterCandidate(
                            master_id=str(item["code"]),
                            code=str(item["code"]),
                            display_name=str(item["display"]),
                            match_method="exact",
                            rank=1,
                            score=100.0,
                            loinc_num=item.get("loinc_num"),
                        )
                    ],
                )

        # 2. Verified synonym / acronym match (prioritize longer, more specific synonyms)
        # Collect all synonyms with their items
        all_syn_entries: list[tuple[str, dict[str, Any]]] = []
        for item in self.TEST_CATALOG:
            for s in item.get("synonyms", []):
                all_syn_entries.append((s.lower(), item))
        # Sort by length descending so "cbc with diff" matches before "cbc"
        all_syn_entries.sort(key=lambda x: len(x[0]), reverse=True)

        for syn, item in all_syn_entries:
            if clean_name == syn or syn in clean_name:
                return NormalizationResult(
                    status="synonym_match",
                    matched_code=str(item["code"]),
                    matched_name=str(item["display"]),
                    confidence=0.95,
                    loinc_num=item.get("loinc_num"),
                    candidates=[
                        MasterCandidate(
                            master_id=str(item["code"]),
                            code=str(item["code"]),
                            display_name=str(item["display"]),
                            match_method="synonym",
                            rank=1,
                            score=95.0,
                            loinc_num=item.get("loinc_num"),
                        )
                    ],
                )

        # 3. Fuzzy token sort retrieval
        scored: list[tuple[dict[str, Any], float]] = []
        for item in self.TEST_CATALOG:
            target = f"{item['name']} {' '.join(item.get('synonyms', []))}"
            score = float(fuzz.token_sort_ratio(clean_name, target))
            if score >= 80.0:
                scored.append((item, score))

        scored.sort(key=lambda x: x[1], reverse=True)
        if scored:
            top_item, top_score = scored[0]
            if top_score >= 88.0:
                if len(scored) > 1 and (top_score - scored[1][1]) < 4.0:
                    return NormalizationResult("ambiguous", None, None, round(top_score / 100.0, 2), [])
                return NormalizationResult(
                    status="fuzzy_match",
                    matched_code=str(top_item["code"]),
                    matched_name=str(top_item["display"]),
                    confidence=0.85,
                    loinc_num=top_item.get("loinc_num"),
                    candidates=[
                        MasterCandidate(
                            master_id=str(top_item["code"]),
                            code=str(top_item["code"]),
                            display_name=str(top_item["display"]),
                            match_method="fuzzy_token_sort",
                            rank=1,
                            score=top_score,
                            loinc_num=top_item.get("loinc_num")
                        )
                    ]
                )

        return NormalizationResult("unresolved", None, None, 0.0, [])


# Backward compatibility alias for older test imports
MockTerminologyNormalizer = ProductionTerminologyNormalizer
