"""Exercise the published CLI command surface against disposable filesystem fixtures."""
import json
import pathlib
import shutil
import subprocess
import sys
import stat
import tempfile
import zipfile

cli = pathlib.Path(sys.argv[1]).resolve()
dotnet = shutil.which('dotnet')
if not dotnet:
    raise SystemExit('dotnet must be on PATH')
base = pathlib.Path.cwd() / '.context'
base.mkdir(exist_ok=True)
with tempfile.TemporaryDirectory(prefix='cli-', dir=base) as temporary:
    root = pathlib.Path(temporary).resolve()
    home, game, external = root / 'data', root / 'game', root / 'settings'
    game.mkdir()
    external.mkdir()
    (game / 'OptiScaler.ini').write_text('original', encoding='utf-8')
    (game / 'unrelated.txt').write_text('preserve', encoding='utf-8')
    (external / 'graphics.ini').write_text('quality=1', encoding='utf-8')

    def run(*args, succeeds=True, input_text=""):
        result = subprocess.run([dotnet, str(cli), '--home', str(home), *args], text=True, input=input_text, capture_output=True)
        if (result.returncode == 0) != succeeds:
            raise AssertionError(f'CLI {args}: {result.returncode}\n{result.stdout}\n{result.stderr}')
        return result

    def catalog():
        return json.loads((home / 'catalog.json').read_text(encoding='utf-8'))

    home.mkdir(exist_ok=True)
    stale_lock = home / 'session.lock'
    stale_lock.write_text('stale copied marker')
    stale_lock.chmod(stat.S_IREAD)
    run('config', 'set', 'download-thumbnails', 'false')
    assert not stale_lock.exists(), 'CLI did not clear its stale read-only session marker'
    assert catalog()['Settings']['DownloadThumbnails'] is False
    run('config', 'set', 'download-thumbnails', 'invalid', succeeds=False)
    assert catalog()['Settings']['DownloadThumbnails'] is False
    run('games', 'add', 'CLI fixture', str(game))
    gid = catalog()['Games'][0]['Id']
    run('files', 'add', gid, 'OptiScaler.ini')
    run('files', 'add', gid, 'dxgi.dll')
    run('files', 'add', gid, str(external / 'graphics.ini'), '--external', '--yes')
    run('snapshots', 'create', gid, 'Original', '--original', succeeds=False)
    run('snapshots', 'create', gid, 'Original', '--original', '--confirm-clean')
    sid = catalog()['Games'][0]['Snapshots'][0]['Id']
    (game / 'OptiScaler.ini').write_text('edited', encoding='utf-8')
    (external / 'graphics.ini').write_text('quality=2', encoding='utf-8')
    preview = json.loads(run('restore', gid, sid, '--dry-run', '--json').stdout)
    assert any(change['Action'] == 'replace' for change in preview['Changes'])
    run('restore', gid, sid, succeeds=False)
    assert (game / 'OptiScaler.ini').read_text() == 'edited'
    run('restore', gid, sid, '--yes')
    assert (game / 'OptiScaler.ini').read_text() == 'original'
    assert (external / 'graphics.ini').read_text() == 'quality=1'

    archive = root / 'package.zip'
    with zipfile.ZipFile(archive, 'w') as package:
        package.writestr('release/OptiScaler.ini', 'package-config')
        package.writestr('release/dxgi.dll', 'package-library')
        package.writestr('release/readme.txt', 'excluded')
    inspected = json.loads(run('packages', 'inspect', gid, str(archive), '--json').stdout)
    mapping = root / 'mapping.json'
    mapping.write_text(json.dumps({entry['Entry']: None if entry['Entry'].endswith('readme.txt') else entry['SuggestedPath'] for entry in inspected['Files']}))
    run('packages', 'import', gid, str(archive), 'Graphics', '1', '--map', str(mapping), '--yes')
    pid = catalog()['Games'][0]['Packages'][0]['Id']
    run('packages', 'install', gid, pid, '--dry-run')
    assert not (game / 'dxgi.dll').exists()
    run('packages', 'install', gid, pid, '--yes')
    iid = catalog()['Games'][0]['Installations'][0]['Id']
    assert (game / 'dxgi.dll').read_text() == 'package-library'
    run('restore', gid, sid, '--yes', succeeds=False)
    (game / 'OptiScaler.ini').write_text('user-edit', encoding='utf-8')
    run('packages', 'rollback', gid, iid, '--yes', succeeds=False)
    run('packages', 'rollback', gid, iid, '--override-edits', '--yes')
    assert (game / 'OptiScaler.ini').read_text() == 'original'
    assert not (game / 'dxgi.dll').exists()
    assert not (game / 'readme.txt').exists()
    assert (game / 'unrelated.txt').read_text() == 'preserve'
    run('packages', 'rollback', gid, iid, '--yes', succeeds=False)
    run('snapshots', 'rename', gid, sid, 'Clean baseline')
    run('packages', 'rename', gid, pid, 'Graphics version one')
    run('config', 'set', 'backup-directory', str(root / 'new-backups'), '--yes')
    run('restore', gid, sid, '--yes')
    assert (root / 'new-backups' / 'blobs').is_dir()
    assert (root / 'new-backups' / 'snapshots').is_dir()
    listed = json.loads(run('snapshots', 'list', gid, '--json').stdout)
    assert pathlib.Path(listed[0]['FolderPath']).is_dir()
    assert listed[0]['Files'][0]['Payload'].startswith('snapshots/')
    backed_up = next(state for state in listed[0]['Files'] if state['Hash'])
    payload = root / 'new-backups' / backed_up['Payload']
    original_bytes, mode = payload.read_bytes(), payload.stat().st_mode
    payload.chmod(mode | stat.S_IWRITE)
    payload.write_bytes(b'edited backup')
    rejected = run('restore', gid, sid, '--yes', succeeds=False)
    assert 'modified payload' in rejected.stderr
    assert (game / 'OptiScaler.ini').read_text() == 'original'
    payload.write_bytes(original_bytes)
    payload.chmod(mode)
    assert json.loads(run('games', 'list', '--json').stdout)[0]['Id'] == gid
    run('unknown-command', succeeds=False)
    run('config', 'set', 'check-updates', 'invalid', succeeds=False)
    run('help')
    # Drive the actual menu, including ambiguous searches and one bulk approval.
    for name in ('Cyberpunk 2077', 'Cyberpunk Edgerunners'):
        folder = root / name
        folder.mkdir()
        for file in ('OptiScaler.ini', 'dxgi.dll', 'Engine.ini', 'custom.cfg'):
            (folder / file).write_text(file, encoding='utf-8')
        run('games', 'add', name, str(folder))
    extra = str(external / 'graphics.ini')
    selected = run('menu', input_text=f'3\nCYB\nedg\ny\ncustom.cfg\n{extra}\n\ny\n0\n0\n')
    assert selected.stdout.count('Manage this entire selection') == 1
    assert 'Manage this file' not in selected.stdout
    chosen = next(g for g in catalog()['Games'] if g['Name'] == 'Cyberpunk Edgerunners')
    assert len(chosen['Managed']) == 5, selected.stdout + selected.stderr
    assert len(chosen['ExternalRoots']) == 1
    before = chosen['Managed']
    repeated = run('menu', input_text='3\nedg\n1\nn\n0\n0\n')
    assert next(g for g in catalog()['Games'] if g['Id'] == chosen['Id'])['Managed'] == before
    declined = run('menu', input_text='3\n2077\nn\nn\n0\n0\n')
    assert 'Selection cancelled' in declined.stdout
    assert next(g for g in catalog()['Games'] if g['Name'] == 'Cyberpunk 2077')['Managed'] == []
    run('menu', input_text='3\n2077\ny\n../outside.ini\n\n')
    assert next(g for g in catalog()['Games'] if g['Name'] == 'Cyberpunk 2077')['Managed'] == []
    unmatched = run('menu', input_text='3\nno-such-game\n*\nedg\n0\n0\n')
    assert 'No games match' in unmatched.stdout
print('PASS CLI end-to-end: backups, packages, game search, automatic bulk selection, extras, cancellation and validation')
