const fs = require('node:fs'), path = require('node:path'), assert = require('node:assert/strict');
const read = p => fs.readFileSync(path.join(__dirname,'..','src/DeploymentManager',p),'utf8');
const w = read('WizardForm.cs'), log = read('LogForm.cs'), reader = read('Services/LogPreviewReader.cs');
const service = read('Services/DeploymentHistoryService.cs'), history = read('DeploymentHistoryForm.cs');
const diag = read('DiagnosticsCenterForm.cs'), bundle = read('Services/DiagnosticBundleService.cs');
const metrics = read('Services/RuntimeMetricsService.cs'), disk = read('Services/LogService.cs');
const { section } = require('./source-contract-r03.cjs');
const checks = [
 ['bounded preview reader', reader.includes('MaxBatchBytes = 64 * 1024')&&reader.includes('Math.Min(MaxBatchBytes, length - start)')],
 ['UTF-8 boundary and offset',reader.includes('CompleteUtf8Prefix(buffer, first, count)')&&reader.includes('start + complete')],
 ['active writer sharing retained',reader.includes('FileShare.ReadWrite | FileShare.Delete')],
 ['incremental asynchronous viewer',log.includes('Task.Run(() => LogPreviewReader.ReadBatch')&&log.includes('_offset = batch.NextOffset')&&!log.includes('ReadToEnd()')],
 ['viewer single flight and stale guard',log.includes('_reading || _paused || _closing')&&log.includes('generation != _generation')],
 ['explicit pause and tail reset',log.includes('Pause live view')&&log.includes('ResetTail()')],
 ['batched styled rendering',log.includes('if (next != style) { Flush(); style = next; }')&&log.includes('_box.AppendText(run.ToString())')],
 ['viewer text bound',log.includes('MaxUiChars = 500_000')&&log.includes('_box.TextLength > MaxUiChars')],
 ['complete snapshot atomic promotion',reader.includes('var remaining = input.Length')&&reader.includes('File.Move(temporary, destination, overwrite: true)')&&reader.includes('ct.ThrowIfCancellationRequested()')],
 ['disk evidence before UI clipping',w.includes('_logService.Write(message, (at, clean)')&&w.includes('clean.Length > MaxUiLineChars')&&disk.includes('_sessionWriter.WriteLine(line)')],
 ['UI line and batch budgets',w.includes('MaxPendingUiLogLines = 2_048')&&w.includes('MaxUiLineChars = 4_096')&&w.includes('MaxUiBatchChars = 64_000')],
 ['UI history and filter bounded',w.includes('MaxUiHistoryChars = 1_000_000')&&w.includes('_uiLogChars -=')&&w.includes('chars + line.Length > 300_000')],
 ['stream reduction with stable tie',service.includes('new Dictionary<string, DeploymentHistoryRecord>(StringComparer.OrdinalIgnoreCase)')&&service.includes('record.EventAtUtc > existing.EventAtUtc')],
 ['malformed row isolation retained',service.includes('catch (JsonException ex)')&&service.includes('ReportedDamagedRows.TryAdd')],
 ['lightweight project catalog',service.includes('GetProjectCatalog(CancellationToken ct = default)')&&diag.includes('_history.GetProjectCatalog(request.Token)')&&history.includes('_history.GetProjectCatalog(request.Token)')],
 ['history cancellation and stale guard',service.includes('GetHistoryPage(')&&history.includes('_history.GetHistoryPage(id, pageIndex, HistoryPageSize, request.Token)')&&diag.includes('historyRequest?.Cancel()')&&history.includes('request.IsCancellationRequested || generation != _generation')],
 ['both history grids virtual',diag.includes('VirtualMode = true')&&diag.includes('grid.CellValueNeeded')&&history.includes('VirtualMode = true')&&history.includes('_grid.CellValueNeeded')&&!history.includes('_grid.Rows.Add(')],
 ['full stage expansion and log link',history.includes('_visibleRows.Add((attempt, stage))')&&history.includes('_visibleRows[index].Attempt')&&history.includes('new LogForm(row.SessionLog)')],
 ['metrics worker and single flight',w.includes('Task.Run(_runtimeMetrics.Sample)')&&w.includes('_runtimeMetricsPending is not null')],
 ['monotonic CPU elapsed interval',metrics.includes('Stopwatch.GetElapsedTime(_lastTimestamp, timestamp)')],
 ['metrics history bound and recent projection',metrics.includes('MaxSamples = 720')&&diag.includes('GetRecentHistory(240)')],
 ['metrics disposal waits off UI',w.includes('pending.ContinueWith(_ => _runtimeMetrics.Dispose(), TaskScheduler.Default)')&&metrics.includes('ObjectDisposedException.ThrowIf(_disposed, this)')],
 ['streamed history ZIP and rejected evidence',section(bundle,'private static void AddHistoryFile','private static void AddText').includes('valid.WriteLine(serialized)')&&bundle.includes('FileOptions.DeleteOnClose')&&bundle.includes('name + ".rejected.txt"')],
 ['history export retains structured redaction',bundle.includes('DeploymentHistoryService.SerializeRedactedRecord(record)')&&bundle.includes('SecretRedactor.Redact(line)')],
 ['arbitrary text whole-file redaction retained',bundle.includes('var contents = reader.ReadToEnd()')&&bundle.includes('AddText(archive, name, contents);')],
 ['diagnostic ZIP promotion remains atomic',bundle.includes('File.Move(temporaryFile, destinationFile, overwrite: true)')]
];
for(const [name,ok] of checks){assert(ok,name);console.log('PASS: '+name);}
console.log(checks.length+' phase 04 source guards passed. Not a .NET build, runtime or benchmark.');
