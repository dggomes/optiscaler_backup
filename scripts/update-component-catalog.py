"""Build append-only recognition metadata. Never execute or install downloaded DLLs."""
import argparse
import concurrent.futures
import hashlib
import json
import pathlib
import re
import shutil
import subprocess
import sys
import tempfile
import threading
import urllib.parse
import urllib.request
import uuid
import zipfile

ROOT = pathlib.Path(__file__).resolve().parents[1]
OUTPUT = ROOT / 'src/core/component-fingerprints.json'
MAX_ARCHIVE = 512 * 1024 * 1024
MAX_FILE = 256 * 1024 * 1024
CORE_NAMES = {'optiscaler.dll', 'optiscaler.asi', 'cyberxess.dll', 'nvngx.dll', 'dxgi.dll', 'winmm.dll', 'version.dll'}


def gh(path):
    return json.loads(subprocess.check_output(['gh', 'api', path], text=True))


def fetch(url, destination, maximum=MAX_ARCHIVE):
    uri = urllib.parse.urlparse(url)
    if uri.scheme != 'https' or uri.hostname not in ('github.com', 'raw.githubusercontent.com') or uri.username:
        raise ValueError('Unapproved upstream URL: ' + url)
    with urllib.request.urlopen(urllib.request.Request(url, headers={'User-Agent': 'OptiScalerBackup-Catalog'}), timeout=90) as source:
        if urllib.parse.urlparse(source.url).scheme != 'https':
            raise ValueError('Insecure upstream redirect')
        with destination.open('wb') as target:
            size = 0
            while chunk := source.read(1024 * 1024):
                size += len(chunk)
                if size > maximum:
                    raise ValueError('Upstream response exceeds configured bound')
                target.write(chunk)


def stream_digest(source):
    result = hashlib.sha256()
    while chunk := source.read(1024 * 1024):
        result.update(chunk)
    return result.hexdigest()


def digest(path):
    with path.open('rb') as source:
        return stream_digest(source)


def safe_entry(name):
    name = name.replace('\\', '/')
    parts = name.rstrip('/').split('/')
    if not name or len(name) > 4096 or any(p in ('', '.', '..') or ':' in p or any(ord(c) < 32 for c in p) for p in parts):
        raise ValueError('Unsafe archive entry: ' + name)
    return name


def bounded_process(arguments, maximum, timeout, hash_only=False):
    with tempfile.TemporaryFile() as errors:
        process = subprocess.Popen(arguments, stdout=subprocess.PIPE, stderr=errors)
        timer = threading.Timer(timeout, process.kill)
        timer.start()
        result = hashlib.sha256() if hash_only else bytearray()
        size = 0
        try:
            while chunk := process.stdout.read(65536):
                size += len(chunk)
                if size > maximum:
                    raise ValueError('Archive output exceeded its bound')
                result.update(chunk) if hash_only else result.extend(chunk)
            if process.wait() != 0:
                raise ValueError('Archive inspection failed or timed out')
            return (result.hexdigest(), size) if hash_only else bytes(result)
        finally:
            timer.cancel()
            if process.poll() is None:
                process.kill()
            process.wait()
            process.stdout.close()


def seven_entries(path, seven):
    output = bounded_process([seven, 'l', '-slt', '-ba', str(path)], 8 * 1024 * 1024, 30).decode('utf-8')
    if len(output) > 8 * 1024 * 1024:
        raise ValueError('Oversized archive listing')
    records = []
    seen = set()
    for block in re.split(r'\r?\n\r?\n', output.strip()):
        fields = {}
        for line in block.splitlines():
            if ' = ' not in line:
                raise ValueError('Malformed 7z listing')
            key, value = line.split(' = ', 1)
            if key in fields:
                raise ValueError('Duplicate archive field')
            fields[key] = value
        name = safe_entry(fields['Path'])
        if name.casefold() in seen:
            raise ValueError('Case-insensitive archive collision')
        seen.add(name.casefold())
        if fields.get('Encrypted') == '+' or any(k in fields for k in ('Symbolic Link', 'Hard Link')):
            raise ValueError('Links/encryption are not allowed')
        if 'l' in fields.get('Mode', '')[:1]:
            raise ValueError('Linked archive entry')
        if fields.get('Folder') == '+' or fields.get('Attributes', '').startswith('D'):
            continue
        records.append((name, int(fields.get('Size', '0'))))
    return records


