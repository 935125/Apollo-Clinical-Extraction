# Apollo Clinical Extraction Platform — Stage 9 UI Design System
## Premium Doctor OP EMR Portal Visual Architecture & Component Specification

---

### 1. Visual Language & Brand Heritage

The Apollo Outpatient Electronic Medical Record (OP EMR) portal extends the established Apollo Hospitals clinical aesthetic into an enterprise-grade web application. It combines traditional clinical gravitas (ivory surfaces, deep forest greens, refined typography) with modern web affordances (tabular data density, real-time progression steppers, and clear action hierarchies).

The visual design is modeled directly on the certified Stage 9 visual specification across all five core workflows:
1. **Screen 1: Doctor Login** (40/60 Split-Screen desktop authentication)
2. **Screen 2: Doctor Dashboard** (Assigned outpatient encounters, tabbed filtering, status indicators)
3. **Screen 3: Clinical Note Editor** (Context line, word count, draft saving, sign confirmation modal)
4. **Screen 4: Extraction Status** (Five-stage progression stepper from signing to clinical review)
5. **Screen 5: Extraction Results & Review** (Deep-linked extraction review preserving model provenance)

---

### 2. Design Tokens & Palette

| Token | Hex Value | Role & Usage |
| :--- | :--- | :--- |
| `--apollo-green-dark` | `#064B41` | Primary brand accent, identity pane background, active navigation indicator, avatar badge |
| `--apollo-green-primary` | `#0D6E5D` | Primary button interactive surface, key focus highlights |
| `--apollo-green-hover` | `#095246` | Interactive hover state for primary buttons and clinical links |
| `--apollo-green-soft` | `#E6F3F0` | Selected navigation pill, active tab background, table selection highlight |
| `--apollo-ivory-warm` | `#F7F5F0` | Application background, secondary surface fill, card background |
| `--apollo-ivory-card` | `#FAF9F6` | Card background, writing surface paper texture |
| `--apollo-border-subtle` | `#E2DDD5` | Standard structural borders, table grid lines, divider rules |
| `--apollo-text-primary` | `#1A2421` | High-contrast clinical text, headings, patient references |
| `--apollo-text-secondary` | `#5C6B64` | Subheadings, metadata labels, word counts, audit captions |
| `--apollo-text-muted` | `#8C9B94` | Inactive elements, empty states, table column footers |
| `--apollo-gold-warm` | `#C8973E` | Apollo logo sunburst rays, secondary clinical accents |
| `--apollo-status-draft` | `#B45309` | Draft note indicator (warm amber) |
| `--apollo-status-signed` | `#047857` | Signed note & completed extraction badge (emerald green) |
| `--apollo-status-processing` | `#0284C7` | Asynchronous extraction queue & in-progress spinner (sky blue) |
| `--apollo-status-review` | `#7C3AED` | Clinician review pending alert (violet purple) |

---

### 3. Typography Hierarchy

The portal employs a dual-typeface pairing:
- **Headings & Brand:** `Merriweather`, Georgia, serif — provides clinical dignity, readability, and distinct institutional authority.
- **Body, Inputs & Clinical Tables:** `Inter`, system-ui, -apple-system, sans-serif — ensures high legibility for alphanumeric identifiers (e.g. `P100234`, `ENC-100567`, `RxNorm: 17767`).

```scss
/* Font scale */
h1 { font-family: 'Merriweather', Georgia, serif; font-size: 1.75rem; font-weight: 700; line-height: 1.25; color: #1A2421; }
h2 { font-family: 'Merriweather', Georgia, serif; font-size: 1.35rem; font-weight: 600; line-height: 1.3; color: #1A2421; }
h3 { font-family: 'Merriweather', Georgia, serif; font-size: 1.1rem; font-weight: 600; line-height: 1.4; color: #1A2421; }
.emr-eyebrow { font-size: 0.72rem; font-weight: 700; letter-spacing: 0.12em; text-transform: uppercase; color: #0D6E5D; }
.clinical-table th { font-size: 0.75rem; font-weight: 600; letter-spacing: 0.05em; text-transform: uppercase; color: #5C6B64; }
.clinical-table td { font-size: 0.88rem; font-weight: 400; color: #1A2421; }
```

---

### 4. Component Layout Specifications

#### 4.1 Shell Navigation (`PortalShell`)
- **Brand Wordmark:** Left-aligned Apollo Hospitals emblem with gold sunburst and deep green wordmark.
- **Context Divider:** Thin vertical border (`1px solid #E2DDD5`) separating brand from navigation links.
- **Navigation Links:** `OP EMR`, `Clinical Extraction`, `History`. Active state designated by deep green bottom border (`3px solid #064B41`).
- **Utility Actions:** Notification bell icon with hover feedback; circular doctor avatar badge containing physician initials (`DK` for Dr. K. Krishnan); doctor name with dropdown chevron.
- **Session Popover:** Sliding 30-minute demonstration warning, role indicator, email identifier, and sign-out action.

