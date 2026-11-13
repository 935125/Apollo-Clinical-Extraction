# ADR-0009: Evidence Span Indexing and Character Offset Conventions

## Context
Extracted clinical entities must link back to exact evidence spans in the source note for doctor verification in the Angular UI. Different runtimes measure string offsets differently:
- JavaScript/TypeScript (browser DOM): UTF-16 code units (surrogate pairs count as 2).
- Python: Unicode code points (characters, surrogate pairs count as 1).
- PostgreSQL: Character length (`char_length`) counts Unicode code points.

If offsets are misaligned, highlighted text spans in the UI will drift and highlight incorrect clinical words.

## Decision
1. **Never Trust Model-Generated Offsets**:
   - The LLM returns ONLY the verbatim substring `evidence_quote`. Models are notoriously inaccurate at counting character indices.
2. **Worker-Computed Exact Substring Matching**:
   - Stage 3 of the Python extraction worker deterministically locates `evidence_quote` in the pre-processed source text.
   - If the quote cannot be located in the text, mark `evidence_verified = false`, flag `EVIDENCE_NOT_FOUND`, and block write-back eligibility.
3. **Database Storage in Unicode Code Points**:
   - Offsets in PostgreSQL (`evidence_start`, `evidence_end`) are stored as **0-based Unicode code-point offsets** matching Python slice semantics.
4. **API Wire Format Normalization**:
   - When serving the Angular frontend, the Review API exposes the note text and spans. The Angular client or API maps spans using standard string index conversion utilities if non-BMP characters are present.

## Consequences
- **Positive**: 100% accurate evidence highlighting in clinician review; immediate identification of hallucinations where the model fabricates a quote not present in the note.