def binary_hashes(path, seven):
    if path.suffix.lower() == '.zip':
        with zipfile.ZipFile(path) as archive:
            entries = archive.infolist()
            seen = set()
            for entry in entries:
                name = safe_entry(entry.filename)
                if name.casefold() in seen or (entry.external_attr >> 16) & 0o170000 == 0o120000 or entry.flag_bits & 1:
                    raise ValueError('Unsafe ZIP entries')
                seen.add(name.casefold())
            if len(entries) > 10000 or sum(e.file_size for e in entries) > 2 * 1024**3:
                raise ValueError('ZIP extraction limit exceeded')
            for entry in entries:
                if pathlib.PurePosixPath(entry.filename).name.lower() in CORE_NAMES:
                    if entry.file_size > MAX_FILE:
                        raise ValueError('DLL exceeds extraction bound')
                    with archive.open(entry) as source:
                        yield entry.filename, stream_digest(source), entry.file_size
    else:
        entries = seven_entries(path, seven)
        if len(entries) > 10000 or sum(size for _, size in entries) > 2 * 1024**3:
            raise ValueError('7z extraction limit exceeded')
        for name, size in entries:
            if pathlib.PurePosixPath(name).name.lower() not in CORE_NAMES:
                continue
            if size < 1 or size > MAX_FILE:
                raise ValueError('DLL exceeds extraction bound')
            sha, actual_size = bounded_process([seven, 'x', '-so', '-spd', str(path), '--', name], min(size, MAX_FILE), 90, hash_only=True)
            if actual_size != size:
                raise ValueError('Extracted DLL length does not match listing')
            yield name, sha, size


def identity(row):
    fields = {k: v for k, v in row.items() if k not in ('SourceRevision', 'ObservedAt')}
    return hashlib.sha256(json.dumps(fields, sort_keys=True, separators=(',', ':')).encode()).hexdigest()


def releases(repo):
    result = []
    for page in range(1, 21):
        batch = gh(f'repos/{repo}/releases?per_page=100&page={page}')
        result.extend(r for r in batch if not r['draft'])
        if len(batch) < 100:
            return result
    raise ValueError('Release pagination exceeded bound; refusing incomplete refresh')


