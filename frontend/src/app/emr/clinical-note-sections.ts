export interface ClinicalSectionDef {
  id: string;
  label: string;
  placeholder: string;
  rows: number;
}

export const CLINICAL_SECTIONS: readonly ClinicalSectionDef[] = [
  {
    id: 'chief_complaint',
    label: 'Chief Complaint',
    placeholder: 'Primary reason for outpatient consultation (e.g., Follow-up for hypertension and review of medications.)',
    rows: 2,
  },
  {
    id: 'hpi',
    label: 'History of Present Illness',
    placeholder: 'Onset, duration, severity, adherence, pertinent positives and negatives…',
    rows: 3,
  },
  {
    id: 'pmh',
    label: 'Past Medical History',
    placeholder: 'Chronic conditions, prior surgeries, relevant hospitalizations…',
    rows: 2,
  },
  {
    id: 'allergies',
    label: 'Allergies',
    placeholder: 'Drug or environmental allergies and reactions (e.g., No known drug allergies.)',
    rows: 2,
  },
  {
    id: 'current_medications',
    label: 'Current Medications',
    placeholder: 'Ongoing medications with dose, route and frequency (e.g., Amlodipine 5 mg once daily. Atorvastatin 10 mg at night.)',
    rows: 3,
  },
  {
    id: 'examination',
    label: 'Clinical Examination',
    placeholder: 'Vitals (BP, HR, SpO2, Temp) and focused physical examination findings…',
    rows: 3,
  },
  {
    id: 'assessment',
    label: 'Assessment',
    placeholder: 'Clinical impression and diagnoses (e.g., Hypertension — controlled. Dyslipidaemia.)',
    rows: 2,
  },
  {
    id: 'treatment_plan',
    label: 'Treatment Plan',
    placeholder: 'Non-pharmacological advice, lifestyle counseling and therapeutic strategy…',
    rows: 3,
  },
  {
    id: 'medications',
    label: 'Medications',
    placeholder: 'Prescribed medication changes: Start, Continue, Increase, or Discontinue with dose, route, frequency and duration…',
    rows: 3,
  },
  {
    id: 'investigations',
    label: 'Investigations',
    placeholder: 'Ordered lab tests, imaging and timing/conditionality (e.g., Order complete blood count and HbA1c tomorrow.)',
    rows: 3,
  },
  {
    id: 'follow_up',
    label: 'Follow-up',
    placeholder: 'Return interval, red-flag precautions and referral instructions…',
    rows: 2,
  },
  {
    id: 'additional_notes',
    label: 'Additional Notes',
    placeholder: 'Any additional consultation context or legacy free-text documentation…',
    rows: 3,
  },
] as const;

export type ClinicalSectionValues = Record<string, string>;

export function emptySectionValues(): ClinicalSectionValues {
  const values: ClinicalSectionValues = {};
  for (const s of CLINICAL_SECTIONS) {
    values[s.id] = '';
  }
  return values;
}

const LABEL_TO_ID = new Map<string, string>(
  CLINICAL_SECTIONS.map((s) => [s.label.toLowerCase(), s.id]),
);

// Build heading matcher matching exact section headings on their own line (e.g. "Chief Complaint:" or "Chief Complaint: text")
const HEADING_REGEX = new RegExp(
  `^(${CLINICAL_SECTIONS.map((s) => s.label.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).join('|')}):\\s*(.*)$`,
  'i',
);

/**
 * Deterministic serializer converting populated structured sections into a properly headed,
 * human-readable clinical note compatible with the existing extraction pipeline and SHA-256 versioning.
 */
export function serializeClinicalSections(values: ClinicalSectionValues): string {
  const blocks: string[] = [];
  for (const section of CLINICAL_SECTIONS) {
    const raw = values[section.id];
    if (!raw || !raw.trim()) continue;
    const cleaned = raw.replace(/\r\n/g, '\n').trim();
    blocks.push(`${section.label}:\n${cleaned}`);
  }
  return blocks.join('\n\n');
}

/**
 * Backward-compatible parser that restores structured sections from serialized clinical text.
 * Any legacy free-text note without canonical section headings is preserved verbatim in
 * `additional_notes` (with `isLegacyUnstructured = true`) so no historical text is ever lost.
 */
export function parseClinicalNote(rawText: string): {
  values: ClinicalSectionValues;
  isLegacyUnstructured: boolean;
} {
  const values = emptySectionValues();
  if (!rawText || !rawText.trim()) {
    return { values, isLegacyUnstructured: false };
  }

  const lines = rawText.replace(/\r\n/g, '\n').split('\n');
  let activeId: string | null = null;
  let matchedHeadingCount = 0;
  const preambleLines: string[] = [];
  const buckets: Record<string, string[]> = {};

  for (const line of lines) {
    const match = HEADING_REGEX.exec(line.trim());
    if (match) {
      const id = LABEL_TO_ID.get(match[1].toLowerCase()) ?? null;
      if (id) {
        activeId = id;
        matchedHeadingCount++;
        buckets[activeId] ??= [];
        if (match[2].trim()) {
          buckets[activeId].push(match[2].trim());
        }
        continue;
      }
    }
    if (activeId) {
      buckets[activeId].push(line);
    } else {
      preambleLines.push(line);
    }
  }

  if (matchedHeadingCount === 0) {
    values['additional_notes'] = rawText;
    return { values, isLegacyUnstructured: true };
  }

  for (const [id, contentLines] of Object.entries(buckets)) {
    values[id] = contentLines.join('\n').trim();
  }

  const preamble = preambleLines.join('\n').trim();
  if (preamble) {
    values['additional_notes'] = values['additional_notes']
      ? `${preamble}\n\n${values['additional_notes']}`
      : preamble;
  }

  return { values, isLegacyUnstructured: false };
}

export function populatedSectionsCount(values: ClinicalSectionValues): number {
  return CLINICAL_SECTIONS.filter((s) => !!(values[s.id] && values[s.id].trim())).length;
}
