using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Primitives;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace HttpFaultInjector
{
    public class Program
    {
        private static readonly HttpClient _httpClient = new HttpClient();

        private static readonly string[] _excludedRequestHeaders = new string[] {
            // Only applies to request between client and proxy
            "Proxy-Connection",
        };

        // Headers which must be set on HttpContent instead of HttpRequestMessage
        private static readonly string[] _contentRequestHeaders = new string[] {
            "Content-Length",
            "Content-Type",
        };

        public static void Main(string[] args)
        {
            new WebHostBuilder()
                .UseKestrel(options =>
                {
                    options.Listen(IPAddress.Any, 7777);
                    options.Listen(IPAddress.Any, 7778, listenOptions =>
                    {
                        listenOptions.UseHttps("testCert.pfx", "testPassword");
                    });
                })
                .UseContentRoot(Directory.GetCurrentDirectory())
                .Configure(app => app.Run(async context =>
                {
                    try
                    {

                        var upstreamResponse = await SendUpstreamRequest(context.Request);

                        Console.WriteLine();

                        Console.WriteLine("Press a key to select a response:");
                        Console.WriteLine("f: Full response");
                        Console.WriteLine("p: Partial Response (full response headers, 50% of body)");
                        Console.WriteLine("n: No response");
                        Console.WriteLine("a: Abort connection");

                        while (true)
                        {
                            var key = Console.ReadKey();

                            switch (key.KeyChar)
                            {
                                case 'f':
                                    await SendDownstreamResponse(upstreamResponse, context.Response);
                                    return;
                                case 'a':
                                    context.Abort();
                                    return;
                                default:
                                    Console.WriteLine($"Invalid selection: {key.KeyChar}");
                                    break;
                            }

                        }
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e);
                    }
                }))
                .Build()
                .Run();
        }

        private static async Task<UpstreamResponse> SendUpstreamRequest(HttpRequest request)
        {
            var upstreamUriBuilder = new UriBuilder()
            {
                Scheme = request.Scheme,
                Host = request.Host.Host,
                Path = request.Path.Value,
                Query = request.QueryString.Value,
            };

            if (request.Host.Port.HasValue)
            {
                upstreamUriBuilder.Port = request.Host.Port.Value;
            }

            var upstreamUri = upstreamUriBuilder.Uri;

            Log("Upstream Request");
            Log($"URL: {upstreamUri}");

            using (var upstreamRequest = new HttpRequestMessage(new HttpMethod(request.Method), upstreamUri))
            {
                Log("Headers:");

                if (request.ContentLength > 0)
                {
                    upstreamRequest.Content = new StreamContent(request.Body);

                    foreach (var header in request.Headers.Where(h => _contentRequestHeaders.Contains(h.Key)))
                    {
                        Log($"  {header.Key}:{header.Value.First()}");
                        upstreamRequest.Content.Headers.Add(header.Key, values: header.Value);
                    }
                }

                foreach (var header in request.Headers.Where(h => !_excludedRequestHeaders.Contains(h.Key) && !_contentRequestHeaders.Contains(h.Key)))
                {
                    Log($"  {header.Key}:{header.Value.First()}");
                    if (!upstreamRequest.Headers.TryAddWithoutValidation(header.Key, values: header.Value))
                    {
                        throw new InvalidOperationException($"Could not add header {header.Key} with value {header.Value}");
                    }
                }

                Log("Sending request to upstream server...");
                using (var upstreamResponseMessage = await _httpClient.SendAsync(upstreamRequest))
                {
                    Log("Upstream Response");
                    var headers = new List<KeyValuePair<string, StringValues>>();

                    Log($"StatusCode: {upstreamResponseMessage.StatusCode}");

                    Log("Headers:");
                    foreach (var header in upstreamResponseMessage.Headers)
                    {
                        Log($"  {header.Key}:{header.Value.First()}");

                        // Must skip "Transfer-Encoding" header, since if it's set manually Kestrel requires you to implement
                        // your own chunking.
                        if (string.Equals(header.Key, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        headers.Add(new KeyValuePair<string, StringValues>(header.Key, header.Value.ToArray()));
                    }

                    foreach (var header in upstreamResponseMessage.Content.Headers)
                    {
                        Log($"  {header.Key}:{header.Value.First()}");
                        headers.Add(new KeyValuePair<string, StringValues>(header.Key, header.Value.ToArray()));
                    }

                    Log("Reading upstream response body...");

                    var upstreamResponse = new UpstreamResponse()
                    {
                        StatusCode = (int)upstreamResponseMessage.StatusCode,
                        Headers = headers.ToArray(),
                        Content = await upstreamResponseMessage.Content.ReadAsByteArrayAsync()
                    };

                    Log($"ContentLength: {upstreamResponse.Content.Length}");

                    return upstreamResponse;
                }
            }
        }

        private static async Task SendDownstreamResponse(UpstreamResponse upstreamResponse, HttpResponse response)
        {
            Log("Sending downstream response...");

            response.StatusCode = upstreamResponse.StatusCode;

            Log($"StatusCode: {upstreamResponse.StatusCode}");

            Log("Headers:");
            foreach (var header in upstreamResponse.Headers)
            {
                Log($"  {header.Key}:{header.Value}");
                response.Headers.Add(header.Key, header.Value);
            }

            Log($"Writing response body of {upstreamResponse.Content.Length} bytes...");
            await response.Body.WriteAsync(upstreamResponse.Content, 0, upstreamResponse.Content.Length);
            Log($"Finished writing response body");
        }

        private static void Log(object value)
        {
            Console.WriteLine($"[{DateTime.Now:hh:mm:ss.fff}] {value}");
        }

        private class UpstreamResponse
        {
            public int StatusCode { get; set; }
            public KeyValuePair<string, StringValues>[] Headers { get; set; }
            public byte[] Content { get; set; }
        }
    }
}
