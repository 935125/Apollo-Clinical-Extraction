import { Component, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ExtractionService } from './services/extraction.service';
import {
  ClinicalExtractionResponse,
  ExtractedInvestigation,
  ExtractedMedication,
  ProblemDetails,
} from './models/extraction.model';

export interface SyntheticPreset {
  id: string;
  title: string;
  badge: string;
  category: 'respiratory' | 'diabetes' | 'hypertension' | 'other';
  icon: string;
  text: string;
}

export interface NarrativeSegment {
  text: string;
  isHighlight: boolean;
}

export interface IntentBadge {
  label: string;
  cssClass: string;
}

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './app.html',
  styleUrl: './app.scss',
})
export class App {
  private readonly extractionService = inject(ExtractionService);

  // Initial clinical note matching the Apollo Hospitals clinical extraction studio mockup
  readonly noteText = signal<string>(
`62-year-old male with type 2 diabetes and hypertension presents with 3 days of fever, productive cough and shortness of breath.

Assessment: Community acquired pneumonia.

Plan:
• Start amoxicillin-clavulanate 875/125 mg orally twice daily for 7 days.
• Add azithromycin 500 mg on day 1, then 250 mg once daily for 4 more days.
• Continue metformin 500 mg twice daily.
• Increase amlodipine to 10 mg once daily.

Investigations:
• Chest X-ray to rule out consolidation.
• CBC, CRP and U&E today.`
  );

  readonly isExtracting = signal<boolean>(false);
  readonly result = signal<ClinicalExtractionResponse | null>(null);
  readonly error = signal<ProblemDetails | null>(null);

  // Active view tabs
  readonly activeTab = signal<'medications' | 'investigations' | 'findings' | 'raw'>('medications');
  readonly activeResultTab = signal<'structured' | 'highlighted' | 'json' | 'validation' | 'model'>('structured');
  readonly activeNav = signal<string>('extraction');
  readonly assertionFilter = signal<string>('all');
  readonly selectedEntity = signal<ExtractedMedication | ExtractedInvestigation | null>(null);
  readonly editorMode = signal<'edit' | 'inspect'>('edit');
  readonly selectedPresetId = signal<string>('respiratory');
  readonly exportDropdownOpen = signal<boolean>(false);

  readonly wordCount = computed<number>(() => {
    const text = this.noteText().trim();
    return text ? text.split(/\s+/).length : 0;
  });

  readonly characterCount = computed<number>(() => this.noteText().length);

  readonly filteredMedications = computed<ExtractedMedication[]>(() => {
    const res = this.result();
    if (!res) return [];
    const filter = this.assertionFilter();
    if (filter === 'all') return res.medications;
    return res.medications.filter(m => m.assertionStatus.toLowerCase() === filter.toLowerCase());
  });

  readonly filteredInvestigations = computed<ExtractedInvestigation[]>(() => {
    const res = this.result();
    if (!res) return [];
    const filter = this.assertionFilter();
    if (filter === 'all') return res.investigations;
    return res.investigations.filter(i => i.assertionStatus.toLowerCase() === filter.toLowerCase());
  });

  readonly runId = computed<string>(() => {
    const res = this.result();
    if (!res) return 'EX-20250126-00034';
    return `EX-${res.extractionId.substring(0, 8).toUpperCase()}`;
  });

  readonly runTimestamp = computed<string>(() => {
    const res = this.result();
    if (!res) return 'Jan 26, 2025, 10:24 AM';
    try {
      const d = new Date(res.extractedAt);
      return d.toLocaleDateString('en-US', {
        month: 'short',
        day: 'numeric',
        year: 'numeric',
        hour: 'numeric',
        minute: '2-digit',
      });
    } catch {
      return 'Jan 26, 2025, 10:24 AM';
    }
  });

