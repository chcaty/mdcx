using System.Diagnostics;
using System.Text.Json;
using Organizer.Core;
using Organizer.Infrastructure;

internal static class MetadataSessionChecks
{
    public static void Register(List<(string Name, Func<Task> Run)> checks, string sandbox, string python, string? actualWorker)
    {
        var directory = Path.Combine(sandbox, "metadata-sessions");
        Directory.CreateDirectory(directory);
        string FileAt(string name, string content)
        {
            var path = Path.Combine(directory, name + "-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(path, content);
            return path;
        }
        string Script(string body) => FileAt("worker.py", "import sys,json,time,os\n" + body);
        string Config() => FileAt("config.json", "{\"network_enabled\":false}");
        void Test(string name, Func<Task> action) => checks.Add((name, action));
        static void Assert(bool condition, string message = "Assertion failed")
        { if (!condition) throw new InvalidOperationException(message); }
        static async Task ExpectCode(string expected, Func<Task> action)
        {
            try { await action(); throw new InvalidOperationException("Expected " + expected); }
            catch (ScraperException error) { Assert(error.Code == expected, $"Expected {expected}, received {error.Code}"); }
        }
        static async Task ExpectCancellation(Task task)
        {
            try { await task; throw new InvalidOperationException("Expected cancellation"); }
            catch (OperationCanceledException) { }
        }
        static async Task Until(Func<bool> condition)
        {
            var clock = Stopwatch.StartNew();
            while (!condition())
            {
                if (clock.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Synthetic worker did not become ready");
                await Task.Delay(10);
            }
        }
        static MetadataCandidate Candidate(string sourceId = "bf_658") => new("BF-658", "Neutral catalog fixture", 2024, "javbus",
            "https://www.javbus.com/" + sourceId, 0.99,
            new Dictionary<string, string> { ["identitySchema"] = "2", ["fixture"] = "true" })
        { SourceId = sourceId, CatalogIdentifier = new("javbus:catalog", "BF-658"), RequiresReview = true, ReviewReasons = ["catalog_metadata_requires_review"] };
        string CandidateWorker(MetadataCandidate candidate) => Script("c=json.loads(" + JsonSerializer.Serialize(JsonSerializer.Serialize(candidate)) + ")\n" + """
            for line in sys.stdin:
                r=json.loads(line)
                print(json.dumps({'version':1,'requestId':r['requestId'],'type':'candidate','candidate':c}),flush=True)
                print(json.dumps({'version':1,'requestId':r['requestId'],'type':'completed','status':'matched','terminal':True,'count':1,'sourceFailures':0}),flush=True)
            """);
        string SessionWorker(string log, string mode = "normal") => Script("log=" + JsonSerializer.Serialize(log) + "\nmode=" + JsonSerializer.Serialize(mode) + "\n" + """
            assert len(sys.argv)==3 and sys.argv[1]=='--metadata-config'
            with open(sys.argv[2],encoding='utf-8') as config_file: config=json.load(config_file)
            def record(value):
                with open(log,'a',encoding='utf-8') as output: output.write(value+'\n')
            def event(request_id,kind,**kwargs):
                print(json.dumps({'version':1,'requestId':request_id,'type':kind,**kwargs}),flush=True)
            record('start:'+str(os.getpid()))
            count=0
            for line in sys.stdin:
                r=json.loads(line)
                record(r['command']+':'+r.get('query',''))
                if r['command']=='cancel':
                    if mode!='no_ack':
                        event(r['requestId'],'cancelled',terminal=True)
                        event(r['requestId'],'candidate',candidate={})
                        event('unrelated-old-request','candidate',candidate={})
                    continue
                count+=1
                if r['query']=='hold': continue
                if mode=='exit': sys.exit(0)
                if mode=='errors':
                    event(r['requestId'],'error',terminal=False,error={'code':r['query'],'message':'SECRET_PROVIDER_VALUE'})
                    event(r['requestId'],'completed',terminal=True,status='sources_failed',count=0,sourceFailures=1)
                    continue
                sys.stderr.write('diagnostic '*10000);sys.stderr.flush()
                candidate={'id':'BF-658','title':'Sequence '+str(count),'year':2024,'source':'javbus','sourceId':'bf_658',
                    'catalogIdentifier':{'namespace':'javbus:catalog','value':'BF-658'},'sourceUrl':'https://www.javbus.com/bf_658',
                    'confidence':0.99,'provenance':{'identitySchema':'2','fixture':'true','sequence':str(count),'pid':str(os.getpid())},
                    'requiresReview':True,'reviewReasons':['catalog_metadata_requires_review']}
                event(r['requestId'],'candidate',candidate=candidate)
                event(r['requestId'],'completed',terminal=True,status='matched',count=1,sourceFailures=0)
            record('eof')
            """);

        Test("configured bridge is lazy and preserves one worker across repeated queries", async () =>
        {
            var log = FileAt("session-log", "");
            var script = SessionWorker(log);
            var config = Config();
            await using var client = new PythonScraperClient(python, script, config);
            Assert(client.MetadataConfigPath == Path.GetFullPath(config));
            await Task.Delay(40);
            Assert(File.ReadAllText(log) == "", "Construction must not start a process or query");
            var first = (await client.MatchAsync("BF-658", sources: ["javbus"])).Single();
            var second = (await client.MatchAsync("bf_658")).Single();
            Assert(first.Title == "Sequence 1" && second.Title == "Sequence 2");
            Assert(first.Provenance["pid"] == second.Provenance["pid"]);
            Assert(File.ReadAllLines(log).Count(line => line.StartsWith("start:", StringComparison.Ordinal)) == 1);
        });
        Test("configured bridge cancellation is acknowledged and late candidates cannot cross queries", async () =>
        {
            var log = FileAt("cancel-log", "");
            await using var client = new PythonScraperClient(python, SessionWorker(log), Config());
            using var cancel = new CancellationTokenSource();
            var pending = client.MatchAsync("hold", cancel.Token);
            await Until(() => File.ReadAllText(log).Contains("match:hold", StringComparison.Ordinal));
            cancel.Cancel();
            await ExpectCancellation(pending);
            var next = (await client.MatchAsync("BF-658")).Single();
            Assert(next.Title == "Sequence 2" && File.ReadAllText(log).Contains("cancel:", StringComparison.Ordinal));
            Assert(File.ReadAllLines(log).Count(line => line.StartsWith("start:", StringComparison.Ordinal)) == 1);
        });
        Test("configured bridge timeout cancels cooperatively and keeps the same healthy session", async () =>
        {
            var log = FileAt("timeout-log", "");
            await using var client = new PythonScraperClient(python, SessionWorker(log), Config()) { RequestTimeout = TimeSpan.FromMilliseconds(300) };
            await ExpectCode("timeout", () => client.MatchAsync("hold"));
            Assert((await client.MatchAsync("BF-658")).Single().Title == "Sequence 2");
            Assert(File.ReadAllText(log).Contains("cancel:", StringComparison.Ordinal));
        });
        Test("configured bridge missing cancellation acknowledgement faults without restart", async () =>
        {
            var log = FileAt("unacknowledged-log", "");
            await using var client = new PythonScraperClient(python, SessionWorker(log, "no_ack"), Config())
            { CancellationAcknowledgementTimeout = TimeSpan.FromMilliseconds(150) };
            using var cancel = new CancellationTokenSource();
            var pending = client.MatchAsync("hold", cancel.Token);
            await Until(() => File.ReadAllText(log).Contains("match:hold", StringComparison.Ordinal));
            cancel.Cancel();
            await ExpectCancellation(pending);
            await ExpectCode("session_faulted", () => client.MatchAsync("BF-658"));
            Assert(File.ReadAllLines(log).Count(line => line.StartsWith("start:", StringComparison.Ordinal)) == 1);
        });
        Test("configured bridge serializes a bounded queue and drops cancelled queued queries", async () =>
        {
            var log = FileAt("queue-log", "");
            await using var client = new PythonScraperClient(python, SessionWorker(log), Config());
            using var runningCancellation = new CancellationTokenSource();
            using var queuedCancellation = new CancellationTokenSource();
            var active = client.MatchAsync("hold", runningCancellation.Token);
            await Until(() => File.ReadAllText(log).Contains("match:hold", StringComparison.Ordinal));
            var queuedCancelled = client.MatchAsync("cancel-before-send", queuedCancellation.Token);
            var queued = Enumerable.Range(0, 6).Select(_ => client.MatchAsync("BF-658")).ToArray();
            await ExpectCode("busy", () => client.MatchAsync("BF-658"));
            queuedCancellation.Cancel();
            await ExpectCancellation(queuedCancelled);
            Assert(File.ReadAllLines(log).Count(line => line.StartsWith("match:", StringComparison.Ordinal)) == 1);
            runningCancellation.Cancel();
            await ExpectCancellation(active);
            var results = await Task.WhenAll(queued);
            Assert(results.SelectMany(result => result).Select(candidate => candidate.Title).Distinct().Count() == 6);
            Assert(!File.ReadAllText(log).Contains("match:cancel-before-send", StringComparison.Ordinal));
        });
        Test("configured bridge session request budget never silently renews the worker", async () =>
        {
            var log = FileAt("session-budget-log", "");
            var script = Script("log=" + JsonSerializer.Serialize(log) + "\n" + """
                with open(log,'a',encoding='utf-8') as output: output.write('start\n')
                for line in sys.stdin:
                    r=json.loads(line)
                    print(json.dumps({'version':1,'requestId':r['requestId'],'type':'completed','terminal':True,
                        'status':'no_match','count':0,'sourceFailures':0}),flush=True)
                """);
            await using var client = new PythonScraperClient(python, script, Config());
            for (var index = 0; index < 8192; index++) Assert((await client.MatchAsync("BF-658")).Count == 0);
            await ExpectCode("session_full", () => client.MatchAsync("BF-658"));
            await ExpectCode("session_full", () => client.MatchAsync("BF-658"));
            Assert(File.ReadAllLines(log).Length == 1);
        });
        Test("configured bridge preserves source failure codes and never exposes provider diagnostics", async () =>
        {
            var log = FileAt("errors-log", "");
            await using var client = new PythonScraperClient(python, SessionWorker(log, "errors"), Config());
            foreach (var code in new[] { "network", "tls", "rate_limited", "auth_required", "challenge", "policy_blocked", "parse", "schema_drift",
                "validation", "identity_mismatch", "ambiguous_identity", "response_too_large", "http_error", "invalid_config", "live_unavailable",
                "unsupported_query", "dependency_unavailable", "timeout" })
            {
                try { await client.MatchAsync(code); throw new InvalidOperationException("Expected " + code); }
                catch (ScraperException error)
                {
                    Assert(error.Code == code, error.Code);
                    Assert(!error.Message.Contains("SECRET_PROVIDER_VALUE", StringComparison.Ordinal));
                }
            }
            await ExpectCode("worker_error", () => client.MatchAsync("unknown_private_error"));
            Assert(File.ReadAllLines(log).Count(line => line.StartsWith("start:", StringComparison.Ordinal)) == 1);
        });
        Test("configured bridge rejects mixed failure certainty and remains reusable", async () =>
        {
            var script = Script("""
                for line in sys.stdin:
                    r=json.loads(line)
                    for code in ['network','schema_drift']:
                        print(json.dumps({'version':1,'requestId':r['requestId'],'type':'error','terminal':False,'error':{'code':code}}),flush=True)
                    print(json.dumps({'version':1,'requestId':r['requestId'],'type':'completed','terminal':True,'status':'sources_failed','count':0,'sourceFailures':2}),flush=True)
                """);
            await using var client = new PythonScraperClient(python, script, Config());
            await ExpectCode("sources_failed", () => client.MatchAsync("BF-658"));
            await ExpectCode("sources_failed", () => client.MatchAsync("BF-658"));
        });
        Test("configured bridge rejects contradictory or missing completion evidence", async () =>
        {
            foreach (var completed in new[]
            {
                "{}",
                "{'terminal':False,'status':'no_match','count':0,'sourceFailures':0}",
                "{'terminal':True,'status':'no_match','count':0}",
                "{'terminal':True,'status':'matched','count':1,'sourceFailures':0}",
                "{'terminal':True,'status':'matched','count':0,'sourceFailures':0}",
                "{'terminal':True,'status':'sources_failed','count':0,'sourceFailures':0}",
                "{'terminal':True,'status':'no_match','count':0,'sourceFailures':1}",
                "{'terminal':True,'status':'unknown','count':0,'sourceFailures':0}",
                "{'terminal':True,'status':'no_match','count':-1,'sourceFailures':0}",
                "{'terminal':True,'status':'no_match','count':'0','sourceFailures':0}"
            })
            {
                var script = Script("r=json.loads(sys.stdin.readline())\npayload=" + completed + "\nprint(json.dumps({'version':1,'requestId':r['requestId'],'type':'completed',**payload}),flush=True)\ntime.sleep(30)\n");
                await using var client = new PythonScraperClient(python, script, Config());
                await ExpectCode("protocol_invalid", () => client.MatchAsync("BF-658"));
            }
            foreach (var status in new[] { "no_match", "sources_failed" })
            {
                var script = Script("r=json.loads(sys.stdin.readline())\nprint(json.dumps({'version':1,'requestId':r['requestId'],'type':'error','terminal':False,'error':{'code':'network'}}),flush=True)\nprint(json.dumps({'version':1,'requestId':r['requestId'],'type':'completed','terminal':True,'status':'" + status + "','count':0,'sourceFailures':0}),flush=True)\ntime.sleep(30)\n");
                await using var client = new PythonScraperClient(python, script, Config());
                await ExpectCode("protocol_invalid", () => client.MatchAsync("BF-658"));
            }
        });
        Test("configured bridge validates exact JavBus route catalog namespace and mandatory review", async () =>
        {
            var valid = Candidate();
            await using (var client = new PythonScraperClient(python, CandidateWorker(valid), Config()))
                Assert((await client.MatchAsync("ＢＦ－６５８")).Single().SourceId == "bf_658");
            foreach (var invalid in new[]
            {
                valid with { SourceId = "BF-658" },
                valid with { CatalogIdentifier = new("xcity:hinban", "BF-658") },
                valid with { CatalogIdentifier = new("javbus:catalog", "BF-659") },
                valid with { CatalogIdentifier = new("javbus:catalog", "bf-658") },
                valid with { Id = "BF-659" },
                valid with { RequiresReview = false },
                valid with { ReviewReasons = [] },
                valid with { SourceUrl = valid.SourceUrl + "?token=secret" },
                valid with { SourceUrl = valid.SourceUrl + "#fragment" },
                valid with { SourceUrl = "https://www.javbus.com:443/bf_658" },
                valid with { SourceUrl = "https://example.invalid/bf_658" },
                valid with { SourceId = "bf/658", SourceUrl = "https://www.javbus.com/bf/658" },
                valid with { SourceId = "bf_0658", SourceUrl = "https://www.javbus.com/bf_0658" },
                valid with { SourceId = "bf_658B", SourceUrl = "https://www.javbus.com/bf_658B" }
            })
            {
                await using var client = new PythonScraperClient(python, CandidateWorker(invalid), Config());
                await ExpectCode("invalid_identity", () => client.MatchAsync("BF-658"));
                await ExpectCode("session_faulted", () => client.MatchAsync("BF-658"));
            }
            await using var mismatch = new PythonScraperClient(python, CandidateWorker(valid), Config());
            await ExpectCode("invalid_identity", () => mismatch.MatchAsync("BF-659"));
        });
        Test("configured bridge bounds stdout events stale output and diagnostics", async () =>
        {
            foreach (var (body, expected) in new[]
            {
                ("print('bad json',flush=True)", "protocol_invalid"),
                ("print('x'*70000,flush=True)", "protocol_limit"),
                ("print(json.dumps({'version':2,'requestId':r['requestId'],'type':'health'}),flush=True)", "protocol_version"),
                ("print(json.dumps({'version':1,'requestId':r['requestId'],'type':'unrecognized'}),flush=True)", "protocol_invalid"),
                ("\nfor i in range(513): print(json.dumps({'version':1,'requestId':r['requestId'],'type':'health'}),flush=True)", "protocol_limit"),
                ("\nfor i in range(1025): print(json.dumps({'version':1,'requestId':'old','type':'candidate','candidate':{}}),flush=True)", "protocol_limit"),
                ("sys.stderr.write('x'*(8*1024*1024+2048));sys.stderr.flush()", "protocol_limit")
            })
            {
                var script = Script("r=json.loads(sys.stdin.readline())\n" + body + "\ntime.sleep(30)\n");
                await using var client = new PythonScraperClient(python, script, Config());
                await ExpectCode(expected, () => client.MatchAsync("BF-658"));
                await ExpectCode("session_faulted", () => client.MatchAsync("BF-658"));
            }
            var candidateJson = JsonSerializer.Serialize(JsonSerializer.Serialize(Candidate()));
            var candidates = Script("r=json.loads(sys.stdin.readline())\nc=json.loads(" + candidateJson + ")\nfor i in range(65): print(json.dumps({'version':1,'requestId':r['requestId'],'type':'candidate','candidate':c}),flush=True)\ntime.sleep(30)\n");
            await using var excessive = new PythonScraperClient(python, candidates, Config());
            await ExpectCode("protocol_limit", () => excessive.MatchAsync("BF-658"));
        });
        Test("configured bridge keeps a validated terminal result when the worker exits afterward", async () =>
        {
            var script = Script("r=json.loads(sys.stdin.readline())\nprint(json.dumps({'version':1,'requestId':r['requestId'],'type':'completed','terminal':True,'status':'no_match','count':0,'sourceFailures':0}),flush=True)\n");
            await using var client = new PythonScraperClient(python, script, Config());
            Assert((await client.MatchAsync("BF-658")).Count == 0);
            await Task.Delay(50);
            await ExpectCode("session_faulted", () => client.MatchAsync("BF-658"));
        });
        Test("configured bridge unexpected exit and startup errors never trigger a hidden restart", async () =>
        {
            var log = FileAt("exit-log", "");
            await using (var client = new PythonScraperClient(python, SessionWorker(log, "exit"), Config()))
            {
                await ExpectCode("worker_exit", () => client.MatchAsync("BF-658"));
                await ExpectCode("session_faulted", () => client.MatchAsync("BF-658"));
            }
            Assert(File.ReadAllLines(log).Count(line => line.StartsWith("start:", StringComparison.Ordinal)) == 1);
            foreach (var code in new[] { "invalid_config", "live_unavailable", "dependency_unavailable" })
            {
                var script = Script("print(json.dumps({'version':1,'requestId':'','type':'startup_error','terminal':True,'error':{'code':'" + code + "','message':'PRIVATE_STARTUP_DETAIL'}}),flush=True)\n");
                await using var client = new PythonScraperClient(python, script, Config());
                await ExpectCode(code, () => client.MatchAsync("BF-658"));
                await ExpectCode("session_faulted", () => client.MatchAsync("BF-658"));
            }
        });
        Test("configured bridge disposal cancels active and queued work with bounded shutdown", async () =>
        {
            var log = FileAt("dispose-log", "");
            var client = new PythonScraperClient(python, SessionWorker(log), Config());
            var active = client.MatchAsync("hold");
            await Until(() => File.ReadAllText(log).Contains("match:hold", StringComparison.Ordinal));
            var queued = client.MatchAsync("BF-658");
            var clock = Stopwatch.StartNew();
            await client.DisposeAsync();
            await client.DisposeAsync();
            Assert(clock.Elapsed < TimeSpan.FromSeconds(3));
            foreach (var task in new[] { active, queued })
            {
                try { await task; throw new InvalidOperationException("Expected disposal"); }
                catch (ObjectDisposedException) { }
            }
            try { await client.MatchAsync("BF-658"); throw new InvalidOperationException("Expected disposed client"); }
            catch (ObjectDisposedException) { }
            Assert(!File.ReadAllText(log).Contains("match:BF-658", StringComparison.Ordinal));
        });
        Test("bridge admission and disposal races leave no untracked worker in either mode", async () =>
        {
            for (var iteration = 0; iteration < 24; iteration++)
            {
                var log = FileAt("dispose-race-log", "");
                var script = Script("log=" + JsonSerializer.Serialize(log) + "\n" + """
                    with open(log,'a',encoding='utf-8') as output: output.write(str(os.getpid())+'\n')
                    for line in sys.stdin:
                        r=json.loads(line)
                        print(json.dumps({'version':1,'requestId':r['requestId'],'type':'completed','terminal':True,
                            'status':'no_match','count':0,'sourceFailures':0}),flush=True)
                    """);
                var client = new PythonScraperClient(python, script, iteration % 2 == 0 ? Config() : null);
                using var start = new ManualResetEventSlim();
                var query = Task.Run(async () =>
                {
                    start.Wait();
                    try { await client.MatchAsync("BF-658"); }
                    catch (ObjectDisposedException) { }
                    catch (OperationCanceledException) { }
                });
                var disposal = Task.Run(async () => { start.Wait(); await client.DisposeAsync(); });
                start.Set();
                await Task.WhenAll(query, disposal).WaitAsync(TimeSpan.FromSeconds(8));
                foreach (var line in File.ReadAllLines(log))
                {
                    try
                    {
                        using var process = Process.GetProcessById(int.Parse(line));
                        Assert(process.HasExited, "Disposal left a worker running");
                    }
                    catch (ArgumentException) { /* Exited processes may already have been reaped. */ }
                }
                try { await client.MatchAsync("BF-658"); throw new InvalidOperationException("Expected disposed client"); }
                catch (ObjectDisposedException) { }
            }
        });
        Test("configured bridge explicit config switch retires old worker without an automatic query", async () =>
        {
            var firstLog = FileAt("first-config-log", "");
            var secondLog = FileAt("second-config-log", "");
            var first = new PythonScraperClient(python, SessionWorker(firstLog), Config());
            await first.MatchAsync("BF-658");
            await first.DisposeAsync();
            await using var second = new PythonScraperClient(python, SessionWorker(secondLog), Config());
            await Task.Delay(40);
            Assert(File.ReadAllText(secondLog) == "");
            Assert((await second.MatchAsync("BF-658")).Single().Title == "Sequence 1");
            Assert(File.ReadAllLines(firstLog).Count(line => line.StartsWith("match:", StringComparison.Ordinal)) == 1);
        });
        Test("bridge accepts supplementary Unicode and rejects lone surrogate queries", async () =>
        {
            var script = Script("r=json.loads(sys.stdin.readline())\nassert r['query']=='A title \\U0001f680'\nprint(json.dumps({'version':1,'requestId':r['requestId'],'type':'completed'}),flush=True)\n");
            await using var fixture = new PythonScraperClient(python, script);
            Assert((await fixture.MatchAsync("A title 🚀")).Count == 0);
            foreach (var invalid in new[] { "Broken \ud800", "Broken \udc00" })
            {
                try { await fixture.MatchAsync(invalid); throw new InvalidOperationException("Expected invalid Unicode rejection"); }
                catch (ArgumentException) { }
            }
        });
        Test("configured bridge rejects unsupported source selection before process start", async () =>
        {
            var log = FileAt("source-selection-log", "");
            await using var client = new PythonScraperClient(python, SessionWorker(log), Config());
            foreach (var selection in new[] { new[] { "xcity" }, new[] { "javbus", "javbus" }, Array.Empty<string>() })
            {
                try { await client.MatchAsync("BF-658", sources: selection); throw new InvalidOperationException("Expected source rejection"); }
                catch (ArgumentException) { }
            }
            Assert(File.ReadAllText(log) == "");
        });
        if (actualWorker is not null)
        {
            Test("configured disabled actual worker remains offline across repeated requests", async () =>
            {
                await using var client = new PythonScraperClient(python, actualWorker, Config());
                await ExpectCode("live_unavailable", () => client.MatchAsync("BF-658", sources: ["javbus"]));
                await ExpectCode("unsupported_query", () => client.MatchAsync("A neutral title", sources: ["javbus"]));
                await ExpectCode("live_unavailable", () => client.MatchAsync("BF-658"));
            });
            Test("configured actual worker returns safe invalid-config startup error", async () =>
            {
                var invalid = FileAt("invalid-config.json", "{\"network_enabled\":false,\"unexpected_field\":true}");
                await using var client = new PythonScraperClient(python, actualWorker, invalid);
                await ExpectCode("invalid_config", () => client.MatchAsync("BF-658"));
                await ExpectCode("session_faulted", () => client.MatchAsync("BF-658"));
            });
        }
    }
}
