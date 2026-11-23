# ADR-0010: Terminology Normalization Strategy and Vector Search Boundary

## Context
Extracted drug and investigation names must be matched against hospital master codes before clinician confirmation. A common AI engineering mistake is relying purely on vector embeddings (cosine similarity) to map clinical concepts. In healthcare, vector similarity does not imply clinical equivalence (e.g. "Metoprolol Succinate 50mg ER" and "Metoprolol Tartrate 50mg IR" have extremely high vector similarity but are clinically distinct formulations with different release kinetics).

## Decision
1. **Deterministic Multi-Tier Matching Sequence**:
   - **Tier 1 (Exact Code Lookup)**: Match exact local hospital code or known barcode.
   - **Tier 2 (Alias & Brand Matching)**: Normalized exact match against curated `drug_alias` / `investigation_alias` tables.
   - **Tier 3 (Trigram & Keyword Search)**: High-speed fuzzy search using PostgreSQL `pg_trgm` and tsvector full-text search against active release items.
   - **Tier 4 (Vector-Assisted Candidate Retrieval - Optional / Deferred)**: Use vector embeddings only to retrieve candidate IDs if Tiers 1–3 return zero candidates.
2. **Mandatory Clinical Attribute Filtering (Safety Gate)**:
   - Regardless of retrieval method (trigram or vector), candidates MUST pass clinical attribute compatibility checks:
     - Medication: Formulation code (e.g. TAB vs INJ) and Route (e.g. PO vs IV) must be compatible. Extracted strength must match master strength.
     - Investigation: Specimen code (e.g. SERUM vs URINE) must be compatible.
   - Incompatible candidates are flagged with `attribute_check = 'incompatible'` and cannot be auto-selected.
3. **Ambiguity Handling**:
   - If multiple compatible candidates exist with close confidence scores, status is marked `ambiguous`, auto-selection is suppressed, and all candidates are presented to the doctor for explicit manual selection.

## Consequences
- **Positive**: Prevents dangerous clinical medication substitution errors; vector search is kept at an assistive retrieval boundary rather than an uncontrolled mapper.