  // Intent counts for the summary card
  readonly newOrdersCount = computed<number>(() => {
    const res = this.result();
    if (!res) return 3;
    const medNew = res.medications.filter(m => m.assertionStatus.toLowerCase() === 'new_order').length;
    const invNew = res.investigations.filter(i => i.assertionStatus.toLowerCase() === 'new_order').length;
    return medNew + invNew;
  });

  readonly continueCount = computed<number>(() => {
    const res = this.result();
    if (!res) return 1;
    return res.medications.filter(m => {
      const st = m.assertionStatus.toLowerCase();
      return st === 'current_medication' || st === 'continue';
    }).length;
  });

  readonly doseChangeCount = computed<number>(() => {
    const res = this.result();
    if (!res) return 1;
    return res.medications.filter(m => {
      const q = (m.evidence?.quote || '').toLowerCase();
      return q.includes('increase') || q.includes('decrease') || q.includes('titrat') || q.includes('change');
    }).length || 1;
  });

  readonly narrativeSegments = computed<NarrativeSegment[]>(() => {
    const fullText = this.noteText();
    const entity = this.selectedEntity();

    if (!entity || !entity.evidence || !entity.evidence.quote) {
      return [{ text: fullText, isHighlight: false }];
    }

    const ev = entity.evidence;
    let start = ev.start;
    let end = ev.end;

    if (start === null || end === null || start < 0 || end > fullText.length || fullText.substring(start, end) !== ev.quote) {
      const idx = fullText.indexOf(ev.quote);
      if (idx !== -1) {
        start = idx;
        end = idx + ev.quote.length;
      } else {
        return [{ text: fullText, isHighlight: false }];
      }
    }

    const segments: NarrativeSegment[] = [];
    if (start > 0) {
      segments.push({ text: fullText.substring(0, start), isHighlight: false });
    }
    segments.push({ text: fullText.substring(start, end), isHighlight: true });
    if (end < fullText.length) {
      segments.push({ text: fullText.substring(end), isHighlight: false });
    }
    return segments;
  });

  readonly presets: SyntheticPreset[] = [
    {
      id: 'respiratory',
      title: 'Respiratory Infection',
      badge: 'Community Acquired',
      category: 'respiratory',
      icon: 'lungs',
      text:
`62-year-old male with type 2 diabetes and hypertension presents with 3 days of fever, productive cough and shortness of breath.

Assessment: Community acquired pneumonia.

Plan:
• Start amoxicillin-clavulanate 875/125 mg orally twice daily for 7 days.
• Add azithromycin 500 mg on day 1, then 250 mg once daily for 4 more days.
• Continue metformin 500 mg twice daily.
• Increase amlodipine to 10 mg once daily.

Investigations:
• Chest X-ray to rule out consolidation.
• CBC, CRP and U&E today.`
    },
    {
      id: 'respiratory_alt',
      title: 'Respiratory & Infection',
      badge: 'Acute Care',
      category: 'respiratory',
      icon: 'lungs',
      text:
`History of Present Illness: 42-year-old female presents with productive cough and fever x 4 days.
Medications: Discontinue Warfarin 5mg immediately due to prolonged prothrombin time.
Plan:
1. Prescribe Amoxicillin 500mg PO TDS for 7 days.
2. Paracetamol 650mg PO SOS for fever > 38.5 C.
3. Order Chest X-ray PA view.
4. Order Complete Blood Count (CBC).`
    },
    {
      id: 'diabetes',
      title: 'Diabetes Follow-up',
      badge: 'Endocrinology',
      category: 'diabetes',
      icon: 'water',
      text:
`Chief Complaint: 58-year-old male with type 2 diabetes and hypertension for routine follow-up.
Past Medical History: Prior history of gout; patient took Allopurinol 100mg daily in 2023, ceased last year.
Allergies: No Aspirin (severe bronchospasm and facial swelling).
Current Medications: Metformin 500mg PO BD with meals.
Assessment: Uncontrolled glycemia; borderline blood pressure.
Plan:
1. Increase Metformin to 1000mg PO BD after meals.
2. Start Atorvastatin 20mg PO at bedtime for dyslipidemia.
3. Order HbA1c and Lipid Profile.
4. Order Serum Creatinine if systolic blood pressure exceeds 160 mmHg.`
    },
    {
      id: 'hypertension',
      title: 'Hypertension',
      badge: 'Cardiology',
      category: 'hypertension',
      icon: 'heart',
      text:
`Patient: 65-year-old female with essential hypertension.
Current Medications: Amlodipine 5mg PO daily.
Plan:
1. Increase Amlodipine to 10mg PO daily.
2. Add Lisinopril 10mg PO daily.
3. Order Serum Electrolytes in 2 weeks.
4. Order 12-lead ECG.`
    },
    {
      id: 'other',
      title: 'Other',
      badge: 'Complex Inpatient',
      category: 'other',
      icon: 'dots',
      text:
`Patient: 74-year-old male presenting for chronic disease review.
Current Medications: Lisinopril 10mg daily, Allopurinol 100mg daily.
Medication Change: Discontinue Lisinopril 10mg immediately due to hyperkalemia.
Plan:
1. Continue Allopurinol 100mg PO daily.
2. Start Amlodipine 5mg PO daily.
3. Order Serum Creatinine next week.`
    }
  ];

