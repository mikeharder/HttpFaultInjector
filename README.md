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
