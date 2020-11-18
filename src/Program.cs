using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
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
using System.Net.Sockets;
using System.Reflection;
using System.Threading.Tasks;

namespace HttpFaultInjector
{
    public static class Program
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

                        Console.WriteLine("Select a response then press ENTER:");
                        Console.WriteLine("f: Full response");
                        Console.WriteLine("p: Partial Response (full headers, 50% of body), then wait indefinitely");
                        Console.WriteLine("pc: Partial Response (full headers, 50% of body), then close (TCP FIN)");
                        Console.WriteLine("pa: Partial Response (full headers, 50% of body), then abort (TCP RST)");
                        Console.WriteLine("n: No response, then wait indefinitely");
                        Console.WriteLine("nc: No response, then close (TCP FIN)");
                        Console.WriteLine("na: No response, then abort (TCP RST)");

                        while (true)
                        {
                            var selection = Console.ReadLine();

                            switch (selection)
                            {
                                case "f":
                                    // Full response
                                    await SendDownstreamResponse(upstreamResponse, context.Response);
                                    return;
                                case "p":
                                    // Partial Response (full headers, 50% of body), then wait indefinitely
                                    await SendDownstreamResponse(upstreamResponse, context.Response, upstreamResponse.Content.Length / 2);
                                    await Task.Delay(TimeSpan.MaxValue);
                                    return;
                                case "pc":
                                    // Partial Response (full headers, 50% of body), then close (TCP FIN)
                                    await SendDownstreamResponse(upstreamResponse, context.Response, upstreamResponse.Content.Length / 2);
                                    Close(context);
                                    return;
                                case "pa":
                                    // Partial Response (full headers, 50% of body), then abort (TCP RST)
                                    await SendDownstreamResponse(upstreamResponse, context.Response, upstreamResponse.Content.Length / 2);
                                    Abort(context);
                                    return;
                                case "n":
                                    // No response, then wait indefinitely
                                    await Task.Delay(TimeSpan.MaxValue);
                                    return;
                                case "nc":
                                    // No response, then close (TCP FIN)
                                    Close(context);
                                    return;
                                case "na":
                                    // No response, then abort (TCP RST)
                                    Abort(context);
                                    return;
                                default:
                                    Console.WriteLine($"Invalid selection: {selection}");
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

        private static async Task SendDownstreamResponse(UpstreamResponse upstreamResponse, HttpResponse response, int? contentBytes = null)
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

            var count = contentBytes ?? upstreamResponse.Content.Length;
            Log($"Writing response body of {count} bytes...");
            await response.Body.WriteAsync(upstreamResponse.Content, 0, count);
            Log($"Finished writing response body");
        }

        // Close the TCP connection by sending FIN
        private static void Close(HttpContext context)
        {
            context.Abort();
        }

        // Abort the TCP connection by sending RST
        private static void Abort(HttpContext context)
        {
            // SocketConnection registered "this" as the IConnectionIdFeature among other things.
            var socketConnection = context.Features.Get<IConnectionIdFeature>();
            var socket = (Socket)socketConnection.GetType().GetField("_socket", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(socketConnection);
            socket.LingerState = new LingerOption(true, 0);
            socket.Dispose();
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
