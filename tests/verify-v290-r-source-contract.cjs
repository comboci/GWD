// Owner only; authored but not executed under the source-only policy.
'use strict';
const assert = require('node:assert/strict');
const { read, unique, section, tail, ordered, verifyPreservation } = require('./source-contract-r03.cjs');
const manifest = verifyPreservation();
const version = JSON.parse(read('deployment-manager.version.json')).version;
assert(typeof version === 'string' && /^\d+\.\d+\.\d+(?:\.\d+)?$/.test(version), 'Invalid release version');
assert.equal(manifest.release, 'V'+version.split('.')[0]+'-R');
unique(read('Directory.Build.props'), '<DeploymentManagerVersion>'+version+'</DeploymentManagerVersion>', 'release property');
assert(Array.isArray(manifest.anchors) && manifest.anchors.length > 0, 'Missing current source anchor manifest');
const executed = new Set();
for (const check of manifest.anchors) {
    assert(typeof check.id === 'string' && check.id.length && !executed.has(check.id), 'Duplicate or malformed anchor identifier');
    executed.add(check.id);
    const contents = read(check.file);
    const region = check.end ? section(contents, check.start, check.end, check.file) :
        check.start ? tail(contents, check.start, check.file) : contents;
    assert(Array.isArray(check.required) && check.required.length > 0, 'Empty positive contract: '+check.id);
    for (const anchor of check.required) {
        assert(typeof anchor === 'string' && anchor.length && region.includes(anchor), check.id+' missing '+String(anchor));
    }
    if (check.ordered && check.ordered.length) assert(ordered(region,...check.ordered), check.id+' order mismatch');
    for (const anchor of check.forbidden || []) assert(typeof anchor === 'string' && anchor.length && !region.includes(anchor), check.id+' forbidden or invalid '+String(anchor));
}
for (const file of manifest.currentVerifiers) assert(read(file).trim().length > 0, 'Empty mapped verifier: '+file);
console.log('Completed '+executed.size+' positive V290-R source anchor groups and integrity checks; Windows runtime NOT_RUN.');