#### 4.2 Doctor Login (`DoctorLogin` — Screen 1)
- **Split-Screen Geometry:** 40% deep green (`#064B41`) identity panel on the left, 60% warm ivory (`#F7F5F0`) interactive form panel on the right.
- **Identity Panel:** Apollo brand in gold/white, prominent headline ("Your clinical workspace."), subtitle ("Secure access to outpatient documentation and AI-assisted clinical extraction."), gold divider line, and security assurance badge.
- **Login Form:** "Welcome back" heading, email/Doctor ID field with envelope icon, password field with lock icon and reveal/mask toggle button, full-width primary button ("Sign in"), and 30-minute session expiration notice.

#### 4.3 Doctor Dashboard (`DoctorDashboard` — Screen 2)
- **Header:** "Doctor Dashboard" title, outpatient description, and "+ New Clinical Note" primary call-to-action button.
- **Filter Tabs:** Three pill-shaped filter controls: `My Encounters`, `Draft Notes (n)`, and `Awaiting Review (n)`.
- **Search Bar:** Real-time filter input with magnifying glass icon for matching patient references or encounter IDs.
- **Encounter Table:** Structured columns for `Patient Reference`, `Encounter Date`, `Note Status`, `Extraction`, and `Action`.
- **Status Badges:**
  - `Signed`: Solid emerald badge (`#ECFDF5`, text `#047857`).
  - `Draft`: Warm amber badge (`#FEF3C7`, text `#B45309`).
  - `Completed`: Soft green badge (`#E6F3F0`, text `#0D6E5D`).
  - `Processing`: Sky blue badge (`#F0F9FF`, text `#0284C7`) with pulsing CSS spinner.
  - `—`: Muted gray dash for unstarted extractions.

#### 4.4 Clinical Note Editor (`NoteEditor` — Screen 3)
- **Header:** Back-link (`← Doctor Dashboard`), "Clinical Note" heading, and dual action buttons: `Save Draft` (secondary ivory) and `✍ Sign & Submit` (primary green).
- **Context Strip:** Bordered metadata bar presenting `Patient: P100234 | Encounter: ENC-100567 | Date: 28 Jan 2026` alongside a "Synthetic record" badge.
- **Writing Surface:** Framed writing container with subtle border (`1px solid #0D6E5D`), textarea with monospace-capable clinical input, live word count, and character meter.
- **Attachment Import:** Text-based PDF uploader allowing up to 20 MiB clinical reports to be ingested directly into the draft workspace.
- **Sign Confirmation Dialog:** Accessible HTML5 `<dialog>` modal displaying clear warnings that AI extraction results require clinician review and do not constitute authorized prescriptions.

#### 4.5 Extraction Status Stepper (`ExtractionStatus` — Screen 4)
- **Submitted Confirmation:** Emerald document checkmark icon with headline "Clinical note submitted" and subtitle "Your note has been signed and queued for extraction."
- **Five-Stage Progression Stepper:**
  1. `Signed` (Checkmark icon, timestamp e.g. 11:24)
  2. `Queued` (Checkmark icon, timestamp e.g. 11:24)
  3. `Processing` (Pulsing ring indicator, "...")
  4. `Completed` (Numeric step or checkmark, "Saved")
  5. `Review` (Numeric step or checkmark, "Ready")
- **Live Polling:** Non-blocking 4-second exponential backoff polling against `/api/v1/emr/notes/{id}` that terminates automatically once `Needs Review`, `Completed`, or `Failed` status is reached.
- **Action Links:** "View Note" (secondary) and "Review extraction results" (primary deep-link).

#### 4.6 Extraction Results & Review (`App` Integration — Screen 5)
- **Deep-Linked Results:** Renders the verified Stage 8 extraction review interface directly inside the portal shell.
- **Provenance Header:** Displays `Microsoft Foundry — gpt-6.1-sol-2026-09-29 (GPT-6.1 Sol)` or controlled ONNX fallback status.
- **Clinical Tabs:** `Medications (n)`, `Investigations (n)`, `Validation & Alerts`, `Provenance`.
- **Entity Verification:** Displays normalized codes (`RxNorm: 17767`, `LOINC: 58410-2`), status tags (`Continued`, `Ordered`), and exact verbatim source text citations.

---

### 5. Responsive Breakpoint Matrix

| Viewport Width | Target Devices | Layout Adjustments |
| :--- | :--- | :--- |
| **1440px+** | Desktop Workstations | 40/60 split login, full 5-column clinical table, fixed-width writing container (960px). |
| **1050px–1439px** | Clinical Tablets / Laptops | Split login transitions to 35/65, table padding scales down, stepper labels condense. |
| **< 1050px** | Mobile & Small Tablets | Login stacks vertically (identity banner on top, form below), table gains horizontal scroll, action buttons span full width. |

---

### 6. Accessibility & Human Factors

- **ARIA Semantics:** All interactive modals use native `<dialog aria-labelledby="...">` with focus trapping and ESC key dismissal.
- **Keyboard Navigation:** Full tab order traversal across login inputs, table rows, textarea, and action buttons.
- **Color Contrast:** All body text meets WCAG 2.1 AA standards (minimum contrast ratio $\ge 4.5:1$ against `#F7F5F0` and `#FFFFFF`; primary button contrast $\ge 5.2:1$).
- **Screen Reader Announcements:** Dynamic extraction status updates use `role="status"` and `aria-live="polite"` to alert clinicians without interrupting navigation.
