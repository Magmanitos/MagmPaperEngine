// Surface build failures in public CI annotations as well as the full job log.
const { spawnSync } = require('node:child_process');
const result = spawnSync(process.execPath, [require.resolve('electron-builder/cli.js'), '--win', ...process.argv.slice(2), '--x64', '--publish', 'never'], {
  encoding: 'utf8', maxBuffer: 20 * 1024 * 1024
});
process.stdout.write(result.stdout || '');
process.stderr.write(result.stderr || '');
if (result.error || result.status !== 0) {
  const message = [result.error?.stack, result.stdout, result.stderr].filter(Boolean).join('\n').slice(-20000);
  if (process.env.GITHUB_ACTIONS) console.log(`::error::${message.replace(/%/g, '%25').replace(/\r/g, '%0D').replace(/\n/g, '%0A')}`);
  process.exit(result.status || 1);
}
