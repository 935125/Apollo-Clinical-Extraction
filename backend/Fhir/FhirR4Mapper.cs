using Apollo.Backend.Models;

namespace Apollo.Backend.Fhir;

public class FhirR4Mapper
{
    public static Dictionary<string, object?> ToFhirBundle(
        string noteText,
        IReadOnlyList<ClinicalEntityResponse> entities,
        IReadOnlyList<StructuredMedicationResponse> structuredMeds,
        string? patientId = null)
    {
        string subjectRef = !string.IsNullOrWhiteSpace(patientId) ? $"Patient/{patientId}" : "urn:uuid:patient-subject";
        var entries = new List<Dictionary<string, object?>>();

        // 1. Conditions
        foreach (var e in entities)
        {
            if (e.Category.Equals("CONDITION", StringComparison.OrdinalIgnoreCase) ||
                e.Category.Equals("DISEASE", StringComparison.OrdinalIgnoreCase))
            {
                if (e.Certainty != null && e.Certainty.Equals("Negated", StringComparison.OrdinalIgnoreCase))
                    continue;

                string clinicalStatus = (e.Temporality != null && e.Temporality.Equals("Historical", StringComparison.OrdinalIgnoreCase))
                    ? "resolved"
                    : "active";

                string verificationStatus = (e.Certainty != null && e.Certainty.Equals("Possible", StringComparison.OrdinalIgnoreCase))
                    ? "differential"
                    : "confirmed";

                string condId = $"cond-{Guid.NewGuid():N}";
                var condResource = new Dictionary<string, object?>
                {
                    ["resourceType"] = "Condition",
                    ["id"] = condId,
                    ["clinicalStatus"] = new Dictionary<string, object?>
                    {
                        ["coding"] = new[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["system"] = "http://terminology.hl7.org/CodeSystem/condition-clinical",
                                ["code"] = clinicalStatus
                            }
                        }
                    },
                    ["verificationStatus"] = new Dictionary<string, object?>
                    {
                        ["coding"] = new[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["system"] = "http://terminology.hl7.org/CodeSystem/condition-ver-status",
                                ["code"] = verificationStatus
                            }
                        }
                    },
                    ["code"] = new Dictionary<string, object?>
                    {
                        ["text"] = e.Text,
                        ["coding"] = e.ConceptCode != null ? new[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["system"] = e.ConceptCode.StartsWith("SNOMED:") ? "http://snomed.info/sct" : "http://hl7.org/fhir/sid/icd-10",
                                ["code"] = e.ConceptCode.Split(':').Last(),
                                ["display"] = e.CanonicalName ?? e.Text
                            }
                        } : Array.Empty<Dictionary<string, object?>>()
                    },
                    ["subject"] = new Dictionary<string, object?> { ["reference"] = subjectRef }
                };

