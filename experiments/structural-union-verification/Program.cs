// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// Structural union classifier verification — Q1/Q2/Q3.
// See ../../plan.md and the cross-session prompt in the parent session.
#pragma warning disable SYSLIB1227 // structural-classifier "ambiguous"/"identical shape" warnings

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var stjAsm = typeof(JsonSerializer).Assembly;
Console.WriteLine($"STJ loaded from: {stjAsm.Location}");
_ = stjAsm.GetType("System.Text.Json.Serialization.JsonStructuralUnionTypeClassifier")
    ?? throw new InvalidOperationException("Prototype STJ dll does not expose JsonStructuralUnionTypeClassifier.");
Console.WriteLine();

var report = new StringBuilder();
var counters = new int[2]; // [0] = pass, [1] = fail

// ---------- Q1: ASP.NET Core wiring via ConfigureHttpJsonOptions ----------
Console.WriteLine("========== Q1: ASP.NET Core wiring through ConfigureHttpJsonOptions ==========");
report.AppendLine("Q1 — global classifier registration via ConfigureHttpJsonOptions");
report.AppendLine("---------------------------------------------------------------");

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5099");
builder.Logging.ClearProviders();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.TypeClassifiers.Add(new JsonStructuralUnionTypeClassifier());
});

var app = builder.Build();

app.MapPost("/q1/parse", (Q1.UnionIntString u) => u.Value switch
{
    int i => new { kind = "Int32", value = (object)i },
    string s => new { kind = "String", value = (object)s },
    _ => new { kind = "other", value = (object?)u.Value! }
});

await app.StartAsync();
using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:5099") };

foreach (var (label, payload, expectedStatus, expectedKindOrNull) in new (string, string, HttpStatusCode, string?)[]
{
    ("Q1: int literal 42",                    "42",        HttpStatusCode.OK,        "Int32"),
    ("Q1: string \"hello\"",                  "\"hello\"", HttpStatusCode.OK,        "String"),
    ("Q1: \"42\" (Web AllowReadFromString)",  "\"42\"",    HttpStatusCode.OK,        "Int32"),
    ("Q1: 42.5 (fractional Number)",          "42.5",      HttpStatusCode.OK,        null),   // record whatever the classifier decides
    ("Q1: null literal",                      "null",      HttpStatusCode.OK,        null),
})
{
    using var req = new HttpRequestMessage(HttpMethod.Post, "/q1/parse")
    {
        Content = new StringContent(payload, Encoding.UTF8, "application/json"),
    };
    using var resp = await http.SendAsync(req);
    var body = await resp.Content.ReadAsStringAsync();
    var pass = resp.StatusCode == expectedStatus &&
        (expectedKindOrNull is null || body.Contains($"\"kind\":\"{expectedKindOrNull}\""));
    Record(pass, $"{label,-44} -> {(int)resp.StatusCode} {body.Trim()}");
}

Console.WriteLine();
report.AppendLine();

// ---------- Q2: precedence between [JsonUnion(TypeClassifier=…)] and JsonSerializerOptions.TypeClassifiers ----------
Console.WriteLine("========== Q2: precedence — [JsonUnion(TypeClassifier=…)] vs global TypeClassifiers ==========");
report.AppendLine("Q2 — [JsonUnion(TypeClassifier)] vs JsonSerializerOptions.TypeClassifiers");
report.AppendLine("---------------------------------------------------------------------");

var q2Opts = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    TypeClassifiers = { new JsonStructuralUnionTypeClassifier() },
};

// Hand-written classifier registered via [JsonUnion] ALWAYS returns typeof(string).
// If attribute wins: every payload → String.
// If global (structural) wins: 42 → Int32, "42" → Int32 (Web), "hello" → String.
foreach (var (label, json) in new[]
{
    ("Q2a: 42 (attribute→String, global→Int32)", "42"),
    ("Q2b: \"42\" (attribute→String, global→Int32)", "\"42\""),
    ("Q2c: \"hello\" (both→String)", "\"hello\""),
})
{
    string caseName;
    try
    {
        var result = JsonSerializer.Deserialize<Q2.UnionIntStringWithAttribute>(json, q2Opts);
        caseName = GetUnionValue(result)?.GetType().Name ?? "<null>";
    }
    catch (Exception ex)
    {
        caseName = $"THROWS:{ex.GetType().Name}";
    }
    Record(true, $"{label,-52} json={json,-10} -> {caseName}"); // pure observation
}

Console.WriteLine();
report.AppendLine();

// ---------- Q3: disambiguating {"Name":"x","Breed":"y"} ----------
Console.WriteLine("========== Q3: {\"Name\":\"x\",\"Breed\":\"y\"} disambiguation ==========");
report.AppendLine("Q3 — Disambiguating overlapping property shapes with structural classifier");
report.AppendLine("--------------------------------------------------------------------------");

var q3Opts = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    TypeClassifiers = { new JsonStructuralUnionTypeClassifier() },
};

void RunQ3<T>(string label, string json, string expected)
{
    string actual;
    try
    {
        var result = JsonSerializer.Deserialize<T>(json, q3Opts);
        actual = GetUnionValue(result)?.GetType().Name ?? "<null>";
    }
    catch (Exception ex)
    {
        actual = $"THROWS:{ex.GetType().Name}";
    }
    var pass = actual == expected || (expected.StartsWith("THROWS:", StringComparison.Ordinal) && actual.StartsWith(expected, StringComparison.Ordinal));
    Record(pass, $"{label,-55} json={json,-46} -> {actual} (expected {expected})");
}

