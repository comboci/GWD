'use strict';
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const assert = require('node:assert/strict');
const { execFileSync } = require('node:child_process');
const root = path.resolve(__dirname, '..');
function checkedPath(file) {
    assert(typeof file === 'string' && /^(?:[A-Za-z0-9_. -]+\/)*[A-Za-z0-9_. -]+$/.test(file) && file.split('/').every(x => x !== '.' && x !== '..'), 'Invalid contract path: ' + file);
    const target = path.resolve(root, file);
    const physical = fs.realpathSync(target);
    assert(physical.startsWith(fs.realpathSync(root) + path.sep), 'Contract path leaves checkout: ' + file);
    return target;
}
const read = file => fs.readFileSync(checkedPath(file), 'utf8').replace(/\r\n/g, '\n');
const app = file => read('src/DeploymentManager/' + file);
function unique(text, anchor, context = '') {
    assert(typeof anchor === 'string' && anchor.length > 0, 'Invalid empty source anchor: ' + context);
    const start = text.indexOf(anchor);
    assert(start >= 0 && text.indexOf(anchor, start + 1) < 0, 'Missing or ambiguous source anchor ' + JSON.stringify(anchor) + ' in ' + context);
    return start;
}
function section(text, startAnchor, endAnchor, context = 'source section') {
    const start = unique(text, startAnchor, context);
    const end = unique(text, endAnchor, context);
    assert(end >= start + startAnchor.length, 'Reversed source anchors in ' + context);
    return text.slice(start, end);
}
function tail(text, startAnchor, context) {
    return text.slice(unique(text, startAnchor, context));
}
function ordered(text, ...anchors) {
    let offset = 0;
    for (const anchor of anchors) {
        if (typeof anchor !== 'string' || anchor.length === 0) return false;
        const i = text.indexOf(anchor, offset);
        if (i < 0) return false;
        offset = i + anchor.length;
    }
    return true;
}
function blob(bytes) {
    return crypto.createHash('sha1').update(Buffer.from('blob ' + bytes.length + '\0')).update(bytes).digest('hex');
}
function assertBlob(file, expected) {
    assert(/^[0-9a-f]{40}$/.test(expected), 'Exact Git blob required for ' + file);
    const data = fs.readFileSync(checkedPath(file));
    const lf = data.toString('utf8').replace(/\r\n/g, '\n');
    const candidates = [data, Buffer.from(lf), Buffer.from(lf.replace(/\n/g,'\r\n'))];
    assert(candidates.some(bytes => blob(bytes) === expected), 'Working tree blob differs: ' + file);
    const git = execFileSync('git', ['rev-parse', 'HEAD:' + file], {cwd: root, encoding:'utf8'}).trim();
    assert.equal(git, expected, 'Committed blob differs: ' + file);
}
function verifyPreservation() {
    const current = JSON.parse(read('tests/v290-r-source-contract.json'));
    assert.equal(current.baseline.commit, '55dfad1ef8e3cdabd31be71d6903bcbad20b64d7');
    assert.equal(current.baseline.release, 'V289-R');
    const evidence = ['tests/v70-preservation.json','tests/v71-preservation.json','tests/v269-r-preservation.json',
        'tests/run-v269-r-review.ps1','audit/regression/V269-R.md','audit/regression/final-architecture-07-checkpoint.json'];
    assert.deepEqual(Object.keys(current.historicalEvidenceBlobs).sort(), evidence.slice().sort(), 'Historical evidence list changed');
    for (const file of evidence) assertBlob(file,current.historicalEvidenceBlobs[file]);
    const historic = JSON.parse(read('tests/v269-r-preservation.json'));
    assert.equal(historic.commit, '5f709d4129304a5cb902205e0b3cf8f53db097a7');
    assert.equal(historic.files.length, 27);
    const saved = new Map(current.protectedFiles.map(x => [x.file,x]));
    assert.equal(saved.size, current.protectedFiles.length, 'Duplicate protected paths');
    assert.equal(saved.size, historic.files.length, 'Protected file coverage changed');
    for (const item of historic.files) {
        const record = saved.get(item.file);
        assert(record, 'Protected file omitted: ' + item.file);
        assert.equal(record.historicalBlobSha,item.blobSha,'Historical hash metadata changed: '+item.file);
        if (record.status === 'UNCHANGED') {
            assert.equal(record.currentBlobSha,item.blobSha,'Unchanged designation lacks baseline identity');
            assert.equal(record.v289BlobSha,item.blobSha,'Unchanged designation lacks V289 identity');
        } else {
            assert.equal(record.status,'INTENTIONAL_CHANGE');
            assert(typeof record.reason === 'string' && record.reason.trim().length > 0,'Intentional hash change must have an explanation');
            assert.notEqual(record.currentBlobSha,item.blobSha,'Unexpected baseline hash for intentional change');
        }
        assertBlob(record.file, record.currentBlobSha);
    }
    return current;
}
module.exports = { read, app, unique, section, tail, ordered, verifyPreservation };
