# Response Caching compatibility samples

Each sample is a [file-based app](https://learn.microsoft.com/dotnet/core/sdk/file-based-apps) showing one Response Caching usage,
and the same app migrated to Output Caching with `UseResponseCachingSemantics()`:

```csharp
// Before
builder.Services.AddResponseCaching();
app.UseResponseCaching();

// After
builder.Services.AddOutputCache(options => options.AddBasePolicy(policy => policy.UseResponseCachingSemantics()));
app.UseOutputCache();
```

| Sample | Scenario |
|---|---|
| [CacheControlHeaders.cs](CacheControlHeaders.cs) | The endpoint sets `Cache-Control: public, max-age=3`. Clients send `no-cache` or `max-age=0`. |

## Running

The samples build the Output Caching project of this repository, so they need the repository SDK:

```bash
# from the repository root
./restore.sh            # Windows: restore.cmd
source activate.sh      # Windows: . ./activate.ps1
cd src/Middleware/OutputCaching/samples/ResponseCachingCompatibility

dotnet run CacheControlHeaders.cs                      # compares the setups side by side
dotnet run CacheControlHeaders.cs -- responsecaching   # serves the Response Caching setup on http://localhost:5000
dotnet run CacheControlHeaders.cs -- outputcaching     # serves the Output Caching setup on http://localhost:5000
```

Without arguments, a sample starts each setup, sends the same requests to all of them, and prints the responses.
The endpoint returns `response #N` with a counter, so a repeated number means the response was served from the cache:

```
Step                                                          ResponseCaching         OutputCaching + policy  OutputCaching default
1. First request: the endpoint runs                           response #1             response #1             response #1
2. Same request within max-age: served from cache             response #1 (Age: 0s)   response #1 (Age: 0s)   response #1 (Age: 0s)
3. Client sends 'Cache-Control: no-cache': endpoint runs      response #2             response #2             response #1 (Age: 0s)
...
```

The `OutputCaching default` column uses the default Output Caching policy, which ignores `Cache-Control` headers.

## How the samples are built

[Directory.Build.props](Directory.Build.props) and [Directory.Build.targets](Directory.Build.targets) don't import the repository
build infrastructure, so the samples build like an application outside of this repository. Each sample references
`Microsoft.AspNetCore.OutputCaching.csproj` with a `#:project` directive, and `Directory.Build.targets` makes the compiler use it instead of
the copy in the installed shared framework.