Console.WriteLine("-- (baseline) plain Cat/Dog --");
report.AppendLine("(baseline) plain Cat/Dog");
RunQ3<Q3.UnionPet>("baseline Cat-only", "{\"Name\":\"x\"}", "Cat");
RunQ3<Q3.UnionPet>("baseline Dog-only", "{\"Breed\":\"y\"}", "Dog");
RunQ3<Q3.UnionPet>("baseline mixed (tie 1:1)", "{\"Name\":\"x\",\"Breed\":\"y\"}", "THROWS:JsonException");

Console.WriteLine("-- (S1) Disallow on Cat only --");
report.AppendLine("(S1) [JsonUnmappedMemberHandling(Disallow)] on Cat only");
RunQ3<Q3S1.UnionPet>("S1 Cat-only",  "{\"Name\":\"x\"}",                     "CatS1");
RunQ3<Q3S1.UnionPet>("S1 Dog-only",  "{\"Breed\":\"y\"}",                    "DogS1");
RunQ3<Q3S1.UnionPet>("S1 mixed (Cat disq→Dog)",  "{\"Name\":\"x\",\"Breed\":\"y\"}", "DogS1");

Console.WriteLine("-- (S2) Disallow on BOTH --");
report.AppendLine("(S2) [JsonUnmappedMemberHandling(Disallow)] on BOTH");
RunQ3<Q3S2.UnionPet>("S2 Cat-only",  "{\"Name\":\"x\"}",                     "CatS2");
RunQ3<Q3S2.UnionPet>("S2 Dog-only",  "{\"Breed\":\"y\"}",                    "DogS2");
RunQ3<Q3S2.UnionPet>("S2 mixed (both disq)",  "{\"Name\":\"x\",\"Breed\":\"y\"}", "THROWS:JsonException");

Console.WriteLine("-- (S3) required Species on Cat --");
report.AppendLine("(S3) required string Species on Cat with [JsonPropertyName(\"species\")]");
RunQ3<Q3S3.UnionPet>("S3 with species→Cat", "{\"species\":\"cat\",\"Name\":\"x\"}",              "CatS3");
RunQ3<Q3S3.UnionPet>("S3 no species,Name only→Dog", "{\"Name\":\"x\"}",                          "DogS3");
RunQ3<Q3S3.UnionPet>("S3 Dog-only", "{\"Breed\":\"y\"}",                                          "DogS3");
RunQ3<Q3S3.UnionPet>("S3 mixed no species→Dog", "{\"Name\":\"x\",\"Breed\":\"y\"}",              "DogS3");
RunQ3<Q3S3.UnionPet>("S3 mixed with species→Cat", "{\"species\":\"cat\",\"Name\":\"x\",\"Breed\":\"y\"}", "CatS3");

Console.WriteLine();
Console.WriteLine($"=================== TOTAL: pass={counters[0]}, fail={counters[1]} ===================");
report.AppendLine();
report.AppendLine($"TOTAL: pass={counters[0]}, fail={counters[1]}");

await app.StopAsync();

var reportPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "run-report.txt"));
File.WriteAllText(reportPath, report.ToString());
Console.WriteLine($"Report written to: {reportPath}");

// ------- helpers -------
void Record(bool pass, string line)
{
    var mark = pass ? "[ok]  " : "[FAIL]";
    var full = $"  {mark} {line}";
    Console.WriteLine(full);
    report.AppendLine(full);
    if (pass) counters[0]++; else counters[1]++;
}

static object? GetUnionValue(object? unionInstance)
{
    if (unionInstance is null) return null;
    var t = unionInstance.GetType();
    return t.GetProperty("Value")?.GetValue(unionInstance);
}

// ---------- Types ----------
namespace Q1
{
    public union UnionIntString(int, string);
}

namespace Q2
{
    public sealed class AlwaysStringClassifier : JsonTypeClassifierFactory<UnionIntStringWithAttribute>
    {
        public override JsonTypeClassifier CreateJsonClassifier(
            JsonTypeClassifierContext context,
            JsonSerializerOptions options) =>
            static (ref System.Text.Json.Utf8JsonReader reader) => typeof(string);
    }

    [JsonUnion(TypeClassifier = typeof(AlwaysStringClassifier))]
    public union UnionIntStringWithAttribute(int, string);
}

namespace Q3
{
    public record Cat(string Name);
    public record Dog(string Breed);
    public union UnionPet(Cat, Dog);
}

namespace Q3S1
{
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public record CatS1(string Name);
    public record DogS1(string Breed);
    public union UnionPet(CatS1, DogS1);
}

namespace Q3S2
{
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public record CatS2(string Name);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public record DogS2(string Breed);
    public union UnionPet(CatS2, DogS2);
}

namespace Q3S3
{
    public record CatS3
    {
        [JsonPropertyName("species")]
        public required string Species { get; init; }
        public string Name { get; init; } = "";
    }
    public record DogS3(string Breed);
    public union UnionPet(CatS3, DogS3);
}
