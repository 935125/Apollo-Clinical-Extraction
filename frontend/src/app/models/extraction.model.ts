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

export interface NormalizationInfo {
  status: string;
  matchedCode: string | null;
  displayName: string | null;
  confidence: number;
  semanticStatus?: 'exact' | 'broader' | 'narrower' | 'ambiguous' | 'unresolved' | string | null;
  candidateCode?: string | null;
  terminologySource?: string | null;
  terminologyVersion?: string | null;
  mappingNote?: string | null;
}

export interface EntityProvenance {
  origin: string;
  provider: string;
  ruleModified?: boolean;
  modifications?: string[] | null;
  uncertaintyReason?: string | null;
  quarantined?: boolean;
  triggeredAlertIds?: string[] | null;
}

export interface ExtractedMedication {
  doseUnit?: string | null;
  action?: string | null;
  origin?: string;
  provenance?: EntityProvenance | null;
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
  normalization: NormalizationInfo;
}

export interface ExtractedInvestigation {
  name: string;
  timing: string | null;
  specimen: string | null;
  assertionStatus: string;
  evidence: Evidence;
  qualityFlags: string[];
  findings: Finding[];
  normalization: NormalizationInfo;
  matchedCode?: string | null;
  displayName?: string | null;
  status?: string;
  clinicalCategory?: string | null;
  method?: string | null;
  resultValue?: string | null;
  origin?: string;
  provenance?: EntityProvenance | null;
}

export interface ValidationDimension {
  dimension: string;
  status: string;
  evaluatedCount: number;
  passedCount: number;
  warningCount: number;
  errorCount: number;
  summary: string;
}

export interface ValidationSummary {
  hasErrors: boolean;
  errorCount: number;
  warningCount: number;
  dimensions?: ValidationDimension[] | null;
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

export interface ProviderAttempt {
  provider: string;
  model: string | null;
  deployment: string | null;
  outcome: string;
  reason: string | null;
  durationMs: number;
  inferenceExecuted: boolean;
  promptTokens?: number | null;
  completionTokens?: number | null;
  totalTokens?: number | null;
  onnxWindows?: number | null;
  httpAttempts?: number | null;
  correlationId?: string | null;
}

export interface ModelMetadata {
  requestedPrimaryProvider?: string | null;
  finalExecutedProvider?: string | null;
  routingMode?: string | null;
  primaryInvocationStatus?: string | null;
  fallbackTriggered?: boolean | null;
  extractionStatus?: string | null;
  validationResult?: string | null;
  modelSha256?: string | null;
  tokenizerSha256?: string | null;
  executionMode?: string | null;
  reviewCandidates?: { name: string; category: string; confidence: number; evidence: Evidence; reason: string; reviewRequired: boolean }[] | null;
  provider?: string | null;
  model?: string | null;
  azureDeployment?: string | null;
  fallbackReason?: string | null;
  inferenceExecuted?: boolean | null;
  attempts?: ProviderAttempt[] | null;
  schemaValidationStatus?: string | null;
  adapterType: string;
  modelDeployment: string;
  promptVersion: string;
  promptTokens?: number | null;
  completionTokens?: number | null;
  totalTokens?: number | null;
  estimatedCostUsd?: number | null;
}

export interface ClinicalSafetyAlert {
  alertId: string;
  severity: 'CRITICAL' | 'HIGH' | 'MODERATE' | 'WARNING' | string;
  category: string;
  title: string;
  description: string;
  involvedEntities: string[];
  recommendation: string;
  alertType?: string;
  temporality?: string;
  thresholdCriterion?: string | null;
  observedFact?: string | null;
  evidenceQuote?: string | null;
  startChar?: number | null;
  endChar?: number | null;
}

export interface ClinicalExtractionResponse {
  schemaVersion: string;
  extractionId: string;
  correlationId: string;
  extractedAt: string;
  executionDurationMs: number;
  modelMetadata: ModelMetadata;
  preprocessed: PreprocessedNote;
  medications: ExtractedMedication[];
  investigations: ExtractedInvestigation[];
  validationSummary: ValidationSummary;
  safetyAlerts?: ClinicalSafetyAlert[];
  embedding?: number[] | null;
  noteText?: string | null;
  inputType: 'text' | 'pdf' | string;
  sourceFileName?: string | null;
  saved: boolean;
  entityReviews?: EntityReviewRecord[] | null;
}

export type EntityReviewStatus =
  | 'PENDING_REVIEW'
  | 'ACCEPTED'
  | 'CORRECTED'
  | 'REJECTED'
  | 'NEEDS_CLARIFICATION';

export interface EntityReviewSubmissionRequest {
  expectedVersion?: number;
  entityType: 'MEDICATION' | 'INVESTIGATION';
  entityIndex: number;
  reviewStatus: EntityReviewStatus;
  correctedEntity?: Record<string, any> | null;
  reviewComment?: string | null;
}

export interface EntityReviewRecord {
  reviewId: string;
  extractionId: string;
  entityType: 'MEDICATION' | 'INVESTIGATION' | string;
  entityIndex: number;
  reviewStatus: EntityReviewStatus | string;
  originalEntity: Record<string, any>;
  correctedEntity?: Record<string, any> | null;
  originalEvidence: Evidence;
  reviewAction: string;
  reviewerIdentity: string;
  authMode: 'AUTHENTICATED_CLINICIAN' | 'LOCAL_DEMO_MODE' | string;
  reviewComment?: string | null;
  reviewedAt: string;
  versionNumber: number;
}

export interface SemanticSearchResult {
  extraction: ClinicalExtractionResponse;
  similarityScore: number;
  matchedHighlights: string[];
}

export interface ProblemDetails {
  title?: string;
  detail?: string;
  status?: number;
  code?: string;
  correlationId?: string;
}
