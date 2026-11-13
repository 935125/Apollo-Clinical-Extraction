export interface ClinicalExtractionRequest {
  noteText: string;
  patientId?: string;
  encounterId?: string;
}

export interface Evidence {
  quote: string;
  verified: boolean;
  start: number | null;
  end: number | null;
}

export interface Finding {
  code: string;
  severity: 'info' | 'warning' | 'error';
  field: string | null;
  message: string;
}

export interface ExtractedMedication {
  name: string;
  strength: string | null;
  dose: string | null;
  route: string | null;
  frequency: string | null;
  duration: string | null;
  formulation: string | null;
  instructions: string | null;
  assertionStatus: string;
  evidence: Evidence;
  qualityFlags: string[];
  findings: Finding[];
  writebackEligible: boolean;
  normalizationStatus: string;
}

export interface ExtractedInvestigation {
  name: string;
  timing: string | null;
  specimen: string | null;
  assertionStatus: string;
  evidence: Evidence;
  qualityFlags: string[];
  findings: Finding[];
  writebackEligible: boolean;
  normalizationStatus: string;
}

export interface ValidationSummary {
  hasErrors: boolean;
  errorCount: number;
  warningCount: number;
}

export interface SectionSpan {
  code: string;
  heading: string;
  start: number;
  end: number;
}

export interface PreprocessedNote {
  charLength: number;
  sections: SectionSpan[];
}

export interface ClinicalExtractionResponse {
  extractionId: string;
  correlationId: string;
  extractedAt: string;
  executionDurationMs: number;
  preprocessed: PreprocessedNote;
  medications: ExtractedMedication[];
  investigations: ExtractedInvestigation[];
  validationSummary: ValidationSummary;
}

export interface ProblemDetails {
  title?: string;
  detail?: string;
  status?: number;
  code?: string;
  correlationId?: string;
}

