const assert = require('node:assert/strict'), fs = require('node:fs'), path = require('node:path');
const root = path.resolve(__dirname, '..');
const read = p => fs.readFileSync(path.join(root,p),'utf8');
const env = read('src/DeploymentManager/Services/EnvironmentService.cs');
const sql = read('src/DeploymentManager/Services/SqlServerService.cs');
const wizard = [
    'src/DeploymentManager/WizardForm.cs',
    'src/DeploymentManager/WizardForm.SourceAndTarget.cs',
    'src/DeploymentManager/WizardForm.InstallersAndSql.cs',
    'src/DeploymentManager/WizardForm.ReviewAndLocation.cs'
].map(read).join('\n');
const parser = read('src/DeploymentManager/Services/IisInventoryParser.cs');
const section = (s,a,b) => { const i=s.indexOf(a), j=s.indexOf(b,i+a.length); assert(i>=0&&j>i,a); return s.slice(i,j); };
const checks = [
 ['three bulk site queries', ['list site /xml','list app /xml','list vdir /xml'].every(x=>env.includes(x))],
 ['bulk pool query', env.includes('list apppool /xml')],
 ['legacy fallbacks and cancellation', env.includes('return await ListIisSitesLegacyAsync(ct)')&&env.includes('return await ListIisAppPoolsLegacyAsync(ct)')&&env.includes('catch (OperationCanceledException) { throw; }')],
 ['bounded capture rejects truncation',env.includes('AppCmd inventory exceeded the bounded capture.')],
 ['exact root join and DTD prohibition', parser.includes('var rootName = name + "/"')&&parser.includes('StringComparer.OrdinalIgnoreCase')&&parser.includes('DtdProcessing.Prohibit')&&parser.includes('!index.TryAdd')],
 ['IIS destructive safety blocks preserved', env.split('IIS_DESTRUCTIVE_CLEANUP_SAFETY_BLOCK').length===3],
 ['SQL discovery truly off-thread', section(sql,'public Task<List<SqlInstanceInfo>> DetectInstancesAsync','private static SqlInstanceInfo ReadInstanceMetadata').includes('Task.Run(')],
 ['SQL discovery has no hidden authentication', !/Open(?:Connection)?Async/.test(section(sql,'public Task<List<SqlInstanceInfo>> DetectInstancesAsync','private static SqlInstanceInfo ReadInstanceMetadata'))],
 ['SQL fingerprint guard', sql.includes('SQL configuration changed during diagnostics.')&&wizard.includes('fingerprint == _sql.Connections.Fingerprint(_profile)')],
 ['IIS inventory publication guarded by latest request', wizard.includes('ReferenceEquals(inventoryRequest, request)')&&wizard.includes('grid.Disposed += (_, _) => inventoryRequest?.Cancel()')&&wizard.includes('var inventory = await Task.Run(')],
 ['hosting detection off-thread', wizard.split('await _environment.CheckHostingBundleAsync(').length===4],
 ['archive worker and promotion',wizard.includes('await Task.Run(() => ExportCleanSourceZip(sourceRoot, zipPath, excluded))')&&wizard.includes('File.Move(temporary, destination, overwrite: true)')&&!section(wizard,'private static void ExportCleanSourceZip','private sealed record DeploymentProfileSnapshot').includes('File.Delete(destination)')],
 ['source export link exclusions retained',section(wizard,'private static void ExportCleanSourceZip','private sealed record DeploymentProfileSnapshot').split('FileAttributes.ReparsePoint').length===3],
 ['CLI, smoke and quality test targets match referenced projects', ['src/DeploymentManager.Cli/DeploymentManager.Cli.csproj','src/DeploymentManager.SmokeTests/DeploymentManager.SmokeTests.csproj','tests/CodeQuality.WindowsTests/CodeQuality.WindowsTests.csproj'].every(p=>read(p).includes('<TargetFramework>net10.0-windows</TargetFramework>'))&&read('tests/CodeQuality.Tests/CodeQuality.Tests.csproj').includes('<TargetFramework>net10.0</TargetFramework>')]
];
for(const [name,ok] of checks){assert(ok,name);console.log('PASS: '+name);}
console.log(checks.length+' phase 02 static source guards passed. Build and Windows runtime are separate.');