def build(args):
    old = json.loads(OUTPUT.read_text()) if OUTPUT.exists() else {'Version': 1, 'Records': [], 'Sources': []}
    if old['Version'] != 1:
        raise ValueError('Unsupported existing catalog; no rewrite')
    records = {identity(row): row for row in old['Records']}
    sources = {row['Revision']: row for row in old['Sources']}
    if args.history:
        previous_file = pathlib.Path(args.history)
        if previous_file.stat().st_size > 32 * 1024 * 1024:
            raise ValueError('Oversized pending history')
        previous = json.loads(previous_file.read_text())
        if previous['Version'] != 1:
            raise ValueError('Unsupported pending catalog; no rewrite')
        for row in previous['Records']:
            records.setdefault(identity(row), row)
        for row in previous['Sources']:
            sources.setdefault(row['Revision'], row)
    cache = pathlib.Path(args.cache).resolve()
    cache.mkdir(parents=True, exist_ok=True)
    # Pin both bytes and signature to the same commit; do not race moving main.
    revision = gh('repos/xt0n1-t3ch/DLSSync-Manifest/commits/main')['sha']
    manifest = cache / (revision + '.json')
    signature = cache / (revision + '.sig')
    base = 'https://raw.githubusercontent.com/xt0n1-t3ch/DLSSync-Manifest/' + revision
    fetch(base + '/manifest-v3.json', manifest, 16 * 1024 * 1024)
    fetch(base + '/manifest-v3.json.sig', signature, 4096)
    subprocess.run(['node', str(ROOT / 'scripts/verify-component-source.mjs'), str(manifest), str(signature)], check=True)
    data = json.loads(manifest.read_text())
    if data.get('schema_version') != 3 or not data.get('vendors', {}).get('amd'):
        raise ValueError('Unexpected upstream catalog; old history preserved')
    added = False
    for family, catalog in data['vendors']['amd'].items():
        if not family.startswith('fsr_'):
            continue
        for release in catalog['releases']:
            artifact = release.get('artifact') or {}
            file_hash = artifact.get('hash') or {}
            if file_hash.get('algorithm') != 'sha256':
                continue  # MD5 is not promoted to SHA-256 evidence.
            row = dict(Component='FSR', Role=family.removeprefix('fsr_'), Version=release['version'],
                       FileName=release['filename'], Sha256=file_hash['digest'].lower(), Size=int(release['size_bytes']),
                       Source='DLSSync signed catalog', SourceRevision=revision, ReleaseTag=release['source'],
                       AssetId=artifact['id'], AssetUrl=artifact['source_url'], ArchiveSha256='',
                       ArchiveEntry=artifact.get('archive_entry') or '', Channel=release.get('channel', 'stable'))
            key = identity(row)
            if key not in records:
                records[key] = row
                added = True
    if added:
        sources[revision] = dict(Revision=revision, Url=base + '/manifest-v3.json', Sha256=digest(manifest),
                                 Signature=signature.read_text().strip(), PublicKey='e9dd0828f9ee5ecb72e0a811723a79c6e5373ca1c20bd5b255d68a2b3928fcd3')

    work = []
    for repo, component in [('optiscaler/OptiPatcher', 'OptiPatcher'), ('optiscaler/OptiScaler', 'OptiScaler')]:
        for release in releases(repo):
            for asset in release['assets']:
                ext = pathlib.Path(asset['name']).suffix.lower()
                if ext not in ('.asi', '.7z', '.zip') or (component == 'OptiPatcher' and ext != '.asi'):
                    continue
                remote_hash = (asset.get('digest') or '').removeprefix('sha256:').lower()
                known = [r for r in records.values() if r['Source'] == repo and r['AssetId'] == str(asset['id'])]
                if known and remote_hash and all(r['ArchiveSha256'] == remote_hash for r in known):
                    continue
                work.append((repo, component, release, asset, remote_hash))

    def process(item):
        repo, component, release, asset, remote_hash = item
        if asset['size'] > MAX_ARCHIVE:
            raise ValueError('Release asset exceeds bound: ' + asset['name'])
        path = cache / (str(asset['id']) + pathlib.Path(asset['name']).suffix.lower())
        if not path.exists() or path.stat().st_size != asset['size'] or (remote_hash and digest(path) != remote_hash):
            fetch(asset['browser_download_url'], path)
        actual = digest(path)
        if path.stat().st_size != asset['size'] or (remote_hash and actual != remote_hash):
            raise ValueError('Official release asset digest/length mismatch: ' + asset['name'])
        files = [(asset['name'], actual, asset['size'])] if component == 'OptiPatcher' else list(binary_hashes(path, args.seven_zip))
        if not files:
            raise ValueError('No recognized primary component in ' + asset['name'])
        version = release['tag_name'].lstrip('v')
        if component == 'OptiPatcher':
            named = re.search(r'OptiPatcher[_-]v?(\d+\.\d+[a-z]?)\.asi$', asset['name'], re.I)
            if named:
                version = named.group(1)
        if component == 'OptiScaler':
            match = re.search(r'(\d+\.\d+(?:\.\d+)?[a-z]?(?:[-_]pre\d+)?)', asset['name'], re.I)
            if match:
                version = match.group(1)
                date = re.search(r'20\d{6}', asset['name'])
                if date:
                    version += ' (' + date.group(0) + ')'
        return [dict(Component=component, Role='plugin' if component == 'OptiPatcher' else 'library', Version=version,
                     FileName=pathlib.PurePosixPath(name).name, Sha256=sha, Size=size, Source=repo,
                     SourceRevision=str(asset['id']) + ':' + actual, ReleaseTag=release['tag_name'], AssetId=str(asset['id']),
                     AssetUrl=asset['browser_download_url'], ArchiveSha256=actual, ArchiveEntry=name,
                     Channel='prerelease' if release['prerelease'] else 'stable') for name, sha, size in files]

    with concurrent.futures.ThreadPoolExecutor(max_workers=args.workers) as pool:
        for rows in pool.map(process, work):
            for row in rows:
                records.setdefault(identity(row), row)
            print('Indexed', rows[0]['Component'], rows[0]['ReleaseTag'], file=sys.stderr)
    result = dict(Version=1, Records=sorted(records.values(), key=lambda r: (r['Component'], r['Version'], r['Sha256'], r['ReleaseTag'])),
                  Sources=sorted(sources.values(), key=lambda s: s['Revision']))
    encoded = json.dumps(result, ensure_ascii=False, indent=2) + '\n'
    if len(encoded.encode()) > 32 * 1024 * 1024:
        raise ValueError('Catalog exceeds app bound')
    stage = OUTPUT.with_name(OUTPUT.name + '.' + uuid.uuid4().hex + '.tmp')
    try:
        stage.write_text(encoded)
        stage.replace(OUTPUT)
    finally:
        if stage.exists():
            stage.unlink()
    print(f'{len(result["Records"])} cumulative fingerprints; {len(work)} assets inspected')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--history', help='Append observed history from an existing review branch')
    parser.add_argument('--cache', default=str(ROOT / '.context/component-history-cache'))
    parser.add_argument('--seven-zip', default=shutil.which('7zz') or shutil.which('7z'))
    parser.add_argument('--workers', type=int, default=3)
    options = parser.parse_args()
    if not options.seven_zip or not 1 <= options.workers <= 4:
        parser.error('A trusted 7z/7zz tool and 1–4 workers are required')
    try:
        build(options)
    except Exception as error:
        print('Catalog refresh failed; previous catalog retained:', error, file=sys.stderr)
        raise SystemExit(1)
