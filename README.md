# XTeam Debugging Sample

I was debugging an Azure Functions Application.  The sample provided was
simplified to better illustrate the problem and the debugging process involved.
The provided code may seem a bit contrived, as I couldn't provide the original
code, as it was written for my employer.

## Requirements

- `dotnet-sdk`, version used: `2.1.101`
- `VS Code`, version used: `1.21.1`
- `azure-functions-core-tools`, version used `2.0.1-beta.24`
- `mono`, version used: `5.8.0.129`

## Problem

I was debugging an Azure Functions Application that was calling external
libraries written in C# and F#.  The unit tests were passing successfuly for
each of the external libraries, but the integration tests for the function app
were failing.

We know that a `GET` request to `/api/sample` should return a JSON similar to
the following:

```json
{
    "scienceReport":"Our current science yeild is 10",
    "timeStamp":"2018-03-21T13:39:48.809166+00:00"
}
```

where `scienceReport` is a string that is influenced by configuration of the
server, and `timeStamp` is the current time stamp.

For simplicity, I ommited the integration tests and I will be testing the azure
function using `curl`.

## Step 1: Reproduce the problem

After checking out the code we need to reproduce the problem locally.

In a real-life scenario we would use `azure-function-core-tools` to download the
configuration from the affected Function App deployment and place them in
`local.settings.json`.  However, for simplicity I already provided
`local.settings.json` that contains the necessary configuraiton to get started.

Now we build and start the function app

``` sh
$ cd XTeamDebuggingSample
$ dotnet restore
$ dotnet build
$ cd src/FunctionsApp/bin/Debug/netstandard2.0/
$ func host start
```

Alternatively, we could open the folder containing `XTeamDebuggingSample.sln` in
VS Code and run the following tasks in the following order:
1. Build All
2. Run Function Host

After starting the function host, we should see the following message:

```
Listening on http://localhost:7071/
Hit CTRL-C to exit...

Http Functions:

        SampleTrigger: http://localhost:7071/api/sample

```

Finally, we can use curl to test the function and reproduce the problem.
``` sh
$ curl localhost:7071/api/sample
```
``` json
{
    "scienceReport":"No Science Yield!",
    "timeStamp":"2018-03-21T13:54:11.063377+00:00"
}
```

## Step 2: Finding the Problem
The `GET` request send to the `/api/sample` endpoint returned a JSON object with
an incorrect value for the `scienceReport` key.  After reading the code for
`SampleTrigger` function, we find the following code for constructing the
response (SampleTrigger.cs:55-65):

``` csharp
string report;

if (_science.Yield > 0) {
    report = Report.Report(_science.Yield);
} else {
    report = "No Science Yield!";
}

var response = new { ScienceReport = report, TimeStamp = DateTime.Now};

return new OkObjectResult(response);
```

and we are able to locate the code that initialises `_science` (SampleTrigger.cs:22-31):

``` csharp
static SampleTrigger() {
    var builder = new ConfigurationBuilder()
        .AddEnvironmentVariables();
    
    var configuration = builder.Build();

    var yield = configuration.GetValue<int>("YIELD_SCIENCE");

    _science = new Science(yield);
}
```

Given that, we have two potential culprits:
- either there is a bug in the implementation of the `Science` class, or
- there is a bug in the configuration.

Since we already noted that the unit tests for external libraries were passing, which should include the unit tests for the `Science` class, therefore we will investigate the configuration first.

## Step 3: Debugging
We set up a breakpoint on the line 30 of SampleTrigger.cs, and since the code in
question is in the static constructor of the `SampleTrigger` class, we restart
the function host before attaching the debugger.  The static constructor for the
`SampleTrigger` class will be executed the first time we make a request to the
`api/sample` endpoint.

We will attach the debugger using the `.NET Core Attach` launch configuration in
VS Code.  We will attach to the `func` process, which represents the process of
the function host.

After the debugger attached, we make another `curl` request to the `api/sample` endpoint:

``` sh
$ curl localhost:7071/api/sample
```

In VS Code we should now see that the breakpoint was hit.

Using either the _Variables_ pane, or the debugger console we can inspect the
state of the local variables:
- after inspecting the `yield` variable, we notice that it is set to `0`.
- after inspecting what keys got loaded to the configuration we notice that the configuration does not contain an entry for `YIELD_SCIENCE`, but an entry for `YEILD_SCIENCE`.

Note that, if you want to list the entries in the `configuration` object from the debugger console, you need to evaluate the following expression:
``` csharp
((Microsoft.Extensions.Configuration.EnvironmentVariables.EnvironmentVariablesConfigurationProvider)System.Linq.Enumerable.First(configuration.Providers)).Data.Keys
```

