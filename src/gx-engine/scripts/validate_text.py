"""Compare the admitted string subset with source-matched native evidence; no KB access."""
import argparse
import json
from pathlib import Path
import subprocess
import tempfile

from mcp_readback import readback_payload
from string_semantics_cases import (
    compare_output, engine_expected, length_labels, native_expected, source, variables, safety_divergences,
)


def verify_evidence(folder):
    folder = Path(folder)
    authored = json.loads((folder / 'authored.json').read_text(encoding='utf-8'))
    report = json.loads((folder / 'report.json').read_text(encoding='utf-8'))
    if authored['source'] != source or authored['variables'] != variables:
        raise ValueError('native evidence belongs to different authored inputs')
    if report['exit'] != 0 or not report['source_verified']:
        raise ValueError('native execution was not verified')
    for part in ['Source', 'Variables']:
        result = readback_payload(folder / f'readback-{part}.json')
        if result.get('truncated') or result.get('isTruncatedByWorker'):
            raise ValueError('native readback was truncated')
        if part == 'Source' and result['source'].replace('\r\n', '\n') != source:
            raise ValueError('native source mismatch')
        if part == 'Variables':
            actual = {v['name'].lstrip('&').upper(): v for v in result['variables']}
            for expected in variables:
                item = actual[expected['varName'].upper()]
                if item['type'].upper() != expected['typeName'].upper() or item['length'] != expected['length']:
                    raise ValueError('native variable declaration mismatch')
    compare_output(report['stdout'], native_expected)
    return report


def validate(engine, evidence):
    verify_evidence(evidence)
    fixture = Path(__file__).resolve().parent.parent / 'fixtures' / 'typed-strings.json'
    with tempfile.TemporaryDirectory(prefix='gx-text-differential-') as output:
        result = subprocess.run([str(Path(engine).resolve()), 'run', str(fixture), output],
                                capture_output=True, text=True, encoding='utf-8', timeout=150)
        if result.returncode:
            raise ValueError('Rust text execution failed: ' + result.stderr)
        count = compare_output(result.stdout, engine_expected)
    return {'matched_outputs': count - len(safety_divergences), 'safety_divergences': safety_divergences, 'native_observations': len(native_expected),
            'length_labels_normalized_to_values': sorted(length_labels),
            'scope': 'standalone Character/Varchar storage, concat and equality subset; no full SDK parity'}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--engine', required=True)
    parser.add_argument('--native-evidence', required=True)
    parser.add_argument('--report', required=True)
    args = parser.parse_args()
    report = validate(args.engine, args.native_evidence)
    Path(args.report).write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps(report, indent=2))
