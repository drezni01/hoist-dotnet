#!/usr/bin/env python3
"""
Side-by-side check of the .NET port against a running hoist-core (Grails) server on the same DB.

Logs in to both servers the way a JWT-based hoist-react client does (Bearer token + xh/login), calls the same read-only
endpoints on each, and reports differences in JSON shape and values. Volatile values (timestamps,
version strings, instance names) are ignored.

Usage:
  python -I scripts/compare-with-grails.py --grails http://localhost:8080 --dotnet http://localhost:8090 \
      --username alice --token <JWT issued by the host app>

Only GET-style reads are issued - nothing is written to either server.
"""
import argparse
import http.cookiejar
import json
import sys
import urllib.parse
import urllib.request

VOLATILE = {'timestamp', 'serverTimeZoneOffset', 'appTimeZoneOffset', 'javaVersion', 'grailsVersion',
            'hoistCoreVersion', 'instanceName', 'databaseConnectionString', 'databaseUser', 'databaseCreateMode'}


def client(base, token):
    jar = http.cookiejar.CookieJar()
    opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(jar))
    opener.addheaders = [('Authorization', f'Bearer {token}'), ('Accept', 'application/json')]

    def call(path, form=None):
        data = urllib.parse.urlencode(form).encode() if form is not None else None
        try:
            with opener.open(base.rstrip('/') + '/' + path, data=data, timeout=30) as r:
                body = r.read().decode()
                return r.status, (json.loads(body) if body else None)
        except urllib.error.HTTPError as e:
            body = e.read().decode()
            try:
                return e.code, json.loads(body) if body else None
            except ValueError:
                return e.code, body
    return call


def diff(a, b, path='$', out=None):
    out = [] if out is None else out
    if isinstance(a, dict) and isinstance(b, dict):
        for k in sorted(set(a) | set(b)):
            if k in VOLATILE:
                continue
            if k not in a:
                out.append(f'{path}.{k}: only in dotnet')
            elif k not in b:
                out.append(f'{path}.{k}: only in grails')
            else:
                diff(a[k], b[k], f'{path}.{k}', out)
    elif isinstance(a, list) and isinstance(b, list):
        if len(a) != len(b):
            out.append(f'{path}: length {len(a)} (grails) vs {len(b)} (dotnet)')
        for i, (x, y) in enumerate(zip(a, b)):
            diff(x, y, f'{path}[{i}]', out)
    elif a != b:
        out.append(f'{path}: {json.dumps(a)[:120]} (grails) vs {json.dumps(b)[:120]} (dotnet)')
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--grails', required=True)
    ap.add_argument('--dotnet', required=True)
    ap.add_argument('--username', required=True)
    ap.add_argument('--token', required=True)
    ap.add_argument('--view-type', default='xhAdminActivityTrackingView')
    args = ap.parse_args()

    user = args.username
    checks = [
        ('xh/version', None),
        ('xh/authStatus', None),
        ('xh/getConfig', None),
        ('xh/environment', None),
        ('xh/getPrefs', {'clientUsername': user}),
        ('xh/impersonationTargets', None),
        ('xhView/allData', {'type': args.view_type, 'viewInstance': 'default'}),
        ('rest/configAdmin', None),
        ('rest/preferenceAdmin', None),
        ('roleAdmin/config', None),
        ('roleAdmin/list', None),
        ('trackLogAdmin/lookups', None),
    ]

    sides = {}
    for name, base in (('grails', args.grails), ('dotnet', args.dotnet)):
        call = client(base, args.token)
        status, _ = call('xh/login', {'username': user, 'password': '*****'})
        print(f'{name}: login -> {status}')
        sides[name] = call

    failures = 0
    for path, form in checks:
        g_status, g_body = sides['grails'](path, form)
        d_status, d_body = sides['dotnet'](path, form)
        problems = [] if g_status == d_status else [f'status {g_status} (grails) vs {d_status} (dotnet)']
        problems += diff(g_body, d_body)
        mark = 'OK  ' if not problems else 'DIFF'
        print(f'{mark} {path}')
        for p in problems[:25]:
            print(f'       {p}')
        failures += bool(problems)
    sys.exit(1 if failures else 0)


if __name__ == '__main__':
    main()
