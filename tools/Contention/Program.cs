using System.Collections.Concurrent; using System.Diagnostics; using System.Net.Http.Json;

var a = args.Chunk(2).ToDictionary(x => x[0].TrimStart('-'), x => x[1]);
string Arg(string k, string d) => a.GetValueOrDefault(k, d);
var mode = Arg("mode", "atomic"); var kase = Arg("case", "c1");
var ops = int.Parse(Arg("ops", "5000")); var par = int.Parse(Arg("parallel", "50"));
var deposit = decimal.Parse(Arg("deposit", "10000")); var n = int.Parse(Arg("accounts", "2"));
var http = new HttpClient { BaseAddress = new Uri(Arg("base", "http://localhost:5000")) };

var lat = new ConcurrentBag<double>(); var codes = new ConcurrentDictionary<string, int>();
async Task Post(string url, object body)
{
    var sw = Stopwatch.StartNew();
    var r = await http.PostAsJsonAsync(url, body);
    lat.Add(sw.Elapsed.TotalMilliseconds);
    var key = ((int)r.StatusCode).ToString();
    if ((int)r.StatusCode == 409) key += " " + (await r.Content.ReadAsStringAsync()).Trim();
    codes.AddOrUpdate(key, 1, (_, c) => c + 1);
}
async Task<long> NewAccount()
{
    var r = await http.PostAsJsonAsync("/accounts", new { owner = "contention", currency = "USD" });
    var id = (await r.Content.ReadFromJsonAsync<Created>())!.Id;
    await http.PostAsJsonAsync($"/accounts/{id}/deposit", new { amount = deposit });
    return id;
}
async Task<decimal> Avail(long id) => (await http.GetFromJsonAsync<Bal>($"/accounts/{id}/balance"))!.Available;

var ids = new List<long>(); for (var i = 0; i < (kase == "c1" ? 1 : n); i++) ids.Add(await NewAccount());
var total = Stopwatch.StartNew();
await Parallel.ForEachAsync(Enumerable.Range(0, ops), new ParallelOptions { MaxDegreeOfParallelism = par }, (_, _) =>
{
    if (kase == "c1") return new ValueTask(Post($"/accounts/{ids[0]}/withdraw?mode={mode}", new { amount = 1m }));
    var f = ids[Random.Shared.Next(ids.Count)]; long t;
    do t = ids[Random.Shared.Next(ids.Count)]; while (t == f);
    return new ValueTask(Post($"/transfers?mode={mode}", new { fromAccountId = f, toAccountId = t, amount = 1m }));
});
total.Stop();

var l = lat.Order().ToArray(); double P(double q) => l[(int)(q * (l.Length - 1))];
Console.WriteLine($"case={kase} mode={mode} rps={ops / total.Elapsed.TotalSeconds:F0} p50={P(.5):F1} p95={P(.95):F1} p99={P(.99):F1} ms");
Console.WriteLine("codes: " + string.Join(", ", codes.OrderBy(c => c.Key).Select(c => $"{c.Key}={c.Value}")));
if (kase == "c1")
{
    var ok = codes.GetValueOrDefault("200"); var actual = await Avail(ids[0]); var expected = deposit - ok;
    Console.WriteLine($"expected={expected} actual={actual} lost={actual - expected} oversell={Math.Max(0, ok - (int)deposit)}");
}
else Console.WriteLine($"sum={(await Task.WhenAll(ids.Select(Avail))).Sum()} expected={deposit * ids.Count}");

record Created(long Id); record Bal(decimal Available);
