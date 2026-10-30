"""Exercise the published CLI command surface against disposable filesystem fixtures."""
import json
import pathlib
import shutil
import subprocess
import sys
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

    def run(*args, succeeds=True):
        result = subprocess.run([dotnet, str(cli), '--home', str(home), *args], text=True, input='', capture_output=True)
        if (result.returncode == 0) != succeeds:
            raise AssertionError(f'CLI {args}: {result.returncode}\n{result.stdout}\n{result.stderr}')
        return result

    def catalog():
        return json.loads((home / 'catalog.json').read_text(encoding='utf-8'))

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
    assert json.loads(run('games', 'list', '--json').stdout)[0]['Id'] == gid
    run('unknown-command', succeeds=False)
    run('config', 'set', 'check-updates', 'invalid', succeeds=False)
    run('help')
print('PASS CLI end-to-end: snapshots, external configs, ZIP review/install, edit protection, rollback, migration and errors')
