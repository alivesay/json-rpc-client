# Anemonis.JsonRpc.ServiceClient

A universal [JSON-RPC 2.0](http://www.jsonrpc.org/specification) service client based on the [JSON-RPC 2.0 Transport: HTTP](https://www.simple-is-better.org/json-rpc/transport_http.html) specification and the [Anemonis.JsonRpc](https://www.nuget.org/packages/Anemonis.JsonRpc)
 serializer.

[![NuGet package](https://img.shields.io/nuget/v/Anemonis.JsonRpc.ServiceClient.svg?style=flat-square)](https://www.nuget.org/packages/Anemonis.JsonRpc.ServiceClient)

## Project Details

- The client can be created by providing an interface-based service contract.
- The client supports operation cancellation via cancellation token.
- The client supports specifying JSON-RPC message identifier.
- The client supports specifying JSON-RPC compatibility level.
- The client supports working with request and response HTTP headers.
- The client supports `gzip`, `DEFLATE`, and `Brotli` response encodings.
- The client supports usage of a custom HTTP message invoker.
- The client provides low-level API for batch requests.
- The client uses an UUID string as a message identifier by default.
- The client does not verify the `Content-Length` header.

## Code Examples


Retrieving API key usage information for the [RANDOM.ORG](https://api.random.org/json-rpc/2) service:
```cs
public class KeyUsage
{
    [JsonProperty("bitsLeft")]
    public long BitsLeft { get; set; }
}
```
```cs
var parameters = new Dictionary<string, object>();

parameters["apiKey"] = "00000000-0000-0000-0000-000000000000";

var client = new JsonRpcClient("https://api.random.org/json-rpc/2/invoke");
var result = await client.InvokeAsync<KeyUsage>("getUsage", parameters);

Console.WriteLine(result.BitsLeft);
```
or
```cs
public class KeyUsage
{
    [JsonProperty("bitsLeft")]
    public long BitsLeft { get; set; }
}

public interface IRandomOrgService
{
    [JsonRpcMethod("getUsage", "apiKey")]
    Task<KeyUsage> GetUsageAsync(string apiKey);
}
```
```cs
var client = JsonRpcClientFactory.Create<IRandomOrgService>("https://api.random.org/json-rpc/2/invoke");
var result = await client.GetUsageAsync("00000000-0000-0000-0000-000000000000");

Console.WriteLine(result.BitsLeft);
```
The corresponding JSON-RPC messages:
```json
{
    "jsonrpc": "2.0",
    "method": "getUsage",
    "params": {
        "apiKey": "00000000-0000-0000-0000-000000000000"
    },
    "id": "89999193-0e46-4a14-b471-69191baf2c2b"
}
```
```json
{
    "jsonrpc": "2.0",
    "result": {
        "status": "running",
        "creationTime": "2013-02-01 17:53:40Z",
        "bitsLeft": 970409,
        "requestsLeft": 198935,
        "totalBits": 208391002,
        "totalRequests": 2963905
    },
    "id": "89999193-0e46-4a14-b471-69191baf2c2b"
}
```

## Quicklinks

- [Contributing Guidelines](./CONTRIBUTING.md)
- [Code of Conduct](./CODE_OF_CONDUCT.md)