                entries.Add(new Dictionary<string, object?>
                {
                    ["fullUrl"] = $"urn:uuid:{condId}",
                    ["resource"] = condResource
                });
            }
        }

        // 2. Medication Statements
        if (structuredMeds.Count > 0)
        {
            foreach (var sm in structuredMeds)
            {
                if (sm.Certainty != null && sm.Certainty.Equals("Negated", StringComparison.OrdinalIgnoreCase))
                    continue;

                string medStatus = (sm.Certainty != null && sm.Certainty.Equals("Historical", StringComparison.OrdinalIgnoreCase))
                    ? "stopped"
                    : "active";

                string medId = $"med-{Guid.NewGuid():N}";
                string doseStr = sm.Dose ?? sm.Dosage ?? "";
                string dosageText = $"{doseStr} {sm.Route} {sm.Frequency} {sm.Duration}".Trim();

                var medResource = new Dictionary<string, object?>
                {
                    ["resourceType"] = "MedicationStatement",
                    ["id"] = medId,
                    ["status"] = medStatus,
                    ["medicationCodeableConcept"] = new Dictionary<string, object?>
                    {
                        ["text"] = sm.Drug,
                        ["coding"] = sm.ConceptCode != null ? new[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["system"] = "http://www.nlm.nih.gov/research/umls/rxnorm",
                                ["code"] = sm.ConceptCode.Replace("RxNorm:", ""),
                                ["display"] = sm.Drug
                            }
                        } : Array.Empty<Dictionary<string, object?>>()
                    },
                    ["dosage"] = new[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["text"] = dosageText,
                            ["route"] = sm.Route != null ? new Dictionary<string, object?> { ["text"] = sm.Route } : null
                        }
                    },
                    ["subject"] = new Dictionary<string, object?> { ["reference"] = subjectRef }
                };

                entries.Add(new Dictionary<string, object?>
                {
                    ["fullUrl"] = $"urn:uuid:{medId}",
                    ["resource"] = medResource
                });
            }
        }
        else
        {
            foreach (var e in entities)
            {
                if (e.Category.Equals("MEDICATION", StringComparison.OrdinalIgnoreCase) &&
                    !(e.Certainty != null && e.Certainty.Equals("Negated", StringComparison.OrdinalIgnoreCase)))
                {
                    string medId = $"med-{Guid.NewGuid():N}";
                    var medResource = new Dictionary<string, object?>
                    {
                        ["resourceType"] = "MedicationStatement",
                        ["id"] = medId,
                        ["status"] = "active",
                        ["medicationCodeableConcept"] = new Dictionary<string, object?>
                        {
                            ["text"] = e.Text,
                            ["coding"] = e.ConceptCode != null ? new[]
                            {
                                new Dictionary<string, object?>
                                {
                                    ["system"] = "http://www.nlm.nih.gov/research/umls/rxnorm",
                                    ["code"] = e.ConceptCode.Replace("RxNorm:", ""),
                                    ["display"] = e.CanonicalName ?? e.Text
                                }
                            } : Array.Empty<Dictionary<string, object?>>()
                        },
                        ["subject"] = new Dictionary<string, object?> { ["reference"] = subjectRef }
                    };

                    entries.Add(new Dictionary<string, object?>
                    {
                        ["fullUrl"] = $"urn:uuid:{medId}",
                        ["resource"] = medResource
                    });
                }
            }
        }

        // 3. Observations (Labs & Investigations)
        foreach (var e in entities)
        {
            bool isLab = e.Category.Equals("LAB", StringComparison.OrdinalIgnoreCase) ||
                         e.Category.Equals("LAB_TEST", StringComparison.OrdinalIgnoreCase) ||
                         e.Category.Equals("INVESTIGATION", StringComparison.OrdinalIgnoreCase) ||
                         (e.ConceptCode != null && e.ConceptCode.StartsWith("LOINC:"));

            if (isLab && !(e.Certainty != null && e.Certainty.Equals("Negated", StringComparison.OrdinalIgnoreCase)))
            {
                string status = "preliminary";
                string loincCode = e.ConceptCode != null && e.ConceptCode.StartsWith("LOINC:") ?
                    e.ConceptCode.Replace("LOINC:", "") : "unknown";

                string obsId = $"obs-{Guid.NewGuid():N}";
                var obsResource = new Dictionary<string, object?>
                {
                    ["resourceType"] = "Observation",
                    ["id"] = obsId,
                    ["status"] = status,
                    ["category"] = new[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["coding"] = new[]
                            {
                                new Dictionary<string, object?>
                                {
                                    ["system"] = "http://terminology.hl7.org/CodeSystem/observation-category",
                                    ["code"] = "laboratory"
                                }
                            }
                        }
                    },
                    ["code"] = new Dictionary<string, object?>
                    {
                        ["text"] = e.Text,
                        ["coding"] = loincCode != "unknown" ? new[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["system"] = "http://loinc.org",
                                ["code"] = loincCode,
                                ["display"] = e.CanonicalName ?? e.Text
                            }
                        } : Array.Empty<Dictionary<string, object?>>()
                    },
                    ["subject"] = new Dictionary<string, object?> { ["reference"] = subjectRef }
                };

                entries.Add(new Dictionary<string, object?>
                {
                    ["fullUrl"] = $"urn:uuid:{obsId}",
                    ["resource"] = obsResource
                });
            }
        }

        return new Dictionary<string, object?>
        {
            ["resourceType"] = "Bundle",
            ["type"] = "collection",
            ["id"] = $"bundle-{Guid.NewGuid():N}",
            ["timestamp"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:sszzz"),
            ["entry"] = entries
        };
    }

    public static Dictionary<string, object?> ToMedicationRequestAndDiagnosticReportBundle(
        ClinicalNERResponse nerResponse,
        string? patientId = null)
    {
        string subjectRef = !string.IsNullOrWhiteSpace(patientId) ? $"Patient/{patientId}" : "urn:uuid:patient-subject";
        var entries = new List<Dictionary<string, object?>>();

        // 1. MedicationRequest resources from StructuredMedications (or MEDICATION entities)
        if (nerResponse.StructuredMedications != null && nerResponse.StructuredMedications.Count > 0)
        {
            foreach (var sm in nerResponse.StructuredMedications)
            {
                if (sm.Certainty != null && sm.Certainty.Equals("Negated", StringComparison.OrdinalIgnoreCase))
                    continue;

                string status = (sm.Certainty != null && sm.Certainty.Equals("Historical", StringComparison.OrdinalIgnoreCase))
                    ? "completed"
                    : "active";

                string medReqId = $"medreq-{Guid.NewGuid():N}";
                string doseStr = sm.Dose ?? sm.Dosage ?? "";
                string dosageText = $"{doseStr} {sm.Route} {sm.Frequency} {sm.Duration}".Trim();
                string? rxCode = sm.ConceptCode?.Replace("RxNorm:", "");

                var medReq = new Dictionary<string, object?>
                {
                    ["resourceType"] = "MedicationRequest",
                    ["id"] = medReqId,
                    ["status"] = status,
                    ["intent"] = "order",
                    ["medicationCodeableConcept"] = new Dictionary<string, object?>
                    {
                        ["text"] = sm.Drug,
                        ["coding"] = !string.IsNullOrWhiteSpace(rxCode) ? new[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["system"] = "http://www.nlm.nih.gov/research/umls/rxnorm",
                                ["code"] = rxCode,
                                ["display"] = sm.Drug
                            }
                        } : Array.Empty<Dictionary<string, object?>>()
                    },
                    ["subject"] = new Dictionary<string, object?> { ["reference"] = subjectRef },
                    ["dosageInstruction"] = new[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["text"] = dosageText,
                            ["route"] = sm.Route != null ? new Dictionary<string, object?> { ["text"] = sm.Route } : null,
                            ["timing"] = sm.Frequency != null ? new Dictionary<string, object?>
                            {
                                ["code"] = new Dictionary<string, object?> { ["text"] = sm.Frequency }
                            } : null
                        }
                    }
                };

                entries.Add(new Dictionary<string, object?>
                {
                    ["fullUrl"] = $"urn:uuid:{medReqId}",
                    ["resource"] = medReq
                });
            }
        }
        else if (nerResponse.Entities != null)
        {
            foreach (var e in nerResponse.Entities)
            {
                if (e.Category.Equals("MEDICATION", StringComparison.OrdinalIgnoreCase) &&
                    !(e.Certainty != null && e.Certainty.Equals("Negated", StringComparison.OrdinalIgnoreCase)))
                {
                    string medReqId = $"medreq-{Guid.NewGuid():N}";
                    string? rxCode = e.ConceptCode?.Replace("RxNorm:", "");

                    var medReq = new Dictionary<string, object?>
                    {
                        ["resourceType"] = "MedicationRequest",
                        ["id"] = medReqId,
                        ["status"] = "active",
                        ["intent"] = "order",
                        ["medicationCodeableConcept"] = new Dictionary<string, object?>
                        {
                            ["text"] = e.Text,
                            ["coding"] = !string.IsNullOrWhiteSpace(rxCode) ? new[]
                            {
                                new Dictionary<string, object?>
                                {
                                    ["system"] = "http://www.nlm.nih.gov/research/umls/rxnorm",
                                    ["code"] = rxCode,
                                    ["display"] = e.CanonicalName ?? e.Text
                                }
                            } : Array.Empty<Dictionary<string, object?>>()
                        },
                        ["subject"] = new Dictionary<string, object?> { ["reference"] = subjectRef }
                    };

                    entries.Add(new Dictionary<string, object?>
                    {
                        ["fullUrl"] = $"urn:uuid:{medReqId}",
                        ["resource"] = medReq
                    });
                }
            }
        }

        // 2. DiagnosticReport resources from LAB / INVESTIGATION entities
        if (nerResponse.Entities != null)
        {
            foreach (var e in nerResponse.Entities)
            {
                bool isLab = e.Category.Equals("LAB", StringComparison.OrdinalIgnoreCase) ||
                             e.Category.Equals("LAB_TEST", StringComparison.OrdinalIgnoreCase) ||
                             e.Category.Equals("INVESTIGATION", StringComparison.OrdinalIgnoreCase) ||
                             (e.ConceptCode != null && e.ConceptCode.StartsWith("LOINC:"));

                if (isLab && !(e.Certainty != null && e.Certainty.Equals("Negated", StringComparison.OrdinalIgnoreCase)))
                {
                    string diagId = $"diagreport-{Guid.NewGuid():N}";
                    string? loincCode = e.ConceptCode?.Replace("LOINC:", "");

                    var diagReport = new Dictionary<string, object?>
                    {
                        ["resourceType"] = "DiagnosticReport",
                        ["id"] = diagId,
                        ["status"] = "preliminary",
                        ["category"] = new[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["coding"] = new[]
                                {
                                    new Dictionary<string, object?>
                                    {
                                        ["system"] = "http://terminology.hl7.org/CodeSystem/v2-0074",
                                        ["code"] = "LAB",
                                        ["display"] = "Laboratory"
                                    }
                                }
                            }
                        },
                        ["code"] = new Dictionary<string, object?>
                        {
                            ["text"] = e.Text,
                            ["coding"] = !string.IsNullOrWhiteSpace(loincCode) ? new[]
                            {
                                new Dictionary<string, object?>
                                {
                                    ["system"] = "http://loinc.org",
                                    ["code"] = loincCode,
                                    ["display"] = e.CanonicalName ?? e.Text
                                }
                            } : Array.Empty<Dictionary<string, object?>>()
                        },
                        ["subject"] = new Dictionary<string, object?> { ["reference"] = subjectRef },
                        ["effectiveDateTime"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:sszzz")
                    };

                    entries.Add(new Dictionary<string, object?>
                    {
                        ["fullUrl"] = $"urn:uuid:{diagId}",
                        ["resource"] = diagReport
                    });
                }
            }
        }

        return new Dictionary<string, object?>
        {
            ["resourceType"] = "Bundle",
            ["type"] = "collection",
            ["id"] = $"bundle-{Guid.NewGuid():N}",
            ["timestamp"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:sszzz"),
            ["entry"] = entries
        };
    }

    public static Dictionary<string, object?> ToMedicationRequestAndDiagnosticReportBundle(
        ClinicalExtractionResponse extraction,
        string? patientId = null)
    {
        string subjectRef = !string.IsNullOrWhiteSpace(patientId) ? $"Patient/{patientId}" : "urn:uuid:patient-subject";
        var entries = new List<Dictionary<string, object?>>();

        foreach (var med in extraction.Medications)
        {
            if (med.AssertionStatus.Equals("allergy", StringComparison.OrdinalIgnoreCase))
            {
                string allergyId = $"allergy-{Guid.NewGuid():N}";
                entries.Add(new Dictionary<string, object?>
                {
                    ["fullUrl"] = $"urn:uuid:{allergyId}",
                    ["resource"] = new Dictionary<string, object?>
                    {
                        ["resourceType"] = "AllergyIntolerance",
                        ["id"] = allergyId,
                        ["type"] = "allergy",
                        ["code"] = new Dictionary<string, object?> { ["text"] = med.Name },
                        ["patient"] = new Dictionary<string, object?> { ["reference"] = subjectRef }
                    }
                });
                continue;
            }
            if (med.AssertionStatus.Equals("negated", StringComparison.OrdinalIgnoreCase))
                continue;

            string status = med.AssertionStatus.Equals("discontinued", StringComparison.OrdinalIgnoreCase)
                ? "stopped"
                : med.AssertionStatus.Equals("historical", StringComparison.OrdinalIgnoreCase)
                    ? "completed"
                    : med.AssertionStatus.Equals("held", StringComparison.OrdinalIgnoreCase) ? "on-hold"
                    : med.AssertionStatus is "conditional" or "uncertain" || med.QualityFlags.Contains("COMPLEX_REGIMEN_REVIEW") ? "draft" : "active";

            string intent = med.AssertionStatus.Equals("new_order", StringComparison.OrdinalIgnoreCase)
                ? "order"
                : "plan";

            string medReqId = $"medreq-{Guid.NewGuid():N}";
            string dosageText = med.Instructions ?? $"{med.Dose} {med.Route} {med.Frequency} {med.Duration}".Trim();

            var medReq = new Dictionary<string, object?>
            {
                ["resourceType"] = "MedicationRequest",
                ["id"] = medReqId,
                ["status"] = status,
                ["intent"] = intent,
                ["medicationCodeableConcept"] = new Dictionary<string, object?>
                {
                    ["text"] = med.Name,
                    ["coding"] = med.Normalization.MatchedCode != null && System.Text.RegularExpressions.Regex.IsMatch(med.Normalization.MatchedCode, @"^\d+$") ? new[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["system"] = "http://www.nlm.nih.gov/research/umls/rxnorm",
                            ["code"] = med.Normalization.MatchedCode,
                            ["display"] = med.Normalization.DisplayName ?? med.Name
                        }
                    } : Array.Empty<Dictionary<string, object?>>()
                },
                ["subject"] = new Dictionary<string, object?> { ["reference"] = subjectRef },
                ["dosageInstruction"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["text"] = dosageText,
                        ["route"] = med.Route != null ? new Dictionary<string, object?> { ["text"] = med.Route } : null,
                        ["timing"] = med.Frequency != null ? new Dictionary<string, object?>
                        {
                            ["code"] = new Dictionary<string, object?> { ["text"] = med.Frequency }
                        } : null
                    }
                }
            };

            entries.Add(new Dictionary<string, object?>
            {
                ["fullUrl"] = $"urn:uuid:{medReqId}",
                ["resource"] = medReq
            });
        }

        foreach (var inv in extraction.Investigations)
        {
            if (inv.AssertionStatus.Equals("negated", StringComparison.OrdinalIgnoreCase))
                continue;

            string diagId = $"diagreport-{Guid.NewGuid():N}";
            string? loincCode = inv.MatchedCode ?? inv.Normalization.MatchedCode;

            var diagReport = new Dictionary<string, object?>
            {
                ["resourceType"] = "DiagnosticReport",
                ["id"] = diagId,
                ["status"] = string.IsNullOrWhiteSpace(inv.Status) ? "preliminary" : inv.Status,
                ["category"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["coding"] = new[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["system"] = "http://terminology.hl7.org/CodeSystem/v2-0074",
                                ["code"] = "LAB",
                                ["display"] = "Laboratory"
                            }
                        }
                    }
                },
                ["code"] = new Dictionary<string, object?>
                {
                    ["text"] = inv.Name,
                    ["coding"] = loincCode != null ? new[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["system"] = "http://loinc.org",
                            ["code"] = loincCode,
                            ["display"] = inv.DisplayName ?? inv.Normalization.DisplayName ?? inv.Name
                        }
                    } : Array.Empty<Dictionary<string, object?>>()
                },
                ["subject"] = new Dictionary<string, object?> { ["reference"] = subjectRef },
                ["effectiveDateTime"] = extraction.ExtractedAt
            };

            entries.Add(new Dictionary<string, object?>
            {
                ["fullUrl"] = $"urn:uuid:{diagId}",
                ["resource"] = diagReport
            });
        }

        return new Dictionary<string, object?>
        {
            ["resourceType"] = "Bundle",
            ["type"] = "collection",
            ["id"] = $"bundle-{extraction.ExtractionId}",
            ["timestamp"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:sszzz"),
            ["entry"] = entries
        };
    }
}
