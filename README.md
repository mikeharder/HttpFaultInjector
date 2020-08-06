# HttpFaultInjector

HttpFaultInjector is used to test the behavior of HTTP clients in response to server "faults" like:

* Partial Response (full response headers, 50% of body)
* No response
* Abort connection

## Startup

```
D:\Git\HttpFaultInjector\src>dotnet run

Hosting environment: Development
Content root path: D:\Git\HttpFaultInjector\src
Now listening on: http://0.0.0.0:7777
Now listening on: https://0.0.0.0:7778
Application started. Press Ctrl+C to shut down.
```

HttpFaultInjector listens for HTTP requests on port 7777 and HTTPS on 7778.

## Request Handling
When HttpFaultInjector receives a request, it:

1. Prints the request info
2. Forwards the request to the upstream server
3. Prints the response info
4. Prompts you to select a response

The available responses are:

1. Full response.  Should be identical to sending the request directly to the upstream server.
2. Partial response.  Sends full response headers and 50% of body.
3. No response.
4. Abort connection.  Immediately sends TCP RST.

Some client timeouts handle "partial response" and "no response" differently, so it's important to ensure your overall http client stack handles both correctly (and as similar as possible).  For example, if "no response" is automatically retried after some client timeout, then "partial response" should behave the same.

For "abort connection", clients should detect the TCP RST immediately and either throw an error or retry.

Example:

```
[05:30:21.633] Upstream Request
[05:30:21.633] URL: https://www.example.org/
[05:30:21.633] Headers:
[05:30:21.633]   Host:www.example.org
[05:30:21.634] Sending request to upstream server...
[05:30:21.641] Upstream Response
[05:30:21.641] StatusCode: OK
[05:30:21.642] Headers:
[05:30:21.642]   Accept-Ranges:bytes
[05:30:21.642]   Age:496169
[05:30:21.642]   Cache-Control:max-age=604800
[05:30:21.642]   Date:Thu, 06 Aug 2020 00:30:22 GMT
[05:30:21.642]   ETag:"3147526947"
[05:30:21.643]   Server:ECS
[05:30:21.643]   Vary:Accept-Encoding
[05:30:21.643]   X-Cache:HIT
[05:30:21.643]   Content-Type:text/html; charset=UTF-8
[05:30:21.643]   Expires:Thu, 13 Aug 2020 00:30:22 GMT
[05:30:21.643]   Last-Modified:Thu, 17 Oct 2019 07:18:26 GMT
[05:30:21.643]   Content-Length:1256
[05:30:21.643] Reading upstream response body...
[05:30:21.644] ContentLength: 1256

Press a key to select a response:
f: Full response
p: Partial Response (full response headers, 50% of body)
n: No response
a: Abort connection
```

## Client Configuration

### Allow Insecure SSL Certs
HttpFaultInjector uses a self-signed SSL cert, so when testing HTTPS your http client must be configured to allow untrusted SSL certs.

### Redirection
When testing an HTTP client, you want to use the same codepath that will be used when talking directly to a server.  For this reason, HttpFaultInjector does not act as a traditional "http proxy" which uses a different codepath in the http client.  Instead, HttpFaultInjector acts like a typical web server, and you need to configure your http client to redirect requests to it.

At the last step in your http client pipeline:

1. Set the `Host` header to the upstream host the proxy should redirect to.  This should be the host from the URI you are requesting.
2. Change the host and port in the URI to the HttpFaultInjector

### .NET Example
```C#
static async Task Main(string[] args)
{
    var httpClient = new HttpClient(new FaultInjectionClientHandler("localhost", 7778));

    Console.WriteLine("Sending request...");
    var response = await httpClient.GetAsync("https://www.example.org");
    Console.WriteLine(response.StatusCode);
}

class FaultInjectionClientHandler : HttpClientHandler
{
    private readonly string _host;
    private readonly int _port;

    public FaultInjectionClientHandler(string host, int port)
    {
        _host = host;
        _port = port;

        // Allow insecure SSL certs
        ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Set "Host" header to upstream host
        request.Headers.Add("Host", request.RequestUri.Host);

        // Set URI to fault injector
        var builder = new UriBuilder(request.RequestUri)
        {
            Host = _host,
            Port = _port
        };
        request.RequestUri = builder.Uri;

        return base.SendAsync(request, cancellationToken);
    }
}
```
