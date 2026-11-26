const fs = require('fs'), path = require('path'), assert = require('assert');
const root = path.resolve(__dirname, '..');
const wizard = fs.readFileSync(path.join(root, 'src/DeploymentManager/WizardForm.cs'), 'utf8');
const diag = fs.readFileSync(path.join(root, 'src/DeploymentManager/DiagnosticsCenterForm.cs'), 'utf8');
const block = (source, a, b) => {
    const i = source.indexOf(a), j = source.indexOf(b, i + a.length);
    assert(i >= 0 && j > i, 'missing source anchor: ' + a);
    return source.slice(i, j);
};
const nav = block(wizard, 'private void UpdateNavigationState()', 'private static string NormalizeDependencyMode');
assert(!/DetectApiProject\(|ValidatePreparedPublish\(|Directory\.(Exists|EnumerateFiles)/.test(nav), 'navigation must not scan disk');
const probe = block(wizard, 'private async Task ProbeNavigationSourceAsync', 'private void UpdateNavigationState');
assert(probe.includes('Task.Run(') && probe.includes('request.Token') && probe.includes('NavigationSourceKey() == key') && probe.includes('ReferenceEquals(_navigationProbeCts, request)'), 'background probe and stale-result guard');
assert(block(wizard, 'private async Task NextAsync()', 'private void BuildWelcomePage()').includes('ValidatePreparedPublish('), 'authoritative Next guard retained');
assert(block(wizard, 'private void ValidateTargetPage()', 'private async Task RunBackgroundAutoVerificationAsync').includes('DetectApiProject('), 'authoritative target validation retained');
assert(block(wizard, 'private void BuildReviewPageRefresh()', 'private async Task BusyAsync').includes('DisposeContentControls();'), 'Review uses disposal path preserving persistent log');
assert(diag.includes('MaxDiagnosticPreviewChars = 500_000') && diag.includes('FileShare.ReadWrite | FileShare.Delete'), 'bounded shared diagnostic preview');
assert(diag.includes('Task.Run(() => ReadDiagnosticPreview') && diag.includes('request != previewGeneration') && diag.includes('request != listGeneration'), 'async previews discard obsolete results');
console.log('PASS: UI performance source contracts (not a runtime or build test)');
