# XTeam Debugging Sample

I was asked to showcase my debugging process using a tricky bug I recently
encountered and fixed.  One particularly annoying bug that I fixed recently
involved a typo in a configuration of an Azure Functions application.  Since the
bug in question occurred in a closed source application, I prepared a contrived
code sample basing on a similar configuration bug
[recently discovered in Civilization VI](https://kotaku.com/fans-discover-game-changing-typo-in-civilization-vis-co-1823845314)
.


## Requirements

I wrote and tested this code sample using the following software.  Be advised
that `azure-functions-core-tools` is being actively developed, so the code
sample may not necessarily work with a different version.

- `dotnet-sdk`, version used: `2.1.101`
- `VS Code`, version used: `1.21.1`
- `azure-functions-core-tools`, version used `2.0.1-beta.24`
- `mono`, version used: `5.8.0.129`

## Situation

I was working on an Azure Functions application that was calling some external
libraries written in C# and F#.  The configuration for the application was
stored in the environment variables set on the target deployment.  After one
particular deployment, I noticed that integration tests starting failing, even
though the function seemed straightforward and all unit tests passed prior to
deployment.

In the original bug I fixed, the failure was quite complicated, as it involved
interplay of multiple libraries and a `NullReferenceException` caused within F#
code.  In this sample, I simplified it to returning a to better illustrate the
underlying cause. 

For further simplicity, I ommited the unit tests and the integration tests and I will be
testing the Azure Function locally using `curl`.


## Problem
We expected that a  `GET` request to `/api/sample` should return a JSON similar to
the following:

```json
{
    "scienceReport":"Our current science yield is 10",
    "timeStamp":"2018-03-21T13:39:48.809166+00:00"
}
```

where `scienceReport` field is configured by setting the `YIELD_SCIENCE`
environment variable on target deployment, and `timeStamp` is the current time
stamp.

However, in our case the integration tests are failing as the `GET` request to
`/api/sample` endpoint returns the following JSON:

```json
{
    "scienceReport":"No Science Yield!",
    "timeStamp":"2018-03-21T13:39:48.809166+00:00"
}
```

After quickly checking the target deployment configuration everything seems
fine, so we will have to take a closer look at the problem.


## Step 1: Reproduce the problem

After checking out the source code for the application, we need to see if we can
reproduce the problem locally.

In a real-life scenario we would use `azure-function-core-tools` to download the
configuration from the affected Function App deployment and place it in a
`local.settings.json` file.  The environment variables from the target
deployment are stored in the `Values` entry in `local.settings.json` file, and
function host will automatically use them to populate the function application
environment.

For simplicity, I already provided `local.settings.json`
that contains the configuration of the target deployment.

Now, let's build and start the function application:

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

After starting the function host, we should see the following message in the
terminal:

```
Listening on http://localhost:7071/
Hit CTRL-C to exit...

Http Functions:

        SampleTrigger: http://localhost:7071/api/sample

```

Now, we can open a new terminal window and use curl to test the function and
reproduce the problem on our machine:
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
response (`SampleTrigger.cs:55-65`):

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

and we are able to locate the code that initialises `_science` (`SampleTrigger.cs:22-31`):

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

Since we already noted that the unit tests for external libraries were passing,
which should include the unit tests for the `Science` class, therefore we will
investigate the configuration first.

## Step 3: Debugging
We set up a breakpoint on the following statement on the line 30 of `SampleTrigger.cs`:
``` csharp
_science = new Science(yield);
```
Since the code in question is in the static constructor of the `SampleTrigger`
class, we need to restart the function host before attaching the debugger.  The
static constructor for the `SampleTrigger` class will be executed the first time
we make a request to the `api/sample` endpoint, therefore we need to attach the
debugger before making any requests to the function application.

We will attach the debugger using the `.NET Core Attach` launch configuration in
VS Code.  We will attach to the `func` process, which represents the process of
the function host.

After attaching the debugger, we make another `curl` request to the `api/sample`
endpoint:

``` sh
$ curl localhost:7071/api/sample
```

In VS Code we should now see that the breakpoint was hit.

Using either the _Variables_ pane, or the debugger console we can inspect the
state of the local variables:
- after inspecting the `yield` variable, we notice that it is set to `0`.
- after inspecting what keys got loaded to the configuration we notice that the
  configuration does not contain an entry for `YIELD_SCIENCE`, but an entry for
  `YEILD_SCIENCE`.

Note that, if we want to list the entries in the `configuration` object from
the debugger console, we need to evaluate the following expression:
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

## Step 4: Writing the Fix
It seems that we have found the culprit---there is a typo in our configuration!

Now, a case when a critical part of configuration is missing should correspond
to a hard failure, as in most cases it is impossible for the system to recover.
In some cases it may seem that having a well known default configuration is a
good back-up option, however it may lead to silent failures for incorrect
configuraiton, as in our case.

Therefore we will update the `SampleTrigger` static constructor to raise an
exception when the configuration is missing.

The updated constructor is as follows (`SampleTrigger.cs:35-45`):
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
Before updating the configuration, we need to make sure that the fix we
implemented does the job and will throw an exception for the current, incorrect
configuration.

We re-build everything and restart the function host.  If the build command fails
due to a locked dll file, we need to stop function host before building.  After
all of that, we make another `curl` request to the `/api/sample` endpoint:

``` sh
$ curl localhost:7071/api/sample
```

After making the request, we check that the function host output to see that the
static constructor for `SampleTrigger` threw an exception:

```
A ScriptHost error has occurred
[21/03/2018 14:38:58] Exception while executing function: SampleTrigger. FunctionsApp: The type initializer for 'FunctionsApp.SampleTrigger' threw an exception. FunctionsApp: Missing configuration for YIELD_SCIENCE.
```

Once we made sure that our fix throws an exception when the configuration is
misssing we can proceed and update the configuration.

## Step 6: Testing the Fix (Green)
Now, we can update the configuration and check if we receive the desired
response from the `GET` request to the `/api/sample` endpoint.


We update the configuration in `local.settings.json file`:  
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

(**Note** that JSON
files should not contain comments, they were used in this case for demonstration
purposes only)

Again, we rebuild everything, restart function host, and make another `curl`
request:

``` sh
$ curl localhost:7071/api/sample
```

### Step 6a: Bonus Failure
(**NOTE** This failure is caused by how Azure Functions are handling some
external libraries, including the libraries need by the runtime for the F#
language.  This problem may seem unrelated to this code sample, however it is
pervasive when working with pre-compiled functions applications, therefore it is
worth mentioning.  Thankfully, there seems to be solution to this problem
[available soon](https://github.com/Azure/azure-functions-host/issues/2446))

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
we can see that it comes with a different version of `FSharp.Core.dll`:
``` sh
$ cd /usr/local/Cellar/azure-functions-core-tools/2.0.1-beta.24/
$ monodis --assembly FSharp.Core.dll | grep Version
Version:       4.4.1.0
```
(**NOTE** that the path to the runtime is specific to macOS)

After some investigation, it turned out that since the last time anyone worked
on this code the `FSharp.Core.dll` was updated from `4.4.1.0` to `4.4.3.0`, whereas
Azure Functions will permit only the version of `FSharp.Core.dll` that comes with
the runtime.

Therefore, we need to update the `FSharpLib.fsproj` file to fix the version of
`FSharp.Core` package to provide `FSharp.Core.dll` with version `4.4.1.0`:

``` xml
<PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <!-- FIX 3: Fix FSharp.Core to match version in Azure Functions Runtime -->
    <FSharpCoreImplicitPackageVersion>4.1.18</FSharpCoreImplicitPackageVersion>
</PropertyGroup>
```

Finally, after re-building and re-starting the function host we can verify that we receive the expected response from the `GET` request to the `/api/sample` endpoint:

``` sh
$ curl localhost:7071/api/sample
```
``` json
{
    "scienceReport":"Our current science yield is 10",
    "timeStamp":"2018-03-21T15:02:42.695296+00:00"
}
```

## Further Steps
I hope that this code sample and associated description illustrates my debugging process.

In a real-life scenario, there are some further steps that I would perform that include:
- updating test cases, to potentially include the case with missing configuration,
- updating documentation, to potentially include information about newly found
  problem with `FSharp.Core` being restricted by Azure Functions runtime, and
- submitting everything for code review.

Unfortunately, the above steps are outside of the scope for this code sample.
