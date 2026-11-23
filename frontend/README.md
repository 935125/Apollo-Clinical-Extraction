# Clinical extraction workspace

From this directory, run `npm ci` and `npm start`, then open `http://localhost:4200`. The development proxy sends API routes to the ASP.NET Core application on port 5000.

Use `npm test -- --watch=false` for unit tests and `npm run build` for a production bundle. The bundle is emitted to `dist/cx-review-ui/browser` and can be served by the backend.

Both pasted notes and uploaded PDFs return the same persisted `ClinicalExtractionResponse`. PDF text extraction happens in .NET; the browser does not synthesize or save PDF results.

The workspace opens with an empty editor. Examples are opt-in. After extraction, the paper view retains the exact submitted text and highlights only returned evidence. Editing, clearing, choosing an example, and opening history invalidate previous work so delayed responses cannot attach to another source.

Run `node e2e_verify.mjs` with the Angular development server running to verify the actual UI using fictional, intercepted API responses. Set `PREVIEW_URL` to override `http://127.0.0.1:4200/`. The script checks the 1440×900 desktop and 390×844 mobile layouts, evidence selection, exports, PDF and history restoration, loading and empty states, save failure retention, and stale-response handling. Screenshots and `browser-report.json` are written to `verification/`. These checks do not test live backend extraction or persistence.

The redesign follows the supplied color, typography, and layout specification. No attached mockup image or approved brand image was available in the checkout; the header uses text branding.
