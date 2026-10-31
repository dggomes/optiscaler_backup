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
    # Review inferred originals/new files through the real CLI before approving writes.
    inventory_game = root / 'Inventory game'
    inventory_game.mkdir()
    (inventory_game / 'OptiScaler.ini').write_text('clean')
    (inventory_game / 'OptiScaler.asi').write_text('plugin')
    inv_id = json.loads(run('games', 'add', 'Inventory game', str(inventory_game)).stdout)['Id']
    run('files', 'add', inv_id, 'OptiScaler.ini')
    plan = json.loads(run('snapshots', 'create', inv_id, 'Original', '--original', '--dry-run', '--json').stdout)
    assert len(plan['Targets']) == 2
    run('snapshots', 'create', inv_id, 'Original', '--original', '--confirm-clean', succeeds=False)
    original = json.loads(run('snapshots', 'create', inv_id, 'Original', '--original', '--confirm-clean', '--yes').stdout)
    assert len(original['Files']) == 2
    assert (home / 'game-inventories.json').exists()
    (inventory_game / 'custom.dat').write_text('new bytes')
    inferred = json.loads(run('snapshots', 'create', inv_id, 'Modded', '--dry-run', '--json').stdout)
    assert any(t['Path'] == 'custom.dat' for t in inferred['Targets'])
    run('snapshots', 'create', inv_id, 'Modded', succeeds=False)
    run('snapshots', 'create', inv_id, 'Modded', '--yes')
    run('restore', inv_id, original['Id'], '--yes')
    assert not (inventory_game / 'custom.dat').exists()
    # Inspect saved graphics preferences through the real CLI, then change live files.
    graphics_game = root / 'Unreal graphics game'
    graphics_game.mkdir()
    (graphics_game / 'GameUserSettings.ini').write_text('[/Script/Engine.GameUserSettings]\nResolutionSizeX=2560\nResolutionSizeY=1440\nFullscreenMode=1\n', encoding='utf-8')
    (graphics_game / 'Engine.ini').write_text('[SystemSettings]\nr.FidelityFX.FSR3.Enabled=1\nr.FidelityFX.FSR3.QualityMode=0\n', encoding='utf-8')
    graphics_id = json.loads(run('games', 'add', 'Unreal graphics game', str(graphics_game)).stdout)['Id']
    for config in ('GameUserSettings.ini', 'Engine.ini'):
        run('files', 'add', graphics_id, config)
    graphics_snapshot = json.loads(run('snapshots', 'create', graphics_id, 'Native AA').stdout)
    captured = json.loads(run('settings', graphics_id, graphics_snapshot['Id'], '--json').stdout)
    assert any(o['Setting'] == 'FSR quality preference' and o['Value'] == 'Native AA' for o in captured['Observations'])
    (graphics_game / 'Engine.ini').write_text('[SystemSettings]\nr.FidelityFX.FSR3.QualityMode=3\n', encoding='utf-8')
    current = json.loads(run('settings', graphics_id, '--json').stdout)
    assert any(o['Value'] == 'Performance' for o in current['Observations'])
    assert any(o['Value'] == 'Native AA' for o in json.loads(run('settings', graphics_id, graphics_snapshot['Id']).stdout)['Observations'])
    catalog = json.loads(run('components', 'catalog', '--json').stdout)
    assert catalog['Count'] >= 98
    identity = json.loads(run('components', 'identify', graphics_id, graphics_snapshot['Id'], '--json').stdout)
    assert not identity['Identified'], 'INI filenames must not identify installed DLL versions'
    listed_graphics = json.loads(run('snapshots', 'list', graphics_id, '--json').stdout)
    assert listed_graphics[0]['Components']['CatalogSha256'] == identity['CatalogSha256']
print('PASS CLI end-to-end: backups, packages, game search, automatic bulk selection, extras, cancellation and validation')
