# ADR-0002: Deterministic Extraction Pipeline vs. Autonomous Agents

## Context
Autonomous AI agents that execute dynamic tool loops, self-critique, or recursive reasoning have high variance in latency, unpredictable cost/token consumption, and risks of non-deterministic clinical decision-making.

## Decision
1. **Deterministic 5-Stage Orchestration**: Core clinical extraction is implemented as a strict, deterministic sequence:
   - Stage 1: Pre-processing & Section Detection (deterministic text normalization, non-destructive).
   - Stage 2: LLM Structured Output Extraction (single inference call via Microsoft Foundry with temperature=0 and JSON strict schema constraint).
   - Stage 3: Deterministic Schema & Clinical Rule Validation (Pydantic structural check, dose/frequency parsing, rule-based assertion verification).
   - Stage 4: Hierarchical Terminology Normalization (deterministic exact code lookup -> alias matching -> keyword/trigram search -> clinical attribute validation).
   - Stage 5: Quality Assessment & Persistence (deterministic scoring heuristic, verification of evidence spans against source note).
2. **Forbidden Patterns**: Autonomous agent frameworks (e.g. LangChain AgentExecutors, AutoGen, CrewAI) and dynamic tool-calling are explicitly prohibited for clinical extraction.

## Consequences
- **Positive**: Strict clinical safety guarantees; predictable latency (p95 < 6s); reproducible runs for medico-legal audit; lower token expenditure.
- **Negative**: Edge cases in clinical text that require multi-turn conversational reasoning must be deferred to human doctor review rather than autonomous self-resolution.

