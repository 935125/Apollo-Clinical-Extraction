"""
Stage 4: Normalization Boundary Interface.
Defines interfaces and mock normalization repository for Milestone 4 verification.
Real multi-tier fuzzy/trigram lookup against PostgreSQL masters belongs to Milestone 5.
"""

from abc import ABC, abstractmethod
from dataclasses import dataclass


@dataclass
class MasterCandidate:
    master_id: str | None
    code: str
    display_name: str
    match_method: str
    rank: int
    attribute_check: str  # 'compatible', 'incompatible'


@dataclass
class NormalizationResult:
    status: str  # 'matched', 'ambiguous', 'unmatched'
    matched_master_id: str | None
    candidates: list[MasterCandidate]


class ITerminologyNormalizer(ABC):
    @abstractmethod
    def normalize_medication(
        self, name: str, strength: str | None, formulation: str | None, route: str | None
    ) -> NormalizationResult:
        pass

    @abstractmethod
    def normalize_investigation(self, name: str, specimen: str | None) -> NormalizationResult:
        pass


class MockTerminologyNormalizer(ITerminologyNormalizer):
    """
    Deterministic mock normalizer for Milestone 4 pipeline testing.
    In Milestone 4, master tables are not yet loaded with live hospital codes,
    so status is 'unmatched' (or matched only if a valid FK exists).
    """

    def normalize_medication(
        self, name: str, strength: str | None, formulation: str | None, route: str | None
    ) -> NormalizationResult:
        # Returns candidates but unmatched status until master FKs exist in Milestone 5
        cand = MasterCandidate(
            None, "MOCK_DRUG_01", f"{name} candidate", "keyword", 1, "compatible"
        )
        return NormalizationResult("unmatched", None, [cand])

    def normalize_investigation(self, name: str, specimen: str | None) -> NormalizationResult:
        cand = MasterCandidate(None, "MOCK_LAB_01", f"{name} candidate", "keyword", 1, "compatible")
        return NormalizationResult("unmatched", None, [cand])
