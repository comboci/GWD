const fs = require('fs'), path = require('path'), crypto = require('crypto');
const root = path.resolve(__dirname, '..');
const read = file => fs.readFileSync(path.join(root, file), 'utf8').replace(/\r\n/g, '\n');
const hash = text => crypto.createHash('sha256').update(text).digest('hex');
function member(text, name) {
    const start = new RegExp('^    (?:public|private|internal|protected) [^\\n]*\\b' + name + '\\(', 'm').exec(text);
    if (!start) throw Error('Missing member: ' + name);
    const tail = text.slice(start.index + start[0].length);
    const next = /^    (?:public|private|internal|protected) /m.exec(tail);
    return text.slice(start.index, next ? start.index + start[0].length + next.index : text.length).trim();
}
function canonical(text) {
    return text.replace(/(?:GenericWeb\.DeploymentManager\.Services\.)?SecretRedactor\.Redact\((ex\.Message|sb\.ToString\(\))\)/g, '$1')
        .replace('=== V71 POST-DEPLOY', '=== V70 POST-DEPLOY');
}
let count = 0;
function check(ok, name) { if (!ok) throw Error(name); console.log('PASS: ' + name); count++; }
const wizard = [
    'src/DeploymentManager/WizardForm.cs',
    'src/DeploymentManager/WizardForm.SourceAndTarget.cs',
    'src/DeploymentManager/WizardForm.InstallersAndSql.cs',
    'src/DeploymentManager/WizardForm.ReviewAndLocation.cs'
].map(read).join('\n');
const sql = read('src/DeploymentManager/Services/SqlServerService.cs');
const strategy = read('src/DeploymentManager/Services/SqlConnectionStrategy.cs');
const deploy = read('src/DeploymentManager/Services/DeploymentService.cs');
const sync = read('src/DeploymentManager/Services/WorkspaceSynchronizer.cs');
check(sync.includes('Hash(entry)') && sync.includes('hash == destinationHash') && !sync.includes('FileInfo'), 'Workspace Unchanged compares actual SHA256, not metadata/manifest');
check(sync.indexOf('var verified = Verify(') >= 0 && sync.indexOf('var verified = Verify(') < sync.indexOf('File.Move(manifestPath') && sync.includes('Build/Publish blocked.'), 'Mandatory verification before manifest promotion, fail closed');
check(sync.includes('"IIS-Publish"') && !/EndsWith\([^\n]*\.cs/.test(sync), 'Explicit directory exclusions, no source extension filter');
check(deploy.indexOf('PrepareStableWorkspace(profile, ct)') >= 0 && deploy.indexOf('PrepareStableWorkspace(profile, ct)') < deploy.indexOf('Staging cleanup / create') && member(deploy, 'PrepareStableWorkspace').includes('WorkspaceSynchronizer.Sync'), 'Production source pipeline sync+verify precedes staging/build/publish');
check(member(deploy, 'PublishAsync').includes('using var workspaceLease') && member(deploy, 'AcquireWorkspaceLease').includes('FileShare.None'), 'Workspace lease spans source build pipeline');
// V70/V71 manifests remain historical evidence; current release guards pin the
// last pre-UI-refactor V268 core files rather than asserting obsolete hashes.
const preservation = JSON.parse(read('tests/v269-r-preservation.json'));
function gitBlobSha(bytes) {
    return crypto.createHash('sha1').update(Buffer.from('blob '+bytes.length+'\0')).update(bytes).digest('hex');
}
for (const item of preservation.files) {
    const bytes = fs.readFileSync(path.join(root,item.file));
    const lf = bytes.toString('utf8').replace(/\r\n/g,'\n');
    const variants = [bytes, Buffer.from(lf,'utf8'), Buffer.from(lf.replace(/\n/g,'\r\n'),'utf8')];
    check(variants.some(value => gitBlobSha(value) === item.blobSha), 'V268 preserved core: '+item.file);
}
check(deploy.includes('CollectStartupDiagnosticsAsync') && wizard.includes('CollectStartupDiagnosticsAsync('), 'V84: startup diagnostics remain wired after post-V70 changes');
check(wizard.includes('VerifyRunningApiReleaseAsync(_profile, publish, ct)') && wizard.includes('VerifyRunningFrontendReleaseAsync(_profile, publish, ct)'), 'V84: changed WizardForm still invokes both live release checks');
const source = fs.readdirSync(path.join(root, 'src/DeploymentManager/Services')).filter(f => f.endsWith('.cs')).map(f => [f, read('src/DeploymentManager/Services/' + f)]);
check(source.filter(([f,s]) => /new SqlConnection\(/.test(s)).map(([f]) => f).join() === 'SqlConnectionStrategy.cs', 'Single SQL connection construction boundary');
check(source.filter(([f,s]) => /new SqlConnectionStringBuilder/.test(s)).map(([f]) => f).join() === 'SqlConnectionStrategy.cs', 'Single connection-string builder boundary');
check(!/Open(?:Connection)?Async/.test(member(sql, 'DetectInstancesAsync')) && !/Open(?:Connection)?Async/.test(member(sql, 'ReadInstanceMetadata')), 'Discovery uses registry metadata without hidden Windows authentication');
for (const name of ['PrepareDatabaseAsync', 'DiagnoseAsync', 'TestConnectionWithSafeFallbackAsync'])
    check(member(sql, name).includes('Connections.OpenAsync(profile') && !member(sql, name).includes('OpenConnectionAsync('), name + ' uses profile strategy');
check(wizard.includes('private bool _sqlAdminConnectionVerified => _sql.Connections.State(_profile).Verified;'), 'Wizard verification derives from strategy, not a second mutable flag');
check(!/if \(passwordWasSuggested\).*GenerateStrongPassword/.test(wizard) && wizard.includes('_profile.SqlCredentialConfigured = false;'), 'Generated suggestions do not silently become fallback credentials');
check(strategy.includes('&& configured &&') && strategy.includes('IsIntegratedAuthenticationFailure(ex)'), 'Fallback requires configured credential and Integrated-auth failure');
check(strategy.includes('SemaphoreSlim') && strategy.includes('Fingerprint(profile) != state.Fingerprint'), 'Concurrent/stale connection guard remains present');
check(wizard.indexOf('CollectStartupDiagnosticsAsync(_profile, publish, health') >= 0 && wizard.indexOf('CollectStartupDiagnosticsAsync(_profile, publish, health') < wizard.indexOf('Post-deploy health failure —'), 'Startup diagnostics remain before health-failure throw and rollback');
check(wizard.includes('_deployment.WriteConnectionString(publish, _profile);'), 'Effective connection refreshed after database preparation');
check(deploy.includes('"appsettings.json", "appsettings.Production.json"'), 'Base and Production application connections aligned');
const declaredVersion = /<DeploymentManagerVersion>([^<]+)<\/DeploymentManagerVersion>/.exec(read('Directory.Build.props'))?.[1];
check(Boolean(declaredVersion) && JSON.parse(read('deployment-manager.version.json')).version === declaredVersion, 'Release metadata matches Directory.Build.props');
for (const project of ['DeploymentManager', 'DeploymentManager.Cli', 'DeploymentManager.SmokeTests'])
    check(read(`src/${project}/${project}.csproj`).includes('<TargetFramework>net10.0-windows</TargetFramework>'), project + ' targets net10.0-windows');
// Source ZIP, IIS view and other intentionally updated UI behavior are checked
// by verify-ui-performance*.cjs, not by obsolete full-method hashes.
check(preservation.commit === '5f709d4129304a5cb902205e0b3cf8f53db097a7' && preservation.files.length === 27, 'V269-R baseline identity');
console.log(`STATIC TESTS: ${count} passed. Source checks do not establish live IIS/SQL behavior.`);
module.exports = {member, canonical};
