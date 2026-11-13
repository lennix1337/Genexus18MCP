"""Differential Date fixture validation with source-matched native evidence; no SDK/KB access."""
import argparse
import json
from pathlib import Path
import subprocess
import tempfile

from mcp_readback import readback_payload
import date_semantics_cases
import calendar_semantics_cases
import datetime_semantics_cases


def compare_output(output, expected):
    actual = output.splitlines()
    if actual != expected:
        raise ValueError(f'Date output mismatch: {actual!r} != {expected!r}')
    return len(actual)


def verify_evidence(folder, cases=date_semantics_cases):
    source, variables, native_expected = cases.source, cases.variables, cases.native_expected
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
                if item['type'].upper() != expected['typeName'].upper():
                    raise ValueError('native variable type mismatch')
                if expected['typeName'].upper() == 'NUMERIC':
                    if item['length'] != expected['length'] or item.get('decimals', 0) != expected['decimals']:
                        raise ValueError('native Numeric declaration mismatch')
    compare_output(report['stdout'], native_expected)
    return report


def validate(engine, evidence, calendar=False, date_time=False):
    if calendar and date_time:
        raise ValueError('choose only one calendar fixture')
    cases = datetime_semantics_cases if date_time else calendar_semantics_cases if calendar else date_semantics_cases
    verify_evidence(evidence, cases)
    fixture = Path(__file__).resolve().parent.parent / 'fixtures' / ('typed-datetime.json' if date_time else 'typed-calendar.json' if calendar else 'typed-dates.json')
    with tempfile.TemporaryDirectory(prefix='gx-date-differential-') as output:
        result = subprocess.run([str(Path(engine).resolve()), 'run', str(fixture), output],
                                capture_output=True, text=True, encoding='utf-8', timeout=150)
        if result.returncode:
            raise ValueError('Rust Date execution failed: ' + result.stderr)
        count = compare_output(result.stdout, [line.split('=', 1)[1] for line in cases.native_expected])
    return {'matched_outputs': count, 'native_observations': len(cases.native_expected),
            'two_digit_year_max': None if calendar else 2039,
            'policy': 'explicit fixture setting, not a universal default',
            'scope': 'standalone Gregorian Date subset' + (' with integral-second DateTime' if date_time else ' with calendar month/year shifts' if calendar else ' with measured year window') + '; no full SDK parity'}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    group = parser.add_mutually_exclusive_group()
    group.add_argument('--calendar', action='store_true', help='validate the separate month/year-shift fixture')
    group.add_argument('--datetime', action='store_true', help='validate the integral-second DateTime fixture')
    parser.add_argument('--engine', required=True)
    parser.add_argument('--native-evidence', required=True)
    parser.add_argument('--report', required=True)
    args = parser.parse_args()
    report = validate(args.engine, args.native_evidence, args.calendar, args.datetime)
    Path(args.report).write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps(report, indent=2))
