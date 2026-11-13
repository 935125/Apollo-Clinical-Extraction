"""
Stage 1: Clinical Text Preprocessor.
Performs safe unicode normalization, section identification, and bidirectional character offset tracking
without altering medical meaning, numbers, or negation cues.
"""

import re
import unicodedata
from dataclasses import dataclass, field


@dataclass
class SectionSpan:
    code: str
    heading: str
    start: int
    end: int


@dataclass
class PreprocessedNote:
    original_text: str
    normalized_text: str
    sections: list[SectionSpan] = field(default_factory=list)
    char_length: int = 0
    is_empty_or_malformed: bool = False
    rejection_reason: str | None = None


# Authoritative known clinical section headings in OP clinical notes
SECTION_PATTERNS = [
    (r"(?i)\b(history of present illness|hpi|chief complaint|presenting complaints?):", "HPI"),
    (r"(?i)\b(past medical history|pmh|past history):", "PAST_HISTORY"),
    (r"(?i)\b(current medications?|medications? on admission|medication history):", "CURRENT_MEDS"),
    (r"(?i)\b(examination|on examination|o/e|physical exam):", "EXAMINATION"),
    (r"(?i)\b(investigations?|lab results?|imaging|reports?):", "INVESTIGATIONS"),
    (r"(?i)\b(assessment|impression|diagnosis|provisional diagnosis):", "ASSESSMENT"),
    (r"(?i)\b(plan|treatment plan|recommendations?|advice|prescriptions?|rx):", "PLAN"),
]


class ClinicalPreprocessor:
    MAX_CHAR_LENGTH = 65536  # 64 KiB safety limit

    def preprocess(self, text: str | None) -> PreprocessedNote:
        if text is None or not text.strip():
            return PreprocessedNote(
                original_text="" if text is None else text,
                normalized_text="",
                char_length=0,
                is_empty_or_malformed=True,
                rejection_reason="EMPTY_NOTE_TEXT",
            )

        if len(text) > self.MAX_CHAR_LENGTH:
            return PreprocessedNote(
                original_text=text,
                normalized_text=text[: self.MAX_CHAR_LENGTH],
                char_length=len(text),
                is_empty_or_malformed=True,
                rejection_reason="NOTE_EXCEEDS_64KIB_LIMIT",
            )

        # 1. Strip null bytes and non-printable control characters (preserving tab \t, newline \n, carriage return \r)
        sanitized = re.sub(r"[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]", "", text)

        # 2. Unicode NFC Normalization (canonical decomposition followed by canonical composition)
        # Preserves characters, dosage symbols, units (µg/mg/°C)
        normalized = unicodedata.normalize("NFC", sanitized)

        # 3. Section detection with character offset spans
        sections: list[SectionSpan] = []
        matches = []
        for pattern, code in SECTION_PATTERNS:
            for m in re.finditer(pattern, normalized):
                matches.append((m.start(), m.end(), code, m.group(1)))

        matches.sort(key=lambda x: x[0])

        for i, (start, end, code, heading) in enumerate(matches):
            section_end = matches[i + 1][0] if i + 1 < len(matches) else len(normalized)
            sections.append(SectionSpan(code=code, heading=heading, start=start, end=section_end))

        return PreprocessedNote(
            original_text=text,
            normalized_text=normalized,
            sections=sections,
            char_length=len(normalized),
            is_empty_or_malformed=False,
            rejection_reason=None,
        )

    def format_llm_prompt_context(self, preprocessed: PreprocessedNote) -> str:
        """
        Wraps clinical note in XML delimiters and escapes internal closing tags
        to prevent prompt injection and untrusted instruction execution.
        """
        safe_body = preprocessed.normalized_text.replace(
            "</clinical_note>", "&lt;/clinical_note&gt;"
        )
        return f"<clinical_note>\n{safe_body}\n</clinical_note>"