  loadPreset(preset: SyntheticPreset): void {
    this.selectedPresetId.set(preset.id);
    this.noteText.set(preset.text);
    this.selectedEntity.set(null);
    this.editorMode.set('edit');
    this.error.set(null);
  }

  clear(): void {
    this.noteText.set('');
    this.result.set(null);
    this.selectedEntity.set(null);
    this.editorMode.set('edit');
    this.error.set(null);
  }

  setTab(tab: 'medications' | 'investigations' | 'findings' | 'raw'): void {
    this.activeTab.set(tab);
  }

  setResultTab(tab: 'structured' | 'highlighted' | 'json' | 'validation' | 'model'): void {
    this.activeResultTab.set(tab);
    if (tab === 'highlighted') {
      this.editorMode.set('inspect');
    }
  }

  setNav(item: string): void {
    this.activeNav.set(item);
  }

  setFilter(filter: string): void {
    this.assertionFilter.set(filter);
  }

  selectEntity(item: ExtractedMedication | ExtractedInvestigation): void {
    if (this.selectedEntity() === item) {
      this.selectedEntity.set(null);
      this.editorMode.set('edit');
    } else {
      this.selectedEntity.set(item);
      this.editorMode.set('inspect');
    }
  }

  clearSelection(): void {
    this.selectedEntity.set(null);
    this.editorMode.set('edit');
  }

  toggleExportDropdown(): void {
    this.exportDropdownOpen.set(!this.exportDropdownOpen());
  }

  getMedicationIntentBadge(med: ExtractedMedication): IntentBadge {
    const status = (med.assertionStatus || '').toLowerCase();
    const quote = (med.evidence?.quote || '').toLowerCase();
    const instructions = (med.instructions || '').toLowerCase();

    if (status === 'new_order') {
      if (quote.includes('add') || instructions.includes('add')) {
        return { label: 'New (Add)', cssClass: 'badge-intent-new' };
      }
      return { label: 'New (Start)', cssClass: 'badge-intent-new' };
    }
    if (status === 'current_medication' || status === 'continue') {
      if (quote.includes('increase') || instructions.includes('increase')) {
        return { label: 'Increase (Dose)', cssClass: 'badge-intent-increase' };
      }
      if (quote.includes('decrease') || instructions.includes('decrease') || quote.includes('taper')) {
        return { label: 'Decrease (Dose)', cssClass: 'badge-intent-decrease' };
      }
      return { label: 'Continue', cssClass: 'badge-intent-continue' };
    }
    if (status === 'discontinued') {
      return { label: 'Discontinue', cssClass: 'badge-intent-discontinue' };
    }
    if (status === 'negated') {
      return { label: 'Allergy / Negated', cssClass: 'badge-intent-negated' };
    }
    if (status === 'conditional') {
      return { label: 'Conditional (PRN)', cssClass: 'badge-intent-conditional' };
    }
    return { label: med.assertionStatus, cssClass: 'badge-intent-default' };
  }

