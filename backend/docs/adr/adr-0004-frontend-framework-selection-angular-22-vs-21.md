# ADR-0004: Frontend Framework Selection: Angular 22 vs Angular 21 LTS

## Context
Angular 22 was released in June 2026. Official package engine checks (`npm view @angular/core@22.2.6 engines`) demonstrate that Angular 22 requires Node.js `^22.22.3 || ^24.15.0 || >=26.0.0`.
The current local workstation has Node.js version **24.12.0** installed. Installing Angular 22 on Node 24.12.0 triggers engine peer-dependency failures. Angular 21 LTS supports Node `^20.19.0 || ^22.12.0 || >=24.0.0`, which is fully satisfied by Node 24.12.0.

## Decision
1. **Primary Recommendation**: Upgrade local Node.js environment to Node 24 LTS patch version `>= 24.15.0` (or Node 26) to allow running **Angular 22.2.x** with modern signal components, Angular Material 22, and Vitest test runner.
2. **Fallback Alternative**: If workstation Node.js cannot be updated immediately, standardize on **Angular 21.2.x LTS** with Angular Material 21, which runs natively on the current Node 24.12.0 environment without bypassing engine checks.

## Consequences
- **Positive**: Complies with framework support matrices without suppressing npm warnings or risking build/compilation anomalies.
- **Action**: Decision submitted to user for environment approval.

