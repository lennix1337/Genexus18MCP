"""Read the tool payload from a saved MCP `tools/call` response."""
import json
from pathlib import Path


def readback_payload(path):
    """Return `result.structuredContent`, or the JSON in `result.content[0].text`.

    The Gateway omits structuredContent by default (it duplicates the text), so
    evidence captured either way must validate.
    """
    result = json.loads(Path(path).read_text(encoding='utf-8'))['result']
    structured = result.get('structuredContent')
    if structured is not None:
        return structured
    return json.loads(result['content'][0]['text'])
