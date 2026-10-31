import { spawnSync } from 'node:child_process';
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

function run(command, args) {
  const result = spawnSync(command, args, { stdio: 'inherit' });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error(`${command} ${args.join(' ')} failed (${result.status})`);
}
function git(args) {
  run(process.platform === 'win32' ? 'git' : '/usr/bin/git', args);
}
function files(root = '.') {
  return readdirSync(root, { withFileTypes: true }).flatMap(entry => {
    if (['.git', '.context', 'bin', 'obj', 'node_modules', 'artifacts', '__pycache__'].includes(entry.name)) return [];
    const path = join(root, entry.name);
    return entry.isDirectory() ? files(path) : [path];
  });
}
function security() {
  for (const path of files()) {
    if (/(^|[/\\])\.env($|\.)/.test(path)) throw new Error(`Local environment/secret file forbidden: ${path}`);
    const text = readFileSync(path, 'utf8');
    if (/-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----/.test(text) ||
        /(?:ghp_|github_pat_)[A-Za-z0-9_]{30,}/.test(text) || /(?:AKIA|ASIA)[A-Z0-9]{16}/.test(text)) {
      throw new Error(`Possible secret in ${path}; inspect locally (secret value not printed).`);
    }
  }
  console.log('Secret-pattern scan passed. No credentials or service APIs required.');
}
try {
  switch (process.argv[2]) {
    case 'preflight':
      if (!existsSync('global.json')) throw new Error('Run from repository root.');
      run('dotnet', ['--version']);
      git(['status', '--short']);
      console.log('Workflow: .NET build, filesystem integration tests, format check, security-scan. Windows CI adds executable updater tests.');
      break;
    case 'migrations': console.log('No database migrations in this application.'); break;
    case 'check':
      security();
      run('dotnet', ['build', 'optiscaler-backup.sln', '-c', 'Release']);
      run('dotnet', ['build', 'scripts/playnite/optiscaler-playnite.csproj', '-c', 'Release']);
      run('dotnet', ['run', '--project', 'tests', '-c', 'Release', '--no-build']);
      run('python3', ['scripts/test-cli.py', 'src/cli/bin/Release/net10.0/optibackup.dll']);
      run('python3', ['scripts/test-component-catalog.py']);
      run('dotnet', ['format', 'optiscaler-backup.sln', '--verify-no-changes', '--no-restore']);
      run('dotnet', ['format', 'scripts/playnite/optiscaler-playnite.csproj', '--verify-no-changes', '--no-restore']);
      git(['diff', '--check']);
      break;
    case 'security': security(); break;
    case 'session-end':
      git(['diff', '--check']);
      for (const doc of ['README.md', 'CHANGELOG.md', 'docs/storage-format.md'])
        if (!existsSync(doc)) throw new Error(`Missing documentation: ${doc}`);
      console.log('Session checks passed. Report local checks and pending real Windows acceptance separately.');
      break;
    default: throw new Error('Unknown workflow.');
  }
} catch (error) {
  console.error(error.message);
  process.exitCode = 1;
}
