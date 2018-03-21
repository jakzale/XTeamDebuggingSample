using System;
using System.Net;
using System.IO;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Configuration;

using Newtonsoft.Json;

using CSharpLib;
using FSharpLib;

namespace FunctionsApp {
    public static class SampleTrigger {
        private static Science _science;


        // static SampleTrigger() {
        //     var builder = new ConfigurationBuilder()
        //         .AddEnvironmentVariables();
            
        //     var configuration = builder.Build();

        //     var yield = configuration.GetValue<int>("YIELD_SCIENCE");

        //     _science = new Science(yield);
        // }

        // FIX 1: Make lack of configuartion a hard failure
        static SampleTrigger() {
            var builder = new ConfigurationBuilder()
                .AddEnvironmentVariables();
            
            var configuration = builder.Build();

            var yield = configuration.GetValue<int?>("YIELD_SCIENCE") ?? throw new InvalidOperationException("Missing configuration for YIELD_SCIENCE");

            _science = new Science(yield);
        }


        [FunctionName("SampleTrigger")]
        public static IActionResult Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route="sample")]HttpRequest req,
            TraceWriter log)
        {
            log.Info("C# HTTP trigger function processed a request.");

            var report = Report.Report(_science.Yield);

            var response = new { ScienceReport = report, TimeStamp = DateTime.Now};

            return new OkObjectResult(response);
        }

    }
}