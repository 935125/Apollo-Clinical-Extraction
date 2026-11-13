# ADR-0008: Microsoft Foundry Integration via OpenAI SDK Structured Outputs

## Context
The platform requires clinical entity extraction from unstructured text with strict schema adherence, zero hallucinations on absent fields, and full traceability. Microsoft Foundry exposes model deployments conforming to the OpenAI v1 chat completions specification.

## Decision
1. **OpenAI v1 Client with Structured Outputs**:
   - Access Microsoft Foundry using the standard `openai.OpenAI` Python SDK configured with `base_url="https://<resource>.openai.azure.com/openai/v1/"`.
   - Authentication via Microsoft Entra ID managed identity using `azure.identity.DefaultAzureCredential` and `get_bearer_token_provider(credential, "https://ai.azure.com/.default")`.
2. **Strict Schema Constraints**:
   - Use `client.beta.chat.completions.parse()` with `response_format=LlmExtractionOutput` (Pydantic model generated from `contracts/extraction/llm-extraction-output.v1.schema.json`).
   - Strict mode forces JSON schema constraint enforcement during token generation.
3. **Model Configuration**:
   - `temperature = 0.0` (greedy deterministic decoding).
   - Zero tool/function calling enabled (prevents instruction injection attacks from clinical text attempting to invoke external tools).
4. **Adapter Isolation**:
   - Wrap invocation in an interface `ExtractionModelAdapter`.
   - In automated unit/integration tests without cloud access, use a deterministic `SyntheticModelAdapter` returning pre-verified clinical fixtures.

## Consequences
- **Positive**: 100% syntactically valid JSON matching Pydantic types; zero reliance on fragile prompt formatting or regex parsing.
- **Negative**: Model strict mode requires every field to be explicitly declared in the schema (nulls handled via unions).

