"""Non-inference metadata GET only. Never print credentials or response bodies."""
from pathlib import Path
import json
import os
import urllib.request
import urllib.error
import urllib.parse

root = Path(__file__).resolve().parents[3]
values = {}
for line in (root / ".env").read_text().splitlines():
    if "=" in line and not line.lstrip().startswith("#"):
        key, value = line.split("=", 1)
        values[key.strip()] = value.strip().strip('"\'')
endpoint = os.environ.get("Foundry__Endpoint") or values.get("Foundry__Endpoint", "")
deployment = os.environ.get("Foundry__DeploymentName") or values.get("Foundry__DeploymentName", "")
key = os.environ.get("Foundry__ApiKey") or values.get("Foundry__ApiKey", "")
uri = urllib.parse.urlsplit(endpoint)
assert uri.scheme == "https" and uri.hostname and uri.hostname.endswith((".services.ai.azure.com", ".openai.azure.com")) and not uri.username and not uri.query
assert key and deployment

class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, message, headers, newurl):
        return None

opener = urllib.request.build_opener(NoRedirect())
base = endpoint.rstrip("/")
if not base.endswith("/openai/v1"):
    base += "/openai/v1"
result = {"method": "GET", "completionRequests": 0, "billableInferenceRequests": 0, "endpointHost": uri.hostname,
          "deployment": deployment, "credentialsPrinted": False, "checks": []}
for suffix in ["/models", "/models/" + urllib.parse.quote(deployment, safe="")]:
    entry = {"metadataPath": suffix}
    request = urllib.request.Request(base + suffix, headers={"api-key": key}, method="GET")
    try:
        with opener.open(request, timeout=15) as response:
            entry["status"] = response.status
            data = json.loads(response.read(2 * 1024 * 1024))
            if suffix == "/models":
                entry["configuredModelVisible"] = any(m.get("id") == deployment for m in data.get("data", []))
            else:
                identifier = data.get("id")
                entry["returnedModelId"] = identifier if isinstance(identifier, str) and identifier.startswith("gpt-6.1-sol") else None
                entry["responseObject"] = data.get("object") if data.get("object") in ("model", "list") else None
                entry["rootProperties"] = list(data.keys())
    except urllib.error.HTTPError as error:
        entry["status"] = error.code
    except Exception as error:
        entry["errorType"] = type(error).__name__
    result["checks"].append(entry)
result["limitation"] = "Metadata access does not prove chat-completions authorization, deployed model mapping, structured-output behavior or generation success. No inference POST was sent."
(root / "docs/stage8-evidence/foundry-metadata-probe.json").write_text(json.dumps(result, indent=2))
print(json.dumps(result, indent=2))