  getInvestigationIntentBadge(inv: ExtractedInvestigation): IntentBadge {
    const status = (inv.assertionStatus || '').toLowerCase();
    if (status === 'new_order' || status === 'order') {
      return { label: 'Order (New)', cssClass: 'badge-intent-new' };
    }
    if (status === 'conditional') {
      return { label: 'Conditional', cssClass: 'badge-intent-conditional' };
    }
    if (status === 'historical') {
      return { label: 'Prior Result', cssClass: 'badge-intent-continue' };
    }
    return { label: inv.assertionStatus, cssClass: 'badge-intent-default' };
  }

  onExtract(): void {
    const text = this.noteText().trim();
    if (!text) {
      this.error.set({
        title: 'Empty Note Text',
        detail: 'Please enter clinical note text before submitting for extraction.',
        status: 400,
        code: 'EMPTY_NOTE_TEXT',
      });
      return;
    }

    this.isExtracting.set(true);
    this.error.set(null);
    this.selectedEntity.set(null);

    const correlationId = 'ui-extract-' + Date.now().toString(36);

    this.extractionService.extract({
      noteText: text,
    }).subscribe({
      next: (response) => {
        this.result.set(response);
        this.isExtracting.set(false);
      },
      error: (err: ProblemDetails) => {
        this.error.set(err);
        this.isExtracting.set(false);
      },
    });
  }

  exportJson(): void {
    const res = this.result();
    if (!res) return;

    const dataStr = 'data:text/json;charset=utf-8,' + encodeURIComponent(JSON.stringify(res, null, 2));
    const downloadAnchor = document.createElement('a');
    downloadAnchor.setAttribute('href', dataStr);
    downloadAnchor.setAttribute('download', `clinical-extraction-${res.correlationId || 'export'}.json`);
    document.body.appendChild(downloadAnchor);
    downloadAnchor.click();
    downloadAnchor.remove();
    this.exportDropdownOpen.set(false);
  }

  exportCsv(): void {
    const res = this.result();
    if (!res) return;

    const rows = [
      ['Type', 'Name', 'Dose/Strength', 'Route', 'Frequency', 'Duration', 'Intent', 'Match Code', 'Evidence Quote', 'Confidence'],
    ];

    for (const m of res.medications) {
      rows.push([
        'Medication',
        m.name,
        m.strength || m.dose || '',
        m.route || '',
        m.frequency || '',
        m.duration || '',
        m.assertionStatus,
        m.normalization?.matchedCode || 'Unresolved',
        `"${(m.evidence?.quote || '').replace(/"/g, '""')}"`,
        m.normalization?.confidence ? (m.normalization.confidence * 100).toFixed(0) + '%' : '0%',
      ]);
    }

    for (const i of res.investigations) {
      rows.push([
        'Investigation',
        i.name,
        '',
        '',
        '',
        '',
        i.assertionStatus,
        i.normalization?.matchedCode || 'Unresolved',
        `"${(i.evidence?.quote || '').replace(/"/g, '""')}"`,
        i.normalization?.confidence ? (i.normalization.confidence * 100).toFixed(0) + '%' : '0%',
      ]);
    }

    const csvContent = 'data:text/csv;charset=utf-8,' + rows.map(e => e.join(',')).join('\n');
    const encodedUri = encodeURI(csvContent);
    const downloadAnchor = document.createElement('a');
    downloadAnchor.setAttribute('href', encodedUri);
    downloadAnchor.setAttribute('download', `clinical-extraction-${res.correlationId || 'export'}.csv`);
    document.body.appendChild(downloadAnchor);
    downloadAnchor.click();
    downloadAnchor.remove();
    this.exportDropdownOpen.set(false);
  }
}
