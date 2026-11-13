import json
import tempfile
import unittest
from pathlib import Path

from mcp_readback import readback_payload


class ReadbackPayloadTests(unittest.TestCase):
    def read(self, result):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'readback.json'
            path.write_text(json.dumps({'result': result}), encoding='utf-8')
            return readback_payload(path)

    def test_structured_content_is_used_when_present(self):
        self.assertEqual({'a': 1}, self.read({'structuredContent': {'a': 1}, 'content': [{'text': '{"a":2}'}]}))

    def test_text_content_is_parsed_when_structured_content_is_omitted(self):
        self.assertEqual({'a': 2}, self.read({'content': [{'type': 'text', 'text': '{"a":2}'}]}))


if __name__ == '__main__':
    unittest.main()