After checking the configuration file we can indeed confirm that there is a typo
in the `YEILD_SCIENCE` entry:

``` json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "",
    "YEILD_SCIENCE": 10
  }
}
```

## Step 4: Fixing
Missing critical configuration should correspond to a hard failure, as in most
cases it is impossible for system to recover, therefore we will update the
`SampleTrigger` static constructor to raise an exception, when the configuration is missing.

The updated constructor is as follows (SampleTrigger.cs:35-45):
``` csharp
// FIX 1: Make lack of configuartion a hard failure
static SampleTrigger() {
    var builder = new ConfigurationBuilder()
        .AddEnvironmentVariables();
    
    var configuration = builder.Build();

    var yield = configuration.GetValue<int?>("YIELD_SCIENCE")
                ?? throw new InvalidOperationException("Missing configuration for YIELD_SCIENCE");

    _science = new Science(yield);
}
```

## Step 5: Testing the Fix (Red)
Before updating the configuration we need to make sure that indeed the fix we
implemented will throw an exception for the current, incorrect configuration.

We stop the function host, build everything, start the function host again, and
make another `curl` request to the `/api/sample` endpoint:

``` sh
$ curl localhost:7071/api/sample
```

After making the request, we check the function host output to see that indeed
the static constructor for `SampleTrigger` threw an exception:

```
A ScriptHost error has occurred
[21/03/2018 14:38:58] Exception while executing function: SampleTrigger. FunctionsApp: The type initializer for 'FunctionsApp.SampleTrigger' threw an exception. FunctionsApp: Missing configuration for YIELD_SCIENCE.
```

## Step 6: Testing the Fix (Green)
Now, we can finally update the configuration to check if everything works fine.

We update the configuration in local.settings.json file.  (**Note** that JSON
files should not contain comments, they were used in this case for demonstration
purposes only):
``` json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "",
    // "YEILD_SCIENCE": 10
    // FIX 2: Fix the configuration file
    "YIELD_SCIENCE": 10
  }
}
```

Again, we stop the function host, build everything, start the function host again, and make another `curl` request.

``` sh
$ curl localhost:7071/api/sample
```

### Step 6a: Bonus Failure
Now we get the following surprising failure:
``` sh
[21/03/2018 14:50:01] A ScriptHost error has occurred
[21/03/2018 14:50:01] Exception while executing function: SampleTrigger. FSharpLib: Could not load file or assembly 'FSharp.Core, Version=4.4.3.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a'. Could not find or load a specific file. (Exception from HRESULT: 0x80131621). System.Private.CoreLib: Could not load file or assembly 'FSharp.Core, Version=4.4.3.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a'.
```

After quickly checking the `src/FunctionsApp/bin/Debug/netstandard2.0/bin`
directory we can confirm that there is a `FSharp.Core.dll` with the requested
version:
``` sh
$ cd XTeamDebuggingSample
$ cd src/FunctionsApp/bin/Debug/netstandard2.0/bin
$ monodis --assembly FSharp.Core.dll | grep Version
Version:       4.4.3.0
```

However, after checking the libraries provided with the Azure Function Runtime,
we can check that it comes with a different version of `FSharp.Core.dll`.
(**NOTE** that the path to the runtime is specific to macOS):
``` sh
$ cd /usr/local/Cellar/azure-functions-core-tools/2.0.1-beta.24/
$ monodis --assembly FSharp.Core.dll | grep Version
Version:       4.4.1.0
```

After some investigation, it turned out that since the last time anyone worked
on this code the `FSharp.Core` was updated from `4.4.1.0` to `4.4.3.0`, whereas
Azure Functions will permit only the version of `FSharp.Core` that comes with
the runtime.

Therefore, we need to update the `FSharpLib.fsproj` file to fix `FSharp.Core` to
version `4.4.1.0`:

``` xml
<PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <!-- FIX 3: Fix FSharp.Core to match version in Azure Functions Runtime -->
    <FSharpCoreImplicitPackageVersion>4.1.18</FSharpCoreImplicitPackageVersion>
</PropertyGroup>
```

Finally, after re-building and re-starting the host we can check if everything
works fine:

``` sh
$ curl localhost:7071/api/sample
```
``` json
{
    "scienceReport":"Our current science yeild is 10",
    "timeStamp":"2018-03-21T15:02:42.695296+00:00"
}
```

## Further Steps
In a real-life scenario, after confirming that the fix works now would be time
to update test cases, documentation, and finally submit everything for code
review, but all of that is outside the scope of this sample.

