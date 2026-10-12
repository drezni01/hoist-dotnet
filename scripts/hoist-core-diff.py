#!/usr/bin/env python3
"""
Map a hoist-core version diff onto the HoistDotnet port.

Reads docs/PORT-MANIFEST.yaml, runs `git diff --name-status FROM..TO` in the hoist-core repo, and
classifies every changed file as:
  - affecting a ported component (by its hoistCoreSources globs) or the shared conventions,
  - belonging to a deliberately skipped area (no action needed), or
  - unmapped (needs a human/agent decision: new feature? new file in a ported area?).

Usage:
  python -I scripts/hoist-core-diff.py [--from v42.1.0] [--to v43.0.0] [--repo ../hoist-core] [--json]

--from defaults to hoistCore.version in the manifest (as tag v<version>); --to defaults to the
repo's latest tag. Uses only the standard library (the manifest format is parsed directly).
"""
import argparse
import json
import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MANIFEST = os.path.join(ROOT, 'docs', 'PORT-MANIFEST.yaml')
SOURCE_KEYS = ('hoistCoreSources', 'paths')


def strip_comment(line):
    # Comments start with ' #' outside quotes - the manifest uses no quoted '#'.
    idx = line.find(' #')
    return line if idx < 0 else line[:idx]


def parse_inline_list(text):
    text = text.strip()
    if not (text.startswith('[') and text.endswith(']')):
        return [text] if text else []
    return [p.strip() for p in text[1:-1].split(',') if p.strip()]


def parse_manifest(path):
    """Returns (pins, groups) where groups is a list of {name, kind, status, globs}."""
    pins = {}
    groups = []
    section = None          # 'hoistCore' | 'shared' | 'components' | 'skipped' | ...
    current = None          # current group dict
    list_key = None         # key whose block list we are reading

    with open(path, encoding='utf-8') as f:
        for raw in f:
            line = strip_comment(raw.rstrip('\n'))
            if not line.strip():
                continue
            indent = len(line) - len(line.lstrip(' '))
            text = line.strip()

            if indent == 0:
                section = text.rstrip(':').split(':')[0]
                current, list_key = None, None
                if section == 'shared':
                    current = {'name': 'shared', 'kind': 'shared', 'status': 'ported', 'globs': []}
                    groups.append(current)
                continue

            if section == 'hoistCore' and ':' in text:
                k, v = text.split(':', 1)
                pins[k.strip()] = v.strip()
                continue

            if section == 'components' and indent == 2 and text.endswith(':'):
                current = {'name': text[:-1], 'kind': 'component', 'status': None, 'globs': []}
                groups.append(current)
                list_key = None
                continue

            if section == 'skipped' and text.startswith('- area:'):
                current = {'name': text.split(':', 1)[1].strip(), 'kind': 'skipped', 'status': 'skipped', 'globs': []}
                groups.append(current)
                list_key = None
                continue

            if current is None:
                continue

            if text.startswith('- ') and list_key in SOURCE_KEYS:
                current['globs'].append(text[2:].strip())
                continue

            if ':' in text:
                k, v = text.split(':', 1)
                k = k.strip()
                v = v.strip()
                list_key = k
                if k == 'status':
                    current['status'] = v
                elif k in SOURCE_KEYS and v:
                    current['globs'].extend(parse_inline_list(v))
    return pins, groups


def glob_to_regex(glob):
    out = ''
    i = 0
    while i < len(glob):
        if glob.startswith('**/', i):
            out += '(?:.*/)?'
            i += 3
        elif glob.startswith('**', i):
            out += '.*'
            i += 2
        elif glob[i] == '*':
            out += '[^/]*'
            i += 1
        else:
            out += re.escape(glob[i])
            i += 1
    return re.compile('^' + out + '$')


def git(repo, *args):
    return subprocess.run(['git', '-C', repo, *args], check=True, capture_output=True, text=True).stdout


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--from', dest='from_ref')
    ap.add_argument('--to', dest='to_ref')
    ap.add_argument('--repo')
    ap.add_argument('--json', action='store_true')
    args = ap.parse_args()

    pins, groups = parse_manifest(MANIFEST)
    repo = os.path.normpath(os.path.join(ROOT, args.repo or pins.get('repo', '../hoist-core')))
    from_ref = args.from_ref or 'v' + pins.get('version', '')
    to_ref = args.to_ref or git(repo, 'describe', '--tags', '--abbrev=0', 'HEAD').strip()

    matchers = [(g, [glob_to_regex(x) for x in g['globs']]) for g in groups]
    diff = git(repo, 'diff', '--name-status', f'{from_ref}..{to_ref}')

    result = {'from': from_ref, 'to': to_ref, 'repo': repo, 'components': {}, 'skipped': {}, 'unmapped': []}
    for row in diff.splitlines():
        parts = row.split('\t')
        status, path = parts[0], parts[-1]
        hits = [g for g, rx in matchers if any(r.match(path) for r in rx)]
        entry = {'status': status, 'path': path}
        relevant = [g for g in hits if g['kind'] != 'skipped']
        if relevant:
            for g in relevant:
                result['components'].setdefault(g['name'], {'status': g['status'], 'files': []})['files'].append(entry)
        elif hits:
            result['skipped'].setdefault(hits[0]['name'], []).append(entry)
        else:
            result['unmapped'].append(entry)

    if args.json:
        json.dump(result, sys.stdout, indent=2)
        return

    print(f"hoist-core {from_ref}..{to_ref}  ({repo})\n")
    print("== Ported components affected ==")
    if not result['components']:
        print("  (none)")
    for name, info in sorted(result['components'].items()):
        print(f"  [{name}] ({info['status']})")
        for f in info['files']:
            print(f"      {f['status']}  {f['path']}")
    print("\n== Unmapped changes (triage: new feature, or extend a component's hoistCoreSources) ==")
    if not result['unmapped']:
        print("  (none)")
    for f in result['unmapped']:
        print(f"      {f['status']}  {f['path']}")
    print("\n== Skipped areas (no action) ==")
    for name, files in sorted(result['skipped'].items()):
        print(f"  [{name}] {len(files)} file(s)")


if __name__ == '__main__':
    main()
